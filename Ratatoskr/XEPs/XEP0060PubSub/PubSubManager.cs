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

using System.Xml.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

#region (delegate) OnPubSubEventDelegate

/// <summary>
/// XEP-0060: something happened at a node we subscribe to.
/// </summary>
public delegate Task OnPubSubEventDelegate(DateTimeOffset     Timestamp,
                                           PubSubManager      Sender,
                                           PubSubEvent        Event,
                                           CancellationToken  CancellationToken);

#endregion


/// <summary>
/// XEP-0060: Manages PubSub subscriptions and processes incoming events.
/// </summary>
public sealed class PubSubManager
{

    /// <summary>
    /// The namespace of the PubSub notifications.
    /// </summary>
    public const string EventNamespace = "http://jabber.org/protocol/pubsub#event";

    /// <summary>
    /// The confirmed subscriptions, by node.
    /// </summary>
    /// <remarks>
    /// <b>Confirmed means: the service has granted it.</b> Until D71 a mere set
    /// of names stood here, and the entry was made when the request was sent off
    /// - a refused subscription stood there afterwards as an existing one. What
    /// lies here now the service has said and this client has not guessed: the
    /// identifier it handed out, and the address under which it did so.
    ///
    /// <b>One list per node and not a single entry</b> (since D73): there can be
    /// several subscriptions on the same node, and until then the second
    /// overwrote the first. With that its identifier was gone - and gone means
    /// here that it could never be unsubscribed again, for the service demands
    /// an identifier when there are several.
    /// </remarks>
    private readonly Dictionary<String, List<PubSubSubscription>> _subscriptions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly JID _pubsubService;
    private readonly Lock _lock = new();
    private readonly ILogger _logger;

    public event OnPubSubEventDelegate? OnEvent;

    public PubSubManager(JID pubsubService, ILogger? logger = null)
    {
        _pubsubService = pubsubService;
        _logger = logger ?? NullLogger.Instance;
    }

    public JID PubSubService => _pubsubService;

    /// <summary>
    /// XEP-0163: whether this client has asked for a PEP node to be pushed to
    /// it - which under PEP is the whole of what subscribing is.
    /// </summary>
    /// <remarks>
    /// <b>A question and not a list, because the answer is somebody else's to
    /// give.</b> What this client wants pushed stands in its own caps, as the
    /// <c>+notify</c> of a node name (XEP-0163, section 4.2, with XEP-0115):
    /// a contact's server sends an avatar notification because this account
    /// said it wanted avatar notifications. A copy of that list kept here
    /// would be a second truth, and the first one is a mutable list anybody
    /// may add to while running.
    ///
    /// Null means nothing has been asked for. Then no account is a permitted
    /// source - which is the right answer for a manager nobody has wired up:
    /// a client that has announced no interest in a node has no business
    /// being told about it.
    /// </remarks>
    public Func<String, Boolean>? AsksToBeNotifiedOf { get; set; }

    /// <summary>
    /// XEP-0163: this client's own account, which is its own PEP service like
    /// any other account's.
    /// </summary>
    /// <remarks>
    /// <b>Needed because publishing and asking to be told are two different
    /// interests.</b> A client that fetches a node when it needs it announces
    /// no <c>+notify</c> for it and still publishes to it - and what comes back
    /// the moment it does is the server's notification of its own publication.
    /// Measured against <see cref="AsksToBeNotifiedOf"/> alone that echo is a
    /// forgery, which is what it was written to the log as: a report about
    /// urn:xmpp:omemo:2:devices from this very account, twice per connection,
    /// in the log where a real forgery would have to be noticed.
    ///
    /// Null means no account is known here, and then nothing is let in on this
    /// ground - the answer for a manager nobody has wired up.
    /// </remarks>
    public JID? OwnAccount { get; set; }

