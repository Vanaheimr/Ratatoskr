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

    /// <summary>The namespace of the PubSub notifications.</summary>
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
            _logger.LogWarning("PubSub spoofing detected! From: {From}, node: {Node}, expected: {Expected}",
                               from, nodeId, expectedPubSubJid);
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

                var retractEvent = new PubSubEvent(nodeId, PubSubEventType.Retract, subId);

                foreach (var retract in retracted)
                {
                    var retractId = retract.Attr("id");
                    if (retractId is not null)
                        retractEvent.RetractedIds.Add(retractId);
                }

                await OnEvent.InvokeAllAsync(handler => handler(Timestamp.Now, this, retractEvent, CancellationToken), _logger);
                return true;

            }

            var itemsEvent = new PubSubEvent(nodeId, PubSubEventType.Items, subId);

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
            await OnEvent.InvokeAllAsync(handler => handler(Timestamp.Now, this, new PubSubEvent(nodeId, PubSubEventType.Purge, subId), CancellationToken), _logger);
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

            await OnEvent.InvokeAllAsync(handler => handler(Timestamp.Now, this, new PubSubEvent(nodeId, PubSubEventType.Delete, subId), CancellationToken), _logger);

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
                                               report.Attr("subid"));

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
            RemoveSubscription(nodeId, ended);

            await OnEvent.InvokeAllAsync(handler => handler(Timestamp.Now, this, new PubSubEvent(nodeId, PubSubEventType.SubscriptionEnded, ended), CancellationToken), _logger);

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

    /// <summary>The namespace of the SHIM headers (XEP-0131).</summary>
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
    /// <b>Until D71 the answer was the configured service alone</b> - right for
    /// a PubSub service as a component of its own, wrong for PEP: there the
    /// report comes from the account itself (XEP-0163, section 4.3), and every
    /// single one therefore counted as a forgery. It did not show up because
    /// nobody had a subscription whose reports anybody expected - OMEMO goes its
    /// own way.
    ///
    /// The second permission is therefore <b>bound to the node and not to the
    /// sender</b>: whoever has subscribed to Bob's weather node has not thereby
    /// permitted Bob to send them reports about every node he can think up.
    /// </remarks>
    private Boolean IsAcceptableSource(JID from, String nodeId, JID expectedPubSubJid)
    {

        var bareFrom = from.Bare;

        if (bareFrom == expectedPubSubJid.Bare)
            return true;

        return SubscriptionsOf(nodeId).Any(sub => bareFrom == sub.ServiceJid.Bare);

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
    /// Strikes all subscriptions of a node <b>at a particular service</b>.
    /// </summary>
    /// <remarks>
    /// The service belongs with it because the node name alone is none:
    /// <c>urn:xmpp:omemo:2:bundles</c> is called that at every account. Whoever
    /// strikes it without the address ends, on a deleted node, the subscription
    /// to somebody else's node of the same name too - and notices it only when
    /// their reports stay away.
    /// </remarks>
    public void RemoveSubscriptionsOf(String nodeId, JID serviceJid)
    {
        lock (_lock)
        {

            if (!_subscriptions.TryGetValue(nodeId, out var subs))
                return;

            subs.RemoveAll(a => a.ServiceJid.Bare == serviceJid.Bare);

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

    /// <summary>All subscriptions, across all nodes.</summary>
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
