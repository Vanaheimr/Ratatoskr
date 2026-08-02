/*
 * Copyright (c) 2010-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of Ratatoskr <https://www.github.com/Vanaheimr/Ratatoskr>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Security.Cryptography;
using System.Text;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    /// <summary>
    /// An account on the test server: credentials and the server-side roster.
    /// </summary>
    public sealed class XMPPAccount
    {

        #region Data

        private readonly List<RosterEntry> _roster = [];
        private readonly Dictionary<String, String> _pendingRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<OfflineMessage> _offlineMessages = [];
        private readonly Lock _lock = new();

        /// <summary>
        /// The PEP nodes of this account (XEP-0163): per node the items by
        /// their identifier.
        /// </summary>
        /// <remarks>
        /// On the account and not on the session, and that is the whole point
        /// of PEP: a bundle has to be retrievable <b>while its owner is
        /// offline</b> - otherwise nobody could write to them encrypted before
        /// they next appear. The server answers here on behalf of a human being
        /// who is not there just now.
        /// </remarks>
        private readonly Dictionary<String, Dictionary<String, String>> _pepNodes =
            new(StringComparer.Ordinal);

        /// <summary>
        /// The settings per node (XEP-0060, section 8.2).
        /// </summary>
        /// <remarks>
        /// Kept apart from the items, because a node and its content are two
        /// things: a node just created has no items yet, and a node without
        /// storage never gets any - both exist all the same.
        /// </remarks>
        private readonly Dictionary<String, PubSubNodeConfiguration> _pepNodeConfigs =
            new(StringComparer.Ordinal);

        /// <summary>
        /// The roles per node (XEP-0060, section 4.1).
        /// </summary>
        /// <remarks>
        /// <b>The owner does not stand in here.</b> The owner is the account,
        /// and an entry that always says the same thing can only be missing or
        /// become wrong.
        /// </remarks>
        private readonly Dictionary<String, Dictionary<String, PubSubAffiliation>> _pepAffiliations =
            new(StringComparer.Ordinal);

        /// <summary>
        /// The subscriptions per node, each with its subscriber and its
        /// identifier (XEP-0060, section 6.1).
        /// </summary>
        /// <remarks>
        /// Likewise on the account and not on the session: a subscription holds
        /// beyond the presence of both sides. Whoever has subscribed and then
        /// leaves still has it when they come back - anything else would not be
        /// a subscription but a presence list.
        ///
        /// <b>A list and not a mapping by JID</b>: the same JID may hold
        /// several subscriptions to the same node, and a mapping could only
        /// swallow the second one. The node is distinguished by
        /// <see cref="StringComparer.Ordinal"/> as in <see cref="_pepNodes"/>,
        /// the subscriber when comparing without regard to upper and lower case
        /// like every JID.
        /// </remarks>
        private readonly Dictionary<String, List<PepSubscription>> _pepSubscriptions =
            new(StringComparer.Ordinal);

        #endregion

        #region Properties

        /// <summary>The bare JID of the account, e.g. alice@localhost.</summary>
        public String BareJid { get; }

        /// <summary>
        /// The credentials for the SASL authentication - derived, not in the
        /// clear.
        /// </summary>
        public XMPPCredentials Credentials { get; }

        /// <summary>A snapshot of the server-side roster.</summary>
        public IReadOnlyList<RosterEntry> Roster
        {
            get { lock (_lock) return _roster.ToList(); }
        }

        /// <summary>
        /// RFC 6121, section 2.6: the version of the roster - an opaque string
        /// that changes with every change.
        /// </summary>
        /// <remarks>
        /// Computed rather than counted. A counter would be the obvious choice
        /// but would have to be stored with the account and would survive a
        /// restart only if somebody thinks of it. A hash over the content needs
        /// no storage, is the same after a restart and stays right even when
        /// somebody changes the roster past the file.
        ///
        /// It has a property a counter does not have: if the roster goes from A
        /// to B and back to A, the version is the old one again. That is not a
        /// shortcoming but right - the intermediate state of a client that
        /// cached A is correct once more.
        ///
        /// The separators are control characters that can occur in no field.
        /// Without them a contact "ab" without a name and a contact "a" with
        /// the name "b" would yield the same character sequence.
        /// </remarks>
        public String RosterVersion
        {
            get
            {

                var sb = new StringBuilder();

                foreach (var e in Roster.OrderBy(e => e.Jid, StringComparer.Ordinal))
                    sb.Append(e.Jid).         Append('\u001F').
                       Append(e.Name).        Append('\u001F').
                       Append(e.Subscription).Append('\u001F').
                       Append(e.Ask).         Append('\u001F').
                       Append(e.Approved).    Append('\u001F').
                       // The groups belong in it, otherwise the version would
                       // stay the same after a regrouping - and a client that
                       // cached it would never fetch the roster again and would
                       // keep the old arrangement.
                       AppendJoin('\u001F', e.Groups.OrderBy(g => g, StringComparer.Ordinal)).
                       Append('\u001E');

                return Convert.ToBase64String(
                           SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))
                       )[..16];

            }
        }

        /// <summary>
        /// Unanswered subscription requests, by the bare JID of the applicant
        /// (RFC 6121, section 3.1.3, rule 4).
        /// </summary>
        /// <remarks>
        /// What is kept is the complete stanza and not merely the sender: the
        /// section demands that explicitly, because a request may carry
        /// extended content - above all the <c>&lt;status/&gt;</c> with which a
        /// human being gives a reason for asking. Whoever only remembers the
        /// sender delivers, at the next login, a request other than the one
        /// that was posed.
        ///
        /// Beside the roster and not in it: the security warning of the same
        /// section forbids a roster entry as long as consent has not been
        /// given.
        /// </remarks>
        public IReadOnlyDictionary<String, String> PendingSubscriptionRequests
        {
            get
            {
                lock (_lock)
                    return new Dictionary<String, String>(_pendingRequests, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Messages that were kept because the account had no reachable
        /// resource (RFC 6121, section 8.5.2.2.1), oldest first.
        /// </summary>
        /// <remarks>
        /// The order is no trimming. A conversation delivered late in the wrong
        /// order is harder to read than one missing entirely - the reader takes
        /// the answer for the question.
        /// </remarks>
        public IReadOnlyList<OfflineMessage> OfflineMessages
        {
            get { lock (_lock) return _offlineMessages.ToList(); }
        }

        /// <summary>
        /// Called after every roster change; the server hangs its account store
        /// on it.
        /// </summary>
        /// <remarks>
        /// Here and not at the call sites in the server: the roster can also be
        /// changed directly on the account - test helpers do exactly that - and
        /// a list of places where one must not forget the saving becomes
        /// incomplete sooner or later.
        /// </remarks>
        internal Action<XMPPAccount>? OnChanged { get; set; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Creates an account with a plaintext password, which is derived from
        /// right away and discarded afterwards.
        /// </summary>
        public XMPPAccount(String bareJid, String password)
            : this(bareJid, XMPPCredentials.FromPassword(password))
        { }

        /// <summary>
        /// Creates an account with already derived credentials - the way an
        /// account store reads them back in.
        /// </summary>
        public XMPPAccount(String bareJid, XMPPCredentials credentials)
        {
            BareJid      = bareJid;
            Credentials  = credentials;
        }

        #endregion


        /// <summary>
        /// Creates a roster entry or updates it.
        /// </summary>
        public void SetRosterEntry(RosterEntry entry)
        {

            lock (_lock)
            {
                _roster.RemoveAll(e => String.Equals(e.Jid, entry.Jid, StringComparison.OrdinalIgnoreCase));
                _roster.Add(entry);
            }

            // Outside the lock: the store may write a file, and nobody who only
            // wants to read the roster shall have to wait for that.
            OnChanged?.Invoke(this);

        }

        /// <summary>
        /// Removes a roster entry.
        /// </summary>
        public void RemoveRosterEntry(String jid)
        {

            lock (_lock)
                _roster.RemoveAll(e => String.Equals(e.Jid, jid, StringComparison.OrdinalIgnoreCase));

            OnChanged?.Invoke(this);

        }

        /// <summary>
        /// Keeps a subscription request until it is answered.
        /// </summary>
        /// <param name="maxStored">
        /// An upper bound for the number of requests kept. Once it is reached,
        /// the new one is discarded instead of displacing one already kept -
        /// otherwise an attacker could deliberately push out the real request
        /// of an acquaintance.
        /// </param>
        /// <returns>
        /// false when nothing was kept: either a request from this sender is
        /// already on hand, or the bound is reached. In both cases nothing
        /// shall be delivered either.
        /// </returns>
        /// <remarks>
        /// Section 3.1.3 leaves it open whether the first or the last request
        /// of a sender is kept, but demands that it stay exactly one ("this
        /// helps to prevent 'subscription request spam'"). Here the first one
        /// stays: otherwise whoever asks last would determine the content of
        /// what the contact gets to see at the next login, and could exchange
        /// it as often as they liked.
        /// </remarks>
        public Boolean RememberSubscriptionRequest(String   fromBareJid,
                                                   String   stanza,
                                                   Int32    maxStored = Int32.MaxValue)
        {

            lock (_lock)
            {

                if (_pendingRequests.ContainsKey(fromBareJid) ||
                    _pendingRequests.Count >= maxStored)
                {
                    return false;
                }

                _pendingRequests[fromBareJid] = stanza;

            }

            OnChanged?.Invoke(this);

            return true;

        }

        /// <summary>
        /// Forgets a request that was kept - it has been answered.
        /// </summary>
        /// <returns>true when one was on hand.</returns>
        public Boolean ForgetSubscriptionRequest(String fromBareJid)
        {

            Boolean removed;

            lock (_lock)
                removed = _pendingRequests.Remove(fromBareJid);

            if (removed)
                OnChanged?.Invoke(this);

            return removed;

        }

        /// <summary>
        /// Is an unanswered request from this contact on hand?
        /// </summary>
        /// <remarks>
        /// The question on which, per section 3.4.2, it hangs whether a
        /// <c>&lt;presence type='subscribed'/&gt;</c> is a consent or an
        /// advance admission.
        /// </remarks>
        public Boolean HasPendingRequestFrom(String bareJid)
        {
            lock (_lock)
                return _pendingRequests.ContainsKey(bareJid);
        }

        #region PEP (XEP-0163)

        /// <summary>
        /// Stores an item in a PEP node and replaces one of the same name.
        /// </summary>
        /// <remarks>
        /// <b>Replacing and not placing beside</b>: the identifier <i>is</i> the
        /// statement. The device list stands under <c>current</c>, a bundle
        /// under the device identifier - two items under the same identifier
        /// would not be two pieces of information but an unclarity about which
        /// one holds.
        ///
        /// Here the oldest item gives way, unlike with the offline storage (see
        /// <see cref="StoreOfflineMessage"/>), where the <i>new</i> message is
        /// refused. The difference is the content: a message is unique and its
        /// loss final; a PEP item is the current state of a thing, and the
        /// newest is the one that matters.
        ///
        /// <b>How many there may be and whether anything is stored at all is
        /// said by the node</b> (since K7). A node without storage only reports
        /// - whoever was not listening has missed it. It comes into being all
        /// the same: a publication creates it, even when nothing of it remains.
        /// </remarks>
        public void PublishPepItem(String  node,
                                   String  itemId,
                                   String  payload)
        {

            lock (_lock)
            {

                if (!_pepNodeConfigs.TryGetValue(node, out var configuration))
                    _pepNodeConfigs[node] = configuration = PubSubNodeConfiguration.Default;

                if (!configuration.PersistItems)
                    return;

                var maxItems = configuration.MaxItems;

                if (!_pepNodes.TryGetValue(node, out var entries))
                    _pepNodes[node] = entries = new Dictionary<String, String>(StringComparer.Ordinal);

                entries[itemId] = payload;

                while (entries.Count > maxItems)
                    entries.Remove(entries.Keys.First());

            }

        }

        /// <summary>
        /// The items of a PEP node.
        /// </summary>
        /// <param name="itemId">
        /// A particular item, or null for all.
        /// </param>
        /// <returns>
        /// The items, or an empty list - <b>also when the node does not exist
        /// at all</b>. The difference between "empty node" and "no node" does
        /// not answer the question the retriever is asking: they want to know
        /// whether there is anything to fetch.
        /// </returns>
        public IReadOnlyList<(String ItemId, String Payload)> GetPepItems(String node, String? itemId = null)
        {

            lock (_lock)
            {

                if (!_pepNodes.TryGetValue(node, out var entries))
                    return [];

                if (itemId is not null)
                    return entries.TryGetValue(itemId, out var single)
                               ? [(itemId, single)]
                               : [];

                return [.. entries.Select(e => (e.Key, e.Value))];

            }

        }

        /// <summary>The nodes this account has published something in.</summary>
        public IReadOnlyCollection<String> PepNodes
        {
            get { lock (_lock) return [.. _pepNodes.Keys]; }
        }

        /// <summary>
        /// Does this node exist?
        /// </summary>
        /// <remarks>
        /// <b>Something other than "has items".</b> A node just created exists
        /// before anything stands in it, and a node without storage never gets
        /// any - both have to be subscribable, otherwise the creating would be
        /// without consequence.
        ///
        /// <b>The settings are the trace a node hangs on</b>, and the only one
        /// at that: both <see cref="CreatePepNode"/> and
        /// <see cref="PublishPepItem"/> create them before anything else comes
        /// into being. That is why an "or there are items" once stood here - a
        /// second answer to the same question that covered no case any more and
        /// would have become a trap when the storage was purged.
        /// </remarks>
        public Boolean PepNodeExists(String node)
        {
            lock (_lock)
                return _pepNodeConfigs.ContainsKey(node);
        }

        /// <summary>
        /// Creates a node (XEP-0060, section 8.1).
        /// </summary>
        /// <returns>
        /// false when it already exists - <c>&lt;conflict/&gt;</c>. Silently
        /// letting a second creation stand would mean replacing an existing
        /// setting with a new one without anyone having asked for it.
        /// </returns>
        public Boolean CreatePepNode(String node, PubSubNodeConfiguration? configuration = null)
        {

            lock (_lock)
            {

                if (PepNodeExists(node))
                    return false;

                _pepNodeConfigs[node] = configuration ?? PubSubNodeConfiguration.Default;

                return true;

            }

        }

        /// <summary>
        /// Configures an existing node (XEP-0060, section 8.2).
        /// </summary>
        /// <returns>false when the node does not exist.</returns>
        public Boolean ConfigurePepNode(String node, PubSubNodeConfiguration configuration)
        {

            lock (_lock)
            {

                if (!PepNodeExists(node))
                    return false;

                _pepNodeConfigs[node] = configuration;

                // A smaller bound holds right away and not only from the next
                // time on: whoever sets it does not want that many kept - and
                // the stock is exactly what is kept.
                if (_pepNodes.TryGetValue(node, out var entries))
                    while (entries.Count > configuration.MaxItems)
                        entries.Remove(entries.Keys.First());

                return true;

            }

        }

        /// <summary>
        /// Retracts a single item (XEP-0060, section 7.2).
        /// </summary>
        /// <returns>
        /// false when the item does not exist - <b>and that includes the case
        /// that the node does not exist.</b> Both times the answer is the same:
        /// there was nothing to retract. The difference between "empty node"
        /// and "no node" does not answer the question that was asked here
        /// either.
        /// </returns>
        /// <remarks>
        /// The node stays, even when it was its last item. A node that
        /// disappeared with its content would be gone for its subscribers
        /// without announcement - and the next publication would create a new
        /// one nobody has subscribed to.
        /// </remarks>
        public Boolean RetractPepItem(String node, String itemId)
        {

            lock (_lock)
                return _pepNodes.TryGetValue(node, out var entries) &&
                       entries.Remove(itemId);

        }

        /// <summary>
        /// Deletes a node together with everything hanging on it (XEP-0060,
        /// section 8.4).
        /// </summary>
        /// <returns>
        /// The subscriptions that expired in the process, or <c>null</c> when
        /// the node did not exist - that is something other than an empty list.
        /// </returns>
        /// <remarks>
        /// <b>All four, and the roles are the reason.</b> Items, settings,
        /// subscriptions and roles go together. If the roles stayed standing,
        /// the next node of the same name would inherit an exclusion list
        /// nobody sees any more - and the owner would wonder why an
        /// acquaintance cannot get at their new node.
        /// </remarks>
        public IReadOnlyList<PepSubscription>? DeletePepNode(String node)
        {

            lock (_lock)
            {

                if (!PepNodeExists(node))
                    return null;

                var affected = _pepSubscriptions.TryGetValue(node, out var subscriptions)
                                   ? (IReadOnlyList<PepSubscription>) [.. subscriptions]
                                   : [];

                _pepNodes.        Remove(node);
                _pepNodeConfigs.  Remove(node);
                _pepAffiliations. Remove(node);
                _pepSubscriptions.Remove(node);

                return affected;

            }

        }

        /// <summary>
        /// Purges a node (XEP-0060, section 8.5).
        /// </summary>
        /// <returns>false when the node does not exist.</returns>
        /// <remarks>
        /// <b>The node stays, and with it its subscribers.</b> That is the
        /// whole difference to deleting: whoever purged carries on publishing
        /// to the same recipients - whoever deleted, to nobody any more.
        ///
        /// The storage may disappear entirely in the process: a node hangs on
        /// its settings and not on its items (see
        /// <see cref="PepNodeExists"/>). As long as that was not so, this line
        /// would have taken the node with it - and a purge would have been a
        /// delete that only put itself right again at the next publication.
        /// </remarks>
        public Boolean PurgePepNode(String node)
        {

            lock (_lock)
            {

                if (!PepNodeExists(node))
                    return false;

                _pepNodes.Remove(node);

                return true;

            }

        }

        /// <summary>
        /// The settings of a node, or null when it does not exist.
        /// </summary>
        public PubSubNodeConfiguration? PepNodeConfiguration(String node)
        {
            lock (_lock)
                return _pepNodeConfigs.TryGetValue(node, out var configuration)
                           ? configuration
                           : null;
        }

        /// <summary>
        /// Creates a subscription and returns its identifier (XEP-0060,
        /// section 6.1).
        /// </summary>
        /// <remarks>
        /// <b>Every <c>subscribe</c> is a subscription of its own</b>, the
        /// second one of the same JID to the same node included. XEP-0060
        /// provides for that explicitly - that is what the <c>subid</c> exists
        /// for in the first place.
        ///
        /// The case is not made up: it arises by itself when a client restarts
        /// and subscribes again without knowing its old identifier. From then
        /// on every unsubscribing without an identifier is ambiguous, and
        /// exactly that is what the service then has to say.
        ///
        /// <b>What is missing here</b> is the reason two subscriptions
        /// otherwise differ: the configuration per subscription (section 6.3).
        /// Without it a second one brings nothing but a second delivery - the
        /// server still has to answer correctly when there is one.
        /// </remarks>
        /// <returns>
        /// The subscription created - with its identifier and its state.
        /// <b>The state is decided here and not at the caller:</b> it hangs on
        /// the setting of the node, and that stands here.
        /// </returns>
        public PepSubscription AddPepSubscription(String node, String subscriberBareJid)
        {

            lock (_lock)
            {

                if (!_pepSubscriptions.TryGetValue(node, out var subscriptions))
                    _pepSubscriptions[node] = subscriptions = [];

                // XEP-0060, section 6.1.3.7: on a node with an approval
                // procedure the answer is a `pending` - the service has
                // accepted the request and nothing more.
                var fresh = new PepSubscription(
                                subscriberBareJid,
                                Guid.NewGuid().ToString("N")[..12],
                                new PubSubSubscriptionOptions(),
                                PepNodeConfiguration(node)?.AccessModel == PubSubAccessModel.Authorize
                                    ? PubSubSubscriptionState.Pending
                                    : PubSubSubscriptionState.Subscribed);

                subscriptions.Add(fresh);

                return fresh;

            }

        }

        /// <summary>
        /// Approves an applied-for subscription (XEP-0060, section 8.6).
        /// </summary>
        /// <returns>
        /// The subscription approved, or null when there was none to approve -
        /// <b>also when it was approved already.</b> A second approval changes
        /// nothing and shall therefore report nothing either.
        /// </returns>
        public PepSubscription? ApprovePepSubscription(String node, String subscriberBareJid, String? subId = null)
        {

            lock (_lock)
            {

                if (FindPepSubscription(node, subscriberBareJid, subId, out var found) != PepSubscriptionResult.Ok ||
                    found!.State != PubSubSubscriptionState.Pending)
                {
                    return null;
                }

                var subscriptions = _pepSubscriptions[node];
                var approved      = found with { State = PubSubSubscriptionState.Subscribed };

                subscriptions[subscriptions.IndexOf(found)] = approved;

                return approved;

            }

        }

        /// <summary>
        /// Works out which subscription is meant.
        /// </summary>
        /// <param name="subId">
        /// The identifier from the promise, or null. It may be missing as long
        /// as there is only one (XEP-0060, section 6.2.3.1); if there are
        /// several, it is the only information about which one is meant.
        /// </param>
        /// <remarks>
        /// One place for two questions: unsubscribing and configuring search
        /// for the same thing. Only the error message for it differs, and the
        /// caller builds that.
        /// </remarks>
        public PepSubscriptionResult FindPepSubscription(String              node,
                                                         String              subscriberBareJid,
                                                         String?             subId,
                                                         out PepSubscription?  subscription)
        {

            subscription = null;

            lock (_lock)
            {

                if (!_pepSubscriptions.TryGetValue(node, out var subscriptions))
                    return PepSubscriptionResult.NotSubscribed;

                var theirs = subscriptions.FindAll(
                                 a => String.Equals(a.Jid, subscriberBareJid, StringComparison.OrdinalIgnoreCase));

                if (theirs.Count == 0)
                    return PepSubscriptionResult.NotSubscribed;

                if (subId is null && theirs.Count > 1)
                    return PepSubscriptionResult.SubIdRequired;

                subscription = subId is null
                                   ? theirs[0]
                                   : theirs.Find(a => String.Equals(a.SubId, subId, StringComparison.Ordinal));

                return subscription is null
                           ? PepSubscriptionResult.WrongSubId
                           : PepSubscriptionResult.Ok;

            }

        }

        /// <summary>
        /// Ends a subscription (XEP-0060, section 6.2).
        /// </summary>
        public PepSubscriptionResult RemovePepSubscription(String   node,
                                                           String   subscriberBareJid,
                                                           String?  subId = null)
        {

            lock (_lock)
            {

                var finding = FindPepSubscription(node, subscriberBareJid, subId, out var found);

                if (finding != PepSubscriptionResult.Ok)
                    return finding;

                var subscriptions = _pepSubscriptions[node];

                subscriptions.Remove(found!);

                if (subscriptions.Count == 0)
                    _pepSubscriptions.Remove(node);

                return PepSubscriptionResult.Ok;

            }

        }

        /// <summary>
        /// Ends subscriptions on the owner's instruction (XEP-0060,
        /// section 8.8.2).
        /// </summary>
        /// <param name="subId">
        /// A particular subscription, or null for all of this JID at this node.
        /// </param>
        /// <returns>
        /// The subscriptions ended - an empty list when there was none. Not the
        /// number: whoever wants to notify the subscriber has to know which
        /// identifier has expired.
        /// </returns>
        /// <remarks>
        /// <b>Without an identifier all of them go, and that is no
        /// contradiction to section 6.2.3.1.</b> There the subscriber has to
        /// say which of their subscriptions they mean, because the others are
        /// to remain theirs. Here the owner means the person and not the
        /// bookkeeping: leaving one standing would mean carrying out the
        /// instruction by half - and the person concerned would keep getting
        /// everything.
        /// </remarks>
        /// <param name="onlyInState">
        /// Only subscriptions in this state, or null for all.
        ///
        /// <b>The refusal of an application needs this</b> (XEP-0060,
        /// section 8.6): a "no" to a question from earlier must not end a
        /// subscription that has been approved in the meantime - otherwise the
        /// order of two messages would decide what holds.
        /// </param>
        public IReadOnlyList<PepSubscription> RemovePepSubscriptions(String                    node,
                                                                     String                    subscriberBareJid,
                                                                     String?                   subId        = null,
                                                                     PubSubSubscriptionState?  onlyInState  = null)
        {

            lock (_lock)
            {

                if (!_pepSubscriptions.TryGetValue(node, out var subscriptions))
                    return [];

                var affected = subscriptions.FindAll(
                                   a => String.Equals(a.Jid, subscriberBareJid, StringComparison.OrdinalIgnoreCase) &&
                                        (subId is null || String.Equals(a.SubId, subId, StringComparison.Ordinal)) &&
                                        (onlyInState is null || a.State == onlyInState));

                foreach (var one in affected)
                    subscriptions.Remove(one);

                if (subscriptions.Count == 0)
                    _pepSubscriptions.Remove(node);

                return affected;

            }

        }

        /// <summary>
        /// Configures a subscription (XEP-0060, section 6.3).
        /// </summary>
        public PepSubscriptionResult SetPepSubscriptionOptions(String                     node,
                                                               String                     subscriberBareJid,
                                                               String?                    subId,
                                                               PubSubSubscriptionOptions  options)
        {

            lock (_lock)
            {

                var finding = FindPepSubscription(node, subscriberBareJid, subId, out var found);

                if (finding != PepSubscriptionResult.Ok)
                    return finding;

                var subscriptions = _pepSubscriptions[node];

                subscriptions[subscriptions.IndexOf(found!)] = found! with { Options = options };

                return PepSubscriptionResult.Ok;

            }

        }

        /// <summary>
        /// The subscriptions of this node.
        /// </summary>
        /// <remarks>
        /// The same JID can occur several times. Whoever wants the recipients
        /// and not the subscriptions therefore has to collapse them themselves
        /// - and whoever delivers, precisely not: every subscription is a
        /// promise of its own, with a setting of its own.
        /// </remarks>
        public IReadOnlyList<PepSubscription> PepSubscriptions(String node)
        {
            lock (_lock)
                return _pepSubscriptions.TryGetValue(node, out var subscriptions)
                           ? [.. subscriptions]
                           : [];
        }

        /// <summary>
        /// What somebody is at a node (XEP-0060, section 4.1).
        /// </summary>
        /// <remarks>
        /// <b>The owner is not looked up but recognised.</b> A PEP node belongs
        /// to the account it stands in - that is not an entry that could be
        /// missing but a fact about the address.
        /// </remarks>
        public PubSubAffiliation PepAffiliationOf(String node, String bareJid)
        {

            if (String.Equals(BareJid, bareJid, StringComparison.OrdinalIgnoreCase))
                return PubSubAffiliation.Owner;

            lock (_lock)
                return _pepAffiliations.TryGetValue(node, out var roles) &&
                       roles.TryGetValue(bareJid, out var role)
                           ? role
                           : PubSubAffiliation.None;

        }

        /// <summary>
        /// Sets a role (XEP-0060, section 8.9.2).
        /// </summary>
        /// <returns>
        /// false when the node does not exist or when somebody wants to change
        /// the role of the owner - neither is a formal error but an instruction
        /// that does not exist.
        /// </returns>
        /// <remarks>
        /// <c>None</c> deletes the entry instead of setting it to a value: "no
        /// role" is the absence of a role and not one among several.
        /// </remarks>
        public Boolean SetPepAffiliation(String node, String bareJid, PubSubAffiliation affiliation)

            => SetPepAffiliation(node, bareJid, affiliation, out _);

        /// <summary>
        /// The same, and names the subscriptions that expired in the process.
        /// </summary>
        /// <param name="endedSubscriptions">
        /// What the exclusion has ended - empty with every other role.
        /// </param>
        /// <remarks>
        /// <b>Why the information belongs here and not to the caller.</b>
        /// Whoever wants to notify the person concerned has to know which
        /// identifiers have expired. Gathering them beforehand themselves would
        /// mean answering the same question a second time - and the second
        /// answer would be the less precise one, because something can come in
        /// between the looking and the setting.
        /// </remarks>
        public Boolean SetPepAffiliation(String                            node,
                                         String                            bareJid,
                                         PubSubAffiliation                 affiliation,
                                         out IReadOnlyList<PepSubscription>  endedSubscriptions)
        {

            endedSubscriptions = [];

            if (String.Equals(BareJid, bareJid, StringComparison.OrdinalIgnoreCase) ||
                affiliation == PubSubAffiliation.Owner)
            {
                return false;
            }

            lock (_lock)
            {

                if (!PepNodeExists(node))
                    return false;

                if (affiliation == PubSubAffiliation.None)
                {

                    if (_pepAffiliations.TryGetValue(node, out var present))
                    {

                        present.Remove(bareJid);

                        if (present.Count == 0)
                            _pepAffiliations.Remove(node);

                    }

                    return true;

                }

                if (!_pepAffiliations.TryGetValue(node, out var roles))
                    _pepAffiliations[node] = roles = new Dictionary<String, PubSubAffiliation>(StringComparer.OrdinalIgnoreCase);

                roles[bareJid] = affiliation;

                // XEP-0060, section 8.9.4: whoever is excluded loses their
                // subscriptions. Merely hindering them from new ones would mean
                // making the exclusion depend on the accident of whether they
                // were already there beforehand.
                //
                // Over the same route as the owner's instruction
                // (section 8.8.2): two places that end subscriptions end them
                // differently at some point.
                if (affiliation == PubSubAffiliation.Outcast)
                    endedSubscriptions = RemovePepSubscriptions(node, bareJid);

                return true;

            }

        }

        /// <summary>
        /// All roles at a node, the owner included (XEP-0060, section 8.9.1).
        /// </summary>
        public IReadOnlyList<(String Jid, PubSubAffiliation Affiliation)> PepAffiliations(String node)
        {
            lock (_lock)
                return [(BareJid, PubSubAffiliation.Owner),
                        .. (_pepAffiliations.TryGetValue(node, out var roles)
                                ? roles.Select(r => (r.Key, r.Value))
                                : [])];
        }

        /// <summary>
        /// The roles of a JID across all nodes of this account (XEP-0060,
        /// section 5.7).
        /// </summary>
        public IReadOnlyList<(String Node, PubSubAffiliation Affiliation)> PepAffiliationsOf(String bareJid)
        {

            lock (_lock)
            {

                // All their nodes belong to the owner - including those at
                // which nobody ever entered a role.
                if (String.Equals(BareJid, bareJid, StringComparison.OrdinalIgnoreCase))
                    return [.. _pepNodeConfigs.Keys
                               .Select(n => (n, PubSubAffiliation.Owner))];

                return [.. _pepAffiliations
                           .Where (n => n.Value.ContainsKey(bareJid))
                           .Select(n => (n.Key, n.Value[bareJid]))];

            }

        }

        /// <summary>
        /// All subscriptions of a JID across all nodes of this account
        /// (XEP-0060, section 5.6).
        /// </summary>
        /// <remarks>
        /// <b>The question a client cannot answer for itself.</b> Its
        /// bookkeeping lives in memory and is empty after a connection drop;
        /// the subscriptions continue to exist, because they stand here on the
        /// account. Without this route it afterwards knows not a single
        /// identifier any more - and with several subscriptions to the same
        /// node cannot end any of them.
        /// </remarks>
        public IReadOnlyList<(String Node, PepSubscription Subscription)> PepSubscriptionsOf(String subscriberBareJid)
        {
            lock (_lock)
                return [.. _pepSubscriptions
                           .SelectMany(node => node.Value.Select(a => (node.Key, a)))
                           .Where(e => String.Equals(e.a.Jid, subscriberBareJid, StringComparison.OrdinalIgnoreCase))];
        }

        #endregion

        /// <summary>
        /// Keeps a message until the account is reachable again.
        /// </summary>
        /// <param name="stanza">The complete stanza with the <c>from</c> set.</param>
        /// <param name="storedAt">The moment it came in.</param>
        /// <param name="maxStored">
        /// An upper bound for the number of messages kept per account.
        /// </param>
        /// <returns>
        /// false when the bound is reached - then the message is not kept, and
        /// the sender shall learn of it.
        /// </returns>
        /// <remarks>
        /// Once the bound is reached, the new message is refused and none of
        /// those kept is displaced. Both lose a message, but only one of them
        /// tells anybody: whoever refuses can answer the sender with
        /// <c>&lt;service-unavailable/&gt;</c> - RFC 6121, section 8.5.2.2.1
        /// puts exactly these two routes side by side. Whoever displaces throws
        /// away a message the sender assumes is lying ready and the recipient
        /// never learns existed.
        /// </remarks>
        public Boolean StoreOfflineMessage(String          stanza,
                                           DateTimeOffset  storedAt,
                                           Int32           maxStored = Int32.MaxValue)
        {

            lock (_lock)
            {

                if (_offlineMessages.Count >= maxStored)
                    return false;

                _offlineMessages.Add(new OfflineMessage(stanza, storedAt));

            }

            OnChanged?.Invoke(this);

            return true;

        }

        /// <summary>
        /// Hands out the messages kept and empties the storage.
        /// </summary>
        /// <remarks>
        /// Handing out and emptying in one step, under the same lock: two
        /// resources signing on at the same time would otherwise both get to
        /// see the storage, and the user would read everything twice.
        ///
        /// Unlike a kept subscription request
        /// (<see cref="PendingSubscriptionRequests"/>) nothing stays standing
        /// here. The request is delivered again until it is answered - a
        /// message is done with once delivered, and whoever got it presented
        /// again at every login could never get rid of it.
        /// </remarks>
        public IReadOnlyList<OfflineMessage> TakeOfflineMessages()
        {

            List<OfflineMessage> taken;

            lock (_lock)
            {

                if (_offlineMessages.Count == 0)
                    return [];

                taken = _offlineMessages.ToList();
                _offlineMessages.Clear();

            }

            OnChanged?.Invoke(this);

            return taken;

        }

        /// <summary>
        /// May this contact see the presence of this account?
        /// </summary>
        /// <remarks>
        /// RFC 6121, section 4.2.2: that is the case exactly with <c>from</c>
        /// and <c>both</c>. The direction is easy to mix up - a <c>to</c> means
        /// that <b>this account</b> sees the presence of the contact, and would
        /// give one's own to exactly the wrong half of the roster.
        /// </remarks>
        public Boolean IsPresenceSubscriber(String bareJid)
            => SubscriptionOf(bareJid) is "from" or "both";

        /// <summary>
        /// Does this JID stand in the roster - and, when groups are named, in
        /// one of them (XEP-0060, section 4.5)?
        /// </summary>
        /// <param name="groups">
        /// The groups permitted, or an empty list for the whole roster.
        /// </param>
        /// <remarks>
        /// <b>An entry suffices, a presence state is not demanded.</b> The
        /// roster is the owner's list: whoever stands in it stands there
        /// because the owner entered them. Whether the contact may conversely
        /// see the owner's presence is another question - and for it there is a
        /// model of its own.
        /// </remarks>
        public Boolean IsInRosterGroups(String bareJid, IReadOnlyList<String> groups)
        {

            lock (_lock)
            {

                var entry = _roster.FirstOrDefault(
                                e => String.Equals(e.Jid, bareJid, StringComparison.OrdinalIgnoreCase));

                if (entry is null)
                    return false;

                return groups.Count == 0 ||
                       entry.Groups.Any(g => groups.Contains(g, StringComparer.Ordinal));

            }

        }

        /// <summary>
        /// Does this account get the presence of the contact - that is,
        /// <c>to</c> or <c>both</c>?
        /// </summary>
        public Boolean ReceivesPresenceFrom(String bareJid)
            => SubscriptionOf(bareJid) is "to" or "both";

        /// <summary>
        /// The subscription state towards this contact, or null when they do
        /// not stand in the roster.
        /// </summary>
        public String? SubscriptionOf(String bareJid)
        {
            lock (_lock)
                return _roster.FirstOrDefault(e => String.Equals(e.Jid, bareJid, StringComparison.OrdinalIgnoreCase))
                             ?.Subscription;
        }

    }

}