    /// <summary>
    /// Processes an incoming PubSub event message with spoofing protection
    /// </summary>
    public async Task<bool> ProcessEventAsync(XElement           stanza,
                                              JID                from,
                                              JID                expectedPubSubJid,
                                              CancellationToken  CancellationToken   = default)
    {

        var eventElement = stanza.Child(EventNamespace, "event");

        if (eventElement is null)
            return false;

        // The node first, for the sender check needs it: what is permitted is
        // not a sender but a sender for a particular node.
        var nodeId = NodeOf(eventElement);

        if (!IsAcceptableSource(from, nodeId, expectedPubSubJid))
        {
            // Named for what was wrong with it, and no longer for an
            // expectation that only one of the four permitted senders could
            // ever have met - see IsAcceptableSource.
            _logger.LogWarning("PubSub spoofing discarded: a report about {Node} from {From}, which is neither {Service}, nor a service this client holds a subscription with, nor an account reporting a node of its own",
                               nodeId, from, expectedPubSubJid);
            return false;
        }

        var subId = SubIdOf(stanza);

        // An items or retract event: both sit in <items node='…'/>.
        var itemsElement = eventElement.Child(EventNamespace, "items");

        if (itemsElement is not null)
        {

            var retracted = itemsElement.Children(EventNamespace, "retract").ToList();

            if (retracted.Count > 0)
            {

                var retractEvent = new PubSubEvent(nodeId, PubSubEventType.Retract, from, subId);

                foreach (var retract in retracted)
                {
                    var retractId = retract.Attr("id");
                    if (retractId is not null)
                        retractEvent.RetractedIds.Add(retractId);
                }

                await OnEvent.InvokeAllAsync(handler => handler(Timestamp.Now, this, retractEvent, CancellationToken), _logger);
                return true;

            }

            var itemsEvent = new PubSubEvent(nodeId, PubSubEventType.Items, from, subId);

            foreach (var item in itemsElement.Children(EventNamespace, "item"))
            {

                var itemId = item.Attr("id");

                if (itemId is null)
                    continue;

                // The payload is kept as raw XML - what stands in it is
                // application-specific. An <item/> entirely without content is
                // permissible; the earlier pattern demanded a tag pair and
                // overlooked self-closing items.
                itemsEvent.Items.Add(new PubSubItem(itemId,
                                                    nodeId,
                                                    string.Concat(item.Nodes())));

            }

            await OnEvent.InvokeAllAsync(handler => handler(Timestamp.Now, this, itemsEvent, CancellationToken), _logger);
            return true;

        }

        // XEP-0060, section 8.5.2: the node is empty - and stays in existence.
        // The subscription is therefore precisely not touched: the next
        // publication comes to the same address.
        if (eventElement.Child(EventNamespace, "purge") is not null)
        {
            await OnEvent.InvokeAllAsync(handler => handler(Timestamp.Now, this, new PubSubEvent(nodeId, PubSubEventType.Purge, from, subId), CancellationToken), _logger);
            return true;
        }

        // XEP-0060, section 8.4.2: the node does not exist any more.
        //
        // <b>So neither does a subscription on it.</b> To leave it standing
        // would mean waiting for notifications from a node nobody publishes to
        // any more - and sending an identifier along when unsubscribing that the
        // service does not know any more.
        if (eventElement.Child(EventNamespace, "delete") is not null)
        {

            RemoveSubscriptionsOf(nodeId, from);

            await OnEvent.InvokeAllAsync(handler => handler(Timestamp.Now, this, new PubSubEvent(nodeId, PubSubEventType.Delete, from, subId), CancellationToken), _logger);

            return true;

        }

        // XEP-0060, section 8.8.4: the service says that a subscription is
        // ended.
        //
        // The identifier stands here in the element and not in the SHIM header:
        // this report belongs to no delivery, it deals with the subscription
        // itself.
        if (eventElement.Child(EventNamespace, "subscription") is { } report)
        {

            var reported = PubSubSubscription.StateOf(report.Attr("subscription"));

            // XEP-0060, section 8.6: the grant on an application of one's own.
            //
            // <b>And only on that.</b> In D86 it stood here that a grant comes
            // in answer to a request and is entered there - right as long as
            // there was no approval procedure. Now there is one, and the grant
            // arrives later than the question. It is accepted all the same only
            // when this client has a pending application for it: otherwise it
            // would let itself be signed up by a service unasked.
            if (reported == PubSubSubscriptionState.Subscribed)
            {

                if (!ApproveSubscription(nodeId, report.Attr("subid"), from))
                {
                    _logger.LogInformation("PubSub: grant for {Node} without a pending application - not entered", nodeId);
                    return false;
                }

                var approved = new PubSubEvent(nodeId, PubSubEventType.SubscriptionApproved,
                                               from, report.Attr("subid"));

                await OnEvent.InvokeAllAsync(handler => handler(Timestamp.Now, this, approved, CancellationToken), _logger);

                return true;

            }

            if (reported != PubSubSubscriptionState.None)
            {
                _logger.LogInformation("PubSub: subscription report for {Node} without an outcome - not evaluated", nodeId);
                return false;
            }

            var ended = report.Attr("subid");

            // Without an identifier all subscriptions of this node are meant:
            // the service names them when it keeps several (section 12.19), and
            // to leave one of them standing would mean going on waiting for
            // notifications that do not come any more.
            //
            // All of this node held *with the sender*, and that binding is not
            // decoration. A report that a subscription has ended says something
            // about the sender's own books and nothing about anybody else's;
            // unbound - RemoveSubscription, as this was - a report naming no
            // identifier struck every subscription on the node, including one
            // taken out with a different service entirely. The method next door
            // says why the address belongs with the node name, and this is the
            // caller that was not listening.
            //
            // It was reachable before by a service subscribed to the same node
            // name; with an account let in for its own nodes it would be
            // reachable by anybody. Bound here rather than guarded up there:
            // what a sender may end is what the sender holds.
            RemoveSubscriptionsOf(nodeId, from, ended);

            await OnEvent.InvokeAllAsync(handler => handler(Timestamp.Now, this, new PubSubEvent(nodeId, PubSubEventType.SubscriptionEnded, from, ended), CancellationToken), _logger);

            return true;

        }

        return false;

    }

