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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0060, sections 6.1 and 6.2: subscribing to and unsubscribing from a
    /// PEP node - the confirmation a client can wait for.
    /// </summary>
    /// <remarks>
    /// <b>Up to here the test server said <c>&lt;service-unavailable/&gt;</c>
    /// to every <c>subscribe</c></b>, because it did not know the request at
    /// all. That is no good ground for a client that is meant to learn to
    /// evaluate answers: whoever only knows refusals cannot show that they
    /// read a confirmation properly.
    ///
    /// And a subscription that has no effect anywhere would be a promise
    /// without cover - the same mistake for which an event that was never
    /// raised was struck in D57. This is why this collection checks not only
    /// the answer but the effect: <b>whoever has subscribed gets the next
    /// publication - even without a presence subscription.</b> That is exactly
    /// what tells a subscription from what this server could do before.
    /// </remarks>
    [TestFixture]
    public class PepSubscriptionTests : AXMPPTests
    {

        #region Helpers

        private const String PubSubNamespace = "http://jabber.org/protocol/pubsub";
        private const String ErrorNamespace  = "http://jabber.org/protocol/pubsub#errors";
        private const String Node            = "urn:example:weather";

        /// <summary>
        /// Sends an IQ and gives back the answer with the same id.
        /// </summary>
        /// <remarks>
        /// Over <see cref="XMPPClient.OnRawXml"/> and not over the client: what
        /// is checked here is the answer of the <i>server</i>. If it went
        /// through the evaluation of the client, the test would in the end
        /// check both at once - and a mistake could no longer be assigned.
        /// </remarks>
        private static async Task<XElement> AskAsync(XMPPClient client, String id, String iq)
        {

            var replies = new List<String>();

            void Collect(String xml)
            {
                if (xml.StartsWith("<<< ", StringComparison.Ordinal) &&
                    xml.Contains($"id='{id}'", StringComparison.Ordinal))
                {
                    lock (replies)
                        replies.Add(xml[4..]);
                }
            }

            client.Connection.OnRawXml += Collect;

            try
            {

                await client.SendRawAsync(iq);

                await WaitFor(() => { lock (replies) return replies.Count > 0; },
                              $"the answer to '{id}'");

                lock (replies)
                    return XElement.Parse(replies[0]);

            }
            finally
            {
                client.Connection.OnRawXml -= Collect;
            }

        }

        /// <summary>
        /// Collects the PubSub notifications that come in at a client.
        /// </summary>
        private static List<String> CollectEvents(XMPPClient client)
        {

            var events = new List<String>();

            client.Connection.OnRawXml += xml =>
            {
                if (xml.StartsWith("<<< ", StringComparison.Ordinal) &&
                    xml.Contains(PubSubManager.EventNamespace, StringComparison.Ordinal))
                {
                    lock (events)
                        events.Add(xml[4..]);
                }
            };

            return events;

        }

        private static Int32 Count(List<String> events)
        {
            lock (events)
                return events.Count;
        }

        private static String PublishIq(String id, String node, String itemId, String payload)

            => $"<iq type='set' id='{id}'>" +
               $"<pubsub xmlns='{PubSubNamespace}'>" +
               $"<publish node='{node}'><item id='{itemId}'>{payload}</item></publish>" +
               "</pubsub></iq>";

        /// <summary>The error condition of an answer, or null.</summary>
        private static String? ConditionOf(XElement reply)
            => reply.Elements().FirstOrDefault(e => e.Name.LocalName == "error")
                   ?.Elements().FirstOrDefault(e => e.Name.NamespaceName ==
                                                    "urn:ietf:params:xml:ns:xmpp-stanzas")
                   ?.Name.LocalName;

        /// <summary>
        /// The severity of the error: modify means "not like this, but perhaps
        /// otherwise", cancel means "not at all" (RFC 6120, section 8.3.2).
        /// </summary>
        private static String? ErrorTypeOf(XElement reply)
            => reply.Elements().FirstOrDefault(e => e.Name.LocalName == "error")?.Attr("type");

        /// <summary>The PubSub-own error condition of an answer, or null.</summary>
        private static String? PubSubConditionOf(XElement reply)
            => reply.Elements().FirstOrDefault(e => e.Name.LocalName == "error")
                   ?.Elements().FirstOrDefault(e => e.Name.NamespaceName == ErrorNamespace)
                   ?.Name.LocalName;

        private static XElement? SubscriptionOf(XElement reply)
            => reply.Child(PubSubNamespace, "pubsub")
                   ?.Child(PubSubNamespace, "subscription");

        /// <summary>
        /// Subscribes and gives back the id from the confirmation.
        /// </summary>
        private async Task<String> SubscribeAsync(XMPPClient client, String id)
        {

            var grant = await AskAsync(client, id,
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                               client.BareJid, id));

            Assert.That(grant.Attr("type"), Is.EqualTo("result"), $"confirmation for '{id}'");

            var subId = SubscriptionOf(grant)?.Attr("subid");

            Assert.That(subId, Is.Not.Null.And.Not.Empty, $"subid in the confirmation for '{id}'");

            return subId!;

        }

        /// <summary>
        /// The subscription ids from the SHIM headers of the collected
        /// notifications (XEP-0060, section 12.20).
        /// </summary>
        private static List<String> SubIdsIn(List<String> events)
        {
            lock (events)
                return [.. events
                           .Select(e => XElement.Parse(e)
                                                .Child("http://jabber.org/protocol/shim", "headers")
                                               ?.Children("http://jabber.org/protocol/shim", "header")
                                                .FirstOrDefault(h => h.Attr("name") == "SubID")
                                               ?.Value)
                           .Where (s => s is not null)
                           .Select(s => s!)];
        }

        /// <summary>
        /// An <c>&lt;options/&gt;</c> IQ, with a form if wanted.
        /// </summary>
        private String OptionsIq(String   id,
                                 String   kind,
                                 String?  subId    = null,
                                 String?  formular = null,
                                 String?  jid      = null)

            => $"<iq type='{kind}' to='bob@{Server.Domain}' id='{id}'>" +
               $"<pubsub xmlns='{PubSubNamespace}'>" +
               $"<options node='{Node}' jid='{jid ?? $"alice@{Server.Domain}"}'" +
               (subId is not null ? $" subid='{subId}'" : "") +
               (formular is null ? "/>" : $">{formular}</options>") +
               "</pubsub></iq>";

        /// <summary>A submitted form with the given fields.</summary>
        private static String SubmitForm(String fields, String kind = "submit")
            => $"<x xmlns='jabber:x:data' type='{kind}'>" +
               "<field var='FORM_TYPE' type='hidden'>" +
               "<value>http://jabber.org/protocol/pubsub#subscribe_options</value></field>" +
               fields +
               "</x>";

        private static String DeliverField(String value)
            => $"<field var='pubsub#deliver'><value>{value}</value></field>";

        /// <summary>A collective query of the own subscriptions.</summary>
        private String SubscriptionsIq(String id, String? node = null)
            => $"<iq type='get' to='bob@{Server.Domain}' id='{id}'>" +
               $"<pubsub xmlns='{PubSubNamespace}'>" +
               "<subscriptions" + (node is null ? "" : $" node='{node}'") + "/>" +
               "</pubsub></iq>";

        /// <summary>The entries of a subscription list.</summary>
        private static List<XElement> SubscriptionsIn(XElement reply, String? ns = null)
            => [.. reply.Child(ns ?? PubSubNamespace, "pubsub")
                         ?.Child(ns ?? PubSubNamespace, "subscriptions")
                       ?.Children(ns ?? PubSubNamespace, "subscription") ?? []];

        private const String OwnerNamespace = "http://jabber.org/protocol/pubsub#owner";

        /// <summary>
        /// The subscriber query of the owner (XEP-0060, section 8.8).
        /// </summary>
        private String NodeSubscriptionsIq(String   id,
                                           String   kind,
                                           String?  content = null,
                                           String?  node    = null)

            => $"<iq type='{kind}' to='bob@{Server.Domain}' id='{id}'>" +
               $"<pubsub xmlns='{OwnerNamespace}'>" +
               $"<subscriptions node='{node ?? Node}'" +
               (content is null ? "/>" : $">{content}</subscriptions>") +
               "</pubsub></iq>";

        /// <summary>An entry in a subscriber instruction.</summary>
        private static String SubscriberEntry(String jid, String state, String? subId = null)
            => $"<subscription jid='{jid}' subscription='{state}'" +
               (subId is null ? "" : $" subid='{subId}'") + "/>";

        /// <summary>A <c>&lt;configure/&gt;</c> IQ in the owner namespace.</summary>
        private String ConfigureIq(String   id,
                                   String   kind,
                                   String?  formular = null,
                                   String?  node     = null)

            => $"<iq type='{kind}' to='bob@{Server.Domain}' id='{id}'>" +
               $"<pubsub xmlns='{OwnerNamespace}'>" +
               $"<configure node='{node ?? Node}'" +
               (formular is null ? "/>" : $">{formular}</configure>") +
               "</pubsub></iq>";

        /// <summary>A role query of the owner (XEP-0060, section 8.9).</summary>
        private String AffiliationsIq(String   id,
                                      String   kind,
                                      String?  content = null,
                                      String?  node    = null)

            => $"<iq type='{kind}' to='bob@{Server.Domain}' id='{id}'>" +
               $"<pubsub xmlns='{OwnerNamespace}'>" +
               $"<affiliations node='{node ?? Node}'" +
               (content is null ? "/>" : $">{content}</affiliations>") +
               "</pubsub></iq>";

        /// <summary>The question about the own roles (XEP-0060, section 5.7).</summary>
        private String OwnAffiliationsIq(String id)
            => $"<iq type='get' to='bob@{Server.Domain}' id='{id}'>" +
               $"<pubsub xmlns='{PubSubNamespace}'><affiliations/></pubsub></iq>";

        /// <summary>The entries of a role list.</summary>
        private static List<XElement> AffiliationsIn(XElement reply, String? ns = null)
            => [.. reply.Child(ns ?? OwnerNamespace, "pubsub")
                         ?.Child(ns ?? OwnerNamespace, "affiliations")
                       ?.Children(ns ?? OwnerNamespace, "affiliation") ?? []];

        /// <summary>A submitted node form.</summary>
        private static String ConfigForm(String fields)
            => "<x xmlns='jabber:x:data' type='submit'>" +
               "<field var='FORM_TYPE' type='hidden'>" +
               "<value>http://jabber.org/protocol/pubsub#node_config</value></field>" +
               fields +
               "</x>";

        /// <summary>A condition form for a publication.</summary>
        private static String PublishOptionsForm(String fields)
            => "<x xmlns='jabber:x:data' type='submit'>" +
               "<field var='FORM_TYPE' type='hidden'>" +
               "<value>http://jabber.org/protocol/pubsub#publish-options</value></field>" +
               fields +
               "</x>";

        /// <summary>The value of a field in the node form of an answer.</summary>
        private static String? ConfigField(XElement reply, String var)
            => reply.Child(OwnerNamespace, "pubsub")
                   ?.Child(OwnerNamespace, "configure")
                   ?.Child("jabber:x:data", "x")
                   ?.Children("jabber:x:data", "field")
                    .FirstOrDefault(f => f.Attr("var") == var)
                   ?.Child("jabber:x:data", "value")
                   ?.Value;

        /// <summary>All values of a field in the node form of an answer.</summary>
        private static List<String> ConfigValues(XElement reply, String var)
            => [.. reply.Child(OwnerNamespace, "pubsub")
                         ?.Child(OwnerNamespace, "configure")
                       ?.Child("jabber:x:data", "x")
                       ?.Children("jabber:x:data", "field")
                        .FirstOrDefault(f => f.Attr("var") == var)
                       ?.Children("jabber:x:data", "value")
                        .Select(v => v.Value) ?? []];

        /// <summary>The value of a form field in an answer.</summary>
        private static String? FieldValue(XElement reply, String var)
            => reply.Child(PubSubNamespace, "pubsub")
                   ?.Child(PubSubNamespace, "options")
                   ?.Child("jabber:x:data", "x")
                   ?.Children("jabber:x:data", "field")
                    .FirstOrDefault(f => f.Attr("var") == var)
                   ?.Child("jabber:x:data", "value")
                   ?.Value;

        /// <summary>
        /// Collects the incoming frames of a client that hold a certain text.
        /// </summary>
        private static List<String> CollectRaw(XMPPClient client, String contains)
        {

            var frames = new List<String>();

            client.Connection.OnRawXml += xml =>
            {
                if (xml.StartsWith("<<< ", StringComparison.Ordinal) &&
                    xml.Contains(contains, StringComparison.Ordinal))
                {
                    lock (frames)
                        frames.Add(xml[4..]);
                }
            };

            return frames;

        }

        /// <summary>The value of a field in any form.</summary>
        private static String? FormValue(XElement formular, String var)
            => formular.Children("jabber:x:data", "field")
                       .FirstOrDefault(f => f.Attr("var") == var)
                      ?.Child("jabber:x:data", "value")
                      ?.Value;

        /// <summary>
        /// The answer of the owner to an application (XEP-0060, section
        /// 8.6.2).
        /// </summary>
        private String AuthorizationAnswer(String jid, String subId, Boolean ja, String? an = null)
            => $"<message to='{an ?? $"bob@{Server.Domain}"}'>" +
               "<x xmlns='jabber:x:data' type='submit'>" +
               "<field var='FORM_TYPE'><value>" + PubSubSubscribeAuthorization.FormType + "</value></field>" +
               $"<field var='pubsub#node'><value>{Node}</value></field>" +
               $"<field var='pubsub#subid'><value>{subId}</value></field>" +
               $"<field var='pubsub#subscriber_jid'><value>{jid}</value></field>" +
               $"<field var='pubsub#allow'><value>{(ja ? "1" : "0")}</value></field>" +
               "</x></message>";

        /// <summary>
        /// A retraction (XEP-0060, section 7.2) - in the ordinary namespace
        /// and not in that of the owner: whoever may publish may also retract.
        /// </summary>
        private String RetractIq(String id, String? itemId, String? node = null)
            => $"<iq type='set' to='bob@{Server.Domain}' id='{id}'>" +
               $"<pubsub xmlns='{PubSubNamespace}'>" +
               $"<retract node='{node ?? Node}'>" +
               (itemId is null ? "" : $"<item id='{itemId}'/>") +
               "</retract></pubsub></iq>";

        /// <summary>
        /// The ids of the delivered items in the collected events.
        /// </summary>
        private static List<String?> ItemIdsIn(List<String> events)
        {
            lock (events)
                return [.. events
                           .SelectMany(e => XElement.Parse(e)
                                                    .Child(PubSubManager.EventNamespace, "event")
                                                   ?.Child(PubSubManager.EventNamespace, "items")
                                                   ?.Children(PubSubManager.EventNamespace, "item") ?? [])
                           .Select(i => i.Attr("id"))];
        }

        /// <summary>
        /// The retracted items in the collected events (XEP-0060, section
        /// 7.2.2.1).
        /// </summary>
        private static List<String?> RetractsIn(List<String> events)
        {
            lock (events)
                return [.. events
                           .SelectMany(e => XElement.Parse(e)
                                                    .Child(PubSubManager.EventNamespace, "event")
                                                   ?.Child(PubSubManager.EventNamespace, "items")
                                                   ?.Children(PubSubManager.EventNamespace, "retract") ?? [])
                           .Select(r => r.Attr("id"))];
        }

        /// <summary>
        /// An instruction of the owner without content - <c>&lt;delete/&gt;</c>
        /// or <c>&lt;purge/&gt;</c>.
        /// </summary>
        private String OwnerIq(String id, String kind, String element, String? node = null)
            => $"<iq type='{kind}' to='bob@{Server.Domain}' id='{id}'>" +
               $"<pubsub xmlns='{OwnerNamespace}'>" +
               $"<{element} node='{node ?? Node}'/>" +
               "</pubsub></iq>";

        /// <summary>
        /// The node events in the collected events (XEP-0060, sections 8.4.2
        /// and 8.5.2) - per entry the kind and the node.
        /// </summary>
        private static List<(String Kind, String? Node)> NodeEventsIn(List<String> events)
        {
            lock (events)
                return [.. events
                           .SelectMany(e => XElement.Parse(e)
                                                    .Child(PubSubManager.EventNamespace, "event")
                                                   ?.Elements() ?? [])
                           .Where (e => e.Name.LocalName is "delete" or "purge")
                           .Select(e => (e.Name.LocalName, e.Attr("node")))];
        }

        /// <summary>
        /// The endings from the collected events (XEP-0060, section 8.8.4) -
        /// per entry the node, the JID and the id.
        /// </summary>
        private static List<(String? Node, String? Jid, String? SubId)> EndingsIn(List<String> events)
        {
            lock (events)
                return [.. events
                           .Select(e => XElement.Parse(e)
                                                .Child(PubSubManager.EventNamespace, "event")
                                               ?.Child(PubSubManager.EventNamespace, "subscription"))
                           .OfType<XElement>()
                           .Where (s => s.Attr("subscription") == "none")
                           .Select(s => (s.Attr("node"), s.Attr("jid"), s.Attr("subid")))];
        }

        /// <summary>
        /// Bob publishes - the node exists afterwards.
        /// </summary>
        private async Task<XMPPClient> PublishingBobAsync(String itemId = "1", String content = "sunny")
        {

            var bob = await ConnectClientAsync("bob");

            await AskAsync(bob, $"pub-{itemId}",
                           PublishIq($"pub-{itemId}", Node, itemId,
                                     $"<weather xmlns='urn:example:x'>{content}</weather>"));

            return bob;

        }

        #endregion


        #region Subscribing_ToAPublishedNode_IsConfirmedWithASubId()

        /// <summary>
        /// XEP-0060, section 6.1.2: the confirmation names the node, the
        /// subscriber, a subscription id and the state.
        /// </summary>
        /// <remarks>
        /// The <c>subid</c> is the part a client has to remember and cannot
        /// make up itself: it comes from the service. Whoever does not read the
        /// answer never has it.
        /// </remarks>
        [Test]
        public async Task Subscribing_ToAPublishedNode_IsConfirmedWithASubId()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            var reply = await AskAsync(alice, "sub-1",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}",
                                                               Node,
                                                               alice.BareJid,
                                                               "sub-1"));

            Assert.Multiple(() =>
            {

                Assert.That(reply.Attr("type"), Is.EqualTo("result"),
                            "A node that exists has to be subscribable.");

                var sub = SubscriptionOf(reply);

                Assert.That(sub, Is.Not.Null, "The confirmation is missing.");
                Assert.That(sub!.Attr("node"),         Is.EqualTo(Node));
                Assert.That(sub!.Attr("subscription"), Is.EqualTo("subscribed"));
                Assert.That(sub!.Attr("jid"),          Is.EqualTo(alice.BareJid));
                Assert.That(sub!.Attr("subid"),        Is.Not.Null.And.Not.Empty,
                            "Without a subid nobody can name a subscription.");

            });

        }

        #endregion

        #region Subscribing_ToANodeThatDoesNotExist_IsRejected()

        /// <summary>
        /// XEP-0060, section 6.1.3.12: what does not exist cannot be
        /// subscribed to.
        /// </summary>
        [Test]
        public async Task Subscribing_ToANodeThatDoesNotExist_IsRejected()
        {

            await ConnectClientAsync("bob");

            var alice = await ConnectClientAsync("alice");

            var reply = await AskAsync(alice, "sub-2",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}",
                                                               "urn:example:doesnotexist",
                                                               alice.BareJid,
                                                               "sub-2"));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"), Is.EqualTo("error"));
                Assert.That(ConditionOf(reply), Is.EqualTo("item-not-found"));
            });

        }

        #endregion

        #region Subscribing_ForSomebodyElsesJid_IsRejected()

        /// <summary>
        /// XEP-0060, section 6.1.3.1: the <c>jid</c> has to be the one of the
        /// sender.
        /// </summary>
        /// <remarks>
        /// <b>That is no formality.</b> Without this check Alice could sign
        /// Carol up, and from then on Carol would get Bob's publications
        /// without ever having asked for anything - a delivery nobody chose and
        /// that Carol would not even know how to assign.
        /// </remarks>
        [Test]
        public async Task Subscribing_ForSomebodyElsesJid_IsRejected()
        {

            await PublishingBobAsync();

            Server.AddAccount("carol");

            var alice = await ConnectClientAsync("alice");

            var reply = await AskAsync(alice, "sub-3",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}",
                                                               Node,
                                                               $"carol@{Server.Domain}",
                                                               "sub-3"));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"),       Is.EqualTo("error"));
                Assert.That(ConditionOf(reply),       Is.EqualTo("bad-request"));
                Assert.That(ErrorTypeOf(reply),       Is.EqualTo("modify"));
                Assert.That(PubSubConditionOf(reply), Is.EqualTo("invalid-jid"),
                            "XEP-0060 names the reason by its name.");
            });

        }

        #endregion

        #region ASubscriber_GetsTheNextItem_WithoutAnyPresenceSubscription()

        /// <summary>
        /// The heart of the matter: a subscription brings notifications - even
        /// to somebody who may not see Bob's presence.
        /// </summary>
        /// <remarks>
        /// Before, a PEP notification went to exactly those who got presence
        /// anyway. With that, "subscribing" was nothing but another word for
        /// "standing in the roster" - and for a foreign node nobody reaches
        /// over presence there was no way at all.
        /// </remarks>
        [Test]
        public async Task ASubscriber_GetsTheNextItem_WithoutAnyPresenceSubscription()
        {

            var bob    = await PublishingBobAsync();
            var alice  = await ConnectClientAsync("alice");

            var reply = await AskAsync(alice, "sub-4",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}",
                                                               Node,
                                                               alice.BareJid,
                                                               "sub-4"));

            Assert.That(reply.Attr("type"), Is.EqualTo("result"));

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-2",
                           PublishIq("pub-2", Node, "2", "<weather xmlns='urn:example:x'>rain</weather>"));

            await WaitFor(() => Count(events) > 0,
                          "the notification to the subscriber");

            Assert.Multiple(() =>
            {

                Assert.That(events[0], Does.Contain(Node));
                Assert.That(events[0], Does.Contain("rain"));
                Assert.That(events[0], Does.Contain($"from='bob@{Server.Domain}'"),
                            "The notification comes from the account and not from the server.");

                // The payload sits in an <item/> with its id, and that is no
                // formality: a client that keeps items by their id passes over
                // an item without one entirely - the content would arrive and
                // still be lost.
                Assert.That(ItemIdsIn(events), Is.EqualTo(new[] { "2" }));

            });

        }

        #endregion

        #region WithoutASubscription_NothingArrives()

        /// <summary>
        /// The cross-check to the previous test: without a subscription and
        /// without presence Alice gets nothing.
        /// </summary>
        /// <remarks>
        /// Without it the previous test would only prove that something
        /// arrives - not that it is down to the subscription.
        /// </remarks>
        [Test]
        public async Task WithoutASubscription_NothingArrives()
        {

            var bob    = await PublishingBobAsync();
            var alice  = await ConnectClientAsync("alice");
            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-2",
                           PublishIq("pub-2", Node, "2", "<weather xmlns='urn:example:x'>rain</weather>"));

            await WaitAgainst(() => Count(events) > 0,
                              "a notification to somebody uninvolved");

        }

        #endregion

        #region Unsubscribing_StopsTheEvents()

        /// <summary>
        /// XEP-0060, section 6.2: after the unsubscribing nothing comes any
        /// more.
        /// </summary>
        [Test]
        public async Task Unsubscribing_StopsTheEvents()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await AskAsync(alice, "sub-5",
                           PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node, alice.BareJid, "sub-5"));

            var unsubscribed = await AskAsync(alice, "unsub-5",
                                              PubSubBuilder.Unsubscribe($"bob@{Server.Domain}",
                                                                        Node,
                                                                        alice.BareJid,
                                                                        "unsub-5"));

            Assert.That(unsubscribed.Attr("type"), Is.EqualTo("result"));

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-3",
                           PublishIq("pub-3", Node, "3", "<weather xmlns='urn:example:x'>snow</weather>"));

            await WaitAgainst(() => Count(events) > 0,
                              "a notification after the unsubscribing");

        }

        #endregion

        #region Unsubscribing_WithoutASubscription_IsRejected()

        /// <summary>
        /// XEP-0060, section 6.2.3.2: whoever has not subscribed cannot
        /// unsubscribe.
        /// </summary>
        [Test]
        public async Task Unsubscribing_WithoutASubscription_IsRejected()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            var reply = await AskAsync(alice, "unsub-6",
                                       PubSubBuilder.Unsubscribe($"bob@{Server.Domain}",
                                                                 Node,
                                                                 alice.BareJid,
                                                                 "unsub-6"));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"),       Is.EqualTo("error"));
                Assert.That(ConditionOf(reply),       Is.EqualTo("unexpected-request"));
                Assert.That(ErrorTypeOf(reply),       Is.EqualTo("cancel"));
                Assert.That(PubSubConditionOf(reply), Is.EqualTo("not-subscribed"));
            });

        }

        #endregion

        #region Unsubscribing_WithAForeignSubId_IsRejected()

        /// <summary>
        /// XEP-0060, section 6.2.3.1: a <c>subid</c> sent along that does not
        /// fit ends nothing.
        /// </summary>
        /// <remarks>
        /// The case is rare and the check necessary all the same: to let a
        /// wrong id through would mean ending an <i>other</i> subscription than
        /// the intended one - and confirming to the sender that it had been
        /// theirs.
        /// </remarks>
        [Test]
        public async Task Unsubscribing_WithAForeignSubId_IsRejected()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            await AskAsync(alice, "sub-7",
                           PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node, alice.BareJid, "sub-7"));

            var reply = await AskAsync(alice, "unsub-7",
                                       $"<iq type='set' to='bob@{Server.Domain}' id='unsub-7'>" +
                                       $"<pubsub xmlns='{PubSubNamespace}'>" +
                                       $"<unsubscribe node='{Node}' jid='{alice.BareJid}' subid='foreign'/>" +
                                       "</pubsub></iq>");

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"),       Is.EqualTo("error"));
                Assert.That(ConditionOf(reply),       Is.EqualTo("not-acceptable"));
                Assert.That(ErrorTypeOf(reply),       Is.EqualTo("modify"));
                Assert.That(PubSubConditionOf(reply), Is.EqualTo("invalid-subid"));
            });

        }

        #endregion

        #region Unsubscribing_ForSomebodyElse_LeavesTheirSubscriptionAlone()

        /// <summary>
        /// With the unsubscribing too the <c>jid</c> has to be the one of the
        /// sender.
        /// </summary>
        /// <remarks>
        /// The other direction from <see cref="Subscribing_ForSomebodyElsesJid_IsRejected"/>
        /// and the more dangerous of the two: to create a foreign subscription
        /// is a nuisance, to end a foreign one is a deprivation. Carol would
        /// get nothing any more and would not even know that something is
        /// missing - absence looks like quiet.
        ///
        /// The test therefore checks both: the refusal <b>and</b> that Carol's
        /// subscription still carries. To check only the refusal would let an
        /// implementation through that first signs off and then complains.
        /// </remarks>
        [Test]
        public async Task Unsubscribing_ForSomebodyElse_LeavesTheirSubscriptionAlone()
        {

            var bob   = await PublishingBobAsync();
            var carol = await ConnectClientAsync("carol");
            var alice = await ConnectClientAsync("alice");

            await AskAsync(carol, "sub-11",
                           PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node, carol.BareJid, "sub-11"));

            var reply = await AskAsync(alice, "unsub-11",
                                       PubSubBuilder.Unsubscribe($"bob@{Server.Domain}",
                                                                 Node,
                                                                 carol.BareJid,
                                                                 "unsub-11"));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"),       Is.EqualTo("error"));
                Assert.That(ConditionOf(reply),       Is.EqualTo("bad-request"));
                Assert.That(PubSubConditionOf(reply), Is.EqualTo("invalid-jid"));
            });

            var events = CollectEvents(carol);

            await AskAsync(bob, "pub-6",
                           PublishIq("pub-6", Node, "6", "<weather xmlns='urn:example:x'>storm</weather>"));

            await WaitFor(() => Count(events) > 0,
                          "the notification to Carol, whose subscription nobody was allowed to end");

        }

        #endregion

        #region TheSubIdFromTheConfirmation_Unsubscribes()

        /// <summary>
        /// The cross-check: with the id from the confirmation it works.
        /// </summary>
        /// <remarks>
        /// Without this test the previous one would only check that
        /// <i>some</i> subid is refused - an implementation that refuses every
        /// one would pass it just as well.
        /// </remarks>
        [Test]
        public async Task TheSubIdFromTheConfirmation_Unsubscribes()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            var grant = await AskAsync(alice, "sub-8",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                               alice.BareJid, "sub-8"));

            var subId = SubscriptionOf(grant)?.Attr("subid");

            Assert.That(subId, Is.Not.Null.And.Not.Empty);

            var reply = await AskAsync(alice, "unsub-8",
                                       $"<iq type='set' to='bob@{Server.Domain}' id='unsub-8'>" +
                                       $"<pubsub xmlns='{PubSubNamespace}'>" +
                                       $"<unsubscribe node='{Node}' jid='{alice.BareJid}' subid='{subId}'/>" +
                                       "</pubsub></iq>");

            Assert.That(reply.Attr("type"), Is.EqualTo("result"));

        }

        #endregion

        #region ASubscriberWhoIsAlsoAContact_GetsTheEventOnlyOnce()

        /// <summary>
        /// Whoever comes into question over both ways gets the notification
        /// once all the same.
        /// </summary>
        /// <remarks>
        /// Two sources for the same list of receivers are the obvious way to
        /// double a message. For a human being that would be annoying; for
        /// OMEMO it would be worse, because a device list arriving twice would
        /// be answered twice.
        /// </remarks>
        [Test]
        public async Task ASubscriberWhoIsAlsoAContact_GetsTheEventOnlyOnce()
        {

            MakeContacts("alice", "bob");

            var bob    = await PublishingBobAsync();
            var alice  = await ConnectClientAsync("alice");

            await AskAsync(alice, "sub-9",
                           PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node, alice.BareJid, "sub-9"));

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-4",
                           PublishIq("pub-4", Node, "4", "<weather xmlns='urn:example:x'>fog</weather>"));

            await WaitFor(() => Count(events) > 0, "the notification");

            await WaitAgainst(() => Count(events) > 1,
                              "a second notification about the same publication");

        }

        #endregion

        #region SubscribingTwice_YieldsTwoSubscriptions()

        /// <summary>
        /// XEP-0060, section 6.1: a second <c>subscribe</c> is a second
        /// subscription, with an id of its own and a delivery of its own.
        /// </summary>
        /// <remarks>
        /// <b>Until K3 the opposite stood here</b> - a second <c>subscribe</c>
        /// gave back the same id, and the delivery stayed single. That was not
        /// wrong (a service may proceed that way), but it made the <c>subid</c>
        /// an ornament: where there are never two, it names nothing one could
        /// not also tell from the node.
        ///
        /// The case is not made up. It comes about by itself when a client
        /// restarts and subscribes again without knowing its old id -
        /// afterwards the service has two, and from then on every unsubscribe
        /// without an id is ambiguous.
        /// </remarks>
        [Test]
        public async Task SubscribingTwice_YieldsTwoSubscriptions()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var first  = await SubscribeAsync(alice, "sub-10a");
            var second = await SubscribeAsync(alice, "sub-10b");

            Assert.That(second, Is.Not.EqualTo(first),
                        "Two subscriptions carrying the same id cannot be told apart.");

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-5",
                           PublishIq("pub-5", Node, "5", "<weather xmlns='urn:example:x'>hail</weather>"));

            await WaitFor(() => Count(events) > 1, "both notifications");

            Assert.That(SubIdsIn(events), Is.EquivalentTo(new[] { first, second }),
                        "Every delivery belongs to exactly one subscription and says to which.");

        }

        #endregion

        #region WithTwoSubscriptions_UnsubscribingWithoutASubId_IsRejected()

        /// <summary>
        /// XEP-0060, section 6.2.3.1: whoever has several has to say which.
        /// </summary>
        /// <remarks>
        /// The reason is the same as with the wrong id, only one step earlier:
        /// a service that picked one might end the wrong one - and confirm to
        /// the sender that it had been the intended one.
        /// </remarks>
        [Test]
        public async Task WithTwoSubscriptions_UnsubscribingWithoutASubId_IsRejected()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-12a");
            await SubscribeAsync(alice, "sub-12b");

            var reply = await AskAsync(alice, "unsub-12",
                                       PubSubBuilder.Unsubscribe($"bob@{Server.Domain}",
                                                                 Node,
                                                                 alice.BareJid,
                                                                 "unsub-12"));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"),       Is.EqualTo("error"));
                Assert.That(ConditionOf(reply),       Is.EqualTo("bad-request"));
                Assert.That(ErrorTypeOf(reply),       Is.EqualTo("modify"));
                Assert.That(PubSubConditionOf(reply), Is.EqualTo("subid-required"));
            });

        }

        #endregion

        #region WithTwoSubscriptions_TheSubIdEndsExactlyOne()

        /// <summary>
        /// And with an id exactly the named one ends.
        /// </summary>
        /// <remarks>
        /// The cross-check to the previous test, and the actual assurance: an
        /// unsubscribe that ended both would be just as unambiguous as it would
        /// be wrong.
        /// </remarks>
        [Test]
        public async Task WithTwoSubscriptions_TheSubIdEndsExactlyOne()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var first  = await SubscribeAsync(alice, "sub-13a");
            var second = await SubscribeAsync(alice, "sub-13b");

            var reply = await AskAsync(alice, "unsub-13",
                                       PubSubBuilder.Unsubscribe($"bob@{Server.Domain}", Node,
                                                                 alice.BareJid, "unsub-13", first));

            Assert.That(reply.Attr("type"), Is.EqualTo("result"));

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-7",
                           PublishIq("pub-7", Node, "7", "<weather xmlns='urn:example:x'>sleet</weather>"));

            await WaitFor(() => Count(events) > 0, "the remaining notification");

            await WaitAgainst(() => Count(events) > 1,
                              "a notification for the ended subscription");

            Assert.That(SubIdsIn(events), Is.EqualTo(new[] { second }),
                        "What was left over was not the one that should have been left.");

        }

        #endregion

        #region TheOptionsForm_OffersDelivery()

        /// <summary>
        /// XEP-0060, section 6.3.2: the form says what can be set.
        /// </summary>
        /// <remarks>
        /// <b>It holds exactly one field</b>, and that is the statement: what
        /// this server cannot do it does not offer either. A form with
        /// <c>pubsub#digest</c> in it that then has no effect would be a
        /// promise without cover - and one the subscriber can never check,
        /// because absent digests look like quiet.
        /// </remarks>
        [Test]
        public async Task TheOptionsForm_OffersDelivery()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-20");

            var reply = await AskAsync(alice, "opt-20", OptionsIq("opt-20", "get"));

            Assert.Multiple(() =>
            {

                Assert.That(reply.Attr("type"), Is.EqualTo("result"));

                Assert.That(FieldValue(reply, "FORM_TYPE"),
                            Is.EqualTo("http://jabber.org/protocol/pubsub#subscribe_options"));

                Assert.That(FieldValue(reply, "pubsub#deliver"), Is.EqualTo("1"),
                            "Delivery happens as long as nobody objects.");

            });

        }

        #endregion

        #region TheNodeConfigForm_OffersWhatTheServerCanDo()

        /// <summary>
        /// XEP-0060, section 8.2: the offer of the owner.
        /// </summary>
        /// <remarks>
        /// Three fields, and every one of them does something. The XEP knows
        /// two dozen more; offered is only what also takes effect - especially
        /// at this place, because an owner believes afterwards to have settled
        /// something.
        /// </remarks>
        [Test]
        public async Task TheNodeConfigForm_OffersWhatTheServerCanDo()
        {

            var bob = await PublishingBobAsync();

            var reply = await AskAsync(bob, "cfg-1", ConfigureIq("cfg-1", "get"));

            Assert.Multiple(() =>
            {

                Assert.That(reply.Attr("type"), Is.EqualTo("result"));

                Assert.That(ConfigField(reply, "FORM_TYPE"),
                            Is.EqualTo("http://jabber.org/protocol/pubsub#node_config"));

                Assert.That(ConfigField(reply, "pubsub#access_model"),   Is.EqualTo("open"));
                Assert.That(ConfigField(reply, "pubsub#max_items"),      Is.EqualTo("256"));
                Assert.That(ConfigField(reply, "pubsub#persist_items"),  Is.EqualTo("1"));

            });

        }

        #endregion

        #region TheConfiguration_IsReadBackAsItWasSet()

        /// <summary>
        /// What was set stands in the offer afterwards.
        /// </summary>
        [Test]
        public async Task TheConfiguration_IsReadBackAsItWasSet()
        {

            var bob = await PublishingBobAsync();

            var set = await AskAsync(bob, "cfg-2",
                                     ConfigureIq("cfg-2", "set",
                                                 ConfigForm("<field var='pubsub#max_items'><value>5</value></field>" +
                                                            "<field var='pubsub#access_model'><value>presence</value></field>")));

            Assert.That(set.Attr("type"), Is.EqualTo("result"));

            var loaded = await AskAsync(bob, "cfg-3", ConfigureIq("cfg-3", "get"));

            Assert.Multiple(() =>
            {
                Assert.That(ConfigField(loaded, "pubsub#max_items"),     Is.EqualTo("5"));
                Assert.That(ConfigField(loaded, "pubsub#access_model"),  Is.EqualTo("presence"));
                Assert.That(ConfigField(loaded, "pubsub#persist_items"), Is.EqualTo("1"),
                            "What did not stand in the partial form stays as it was.");
            });

            // And the proof of it: a second partial form must not set the first
            // value back to the default. XEP-0060, section 8.2.4 expressly
            // allows partial forms - whoever fills the missing fields with the
            // default changes silently what nobody asked about.
            await AskAsync(bob, "cfg-3b",
                           ConfigureIq("cfg-3b", "set",
                                       ConfigForm("<field var='pubsub#persist_items'><value>0</value></field>")));

            var onceMore = await AskAsync(bob, "cfg-3c", ConfigureIq("cfg-3c", "get"));

            Assert.Multiple(() =>
            {
                Assert.That(ConfigField(onceMore, "pubsub#persist_items"), Is.EqualTo("0"));
                Assert.That(ConfigField(onceMore, "pubsub#max_items"),     Is.EqualTo("5"),
                            "The state from before is the ground, not the default.");
            });

        }

        #endregion

        #region AConfigurationThatIsNoConfiguration_IsRejected()

        /// <summary>
        /// An unknown field, a number that is none, and a limit below one.
        /// </summary>
        /// <remarks>
        /// The same strictness as with the subscription options: what comes in
        /// is an instruction, and an instruction passed over is worse than one
        /// refused. <c>max_items=0</c> is no mere formal error in this but a
        /// trap - a node that may keep nothing would look like one nobody
        /// writes into.
        /// </remarks>
        [Test]
        public async Task AConfigurationThatIsNoConfiguration_IsRejected()
        {

            var bob = await PublishingBobAsync();

            foreach (var (id, field) in new[] {
                         ("cfg-11", "<field var='pubsub#digest'><value>1</value></field>"),
                         ("cfg-12", "<field var='pubsub#max_items'><value>many</value></field>"),
                         ("cfg-13", "<field var='pubsub#max_items'><value>0</value></field>")
                     })
            {

                var reply = await AskAsync(bob, id, ConfigureIq(id, "set", ConfigForm(field)));

                Assert.That(reply.Attr("type"), Is.EqualTo("error"), field);

            }

            var loaded = await AskAsync(bob, "cfg-14", ConfigureIq("cfg-14", "get"));

            Assert.That(ConfigField(loaded, "pubsub#max_items"), Is.EqualTo("256"),
                        "None of the refused requests may have changed anything.");

        }

        #endregion

        #region CreatingANodeInSomebodyElsesAccount_IsForbidden()

        /// <summary>
        /// Creating is allowed only at one's own place.
        /// </summary>
        /// <remarks>
        /// Otherwise anybody could create nodes in foreign accounts - and would
        /// not be their owner but their originator: the one concerned would
        /// find nodes in their list they never created, with settings they did
        /// not choose.
        /// </remarks>
        [Test]
        public async Task CreatingANodeInSomebodyElsesAccount_IsForbidden()
        {

            await ConnectClientAsync("bob");

            var alice = await ConnectClientAsync("alice");

            var reply = await AskAsync(alice, "new-4",
                                       PubSubBuilder.CreateNode($"bob@{Server.Domain}", "urn:example:foreign", "new-4"));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"), Is.EqualTo("error"));
                Assert.That(ConditionOf(reply), Is.EqualTo("forbidden"));
            });

            Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.PepNodeExists("urn:example:foreign"),
                        Is.False,
                        "A refused creating must not have created anything.");

        }

        #endregion

        #region MaxItems_LimitsWhatTheNodeKeeps()

        /// <summary>
        /// <c>pubsub#max_items</c> - the oldest gives way.
        /// </summary>
        [Test]
        public async Task MaxItems_LimitsWhatTheNodeKeeps()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-4",
                           ConfigureIq("cfg-4", "set",
                                       ConfigForm("<field var='pubsub#max_items'><value>2</value></field>")));

            await AskAsync(bob, "pub-30", PublishIq("pub-30", Node, "30", "<w xmlns='urn:example:x'>a</w>"));
            await AskAsync(bob, "pub-31", PublishIq("pub-31", Node, "31", "<w xmlns='urn:example:x'>b</w>"));

            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            Assert.That(account.GetPepItems(Node).Select(e => e.ItemId),
                        Is.EqualTo(new[] { "30", "31" }),
                        "The first item should have given way.");

        }

        #endregion

        #region ASmallerLimit_TakesEffectAtOnce()

        /// <summary>
        /// A smaller limit holds at once and not only from the next time.
        /// </summary>
        /// <remarks>
        /// Whoever sets it does not want so many kept - and the stock is
        /// exactly what is kept. To tidy up only at the next publication would
        /// mean: on a node where nothing ever appears again, everything stays
        /// lying about.
        /// </remarks>
        [Test]
        public async Task ASmallerLimit_TakesEffectAtOnce()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "pub-32", PublishIq("pub-32", Node, "32", "<w xmlns='urn:example:x'>b</w>"));
            await AskAsync(bob, "pub-33", PublishIq("pub-33", Node, "33", "<w xmlns='urn:example:x'>c</w>"));

            await AskAsync(bob, "cfg-5",
                           ConfigureIq("cfg-5", "set",
                                       ConfigForm("<field var='pubsub#max_items'><value>1</value></field>")));

            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            Assert.That(account.GetPepItems(Node).Select(e => e.ItemId),
                        Is.EqualTo(new[] { "33" }));

        }

        #endregion

        #region WithoutPersistence_TheNotificationGoesOut_ButNothingIsKept()

        /// <summary>
        /// <c>pubsub#persist_items=0</c>: the node notifies but keeps nothing.
        /// </summary>
        /// <remarks>
        /// Both halves belong in one test. To check only "keeps nothing" would
        /// pass against a server that does nothing at all any more - and then a
        /// node without storage would have become one without effect.
        /// </remarks>
        [Test]
        public async Task WithoutPersistence_TheNotificationGoesOut_ButNothingIsKept()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-30");

            await AskAsync(bob, "cfg-6",
                           ConfigureIq("cfg-6", "set",
                                       ConfigForm("<field var='pubsub#persist_items'><value>0</value></field>")));

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-34", PublishIq("pub-34", Node, "34", "<w xmlns='urn:example:x'>fleeting</w>"));

            await WaitFor(() => Count(events) > 0, "the notification");

            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            Assert.That(account.GetPepItems(Node).Select(e => e.ItemId),
                        Does.Not.Contain("34"),
                        "A node without storage keeps nothing.");

        }

        #endregion

        #region ACreatedNode_CanBeSubscribed_BeforeAnythingIsPublished()

        /// <summary>
        /// XEP-0060, section 8.1: a created node exists before anything stands
        /// in it.
        /// </summary>
        /// <remarks>
        /// Before, "the node exists" meant the same as "something stands in
        /// it". With that the creating had no consequence - and a node without
        /// storage could never be subscribed to at all.
        /// </remarks>
        [Test]
        public async Task ACreatedNode_CanBeSubscribed_BeforeAnythingIsPublished()
        {

            var bob = await ConnectClientAsync("bob");

            var created = await AskAsync(bob, "new-1",
                                         PubSubBuilder.CreateNode($"bob@{Server.Domain}", "urn:example:empty", "new-1"));

            Assert.That(created.Attr("type"), Is.EqualTo("result"));

            var alice = await ConnectClientAsync("alice");

            var grant = await AskAsync(alice, "sub-31",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}", "urn:example:empty",
                                                               alice.BareJid, "sub-31"));

            Assert.That(grant.Attr("type"), Is.EqualTo("result"),
                        "A created node has to be subscribable.");

        }

        #endregion

        #region CreatingANodeTwice_IsRejected()

        /// <summary>
        /// XEP-0060, section 8.1.3: what exists is not created a second time.
        /// </summary>
        /// <remarks>
        /// To let it pass in silence would mean replacing an existing setting
        /// with a new one without anybody having asked for it - and the new one
        /// would be the default.
        /// </remarks>
        [Test]
        public async Task CreatingANodeTwice_IsRejected()
        {

            var bob = await PublishingBobAsync();

            var reply = await AskAsync(bob, "new-2",
                                       PubSubBuilder.CreateNode($"bob@{Server.Domain}", Node, "new-2"));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"), Is.EqualTo("error"));
                Assert.That(ConditionOf(reply), Is.EqualTo("conflict"));
            });

        }

        #endregion

        #region CreatingWithAConfiguration_AppliesIt()

        /// <summary>
        /// XEP-0060, section 8.1.3: create and configure in one go.
        /// </summary>
        [Test]
        public async Task CreatingWithAConfiguration_AppliesIt()
        {

            var bob = await ConnectClientAsync("bob");

            await AskAsync(bob, "new-3",
                           $"<iq type='set' id='new-3'><pubsub xmlns='{PubSubNamespace}'>" +
                           "<create node='urn:example:tight'/>" +
                           "<configure>" +
                           ConfigForm("<field var='pubsub#max_items'><value>1</value></field>") +
                           "</configure></pubsub></iq>");

            await AskAsync(bob, "pub-35", PublishIq("pub-35", "urn:example:tight", "35", "<w xmlns='urn:example:x'>a</w>"));
            await AskAsync(bob, "pub-36", PublishIq("pub-36", "urn:example:tight", "36", "<w xmlns='urn:example:x'>b</w>"));

            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            Assert.That(account.GetPepItems("urn:example:tight").Select(e => e.ItemId),
                        Is.EqualTo(new[] { "36" }),
                        "The setting given along has to hold from the start.");

        }

        #endregion

        #region ConfiguringSomebodyElsesNode_IsForbidden()

        /// <summary>
        /// A PEP node belongs to a human being, and only they may configure it.
        /// </summary>
        /// <remarks>
        /// The fourth place with this check, and the furthest-reaching one:
        /// whoever could configure foreign nodes could switch off the storage
        /// and thereby make foreign bundles unreachable - silently, because a
        /// node that keeps nothing any more looks like one nobody has written
        /// anything into.
        /// </remarks>
        [Test]
        public async Task ConfiguringSomebodyElsesNode_IsForbidden()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            var reply = await AskAsync(alice, "cfg-7",
                                       ConfigureIq("cfg-7", "set",
                                                   ConfigForm("<field var='pubsub#persist_items'><value>0</value></field>")));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"), Is.EqualTo("error"));
                Assert.That(ConditionOf(reply), Is.EqualTo("forbidden"));
            });

        }

        #endregion

        #region ConfiguringANodeThatDoesNotExist_IsRejected()

        /// <summary>
        /// What does not exist cannot be configured.
        /// </summary>
        [Test]
        public async Task ConfiguringANodeThatDoesNotExist_IsRejected()
        {

            var bob = await ConnectClientAsync("bob");

            var reply = await AskAsync(bob, "cfg-8",
                                       ConfigureIq("cfg-8", "get", node: "urn:example:doesnotexist"));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"), Is.EqualTo("error"));
                Assert.That(ConditionOf(reply), Is.EqualTo("item-not-found"));
            });

        }

        #endregion

        #region AnAccessModelNobodyOffered_IsRejected()

        /// <summary>
        /// What is no access model is refused - and not silently turned into
        /// <c>open</c>.
        /// </summary>
        /// <remarks>
        /// <b>The most expensive place for a promise without cover.</b>
        /// Whoever sets something closed and gets <c>open</c> believes their
        /// items protected and has published them.
        ///
        /// <b>This test has lost its example twice</b>, and both times for the
        /// best reason: it was called <c>whitelist</c> until K13 and
        /// <c>authorize</c> until D93 - now both are offered, because they can
        /// be enforced. What is left is the case there will always be: a name
        /// nobody granted. Here <c>closed</c>, which sounds like an intention
        /// and is none.
        /// </remarks>
        [Test]
        public async Task AnAccessModelNobodyOffered_IsRejected()
        {

            var bob = await PublishingBobAsync();

            var reply = await AskAsync(bob, "cfg-9",
                                       ConfigureIq("cfg-9", "set",
                                                   ConfigForm("<field var='pubsub#access_model'><value>closed</value></field>")));

            Assert.That(reply.Attr("type"), Is.EqualTo("error"));

            var loaded = await AskAsync(bob, "cfg-10", ConfigureIq("cfg-10", "get"));

            Assert.That(ConfigField(loaded, "pubsub#access_model"), Is.EqualTo("open"),
                        "A refused setting must not have changed anything.");

        }

        #endregion

        #region WithPresenceAccess_AStranger_GetsNothingAndCannotSubscribe()

        /// <summary>
        /// XEP-0060, sections 6.5.3 and 6.1.3.4: <c>presence</c> means that
        /// only those get to the node who may see the presence of the owner.
        /// </summary>
        /// <remarks>
        /// Until K8 the access model was stored and without effect - exactly
        /// the sort of promise this series otherwise argues against. An owner
        /// who sets <c>presence</c> and gets <c>open</c> believes their items
        /// protected and has published them.
        /// </remarks>
        [Test]
        public async Task WithPresenceAccess_AStranger_GetsNothingAndCannotSubscribe()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-20",
                           ConfigureIq("cfg-20", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>presence</value></field>")));

            var alice = await ConnectClientAsync("alice");

            var fetched = await AskAsync(alice, "get-20",
                                         $"<iq type='get' to='bob@{Server.Domain}' id='get-20'>" +
                                         $"<pubsub xmlns='{PubSubNamespace}'><items node='{Node}'/></pubsub></iq>");

            var subscribed = await AskAsync(alice, "sub-40",
                                            PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                    alice.BareJid, "sub-40"));

            Assert.Multiple(() =>
            {

                Assert.That(fetched.Attr("type"),       Is.EqualTo("error"));
                Assert.That(ConditionOf(fetched),       Is.EqualTo("not-authorized"));
                Assert.That(ErrorTypeOf(fetched),       Is.EqualTo("auth"));
                Assert.That(PubSubConditionOf(fetched), Is.EqualTo("presence-subscription-required"));

                Assert.That(subscribed.Attr("type"),    Is.EqualTo("error"));
                Assert.That(ConditionOf(subscribed),    Is.EqualTo("not-authorized"));

            });

        }

        #endregion

        #region WithPresenceAccess_AContactStillGetsIn()

        /// <summary>
        /// The cross-check: whoever may see the presence gets to the node.
        /// </summary>
        /// <remarks>
        /// Without it the previous test would pass against a server that
        /// simply refuses everybody on <c>presence</c> - and an access model
        /// would have become a lock without a key.
        /// </remarks>
        [Test]
        public async Task WithPresenceAccess_AContactStillGetsIn()
        {

            MakeContacts("alice", "bob");

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-21",
                           ConfigureIq("cfg-21", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>presence</value></field>")));

            var alice = await ConnectClientAsync("alice");

            var fetched = await AskAsync(alice, "get-21",
                                         $"<iq type='get' to='bob@{Server.Domain}' id='get-21'>" +
                                         $"<pubsub xmlns='{PubSubNamespace}'><items node='{Node}'/></pubsub></iq>");

            Assert.That(fetched.Attr("type"), Is.EqualTo("result"));

            var subscribed = await AskAsync(alice, "sub-41",
                                            PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                    alice.BareJid, "sub-41"));

            Assert.That(subscribed.Attr("type"), Is.EqualTo("result"));

        }

        #endregion

        #region TheOwner_ReachesHisOwnNode()

        /// <summary>
        /// The owner gets to their own node, with <c>presence</c> as well.
        /// </summary>
        /// <remarks>
        /// At their own place they are no presence subscriber. A model that
        /// locked them out of their own node would not deserve the name - and
        /// the mistake would show only when a client can no longer read its own
        /// device list.
        /// </remarks>
        [Test]
        public async Task TheOwner_ReachesHisOwnNode()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-22",
                           ConfigureIq("cfg-22", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>presence</value></field>")));

            var reply = await AskAsync(bob, "get-22",
                                       $"<iq type='get' id='get-22'>" +
                                       $"<pubsub xmlns='{PubSubNamespace}'><items node='{Node}'/></pubsub></iq>");

            Assert.That(reply.Attr("type"), Is.EqualTo("result"));

        }

        #endregion

        #region PublishOptions_CreateTheNodeAsDemanded()

        /// <summary>
        /// XEP-0060, section 7.1.5: the node comes about with the demanded
        /// properties.
        /// </summary>
        [Test]
        public async Task PublishOptions_CreateTheNodeAsDemanded()
        {

            var bob = await ConnectClientAsync("bob");

            await AskAsync(bob, "pub-40",
                           $"<iq type='set' id='pub-40'><pubsub xmlns='{PubSubNamespace}'>" +
                           "<publish node='urn:example:narrow'><item id='40'>" +
                           "<w xmlns='urn:example:x'>a</w></item></publish>" +
                           "<publish-options>" +
                           PublishOptionsForm("<field var='pubsub#access_model'><value>presence</value></field>") +
                           "</publish-options></pubsub></iq>");

            var loaded = await AskAsync(bob, "cfg-23",
                                        ConfigureIq("cfg-23", "get", node: "urn:example:narrow"));

            Assert.That(ConfigField(loaded, "pubsub#access_model"), Is.EqualTo("presence"));

        }

        #endregion

        #region PublishOptions_ThatTheNodeDoesNotMeet_StopThePublication()

        /// <summary>
        /// XEP-0060, section 7.1.5: if the node does not fit, nothing is
        /// published.
        /// </summary>
        /// <remarks>
        /// <b>And not published means: not at all.</b> A service that refused
        /// the condition and stored the item all the same would have done the
        /// opposite of what conditions are there for - the sender would assume
        /// their item does not lie where it now does lie after all.
        /// </remarks>
        [Test]
        public async Task PublishOptions_ThatTheNodeDoesNotMeet_StopThePublication()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-24",
                           ConfigureIq("cfg-24", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>presence</value></field>")));

            var reply = await AskAsync(bob, "pub-41",
                                       $"<iq type='set' id='pub-41'><pubsub xmlns='{PubSubNamespace}'>" +
                                       $"<publish node='{Node}'><item id='41'>" +
                                       "<w xmlns='urn:example:x'>b</w></item></publish>" +
                                       "<publish-options>" +
                                       PublishOptionsForm("<field var='pubsub#access_model'><value>open</value></field>") +
                                       "</publish-options></pubsub></iq>");

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"),       Is.EqualTo("error"));
                Assert.That(ConditionOf(reply),       Is.EqualTo("conflict"));
                Assert.That(PubSubConditionOf(reply), Is.EqualTo("precondition-not-met"));
            });

            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            Assert.Multiple(() =>
            {

                Assert.That(account.GetPepItems(Node).Select(e => e.ItemId), Does.Not.Contain("41"),
                            "A refused publication must not have stored anything.");

                Assert.That(account.PepNodeConfiguration(Node)!.AccessModel,
                            Is.EqualTo(PubSubAccessModel.Presence),
                            "And it must not have reconfigured the node.");

            });

        }

        #endregion

        #region PublishOptions_ThatFit_GoThrough()

        /// <summary>
        /// The cross-check: fitting conditions hold nothing up.
        /// </summary>
        [Test]
        public async Task PublishOptions_ThatFit_GoThrough()
        {

            var bob = await PublishingBobAsync();

            var reply = await AskAsync(bob, "pub-42",
                                       $"<iq type='set' id='pub-42'><pubsub xmlns='{PubSubNamespace}'>" +
                                       $"<publish node='{Node}'><item id='42'>" +
                                       "<w xmlns='urn:example:x'>c</w></item></publish>" +
                                       "<publish-options>" +
                                       PublishOptionsForm("<field var='pubsub#access_model'><value>open</value></field>") +
                                       "</publish-options></pubsub></iq>");

            Assert.That(reply.Attr("type"), Is.EqualTo("result"));

            Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.GetPepItems(Node).Select(e => e.ItemId),
                        Does.Contain("42"));

        }

        #endregion

        #region AConditionNobodyNamed_IsNoCondition()

        /// <summary>
        /// What does not stand in the condition form is not demanded.
        /// </summary>
        /// <remarks>
        /// The difference between a condition and a setting, and it lies
        /// exactly in this <c>null</c>: it means "this is not being asked
        /// about" and not "default". Whoever confuses the two refuses a
        /// publication because the node differs from the default in a point the
        /// sender never said anything about.
        /// </remarks>
        [Test]
        public async Task AConditionNobodyNamed_IsNoCondition()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-25",
                           ConfigureIq("cfg-25", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>presence</value></field>")));

            var reply = await AskAsync(bob, "pub-44",
                                       $"<iq type='set' id='pub-44'><pubsub xmlns='{PubSubNamespace}'>" +
                                       $"<publish node='{Node}'><item id='44'>" +
                                       "<w xmlns='urn:example:x'>e</w></item></publish>" +
                                       "<publish-options>" +
                                       PublishOptionsForm("<field var='pubsub#max_items'><value>256</value></field>") +
                                       "</publish-options></pubsub></iq>");

            Assert.That(reply.Attr("type"), Is.EqualTo("result"),
                        "About the access model nobody demanded anything here.");

            Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.GetPepItems(Node).Select(e => e.ItemId),
                        Does.Contain("44"));

        }

        #endregion

        #region TheOmemoBundleNode_IsOpen_BecauseOmemoDemandsIt()

        /// <summary>
        /// And with that the condition OMEMO has been sending along since D66
        /// has an effect for the first time.
        /// </summary>
        /// <remarks>
        /// XEP-0384, section 5.2 demands an open access model: whoever wants to
        /// write encrypted has to be able to read the bundle, and in case of
        /// doubt that is somebody who stands in no roster yet. Until K8 nobody
        /// read this condition - the client demanded an open node, got a
        /// <c>result</c> and was entitled to assume its bundle could be
        /// fetched.
        /// </remarks>
        [Test]
        public async Task TheOmemoBundleNode_IsOpen_BecauseOmemoDemandsIt()
        {

            var bob = await ConnectClientAsync("bob");

            await AskAsync(bob, "omemo-1",
                           OmemoPep.PublishIq("omemo-1",
                                              OmemoPep.BundlesNode,
                                              "31415",
                                              XElement.Parse("<bundle xmlns='urn:xmpp:omemo:2'/>")));

            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            Assert.That(account.PepNodeConfiguration(OmemoPep.BundlesNode)!.AccessModel,
                        Is.EqualTo(PubSubAccessModel.Open));

        }

        #endregion

        #region APublishOptionNobodyOffered_IsRejected()

        /// <summary>
        /// A condition this service can promise nothing about is refused.
        /// </summary>
        /// <remarks>
        /// Leniency would be wrong precisely here: <b>a condition that is
        /// passed over is one the sender holds to be met.</b>
        /// </remarks>
        [Test]
        public async Task APublishOptionNobodyOffered_IsRejected()
        {

            var bob = await PublishingBobAsync();

            var reply = await AskAsync(bob, "pub-43",
                                       $"<iq type='set' id='pub-43'><pubsub xmlns='{PubSubNamespace}'>" +
                                       $"<publish node='{Node}'><item id='43'>" +
                                       "<w xmlns='urn:example:x'>d</w></item></publish>" +
                                       "<publish-options>" +
                                       PublishOptionsForm("<field var='pubsub#roster_groups_allowed'><value>friends</value></field>") +
                                       "</publish-options></pubsub></iq>");

            Assert.That(reply.Attr("type"), Is.EqualTo("error"));

            Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.GetPepItems(Node).Select(e => e.ItemId),
                        Does.Not.Contain("43"));

        }

        #endregion

        #region TheSubscriptionList_NamesEveryNodeAndSubId()

        /// <summary>
        /// XEP-0060, section 5.6: one request, and all the own subscriptions
        /// stand there.
        /// </summary>
        /// <remarks>
        /// This is the question a client cannot answer for itself: its books
        /// stand in memory, the subscriptions stand at the account.
        /// </remarks>
        [Test]
        public async Task TheSubscriptionList_NamesEveryNodeAndSubId()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "new-10",
                           PubSubBuilder.CreateNode($"bob@{Server.Domain}", "urn:example:second", "new-10"));

            var alice   = await ConnectClientAsync("alice");
            var firstId = await SubscribeAsync(alice, "sub-50");

            var second  = await AskAsync(alice, "sub-51",
                                         PubSubBuilder.Subscribe($"bob@{Server.Domain}", "urn:example:second",
                                                                 alice.BareJid, "sub-51"));

            var secondId = SubscriptionOf(second)?.Attr("subid");

            var list = await AskAsync(alice, "list-1", SubscriptionsIq("list-1"));

            var entries = SubscriptionsIn(list);

            Assert.Multiple(() =>
            {

                Assert.That(list.Attr("type"), Is.EqualTo("result"));

                Assert.That(entries.Select(e => (e.Attr("node"), e.Attr("subid"))),
                            Is.EquivalentTo(new[] { (Node, firstId), ("urn:example:second", secondId) }));

                Assert.That(entries.Select(e => e.Attr("jid")).Distinct(),
                            Is.EqualTo(new[] { alice.BareJid }));

                Assert.That(entries.Select(e => e.Attr("subscription")).Distinct(),
                            Is.EqualTo(new[] { "subscribed" }));

            });

        }

        #endregion

        #region TheSubscriptionList_ShowsOnlyMyOwn()

        /// <summary>
        /// Foreign subscriptions nobody enumerates.
        /// </summary>
        /// <remarks>
        /// <b>This is information about human beings and not about nodes.</b>
        /// Whoever got it would learn who is interested in what - and Carol
        /// would have told nobody anything.
        /// </remarks>
        [Test]
        public async Task TheSubscriptionList_ShowsOnlyMyOwn()
        {

            await PublishingBobAsync();

            var carol = await ConnectClientAsync("carol");
            await SubscribeAsync(carol, "sub-52");

            var alice = await ConnectClientAsync("alice");
            await SubscribeAsync(alice, "sub-53");

            var list = await AskAsync(alice, "list-2", SubscriptionsIq("list-2"));

            Assert.That(SubscriptionsIn(list).Select(e => e.Attr("jid")),
                        Is.EqualTo(new[] { alice.BareJid }),
                        "Foreign subscriptions stand in the list.");

        }

        #endregion

        #region TheSubscriptionList_CanBeScopedToOneNode()

        /// <summary>
        /// XEP-0060, section 5.6: with <c>node</c> only its subscriptions.
        /// </summary>
        [Test]
        public async Task TheSubscriptionList_CanBeScopedToOneNode()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "new-11",
                           PubSubBuilder.CreateNode($"bob@{Server.Domain}", "urn:example:second", "new-11"));

            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-54");

            await AskAsync(alice, "sub-55",
                           PubSubBuilder.Subscribe($"bob@{Server.Domain}", "urn:example:second",
                                                   alice.BareJid, "sub-55"));

            var list = await AskAsync(alice, "list-3", SubscriptionsIq("list-3", "urn:example:second"));

            Assert.That(SubscriptionsIn(list).Select(e => e.Attr("node")),
                        Is.EqualTo(new[] { "urn:example:second" }));

        }

        #endregion

        #region TwoSubscriptionsOnOneNode_AppearTwice()

        /// <summary>
        /// And with that the bind from K3 becomes resolvable: both ids stand
        /// in the list.
        /// </summary>
        /// <remarks>
        /// Whoever has subscribed twice after a connection break could until
        /// now end none of them - the service demands an id when there are
        /// several, and the client knew none any more. Here they stand.
        /// </remarks>
        [Test]
        public async Task TwoSubscriptionsOnOneNode_AppearTwice()
        {

            await PublishingBobAsync();

            var alice  = await ConnectClientAsync("alice");

            var first  = await SubscribeAsync(alice, "sub-56");
            var second = await SubscribeAsync(alice, "sub-57");

            var list = await AskAsync(alice, "list-4", SubscriptionsIq("list-4"));

            Assert.That(SubscriptionsIn(list).Select(e => e.Attr("subid")),
                        Is.EquivalentTo(new[] { first, second }));

        }

        #endregion

        #region WithoutAnySubscription_TheListIsEmptyAndNoError()

        /// <summary>
        /// No subscriptions are an empty list and no error.
        /// </summary>
        /// <remarks>
        /// The question was answerable, the answer reads "none". An error would
        /// mean something else - namely that the question could not be put, and
        /// a client would afterwards have to guess what it was down to.
        /// </remarks>
        [Test]
        public async Task WithoutAnySubscription_TheListIsEmptyAndNoError()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            var list = await AskAsync(alice, "list-5", SubscriptionsIq("list-5"));

            Assert.Multiple(() =>
            {
                Assert.That(list.Attr("type"), Is.EqualTo("result"));
                Assert.That(SubscriptionsIn(list), Is.Empty);
            });

        }

        #endregion

        #region TheOwner_IsTheAccountAndCannotBeChanged()

        /// <summary>
        /// XEP-0060, section 8.9: the owner stands in the list without
        /// anybody having entered them - and cannot be moved out.
        /// </summary>
        /// <remarks>
        /// A PEP node belongs to the human being in whose account it stands.
        /// Whoever could change the owner could take away somebody else's own
        /// account.
        /// </remarks>
        [Test]
        public async Task TheOwner_IsTheAccountAndCannotBeChanged()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var list = await AskAsync(bob, "aff-1", AffiliationsIq("aff-1", "get"));

            Assert.That(AffiliationsIn(list).Select(e => (e.Attr("jid"), e.Attr("affiliation"))),
                        Is.EqualTo(new[] { ($"bob@{Server.Domain}", "owner") }));

            var refused = await AskAsync(bob, "aff-2",
                                         AffiliationsIq("aff-2", "set",
                                                        $"<affiliation jid='{alice.BareJid}' affiliation='owner'/>"));

            var himself = await AskAsync(bob, "aff-3",
                                         AffiliationsIq("aff-3", "set",
                                                        $"<affiliation jid='bob@{Server.Domain}' affiliation='member'/>"));

            Assert.Multiple(() =>
            {
                Assert.That(ConditionOf(refused), Is.EqualTo("not-allowed"),
                            "A second owner does not exist.");
                Assert.That(ConditionOf(himself), Is.EqualTo("not-allowed"),
                            "And the owner cannot demote themselves.");
            });

        }

        #endregion

        #region TheAccountApi_RefusesToMoveTheOwnership()

        /// <summary>
        /// Below the protocol as well: the ownership cannot be set.
        /// </summary>
        /// <remarks>
        /// The server already refuses it before it gets here - this check is no
        /// duplicate all the same, but the promise of a public method. One that
        /// silently changed the owner would be a trap for the next caller.
        /// </remarks>
        [Test]
        public async Task TheAccountApi_RefusesToMoveTheOwnership()
        {

            await PublishingBobAsync();

            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            Assert.Multiple(() =>
            {

                Assert.That(account.SetPepAffiliation(Node, $"alice@{Server.Domain}", PubSubAffiliation.Owner),
                            Is.False,
                            "A second owner does not exist.");

                Assert.That(account.SetPepAffiliation(Node, $"bob@{Server.Domain}", PubSubAffiliation.Member),
                            Is.False,
                            "And the owner cannot be demoted.");

                Assert.That(account.PepAffiliationOf(Node, $"bob@{Server.Domain}"),
                            Is.EqualTo(PubSubAffiliation.Owner));

                Assert.That(account.PepAffiliationOf(Node, $"alice@{Server.Domain}"),
                            Is.EqualTo(PubSubAffiliation.None));

            });

        }

        #endregion

        #region AffiliationsOfANode_AreTheOwnersBusiness()

        /// <summary>
        /// Who is what at a node concerns the owner alone.
        /// </summary>
        [Test]
        public async Task AffiliationsOfANode_AreTheOwnersBusiness()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            var reply = await AskAsync(alice, "aff-4", AffiliationsIq("aff-4", "get"));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"), Is.EqualTo("error"));
                Assert.That(ConditionOf(reply), Is.EqualTo("forbidden"));
            });

        }

        #endregion

        #region APublisher_MayPublishIntoAForeignNode()

        /// <summary>
        /// XEP-0060, section 4.1: a <c>publisher</c> may write into a foreign
        /// node - and the event comes from the owner all the same.
        /// </summary>
        /// <remarks>
        /// The second part is the important one. If it came from the writer, it
        /// would be a false statement about the origin - and the spoofing
        /// protection of the receiver would be right to throw it away.
        /// </remarks>
        [Test]
        public async Task APublisher_MayPublishIntoAForeignNode()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await AskAsync(bob, "aff-5",
                           AffiliationsIq("aff-5", "set",
                                          $"<affiliation jid='{alice.BareJid}' affiliation='publisher'/>"));

            var carol = await ConnectClientAsync("carol");
            await SubscribeAsync(carol, "sub-60");

            var events = CollectEvents(carol);

            var reply = await AskAsync(alice, "pub-50",
                                       $"<iq type='set' to='bob@{Server.Domain}' id='pub-50'>" +
                                       $"<pubsub xmlns='{PubSubNamespace}'>" +
                                       $"<publish node='{Node}'><item id='50'>" +
                                       "<w xmlns='urn:example:x'>from Alice</w></item></publish>" +
                                       "</pubsub></iq>");

            Assert.That(reply.Attr("type"), Is.EqualTo("result"));

            Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.GetPepItems(Node).Select(e => e.ItemId),
                        Does.Contain("50"),
                        "The item belongs in Bob's node and not in Alice's.");

            await WaitFor(() => Count(events) > 0, "the notification to the subscriber");

            Assert.That(events[0], Does.Contain($"from='bob@{Server.Domain}'"),
                        "The event comes from the owner of the node.");

        }

        #endregion

        #region WithoutTheRole_PublishingIntoAForeignNodeStaysForbidden()

        /// <summary>
        /// The cross-check: without the role the refusal stands.
        /// </summary>
        /// <remarks>
        /// Without it the previous test would only check that somebody may
        /// write at all - and the check the OMEMO signature stands against
        /// would have fallen away in silence.
        /// </remarks>
        [Test]
        public async Task WithoutTheRole_PublishingIntoAForeignNodeStaysForbidden()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            var reply = await AskAsync(alice, "pub-51",
                                       $"<iq type='set' to='bob@{Server.Domain}' id='pub-51'>" +
                                       $"<pubsub xmlns='{PubSubNamespace}'>" +
                                       $"<publish node='{Node}'><item id='51'>" +
                                       "<w xmlns='urn:example:x'>forged</w></item></publish>" +
                                       "</pubsub></iq>");

            Assert.Multiple(() =>
            {
                Assert.That(ConditionOf(reply), Is.EqualTo("forbidden"));
                Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.GetPepItems(Node).Select(e => e.ItemId),
                            Does.Not.Contain("51"));
            });

        }

        #endregion

        #region APublisher_MayNotConfigureTheNode()

        /// <summary>
        /// Being allowed to write does not mean being allowed to decide.
        /// </summary>
        [Test]
        public async Task APublisher_MayNotConfigureTheNode()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await AskAsync(bob, "aff-6",
                           AffiliationsIq("aff-6", "set",
                                          $"<affiliation jid='{alice.BareJid}' affiliation='publisher'/>"));

            var reply = await AskAsync(alice, "cfg-30",
                                       ConfigureIq("cfg-30", "set",
                                                   ConfigForm("<field var='pubsub#persist_items'><value>0</value></field>")));

            Assert.That(ConditionOf(reply), Is.EqualTo("forbidden"));

        }

        #endregion

        #region ARole_BelongsToANodeAndNotToAnAccount()

        /// <summary>
        /// Whoever may write at one node may not do it everywhere.
        /// </summary>
        /// <remarks>
        /// <b>The test was first called "a publisher cannot create nodes" and
        /// checked something that does not exist at all:</b> at a node that
        /// does not exist nobody has a role - the refusal already comes from
        /// the role check. The existence check written expressly for it was
        /// thereby unreachable and is out again.
        ///
        /// What is left is the rule behind it, and that is checkable: a role
        /// belongs to a node and not to an account. Otherwise a write
        /// permission granted once would be a write permission on everything -
        /// on the OMEMO node as well, where the signature otherwise stands.
        /// </remarks>
        [Test]
        public async Task ARole_BelongsToANodeAndNotToAnAccount()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await AskAsync(bob, "new-21",
                           PubSubBuilder.CreateNode($"bob@{Server.Domain}", "urn:example:second", "new-21"));

            await AskAsync(bob, "aff-7",
                           AffiliationsIq("aff-7", "set",
                                          $"<affiliation jid='{alice.BareJid}' affiliation='publisher'/>"));

            var reply = await AskAsync(alice, "pub-52",
                                       $"<iq type='set' to='bob@{Server.Domain}' id='pub-52'>" +
                                       $"<pubsub xmlns='{PubSubNamespace}'>" +
                                       "<publish node='urn:example:second'><item id='52'>" +
                                       "<w xmlns='urn:example:x'>a</w></item></publish>" +
                                       "</pubsub></iq>");

            Assert.Multiple(() =>
            {
                Assert.That(ConditionOf(reply), Is.EqualTo("forbidden"));
                Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.GetPepItems("urn:example:second"),
                            Is.Empty);
            });

        }

        #endregion

        #region AnOutcast_IsLockedOutAndLosesHisSubscription()

        /// <summary>
        /// XEP-0060, sections 6.1.3.8 and 8.9.4: locked out means locked out -
        /// and existing subscriptions end.
        /// </summary>
        /// <remarks>
        /// To hinder them only at new ones would mean making the lockout depend
        /// on the coincidence of whether they were there before.
        ///
        /// The refusal is another one than with the access model:
        /// <c>&lt;forbidden/&gt;</c> instead of <c>&lt;not-authorized/&gt;</c>.
        /// The latter names the way in with the presence request - for somebody
        /// locked out there would be none, and to send them down that road
        /// would be false information.
        /// </remarks>
        [Test]
        public async Task AnOutcast_IsLockedOutAndLosesHisSubscription()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-61");

            await AskAsync(bob, "aff-8",
                           AffiliationsIq("aff-8", "set",
                                          $"<affiliation jid='{alice.BareJid}' affiliation='outcast'/>"));

            var fetched = await AskAsync(alice, "get-30",
                                         $"<iq type='get' to='bob@{Server.Domain}' id='get-30'>" +
                                         $"<pubsub xmlns='{PubSubNamespace}'><items node='{Node}'/></pubsub></iq>");

            var subscribed = await AskAsync(alice, "sub-62",
                                            PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                    alice.BareJid, "sub-62"));

            Assert.Multiple(() =>
            {

                Assert.That(ConditionOf(fetched),    Is.EqualTo("forbidden"));
                Assert.That(ConditionOf(subscribed), Is.EqualTo("forbidden"));

                Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.PepSubscriptions(Node), Is.Empty,
                            "The existing subscription should have ended.");

            });

        }

        #endregion

        #region AnUnknownRole_IsRejectedAndChangesNothing()

        /// <summary>
        /// A role this service does not know is refused.
        /// </summary>
        /// <remarks>
        /// <b>Leniency would be especially expensive here:</b> whoever wants to
        /// lock somebody out and mistypes would otherwise get a <c>result</c>
        /// and hold the lockout to be done.
        ///
        /// And everything is checked before anything holds: a request that
        /// takes effect by half would be worse than one refused entirely - the
        /// sender would not know which half.
        /// </remarks>
        [Test]
        public async Task AnUnknownRole_IsRejectedAndChangesNothing()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var reply = await AskAsync(bob, "aff-9",
                                       AffiliationsIq("aff-9", "set",
                                                      $"<affiliation jid='{alice.BareJid}' affiliation='publisher'/>" +
                                                      $"<affiliation jid='carol@{Server.Domain}' affiliation='publish-only'/>"));

            Assert.That(ConditionOf(reply), Is.EqualTo("bad-request"));

            var list = await AskAsync(bob, "aff-10", AffiliationsIq("aff-10", "get"));

            Assert.That(AffiliationsIn(list).Select(e => e.Attr("jid")),
                        Is.EqualTo(new[] { $"bob@{Server.Domain}" }),
                        "The valid half must not have taken effect either.");

        }

        #endregion

        #region TakingTheRoleBack_EndsThePermission()

        /// <summary>
        /// <c>none</c> takes the role back - and with it what it allowed.
        /// </summary>
        [Test]
        public async Task TakingTheRoleBack_EndsThePermission()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await AskAsync(bob, "aff-11",
                           AffiliationsIq("aff-11", "set",
                                          $"<affiliation jid='{alice.BareJid}' affiliation='publisher'/>"));

            await AskAsync(bob, "aff-12",
                           AffiliationsIq("aff-12", "set",
                                          $"<affiliation jid='{alice.BareJid}' affiliation='none'/>"));

            var reply = await AskAsync(alice, "pub-53",
                                       $"<iq type='set' to='bob@{Server.Domain}' id='pub-53'>" +
                                       $"<pubsub xmlns='{PubSubNamespace}'>" +
                                       $"<publish node='{Node}'><item id='53'>" +
                                       "<w xmlns='urn:example:x'>too late</w></item></publish>" +
                                       "</pubsub></iq>");

            Assert.That(ConditionOf(reply), Is.EqualTo("forbidden"));

            var list = await AskAsync(bob, "aff-13", AffiliationsIq("aff-13", "get"));

            Assert.That(AffiliationsIn(list).Select(e => e.Attr("jid")),
                        Is.EqualTo(new[] { $"bob@{Server.Domain}" }),
                        "A role taken back does not stand in the list any more.");

        }

        #endregion

        #region MyOwnAffiliations_AreListedAcrossNodes()

        /// <summary>
        /// XEP-0060, section 5.7: what am I where?
        /// </summary>
        /// <remarks>
        /// As with the subscriptions: the roles of the one asking, never those
        /// of another. Whoever were allowed to enumerate foreign ones would
        /// learn who may do what where.
        /// </remarks>
        [Test]
        public async Task MyOwnAffiliations_AreListedAcrossNodes()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "new-20",
                           PubSubBuilder.CreateNode($"bob@{Server.Domain}", "urn:example:second", "new-20"));

            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            await AskAsync(bob, "aff-14",
                           AffiliationsIq("aff-14", "set",
                                          $"<affiliation jid='{alice.BareJid}' affiliation='publisher'/>"));

            await AskAsync(bob, "aff-15",
                           AffiliationsIq("aff-15", "set", node: "urn:example:second",
                                          content: $"<affiliation jid='{carol.BareJid}' affiliation='member'/>"));

            var mine = await AskAsync(alice, "own-1", OwnAffiliationsIq("own-1"));

            Assert.That(AffiliationsIn(mine, PubSubNamespace)
                            .Select(e => (e.Attr("node"), e.Attr("affiliation"))),
                        Is.EqualTo(new[] { (Node, "publisher") }),
                        "Carol's role is none of Alice's business.");

            var bobs = await AskAsync(bob, "own-2", OwnAffiliationsIq("own-2"));

            Assert.That(AffiliationsIn(bobs, PubSubNamespace).Select(e => e.Attr("affiliation")).Distinct(),
                        Is.EqualTo(new[] { "owner" }),
                        "All their nodes belong to the owner.");

        }

        #endregion

        #region OnAWhitelistedNode_OnlyTheListGetsIn()

        /// <summary>
        /// XEP-0060, section 4.5: <c>whitelist</c> - and with that
        /// <c>member</c> decides something for the first time.
        /// </summary>
        /// <remarks>
        /// <b>The strictest of the three models and the only one where the
        /// roster decides nothing.</b> Presence permission comes about in
        /// passing - somebody takes up a contact, and already they see more. A
        /// list does not come about in passing.
        /// </remarks>
        [Test]
        public async Task OnAWhitelistedNode_OnlyTheListGetsIn()
        {

            // Carol is a contact and would be in on 'presence' - here not.
            MakeContacts("carol", "bob");

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            await AskAsync(bob, "aff-20",
                           AffiliationsIq("aff-20", "set",
                                          $"<affiliation jid='{alice.BareJid}' affiliation='member'/>"));

            await AskAsync(bob, "cfg-40",
                           ConfigureIq("cfg-40", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>whitelist</value></field>")));

            var loaded = await AskAsync(bob, "cfg-40b", ConfigureIq("cfg-40b", "get"));

            Assert.That(ConfigField(loaded, "pubsub#access_model"), Is.EqualTo("whitelist"),
                        "The form has to name the model by its name - otherwise the owner " +
                        "would hold the node to be open and leave it closed, or the other way round.");

            var member = await AskAsync(alice, "get-40",
                                        $"<iq type='get' to='bob@{Server.Domain}' id='get-40'>" +
                                        $"<pubsub xmlns='{PubSubNamespace}'><items node='{Node}'/></pubsub></iq>");

            var contact = await AskAsync(carol, "get-41",
                                         $"<iq type='get' to='bob@{Server.Domain}' id='get-41'>" +
                                         $"<pubsub xmlns='{PubSubNamespace}'><items node='{Node}'/></pubsub></iq>");

            var own = await AskAsync(bob, "get-42",
                                     $"<iq type='get' id='get-42'>" +
                                     $"<pubsub xmlns='{PubSubNamespace}'><items node='{Node}'/></pubsub></iq>");

            Assert.Multiple(() =>
            {

                Assert.That(member.Attr("type"), Is.EqualTo("result"),
                            "Whoever stands on the list gets in.");

                Assert.That(contact.Attr("type"), Is.EqualTo("error"),
                            "A contact does not stand on the list for that reason.");
                Assert.That(ConditionOf(contact),  Is.EqualTo("not-authorized"));

                Assert.That(own.Attr("type"), Is.EqualTo("result"),
                            "The owner stands on no list and gets to their node all the same.");

            });

        }

        #endregion

        #region OnAWhitelistedNode_AMemberMaySubscribe()

        /// <summary>
        /// And the same with the subscribing.
        /// </summary>
        /// <remarks>
        /// Both ways belong checked: a model that holds only on fetching could
        /// be got around with a subscription - the one locked out would have
        /// the items delivered instead of fetching them.
        /// </remarks>
        [Test]
        public async Task OnAWhitelistedNode_AMemberMaySubscribe()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            await AskAsync(bob, "aff-21",
                           AffiliationsIq("aff-21", "set",
                                          $"<affiliation jid='{alice.BareJid}' affiliation='member'/>"));

            await AskAsync(bob, "cfg-41",
                           ConfigureIq("cfg-41", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>whitelist</value></field>")));

            var member   = await AskAsync(alice, "sub-70",
                                          PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                  alice.BareJid, "sub-70"));

            var stranger = await AskAsync(carol, "sub-71",
                                          PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                  carol.BareJid, "sub-71"));

            Assert.Multiple(() =>
            {
                Assert.That(member.Attr("type"),   Is.EqualTo("result"));
                Assert.That(stranger.Attr("type"), Is.EqualTo("error"));
                Assert.That(ConditionOf(stranger), Is.EqualTo("not-authorized"));
            });

        }

        #endregion

        #region APublisher_IsOnTheListToo()

        /// <summary>
        /// Whoever may write may also read.
        /// </summary>
        /// <remarks>
        /// Anything else would be a role one can use only together with a
        /// second one - and the owner would have to remember, for every
        /// publisher, to put them on the list as well.
        /// </remarks>
        [Test]
        public async Task APublisher_IsOnTheListToo()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await AskAsync(bob, "aff-22",
                           AffiliationsIq("aff-22", "set",
                                          $"<affiliation jid='{alice.BareJid}' affiliation='publisher'/>"));

            await AskAsync(bob, "cfg-42",
                           ConfigureIq("cfg-42", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>whitelist</value></field>")));

            var reply = await AskAsync(alice, "get-43",
                                       $"<iq type='get' to='bob@{Server.Domain}' id='get-43'>" +
                                       $"<pubsub xmlns='{PubSubNamespace}'><items node='{Node}'/></pubsub></iq>");

            Assert.That(reply.Attr("type"), Is.EqualTo("result"));

        }

        #endregion

        #region AnOutcast_StaysOutOfAnOpenNodeToo()

        /// <summary>
        /// And the lockout stands above the model - above <c>whitelist</c> as
        /// well.
        /// </summary>
        /// <remarks>
        /// The access model says who may come in; the role says who stays out.
        /// Somebody locked out whom another puts on the list by mistake stays
        /// out - otherwise the lockout would depend on the order in which two
        /// instructions came.
        /// </remarks>
        [Test]
        public async Task AnOutcast_StaysOutOfAnOpenNodeToo()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await AskAsync(bob, "aff-23",
                           AffiliationsIq("aff-23", "set",
                                          $"<affiliation jid='{alice.BareJid}' affiliation='outcast'/>"));

            // The node stays open - the lockout alone has to be enough.
            var reply = await AskAsync(alice, "get-44",
                                       $"<iq type='get' to='bob@{Server.Domain}' id='get-44'>" +
                                       $"<pubsub xmlns='{PubSubNamespace}'><items node='{Node}'/></pubsub></iq>");

            Assert.Multiple(() =>
            {
                Assert.That(ConditionOf(reply), Is.EqualTo("forbidden"));
                Assert.That(Server.GetAccount($"bob@{Server.Domain}")!
                                  .PepNodeConfiguration(Node)!.AccessModel,
                            Is.EqualTo(PubSubAccessModel.Open),
                            "The node stood open - it was down to the role alone.");
            });

        }

        #endregion

        #region TheSubmittedForm_IsReadStrictly()

        /// <summary>
        /// What a submitted form says - and what it must not say.
        /// </summary>
        /// <remarks>
        /// Without a server, because this is about the reading and not about
        /// the way. The four spellings from XEP-0004, section 3.3 all stand in
        /// it: what comes in was written by somebody else, and they may choose.
        /// </remarks>
        [Test]
        public void TheSubmittedForm_IsReadStrictly()
        {

            static XElement Form(String content)
                => XElement.Parse($"<x xmlns='jabber:x:data' type='submit'>{content}</x>");

            static String Field(String value)
                => $"<field var='pubsub#deliver'><value>{value}</value></field>";

            Assert.Multiple(() =>
            {

                foreach (var (value, expected) in new[] { ("1", true), ("true", true),
                                                         ("0", false), ("false", false) })
                {
                    Assert.That(PubSubSubscriptionOptions.TryRead(Form(Field(value)), out var loaded),
                                Is.True, $"'{value}' is an allowed spelling.");
                    Assert.That(loaded!.Deliver, Is.EqualTo(expected), $"'{value}'");
                }

                Assert.That(PubSubSubscriptionOptions.TryRead(Form(Field("maybe")), out _),
                            Is.False, "Everything else is no truth value.");

                Assert.That(PubSubSubscriptionOptions.TryRead(Form(""), out var empty), Is.True);
                Assert.That(empty!.Deliver, Is.True,
                            "A missing field stands on the default.");

                // A form for another purpose - the publish-options from
                // XEP-0384, say - happens to carry no known field and would
                // otherwise pass as an empty setting.
                Assert.That(PubSubSubscriptionOptions.TryRead(
                                Form("<field var='FORM_TYPE' type='hidden'>" +
                                     "<value>http://jabber.org/protocol/pubsub#publish-options</value></field>"),
                                out _),
                            Is.False,
                            "A form for another purpose is no setting.");

            });

        }

        #endregion

        #region TurningDeliveryOff_SilencesTheSubscription()

        /// <summary>
        /// XEP-0060, Abschnitt 12.18: <c>pubsub#deliver=0</c> - das Abonnement
        /// bleibt, die Zustellung nicht.
        /// </summary>
        /// <remarks>
        /// <b>Und es fällt nicht auf die Presence-Zustellung zurück.</b> Wer
        /// gesagt hat, dass er nichts bekommen will, bekommt nichts - auch
        /// wenn er nebenbei im Roster steht. Alles andere hiesse, eine
        /// Einstellung mit einem anderen Weg zu unterlaufen.
        /// </remarks>
        [Test]
        public async Task TurningDeliveryOff_SilencesTheSubscription()
        {

            MakeContacts("alice", "bob");

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-21");

            var gesetzt = await AskAsync(alice, "opt-21",
                                         OptionsIq("opt-21", "set",
                                                   formular: SubmitForm(DeliverField("0"))));

            Assert.That(gesetzt.Attr("type"), Is.EqualTo("result"));

            var gelesen = await AskAsync(alice, "opt-21b", OptionsIq("opt-21b", "get"));

            Assert.That(FieldValue(gelesen, "pubsub#deliver"), Is.EqualTo("0"),
                        "Das Formular muss zeigen, was gilt, und nicht, was vorgesehen war.");

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "pub-20",
                           PublishIq("pub-20", Node, "20", "<wetter xmlns='urn:example:x'>still</wetter>"));

            await WaitAgainst(() => Count(ereignisse) > 0,
                              "eine Benachrichtigung an ein stillgelegtes Abonnement");

        }

        #endregion

        #region TurningDeliveryOnAgain_ResumesIt()

        /// <summary>
        /// Die Gegenprobe: Was sich abschalten lässt, lässt sich auch wieder
        /// einschalten.
        /// </summary>
        /// <remarks>
        /// Ohne sie bestünde der vorige Test auch gegen eine Umsetzung, die
        /// jede Einstellung als „nicht zustellen" liest.
        /// </remarks>
        [Test]
        public async Task TurningDeliveryOnAgain_ResumesIt()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-22");

            await AskAsync(alice, "opt-22a",
                           OptionsIq("opt-22a", "set", formular: SubmitForm(DeliverField("0"))));

            await AskAsync(alice, "opt-22b",
                           OptionsIq("opt-22b", "set", formular: SubmitForm(DeliverField("true"))));

            var gelesen = await AskAsync(alice, "opt-22c", OptionsIq("opt-22c", "get"));

            Assert.That(FieldValue(gelesen, "pubsub#deliver"), Is.EqualTo("1"),
                        "Auch 'true' ist ein Ja - XEP-0004 kennt beide Schreibweisen.");

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "pub-21",
                           PublishIq("pub-21", Node, "21", "<wetter xmlns='urn:example:x'>wieder da</wetter>"));

            await WaitFor(() => Count(ereignisse) > 0, "die wieder zugestellte Benachrichtigung");

        }

        #endregion

        #region WithTwoSubscriptions_OnlyTheConfiguredOneGoesQuiet()

        /// <summary>
        /// Der Grund, aus dem sich zwei Abonnements desselben JIDs auf
        /// denselben Knoten überhaupt unterscheiden können.
        /// </summary>
        /// <remarks>
        /// Bis hierher waren zwei Abonnements zwei gleiche Dinge, und das
        /// zweite brachte nichts ein als eine zweite Zustellung. Mit der
        /// Konfiguration je Abonnement bekommen sie verschiedene Eigenschaften
        /// - und erst damit ist die <c>subid</c> nicht nur eine Kennung,
        /// sondern die Adresse einer Einstellung.
        /// </remarks>
        [Test]
        public async Task WithTwoSubscriptions_OnlyTheConfiguredOneGoesQuiet()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var erste  = await SubscribeAsync(alice, "sub-23a");
            var zweite = await SubscribeAsync(alice, "sub-23b");

            var gesetzt = await AskAsync(alice, "opt-23",
                                         OptionsIq("opt-23", "set", erste,
                                                   SubmitForm(DeliverField("0"))));

            Assert.That(gesetzt.Attr("type"), Is.EqualTo("result"));

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "pub-22",
                           PublishIq("pub-22", Node, "22", "<wetter xmlns='urn:example:x'>halb</wetter>"));

            await WaitFor(() => Count(ereignisse) > 0, "die Benachrichtigung des lauten Abonnements");

            await WaitAgainst(() => Count(ereignisse) > 1,
                              "eine Benachrichtigung des stillgelegten Abonnements");

            Assert.That(SubIdsIn(ereignisse), Is.EqualTo(new[] { zweite }),
                        "Es wurde das falsche stillgelegt.");

        }

        #endregion

        #region Options_WithoutASubId_WhenSeveralExist_AreRejected()

        /// <summary>
        /// XEP-0060, Abschnitt 6.3.3: Auch hier muss gesagt werden, welches
        /// Abonnement gemeint ist - nur mit einem anderen Fehler als beim
        /// Abbestellen.
        /// </summary>
        /// <remarks>
        /// <c>&lt;not-acceptable/&gt;</c> statt <c>&lt;bad-request/&gt;</c>,
        /// und das ist keine Willkür des XEP: Die Anfrage <i>ist</i> in Ordnung,
        /// sie lässt sich nur in dieser Lage nicht beantworten. Eine Umsetzung,
        /// die beide Stellen gleich behandelt, hat eine davon nicht gelesen.
        /// </remarks>
        [Test]
        public async Task Options_WithoutASubId_WhenSeveralExist_AreRejected()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-24a");
            await SubscribeAsync(alice, "sub-24b");

            var antwort = await AskAsync(alice, "opt-24", OptionsIq("opt-24", "get"));

            Assert.Multiple(() =>
            {
                Assert.That(antwort.Attr("type"),       Is.EqualTo("error"));
                Assert.That(ConditionOf(antwort),       Is.EqualTo("not-acceptable"));
                Assert.That(PubSubConditionOf(antwort), Is.EqualTo("subid-required"));
            });

        }

        #endregion

        #region Options_OfANodeNobodySubscribed_AreRejected()

        /// <summary>
        /// Ohne Abonnement gibt es nichts einzustellen.
        /// </summary>
        [Test]
        public async Task Options_OfANodeNobodySubscribed_AreRejected()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            var antwort = await AskAsync(alice, "opt-25", OptionsIq("opt-25", "get"));

            Assert.Multiple(() =>
            {
                Assert.That(antwort.Attr("type"),       Is.EqualTo("error"));
                Assert.That(ConditionOf(antwort),       Is.EqualTo("unexpected-request"));
                Assert.That(PubSubConditionOf(antwort), Is.EqualTo("not-subscribed"));
            });

        }

        #endregion

        #region Options_ForSomebodyElse_AreRejected()

        /// <summary>
        /// Und auch hier darf den <c>jid</c> nur setzen, wem er gehört.
        /// </summary>
        /// <remarks>
        /// Die dritte Stelle mit derselben Prüfung, und die stillste: Wer
        /// fremde Abonnements einstellen dürfte, könnte sie lautlos
        /// abschalten. Das Abonnement bliebe stehen - es käme nur nichts mehr
        /// an, und der Betroffene fände in seiner eigenen Liste nichts
        /// Auffälliges.
        /// </remarks>
        [Test]
        public async Task Options_ForSomebodyElse_AreRejected()
        {

            var bob   = await PublishingBobAsync();
            var carol = await ConnectClientAsync("carol");
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(carol, "sub-26");

            var antwort = await AskAsync(alice, "opt-26",
                                         OptionsIq("opt-26", "set",
                                                   formular: SubmitForm(DeliverField("0")),
                                                   jid:      carol.BareJid));

            Assert.Multiple(() =>
            {
                Assert.That(antwort.Attr("type"),       Is.EqualTo("error"));
                Assert.That(PubSubConditionOf(antwort), Is.EqualTo("invalid-jid"));
            });

            var ereignisse = CollectEvents(carol);

            await AskAsync(bob, "pub-23",
                           PublishIq("pub-23", Node, "23", "<wetter xmlns='urn:example:x'>laut</wetter>"));

            await WaitFor(() => Count(ereignisse) > 0,
                          "die Benachrichtigung an Carol, die niemand abschalten durfte");

        }

        #endregion

        #region AnOptionNobodyOffered_IsRejected()

        /// <summary>
        /// Ein Feld, das im Formular nicht stand, wird abgewiesen statt
        /// übergangen.
        /// </summary>
        /// <remarks>
        /// <b>Das ist strenger als üblich und Absicht.</b> Ein Dienst, der
        /// Unbekanntes stillschweigend schluckt, lässt den Abonnenten in dem
        /// Glauben, seine Einstellung gelte - und ausbleibende Wirkung sieht
        /// aus wie ein Fehler anderswo. Lieber eine Absage, die man lesen
        /// kann.
        /// </remarks>
        [Test]
        public async Task AnOptionNobodyOffered_IsRejected()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-27");

            var antwort = await AskAsync(alice, "opt-27",
                                         OptionsIq("opt-27", "set",
                                                   formular: SubmitForm(
                                                       "<field var='pubsub#digest'><value>1</value></field>")));

            Assert.Multiple(() =>
            {
                Assert.That(antwort.Attr("type"),       Is.EqualTo("error"));
                Assert.That(ConditionOf(antwort),       Is.EqualTo("bad-request"));
                Assert.That(PubSubConditionOf(antwort), Is.EqualTo("invalid-options"));
            });

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "pub-24",
                           PublishIq("pub-24", Node, "24", "<wetter xmlns='urn:example:x'>unverändert</wetter>"));

            await WaitFor(() => Count(ereignisse) > 0,
                          "die Benachrichtigung - eine abgewiesene Einstellung ändert nichts");

        }

        #endregion

        #region ASetWithoutAForm_IsRejected()

        /// <summary>
        /// Ein <c>set</c> ohne Formular sagt nicht, was eingestellt werden
        /// soll.
        /// </summary>
        /// <remarks>
        /// Die Vorgaben einzusetzen wäre die freundliche Auslegung und die
        /// gefährliche: Aus einer unvollständigen Anfrage würde eine Änderung,
        /// die niemand verlangt hat - und sie träfe ausgerechnet den, der
        /// gerade etwas anderes eingestellt hatte.
        /// </remarks>
        [Test]
        public async Task ASetWithoutAForm_IsRejected()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-29");

            await AskAsync(alice, "opt-29a",
                           OptionsIq("opt-29a", "set", formular: SubmitForm(DeliverField("0"))));

            var antwort = await AskAsync(alice, "opt-29b", OptionsIq("opt-29b", "set"));

            Assert.Multiple(() =>
            {
                Assert.That(antwort.Attr("type"),       Is.EqualTo("error"));
                Assert.That(PubSubConditionOf(antwort), Is.EqualTo("invalid-options"));
            });

            var gelesen = await AskAsync(alice, "opt-29c", OptionsIq("opt-29c", "get"));

            Assert.That(FieldValue(gelesen, "pubsub#deliver"), Is.EqualTo("0"),
                        "Eine abgewiesene Anfrage darf nichts zurückgesetzt haben.");

        }

        #endregion

        #region AFormThatIsNotSubmitted_IsRejected()

        /// <summary>
        /// XEP-0004: Was zurückkommt, muss ein <c>submit</c> sein.
        /// </summary>
        /// <remarks>
        /// Ein zurückgeschicktes <c>form</c> ist das Angebot und keine
        /// Antwort. Es anzunehmen hiesse, den Vorschlag des Dienstes für den
        /// Willen des Abonnenten zu halten.
        /// </remarks>
        [Test]
        public async Task AFormThatIsNotSubmitted_IsRejected()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-28");

            var antwort = await AskAsync(alice, "opt-28",
                                         OptionsIq("opt-28", "set",
                                                   formular: SubmitForm(DeliverField("0"), "form")));

            Assert.Multiple(() =>
            {
                Assert.That(antwort.Attr("type"),       Is.EqualTo("error"));
                Assert.That(PubSubConditionOf(antwort), Is.EqualTo("invalid-options"));
            });

        }

        #endregion

        #region APresenceDrivenNotification_CarriesNoSubId()

        /// <summary>
        /// Wer nur über Presence benachrichtigt wird, bekommt keine Kennung -
        /// es gibt keine.
        /// </summary>
        /// <remarks>
        /// XEP-0060, Abschnitt 12.20 verlangt die Kennung, <i>wenn</i> es
        /// mehrere Abonnements gibt. Eine erfundene mitzuschicken wäre
        /// schlimmer als keine: Der Empfänger könnte danach abbestellen wollen,
        /// was nie bestellt wurde.
        /// </remarks>
        [Test]
        public async Task APresenceDrivenNotification_CarriesNoSubId()
        {

            MakeContacts("alice", "bob");

            var bob        = await PublishingBobAsync();
            var alice      = await ConnectClientAsync("alice");
            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "pub-8",
                           PublishIq("pub-8", Node, "8", "<wetter xmlns='urn:example:x'>Dunst</wetter>"));

            await WaitFor(() => Count(ereignisse) > 0, "die Benachrichtigung an den Kontakt");

            Assert.That(SubIdsIn(ereignisse), Is.Empty);

        }

        #endregion


        #region TheSubscriberList_NamesEverybodyWithHisSubId()

        /// <summary>
        /// XEP-0060, Abschnitt 8.8.1: Wer am Knoten hängt - mit Kennung, und
        /// derselbe JID mehrfach, wenn er mehrfach abonniert hat.
        /// </summary>
        /// <remarks>
        /// Die Kennung ist hier keine Zierde. Ohne sie stünde Alice zweimal
        /// gleich da, und der Eigentümer könnte das eine ihrer Abonnements
        /// nicht von dem anderen unterscheiden - also auch keines davon
        /// einzeln beenden.
        /// </remarks>
        [Test]
        public async Task TheSubscriberList_NamesEverybodyWithHisSubId()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            var erste  = await SubscribeAsync(alice, "abo-1");
            var zweite = await SubscribeAsync(alice, "abo-2");
            var dritte = await SubscribeAsync(carol, "abo-3");

            var liste = await AskAsync(bob, "subm-1", NodeSubscriptionsIq("subm-1", "get"));

            var eintraege = SubscriptionsIn(liste, OwnerNamespace);

            Assert.Multiple(() =>
            {

                Assert.That(eintraege.Select(e => (e.Attr("jid"), e.Attr("subid"))),
                            Is.EquivalentTo(new[] {
                                ($"alice@{Server.Domain}", erste),
                                ($"alice@{Server.Domain}", zweite),
                                ($"carol@{Server.Domain}", dritte)
                            }));

                Assert.That(eintraege.Select(e => e.Attr("subscription")).Distinct(),
                            Is.EqualTo(new[] { "subscribed" }),
                            "Ohne Genehmigungsverfahren ist jedes eingetragene Abonnement ein abonniertes.");

            });

        }

        #endregion

        #region TheSubscriberList_IsOnlyForTheOwner()

        /// <summary>
        /// Die Liste sagt, wer sich für Bobs Knoten interessiert - und das geht
        /// niemanden ausser Bob etwas an.
        /// </summary>
        /// <remarks>
        /// <b>Der Unterschied zu Abschnitt 5.6.</b> Dort verschweigt der Server
        /// fremde Abonnements, weil sie eine Auskunft über Menschen wären. Hier
        /// gibt er sie heraus, weil die Frage eine andere ist: nicht „wo hängt
        /// dieser Mensch überall", sondern „wer hängt an meinem Knoten". Wer
        /// veröffentlicht, muss wissen dürfen, wohin es geht.
        /// </remarks>
        [Test]
        public async Task TheSubscriberList_IsOnlyForTheOwner()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "abo-4");

            var antwort = await AskAsync(alice, "subm-2", NodeSubscriptionsIq("subm-2", "get"));

            Assert.Multiple(() =>
            {
                Assert.That(antwort.Attr("type"), Is.EqualTo("error"));
                Assert.That(ConditionOf(antwort), Is.EqualTo("forbidden"));
            });

        }

        #endregion

        #region TheSubscriberList_OfANodeThatIsNotThere_IsRejected()

        /// <summary>
        /// Ein Knoten, den es nicht gibt, hat keine leere Abonnentenliste - er
        /// hat gar keine.
        /// </summary>
        [Test]
        public async Task TheSubscriberList_OfANodeThatIsNotThere_IsRejected()
        {

            var bob = await PublishingBobAsync();

            var erfunden = await AskAsync(bob, "subm-3",
                                          NodeSubscriptionsIq("subm-3", "get", node: "urn:example:nichts"));

            var ohne     = await AskAsync(bob, "subm-4",
                                          $"<iq type='get' to='bob@{Server.Domain}' id='subm-4'>" +
                                          $"<pubsub xmlns='{OwnerNamespace}'><subscriptions/></pubsub></iq>");

            Assert.Multiple(() =>
            {
                Assert.That(ConditionOf(erfunden), Is.EqualTo("item-not-found"));
                Assert.That(ConditionOf(ohne),     Is.EqualTo("bad-request"),
                            "Ohne Knotennamen ist die Frage unvollständig und nicht unbeantwortbar.");
            });

        }

        #endregion

        #region TheOwner_RemovesASubscriber_AndTheEventsStop()

        /// <summary>
        /// XEP-0060, Abschnitt 8.8.2: <c>subscription='none'</c> beendet das
        /// Abonnement, ohne dass der Abonnent gefragt worden wäre.
        /// </summary>
        /// <remarks>
        /// Anders als der Ausschluss über <c>outcast</c>: Der sperrt auf Dauer,
        /// dies nimmt nur weg, was gerade besteht. Alice darf danach wieder
        /// abonnieren - der Eigentümer hat sie entfernt, nicht ausgeschlossen.
        /// </remarks>
        [Test]
        public async Task TheOwner_RemovesASubscriber_AndTheEventsStop()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "abo-5");

            var entfernt = await AskAsync(bob, "subm-5",
                                          NodeSubscriptionsIq("subm-5", "set",
                                                              SubscriberEntry(alice.BareJid, "none")));

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "pub-9",
                           PublishIq("pub-9", Node, "9", "<wetter xmlns='urn:example:x'>Frost</wetter>"));

            await WaitAgainst(() => Count(ereignisse) > 0,
                              "eine Benachrichtigung an den entfernten Abonnenten");

            var liste = await AskAsync(bob, "subm-6", NodeSubscriptionsIq("subm-6", "get"));

            Assert.Multiple(() =>
            {
                Assert.That(entfernt.Attr("type"),                     Is.EqualTo("result"));
                Assert.That(SubscriptionsIn(liste, OwnerNamespace),    Is.Empty);
            });

            var wieder = await SubscribeAsync(alice, "abo-6");

            Assert.That(wieder, Is.Not.Empty,
                        "Entfernt ist nicht ausgeschlossen: Alice darf wieder abonnieren.");

        }

        #endregion

        #region WithoutASubId_TheWholeSubscriberGoes()

        /// <summary>
        /// Ohne Kennung meint der Eigentümer den Menschen und nicht eines
        /// seiner Abonnements.
        /// </summary>
        /// <remarks>
        /// <b>Und das ist kein Widerspruch zu Abschnitt 6.2.3.1.</b> Dort muss
        /// der Abonnent sagen, welches er meint, weil die anderen seine bleiben
        /// sollen. Hier eines stehen zu lassen hiesse, die Anweisung zur Hälfte
        /// auszuführen - der Entfernte bekäme weiter alles.
        /// </remarks>
        [Test]
        public async Task WithoutASubId_TheWholeSubscriberGoes()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "abo-7");
            await SubscribeAsync(alice, "abo-8");

            await AskAsync(bob, "subm-7",
                           NodeSubscriptionsIq("subm-7", "set",
                                               SubscriberEntry(alice.BareJid, "none")));

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "pub-10",
                           PublishIq("pub-10", Node, "10", "<wetter xmlns='urn:example:x'>Hagel</wetter>"));

            await WaitAgainst(() => Count(ereignisse) > 0,
                              "eine Benachrichtigung an das übriggebliebene Abonnement");

            var liste = await AskAsync(bob, "subm-8", NodeSubscriptionsIq("subm-8", "get"));

            Assert.That(SubscriptionsIn(liste, OwnerNamespace), Is.Empty);

        }

        #endregion

        #region RemovingOne_LeavesTheOthers()

        /// <summary>
        /// Wer einen entfernt, entfernt einen - und nicht den Knoten leer.
        /// </summary>
        /// <remarks>
        /// Die Selbstverständlichkeit, die man prüfen muss: Der Eigentümer
        /// merkt einen zu viel entfernten Abonnenten nicht. Der Betroffene
        /// merkt es und weiss nicht, warum.
        /// </remarks>
        [Test]
        public async Task RemovingOne_LeavesTheOthers()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            await SubscribeAsync(alice, "abo-17");

            var seines = await SubscribeAsync(carol, "abo-18");

            await AskAsync(bob, "subm-21",
                           NodeSubscriptionsIq("subm-21", "set",
                                               SubscriberEntry(alice.BareJid, "none")));

            var beiCarol = CollectEvents(carol);

            await AskAsync(bob, "pub-14",
                           PublishIq("pub-14", Node, "14", "<wetter xmlns='urn:example:x'>Wind</wetter>"));

            await WaitFor(() => Count(beiCarol) > 0, "die Benachrichtigung an den anderen Abonnenten");

            var liste = await AskAsync(bob, "subm-22", NodeSubscriptionsIq("subm-22", "get"));

            Assert.That(SubscriptionsIn(liste, OwnerNamespace).Select(e => (e.Attr("jid"), e.Attr("subid"))),
                        Is.EqualTo(new[] { ($"carol@{Server.Domain}", seines) }));

        }

        #endregion

        #region WithASubId_OnlyThatOneGoes()

        /// <summary>
        /// Mit Kennung geht genau eines - das andere liefert weiter.
        /// </summary>
        [Test]
        public async Task WithASubId_OnlyThatOneGoes()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var erste  = await SubscribeAsync(alice, "abo-9");
            var zweite = await SubscribeAsync(alice, "abo-10");

            await AskAsync(bob, "subm-9",
                           NodeSubscriptionsIq("subm-9", "set",
                                               SubscriberEntry(alice.BareJid, "none", erste)));

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "pub-11",
                           PublishIq("pub-11", Node, "11", "<wetter xmlns='urn:example:x'>Nebel</wetter>"));

            await WaitFor(() => Count(ereignisse) > 0, "die Benachrichtigung an das zweite Abonnement");

            var liste = await AskAsync(bob, "subm-10", NodeSubscriptionsIq("subm-10", "get"));

            Assert.Multiple(() =>
            {

                Assert.That(SubIdsIn(ereignisse), Is.EqualTo(new[] { zweite }),
                            "Es liefert das Abonnement, das geblieben ist.");

                Assert.That(SubscriptionsIn(liste, OwnerNamespace).Select(e => e.Attr("subid")),
                            Is.EqualTo(new[] { zweite }));

            });

        }

        #endregion

        #region RemovingSomebodyWhoIsNotThere_IsRejected()

        /// <summary>
        /// Was niemand findet, wird auch nicht beendet.
        /// </summary>
        /// <remarks>
        /// Stillschweigend zuzustimmen hiesse, den Erfolg einer Anweisung zu
        /// melden, die ins Leere ging. Ein Tippfehler im JID, und der
        /// Eigentümer hielte jemanden für entfernt, der weiter alles bekommt -
        /// dieselbe Verwechslung wie überall in dieser Reihe, nur diesmal von
        /// der bequemen Seite aus.
        /// </remarks>
        [Test]
        public async Task RemovingSomebodyWhoIsNotThere_IsRejected()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var subId = await SubscribeAsync(alice, "abo-11");

            var fremd = await AskAsync(bob, "subm-11",
                                       NodeSubscriptionsIq("subm-11", "set",
                                                           SubscriberEntry($"carol@{Server.Domain}", "none")));

            var falsch = await AskAsync(bob, "subm-12",
                                        NodeSubscriptionsIq("subm-12", "set",
                                                            SubscriberEntry(alice.BareJid, "none", "gibtesnicht")));

            var liste = await AskAsync(bob, "subm-13", NodeSubscriptionsIq("subm-13", "get"));

            Assert.Multiple(() =>
            {

                Assert.That(ConditionOf(fremd),  Is.EqualTo("item-not-found"),
                            "Carol hat nie abonniert.");

                Assert.That(ConditionOf(falsch), Is.EqualTo("item-not-found"),
                            "Und diese Kennung gehört zu keinem Abonnement.");

                Assert.That(SubscriptionsIn(liste, OwnerNamespace).Select(e => e.Attr("subid")),
                            Is.EqualTo(new[] { subId }),
                            "Alices Abonnement steht unangetastet da.");

            });

        }

        #endregion

        #region TheOwner_CannotEnrolSomebody()

        /// <summary>
        /// Der Eigentümer darf wegnehmen und nicht hergeben.
        /// </summary>
        /// <remarks>
        /// XEP-0060, Abschnitt 8.8.2 lässt ihn auch anmelden; dieser Server
        /// nicht. Jemanden einzutragen, der nicht gefragt hat, ist genau das,
        /// was Abschnitt 6.1.3.1 auf der anderen Seite verhindert - und dass es
        /// der eigene Knoten ist, ändert nichts für den, dessen Postfach sich
        /// füllt.
        /// </remarks>
        [Test]
        public async Task TheOwner_CannotEnrolSomebody()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var abgewiesen = await AskAsync(bob, "subm-14",
                                            NodeSubscriptionsIq("subm-14", "set",
                                                                SubscriberEntry(alice.BareJid, "subscribed")));

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "pub-12",
                           PublishIq("pub-12", Node, "12", "<wetter xmlns='urn:example:x'>Sturm</wetter>"));

            await WaitAgainst(() => Count(ereignisse) > 0,
                              "eine Benachrichtigung an einen ungefragt Angemeldeten");

            Assert.Multiple(() =>
            {
                Assert.That(ConditionOf(abgewiesen), Is.EqualTo("not-allowed"));
                Assert.That(ErrorTypeOf(abgewiesen), Is.EqualTo("cancel"));
            });

        }

        #endregion

        #region TheListCanBeSentBackUnchanged()

        /// <summary>
        /// Was der Server als Zustand herausgibt, nimmt er auch wieder an.
        /// </summary>
        /// <remarks>
        /// Eine Liste, die sich nicht unverändert zurückschicken lässt, wäre
        /// kein Zustand, sondern ein Formular. <c>subscribed</c> für ein
        /// bestehendes Abonnement ist keine Anweisung, sondern eine Bestätigung
        /// - und ändert entsprechend nichts.
        /// </remarks>
        [Test]
        public async Task TheListCanBeSentBackUnchanged()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var subId = await SubscribeAsync(alice, "abo-12");

            var ereignisse = CollectEvents(alice);

            var zurueck = await AskAsync(bob, "subm-15",
                                         NodeSubscriptionsIq("subm-15", "set",
                                                             SubscriberEntry(alice.BareJid, "subscribed", subId)));

            await AskAsync(bob, "pub-13",
                           PublishIq("pub-13", Node, "13", "<wetter xmlns='urn:example:x'>Tau</wetter>"));

            await WaitFor(() => ItemIdsIn(ereignisse).Count > 0,
                          "die Benachrichtigung an das bestätigte Abonnement");

            Assert.Multiple(() =>
            {

                Assert.That(zurueck.Attr("type"), Is.EqualTo("result"));

                // Eine Bestätigung meldet nichts: Es hat sich nichts geändert.
                // Erst mit dem Genehmigungsvorgang aus D93 kann dasselbe
                // 'subscribed' eine Zusage sein - und dann meldet es sich.
                Assert.That(ereignisse.Any(e => e.Contains("<subscription", StringComparison.Ordinal)),
                            Is.False,
                            "Eine Bestätigung ist keine Änderung und meldet sich nicht.");

            });

        }

        #endregion

        #region AnUnknownState_IsRejectedAndChangesNothing()

        /// <summary>
        /// Eine Anweisung wird streng gelesen: Was kein Zustandsname ist,
        /// bewirkt nichts.
        /// </summary>
        /// <remarks>
        /// Die Antwort eines Dienstes wird nachsichtig gelesen - Unbekanntes
        /// gilt dort als „nicht abonniert", die sichere Annahme. Hier gerade
        /// nicht: Wäre Unbekanntes auch hier ein <c>none</c>, beendete ein
        /// Tippfehler ein Abonnement.
        /// </remarks>
        [Test]
        public async Task AnUnknownState_IsRejectedAndChangesNothing()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var subId = await SubscribeAsync(alice, "abo-13");

            var unsinn = await AskAsync(bob, "subm-16",
                                        NodeSubscriptionsIq("subm-16", "set",
                                                            SubscriberEntry(alice.BareJid, "nonw")));

            var schwebend = await AskAsync(bob, "subm-17",
                                           NodeSubscriptionsIq("subm-17", "set",
                                                               SubscriberEntry(alice.BareJid, "pending", subId)));

            var liste = await AskAsync(bob, "subm-18", NodeSubscriptionsIq("subm-18", "get"));

            Assert.Multiple(() =>
            {

                Assert.That(ConditionOf(unsinn),    Is.EqualTo("bad-request"),
                            "Kein Zustandsname - und beinahe einer.");

                Assert.That(ConditionOf(schwebend), Is.EqualTo("not-allowed"),
                            "Ein Zustandsname, aber keiner, den dieser Server herstellen kann.");

                Assert.That(SubscriptionsIn(liste, OwnerNamespace).Select(e => e.Attr("subid")),
                            Is.EqualTo(new[] { subId }));

            });

        }

        #endregion

        #region HalfAnInstruction_IsNoInstruction()

        /// <summary>
        /// Erst alles prüfen, dann alles ausführen: Ein fehlerhafter Eintrag
        /// verwirft auch die gültigen davor.
        /// </summary>
        /// <remarks>
        /// Eine Anweisung, die zur Hälfte gilt, wäre schlimmer als eine, die
        /// ganz abgewiesen wird - der Absender wüsste nicht, welche Hälfte.
        /// </remarks>
        [Test]
        public async Task HalfAnInstruction_IsNoInstruction()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            var ihres  = await SubscribeAsync(alice, "abo-14");
            var seines = await SubscribeAsync(carol, "abo-15");

            var abgewiesen = await AskAsync(bob, "subm-19",
                                            NodeSubscriptionsIq("subm-19", "set",
                                                                SubscriberEntry(alice.BareJid, "none") +
                                                                SubscriberEntry(carol.BareJid, "vielleicht")));

            var liste = await AskAsync(bob, "subm-20", NodeSubscriptionsIq("subm-20", "get"));

            Assert.Multiple(() =>
            {

                Assert.That(ConditionOf(abgewiesen), Is.EqualTo("bad-request"));

                Assert.That(SubscriptionsIn(liste, OwnerNamespace).Select(e => e.Attr("subid")),
                            Is.EquivalentTo(new[] { ihres, seines }),
                            "Auch Alices Abonnement steht noch da - geprüft wurde vor dem ersten Schritt.");

            });

        }

        #endregion

        #region TheRemovedSubscriber_IsTold()

        /// <summary>
        /// XEP-0060, Abschnitt 8.8.4: Wer beendet wurde, ohne zu fragen,
        /// erfährt es.
        /// </summary>
        /// <remarks>
        /// Sonst wartet er auf Meldungen, die nicht mehr kommen — der Zustand,
        /// den <c>PubSubSubscriptionState</c> seit D71 als den schlimmeren
        /// beschreibt. Die Kennung gehört dazu: Bei mehreren Abonnements ist
        /// sie das einzige, woran der Empfänger erkennt, welches erloschen ist.
        /// </remarks>
        [Test]
        public async Task TheRemovedSubscriber_IsTold()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var subId = await SubscribeAsync(alice, "abo-19");

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "subm-23",
                           NodeSubscriptionsIq("subm-23", "set",
                                               SubscriberEntry(alice.BareJid, "none")));

            await WaitFor(() => EndingsIn(ereignisse).Count > 0, "die Abmeldung an den Entfernten");

            Assert.Multiple(() =>
            {

                Assert.That(EndingsIn(ereignisse),
                            Is.EqualTo(new[] { ((String?) Node, (String?) alice.BareJid, (String?) subId) }));

                Assert.That(ereignisse[0], Does.Contain($"from='bob@{Server.Domain}'"),
                            "Sie kommt vom Konto, dem der Knoten gehört - sonst verwirft sie der " +
                            "Spoofing-Schutz des Empfängers zu Recht.");

            });

        }

        #endregion

        #region EveryEndedSubscription_IsAnnouncedOnce()

        /// <summary>
        /// Eine Meldung je erloschenem Abonnement, nicht eine je Anweisung.
        /// </summary>
        /// <remarks>
        /// Ein <c>none</c> ohne Kennung beendet alle Abonnements dieses JIDs.
        /// Käme darauf nur eine Meldung, wüsste der Empfänger von einer
        /// Kennung, dass sie erloschen ist, und von der anderen nichts.
        /// </remarks>
        [Test]
        public async Task EveryEndedSubscription_IsAnnouncedOnce()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var erste  = await SubscribeAsync(alice, "abo-28");
            var zweite = await SubscribeAsync(alice, "abo-29");

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "subm-28",
                           NodeSubscriptionsIq("subm-28", "set",
                                               SubscriberEntry(alice.BareJid, "none")));

            await WaitFor(() => EndingsIn(ereignisse).Count > 1, "beide Abmeldungen");

            Assert.That(EndingsIn(ereignisse).Select(e => e.SubId),
                        Is.EquivalentTo(new[] { erste, zweite }));

        }

        #endregion

        #region TheOutcast_IsToldToo()

        /// <summary>
        /// Auch der Ausschluss beendet Abonnements (Abschnitt 8.9.4) — und auch
        /// davon erfährt der Betroffene.
        /// </summary>
        /// <remarks>
        /// <b>Der Ausschluss selbst bleibt ihm verborgen.</b> Was er an diesem
        /// Knoten ist, geht ihn nichts an; dass er ihn nicht mehr bekommt,
        /// schon. Zwei verschiedene Auskünfte, und nur die zweite schuldet ihm
        /// der Server.
        /// </remarks>
        [Test]
        public async Task TheOutcast_IsToldToo()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var subId = await SubscribeAsync(alice, "abo-20");

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "aff-20",
                           AffiliationsIq("aff-20", "set",
                                          $"<affiliation jid='{alice.BareJid}' affiliation='outcast'/>"));

            await WaitFor(() => EndingsIn(ereignisse).Count > 0, "die Abmeldung an den Ausgeschlossenen");

            Assert.Multiple(() =>
            {

                Assert.That(EndingsIn(ereignisse),
                            Is.EqualTo(new[] { ((String?) Node, (String?) alice.BareJid, (String?) subId) }));

                Assert.That(ereignisse.Any(e => e.Contains("outcast", StringComparison.Ordinal)),
                            Is.False,
                            "Seine Rolle steht nicht darin.");

            });

        }

        #endregion

        #region OnlyTheEndedOne_IsAnnounced()

        /// <summary>
        /// Gemeldet wird, was erloschen ist - nicht, was der Eigentümer
        /// aufgeschrieben hat.
        /// </summary>
        [Test]
        public async Task OnlyTheEndedOne_IsAnnounced()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var erste = await SubscribeAsync(alice, "abo-21");

            await SubscribeAsync(alice, "abo-22");

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "subm-24",
                           NodeSubscriptionsIq("subm-24", "set",
                                               SubscriberEntry(alice.BareJid, "none", erste)));

            await WaitFor(() => EndingsIn(ereignisse).Count > 0, "die Abmeldung des einen Abonnements");

            await AskAsync(bob, "subm-25", NodeSubscriptionsIq("subm-25", "get"));

            Assert.That(EndingsIn(ereignisse).Select(e => e.SubId),
                        Is.EqualTo(new[] { erste }),
                        "Genau eines ist erloschen, also kommt genau eine Meldung.");

        }

        #endregion

        #region NobodyElse_IsTold()

        /// <summary>
        /// Die Abmeldung geht an den Betroffenen und an sonst niemanden.
        /// </summary>
        /// <remarks>
        /// Wer sie mitbekäme, erführe, wer den Knoten verlassen hat — und der
        /// Eigentümer bekäme sie als Antwort auf seine eigene Anweisung ein
        /// zweites Mal.
        /// </remarks>
        [Test]
        public async Task NobodyElse_IsTold()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            await SubscribeAsync(alice, "abo-23");
            await SubscribeAsync(carol, "abo-24");

            var beiAlice = CollectEvents(alice);
            var beiCarol = CollectEvents(carol);
            var beiBob   = CollectEvents(bob);

            await AskAsync(bob, "subm-26",
                           NodeSubscriptionsIq("subm-26", "set",
                                               SubscriberEntry(alice.BareJid, "none")));

            await WaitFor(() => EndingsIn(beiAlice).Count > 0, "die Abmeldung an die Entfernte");

            await WaitAgainst(() => EndingsIn(beiCarol).Count > 0 || EndingsIn(beiBob).Count > 0,
                              "eine Abmeldung an Unbeteiligte");

        }

        #endregion

        #region AnUnsuccessfulRemoval_AnnouncesNothing()

        /// <summary>
        /// Eine abgewiesene Anweisung meldet nichts ab.
        /// </summary>
        /// <remarks>
        /// Sonst hinge die Meldung an dem, was jemand aufgeschrieben hat, und
        /// nicht an dem, was geschehen ist: Alice bekäme die Abmeldung eines
        /// Abonnements, das sie weiterhin hat.
        /// </remarks>
        [Test]
        public async Task AnUnsuccessfulRemoval_AnnouncesNothing()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "abo-25");

            var ereignisse = CollectEvents(alice);

            var abgewiesen = await AskAsync(bob, "subm-27",
                                            NodeSubscriptionsIq("subm-27", "set",
                                                                SubscriberEntry(alice.BareJid, "none", "gibtesnicht")));

            await WaitAgainst(() => EndingsIn(ereignisse).Count > 0,
                              "eine Abmeldung ohne beendetes Abonnement");

            Assert.That(ConditionOf(abgewiesen), Is.EqualTo("item-not-found"));

        }

        #endregion

        #region WhoUnsubscribesHimself_IsNotTold()

        /// <summary>
        /// Wer selbst abbestellt, bekommt keine Abmeldung.
        /// </summary>
        /// <remarks>
        /// Er hat die Antwort schon: das <c>result</c> auf sein eigenes
        /// <c>unsubscribe</c>. Eine zweite Auskunft darüber wäre keine
        /// Nachricht, sondern ein Echo.
        /// </remarks>
        [Test]
        public async Task WhoUnsubscribesHimself_IsNotTold()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "abo-26");

            var ereignisse = CollectEvents(alice);

            var antwort = await AskAsync(alice, "unsub-20",
                                         PubSubBuilder.Unsubscribe($"bob@{Server.Domain}",
                                                                   Node,
                                                                   alice.BareJid,
                                                                   "unsub-20"));

            await WaitAgainst(() => EndingsIn(ereignisse).Count > 0,
                              "eine Abmeldung an den, der selbst abbestellt hat");

            Assert.That(antwort.Attr("type"), Is.EqualTo("result"));

        }

        #endregion

        #region WithAuthorize_ASubscriptionIsOnlyARequest()

        /// <summary>
        /// XEP-0060, Abschnitt 6.1.3.7: Auf einem Knoten mit
        /// Genehmigungsvorgang ist die Antwort ein <c>pending</c>.
        /// </summary>
        /// <remarks>
        /// <b>Das einzige Modell, bei dem Abonnieren und Hereinkommen zwei
        /// Dinge sind.</b> Jeder darf fragen - das Fragen ist der Vorgang -,
        /// und was er bekommt, ist die angenommene Frage und nicht die Zusage.
        /// Wer sie als Zusage läse, wartete auf Meldungen, die erst jemand
        /// freigeben muss.
        /// </remarks>
        [Test]
        public async Task WithAuthorize_ASubscriptionIsOnlyARequest()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-60",
                           ConfigureIq("cfg-60", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var alice = await ConnectClientAsync("alice");

            var zusage = await AskAsync(alice, "sub-60",
                                        PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                alice.BareJid, "sub-60"));

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "pub-60",
                           PublishIq("pub-60", Node, "60", "<wetter xmlns='urn:example:x'>heiter</wetter>"));

            await WaitAgainst(() => Count(ereignisse) > 0,
                              "eine Zustellung an ein beantragtes Abonnement");

            var abholen = await AskAsync(alice, "get-60",
                                         PubSubBuilder.GetItems($"bob@{Server.Domain}", Node, id: "get-60"));

            Assert.Multiple(() =>
            {

                Assert.That(zusage.Attr("type"),                  Is.EqualTo("result"),
                            "Fragen darf jeder.");

                Assert.That(SubscriptionOf(zusage)?.Attr("subscription"), Is.EqualTo("pending"),
                            "Aber die Antwort ist die angenommene Frage.");

                Assert.That(ConditionOf(abholen),                 Is.EqualTo("not-authorized"),
                            "Und abholen kann er auch nichts.");

            });

        }

        #endregion

        #region TheOwner_SeesWhoIsWaiting_AndApproves()

        /// <summary>
        /// XEP-0060, Abschnitt 8.6: Der Eigentümer sieht den Antrag in seiner
        /// Abonnentenliste und sagt ihn zu.
        /// </summary>
        /// <remarks>
        /// In D84 stand an der Liste, der Zustand stehe dort fest im Text, und
        /// dies wäre eine der Stellen, die einen echten brauchten, sobald es
        /// <c>authorize</c> gibt. Genau so ist es gekommen.
        /// </remarks>
        [Test]
        public async Task TheOwner_SeesWhoIsWaiting_AndApproves()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-61",
                           ConfigureIq("cfg-61", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var alice = await ConnectClientAsync("alice");

            var zusage = await AskAsync(alice, "sub-61",
                                        PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                alice.BareJid, "sub-61"));

            var subId  = SubscriptionOf(zusage)!.Attr("subid")!;

            var wartend = await AskAsync(bob, "subm-60", NodeSubscriptionsIq("subm-60", "get"));

            var beiAlice = CollectEvents(alice);

            var genehmigt = await AskAsync(bob, "subm-61",
                                           NodeSubscriptionsIq("subm-61", "set",
                                                               SubscriberEntry(alice.BareJid, "subscribed", subId)));

            await WaitFor(() => Count(beiAlice) > 0, "die Zusage an die Wartende");

            var danach = await AskAsync(bob, "subm-62", NodeSubscriptionsIq("subm-62", "get"));

            Assert.Multiple(() =>
            {

                Assert.That(SubscriptionsIn(wartend, OwnerNamespace).Select(e => e.Attr("subscription")),
                            Is.EqualTo(new[] { "pending" }),
                            "Der Eigentümer sieht, wer wartet.");

                Assert.That(genehmigt.Attr("type"), Is.EqualTo("result"));

                Assert.That(beiAlice[0], Does.Contain("subscription='subscribed'"),
                            "Und die Wartende erfährt die Zusage.");

                Assert.That(SubscriptionsIn(danach, OwnerNamespace).Select(e => e.Attr("subscription")),
                            Is.EqualTo(new[] { "subscribed" }));

            });

        }

        #endregion

        #region AfterTheApproval_TheItemsArrive()

        /// <summary>
        /// Erst die Zusage, dann die Zustellung - und dann auch das Abholen.
        /// </summary>
        [Test]
        public async Task AfterTheApproval_TheItemsArrive()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-62",
                           ConfigureIq("cfg-62", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var alice = await ConnectClientAsync("alice");

            var zusage = await AskAsync(alice, "sub-62",
                                        PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                alice.BareJid, "sub-62"));

            await AskAsync(bob, "subm-63",
                           NodeSubscriptionsIq("subm-63", "set",
                                               SubscriberEntry(alice.BareJid, "subscribed",
                                                               SubscriptionOf(zusage)!.Attr("subid"))));

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "pub-61",
                           PublishIq("pub-61", Node, "61", "<wetter xmlns='urn:example:x'>endlich</wetter>"));

            await WaitFor(() => ItemIdsIn(ereignisse).Count > 0, "die Zustellung nach der Zusage");

            var abholen = await AskAsync(alice, "get-61",
                                         PubSubBuilder.GetItems($"bob@{Server.Domain}", Node, id: "get-61"));

            Assert.Multiple(() =>
            {
                Assert.That(ItemIdsIn(ereignisse), Is.EqualTo(new[] { "61" }));
                Assert.That(abholen.Attr("type"),  Is.EqualTo("result"));
            });

        }

        #endregion

        #region ADeniedRequest_IsEndedAndAnnounced()

        /// <summary>
        /// Die Ablehnung ist dieselbe Anweisung wie das Entfernen - und sagt
        /// dem Wartenden Bescheid.
        /// </summary>
        /// <remarks>
        /// Ohne die Meldung wartete er weiter auf eine Antwort, die es schon
        /// gab. Das ist derselbe Grund wie in D85, nur von der anderen Seite:
        /// Wer nichts hört, hält die Frage für offen.
        /// </remarks>
        [Test]
        public async Task ADeniedRequest_IsEndedAndAnnounced()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-63",
                           ConfigureIq("cfg-63", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var alice = await ConnectClientAsync("alice");

            var zusage = await AskAsync(alice, "sub-63",
                                        PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                alice.BareJid, "sub-63"));

            var subId  = SubscriptionOf(zusage)!.Attr("subid")!;

            var beiAlice = CollectEvents(alice);

            await AskAsync(bob, "subm-64",
                           NodeSubscriptionsIq("subm-64", "set",
                                               SubscriberEntry(alice.BareJid, "none", subId)));

            await WaitFor(() => EndingsIn(beiAlice).Count > 0, "die Ablehnung an die Wartende");

            var danach = await AskAsync(bob, "subm-65", NodeSubscriptionsIq("subm-65", "get"));

            Assert.Multiple(() =>
            {

                Assert.That(EndingsIn(beiAlice).Select(e => e.SubId), Is.EqualTo(new[] { subId }));

                Assert.That(SubscriptionsIn(danach, OwnerNamespace), Is.Empty,
                            "Der Antrag ist fort und steht nicht als abgelehnter herum.");

            });

        }

        #endregion

        #region OnAnAuthorizeNode_APresenceContactGetsNothing()

        /// <summary>
        /// Auch die beiläufige Zustellung fragt das Zugriffsmodell.
        /// </summary>
        /// <remarks>
        /// <b>Sie tat es bis D93 nicht.</b> Ein Kontakt bekam jede
        /// Veröffentlichung über die Presence - auch von einem Knoten, dessen
        /// Modell ihm den Abruf versperrte. Das Modell hielt die Tür zu und
        /// liess die Meldung durch, in der der Eintrag vollständig steht; bei
        /// <c>authorize</c> wäre die Genehmigung damit eine blosse Förmlichkeit
        /// gewesen.
        /// </remarks>
        [Test]
        public async Task OnAnAuthorizeNode_APresenceContactGetsNothing()
        {

            MakeContacts("carol", "bob");

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-64",
                           ConfigureIq("cfg-64", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var carol      = await ConnectClientAsync("carol");
            var ereignisse = CollectEvents(carol);

            await AskAsync(bob, "pub-62",
                           PublishIq("pub-62", Node, "62", "<wetter xmlns='urn:example:x'>still</wetter>"));

            await WaitAgainst(() => Count(ereignisse) > 0,
                              "eine Zustellung an einen Kontakt ohne Zusage");

        }

        #endregion

        #region TheOwner_IsAskedWithAForm()

        /// <summary>
        /// XEP-0060, Abschnitt 8.6.1: Der Eigentümer bekommt den Antrag
        /// vorgelegt, ohne nachsehen zu müssen.
        /// </summary>
        /// <remarks>
        /// Die Vorbelegung von <c>pubsub#allow</c> ist <c>false</c>: Ein
        /// Formular, das schon auf „ja" steht, machte aus dem Wegklicken eine
        /// Zusage.
        /// </remarks>
        [Test]
        public async Task TheOwner_IsAskedWithAForm()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-70",
                           ConfigureIq("cfg-70", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var beiBob = CollectRaw(bob, PubSubSubscribeAuthorization.FormType);

            var alice  = await ConnectClientAsync("alice");

            var zusage = await AskAsync(alice, "sub-70",
                                        PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                alice.BareJid, "sub-70"));

            await WaitFor(() => Count(beiBob) > 0, "den Antrag beim Eigentümer");

            var formular = XElement.Parse(beiBob[0]).Child("jabber:x:data", "x")!;

            Assert.Multiple(() =>
            {

                Assert.That(FormValue(formular, PubSubSubscribeAuthorization.NodeVariable),
                            Is.EqualTo(Node));

                Assert.That(FormValue(formular, PubSubSubscribeAuthorization.SubscriberVariable),
                            Is.EqualTo(alice.BareJid));

                Assert.That(FormValue(formular, PubSubSubscribeAuthorization.SubIdVariable),
                            Is.EqualTo(SubscriptionOf(zusage)!.Attr("subid")));

                Assert.That(FormValue(formular, PubSubSubscribeAuthorization.AllowVariable),
                            Is.EqualTo("0"),
                            "Ein Formular, das schon auf ja steht, macht aus dem Wegklicken eine Zusage.");

            });

        }

        #endregion

        #region TheReturnedForm_ApprovesTheRequest()

        /// <summary>
        /// XEP-0060, Abschnitt 8.6.2: Das zurückgeschickte Formular sagt zu -
        /// und tut dasselbe wie die Abonnentenliste.
        /// </summary>
        /// <remarks>
        /// <b>Zwei Türen in denselben Raum.</b> Die Liste ist die Sicht eines
        /// Verwalters, das Formular die eines Menschen, dem sein Client eine
        /// Frage anzeigt. Ein Formular zu stellen, das niemand beantworten
        /// kann, wäre schlimmer als keines: Der Mensch genehmigte etwas, und
        /// es geschähe nichts.
        /// </remarks>
        [Test]
        public async Task TheReturnedForm_ApprovesTheRequest()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-71",
                           ConfigureIq("cfg-71", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var alice  = await ConnectClientAsync("alice");

            var zusage = await AskAsync(alice, "sub-71",
                                        PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                alice.BareJid, "sub-71"));

            var subId  = SubscriptionOf(zusage)!.Attr("subid")!;

            var beiAlice = CollectEvents(alice);

            await bob.SendRawAsync(AuthorizationAnswer(alice.BareJid, subId, ja: true));

            await WaitFor(() => beiAlice.Any(e => e.Contains("subscription='subscribed'", StringComparison.Ordinal)),
                          "die Zusage an die Wartende");

            var liste = await AskAsync(bob, "subm-70", NodeSubscriptionsIq("subm-70", "get"));

            Assert.That(SubscriptionsIn(liste, OwnerNamespace).Select(e => e.Attr("subscription")),
                        Is.EqualTo(new[] { "subscribed" }));

        }

        #endregion

        #region TheReturnedForm_DeniesTheRequest()

        /// <summary>
        /// Und ein „nein" beendet den Antrag - dieselbe Meldung wie ein
        /// Entfernen.
        /// </summary>
        [Test]
        public async Task TheReturnedForm_DeniesTheRequest()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-72",
                           ConfigureIq("cfg-72", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var alice  = await ConnectClientAsync("alice");

            var zusage = await AskAsync(alice, "sub-72",
                                        PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                alice.BareJid, "sub-72"));

            var subId  = SubscriptionOf(zusage)!.Attr("subid")!;

            var beiAlice = CollectEvents(alice);

            await bob.SendRawAsync(AuthorizationAnswer(alice.BareJid, subId, ja: false));

            await WaitFor(() => EndingsIn(beiAlice).Count > 0, "die Ablehnung an die Wartende");

            var liste = await AskAsync(bob, "subm-71", NodeSubscriptionsIq("subm-71", "get"));

            Assert.Multiple(() =>
            {
                Assert.That(EndingsIn(beiAlice).Select(e => e.SubId), Is.EqualTo(new[] { subId }));
                Assert.That(SubscriptionsIn(liste, OwnerNamespace),   Is.Empty);
            });

        }

        #endregion

        #region ADenialAfterTheApproval_ChangesNothing()

        /// <summary>
        /// Ein „nein" auf eine Frage von vorhin beendet kein zugesagtes
        /// Abonnement.
        /// </summary>
        /// <remarks>
        /// Sonst entschiede die Reihenfolge zweier Nachrichten darüber, was
        /// gilt - und ein spät eintreffendes Formular nähme jemandem ein
        /// Abonnement weg, das er längst hat.
        /// </remarks>
        [Test]
        public async Task ADenialAfterTheApproval_ChangesNothing()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-73",
                           ConfigureIq("cfg-73", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var alice  = await ConnectClientAsync("alice");

            var zusage = await AskAsync(alice, "sub-73",
                                        PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                alice.BareJid, "sub-73"));

            var subId  = SubscriptionOf(zusage)!.Attr("subid")!;

            await AskAsync(bob, "subm-72",
                           NodeSubscriptionsIq("subm-72", "set",
                                               SubscriberEntry(alice.BareJid, "subscribed", subId)));

            var beiAlice = CollectEvents(alice);

            await bob.SendRawAsync(AuthorizationAnswer(alice.BareJid, subId, ja: false));

            await WaitAgainst(() => EndingsIn(beiAlice).Count > 0,
                              "eine Ablehnung nach der Zusage");

            var liste = await AskAsync(bob, "subm-73", NodeSubscriptionsIq("subm-73", "get"));

            Assert.That(SubscriptionsIn(liste, OwnerNamespace).Select(e => e.Attr("subscription")),
                        Is.EqualTo(new[] { "subscribed" }));

        }

        #endregion

        #region AFormAboutAForeignNode_IsNoAnswer()

        /// <summary>
        /// Beschieden wird nur, was am eigenen Knoten hängt - alles andere
        /// bleibt eine Nachricht.
        /// </summary>
        /// <remarks>
        /// Alice kann Bobs Antrag nicht für ihn beantworten: Ihr Formular nennt
        /// einen Knoten, den es in ihrem Konto nicht gibt. <b>Und es
        /// verschwindet dabei nicht</b> - es geht seinen gewöhnlichen Weg
        /// weiter. Eine Nachricht spurlos verschwinden zu lassen ist die
        /// teuerste Art, höflich zu sein; genau daran hing die Mutation, die
        /// diese Prüfung entfernt.
        /// </remarks>
        [Test]
        public async Task AFormAboutAForeignNode_IsNoAnswer()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-74",
                           ConfigureIq("cfg-74", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var alice  = await ConnectClientAsync("alice");
            var carol  = await ConnectClientAsync("carol");

            var zusage = await AskAsync(alice, "sub-74",
                                        PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                alice.BareJid, "sub-74"));

            var subId  = SubscriptionOf(zusage)!.Attr("subid")!;

            var beiCarol = CollectRaw(carol, PubSubSubscribeAuthorization.FormType);

            // Alice schickt die Antwort auf ihren eigenen Antrag an Carol - in
            // deren Konto es diesen Knoten nicht gibt.
            await alice.SendRawAsync(AuthorizationAnswer(alice.BareJid, subId, ja: true, an: carol.BareJid));

            await WaitFor(() => Count(beiCarol) > 0,
                          "die Nachricht, die keine Antwort ist");

            Assert.That(Server.GetAccount($"bob@{Server.Domain}")!
                              .PepSubscriptions(Node)
                              .Select(a => a.State),
                        Is.EqualTo(new[] { PubSubSubscriptionState.Pending }),
                        "Und Bobs Antrag steht unbeschieden da.");

        }

        #endregion

        #region WithRosterAccess_OnlyTheRosterGetsIn()

        /// <summary>
        /// XEP-0060, Abschnitt 4.5: Beim Zugriffsmodell <c>roster</c> kommt
        /// herein, wer im Roster des Eigentümers steht.
        /// </summary>
        /// <remarks>
        /// <b>Ein Eintrag genügt, ein Presence-Zustand wird nicht verlangt.</b>
        /// Der Roster ist die Liste des Eigentümers: Wer darin steht, steht
        /// dort, weil der Eigentümer ihn eingetragen hat. Ob der Kontakt
        /// umgekehrt dessen Presence sehen darf, ist eine andere Frage - und
        /// für die gibt es <c>presence</c>.
        /// </remarks>
        [Test]
        public async Task WithRosterAccess_OnlyTheRosterGetsIn()
        {

            var bob = await PublishingBobAsync();

            // Ein Eintrag ganz ohne Presence-Berechtigung.
            Server.GetAccount($"bob@{Server.Domain}")!.SetRosterEntry(
                new RosterEntry($"alice@{Server.Domain}", null, "none"));

            await AskAsync(bob, "cfg-50",
                           ConfigureIq("cfg-50", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>roster</value></field>")));

            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            var drin   = await AskAsync(alice, "sub-50",
                                        PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                alice.BareJid, "sub-50"));

            var draussen = await AskAsync(carol, "sub-51",
                                          PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                  carol.BareJid, "sub-51"));

            Assert.Multiple(() =>
            {

                Assert.That(drin.Attr("type"),      Is.EqualTo("result"),
                            "Alice steht im Roster - auch ohne jede Presence-Berechtigung.");

                Assert.That(ConditionOf(draussen),  Is.EqualTo("not-authorized"),
                            "Carol steht nirgends.");

            });

        }

        #endregion

        #region WithRosterGroups_OnlyTheNamedOnesGetIn()

        /// <summary>
        /// Sind Gruppen genannt, kommt nur herein, wer in einer davon steht.
        /// </summary>
        [Test]
        public async Task WithRosterGroups_OnlyTheNamedOnesGetIn()
        {

            var bob   = await PublishingBobAsync();
            var konto = Server.GetAccount($"bob@{Server.Domain}")!;

            konto.SetRosterEntry(new RosterEntry($"alice@{Server.Domain}", null, "both", null, false, ["Freunde"]));
            konto.SetRosterEntry(new RosterEntry($"carol@{Server.Domain}", null, "both", null, false, ["Arbeit"]));

            await AskAsync(bob, "cfg-51",
                           ConfigureIq("cfg-51", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>roster</value></field>" +
                                                  "<field var='pubsub#roster_groups_allowed'>" +
                                                  "<value>Freunde</value></field>")));

            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            var drin     = await AskAsync(alice, "sub-52",
                                          PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                  alice.BareJid, "sub-52"));

            var draussen = await AskAsync(carol, "sub-53",
                                          PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                  carol.BareJid, "sub-53"));

            Assert.Multiple(() =>
            {
                Assert.That(drin.Attr("type"),     Is.EqualTo("result"));
                Assert.That(ConditionOf(draussen), Is.EqualTo("not-authorized"),
                            "Im Roster, aber in der falschen Gruppe.");
            });

        }

        #endregion

        #region SeveralGroups_AreAllRead()

        /// <summary>
        /// Ein Mehrfachfeld trägt mehrere Werte - und alle werden gelesen.
        /// </summary>
        /// <remarks>
        /// Ein <c>list-multi</c>, von dem nur der erste Wert ankäme, gäbe dem
        /// Eigentümer eine Liste zurück, die er so nie geschickt hat - und
        /// sperrte die halbe erlaubte Menge aus.
        /// </remarks>
        [Test]
        public async Task SeveralGroups_AreAllRead()
        {

            var bob   = await PublishingBobAsync();
            var konto = Server.GetAccount($"bob@{Server.Domain}")!;

            konto.SetRosterEntry(new RosterEntry($"carol@{Server.Domain}", null, "both", null, false, ["Arbeit"]));

            await AskAsync(bob, "cfg-52",
                           ConfigureIq("cfg-52", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>roster</value></field>" +
                                                  "<field var='pubsub#roster_groups_allowed'>" +
                                                  "<value>Freunde</value><value>Arbeit</value></field>")));

            var gelesen = await AskAsync(bob, "cfg-53", ConfigureIq("cfg-53", "get"));

            var carol = await ConnectClientAsync("carol");

            var drin  = await AskAsync(carol, "sub-54",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                               carol.BareJid, "sub-54"));

            Assert.Multiple(() =>
            {

                Assert.That(ConfigValues(gelesen, "pubsub#roster_groups_allowed"),
                            Is.EqualTo(new[] { "Freunde", "Arbeit" }),
                            "Das Angebot nennt beide zurück.");

                Assert.That(drin.Attr("type"), Is.EqualTo("result"),
                            "Und die zweite Gruppe gilt so gut wie die erste.");

            });

        }

        #endregion

        #region WithoutGroups_TheWholeRosterGetsIn()

        /// <summary>
        /// Keine Gruppe genannt heisst: der ganze Roster.
        /// </summary>
        /// <remarks>
        /// Die leere Liste als „niemand" zu lesen wäre die andere Möglichkeit
        /// und die schlechtere: Sie machte <c>roster</c> in seiner
        /// Grundeinstellung wirkungsgleich mit einer leeren <c>whitelist</c> -
        /// zwei Namen für dieselbe Sache, und einer davon führte in die Irre.
        /// </remarks>
        [Test]
        public async Task WithoutGroups_TheWholeRosterGetsIn()
        {

            var bob   = await PublishingBobAsync();
            var konto = Server.GetAccount($"bob@{Server.Domain}")!;

            konto.SetRosterEntry(new RosterEntry($"alice@{Server.Domain}", null, "both", null, false, ["Freunde"]));

            await AskAsync(bob, "cfg-54",
                           ConfigureIq("cfg-54", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>roster</value></field>" +
                                                  "<field var='pubsub#roster_groups_allowed'/>")));

            var alice = await ConnectClientAsync("alice");

            var drin  = await AskAsync(alice, "sub-55",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                               alice.BareJid, "sub-55"));

            Assert.That(drin.Attr("type"), Is.EqualTo("result"),
                        "Eine Gruppe zu haben schadet nicht, wenn keine verlangt ist.");

        }

        #endregion

        #region TheGroupsSurvive_AChangeOfTheAccessModel()

        /// <summary>
        /// Die Gruppenliste ist eine Einstellung des Knotens und nicht des
        /// Modells.
        /// </summary>
        /// <remarks>
        /// Wer von <c>open</c> auf <c>roster</c> umstellt, soll die Liste
        /// vorher setzen können - sonst stünde der Knoten zwischen den beiden
        /// Anweisungen für den ganzen Roster offen.
        /// </remarks>
        [Test]
        public async Task TheGroupsSurvive_AChangeOfTheAccessModel()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-55",
                           ConfigureIq("cfg-55", "set",
                                       ConfigForm("<field var='pubsub#roster_groups_allowed'>" +
                                                  "<value>Freunde</value></field>")));

            await AskAsync(bob, "cfg-56",
                           ConfigureIq("cfg-56", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>roster</value></field>")));

            var gelesen = await AskAsync(bob, "cfg-57", ConfigureIq("cfg-57", "get"));

            Assert.Multiple(() =>
            {
                Assert.That(ConfigField (gelesen, "pubsub#access_model"),          Is.EqualTo("roster"));
                Assert.That(ConfigValues(gelesen, "pubsub#roster_groups_allowed"), Is.EqualTo(new[] { "Freunde" }));
            });

        }

        #endregion

        #region ARetractedItem_IsGoneAndAnnounced()

        /// <summary>
        /// XEP-0060, Abschnitt 7.2: Ein einzelner Eintrag wird zurückgenommen -
        /// und die Abonnenten erfahren es mit seiner Kennung.
        /// </summary>
        /// <remarks>
        /// <b>Eine Zustellung wie eine Veröffentlichung</b>, nur mit anderem
        /// Inhalt: je Abonnement eine, mit der SHIM-Kennung. Das unterscheidet
        /// sie vom Löschen und Leeren, die den Knoten betreffen und deshalb je
        /// Abonnenten einmal hinausgehen.
        /// </remarks>
        [Test]
        public async Task ARetractedItem_IsGoneAndAnnounced()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await AskAsync(bob, "pub-30",
                           PublishIq("pub-30", Node, "30", "<wetter xmlns='urn:example:x'>windig</wetter>"));

            var subId = await SubscribeAsync(alice, "abo-40");

            var ereignisse = CollectEvents(alice);

            var zurueck = await AskAsync(bob, "ret-1", RetractIq("ret-1", "1"));

            await WaitFor(() => RetractsIn(ereignisse).Count > 0, "die Meldung über die Rücknahme");

            var konto = Server.GetAccount($"bob@{Server.Domain}")!;

            Assert.Multiple(() =>
            {

                Assert.That(zurueck.Attr("type"),   Is.EqualTo("result"));
                Assert.That(RetractsIn(ereignisse), Is.EqualTo(new[] { "1" }));
                Assert.That(SubIdsIn(ereignisse),   Is.EqualTo(new[] { subId }),
                            "Mit Kennung: Es ist eine Zustellung und keine Nachricht über den Knoten.");

                Assert.That(konto.GetPepItems(Node).Select(e => e.ItemId), Is.EqualTo(new[] { "30" }),
                            "Der eine Eintrag ist fort, der andere steht da.");

            });

        }

        #endregion

        #region ARetractionWithoutTheRole_IsForbidden()

        /// <summary>
        /// Dieselbe Regel wie beim Veröffentlichen: Wer nicht schreiben darf,
        /// darf auch nicht zurücknehmen.
        /// </summary>
        [Test]
        public async Task ARetractionWithoutTheRole_IsForbidden()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            var abgewiesen = await AskAsync(alice, "ret-2", RetractIq("ret-2", "1"));

            Assert.Multiple(() =>
            {

                Assert.That(ConditionOf(abgewiesen), Is.EqualTo("forbidden"));

                Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.GetPepItems(Node), Is.Not.Empty,
                            "Und der Eintrag steht noch da.");

            });

        }

        #endregion

        #region APublisher_MayRetractToo()

        /// <summary>
        /// <b>Wer schreiben darf, darf auch zurücknehmen</b> - und die Meldung
        /// kommt trotzdem vom Eigentümer.
        /// </summary>
        /// <remarks>
        /// Ein Publizierender kommt damit auch an fremde Einträge im selben
        /// Knoten. Sie auseinanderzuhalten hiesse, sich zu merken, wer welchen
        /// geschrieben hat - eine Ablage, die es hier nicht gibt, und ohne die
        /// jede feinere Regel bloss behauptet wäre.
        /// </remarks>
        [Test]
        public async Task APublisher_MayRetractToo()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            await AskAsync(bob, "aff-40",
                           AffiliationsIq("aff-40", "set",
                                          $"<affiliation jid='{carol.BareJid}' affiliation='publisher'/>"));

            await SubscribeAsync(alice, "abo-41");

            var ereignisse = CollectEvents(alice);

            var zurueck = await AskAsync(carol, "ret-3", RetractIq("ret-3", "1"));

            await WaitFor(() => RetractsIn(ereignisse).Count > 0, "die Meldung über die fremde Rücknahme");

            Assert.Multiple(() =>
            {

                Assert.That(zurueck.Attr("type"), Is.EqualTo("result"));

                Assert.That(ereignisse[0], Does.Contain($"from='bob@{Server.Domain}'"),
                            "Die Meldung kommt vom Eigentümer und nicht von dem, der zurückgenommen hat.");

                Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.GetPepItems(Node), Is.Empty);

            });

        }

        #endregion

        #region RetractingWhatIsNotThere_IsRejected()

        /// <summary>
        /// XEP-0060, Abschnitt 7.2.3.2: Was es nicht gibt, wird nicht
        /// zurückgenommen.
        /// </summary>
        /// <remarks>
        /// Ein <c>result</c> darauf wäre die Auskunft, der Eintrag sei jetzt
        /// fort - und die Meldung an die Abonnenten die Aufforderung, etwas
        /// wegzuwerfen, das sie nie bekommen haben.
        /// </remarks>
        [Test]
        public async Task RetractingWhatIsNotThere_IsRejected()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "abo-42");

            var ereignisse = CollectEvents(alice);

            var falscherEintrag = await AskAsync(bob, "ret-4", RetractIq("ret-4", "gibtesnicht"));
            var falscherKnoten  = await AskAsync(bob, "ret-5", RetractIq("ret-5", "1", "urn:example:nichts"));
            var ohneEintrag     = await AskAsync(bob, "ret-6", RetractIq("ret-6", null));

            await WaitAgainst(() => RetractsIn(ereignisse).Count > 0,
                              "eine Meldung über eine Rücknahme, die nicht stattfand");

            Assert.Multiple(() =>
            {

                Assert.That(ConditionOf(falscherEintrag), Is.EqualTo("item-not-found"));

                // Für einen Fremden käme hier ein <forbidden/>: An einem
                // Knoten, den es nicht gibt, hat niemand eine Rolle. Für den
                // Eigentümer nicht - er wird erkannt und nicht nachgeschlagen,
                // weil ein PEP-Knoten dem Konto gehört. Ihm fehlt also nicht
                // die Erlaubnis, sondern der Eintrag.
                Assert.That(ConditionOf(falscherKnoten),  Is.EqualTo("item-not-found"),
                            "Dem Eigentümer fehlt nicht die Rolle, sondern der Eintrag.");

                Assert.That(ConditionOf(ohneEintrag),     Is.EqualTo("bad-request"),
                            "„Nimm irgendetwas zurück\" gibt es nicht - dafür ist das Leeren da.");

            });

        }

        #endregion

        #region RetractingFromANodeWithoutStorage_IsRejected()

        /// <summary>
        /// XEP-0060, Abschnitt 7.2.3.3, wie beim Leeren: Was nichts aufbewahrt,
        /// kann nichts zurücknehmen.
        /// </summary>
        [Test]
        public async Task RetractingFromANodeWithoutStorage_IsRejected()
        {

            var bob = await ConnectClientAsync("bob");

            await AskAsync(bob, "cr-40",
                           $"<iq type='set' id='cr-40'>" +
                           $"<pubsub xmlns='{PubSubNamespace}'>" +
                           $"<create node='urn:example:fluechtig'/>" +
                           "<configure>" +
                           ConfigForm("<field var='pubsub#persist_items'><value>0</value></field>") +
                           "</configure></pubsub></iq>");

            var abgewiesen = await AskAsync(bob, "ret-7",
                                            RetractIq("ret-7", "1", "urn:example:fluechtig"));

            Assert.Multiple(() =>
            {
                Assert.That(ConditionOf(abgewiesen),       Is.EqualTo("feature-not-implemented"));
                Assert.That(PubSubConditionOf(abgewiesen), Is.EqualTo("unsupported"));
            });

        }

        #endregion

        #region ARetraction_RespectsASilencedSubscription()

        /// <summary>
        /// Ein stillgelegtes Abonnement bleibt auch bei einer Rücknahme still.
        /// </summary>
        /// <remarks>
        /// Sie geht denselben Weg wie eine Veröffentlichung - dass beide durch
        /// dieselbe Stelle laufen, ist genau der Grund, aus dem hier nichts
        /// zusätzlich zu bedenken war.
        /// </remarks>
        [Test]
        public async Task ARetraction_RespectsASilencedSubscription()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var subId = await SubscribeAsync(alice, "abo-43");

            await AskAsync(alice, "opt-40",
                           OptionsIq("opt-40", "set", subId, SubmitForm(DeliverField("0"))));

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "ret-8", RetractIq("ret-8", "1"));

            await WaitAgainst(() => RetractsIn(ereignisse).Count > 0,
                              "eine Rücknahme an ein stillgelegtes Abonnement");

        }

        #endregion

        #region TheLastRetractedItem_LeavesTheNodeStanding()

        /// <summary>
        /// Auch der letzte Eintrag nimmt den Knoten nicht mit.
        /// </summary>
        /// <remarks>
        /// Ein Knoten, der mit seinem Inhalt verschwände, wäre für seine
        /// Abonnenten ohne Ankündigung fort - und die nächste Veröffentlichung
        /// legte einen neuen an, den niemand abonniert hat.
        /// </remarks>
        [Test]
        public async Task TheLastRetractedItem_LeavesTheNodeStanding()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var subId = await SubscribeAsync(alice, "abo-44");

            await AskAsync(bob, "ret-9", RetractIq("ret-9", "1"));

            var konto = Server.GetAccount($"bob@{Server.Domain}")!;

            var gleichDanach = (Existiert: konto.PepNodeExists(Node),
                                Eintraege: konto.GetPepItems(Node).Count);

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "pub-31",
                           PublishIq("pub-31", Node, "31", "<wetter xmlns='urn:example:x'>klar</wetter>"));

            await WaitFor(() => SubIdsIn(ereignisse).Count > 0, "die Zustellung nach der Rücknahme");

            Assert.Multiple(() =>
            {

                Assert.That(gleichDanach.Existiert, Is.True,
                            "Den Knoten gibt es weiter.");

                Assert.That(gleichDanach.Eintraege, Is.Zero);

                Assert.That(konto.PepSubscriptions(Node).Select(a => a.SubId), Is.EqualTo(new[] { subId }),
                            "Und das Abonnement auch.");

            });

        }

        #endregion

        #region ADeletedNode_TakesEverythingWithIt()

        /// <summary>
        /// XEP-0060, Abschnitt 8.4: Gelöscht wird der Knoten, nicht sein
        /// Inhalt - mit Einträgen, Einstellungen, Abonnements und Rollen.
        /// </summary>
        /// <remarks>
        /// <b>Die Rollen sind der Grund, das aufzuschreiben.</b> Blieben sie
        /// stehen, erbte der nächste Knoten desselben Namens eine
        /// Ausschlussliste, die niemand mehr sieht - und der Eigentümer
        /// wunderte sich, warum ein Bekannter an seinen neuen Knoten nicht
        /// herankommt.
        /// </remarks>
        [Test]
        public async Task ADeletedNode_TakesEverythingWithIt()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            await SubscribeAsync(alice, "abo-30");

            await AskAsync(bob, "aff-30",
                           AffiliationsIq("aff-30", "set",
                                          $"<affiliation jid='{carol.BareJid}' affiliation='outcast'/>"));

            var geloescht = await AskAsync(bob, "del-1", OwnerIq("del-1", "set", "delete"));

            var konto = Server.GetAccount($"bob@{Server.Domain}")!;

            Assert.Multiple(() =>
            {

                Assert.That(geloescht.Attr("type"),         Is.EqualTo("result"));
                Assert.That(konto.PepNodeExists(Node),      Is.False);
                Assert.That(konto.PepSubscriptions(Node),   Is.Empty);
                Assert.That(konto.GetPepItems(Node),        Is.Empty);
                Assert.That(konto.PepNodeConfiguration(Node), Is.Null);

                Assert.That(konto.PepAffiliationOf(Node, carol.BareJid),
                            Is.EqualTo(PubSubAffiliation.None),
                            "Auch der Ausschluss ist mit dem Knoten gegangen.");

            });

        }

        #endregion

        #region ADeletedNode_CanBeSubscribedNoMore()

        /// <summary>
        /// Und was es nicht gibt, lässt sich nicht abonnieren.
        /// </summary>
        [Test]
        public async Task ADeletedNode_CanBeSubscribedNoMore()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await AskAsync(bob, "del-2", OwnerIq("del-2", "set", "delete"));

            var abgewiesen = await AskAsync(alice, "sub-30",
                                            PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                    alice.BareJid, "sub-30"));

            Assert.That(ConditionOf(abgewiesen), Is.EqualTo("item-not-found"));

        }

        #endregion

        #region DeletingSomebodyElsesNode_IsForbidden()

        /// <summary>
        /// Löschen darf nur der Eigentümer - hier hinge mehr daran als bei
        /// jeder anderen Anweisung.
        /// </summary>
        [Test]
        public async Task DeletingSomebodyElsesNode_IsForbidden()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            var abgewiesen = await AskAsync(alice, "del-3", OwnerIq("del-3", "set", "delete"));

            Assert.Multiple(() =>
            {
                Assert.That(ConditionOf(abgewiesen),                             Is.EqualTo("forbidden"));
                Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.PepNodeExists(Node), Is.True);
            });

        }

        #endregion

        #region DeletingANodeThatIsNotThere_IsRejected()

        /// <summary>
        /// Ein Knoten, den es nicht gibt, wird nicht gelöscht - er ist nicht
        /// schon gelöscht.
        /// </summary>
        [Test]
        public async Task DeletingANodeThatIsNotThere_IsRejected()
        {

            var bob = await PublishingBobAsync();

            var abgewiesen = await AskAsync(bob, "del-4",
                                            OwnerIq("del-4", "set", "delete", "urn:example:nichts"));

            Assert.That(ConditionOf(abgewiesen), Is.EqualTo("item-not-found"));

        }

        #endregion

        #region AGetOnDelete_IsRejected()

        /// <summary>
        /// Löschen und Leeren sind keine Fragen.
        /// </summary>
        /// <remarks>
        /// Ohne diese Prüfung fiele ein <c>get</c> auf <c>&lt;delete/&gt;</c>
        /// bis zum Einstellen durch und bekäme die Knotenkonfiguration zurück -
        /// eine Antwort auf eine Frage, die niemand gestellt hat.
        /// </remarks>
        [Test]
        public async Task AGetOnDelete_IsRejected()
        {

            var bob = await PublishingBobAsync();

            var geloescht = await AskAsync(bob, "del-5", OwnerIq("del-5", "get", "delete"));
            var geleert   = await AskAsync(bob, "pur-1", OwnerIq("pur-1", "get", "purge"));

            Assert.Multiple(() =>
            {

                Assert.That(ConditionOf(geloescht), Is.EqualTo("bad-request"));
                Assert.That(ConditionOf(geleert),   Is.EqualTo("bad-request"));

                Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.PepNodeExists(Node), Is.True,
                            "Und nichts davon hat etwas getan.");

            });

        }

        #endregion

        #region TheDeletion_ReachesEverySubscriberOnce()

        /// <summary>
        /// XEP-0060, Abschnitt 8.4.2: Eine Meldung je Abonnenten und nicht je
        /// Abonnement - und ohne Kennung.
        /// </summary>
        /// <remarks>
        /// Es endet nicht ein Abonnement, sondern der Knoten. Eine Kennung zu
        /// nennen hiesse, die anderen bestünden weiter. Aus demselben Grund
        /// kommt keine zweite Meldung nach Abschnitt 8.8.4 hinterher: Dass ein
        /// Abonnement auf einen Knoten, den es nicht mehr gibt, erloschen ist,
        /// steht schon hier.
        /// </remarks>
        [Test]
        public async Task TheDeletion_ReachesEverySubscriberOnce()
        {

            MakeContacts("carol", "bob");

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            await SubscribeAsync(alice, "abo-31");
            await SubscribeAsync(alice, "abo-32");

            var beiAlice = CollectEvents(alice);
            var beiCarol = CollectEvents(carol);

            await AskAsync(bob, "del-6", OwnerIq("del-6", "set", "delete"));

            await WaitFor(() => NodeEventsIn(beiAlice).Count > 0 && NodeEventsIn(beiCarol).Count > 0,
                          "die Löschmeldung an Abonnentin und Kontakt");

            Assert.Multiple(() =>
            {

                Assert.That(NodeEventsIn(beiAlice), Is.EqualTo(new[] { ("delete", (String?) Node) }),
                            "Zwei Abonnements, eine Meldung.");

                Assert.That(NodeEventsIn(beiCarol), Is.EqualTo(new[] { ("delete", (String?) Node) }),
                            "Wer die Einträge bekommen hätte, erfährt auch ihr Ende.");

                Assert.That(SubIdsIn(beiAlice), Is.Empty,
                            "Eine Kennung zu nennen hiesse, die anderen bestünden weiter.");

                Assert.That(EndingsIn(beiAlice), Is.Empty,
                            "Und eine zweite Meldung darüber gibt es nicht.");

            });

        }

        #endregion

        #region APurgedNode_KeepsItsSubscribersAndGoesOn()

        /// <summary>
        /// XEP-0060, Abschnitt 8.5: Geleert wird der Inhalt, nicht der Knoten.
        /// </summary>
        /// <remarks>
        /// Der ganze Unterschied zum Löschen: Wer geleert hat, veröffentlicht
        /// weiter an dieselben Empfänger.
        /// </remarks>
        [Test]
        public async Task APurgedNode_KeepsItsSubscribersAndGoesOn()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var subId = await SubscribeAsync(alice, "abo-33");

            await AskAsync(bob, "pur-2", OwnerIq("pur-2", "set", "purge"));

            var konto = Server.GetAccount($"bob@{Server.Domain}")!;

            // Sofort nachsehen und nicht erst nach der nächsten
            // Veröffentlichung: <b>Ein Veröffentlichen legt den Knoten wieder
            // an</b>, und danach sähe ein gelöschter aus wie ein geleerter.
            // Genau daran ist die erste Fassung dieses Tests vorbeigelaufen -
            // die Mutation, die die Ablage entfernt statt sie zu leeren, hat
            // sie überlebt.
            var gleichDanach = (Existiert: konto.PepNodeExists(Node),
                                Eintraege: konto.GetPepItems(Node).Count);

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "pub-20",
                           PublishIq("pub-20", Node, "20", "<wetter xmlns='urn:example:x'>neu</wetter>"));

            await WaitFor(() => SubIdsIn(ereignisse).Count > 0, "die Zustellung nach dem Leeren");

            Assert.Multiple(() =>
            {

                Assert.That(gleichDanach.Existiert, Is.True,
                            "Den Knoten gibt es weiter - auch bevor wieder etwas darin steht.");

                Assert.That(gleichDanach.Eintraege, Is.Zero,
                            "Und leer ist er.");

                Assert.That(konto.PepSubscriptions(Node).Select(a => a.SubId), Is.EqualTo(new[] { subId }),
                            "Das Abonnement ist geblieben.");

                Assert.That(konto.GetPepItems(Node).Select(e => e.ItemId), Is.EqualTo(new[] { "20" }),
                            "Der alte Eintrag ist fort, der neue steht da.");

            });

        }

        #endregion

        #region ThePurge_IsAnnouncedOnce()

        /// <summary>
        /// Auch das Leeren wird gemeldet - wer etwas abgeholt hat, hält es
        /// sonst für aktuell.
        /// </summary>
        [Test]
        public async Task ThePurge_IsAnnouncedOnce()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "abo-34");
            await SubscribeAsync(alice, "abo-35");

            var ereignisse = CollectEvents(alice);

            await AskAsync(bob, "pur-3", OwnerIq("pur-3", "set", "purge"));

            await WaitFor(() => NodeEventsIn(ereignisse).Count > 0, "die Meldung über das Leeren");

            Assert.That(NodeEventsIn(ereignisse), Is.EqualTo(new[] { ("purge", (String?) Node) }));

        }

        #endregion

        #region PurgingANodeWithoutStorage_IsRejected()

        /// <summary>
        /// XEP-0060, Abschnitt 8.5.3.2: Was nichts aufbewahrt, kann nichts
        /// hergeben.
        /// </summary>
        /// <remarks>
        /// Ein <c>result</c> darauf wäre die Auskunft, es sei etwas geleert
        /// worden - und die Meldung an die Abonnenten die Aufforderung, etwas
        /// wegzuwerfen, das dieser Knoten nie ausgeliefert hat.
        /// </remarks>
        [Test]
        public async Task PurgingANodeWithoutStorage_IsRejected()
        {

            var bob = await ConnectClientAsync("bob");

            await AskAsync(bob, "cr-30",
                           $"<iq type='set' id='cr-30'>" +
                           $"<pubsub xmlns='{PubSubNamespace}'>" +
                           $"<create node='urn:example:fluechtig'/>" +
                           "<configure>" +
                           ConfigForm("<field var='pubsub#persist_items'><value>0</value></field>") +
                           "</configure></pubsub></iq>");

            var abgewiesen = await AskAsync(bob, "pur-4",
                                            OwnerIq("pur-4", "set", "purge", "urn:example:fluechtig"));

            Assert.Multiple(() =>
            {
                Assert.That(ConditionOf(abgewiesen),       Is.EqualTo("feature-not-implemented"));
                Assert.That(ErrorTypeOf(abgewiesen),       Is.EqualTo("cancel"));
                Assert.That(PubSubConditionOf(abgewiesen), Is.EqualTo("unsupported"));
            });

        }

        #endregion

        #region PurgingSomebodyElsesNode_IsForbidden()

        /// <summary>
        /// Und leeren darf ebenfalls nur der Eigentümer.
        /// </summary>
        [Test]
        public async Task PurgingSomebodyElsesNode_IsForbidden()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            var abgewiesen = await AskAsync(alice, "pur-5", OwnerIq("pur-5", "set", "purge"));

            Assert.Multiple(() =>
            {

                Assert.That(ConditionOf(abgewiesen), Is.EqualTo("forbidden"));

                Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.GetPepItems(Node), Is.Not.Empty,
                            "Und der Eintrag steht noch da.");

            });

        }

        #endregion

        #region ADeletedNode_CanBeCreatedAgain_AndIsEmpty()

        /// <summary>
        /// Der Name ist danach wieder frei - und was darin stand, kommt nicht
        /// zurück.
        /// </summary>
        [Test]
        public async Task ADeletedNode_CanBeCreatedAgain_AndIsEmpty()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "del-7", OwnerIq("del-7", "set", "delete"));

            var neu = await AskAsync(bob, "cr-31",
                                     $"<iq type='set' id='cr-31'>" +
                                     $"<pubsub xmlns='{PubSubNamespace}'>" +
                                     $"<create node='{Node}'/></pubsub></iq>");

            var konto = Server.GetAccount($"bob@{Server.Domain}")!;

            Assert.Multiple(() =>
            {
                Assert.That(neu.Attr("type"),          Is.EqualTo("result"),
                            "Kein <conflict/>: Den alten gibt es nicht mehr.");
                Assert.That(konto.PepNodeExists(Node), Is.True);
                Assert.That(konto.GetPepItems(Node),   Is.Empty);
            });

        }

        #endregion

        #region TheAccountApi_NamesWhatTheBanCostHim()

        /// <summary>
        /// Wer die Rolle setzt, erfährt dabei, welche Abonnements sie beendet
        /// hat.
        /// </summary>
        /// <remarks>
        /// Die Auskunft gehört dorthin, wo entfernt wird. Sie sich vorher
        /// selbst zusammenzusuchen hiesse, dieselbe Frage zweimal zu
        /// beantworten - und die zweite Antwort wäre die ungenauere, weil
        /// zwischen Nachsehen und Setzen etwas dazwischenkommen kann.
        /// </remarks>
        [Test]
        public async Task TheAccountApi_NamesWhatTheBanCostHim()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");
            var subId = await SubscribeAsync(alice, "abo-27");
            var konto = Server.GetAccount($"bob@{Server.Domain}")!;

            Assert.Multiple(() =>
            {

                // Zuerst eine Rolle, die nichts beendet - an derselben
                // Abonnentin. Sonst bewiese die leere Liste nur, dass Carol
                // ohnehin nichts hatte.
                Assert.That(konto.SetPepAffiliation(Node, alice.BareJid,
                                                    PubSubAffiliation.Member, out var keine),
                            Is.True);

                Assert.That(keine, Is.Empty,
                            "Jede andere Rolle beendet nichts.");

                Assert.That(konto.PepSubscriptions(Node).Select(a => a.SubId),
                            Is.EqualTo(new[] { subId }),
                            "Und lässt das Abonnement stehen.");

                Assert.That(konto.SetPepAffiliation(Node, alice.BareJid,
                                                    PubSubAffiliation.Outcast, out var erloschen),
                            Is.True);

                Assert.That(erloschen.Select(a => a.SubId), Is.EqualTo(new[] { subId }));

            });

        }

        #endregion

        #region TheAccountApi_RemovesNothingFromANodeThatIsNotThere()

        /// <summary>
        /// Auch unterhalb des Protokolls: Was nicht da ist, wird nicht
        /// entfernt, und die Antwort sagt es.
        /// </summary>
        /// <remarks>
        /// Die Rückgabe ist die Liste der beendeten Abonnements und nicht ihre
        /// Zahl: Wer den Abonnenten benachrichtigen will, muss wissen, welche
        /// Kennung erloschen ist.
        /// </remarks>
        [Test]
        public async Task TheAccountApi_RemovesNothingFromANodeThatIsNotThere()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");
            var konto = Server.GetAccount($"bob@{Server.Domain}")!;

            await SubscribeAsync(alice, "abo-16");

            Assert.Multiple(() =>
            {

                Assert.That(konto.RemovePepSubscriptions("urn:example:nichts", alice.BareJid),
                            Is.Empty);

                Assert.That(konto.RemovePepSubscriptions(Node, $"carol@{Server.Domain}"),
                            Is.Empty);

                Assert.That(konto.RemovePepSubscriptions(Node, alice.BareJid).Select(a => a.Jid),
                            Is.EqualTo(new[] { alice.BareJid }));

                Assert.That(konto.PepSubscriptions(Node), Is.Empty);

            });

        }

        #endregion

    }

}
