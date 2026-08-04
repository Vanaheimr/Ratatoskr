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
                                 String?  subId = null,
                                 String?  form  = null,
                                 String?  jid   = null)

            => $"<iq type='{kind}' to='bob@{Server.Domain}' id='{id}'>" +
               $"<pubsub xmlns='{PubSubNamespace}'>" +
               $"<options node='{Node}' jid='{jid ?? $"alice@{Server.Domain}"}'" +
               (subId is not null ? $" subid='{subId}'" : "") +
               (form is null ? "/>" : $">{form}</options>") +
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
                                   String?  form = null,
                                   String?  node = null)

            => $"<iq type='{kind}' to='bob@{Server.Domain}' id='{id}'>" +
               $"<pubsub xmlns='{OwnerNamespace}'>" +
               $"<configure node='{node ?? Node}'" +
               (form is null ? "/>" : $">{form}</configure>") +
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
        private static String? FormValue(XElement form, String var)
            => form.Children("jabber:x:data", "field")
                   .FirstOrDefault(f => f.Attr("var") == var)
                  ?.Child("jabber:x:data", "value")
                  ?.Value;

        /// <summary>
        /// The answer of the owner to an application (XEP-0060, section
        /// 8.6.2).
        /// </summary>
        private String AuthorizationAnswer(String jid, String subId, Boolean yes, String? to = null)
            => $"<message to='{to ?? $"bob@{Server.Domain}"}'>" +
               "<x xmlns='jabber:x:data' type='submit'>" +
               "<field var='FORM_TYPE'><value>" + PubSubSubscribeAuthorization.FormType + "</value></field>" +
               $"<field var='pubsub#node'><value>{Node}</value></field>" +
               $"<field var='pubsub#subid'><value>{subId}</value></field>" +
               $"<field var='pubsub#subscriber_jid'><value>{jid}</value></field>" +
               $"<field var='pubsub#allow'><value>{(yes ? "1" : "0")}</value></field>" +
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
        /// XEP-0060, section 12.18: <c>pubsub#deliver=0</c> - the subscription
        /// stays, the delivery does not.
        /// </summary>
        /// <remarks>
        /// <b>And it does not fall back on the presence delivery.</b> Whoever
        /// has said they want nothing gets nothing - even when they stand in
        /// the roster on the side. Anything else would mean undercutting a
        /// setting by another way.
        /// </remarks>
        [Test]
        public async Task TurningDeliveryOff_SilencesTheSubscription()
        {

            MakeContacts("alice", "bob");

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-21");

            var set = await AskAsync(alice, "opt-21",
                                     OptionsIq("opt-21", "set",
                                               form: SubmitForm(DeliverField("0"))));

            Assert.That(set.Attr("type"), Is.EqualTo("result"));

            var loaded = await AskAsync(alice, "opt-21b", OptionsIq("opt-21b", "get"));

            Assert.That(FieldValue(loaded, "pubsub#deliver"), Is.EqualTo("0"),
                        "The form has to show what holds, not what was intended.");

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-20",
                           PublishIq("pub-20", Node, "20", "<weather xmlns='urn:example:x'>quiet</weather>"));

            await WaitAgainst(() => Count(events) > 0,
                              "a notification to a silenced subscription");

        }

        #endregion

        #region TurningDeliveryOnAgain_ResumesIt()

        /// <summary>
        /// The cross-check: what can be switched off can also be switched on
        /// again.
        /// </summary>
        /// <remarks>
        /// Without it the previous test would pass against an implementation
        /// that reads every setting as "do not deliver".
        /// </remarks>
        [Test]
        public async Task TurningDeliveryOnAgain_ResumesIt()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-22");

            await AskAsync(alice, "opt-22a",
                           OptionsIq("opt-22a", "set", form: SubmitForm(DeliverField("0"))));

            await AskAsync(alice, "opt-22b",
                           OptionsIq("opt-22b", "set", form: SubmitForm(DeliverField("true"))));

            var loaded = await AskAsync(alice, "opt-22c", OptionsIq("opt-22c", "get"));

            Assert.That(FieldValue(loaded, "pubsub#deliver"), Is.EqualTo("1"),
                        "'true' is a yes as well - XEP-0004 knows both spellings.");

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-21",
                           PublishIq("pub-21", Node, "21", "<weather xmlns='urn:example:x'>back again</weather>"));

            await WaitFor(() => Count(events) > 0, "the notification delivered again");

        }

        #endregion

        #region WithTwoSubscriptions_OnlyTheConfiguredOneGoesQuiet()

        /// <summary>
        /// The reason two subscriptions of the same JID to the same node can
        /// be told apart at all.
        /// </summary>
        /// <remarks>
        /// Up to here two subscriptions were two equal things, and the second
        /// brought nothing but a second delivery. With the configuration per
        /// subscription they get different properties - and only with that is
        /// the <c>subid</c> not merely an id but the address of a setting.
        /// </remarks>
        [Test]
        public async Task WithTwoSubscriptions_OnlyTheConfiguredOneGoesQuiet()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var first  = await SubscribeAsync(alice, "sub-23a");
            var second = await SubscribeAsync(alice, "sub-23b");

            var set = await AskAsync(alice, "opt-23",
                                     OptionsIq("opt-23", "set", first,
                                               SubmitForm(DeliverField("0"))));

            Assert.That(set.Attr("type"), Is.EqualTo("result"));

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-22",
                           PublishIq("pub-22", Node, "22", "<weather xmlns='urn:example:x'>half</weather>"));

            await WaitFor(() => Count(events) > 0, "the notification of the loud subscription");

            await WaitAgainst(() => Count(events) > 1,
                              "a notification of the silenced subscription");

            Assert.That(SubIdsIn(events), Is.EqualTo(new[] { second }),
                        "The wrong one was silenced.");

        }

        #endregion

        #region Options_WithoutASubId_WhenSeveralExist_AreRejected()

        /// <summary>
        /// XEP-0060, section 6.3.3: here too it has to be said which
        /// subscription is meant - only with a different error than with the
        /// unsubscribing.
        /// </summary>
        /// <remarks>
        /// <c>&lt;not-acceptable/&gt;</c> instead of <c>&lt;bad-request/&gt;</c>,
        /// and that is no arbitrariness of the XEP: the request <i>is</i> in
        /// order, it just cannot be answered in this situation. An
        /// implementation that treats both places alike has not read one of
        /// them.
        /// </remarks>
        [Test]
        public async Task Options_WithoutASubId_WhenSeveralExist_AreRejected()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-24a");
            await SubscribeAsync(alice, "sub-24b");

            var reply = await AskAsync(alice, "opt-24", OptionsIq("opt-24", "get"));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"),       Is.EqualTo("error"));
                Assert.That(ConditionOf(reply),       Is.EqualTo("not-acceptable"));
                Assert.That(PubSubConditionOf(reply), Is.EqualTo("subid-required"));
            });

        }

        #endregion

        #region Options_OfANodeNobodySubscribed_AreRejected()

        /// <summary>
        /// Without a subscription there is nothing to set.
        /// </summary>
        [Test]
        public async Task Options_OfANodeNobodySubscribed_AreRejected()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            var reply = await AskAsync(alice, "opt-25", OptionsIq("opt-25", "get"));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"),       Is.EqualTo("error"));
                Assert.That(ConditionOf(reply),       Is.EqualTo("unexpected-request"));
                Assert.That(PubSubConditionOf(reply), Is.EqualTo("not-subscribed"));
            });

        }

        #endregion

        #region Options_ForSomebodyElse_AreRejected()

        /// <summary>
        /// And here too only the one it belongs to may set the <c>jid</c>.
        /// </summary>
        /// <remarks>
        /// The third place with the same check, and the quietest: whoever were
        /// allowed to set foreign subscriptions could switch them off
        /// silently. The subscription would stay standing - only nothing would
        /// arrive any more, and the one concerned would find nothing
        /// conspicuous in their own list.
        /// </remarks>
        [Test]
        public async Task Options_ForSomebodyElse_AreRejected()
        {

            var bob   = await PublishingBobAsync();
            var carol = await ConnectClientAsync("carol");
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(carol, "sub-26");

            var reply = await AskAsync(alice, "opt-26",
                                       OptionsIq("opt-26", "set",
                                                 form: SubmitForm(DeliverField("0")),
                                                 jid:  carol.BareJid));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"),       Is.EqualTo("error"));
                Assert.That(PubSubConditionOf(reply), Is.EqualTo("invalid-jid"));
            });

            var events = CollectEvents(carol);

            await AskAsync(bob, "pub-23",
                           PublishIq("pub-23", Node, "23", "<weather xmlns='urn:example:x'>loud</weather>"));

            await WaitFor(() => Count(events) > 0,
                          "the notification to Carol, whom nobody was allowed to switch off");

        }

        #endregion

        #region AnOptionNobodyOffered_IsRejected()

        /// <summary>
        /// A field that did not stand in the form is refused instead of passed
        /// over.
        /// </summary>
        /// <remarks>
        /// <b>That is stricter than usual and deliberate.</b> A service that
        /// swallows the unknown in silence leaves the subscriber in the belief
        /// that their setting holds - and an absent effect looks like a
        /// mistake somewhere else. Better a refusal one can read.
        /// </remarks>
        [Test]
        public async Task AnOptionNobodyOffered_IsRejected()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-27");

            var reply = await AskAsync(alice, "opt-27",
                                       OptionsIq("opt-27", "set",
                                                 form: SubmitForm(
                                                 "<field var='pubsub#digest'><value>1</value></field>")));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"),       Is.EqualTo("error"));
                Assert.That(ConditionOf(reply),       Is.EqualTo("bad-request"));
                Assert.That(PubSubConditionOf(reply), Is.EqualTo("invalid-options"));
            });

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-24",
                           PublishIq("pub-24", Node, "24", "<weather xmlns='urn:example:x'>unchanged</weather>"));

            await WaitFor(() => Count(events) > 0,
                          "the notification - a refused setting changes nothing");

        }

        #endregion

        #region ASetWithoutAForm_IsRejected()

        /// <summary>
        /// A <c>set</c> without a form does not say what is to be set.
        /// </summary>
        /// <remarks>
        /// To put in the defaults would be the friendly reading and the
        /// dangerous one: an incomplete request would turn into a change
        /// nobody demanded - and it would hit precisely the one who had just
        /// set something else.
        /// </remarks>
        [Test]
        public async Task ASetWithoutAForm_IsRejected()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-29");

            await AskAsync(alice, "opt-29a",
                           OptionsIq("opt-29a", "set", form: SubmitForm(DeliverField("0"))));

            var reply = await AskAsync(alice, "opt-29b", OptionsIq("opt-29b", "set"));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"),       Is.EqualTo("error"));
                Assert.That(PubSubConditionOf(reply), Is.EqualTo("invalid-options"));
            });

            var loaded = await AskAsync(alice, "opt-29c", OptionsIq("opt-29c", "get"));

            Assert.That(FieldValue(loaded, "pubsub#deliver"), Is.EqualTo("0"),
                        "A refused request must not have reset anything.");

        }

        #endregion

        #region AFormThatIsNotSubmitted_IsRejected()

        /// <summary>
        /// XEP-0004: what comes back has to be a <c>submit</c>.
        /// </summary>
        /// <remarks>
        /// A <c>form</c> sent back is the offer and no answer. To accept it
        /// would mean taking the proposal of the service for the will of the
        /// subscriber.
        /// </remarks>
        [Test]
        public async Task AFormThatIsNotSubmitted_IsRejected()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "sub-28");

            var reply = await AskAsync(alice, "opt-28",
                                       OptionsIq("opt-28", "set",
                                                 form: SubmitForm(DeliverField("0"), "form")));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"),       Is.EqualTo("error"));
                Assert.That(PubSubConditionOf(reply), Is.EqualTo("invalid-options"));
            });

        }

        #endregion

        #region APresenceDrivenNotification_CarriesNoSubId()

        /// <summary>
        /// Whoever is notified over presence alone gets no id - there is none.
        /// </summary>
        /// <remarks>
        /// XEP-0060, section 12.20 demands the id <i>when</i> there are
        /// several subscriptions. To send along a made-up one would be worse
        /// than none: the receiver could afterwards want to unsubscribe from
        /// what was never subscribed to.
        /// </remarks>
        [Test]
        public async Task APresenceDrivenNotification_CarriesNoSubId()
        {

            MakeContacts("alice", "bob");

            var bob    = await PublishingBobAsync();
            var alice  = await ConnectClientAsync("alice");
            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-8",
                           PublishIq("pub-8", Node, "8", "<weather xmlns='urn:example:x'>haze</weather>"));

            await WaitFor(() => Count(events) > 0, "the notification to the contact");

            Assert.That(SubIdsIn(events), Is.Empty);

        }

        #endregion


        #region TheSubscriberList_NamesEverybodyWithHisSubId()

        /// <summary>
        /// XEP-0060, section 8.8.1: who hangs on the node - with an id, and
        /// the same JID several times if they have subscribed several times.
        /// </summary>
        /// <remarks>
        /// The id is no ornament here. Without it Alice would stand there
        /// twice alike, and the owner could not tell one of her subscriptions
        /// from the other - so could end neither of them on its own.
        /// </remarks>
        [Test]
        public async Task TheSubscriberList_NamesEverybodyWithHisSubId()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            var first  = await SubscribeAsync(alice, "abo-1");
            var second = await SubscribeAsync(alice, "abo-2");
            var third  = await SubscribeAsync(carol, "abo-3");

            var list = await AskAsync(bob, "subm-1", NodeSubscriptionsIq("subm-1", "get"));

            var entries = SubscriptionsIn(list, OwnerNamespace);

            Assert.Multiple(() =>
            {

                Assert.That(entries.Select(e => (e.Attr("jid"), e.Attr("subid"))),
                            Is.EquivalentTo(new[] {
                                ($"alice@{Server.Domain}", first),
                                ($"alice@{Server.Domain}", second),
                                ($"carol@{Server.Domain}", third)
                            }));

                Assert.That(entries.Select(e => e.Attr("subscription")).Distinct(),
                            Is.EqualTo(new[] { "subscribed" }),
                            "Without an approval procedure every recorded subscription is a subscribed one.");

            });

        }

        #endregion

        #region TheSubscriberList_IsOnlyForTheOwner()

        /// <summary>
        /// The list says who is interested in Bob's node - and that is nobody's
        /// business but Bob's.
        /// </summary>
        /// <remarks>
        /// <b>The difference from section 5.6.</b> There the server keeps
        /// foreign subscriptions to itself, because they would be information
        /// about human beings. Here it hands them out, because the question is
        /// another one: not "where does this human being hang everywhere" but
        /// "who hangs on my node". Whoever publishes has to be allowed to know
        /// where it goes.
        /// </remarks>
        [Test]
        public async Task TheSubscriberList_IsOnlyForTheOwner()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "abo-4");

            var reply = await AskAsync(alice, "subm-2", NodeSubscriptionsIq("subm-2", "get"));

            Assert.Multiple(() =>
            {
                Assert.That(reply.Attr("type"), Is.EqualTo("error"));
                Assert.That(ConditionOf(reply), Is.EqualTo("forbidden"));
            });

        }

        #endregion

        #region TheSubscriberList_OfANodeThatIsNotThere_IsRejected()

        /// <summary>
        /// A node that does not exist has no empty subscriber list - it has
        /// none at all.
        /// </summary>
        [Test]
        public async Task TheSubscriberList_OfANodeThatIsNotThere_IsRejected()
        {

            var bob = await PublishingBobAsync();

            var invented = await AskAsync(bob, "subm-3",
                                          NodeSubscriptionsIq("subm-3", "get", node: "urn:example:nothing"));

            var withoutNode = await AskAsync(bob, "subm-4",
                                             $"<iq type='get' to='bob@{Server.Domain}' id='subm-4'>" +
                                             $"<pubsub xmlns='{OwnerNamespace}'><subscriptions/></pubsub></iq>");

            Assert.Multiple(() =>
            {
                Assert.That(ConditionOf(invented),    Is.EqualTo("item-not-found"));
                Assert.That(ConditionOf(withoutNode), Is.EqualTo("bad-request"),
                            "Without a node name the question is incomplete, not unanswerable.");
            });

        }

        #endregion

        #region TheOwner_RemovesASubscriber_AndTheEventsStop()

        /// <summary>
        /// XEP-0060, section 8.8.2: <c>subscription='none'</c> ends the
        /// subscription without the subscriber having been asked.
        /// </summary>
        /// <remarks>
        /// Unlike the lockout over <c>outcast</c>: that one bars for good, this
        /// only takes away what exists right now. Alice may subscribe again
        /// afterwards - the owner has removed her, not locked her out.
        /// </remarks>
        [Test]
        public async Task TheOwner_RemovesASubscriber_AndTheEventsStop()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "abo-5");

            var removed = await AskAsync(bob, "subm-5",
                                         NodeSubscriptionsIq("subm-5", "set",
                                                             SubscriberEntry(alice.BareJid, "none")));

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-9",
                           PublishIq("pub-9", Node, "9", "<weather xmlns='urn:example:x'>frost</weather>"));

            await WaitAgainst(() => Count(events) > 0,
                              "a notification to the removed subscriber");

            var list = await AskAsync(bob, "subm-6", NodeSubscriptionsIq("subm-6", "get"));

            Assert.Multiple(() =>
            {
                Assert.That(removed.Attr("type"),                  Is.EqualTo("result"));
                Assert.That(SubscriptionsIn(list, OwnerNamespace), Is.Empty);
            });

            var again = await SubscribeAsync(alice, "abo-6");

            Assert.That(again, Is.Not.Empty,
                        "Removed is not locked out: Alice may subscribe again.");

        }

        #endregion

        #region WithoutASubId_TheWholeSubscriberGoes()

        /// <summary>
        /// Without an id the owner means the human being and not one of their
        /// subscriptions.
        /// </summary>
        /// <remarks>
        /// <b>And that is no contradiction to section 6.2.3.1.</b> There the
        /// subscriber has to say which one they mean, because the others are
        /// meant to stay theirs. To leave one standing here would mean carrying
        /// out the instruction by half - the removed one would go on getting
        /// everything.
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

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-10",
                           PublishIq("pub-10", Node, "10", "<weather xmlns='urn:example:x'>hail</weather>"));

            await WaitAgainst(() => Count(events) > 0,
                              "a notification to the subscription left over");

            var list = await AskAsync(bob, "subm-8", NodeSubscriptionsIq("subm-8", "get"));

            Assert.That(SubscriptionsIn(list, OwnerNamespace), Is.Empty);

        }

        #endregion

        #region RemovingOne_LeavesTheOthers()

        /// <summary>
        /// Whoever removes one removes one - and not the node empty.
        /// </summary>
        /// <remarks>
        /// The self-evident thing one has to check: the owner does not notice a
        /// subscriber removed too many. The one concerned notices it and does
        /// not know why.
        /// </remarks>
        [Test]
        public async Task RemovingOne_LeavesTheOthers()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            await SubscribeAsync(alice, "abo-17");

            var carols = await SubscribeAsync(carol, "abo-18");

            await AskAsync(bob, "subm-21",
                           NodeSubscriptionsIq("subm-21", "set",
                                               SubscriberEntry(alice.BareJid, "none")));

            var atCarol = CollectEvents(carol);

            await AskAsync(bob, "pub-14",
                           PublishIq("pub-14", Node, "14", "<weather xmlns='urn:example:x'>wind</weather>"));

            await WaitFor(() => Count(atCarol) > 0, "the notification to the other subscriber");

            var list = await AskAsync(bob, "subm-22", NodeSubscriptionsIq("subm-22", "get"));

            Assert.That(SubscriptionsIn(list, OwnerNamespace).Select(e => (e.Attr("jid"), e.Attr("subid"))),
                        Is.EqualTo(new[] { ($"carol@{Server.Domain}", carols) }));

        }

        #endregion

        #region WithASubId_OnlyThatOneGoes()

        /// <summary>
        /// With an id exactly one goes - the other goes on delivering.
        /// </summary>
        [Test]
        public async Task WithASubId_OnlyThatOneGoes()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var first  = await SubscribeAsync(alice, "abo-9");
            var second = await SubscribeAsync(alice, "abo-10");

            await AskAsync(bob, "subm-9",
                           NodeSubscriptionsIq("subm-9", "set",
                                               SubscriberEntry(alice.BareJid, "none", first)));

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-11",
                           PublishIq("pub-11", Node, "11", "<weather xmlns='urn:example:x'>fog</weather>"));

            await WaitFor(() => Count(events) > 0, "the notification to the second subscription");

            var list = await AskAsync(bob, "subm-10", NodeSubscriptionsIq("subm-10", "get"));

            Assert.Multiple(() =>
            {

                Assert.That(SubIdsIn(events), Is.EqualTo(new[] { second }),
                            "The subscription that stayed is the one delivering.");

                Assert.That(SubscriptionsIn(list, OwnerNamespace).Select(e => e.Attr("subid")),
                            Is.EqualTo(new[] { second }));

            });

        }

        #endregion

        #region RemovingSomebodyWhoIsNotThere_IsRejected()

        /// <summary>
        /// What nobody finds is not ended either.
        /// </summary>
        /// <remarks>
        /// To agree in silence would mean reporting the success of an
        /// instruction that went nowhere. A typo in the JID, and the owner
        /// would hold somebody to be removed who goes on getting everything -
        /// the same confusion as everywhere in this series, only this time from
        /// the comfortable side.
        /// </remarks>
        [Test]
        public async Task RemovingSomebodyWhoIsNotThere_IsRejected()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var subId = await SubscribeAsync(alice, "abo-11");

            var foreign = await AskAsync(bob, "subm-11",
                                         NodeSubscriptionsIq("subm-11", "set",
                                                             SubscriberEntry($"carol@{Server.Domain}", "none")));

            var wrong = await AskAsync(bob, "subm-12",
                                       NodeSubscriptionsIq("subm-12", "set",
                                                           SubscriberEntry(alice.BareJid, "none", "doesnotexist")));

            var list = await AskAsync(bob, "subm-13", NodeSubscriptionsIq("subm-13", "get"));

            Assert.Multiple(() =>
            {

                Assert.That(ConditionOf(foreign), Is.EqualTo("item-not-found"),
                            "Carol has never subscribed.");

                Assert.That(ConditionOf(wrong),   Is.EqualTo("item-not-found"),
                            "And this id belongs to no subscription.");

                Assert.That(SubscriptionsIn(list, OwnerNamespace).Select(e => e.Attr("subid")),
                            Is.EqualTo(new[] { subId }),
                            "Alice's subscription stands there untouched.");

            });

        }

        #endregion

        #region TheOwner_CannotEnrolSomebody()

        /// <summary>
        /// The owner may take away and not give.
        /// </summary>
        /// <remarks>
        /// XEP-0060, section 8.8.2 lets them sign somebody up as well; this
        /// server does not. To record somebody who has not asked is exactly
        /// what section 6.1.3.1 prevents on the other side - and that it is
        /// one's own node changes nothing for the one whose inbox fills up.
        /// </remarks>
        [Test]
        public async Task TheOwner_CannotEnrolSomebody()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var refused = await AskAsync(bob, "subm-14",
                                         NodeSubscriptionsIq("subm-14", "set",
                                                             SubscriberEntry(alice.BareJid, "subscribed")));

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-12",
                           PublishIq("pub-12", Node, "12", "<weather xmlns='urn:example:x'>storm</weather>"));

            await WaitAgainst(() => Count(events) > 0,
                              "a notification to somebody signed up unasked");

            Assert.Multiple(() =>
            {
                Assert.That(ConditionOf(refused), Is.EqualTo("not-allowed"));
                Assert.That(ErrorTypeOf(refused), Is.EqualTo("cancel"));
            });

        }

        #endregion

        #region TheListCanBeSentBackUnchanged()

        /// <summary>
        /// What the server hands out as the state it also takes back.
        /// </summary>
        /// <remarks>
        /// A list that cannot be sent back unchanged would be no state but a
        /// form. <c>subscribed</c> for an existing subscription is no
        /// instruction but a confirmation - and accordingly changes nothing.
        /// </remarks>
        [Test]
        public async Task TheListCanBeSentBackUnchanged()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var subId = await SubscribeAsync(alice, "abo-12");

            var events = CollectEvents(alice);

            var back = await AskAsync(bob, "subm-15",
                                      NodeSubscriptionsIq("subm-15", "set",
                                                          SubscriberEntry(alice.BareJid, "subscribed", subId)));

            await AskAsync(bob, "pub-13",
                           PublishIq("pub-13", Node, "13", "<weather xmlns='urn:example:x'>dew</weather>"));

            await WaitFor(() => ItemIdsIn(events).Count > 0,
                          "the notification to the confirmed subscription");

            Assert.Multiple(() =>
            {

                Assert.That(back.Attr("type"), Is.EqualTo("result"));

                // A confirmation announces nothing: nothing has changed. Only
                // with the approval procedure from D93 can the same
                // 'subscribed' be a grant - and then it does announce itself.
                Assert.That(events.Any(e => e.Contains("<subscription", StringComparison.Ordinal)),
                            Is.False,
                            "A confirmation is no change and does not announce itself.");

            });

        }

        #endregion

        #region AnUnknownState_IsRejectedAndChangesNothing()

        /// <summary>
        /// An instruction is read strictly: what is no state name has no
        /// effect.
        /// </summary>
        /// <remarks>
        /// The answer of a service is read leniently - the unknown counts there
        /// as "not subscribed", the safe assumption. Here precisely not: if the
        /// unknown were a <c>none</c> here too, a typo would end a
        /// subscription.
        /// </remarks>
        [Test]
        public async Task AnUnknownState_IsRejectedAndChangesNothing()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var subId = await SubscribeAsync(alice, "abo-13");

            var nonsense = await AskAsync(bob, "subm-16",
                                          NodeSubscriptionsIq("subm-16", "set",
                                                              SubscriberEntry(alice.BareJid, "nonw")));

            var pending = await AskAsync(bob, "subm-17",
                                         NodeSubscriptionsIq("subm-17", "set",
                                                             SubscriberEntry(alice.BareJid, "pending", subId)));

            var list = await AskAsync(bob, "subm-18", NodeSubscriptionsIq("subm-18", "get"));

            Assert.Multiple(() =>
            {

                Assert.That(ConditionOf(nonsense), Is.EqualTo("bad-request"),
                            "No state name - and very nearly one.");

                Assert.That(ConditionOf(pending),  Is.EqualTo("not-allowed"),
                            "A state name, but not one this server can bring about.");

                Assert.That(SubscriptionsIn(list, OwnerNamespace).Select(e => e.Attr("subid")),
                            Is.EqualTo(new[] { subId }));

            });

        }

        #endregion

        #region HalfAnInstruction_IsNoInstruction()

        /// <summary>
        /// Check everything first, then carry out everything: one faulty entry
        /// discards the valid ones before it as well.
        /// </summary>
        /// <remarks>
        /// An instruction that holds by half would be worse than one refused
        /// entirely - the sender would not know which half.
        /// </remarks>
        [Test]
        public async Task HalfAnInstruction_IsNoInstruction()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            var alices = await SubscribeAsync(alice, "abo-14");
            var carols = await SubscribeAsync(carol, "abo-15");

            var refused = await AskAsync(bob, "subm-19",
                                         NodeSubscriptionsIq("subm-19", "set",
                                                             SubscriberEntry(alice.BareJid, "none") +
                                                             SubscriberEntry(carol.BareJid, "maybe")));

            var list = await AskAsync(bob, "subm-20", NodeSubscriptionsIq("subm-20", "get"));

            Assert.Multiple(() =>
            {

                Assert.That(ConditionOf(refused), Is.EqualTo("bad-request"));

                Assert.That(SubscriptionsIn(list, OwnerNamespace).Select(e => e.Attr("subid")),
                            Is.EquivalentTo(new[] { alices, carols }),
                            "Alice's subscription still stands there too - the check came before the first step.");

            });

        }

        #endregion

        #region TheRemovedSubscriber_IsTold()

        /// <summary>
        /// XEP-0060, section 8.8.4: whoever was ended without being asked
        /// learns of it.
        /// </summary>
        /// <remarks>
        /// Otherwise they wait for events that no longer come - the state
        /// <c>PubSubSubscriptionState</c> has described as the worse one since
        /// D71. The id belongs to it: with several subscriptions it is the only
        /// thing by which the receiver can tell which one has ended.
        /// </remarks>
        [Test]
        public async Task TheRemovedSubscriber_IsTold()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var subId = await SubscribeAsync(alice, "abo-19");

            var events = CollectEvents(alice);

            await AskAsync(bob, "subm-23",
                           NodeSubscriptionsIq("subm-23", "set",
                                               SubscriberEntry(alice.BareJid, "none")));

            await WaitFor(() => EndingsIn(events).Count > 0, "the notice to the removed one");

            Assert.Multiple(() =>
            {

                Assert.That(EndingsIn(events),
                            Is.EqualTo(new[] { ((String?) Node, (String?) alice.BareJid, (String?) subId) }));

                Assert.That(events[0], Does.Contain($"from='bob@{Server.Domain}'"),
                            "It comes from the account the node belongs to - otherwise the " +
                            "spoofing protection of the receiver rightly throws it away.");

            });

        }

        #endregion

        #region EveryEndedSubscription_IsAnnouncedOnce()

        /// <summary>
        /// One notice per ended subscription, not one per instruction.
        /// </summary>
        /// <remarks>
        /// A <c>none</c> without an id ends all subscriptions of that JID. If
        /// only one notice came on it, the receiver would know of one id that
        /// it has ended, and nothing of the other.
        /// </remarks>
        [Test]
        public async Task EveryEndedSubscription_IsAnnouncedOnce()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var first  = await SubscribeAsync(alice, "abo-28");
            var second = await SubscribeAsync(alice, "abo-29");

            var events = CollectEvents(alice);

            await AskAsync(bob, "subm-28",
                           NodeSubscriptionsIq("subm-28", "set",
                                               SubscriberEntry(alice.BareJid, "none")));

            await WaitFor(() => EndingsIn(events).Count > 1, "both notices");

            Assert.That(EndingsIn(events).Select(e => e.SubId),
                        Is.EquivalentTo(new[] { first, second }));

        }

        #endregion

        #region TheOutcast_IsToldToo()

        /// <summary>
        /// The lockout ends subscriptions too (section 8.9.4) - and the one
        /// concerned learns of that as well.
        /// </summary>
        /// <remarks>
        /// <b>The lockout itself stays hidden from them.</b> What they are at
        /// this node is none of their business; that they no longer get it, is.
        /// Two different pieces of information, and the server owes them only
        /// the second.
        /// </remarks>
        [Test]
        public async Task TheOutcast_IsToldToo()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var subId = await SubscribeAsync(alice, "abo-20");

            var events = CollectEvents(alice);

            await AskAsync(bob, "aff-20",
                           AffiliationsIq("aff-20", "set",
                                          $"<affiliation jid='{alice.BareJid}' affiliation='outcast'/>"));

            await WaitFor(() => EndingsIn(events).Count > 0, "the notice to the one locked out");

            Assert.Multiple(() =>
            {

                Assert.That(EndingsIn(events),
                            Is.EqualTo(new[] { ((String?) Node, (String?) alice.BareJid, (String?) subId) }));

                Assert.That(events.Any(e => e.Contains("outcast", StringComparison.Ordinal)),
                            Is.False,
                            "Their role does not stand in it.");

            });

        }

        #endregion

        #region OnlyTheEndedOne_IsAnnounced()

        /// <summary>
        /// What is announced is what has ended - not what the owner wrote down.
        /// </summary>
        [Test]
        public async Task OnlyTheEndedOne_IsAnnounced()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var first = await SubscribeAsync(alice, "abo-21");

            await SubscribeAsync(alice, "abo-22");

            var events = CollectEvents(alice);

            await AskAsync(bob, "subm-24",
                           NodeSubscriptionsIq("subm-24", "set",
                                               SubscriberEntry(alice.BareJid, "none", first)));

            await WaitFor(() => EndingsIn(events).Count > 0, "the notice about the one subscription");

            await AskAsync(bob, "subm-25", NodeSubscriptionsIq("subm-25", "get"));

            Assert.That(EndingsIn(events).Select(e => e.SubId),
                        Is.EqualTo(new[] { first }),
                        "Exactly one has ended, so exactly one notice comes.");

        }

        #endregion

        #region NobodyElse_IsTold()

        /// <summary>
        /// The notice goes to the one concerned and to nobody else.
        /// </summary>
        /// <remarks>
        /// Whoever got it along would learn who has left the node - and the
        /// owner would get it a second time as the answer to their own
        /// instruction.
        /// </remarks>
        [Test]
        public async Task NobodyElse_IsTold()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            await SubscribeAsync(alice, "abo-23");
            await SubscribeAsync(carol, "abo-24");

            var atAlice = CollectEvents(alice);
            var atCarol = CollectEvents(carol);
            var atBob   = CollectEvents(bob);

            await AskAsync(bob, "subm-26",
                           NodeSubscriptionsIq("subm-26", "set",
                                               SubscriberEntry(alice.BareJid, "none")));

            await WaitFor(() => EndingsIn(atAlice).Count > 0, "the notice to the removed one");

            await WaitAgainst(() => EndingsIn(atCarol).Count > 0 || EndingsIn(atBob).Count > 0,
                              "a notice to the uninvolved");

        }

        #endregion

        #region AnUnsuccessfulRemoval_AnnouncesNothing()

        /// <summary>
        /// A refused instruction signs nothing off.
        /// </summary>
        /// <remarks>
        /// Otherwise the notice would hang on what somebody wrote down and not
        /// on what happened: Alice would get the notice about a subscription
        /// she still has.
        /// </remarks>
        [Test]
        public async Task AnUnsuccessfulRemoval_AnnouncesNothing()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "abo-25");

            var events = CollectEvents(alice);

            var refused = await AskAsync(bob, "subm-27",
                                         NodeSubscriptionsIq("subm-27", "set",
                                                             SubscriberEntry(alice.BareJid, "none", "doesnotexist")));

            await WaitAgainst(() => EndingsIn(events).Count > 0,
                              "a notice without an ended subscription");

            Assert.That(ConditionOf(refused), Is.EqualTo("item-not-found"));

        }

        #endregion

        #region WhoUnsubscribesHimself_IsNotTold()

        /// <summary>
        /// Whoever unsubscribes themselves gets no notice.
        /// </summary>
        /// <remarks>
        /// They already have the answer: the <c>result</c> to their own
        /// <c>unsubscribe</c>. A second piece of information about it would be
        /// no message but an echo.
        /// </remarks>
        [Test]
        public async Task WhoUnsubscribesHimself_IsNotTold()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "abo-26");

            var events = CollectEvents(alice);

            var reply = await AskAsync(alice, "unsub-20",
                                       PubSubBuilder.Unsubscribe($"bob@{Server.Domain}",
                                                                 Node,
                                                                 alice.BareJid,
                                                                 "unsub-20"));

            await WaitAgainst(() => EndingsIn(events).Count > 0,
                              "a notice to the one who unsubscribed themselves");

            Assert.That(reply.Attr("type"), Is.EqualTo("result"));

        }

        #endregion

        #region WithAuthorize_ASubscriptionIsOnlyARequest()

        /// <summary>
        /// XEP-0060, section 6.1.3.7: on a node with an approval procedure the
        /// answer is a <c>pending</c>.
        /// </summary>
        /// <remarks>
        /// <b>The only model where subscribing and getting in are two
        /// things.</b> Anybody may ask - the asking is the procedure - and
        /// what they get is the accepted question and not the grant. Whoever
        /// read it as a grant would wait for events somebody has to release
        /// first.
        /// </remarks>
        [Test]
        public async Task WithAuthorize_ASubscriptionIsOnlyARequest()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-60",
                           ConfigureIq("cfg-60", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var alice = await ConnectClientAsync("alice");

            var grant = await AskAsync(alice, "sub-60",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                               alice.BareJid, "sub-60"));

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-60",
                           PublishIq("pub-60", Node, "60", "<weather xmlns='urn:example:x'>bright</weather>"));

            await WaitAgainst(() => Count(events) > 0,
                              "a delivery to an applied-for subscription");

            var fetching = await AskAsync(alice, "get-60",
                                          PubSubBuilder.GetItems($"bob@{Server.Domain}", Node, id: "get-60"));

            Assert.Multiple(() =>
            {

                Assert.That(grant.Attr("type"),                   Is.EqualTo("result"),
                            "Anybody may ask.");

                Assert.That(SubscriptionOf(grant)?.Attr("subscription"), Is.EqualTo("pending"),
                            "But the answer is the accepted question.");

                Assert.That(ConditionOf(fetching),                Is.EqualTo("not-authorized"),
                            "And they cannot fetch anything either.");

            });

        }

        #endregion

        #region TheOwner_SeesWhoIsWaiting_AndApproves()

        /// <summary>
        /// XEP-0060, section 8.6: the owner sees the application in their
        /// subscriber list and grants it.
        /// </summary>
        /// <remarks>
        /// In D84 it stood at the list that the state was fixed there in the
        /// text, and that this would be one of the places needing a real one as
        /// soon as <c>authorize</c> existed. That is exactly how it came about.
        /// </remarks>
        [Test]
        public async Task TheOwner_SeesWhoIsWaiting_AndApproves()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-61",
                           ConfigureIq("cfg-61", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var alice = await ConnectClientAsync("alice");

            var grant = await AskAsync(alice, "sub-61",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                               alice.BareJid, "sub-61"));

            var subId  = SubscriptionOf(grant)!.Attr("subid")!;

            var waiting = await AskAsync(bob, "subm-60", NodeSubscriptionsIq("subm-60", "get"));

            var atAlice = CollectEvents(alice);

            var approved = await AskAsync(bob, "subm-61",
                                          NodeSubscriptionsIq("subm-61", "set",
                                                              SubscriberEntry(alice.BareJid, "subscribed", subId)));

            await WaitFor(() => Count(atAlice) > 0, "the grant to the one waiting");

            var afterwards = await AskAsync(bob, "subm-62", NodeSubscriptionsIq("subm-62", "get"));

            Assert.Multiple(() =>
            {

                Assert.That(SubscriptionsIn(waiting, OwnerNamespace).Select(e => e.Attr("subscription")),
                            Is.EqualTo(new[] { "pending" }),
                            "The owner sees who is waiting.");

                Assert.That(approved.Attr("type"), Is.EqualTo("result"));

                Assert.That(atAlice[0], Does.Contain("subscription='subscribed'"),
                            "And the one waiting learns of the grant.");

                Assert.That(SubscriptionsIn(afterwards, OwnerNamespace).Select(e => e.Attr("subscription")),
                            Is.EqualTo(new[] { "subscribed" }));

            });

        }

        #endregion

        #region AfterTheApproval_TheItemsArrive()

        /// <summary>
        /// First the grant, then the delivery - and then the fetching as well.
        /// </summary>
        [Test]
        public async Task AfterTheApproval_TheItemsArrive()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-62",
                           ConfigureIq("cfg-62", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var alice = await ConnectClientAsync("alice");

            var grant = await AskAsync(alice, "sub-62",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                               alice.BareJid, "sub-62"));

            await AskAsync(bob, "subm-63",
                           NodeSubscriptionsIq("subm-63", "set",
                                               SubscriberEntry(alice.BareJid, "subscribed",
                                                               SubscriptionOf(grant)!.Attr("subid"))));

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-61",
                           PublishIq("pub-61", Node, "61", "<weather xmlns='urn:example:x'>at last</weather>"));

            await WaitFor(() => ItemIdsIn(events).Count > 0, "the delivery after the grant");

            var fetching = await AskAsync(alice, "get-61",
                                          PubSubBuilder.GetItems($"bob@{Server.Domain}", Node, id: "get-61"));

            Assert.Multiple(() =>
            {
                Assert.That(ItemIdsIn(events),      Is.EqualTo(new[] { "61" }));
                Assert.That(fetching.Attr("type"),  Is.EqualTo("result"));
            });

        }

        #endregion

        #region ADeniedRequest_IsEndedAndAnnounced()

        /// <summary>
        /// The denial is the same instruction as the removing - and tells the
        /// one waiting.
        /// </summary>
        /// <remarks>
        /// Without the notice they would go on waiting for an answer that had
        /// already come. That is the same reason as in D85, only from the other
        /// side: whoever hears nothing holds the question to be open.
        /// </remarks>
        [Test]
        public async Task ADeniedRequest_IsEndedAndAnnounced()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-63",
                           ConfigureIq("cfg-63", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var alice = await ConnectClientAsync("alice");

            var grant = await AskAsync(alice, "sub-63",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                               alice.BareJid, "sub-63"));

            var subId  = SubscriptionOf(grant)!.Attr("subid")!;

            var atAlice = CollectEvents(alice);

            await AskAsync(bob, "subm-64",
                           NodeSubscriptionsIq("subm-64", "set",
                                               SubscriberEntry(alice.BareJid, "none", subId)));

            await WaitFor(() => EndingsIn(atAlice).Count > 0, "the denial to the one waiting");

            var afterwards = await AskAsync(bob, "subm-65", NodeSubscriptionsIq("subm-65", "get"));

            Assert.Multiple(() =>
            {

                Assert.That(EndingsIn(atAlice).Select(e => e.SubId), Is.EqualTo(new[] { subId }));

                Assert.That(SubscriptionsIn(afterwards, OwnerNamespace), Is.Empty,
                            "The application is gone and does not stand about as a denied one.");

            });

        }

        #endregion

        #region OnAnAuthorizeNode_APresenceContactGetsNothing()

        /// <summary>
        /// The delivery in passing asks the access model as well.
        /// </summary>
        /// <remarks>
        /// <b>Until D93 it did not.</b> A contact got every publication over
        /// the presence - even from a node whose model barred them from
        /// fetching. The model held the door shut and let through the event
        /// that holds the item in full; with <c>authorize</c> the approval
        /// would thereby have been a mere formality.
        /// </remarks>
        [Test]
        public async Task OnAnAuthorizeNode_APresenceContactGetsNothing()
        {

            MakeContacts("carol", "bob");

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-64",
                           ConfigureIq("cfg-64", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var carol  = await ConnectClientAsync("carol");
            var events = CollectEvents(carol);

            await AskAsync(bob, "pub-62",
                           PublishIq("pub-62", Node, "62", "<weather xmlns='urn:example:x'>quiet</weather>"));

            await WaitAgainst(() => Count(events) > 0,
                              "a delivery to a contact without a grant");

        }

        #endregion

        #region TheOwner_IsAskedWithAForm()

        /// <summary>
        /// XEP-0060, section 8.6.1: the owner is presented with the
        /// application without having to look.
        /// </summary>
        /// <remarks>
        /// The preset of <c>pubsub#allow</c> is <c>false</c>: a form that
        /// already stands on "yes" would turn clicking it away into a grant.
        /// </remarks>
        [Test]
        public async Task TheOwner_IsAskedWithAForm()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-70",
                           ConfigureIq("cfg-70", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var atBob = CollectRaw(bob, PubSubSubscribeAuthorization.FormType);

            var alice = await ConnectClientAsync("alice");

            var grant = await AskAsync(alice, "sub-70",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                               alice.BareJid, "sub-70"));

            await WaitFor(() => Count(atBob) > 0, "the application at the owner");

            var form = XElement.Parse(atBob[0]).Child("jabber:x:data", "x")!;

            Assert.Multiple(() =>
            {

                Assert.That(FormValue(form, PubSubSubscribeAuthorization.NodeVariable),
                            Is.EqualTo(Node));

                Assert.That(FormValue(form, PubSubSubscribeAuthorization.SubscriberVariable),
                            Is.EqualTo(alice.BareJid));

                Assert.That(FormValue(form, PubSubSubscribeAuthorization.SubIdVariable),
                            Is.EqualTo(SubscriptionOf(grant)!.Attr("subid")));

                Assert.That(FormValue(form, PubSubSubscribeAuthorization.AllowVariable),
                            Is.EqualTo("0"),
                            "A form that already stands on yes turns clicking it away into a grant.");

            });

        }

        #endregion

        #region TheReturnedForm_ApprovesTheRequest()

        /// <summary>
        /// XEP-0060, section 8.6.2: the form sent back grants - and does the
        /// same as the subscriber list.
        /// </summary>
        /// <remarks>
        /// <b>Two doors into the same room.</b> The list is the view of an
        /// administrator, the form the view of a human being whose client shows
        /// them a question. To put a form nobody can answer would be worse than
        /// none: the human being would approve something, and nothing would
        /// happen.
        /// </remarks>
        [Test]
        public async Task TheReturnedForm_ApprovesTheRequest()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-71",
                           ConfigureIq("cfg-71", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var alice = await ConnectClientAsync("alice");

            var grant = await AskAsync(alice, "sub-71",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                               alice.BareJid, "sub-71"));

            var subId  = SubscriptionOf(grant)!.Attr("subid")!;

            var atAlice = CollectEvents(alice);

            await bob.SendRawAsync(AuthorizationAnswer(alice.BareJid, subId, yes: true));

            await WaitFor(() => atAlice.Any(e => e.Contains("subscription='subscribed'", StringComparison.Ordinal)),
                          "the grant to the one waiting");

            var list = await AskAsync(bob, "subm-70", NodeSubscriptionsIq("subm-70", "get"));

            Assert.That(SubscriptionsIn(list, OwnerNamespace).Select(e => e.Attr("subscription")),
                        Is.EqualTo(new[] { "subscribed" }));

        }

        #endregion

        #region TheReturnedForm_DeniesTheRequest()

        /// <summary>
        /// And a "no" ends the application - the same notice as a removal.
        /// </summary>
        [Test]
        public async Task TheReturnedForm_DeniesTheRequest()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-72",
                           ConfigureIq("cfg-72", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var alice = await ConnectClientAsync("alice");

            var grant = await AskAsync(alice, "sub-72",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                               alice.BareJid, "sub-72"));

            var subId  = SubscriptionOf(grant)!.Attr("subid")!;

            var atAlice = CollectEvents(alice);

            await bob.SendRawAsync(AuthorizationAnswer(alice.BareJid, subId, yes: false));

            await WaitFor(() => EndingsIn(atAlice).Count > 0, "the denial to the one waiting");

            var list = await AskAsync(bob, "subm-71", NodeSubscriptionsIq("subm-71", "get"));

            Assert.Multiple(() =>
            {
                Assert.That(EndingsIn(atAlice).Select(e => e.SubId), Is.EqualTo(new[] { subId }));
                Assert.That(SubscriptionsIn(list, OwnerNamespace),   Is.Empty);
            });

        }

        #endregion

        #region ADenialAfterTheApproval_ChangesNothing()

        /// <summary>
        /// A "no" to a question from before ends no granted subscription.
        /// </summary>
        /// <remarks>
        /// Otherwise the order of two messages would decide what holds - and a
        /// form arriving late would take away from somebody a subscription they
        /// have long had.
        /// </remarks>
        [Test]
        public async Task ADenialAfterTheApproval_ChangesNothing()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-73",
                           ConfigureIq("cfg-73", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>authorize</value></field>")));

            var alice = await ConnectClientAsync("alice");

            var grant = await AskAsync(alice, "sub-73",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                               alice.BareJid, "sub-73"));

            var subId  = SubscriptionOf(grant)!.Attr("subid")!;

            await AskAsync(bob, "subm-72",
                           NodeSubscriptionsIq("subm-72", "set",
                                               SubscriberEntry(alice.BareJid, "subscribed", subId)));

            var atAlice = CollectEvents(alice);

            await bob.SendRawAsync(AuthorizationAnswer(alice.BareJid, subId, yes: false));

            await WaitAgainst(() => EndingsIn(atAlice).Count > 0,
                              "a denial after the grant");

            var list = await AskAsync(bob, "subm-73", NodeSubscriptionsIq("subm-73", "get"));

            Assert.That(SubscriptionsIn(list, OwnerNamespace).Select(e => e.Attr("subscription")),
                        Is.EqualTo(new[] { "subscribed" }));

        }

        #endregion

        #region AFormAboutAForeignNode_IsNoAnswer()

        /// <summary>
        /// Decided is only what hangs on one's own node - everything else stays
        /// a message.
        /// </summary>
        /// <remarks>
        /// Alice cannot answer Bob's application for him: her form names a node
        /// that does not exist in her account. <b>And it does not disappear in
        /// doing so</b> - it goes its ordinary way on. To let a message vanish
        /// without a trace is the most expensive way of being polite; that is
        /// exactly what the mutation removing this check hung on.
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

            var grant = await AskAsync(alice, "sub-74",
                                       PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                               alice.BareJid, "sub-74"));

            var subId  = SubscriptionOf(grant)!.Attr("subid")!;

            var atCarol = CollectRaw(carol, PubSubSubscribeAuthorization.FormType);

            // Alice sends the answer to her own application to Carol - in whose
            // account this node does not exist.
            await alice.SendRawAsync(AuthorizationAnswer(alice.BareJid, subId, yes: true, to: carol.BareJid));

            await WaitFor(() => Count(atCarol) > 0,
                          "the message that is no answer");

            Assert.That(Server.GetAccount($"bob@{Server.Domain}")!
                              .PepSubscriptions(Node)
                              .Select(a => a.State),
                        Is.EqualTo(new[] { PubSubSubscriptionState.Pending }),
                        "And Bob's application stands there undecided.");

        }

        #endregion

        #region WithRosterAccess_OnlyTheRosterGetsIn()

        /// <summary>
        /// XEP-0060, section 4.5: with the access model <c>roster</c> whoever
        /// stands in the roster of the owner gets in.
        /// </summary>
        /// <remarks>
        /// <b>An entry is enough, a presence state is not demanded.</b> The
        /// roster is the list of the owner: whoever stands in it stands there
        /// because the owner has entered them. Whether the contact may
        /// conversely see their presence is another question - and for that
        /// there is <c>presence</c>.
        /// </remarks>
        [Test]
        public async Task WithRosterAccess_OnlyTheRosterGetsIn()
        {

            var bob = await PublishingBobAsync();

            // An entry entirely without a presence permission.
            Server.GetAccount($"bob@{Server.Domain}")!.SetRosterEntry(
                new RosterEntry($"alice@{Server.Domain}", null, "none"));

            await AskAsync(bob, "cfg-50",
                           ConfigureIq("cfg-50", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>roster</value></field>")));

            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            var inside  = await AskAsync(alice, "sub-50",
                                         PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                 alice.BareJid, "sub-50"));

            var outside = await AskAsync(carol, "sub-51",
                                         PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                 carol.BareJid, "sub-51"));

            Assert.Multiple(() =>
            {

                Assert.That(inside.Attr("type"),   Is.EqualTo("result"),
                            "Alice stands in the roster - even without any presence permission.");

                Assert.That(ConditionOf(outside),  Is.EqualTo("not-authorized"),
                            "Carol stands nowhere.");

            });

        }

        #endregion

        #region WithRosterGroups_OnlyTheNamedOnesGetIn()

        /// <summary>
        /// If groups are named, only whoever stands in one of them gets in.
        /// </summary>
        [Test]
        public async Task WithRosterGroups_OnlyTheNamedOnesGetIn()
        {

            var bob     = await PublishingBobAsync();
            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            account.SetRosterEntry(new RosterEntry($"alice@{Server.Domain}", null, "both", null, false, ["Friends"]));
            account.SetRosterEntry(new RosterEntry($"carol@{Server.Domain}", null, "both", null, false, ["Work"]));

            await AskAsync(bob, "cfg-51",
                           ConfigureIq("cfg-51", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>roster</value></field>" +
                                                  "<field var='pubsub#roster_groups_allowed'>" +
                                                  "<value>Friends</value></field>")));

            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            var inside  = await AskAsync(alice, "sub-52",
                                         PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                 alice.BareJid, "sub-52"));

            var outside = await AskAsync(carol, "sub-53",
                                         PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                 carol.BareJid, "sub-53"));

            Assert.Multiple(() =>
            {
                Assert.That(inside.Attr("type"),  Is.EqualTo("result"));
                Assert.That(ConditionOf(outside), Is.EqualTo("not-authorized"),
                            "In the roster, but in the wrong group.");
            });

        }

        #endregion

        #region SeveralGroups_AreAllRead()

        /// <summary>
        /// A multiple field carries several values - and all of them are read.
        /// </summary>
        /// <remarks>
        /// A <c>list-multi</c> of which only the first value arrived would give
        /// the owner back a list they never sent that way - and would lock out
        /// half the allowed set.
        /// </remarks>
        [Test]
        public async Task SeveralGroups_AreAllRead()
        {

            var bob     = await PublishingBobAsync();
            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            account.SetRosterEntry(new RosterEntry($"carol@{Server.Domain}", null, "both", null, false, ["Work"]));

            await AskAsync(bob, "cfg-52",
                           ConfigureIq("cfg-52", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>roster</value></field>" +
                                                  "<field var='pubsub#roster_groups_allowed'>" +
                                                  "<value>Friends</value><value>Work</value></field>")));

            var loaded = await AskAsync(bob, "cfg-53", ConfigureIq("cfg-53", "get"));

            var carol = await ConnectClientAsync("carol");

            var inside = await AskAsync(carol, "sub-54",
                                        PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                carol.BareJid, "sub-54"));

            Assert.Multiple(() =>
            {

                Assert.That(ConfigValues(loaded, "pubsub#roster_groups_allowed"),
                            Is.EqualTo(new[] { "Friends", "Work" }),
                            "The offer names both of them back.");

                Assert.That(inside.Attr("type"), Is.EqualTo("result"),
                            "And the second group holds as well as the first.");

            });

        }

        #endregion

        #region WithoutGroups_TheWholeRosterGetsIn()

        /// <summary>
        /// No group named means: the whole roster.
        /// </summary>
        /// <remarks>
        /// To read the empty list as "nobody" would be the other possibility
        /// and the worse one: it would make <c>roster</c> in its default
        /// setting equal in effect to an empty <c>whitelist</c> - two names for
        /// the same thing, and one of them misleading.
        /// </remarks>
        [Test]
        public async Task WithoutGroups_TheWholeRosterGetsIn()
        {

            var bob     = await PublishingBobAsync();
            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            account.SetRosterEntry(new RosterEntry($"alice@{Server.Domain}", null, "both", null, false, ["Friends"]));

            await AskAsync(bob, "cfg-54",
                           ConfigureIq("cfg-54", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>roster</value></field>" +
                                                  "<field var='pubsub#roster_groups_allowed'/>")));

            var alice = await ConnectClientAsync("alice");

            var inside = await AskAsync(alice, "sub-55",
                                        PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                alice.BareJid, "sub-55"));

            Assert.That(inside.Attr("type"), Is.EqualTo("result"),
                        "Having a group does no harm when none is demanded.");

        }

        #endregion

        #region TheGroupsSurvive_AChangeOfTheAccessModel()

        /// <summary>
        /// The group list is a setting of the node and not of the model.
        /// </summary>
        /// <remarks>
        /// Whoever switches from <c>open</c> to <c>roster</c> should be able to
        /// set the list beforehand - otherwise the node would stand open to the
        /// whole roster between the two instructions.
        /// </remarks>
        [Test]
        public async Task TheGroupsSurvive_AChangeOfTheAccessModel()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "cfg-55",
                           ConfigureIq("cfg-55", "set",
                                       ConfigForm("<field var='pubsub#roster_groups_allowed'>" +
                                                  "<value>Friends</value></field>")));

            await AskAsync(bob, "cfg-56",
                           ConfigureIq("cfg-56", "set",
                                       ConfigForm("<field var='pubsub#access_model'><value>roster</value></field>")));

            var loaded = await AskAsync(bob, "cfg-57", ConfigureIq("cfg-57", "get"));

            Assert.Multiple(() =>
            {
                Assert.That(ConfigField (loaded, "pubsub#access_model"),          Is.EqualTo("roster"));
                Assert.That(ConfigValues(loaded, "pubsub#roster_groups_allowed"), Is.EqualTo(new[] { "Friends" }));
            });

        }

        #endregion

        #region ARetractedItem_IsGoneAndAnnounced()

        /// <summary>
        /// XEP-0060, section 7.2: a single item is retracted - and the
        /// subscribers learn of it with its id.
        /// </summary>
        /// <remarks>
        /// <b>A delivery like a publication</b>, only with different content:
        /// one per subscription, with the SHIM id. That tells it apart from the
        /// deleting and the purging, which concern the node and therefore go
        /// out once per subscriber.
        /// </remarks>
        [Test]
        public async Task ARetractedItem_IsGoneAndAnnounced()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await AskAsync(bob, "pub-30",
                           PublishIq("pub-30", Node, "30", "<weather xmlns='urn:example:x'>windy</weather>"));

            var subId = await SubscribeAsync(alice, "abo-40");

            var events = CollectEvents(alice);

            var back = await AskAsync(bob, "ret-1", RetractIq("ret-1", "1"));

            await WaitFor(() => RetractsIn(events).Count > 0, "the event about the retraction");

            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            Assert.Multiple(() =>
            {

                Assert.That(back.Attr("type"),   Is.EqualTo("result"));
                Assert.That(RetractsIn(events),  Is.EqualTo(new[] { "1" }));
                Assert.That(SubIdsIn(events),    Is.EqualTo(new[] { subId }),
                            "With an id: it is a delivery and no message about the node.");

                Assert.That(account.GetPepItems(Node).Select(e => e.ItemId), Is.EqualTo(new[] { "30" }),
                            "The one item is gone, the other stands there.");

            });

        }

        #endregion

        #region ARetractionWithoutTheRole_IsForbidden()

        /// <summary>
        /// The same rule as with the publishing: whoever may not write may not
        /// retract either.
        /// </summary>
        [Test]
        public async Task ARetractionWithoutTheRole_IsForbidden()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            var refused = await AskAsync(alice, "ret-2", RetractIq("ret-2", "1"));

            Assert.Multiple(() =>
            {

                Assert.That(ConditionOf(refused), Is.EqualTo("forbidden"));

                Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.GetPepItems(Node), Is.Not.Empty,
                            "And the item still stands there.");

            });

        }

        #endregion

        #region APublisher_MayRetractToo()

        /// <summary>
        /// <b>Whoever may write may also retract</b> - and the event comes from
        /// the owner all the same.
        /// </summary>
        /// <remarks>
        /// A publisher thereby gets at foreign items in the same node as well.
        /// To tell them apart would mean remembering who wrote which - a store
        /// that does not exist here, and without which every finer rule would
        /// merely be claimed.
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

            var events = CollectEvents(alice);

            var back = await AskAsync(carol, "ret-3", RetractIq("ret-3", "1"));

            await WaitFor(() => RetractsIn(events).Count > 0, "the event about the foreign retraction");

            Assert.Multiple(() =>
            {

                Assert.That(back.Attr("type"), Is.EqualTo("result"));

                Assert.That(events[0], Does.Contain($"from='bob@{Server.Domain}'"),
                            "The event comes from the owner and not from the one who retracted.");

                Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.GetPepItems(Node), Is.Empty);

            });

        }

        #endregion

        #region RetractingWhatIsNotThere_IsRejected()

        /// <summary>
        /// XEP-0060, section 7.2.3.2: what does not exist is not retracted.
        /// </summary>
        /// <remarks>
        /// A <c>result</c> to it would be the information that the item is now
        /// gone - and the event to the subscribers the call to throw away
        /// something they never got.
        /// </remarks>
        [Test]
        public async Task RetractingWhatIsNotThere_IsRejected()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "abo-42");

            var events = CollectEvents(alice);

            var wrongItem   = await AskAsync(bob, "ret-4", RetractIq("ret-4", "doesnotexist"));
            var wrongNode   = await AskAsync(bob, "ret-5", RetractIq("ret-5", "1", "urn:example:nothing"));
            var withoutItem = await AskAsync(bob, "ret-6", RetractIq("ret-6", null));

            await WaitAgainst(() => RetractsIn(events).Count > 0,
                              "an event about a retraction that did not happen");

            Assert.Multiple(() =>
            {

                Assert.That(ConditionOf(wrongItem),   Is.EqualTo("item-not-found"));

                // For a stranger a <forbidden/> would come here: at a node that
                // does not exist nobody has a role. Not for the owner - they
                // are recognised and not looked up, because a PEP node belongs
                // to the account. So what they lack is not the permission but
                // the item.
                Assert.That(ConditionOf(wrongNode),   Is.EqualTo("item-not-found"),
                            "The owner lacks not the role but the item.");

                Assert.That(ConditionOf(withoutItem), Is.EqualTo("bad-request"),
                            "There is no 'retract something or other' - that is what the purging is for.");

            });

        }

        #endregion

        #region RetractingFromANodeWithoutStorage_IsRejected()

        /// <summary>
        /// XEP-0060, section 7.2.3.3, as with the purging: what keeps nothing
        /// can retract nothing.
        /// </summary>
        [Test]
        public async Task RetractingFromANodeWithoutStorage_IsRejected()
        {

            var bob = await ConnectClientAsync("bob");

            await AskAsync(bob, "cr-40",
                           $"<iq type='set' id='cr-40'>" +
                           $"<pubsub xmlns='{PubSubNamespace}'>" +
                           $"<create node='urn:example:fleeting'/>" +
                           "<configure>" +
                           ConfigForm("<field var='pubsub#persist_items'><value>0</value></field>") +
                           "</configure></pubsub></iq>");

            var refused = await AskAsync(bob, "ret-7",
                                         RetractIq("ret-7", "1", "urn:example:fleeting"));

            Assert.Multiple(() =>
            {
                Assert.That(ConditionOf(refused),       Is.EqualTo("feature-not-implemented"));
                Assert.That(PubSubConditionOf(refused), Is.EqualTo("unsupported"));
            });

        }

        #endregion

        #region ARetraction_RespectsASilencedSubscription()

        /// <summary>
        /// A silenced subscription stays quiet with a retraction as well.
        /// </summary>
        /// <remarks>
        /// It goes the same way as a publication - that both run through the
        /// same place is exactly the reason nothing had to be considered on top
        /// here.
        /// </remarks>
        [Test]
        public async Task ARetraction_RespectsASilencedSubscription()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var subId = await SubscribeAsync(alice, "abo-43");

            await AskAsync(alice, "opt-40",
                           OptionsIq("opt-40", "set", subId, SubmitForm(DeliverField("0"))));

            var events = CollectEvents(alice);

            await AskAsync(bob, "ret-8", RetractIq("ret-8", "1"));

            await WaitAgainst(() => RetractsIn(events).Count > 0,
                              "a retraction to a silenced subscription");

        }

        #endregion

        #region TheLastRetractedItem_LeavesTheNodeStanding()

        /// <summary>
        /// The last item does not take the node with it either.
        /// </summary>
        /// <remarks>
        /// A node that vanished with its content would be gone for its
        /// subscribers without announcement - and the next publication would
        /// create a new one nobody has subscribed to.
        /// </remarks>
        [Test]
        public async Task TheLastRetractedItem_LeavesTheNodeStanding()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var subId = await SubscribeAsync(alice, "abo-44");

            await AskAsync(bob, "ret-9", RetractIq("ret-9", "1"));

            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            var rightAfter = (Exists:  account.PepNodeExists(Node),
                              Entries: account.GetPepItems(Node).Count);

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-31",
                           PublishIq("pub-31", Node, "31", "<weather xmlns='urn:example:x'>clear</weather>"));

            await WaitFor(() => SubIdsIn(events).Count > 0, "the delivery after the retraction");

            Assert.Multiple(() =>
            {

                Assert.That(rightAfter.Exists, Is.True,
                            "The node goes on existing.");

                Assert.That(rightAfter.Entries, Is.Zero);

                Assert.That(account.PepSubscriptions(Node).Select(a => a.SubId), Is.EqualTo(new[] { subId }),
                            "And so does the subscription.");

            });

        }

        #endregion

        #region ADeletedNode_TakesEverythingWithIt()

        /// <summary>
        /// XEP-0060, section 8.4: what is deleted is the node, not its content
        /// - with items, settings, subscriptions and roles.
        /// </summary>
        /// <remarks>
        /// <b>The roles are the reason to write that down.</b> If they stayed
        /// standing, the next node of the same name would inherit a lockout
        /// list nobody sees any more - and the owner would wonder why an
        /// acquaintance cannot get at their new node.
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

            var deleted = await AskAsync(bob, "del-1", OwnerIq("del-1", "set", "delete"));

            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            Assert.Multiple(() =>
            {

                Assert.That(deleted.Attr("type"),           Is.EqualTo("result"));
                Assert.That(account.PepNodeExists(Node),    Is.False);
                Assert.That(account.PepSubscriptions(Node), Is.Empty);
                Assert.That(account.GetPepItems(Node),      Is.Empty);
                Assert.That(account.PepNodeConfiguration(Node), Is.Null);

                Assert.That(account.PepAffiliationOf(Node, carol.BareJid),
                            Is.EqualTo(PubSubAffiliation.None),
                            "The lockout has gone with the node as well.");

            });

        }

        #endregion

        #region ADeletedNode_CanBeSubscribedNoMore()

        /// <summary>
        /// And what does not exist cannot be subscribed to.
        /// </summary>
        [Test]
        public async Task ADeletedNode_CanBeSubscribedNoMore()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await AskAsync(bob, "del-2", OwnerIq("del-2", "set", "delete"));

            var refused = await AskAsync(alice, "sub-30",
                                         PubSubBuilder.Subscribe($"bob@{Server.Domain}", Node,
                                                                 alice.BareJid, "sub-30"));

            Assert.That(ConditionOf(refused), Is.EqualTo("item-not-found"));

        }

        #endregion

        #region DeletingSomebodyElsesNode_IsForbidden()

        /// <summary>
        /// Only the owner may delete - more hangs on this than on any other
        /// instruction.
        /// </summary>
        [Test]
        public async Task DeletingSomebodyElsesNode_IsForbidden()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            var refused = await AskAsync(alice, "del-3", OwnerIq("del-3", "set", "delete"));

            Assert.Multiple(() =>
            {
                Assert.That(ConditionOf(refused),                                Is.EqualTo("forbidden"));
                Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.PepNodeExists(Node), Is.True);
            });

        }

        #endregion

        #region DeletingANodeThatIsNotThere_IsRejected()

        /// <summary>
        /// A node that does not exist is not deleted - it is not already
        /// deleted.
        /// </summary>
        [Test]
        public async Task DeletingANodeThatIsNotThere_IsRejected()
        {

            var bob = await PublishingBobAsync();

            var refused = await AskAsync(bob, "del-4",
                                         OwnerIq("del-4", "set", "delete", "urn:example:nothing"));

            Assert.That(ConditionOf(refused), Is.EqualTo("item-not-found"));

        }

        #endregion

        #region AGetOnDelete_IsRejected()

        /// <summary>
        /// Deleting and purging are no questions.
        /// </summary>
        /// <remarks>
        /// Without this check a <c>get</c> on <c>&lt;delete/&gt;</c> would fall
        /// through as far as the configuring and get the node configuration
        /// back - an answer to a question nobody put.
        /// </remarks>
        [Test]
        public async Task AGetOnDelete_IsRejected()
        {

            var bob = await PublishingBobAsync();

            var deleted = await AskAsync(bob, "del-5", OwnerIq("del-5", "get", "delete"));
            var purged  = await AskAsync(bob, "pur-1", OwnerIq("pur-1", "get", "purge"));

            Assert.Multiple(() =>
            {

                Assert.That(ConditionOf(deleted), Is.EqualTo("bad-request"));
                Assert.That(ConditionOf(purged),  Is.EqualTo("bad-request"));

                Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.PepNodeExists(Node), Is.True,
                            "And not one of them has done anything.");

            });

        }

        #endregion

        #region TheDeletion_ReachesEverySubscriberOnce()

        /// <summary>
        /// XEP-0060, section 8.4.2: one event per subscriber and not per
        /// subscription - and without an id.
        /// </summary>
        /// <remarks>
        /// What ends is not a subscription but the node. To name an id would
        /// mean the others go on existing. For the same reason no second event
        /// under section 8.8.4 follows: that a subscription to a node that no
        /// longer exists has ended already stands here.
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

            var atAlice = CollectEvents(alice);
            var atCarol = CollectEvents(carol);

            await AskAsync(bob, "del-6", OwnerIq("del-6", "set", "delete"));

            await WaitFor(() => NodeEventsIn(atAlice).Count > 0 && NodeEventsIn(atCarol).Count > 0,
                          "the deletion event to the subscriber and the contact");

            Assert.Multiple(() =>
            {

                Assert.That(NodeEventsIn(atAlice), Is.EqualTo(new[] { ("delete", (String?) Node) }),
                            "Two subscriptions, one event.");

                Assert.That(NodeEventsIn(atCarol), Is.EqualTo(new[] { ("delete", (String?) Node) }),
                            "Whoever would have got the items learns of their end as well.");

                Assert.That(SubIdsIn(atAlice), Is.Empty,
                            "To name an id would mean the others go on existing.");

                Assert.That(EndingsIn(atAlice), Is.Empty,
                            "And a second event about it does not exist.");

            });

        }

        #endregion

        #region APurgedNode_KeepsItsSubscribersAndGoesOn()

        /// <summary>
        /// XEP-0060, section 8.5: what is purged is the content, not the node.
        /// </summary>
        /// <remarks>
        /// The whole difference from the deleting: whoever has purged goes on
        /// publishing to the same receivers.
        /// </remarks>
        [Test]
        public async Task APurgedNode_KeepsItsSubscribersAndGoesOn()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var subId = await SubscribeAsync(alice, "abo-33");

            await AskAsync(bob, "pur-2", OwnerIq("pur-2", "set", "purge"));

            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            // Look at once and not only after the next publication: <b>a
            // publishing creates the node again</b>, and after that a deleted
            // one would look like a purged one. That is exactly what the first
            // version of this test ran past - the mutation that removes the
            // store instead of emptying it survived it.
            var rightAfter = (Exists:  account.PepNodeExists(Node),
                              Entries: account.GetPepItems(Node).Count);

            var events = CollectEvents(alice);

            await AskAsync(bob, "pub-20",
                           PublishIq("pub-20", Node, "20", "<weather xmlns='urn:example:x'>new</weather>"));

            await WaitFor(() => SubIdsIn(events).Count > 0, "the delivery after the purging");

            Assert.Multiple(() =>
            {

                Assert.That(rightAfter.Exists, Is.True,
                            "The node goes on existing - even before something stands in it again.");

                Assert.That(rightAfter.Entries, Is.Zero,
                            "And empty it is.");

                Assert.That(account.PepSubscriptions(Node).Select(a => a.SubId), Is.EqualTo(new[] { subId }),
                            "The subscription has stayed.");

                Assert.That(account.GetPepItems(Node).Select(e => e.ItemId), Is.EqualTo(new[] { "20" }),
                            "The old item is gone, the new one stands there.");

            });

        }

        #endregion

        #region ThePurge_IsAnnouncedOnce()

        /// <summary>
        /// The purging is announced as well - whoever has fetched something
        /// otherwise holds it to be current.
        /// </summary>
        [Test]
        public async Task ThePurge_IsAnnouncedOnce()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            await SubscribeAsync(alice, "abo-34");
            await SubscribeAsync(alice, "abo-35");

            var events = CollectEvents(alice);

            await AskAsync(bob, "pur-3", OwnerIq("pur-3", "set", "purge"));

            await WaitFor(() => NodeEventsIn(events).Count > 0, "the event about the purging");

            Assert.That(NodeEventsIn(events), Is.EqualTo(new[] { ("purge", (String?) Node) }));

        }

        #endregion

        #region PurgingANodeWithoutStorage_IsRejected()

        /// <summary>
        /// XEP-0060, section 8.5.3.2: what keeps nothing can give nothing.
        /// </summary>
        /// <remarks>
        /// A <c>result</c> to it would be the information that something had
        /// been purged - and the event to the subscribers the call to throw
        /// away something this node never delivered.
        /// </remarks>
        [Test]
        public async Task PurgingANodeWithoutStorage_IsRejected()
        {

            var bob = await ConnectClientAsync("bob");

            await AskAsync(bob, "cr-30",
                           $"<iq type='set' id='cr-30'>" +
                           $"<pubsub xmlns='{PubSubNamespace}'>" +
                           $"<create node='urn:example:fleeting'/>" +
                           "<configure>" +
                           ConfigForm("<field var='pubsub#persist_items'><value>0</value></field>") +
                           "</configure></pubsub></iq>");

            var refused = await AskAsync(bob, "pur-4",
                                         OwnerIq("pur-4", "set", "purge", "urn:example:fleeting"));

            Assert.Multiple(() =>
            {
                Assert.That(ConditionOf(refused),       Is.EqualTo("feature-not-implemented"));
                Assert.That(ErrorTypeOf(refused),       Is.EqualTo("cancel"));
                Assert.That(PubSubConditionOf(refused), Is.EqualTo("unsupported"));
            });

        }

        #endregion

        #region PurgingSomebodyElsesNode_IsForbidden()

        /// <summary>
        /// And only the owner may purge as well.
        /// </summary>
        [Test]
        public async Task PurgingSomebodyElsesNode_IsForbidden()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            var refused = await AskAsync(alice, "pur-5", OwnerIq("pur-5", "set", "purge"));

            Assert.Multiple(() =>
            {

                Assert.That(ConditionOf(refused), Is.EqualTo("forbidden"));

                Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.GetPepItems(Node), Is.Not.Empty,
                            "And the item still stands there.");

            });

        }

        #endregion

        #region ADeletedNode_CanBeCreatedAgain_AndIsEmpty()

        /// <summary>
        /// The name is free again afterwards - and what stood in it does not
        /// come back.
        /// </summary>
        [Test]
        public async Task ADeletedNode_CanBeCreatedAgain_AndIsEmpty()
        {

            var bob = await PublishingBobAsync();

            await AskAsync(bob, "del-7", OwnerIq("del-7", "set", "delete"));

            var recreated = await AskAsync(bob, "cr-31",
                                           $"<iq type='set' id='cr-31'>" +
                                           $"<pubsub xmlns='{PubSubNamespace}'>" +
                                           $"<create node='{Node}'/></pubsub></iq>");

            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            Assert.Multiple(() =>
            {
                Assert.That(recreated.Attr("type"),      Is.EqualTo("result"),
                            "No <conflict/>: the old one does not exist any more.");
                Assert.That(account.PepNodeExists(Node), Is.True);
                Assert.That(account.GetPepItems(Node),   Is.Empty);
            });

        }

        #endregion

        #region TheAccountApi_NamesWhatTheBanCostHim()

        /// <summary>
        /// Whoever sets the role learns in doing so which subscriptions it has
        /// ended.
        /// </summary>
        /// <remarks>
        /// The information belongs where the removing happens. To gather it
        /// oneself beforehand would mean answering the same question twice -
        /// and the second answer would be the less exact one, because something
        /// can come in between the looking and the setting.
        /// </remarks>
        [Test]
        public async Task TheAccountApi_NamesWhatTheBanCostHim()
        {

            await PublishingBobAsync();

            var alice   = await ConnectClientAsync("alice");
            var subId   = await SubscribeAsync(alice, "abo-27");
            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            Assert.Multiple(() =>
            {

                // First a role that ends nothing - at the same subscriber.
                // Otherwise the empty list would only prove that Carol had
                // nothing anyway.
                Assert.That(account.SetPepAffiliation(Node, alice.BareJid,
                                                      PubSubAffiliation.Member, out var none),
                            Is.True);

                Assert.That(none, Is.Empty,
                            "Every other role ends nothing.");

                Assert.That(account.PepSubscriptions(Node).Select(a => a.SubId),
                            Is.EqualTo(new[] { subId }),
                            "And leaves the subscription standing.");

                Assert.That(account.SetPepAffiliation(Node, alice.BareJid,
                                                      PubSubAffiliation.Outcast, out var ended),
                            Is.True);

                Assert.That(ended.Select(a => a.SubId), Is.EqualTo(new[] { subId }));

            });

        }

        #endregion

        #region TheAccountApi_RemovesNothingFromANodeThatIsNotThere()

        /// <summary>
        /// Below the protocol as well: what is not there is not removed, and
        /// the answer says so.
        /// </summary>
        /// <remarks>
        /// The return is the list of the ended subscriptions and not their
        /// number: whoever wants to notify the subscriber has to know which id
        /// has ended.
        /// </remarks>
        [Test]
        public async Task TheAccountApi_RemovesNothingFromANodeThatIsNotThere()
        {

            await PublishingBobAsync();

            var alice   = await ConnectClientAsync("alice");
            var account = Server.GetAccount($"bob@{Server.Domain}")!;

            await SubscribeAsync(alice, "abo-16");

            Assert.Multiple(() =>
            {

                Assert.That(account.RemovePepSubscriptions("urn:example:nothing", alice.BareJid),
                            Is.Empty);

                Assert.That(account.RemovePepSubscriptions(Node, $"carol@{Server.Domain}"),
                            Is.Empty);

                Assert.That(account.RemovePepSubscriptions(Node, alice.BareJid).Select(a => a.Jid),
                            Is.EqualTo(new[] { alice.BareJid }));

                Assert.That(account.PepSubscriptions(Node), Is.Empty);

            });

        }

        #endregion

    }

}