    /// <summary>
    /// The node an event is about - from <c>items</c>, <c>purge</c>,
    /// <c>delete</c> or <c>subscription</c>, depending on what stands there.
    /// </summary>
    /// <remarks>
    /// Every kind of report has to stand here, and not only so that it arrives:
    /// on this node hangs the sender check. A report whose node stays empty here
    /// counts as a report about the node "" - which nobody has subscribed to,
    /// and the check would let it through only when it came from the configured
    /// service anyway.
    /// </remarks>
    private static String NodeOf(XElement eventElement)
        => eventElement.Elements()
                       .FirstOrDefault(e => e.Name.NamespaceName == EventNamespace &&
                                            e.Name.LocalName is "items" or "purge" or "delete" or "subscription")
                      ?.Attr("node") ?? "";

    /// <summary>
    /// The namespace of the SHIM headers (XEP-0131).
    /// </summary>
    public const string ShimNamespace = "http://jabber.org/protocol/shim";

    /// <summary>
    /// The subscription a report belongs to - from the SHIM header
    /// <c>SubID</c> (XEP-0060, section 12.20), or null.
    /// </summary>
    /// <remarks>
    /// It stands beside the <c>event</c> and not in it: it says something about
    /// the delivery and not about the occurrence. The same publication can
    /// arrive several times, once per subscription - then this header is the
    /// only thing the reports differ in.
    /// </remarks>
    private static String? SubIdOf(XElement stanza)
        => stanza.Child(ShimNamespace, "headers")
                ?.Children(ShimNamespace, "header")
                 .FirstOrDefault(h => h.Attr("name") == "SubID")
                ?.Value;

    /// <summary>
    /// May a report about this node come from this sender?
    /// </summary>
    /// <remarks>
    /// <b>Three senders may, for three different reasons.</b>
    ///
    /// The configured service, which is what this manager was built around.
    ///
    /// A service this client holds a confirmed subscription with, and only for
    /// the node that subscription is on - <b>bound to the node and not to the
    /// sender</b>: whoever has subscribed to one node of a service has not
    /// thereby permitted it to report every node they can think up.
    ///
    /// This account itself (<see cref="OwnAccount"/>), about any node of its
    /// own - the one sender not bound to a node, because what an account
    /// publishes to is not what it asks to be told about.
    ///
    /// And another account, about a node of its own - but only a node this
    /// client has asked to be told about (<see cref="AsksToBeNotifiedOf"/>).
    ///
    /// <b>That third one is new, and it is the one that was missing.</b> D71
    /// added the second in order to cover PEP and could not: a PEP
    /// notification is not a subscription this client ever applied for.
    /// Delivery follows presence and caps (XEP-0163, section 4.2), so nothing
    /// is entered in the subscription table and there was nothing for the
    /// second rule to find. Every single avatar notification from a contact
    /// was therefore written to the log as a forgery - the exact line being
    /// <i>PubSub spoofing detected! From: someone@example.org, node:
    /// urn:xmpp:avatar:metadata, expected: pubsub.example.com</i>, where the
    /// expectation was the one thing a PEP notification can never meet.
    ///
    /// Harmless, in that <c>ProcessPepEventAsync</c> runs on its own path and
    /// the avatars arrived anyway. Not harmless as a habit: <b>a security log
    /// that cries wolf at every ordinary event is a security log nobody reads
    /// the one time it is right.</b>
    ///
    /// <b>Asked for, because under PEP that is what being subscribed means.</b>
    /// Nobody applies for an avatar node; a client announces <c>+notify</c>
    /// for it and the contact's server pushes it from then on. So the
    /// permission is bound to the node here too, by the only register that
    /// says anything about PEP interest - and an account reporting a node this
    /// client never announced is as unasked-for as a component reporting one
    /// nobody subscribed to. Both stay refused, which is what keeps this from
    /// being "any account may report anything".
    ///
    /// <b>Why an account may at all is not a hole.</b> A PEP report says
    /// something about the account it came from and about no other - that is
    /// what makes an account its own service, and it is why Bob's avatar and
    /// Carol's are not each other's business to refuse. What makes it safe to
    /// pass on is that it is passed on with its sender
    /// (<see cref="PubSubEvent.From"/>), because the node name does not say
    /// whose node it is: <c>urn:xmpp:avatar:metadata</c> is the name of a node
    /// every account has.
    ///
    /// <b>And why this account needed a rule of its own.</b> Measured by
    /// <c>+notify</c> it was a stranger at its own door: a client that fetches
    /// a node when it needs it - which is what this one does with OMEMO's -
    /// announces no interest in being told, publishes all the same, and gets
    /// the server's notification of that publication back. Two of those per
    /// connection stood in the log as attempted forgery, which is the same
    /// fault as the one above with the contacts and was left over from it.
    /// </remarks>
    private Boolean IsAcceptableSource(JID from, String nodeId, JID expectedPubSubJid)
    {

        var bareFrom = from.Bare;

        if (bareFrom == expectedPubSubJid.Bare)
            return true;

        if (SubscriptionsOf(nodeId).Any(sub => bareFrom == sub.ServiceJid.Bare))
            return true;

        // XEP-0163: this account about a node of its own, and not bound to a
        // node - the nodes an account has are whatever its protocols make of
        // it, so no list kept here would stay complete. See OwnAccount for why
        // the rule below cannot stand in for this one.
        //
        // <b>Not a hole, because of who may wear this address.</b> A server
        // stamps the from of its own users (RFC 6120, section 8.1.2.1) and
        // refuses a foreign domain that claims one of them, so what is let in
        // here is this stream's own server or another resource of this account
        // - inside the trust this stream already rests on. And every branch
        // that writes anything down is bound to the sender in any case, which
        // is where that binding belongs: see RemoveSubscriptionsOf.
        if (OwnAccount is JID own && bareFrom == own.Bare)
            return true;

        // XEP-0163: an account, and therefore its own service - about a node
        // this client has announced it wants. A localpart is what tells an
        // account from a component, and the whole of what tells them apart,
        // which is why the sender travels with the event.
        return from.Localpart is not null &&
               AsksToBeNotifiedOf?.Invoke(nodeId) == true;

    }

    /// <summary>
    /// Enters a granted subscription.
    /// </summary>
    /// <remarks>
    /// The same identifier a second time replaces the entry instead of doubling
    /// it: that is not a second grant but the same one once more.
    /// </remarks>
    public void AddSubscription(PubSubSubscription subscription)
    {
        lock (_lock)
        {

            if (!_subscriptions.TryGetValue(subscription.NodeId, out var subs))
                _subscriptions[subscription.NodeId] = subs = [];

            subs.RemoveAll(a => a.SubId is not null &&
                                String.Equals(a.SubId, subscription.SubId, StringComparison.Ordinal));

            subs.Add(subscription);

        }
    }

    /// <summary>
    /// Strikes a subscription from the bookkeeping.
    /// </summary>
    /// <param name="subId">
    /// The identifier of the ended subscription, or null for all of this node -
    /// the latter right only where there demonstrably was only one.
    /// </param>
    public void RemoveSubscription(String nodeId, String? subId = null)
    {
        lock (_lock)
        {

            if (subId is null)
            {
                _subscriptions.Remove(nodeId);
                return;
            }

            if (!_subscriptions.TryGetValue(nodeId, out var subs))
                return;

            subs.RemoveAll(a => String.Equals(a.SubId, subId, StringComparison.Ordinal));

            if (subs.Count == 0)
                _subscriptions.Remove(nodeId);

        }
    }

    /// <summary>
    /// Strikes the subscriptions of a node <b>at a particular service</b>.
    /// </summary>
    /// <param name="subId">
    /// The one subscription meant, or null for every one of this node held with
    /// that service - which is what a service naming no identifier means
    /// (section 12.19: it names them when it keeps several).
    /// </param>
    /// <remarks>
    /// The service belongs with it because the node name alone is none:
    /// <c>urn:xmpp:omemo:2:bundles</c> is called that at every account. Whoever
    /// strikes it without the address ends, on a deleted node, the subscription
    /// to somebody else's node of the same name too - and notices it only when
    /// their reports stay away.
    /// </remarks>
    public void RemoveSubscriptionsOf(String nodeId, JID serviceJid, String? subId = null)
    {
        lock (_lock)
        {

            if (!_subscriptions.TryGetValue(nodeId, out var subs))
                return;

            subs.RemoveAll(a => a.ServiceJid.Bare == serviceJid.Bare &&
                               (subId is null ||
                                String.Equals(a.SubId, subId, StringComparison.Ordinal)));

            if (subs.Count == 0)
                _subscriptions.Remove(nodeId);

        }
    }

    /// <summary>
    /// Is there a <b>granted</b> subscription on this node?
    /// </summary>
    /// <remarks>
    /// <b>Granted and not merely entered.</b> Since D95 an applied-for one
    /// stands in the bookkeeping too - otherwise this client would not even know
    /// after a <c>pending</c> what it had asked for. With that "is something
    /// entered" and "am I subscribed" become two things, and the question that
    /// stands here is the second one.
    /// </remarks>
    public Boolean IsSubscribed(String nodeId)
    {
        lock (_lock)
            return _subscriptions.TryGetValue(nodeId, out var subs) &&
                   subs.Any(a => a.State == PubSubSubscriptionState.Subscribed);
    }

    /// <summary>
    /// Enters the grant on an applied-for subscription (XEP-0060,
    /// section 8.6).
    /// </summary>
    /// <param name="subId">
    /// The identifier from the report, or null - then all applied-for
    /// subscriptions of this node at this service are meant.
    /// </param>
    /// <returns>
    /// false when there was no pending application for it. <b>Then the report is
    /// no answer to a question of this client's</b>, and it is not accepted:
    /// whoever accepted it would let themselves be signed up by a service
    /// unasked.
    /// </returns>
    public Boolean ApproveSubscription(String nodeId, String? subId, JID serviceJid)
    {
        lock (_lock)
        {

            if (!_subscriptions.TryGetValue(nodeId, out var subs))
                return false;

            var pending = subs.FindAll(a => a.State == PubSubSubscriptionState.Pending &&
                                          (subId is null || String.Equals(a.SubId, subId, StringComparison.Ordinal)) &&
                                          a.ServiceJid.Bare == serviceJid.Bare);

            foreach (var one in pending)
                subs[subs.IndexOf(one)] = one with { State = PubSubSubscriptionState.Subscribed };

            return pending.Count > 0;

        }
    }

    /// <summary>
    /// Notes down what the service has said about the settings of a
    /// subscription.
    /// </summary>
    /// <remarks>
    /// Only what was confirmed: a wish the service has refused must not land
    /// here as the state that holds - the same error as a subscription that is
    /// entered before the grant.
    /// </remarks>
    public void SetOptions(String nodeId, String? subId, PubSubSubscriptionOptions options)
    {
        lock (_lock)
        {

            if (!_subscriptions.TryGetValue(nodeId, out var subs))
                return;

            for (var i = 0; i < subs.Count; i++)
                if (subId is null || String.Equals(subs[i].SubId, subId, StringComparison.Ordinal))
                    subs[i] = subs[i] with { Options = options };

        }
    }

    /// <summary>
    /// The subscriptions of this node - none, one or several.
    /// </summary>
    public IReadOnlyList<PubSubSubscription> SubscriptionsOf(String nodeId)
    {
        lock (_lock) return _subscriptions.TryGetValue(nodeId, out var subs) ? [.. subs] : [];
    }

    /// <summary>
    /// All subscriptions, across all nodes.
    /// </summary>
    public IReadOnlyList<PubSubSubscription> Subscriptions
    {
        get { lock (_lock) return [.. _subscriptions.Values.SelectMany(a => a)]; }
    }

    /// <summary>
    /// Takes over what a service has said about one's own subscriptions.
    /// </summary>
    /// <remarks>
    /// <b>Replace and not add to.</b> The answer is complete for this service;
    /// what still stands here from it and no longer occurs there does not exist
    /// any more. To merge them would mean putting a memory beside a piece of
    /// information and holding both for true - and sending, at the next
    /// unsubscribing, an identifier nobody knows any more.
    ///
    /// <b>What the service does not name is not touched</b>: subscriptions at
    /// other services are none of its business.
    /// </remarks>
    public void ReplaceSubscriptionsOf(JID serviceJid, IEnumerable<PubSubSubscription> subscriptions)
    {
        lock (_lock)
        {

            foreach (var node in _subscriptions.Keys.ToList())
            {

                _subscriptions[node].RemoveAll(
                    a => a.ServiceJid.Bare == serviceJid.Bare);

                if (_subscriptions[node].Count == 0)
                    _subscriptions.Remove(node);

            }

            foreach (var sub in subscriptions)
                AddSubscription(sub);

        }
    }
}
