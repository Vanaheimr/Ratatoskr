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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// The outgoing half of XEP-0060: the client waits for the answer before
    /// it holds a subscription to exist.
    /// </summary>
    /// <remarks>
    /// <b>The mistake this is about had stood in the WORKPLAN since D38:</b>
    /// <c>PubSubSubscribeAsync</c> sent the request off and recorded the
    /// subscription in the same line - without anybody having read the answer.
    /// A refused subscription stood there afterwards as an existing one, and
    /// the caller never learned of it.
    ///
    /// That is the same sort of mistake as the ones from the OMEMO series,
    /// only without cryptography: <b>a claim about something one has not
    /// looked at.</b> It goes unnoticed for a long time, because in the good
    /// case it is true.
    /// </remarks>
    [TestFixture]
    public class PubSubCorrelationTests : AXMPPTests
    {

        #region Helpers

        private const String Node = "urn:example:weather";

        private static String Payload(String content)
            => $"<weather xmlns='urn:example:x'>{content}</weather>";

        /// <summary>
        /// Bob, who has published - the node exists afterwards.
        /// </summary>
        private async Task<XMPPClient> PublishingBobAsync(String itemId = "1", String content = "sunny")
        {

            var bob = await ConnectClientAsync("bob");

            Assert.That(await bob.PubSubPublishAsync(Node, itemId, Payload(content), bob.BareJid),
                        Is.True,
                        "Into his own node Bob has to be able to publish.");

            return bob;

        }

        private String BobsJid => $"bob@{Server.Domain}";

        /// <summary>
        /// Makes the test server keep quiet and answers instead of it - with
        /// an answer this server would never give.
        /// </summary>
        /// <param name="request">What is waited for, e.g. <c>&lt;subscribe</c>.</param>
        /// <param name="reply">The answer; <c>{id}</c> is replaced by the id.</param>
        /// <remarks>
        /// Without this detour a part of the evaluation would stay unchecked:
        /// the own server is well-behaved, and precisely the cases in which a
        /// client gets ahead of itself come from a far side that is not.
        ///
        /// The switch belongs to it: if the server answered as well, the
        /// result would hang on who is faster - and a test decided by a race
        /// measures nothing (see D69).
        /// </remarks>
        private void PlayTheService(String request, String reply)
        {

            Server.AnswerPepRequests = false;

            Server.OnStanzaReceived += (session, frame) =>
            {
                if (frame.Contains(request, StringComparison.Ordinal))
                {

                    var id = System.Text.RegularExpressions.Regex.Match(frame, @"id='([^']+)'").Groups[1].Value;

                    _ = session.SendAsync(reply.Replace("{id}", id));

                }
            };

        }

        private static String SubscriptionIq(String kind, String state)
            => $"<iq type='{kind}' id='{{id}}'>" +
               "<pubsub xmlns='http://jabber.org/protocol/pubsub'>" +
               $"<subscription node='{Node}' subid='abc123' subscription='{state}'/>" +
               "</pubsub></iq>";

        #endregion


        #region AConfirmedSubscription_IsRecordedWithItsSubId()

        /// <summary>
        /// The confirmation is read, and what stands in it stays known.
        /// </summary>
        [Test]
        public async Task AConfirmedSubscription_IsRecordedWithItsSubId()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");
            var sub   = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {

                Assert.That(sub,        Is.Not.Null);
                Assert.That(sub!.State, Is.EqualTo(PubSubSubscriptionState.Subscribed));
                Assert.That(sub!.NodeId, Is.EqualTo(Node));
                Assert.That(sub!.SubId, Is.Not.Null.And.Not.Empty,
                            "The id comes from the service - whoever does not look has none.");

                Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.True);

            });

        }

        #endregion

        #region ARejectedSubscription_IsNotRecorded()

        /// <summary>
        /// The mistake from D38: a refused subscription stood in the books as
        /// an existing one.
        /// </summary>
        [Test]
        public async Task ARejectedSubscription_IsNotRecorded()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");
            var sub   = await alice.PubSubSubscribeAsync("urn:example:doesnotexist", JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {
                Assert.That(sub, Is.Null,
                            "A refusal is no subscription.");
                Assert.That(alice.Connection.PubSub!.IsSubscribed("urn:example:doesnotexist"), Is.False,
                            "A refused subscription must not stand there as an existing one.");
            });

        }

        #endregion

        #region AnUnansweredSubscription_IsNotRecorded()

        /// <summary>
        /// Silence is no confirmation.
        /// </summary>
        /// <remarks>
        /// The case a client is most likely to handle wrongly, because it does
        /// not announce itself. The test server can keep quiet for it -
        /// <c>AnswerPepRequests</c>, like <c>AnswerPings</c> for XEP-0199.
        ///
        /// The test costs the full term of ten seconds. That is the price for
        /// this branch having run even once.
        /// </remarks>
        [Test]
        public async Task AnUnansweredSubscription_IsNotRecorded()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            Server.AnswerPepRequests = false;

            var sub = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {
                Assert.That(sub, Is.Null);
                Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.False);
            });

        }

        #endregion

        #region APendingSubscription_IsRecordedButIsNoSubscription()

        /// <summary>
        /// XEP-0060, section 6.1.4: <c>pending</c> means that somebody is
        /// still deciding - it is no subscription, but it is information.
        /// </summary>
        /// <remarks>
        /// <b>Until D95 it was thrown away</b>, and the caller got
        /// <c>null</c> - the same answer as to a refusal. That was right to
        /// the question "am I subscribed" and wrong to the question "what have
        /// I applied for": the id of the application comes from the service,
        /// and without it this client cannot assign the later confirmation to
        /// a question of its own.
        ///
        /// The confusion the test has stood against since D71 stays ruled
        /// out - only at a different place: <c>IsSubscribed</c> counts what
        /// was confirmed and not what was recorded.
        /// </remarks>
        [Test]
        public async Task APendingSubscription_IsRecordedButIsNoSubscription()
        {

            var alice = await ConnectClientAsync("alice");

            PlayTheService("<subscribe", SubscriptionIq("result", "pending"));

            var sub = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {

                Assert.That(sub,        Is.Not.Null);
                Assert.That(sub!.State, Is.EqualTo(PubSubSubscriptionState.Pending),
                            "What the service has said stands in the answer.");
                Assert.That(sub!.SubId, Is.Not.Null.And.Not.Empty,
                            "And the id of the application is what would be lost without it.");

                Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.False,
                            "A pending is still no confirmation.");

                Assert.That(alice.Connection.PubSub!.SubscriptionsOf(Node), Has.Count.EqualTo(1),
                            "Recorded it is as what it is.");

            });

        }

        #endregion

        #region AnErrorCarryingAConfirmation_IsStillARejection()

        /// <summary>
        /// A <c>type='error'</c> stays a refusal, even when a confirmation
        /// stands in it.
        /// </summary>
        /// <remarks>
        /// <b>Why this is not merely theoretical:</b> without the check on the
        /// type, the refusing would hang solely on there happening to be no
        /// confirmation in an error answer. That is no decision but a
        /// coincidence that goes well for a long time - the same sort of
        /// ground the five OMEMO findings stood on.
        /// </remarks>
        [Test]
        public async Task AnErrorCarryingAConfirmation_IsStillARejection()
        {

            var alice = await ConnectClientAsync("alice");

            PlayTheService("<subscribe", SubscriptionIq("error", "subscribed"));

            var sub = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {
                Assert.That(sub, Is.Null);
                Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.False);
            });

        }

        #endregion

        #region AResultWithoutAConfirmation_IsNoSubscription()

        /// <summary>
        /// A <c>result</c> without a confirmation does not say that a
        /// subscription was made.
        /// </summary>
        /// <remarks>
        /// XEP-0060, section 6.1.2 demands the confirmation; a service that
        /// merely acknowledges has not answered the question. To read it as a
        /// confirmation would mean concluding a result from the absence of an
        /// error.
        /// </remarks>
        [Test]
        public async Task AResultWithoutAConfirmation_IsNoSubscription()
        {

            var alice = await ConnectClientAsync("alice");

            PlayTheService("<subscribe", "<iq type='result' id='{id}'/>");

            Assert.That(await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid)), Is.Null);
            Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.False);

        }

        #endregion

        #region AConfirmationWithoutANode_IsNoSubscription()

        /// <summary>
        /// A confirmation without a node names nothing.
        /// </summary>
        /// <remarks>
        /// The node is no ornament but the key: under it the subscription
        /// stands in the books, and on it hangs the later question of whose
        /// events are accepted. A confirmation without a node would come to
        /// lie under the empty name - and that one fits every event whose node
        /// cannot be read.
        /// </remarks>
        [Test]
        public async Task AConfirmationWithoutANode_IsNoSubscription()
        {

            var alice = await ConnectClientAsync("alice");

            PlayTheService("<subscribe",
                           "<iq type='result' id='{id}'>" +
                           "<pubsub xmlns='http://jabber.org/protocol/pubsub'>" +
                           "<subscription subid='abc123' subscription='subscribed'/>" +
                           "</pubsub></iq>");

            Assert.That(await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid)), Is.Null);
            Assert.That(alice.Connection.PubSub!.IsSubscribed(""), Is.False,
                        "A subscription under the empty name would be worse than none.");

        }

        #endregion

        #region AnUnknownSubscriptionState_IsNoSubscription()

        /// <summary>
        /// A state this client does not know does not count as a
        /// confirmation.
        /// </summary>
        /// <remarks>
        /// The caution costs nothing: whoever wrongly holds themselves to be
        /// not subscribed asks once more - whoever wrongly holds themselves to
        /// be subscribed waits for something that never comes.
        /// </remarks>
        [Test]
        public async Task AnUnknownSubscriptionState_IsNoSubscription()
        {

            var alice = await ConnectClientAsync("alice");

            PlayTheService("<subscribe", SubscriptionIq("result", "maybe"));

            Assert.That(PubSubSubscription.StateOf("maybe"),
                        Is.EqualTo(PubSubSubscriptionState.None));

            Assert.That(await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid)), Is.Null);
            Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.False);

        }

        #endregion

        #region ARejectedUnsubscribe_KeepsTheRecord()

        /// <summary>
        /// What could not be unsubscribed stays subscribed.
        /// </summary>
        /// <remarks>
        /// The other direction from the recording, and the same mistake the
        /// other way round: whoever deletes the record before the answer
        /// throws away the events of a subscription that still exists - and
        /// sees the same silence as somebody who unsubscribed properly.
        /// </remarks>
        [Test]
        public async Task ARejectedUnsubscribe_KeepsTheRecord()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            Assert.That(await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid)), Is.Not.Null);

            PlayTheService("<unsubscribe",
                           "<iq type='error' id='{id}'><error type='cancel'>" +
                           "<not-allowed xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                           "</error></iq>");

            var ended = await alice.PubSubUnsubscribeAsync(Node, JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {
                Assert.That(ended, Is.False);
                Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.True,
                            "A refused cancellation ends nothing.");
            });

        }

        #endregion

        #region TwoRequests_CarryTwoDifferentIds()

        /// <summary>
        /// Every request gets an id of its own.
        /// </summary>
        /// <remarks>
        /// Until D71 all <c>subscribe</c> carried the same fixed id
        /// <c>pubsub-sub</c>. As long as nobody assigned answers it did not
        /// show - as soon as somebody does, the second request would get the
        /// answer to the first.
        /// </remarks>
        [Test]
        public async Task TwoRequests_CarryTwoDifferentIds()
        {

            await PublishingBobAsync();

            var alice   = await ConnectClientAsync("alice");
            var session = Server.SessionOf(alice.FullJid.ToString())!;

            await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));
            await alice.PubSubSubscribeAsync("urn:example:doesnotexist", JID.Parse(BobsJid));

            var ids = session.Received
                             .Where (f => f.Contains("<subscribe", StringComparison.Ordinal))
                             .Select(f => System.Text.RegularExpressions.Regex.Match(f, @"id='([^']+)'").Groups[1].Value)
                             .ToList();

            Assert.That(ids, Has.Count.EqualTo(2));
            Assert.That(ids[0], Is.Not.EqualTo(ids[1]),
                        "Two requests with the same id cannot be told apart.");

        }

        #endregion

        #region TwoSubscriptions_AreBothRemembered()

        /// <summary>
        /// Whoever subscribes twice has two subscriptions - and the client
        /// knows of both.
        /// </summary>
        /// <remarks>
        /// Until K4 exactly one per node stood in the books, and the second
        /// overwrote the first. With that the id of the first was gone, and
        /// gone means here: <b>it could never be unsubscribed again</b> - the
        /// service demands an id when there are several, and nobody knew it
        /// any more.
        /// </remarks>
        [Test]
        public async Task TwoSubscriptions_AreBothRemembered()
        {

            await PublishingBobAsync();

            var alice  = await ConnectClientAsync("alice");

            var first  = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));
            var second = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            var subs   = alice.Connection.PubSub!.SubscriptionsOf(Node);

            Assert.Multiple(() =>
            {
                Assert.That(subs, Has.Count.EqualTo(2));
                Assert.That(subs.Select(a => a.SubId),
                            Is.EquivalentTo(new[] { first?.SubId, second?.SubId }));
                Assert.That(first?.SubId, Is.Not.EqualTo(second?.SubId));
            });

        }

        #endregion

        #region UnsubscribingWithoutASubId_WhenThereAreSeveral_IsRefused()

        /// <summary>
        /// With several subscriptions, nothing is even asked without an id.
        /// </summary>
        /// <remarks>
        /// The service would refuse it with <c>&lt;subid-required/&gt;</c> -
        /// but the client knows that itself and does not have to put the
        /// request at all. <b>More important is what it does not do:</b> pick
        /// one. That might end the wrong one, and the caller would take it for
        /// the intended one.
        /// </remarks>
        [Test]
        public async Task UnsubscribingWithoutASubId_WhenThereAreSeveral_IsRefused()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));
            await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            var session = Server.SessionOf(alice.FullJid.ToString())!;
            var before  = session.Received.Count(f => f.Contains("<unsubscribe", StringComparison.Ordinal));

            var ended = await alice.PubSubUnsubscribeAsync(Node, JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {

                Assert.That(ended, Is.False);

                Assert.That(session.Received.Count(f => f.Contains("<unsubscribe", StringComparison.Ordinal)),
                            Is.EqualTo(before),
                            "A request that can only be refused does not have to be put.");

                Assert.That(alice.Connection.PubSub!.SubscriptionsOf(Node), Has.Count.EqualTo(2),
                            "None may disappear when none was ended.");

            });

        }

        #endregion

        #region UnsubscribingWithASubId_EndsOnlyThatOne()

        /// <summary>
        /// With an id exactly the named one ends, and the other one stays.
        /// </summary>
        [Test]
        public async Task UnsubscribingWithASubId_EndsOnlyThatOne()
        {

            await PublishingBobAsync();

            var alice  = await ConnectClientAsync("alice");

            var first  = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));
            var second = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            Assert.That(await alice.PubSubUnsubscribeAsync(Node, JID.Parse(BobsJid), first!.SubId), Is.True);

            var subs = alice.Connection.PubSub!.SubscriptionsOf(Node);

            Assert.Multiple(() =>
            {
                Assert.That(subs,                    Has.Count.EqualTo(1));
                Assert.That(subs[0].SubId,           Is.EqualTo(second!.SubId));
                Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.True);
            });

        }

        #endregion

        #region EachEvent_NamesItsSubscription()

        /// <summary>
        /// XEP-0060, section 12.20: the event says which subscription it
        /// belongs to.
        /// </summary>
        /// <remarks>
        /// Without that detail two deliveries of the same thing could not be
        /// told apart - and a receiver who wants to end one of the two
        /// subscriptions would not know which one they are hearing.
        /// </remarks>
        [Test]
        public async Task EachEvent_NamesItsSubscription()
        {

            var bob    = await PublishingBobAsync();
            var alice  = await ConnectClientAsync("alice");

            var first  = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));
            var second = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            var reported = new List<PubSubEvent>();
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { lock (reported) reported.Add(e);  return Task.CompletedTask; };

            Assert.That(await bob.PubSubPublishAsync(Node, "2", Payload("rain"), bob.BareJid), Is.True);

            await WaitFor(() => { lock (reported) return reported.Count > 1; },
                          "both events");

            lock (reported)
                Assert.That(reported.Select(e => e.SubId),
                            Is.EquivalentTo(new[] { first!.SubId, second!.SubId }));

        }

        #endregion

        #region AfterTheLastUnsubscribe_TheEventsAreRejectedAgain()

        /// <summary>
        /// Once the last subscription is ended, the sender is a stranger
        /// again.
        /// </summary>
        /// <remarks>
        /// The permission hangs on the books; if a remainder stayed there, the
        /// permission would stay too - and the spoofing protection would be
        /// open for good after the first subscription for this node.
        /// </remarks>
        [Test]
        public async Task AfterTheLastUnsubscribe_TheEventsAreRejectedAgain()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");
            var sub   = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            Assert.That(await alice.PubSubUnsubscribeAsync(Node, JID.Parse(BobsJid), sub!.SubId), Is.True);

            PubSubEvent? reported = null;
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            await Server.SessionOf(alice.FullJid.ToString())!.SendAsync(
                $"<message from='{BobsJid}' type='headline' to='{alice.FullJid}'>" +
                "<event xmlns='http://jabber.org/protocol/pubsub#event'>" +
                $"<items node='{Node}'>" +
                "<item id='9'><weather xmlns='urn:example:x'>belatedly</weather></item>" +
                "</items></event></message>");

            await WaitAgainst(() => reported is not null,
                              "an event for a node that is no longer subscribed");

        }

        #endregion

        #region TheOptions_AreReadFromTheService()

        /// <summary>
        /// XEP-0060, section 6.3.1: what holds, the service says.
        /// </summary>
        /// <remarks>
        /// Not the client - it only knows what it set itself, and that is
        /// something else: another device of the same account may have changed
        /// the very same subscription.
        /// </remarks>
        [Test]
        public async Task TheOptions_AreReadFromTheService()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");
            var sub   = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            var options = await alice.PubSubGetOptionsAsync(Node, JID.Parse(BobsJid), sub!.SubId);

            Assert.Multiple(() =>
            {
                Assert.That(options,          Is.Not.Null);
                Assert.That(options!.Deliver, Is.True,
                            "Delivery happens as long as nobody objects.");
            });

        }

        #endregion

        #region SettingTheOptions_SilencesTheSubscription()

        /// <summary>
        /// Set, confirmed, remembered - and the events stay away.
        /// </summary>
        [Test]
        public async Task SettingTheOptions_SilencesTheSubscription()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");
            var sub   = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            Assert.That(await alice.PubSubSetOptionsAsync(Node,
                                                          new PubSubSubscriptionOptions(Deliver: false),
                                                          JID.Parse(BobsJid),
                                                          sub!.SubId),
                        Is.True);

            Assert.That(alice.Connection.PubSub!.SubscriptionsOf(Node)[0].Options?.Deliver,
                        Is.False,
                        "What the service has confirmed belongs in the own books.");

            Assert.That((await alice.PubSubGetOptionsAsync(Node, JID.Parse(BobsJid), sub.SubId))?.Deliver,
                        Is.False,
                        "And on asking again the same has to come out.");

            PubSubEvent? reported = null;
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            Assert.That(await bob.PubSubPublishAsync(Node, "2", Payload("quiet"), bob.BareJid), Is.True);

            await WaitAgainst(() => reported is not null,
                              "an event to a silenced subscription");

        }

        #endregion

        #region ARejectedSetting_IsNotRecorded()

        /// <summary>
        /// A refused setting must not stand there as one that holds.
        /// </summary>
        /// <remarks>
        /// The same mistake as with the subscribing in D71, one level deeper:
        /// whoever does not read the answer takes their wish for the state.
        /// Here the service refuses because the subscription belongs to
        /// somebody else.
        /// </remarks>
        [Test]
        public async Task ARejectedSetting_IsNotRecorded()
        {

            await PublishingBobAsync();

            var carol   = await ConnectClientAsync("carol");
            var foreign = await carol.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            var alice = await ConnectClientAsync("alice");
            var sub   = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            Assert.That(await alice.PubSubSetOptionsAsync(Node,
                                                          new PubSubSubscriptionOptions(Deliver: false),
                                                          JID.Parse(BobsJid),
                                                          foreign!.SubId),
                        Is.False,
                        "The id belongs to Carol's subscription.");

            Assert.That(alice.Connection.PubSub!.SubscriptionsOf(Node)[0].Options,
                        Is.Null,
                        "What was not accepted is not known either.");

            Assert.That(sub!.SubId, Is.Not.EqualTo(foreign!.SubId));

        }

        #endregion

        #region TheSubscriptions_AreFetchedAndTaken()

        /// <summary>
        /// XEP-0060, section 5.6: what the service enumerates stands in the
        /// books afterwards.
        /// </summary>
        [Test]
        public async Task TheSubscriptions_AreFetchedAndTaken()
        {

            await PublishingBobAsync();

            var alice  = await ConnectClientAsync("alice");

            var first  = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));
            var second = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            var fetched = await alice.PubSubGetSubscriptionsAsync(JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {

                Assert.That(fetched, Is.Not.Null);

                Assert.That(fetched!.Select(a => a.SubId),
                            Is.EquivalentTo(new[] { first!.SubId, second!.SubId }));

                Assert.That(fetched!.Select(a => a.NodeId).Distinct(), Is.EqualTo(new[] { Node }));

            });

        }

        #endregion

        #region AfterAReconnect_TheSubIdsComeBackFromTheService()

        /// <summary>
        /// <b>The bind this client runs into on every break - and the way
        /// out.</b>
        /// </summary>
        /// <remarks>
        /// The <c>PubSubManager</c> is created anew on every connection; only
        /// the stream management survives a reconnect. The subscriptions go on
        /// existing at the service - so afterwards the client knows not a
        /// single id any more, and since K3 the service refuses an
        /// unsubscribe without an id as soon as there are several.
        ///
        /// The test therefore checks both: that the books really are empty
        /// after the break - otherwise it would check nothing - and that they
        /// can be filled until the unsubscribing works again.
        /// </remarks>
        [Test]
        public async Task AfterAReconnect_TheSubIdsComeBackFromTheService()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice", reconnectDelay: TimeSpan.FromMilliseconds(200));

            await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));
            await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            var before = Server.ConnectionCount;

            Server.KillSessionsOf(alice.BareJid.ToString());

            await WaitFor(() => Server.ConnectionCount > before && alice.IsConnected,
                          "the rebuilding of the connection",
                          TimeSpan.FromSeconds(20));

            Assert.That(alice.Connection.PubSub!.SubscriptionsOf(Node), Is.Empty,
                        "The books do not survive the break - otherwise this test checks nothing.");

            var fetched = await alice.PubSubGetSubscriptionsAsync(JID.Parse(BobsJid));

            Assert.That(fetched, Has.Count.EqualTo(2));

            var again = alice.Connection.PubSub!.SubscriptionsOf(Node);

            Assert.That(again, Has.Count.EqualTo(2),
                        "And after the fetching it knows of both again.");

            Assert.That(await alice.PubSubUnsubscribeAsync(Node, JID.Parse(BobsJid), again[0].SubId), Is.True,
                        "With the recovered id the unsubscribing has to work.");

        }

        #endregion

        #region TheServiceAnswer_ReplacesWhatWeThoughtWeKnew()

        /// <summary>
        /// The enumeration is complete - what is missing from it does not
        /// exist any more.
        /// </summary>
        /// <remarks>
        /// To merge them would mean putting a memory next to a piece of
        /// information and holding both to be true: the client would
        /// afterwards unsubscribe with an id nobody knows any more, and would
        /// take the refusal for a mistake of the service.
        /// </remarks>
        [Test]
        public async Task TheServiceAnswer_ReplacesWhatWeThoughtWeKnew()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");
            var sub   = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            Assert.That(sub, Is.Not.Null);

            // Ended at the service without this client learning of it - the
            // way a second device of the same account would do it.
            Server.GetAccount(BobsJid)!.RemovePepSubscription(Node, alice.BareJid.ToString(), sub!.SubId);

            var fetched = await alice.PubSubGetSubscriptionsAsync(JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {
                Assert.That(fetched, Is.Empty);
                Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.False,
                            "What the service no longer knows must not stay standing here.");
            });

        }

        #endregion

        #region AResultWithoutAList_ClearsNothing()

        /// <summary>
        /// A <c>result</c> without an enumeration is no empty enumeration.
        /// </summary>
        /// <remarks>
        /// <b>The difference costs the whole books here.</b> An empty
        /// enumeration means "you have none" and empties them rightly; a
        /// missing one means "nothing stands here about that". To equate the
        /// two would mean forgetting, on an answer, what the service did not
        /// dispute at all - and the ids would be gone although the
        /// subscriptions exist.
        /// </remarks>
        [Test]
        public async Task AResultWithoutAList_ClearsNothing()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            Assert.That(await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid)), Is.Not.Null);

            PlayTheService("<subscriptions", "<iq type='result' id='{id}'/>");

            var fetched = await alice.PubSubGetSubscriptionsAsync(JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {
                Assert.That(fetched, Is.Null);
                Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.True,
                            "An answer that says nothing must not delete anything.");
            });

        }

        #endregion

        #region TheFetch_TakesOnlyWhatIsActuallySubscribed()

        /// <summary>
        /// An enumeration can also hold what is <i>no</i> subscription.
        /// </summary>
        /// <remarks>
        /// XEP-0060, section 5.6 enumerates every state - <c>pending</c> and
        /// <c>none</c> as well. The own server always says <c>subscribed</c>;
        /// a foreign one with an approval procedure does not, and then an
        /// applied-for subscription would stand in the books as an existing
        /// one. The same mistake as in D71, only carried in over the
        /// collective query.
        /// </remarks>
        [Test]
        public async Task TheFetch_TakesOnlyWhatIsActuallySubscribed()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            PlayTheService("<subscriptions",
                           "<iq type='result' id='{id}'>" +
                           "<pubsub xmlns='http://jabber.org/protocol/pubsub'><subscriptions>" +
                           $"<subscription node='{Node}' jid='alice@{Server.Domain}' subid='yes' subscription='subscribed'/>" +
                           "<subscription node='urn:example:requested' jid='alice@" + Server.Domain +
                           "' subid='maybe' subscription='pending'/>" +
                           "</subscriptions></pubsub></iq>");

            var fetched = await alice.PubSubGetSubscriptionsAsync(JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {
                Assert.That(fetched!.Select(a => a.NodeId), Is.EqualTo(new[] { Node }));
                Assert.That(alice.Connection.PubSub!.IsSubscribed("urn:example:requested"), Is.False,
                            "An applied-for subscription is none.");
            });

        }

        #endregion

        #region TheFetch_LeavesOtherServicesAlone()

        /// <summary>
        /// A service speaks for itself - not for the others.
        /// </summary>
        /// <remarks>
        /// The enumeration is complete <i>for its service</i>. To apply it to
        /// the whole books would mean concluding from the silence of the one
        /// that the subscriptions at the other have ended.
        /// </remarks>
        [Test]
        public async Task TheFetch_LeavesOtherServicesAlone()
        {

            await PublishingBobAsync();

            var carol = await ConnectClientAsync("carol");

            Assert.That(await carol.PubSubPublishAsync(Node, "1", Payload("at Carol"), carol.BareJid), Is.True);

            var alice = await ConnectClientAsync("alice");

            await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));
            await alice.PubSubSubscribeAsync(Node, carol.BareJid);

            // At the one service there is nothing left - at the other there is.
            Server.GetAccount(BobsJid)!.RemovePepSubscription(
                Node, alice.BareJid.ToString(),
                alice.Connection.PubSub!.SubscriptionsOf(Node)
                     .First(a => a.ServiceJid == JID.Parse(BobsJid)).SubId);

            await alice.PubSubGetSubscriptionsAsync(JID.Parse(BobsJid));

            Assert.That(alice.Connection.PubSub!.SubscriptionsOf(Node).Select(a => a.ServiceJid),
                        Is.EqualTo(new[] { carol.BareJid }),
                        "Carol's subscription was not up for discussion.");

        }

        #endregion

        #region AScopedFetch_LeavesTheOtherNodesAlone()

        /// <summary>
        /// A query for one node says nothing about the rest.
        /// </summary>
        /// <remarks>
        /// To treat it as complete would be the obvious shortcut and a loss:
        /// the client would forget subscriptions it merely happened not to ask
        /// about.
        /// </remarks>
        [Test]
        public async Task AScopedFetch_LeavesTheOtherNodesAlone()
        {

            var bob = await PublishingBobAsync();

            Assert.That(await bob.PubSubCreateNodeAsync("urn:example:second", service: bob.BareJid), Is.True);

            var alice = await ConnectClientAsync("alice");

            await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));
            await alice.PubSubSubscribeAsync("urn:example:second", JID.Parse(BobsJid));

            var fetched = await alice.PubSubGetSubscriptionsAsync(JID.Parse(BobsJid), "urn:example:second");

            Assert.Multiple(() =>
            {

                Assert.That(fetched!.Select(a => a.NodeId), Is.EqualTo(new[] { "urn:example:second" }));

                Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.True,
                            "The other node was not the subject of the question.");

            });

        }

        #endregion

        #region ARole_IsGrantedAndThenVisibleOnBothSides()

        /// <summary>
        /// Grant it, look at it, let it take effect - all over the client.
        /// </summary>
        /// <remarks>
        /// Three questions that belong apart: what have I granted (section
        /// 8.9.1), what am I elsewhere (5.7), and may I do what the role
        /// promises.
        /// </remarks>
        [Test]
        public async Task ARole_IsGrantedAndThenVisibleOnBothSides()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            Assert.That(await bob.PubSubSetAffiliationAsync(Node, alice.BareJid,
                                                            PubSubAffiliation.Publisher, bob.BareJid),
                        Is.True);

            var atTheNode = await bob.PubSubGetNodeAffiliationsAsync(Node, bob.BareJid);

            Assert.That(atTheNode, Is.EquivalentTo(new[] {
                             (bob.BareJid,   PubSubAffiliation.Owner),
                             (alice.BareJid, PubSubAffiliation.Publisher)
                         }));

            var alices = await alice.PubSubGetAffiliationsAsync(JID.Parse(BobsJid));

            Assert.That(alices, Is.EqualTo(new[] { (Node, PubSubAffiliation.Publisher) }));

            Assert.That(await alice.PubSubPublishAsync(Node, "70", Payload("from Alice"), JID.Parse(BobsJid)),
                        Is.True,
                        "And the role allows what it promises.");

        }

        #endregion

        #region ARoleTheServiceRefuses_IsReported()

        /// <summary>
        /// A refused granting is reported as such.
        /// </summary>
        [Test]
        public async Task ARoleTheServiceRefuses_IsReported()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            // No Assert.Multiple with an async lambda: it takes an Action, the
            // body would run on as async void, and the assertions might fall
            // after the block - that is, nowhere.
            var granted  = await alice.PubSubSetAffiliationAsync(Node, alice.BareJid,
                                                                 PubSubAffiliation.Publisher, JID.Parse(BobsJid));

            var inspected = await alice.PubSubGetNodeAffiliationsAsync(Node, JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {
                Assert.That(granted,   Is.False, "Roles are granted by the owner.");
                Assert.That(inspected, Is.Null,  "And whoever may not grant them does not get to see them either.");
            });

        }

        #endregion

        #region AnErrorCarryingARoleList_IsStillARejection()

        /// <summary>
        /// The same place for the third time: a <c>type='error'</c> stays a
        /// refusal, even with a complete list in it.
        /// </summary>
        /// <remarks>
        /// Here the confusion would be especially unpleasant: the client would
        /// show a role list it is not allowed to see - and the owner would
        /// gather from it that their node stands more open than it does.
        /// </remarks>
        [Test]
        public async Task AnErrorCarryingARoleList_IsStillARejection()
        {

            var bob = await PublishingBobAsync();

            PlayTheService("<affiliations",
                           "<iq type='error' id='{id}'>" +
                           "<pubsub xmlns='http://jabber.org/protocol/pubsub#owner'>" +
                           $"<affiliations node='{Node}'>" +
                           $"<affiliation jid='bob@{Server.Domain}' affiliation='owner'/>" +
                           "</affiliations></pubsub>" +
                           "<error type='auth'><forbidden xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/></error></iq>");

            Assert.That(await bob.PubSubGetNodeAffiliationsAsync(Node, bob.BareJid), Is.Null);

        }

        #endregion

        #region AnUnreadableEntry_InvalidatesTheWholeList()

        /// <summary>
        /// An entry with an unknown role makes the whole list fail.
        /// </summary>
        /// <remarks>
        /// <b>A list from which single lines disappear is worse than none:</b>
        /// whoever looks at it holds somebody to be without rights who is not
        /// - and may well take away the role they thought they had.
        /// </remarks>
        [Test]
        public async Task AnUnreadableEntry_InvalidatesTheWholeList()
        {

            var bob = await PublishingBobAsync();

            PlayTheService("<affiliations",
                           "<iq type='result' id='{id}'>" +
                           "<pubsub xmlns='http://jabber.org/protocol/pubsub#owner'>" +
                           $"<affiliations node='{Node}'>" +
                           $"<affiliation jid='bob@{Server.Domain}' affiliation='owner'/>" +
                           $"<affiliation jid='alice@{Server.Domain}' affiliation='publish-only'/>" +
                           "</affiliations></pubsub></iq>");

            Assert.That(await bob.PubSubGetNodeAffiliationsAsync(Node, bob.BareJid), Is.Null);

        }

        #endregion

        #region CreatingANode_WithItsConfiguration_IsConfirmed()

        /// <summary>
        /// Create and configure in one go - and the client learns whether it
        /// worked.
        /// </summary>
        /// <remarks>
        /// Two steps would have a gap: between the creating and the
        /// configuring the node would stand open, and whoever asks in that
        /// time gets.
        /// </remarks>
        [Test]
        public async Task CreatingANode_WithItsConfiguration_IsConfirmed()
        {

            var bob = await ConnectClientAsync("bob");

            Assert.That(await bob.PubSubCreateNodeAsync("urn:example:new",
                                                        new PubSubNodeConfiguration(PubSubAccessModel.Presence,
                                                                                    MaxItems: 3),
                                                        bob.BareJid),
                        Is.True);

            var loaded = await bob.PubSubGetNodeConfigAsync("urn:example:new", bob.BareJid);

            Assert.Multiple(() =>
            {
                Assert.That(loaded,              Is.Not.Null);
                Assert.That(loaded!.AccessModel, Is.EqualTo(PubSubAccessModel.Presence));
                Assert.That(loaded!.MaxItems,    Is.EqualTo(3));
            });

        }

        #endregion

        #region CreatingANodeTwice_IsReported()

        /// <summary>
        /// The second attempt is reported as a failure and not as a success.
        /// </summary>
        [Test]
        public async Task CreatingANodeTwice_IsReported()
        {

            var bob = await ConnectClientAsync("bob");

            Assert.That(await bob.PubSubCreateNodeAsync("urn:example:new", service: bob.BareJid), Is.True);
            Assert.That(await bob.PubSubCreateNodeAsync("urn:example:new", service: bob.BareJid), Is.False,
                        "What exists is not created a second time.");

        }

        #endregion

        #region ConfiguringSomebodyElsesNode_IsReported()

        /// <summary>
        /// A refused configuring is reported as such.
        /// </summary>
        [Test]
        public async Task ConfiguringSomebodyElsesNode_IsReported()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            Assert.That(await alice.PubSubConfigureNodeAsync(Node,
                                                             new PubSubNodeConfiguration(PersistItems: false),
                                                             JID.Parse(BobsJid)),
                        Is.False);

            Assert.That(Server.GetAccount(BobsJid)!.PepNodeConfiguration(Node)!.PersistItems,
                        Is.True,
                        "And it must not have changed anything.");

        }

        #endregion

        #region AnErrorCarryingAForm_IsStillARejection()

        /// <summary>
        /// A <c>type='error'</c> stays a refusal, even when a complete node
        /// form stands in it.
        /// </summary>
        /// <remarks>
        /// The same place as with the confirmation in D71: without the check
        /// on the type, the refusing would hang solely on there happening to
        /// be no form in an error answer. That is no decision but a
        /// coincidence that goes well for a long time.
        /// </remarks>
        [Test]
        public async Task AnErrorCarryingAForm_IsStillARejection()
        {

            var bob = await PublishingBobAsync();

            PlayTheService("<configure",
                           "<iq type='error' id='{id}'>" +
                           "<pubsub xmlns='http://jabber.org/protocol/pubsub#owner'>" +
                           $"<configure node='{Node}'>" +
                           "<x xmlns='jabber:x:data' type='form'>" +
                           "<field var='FORM_TYPE' type='hidden'>" +
                           "<value>http://jabber.org/protocol/pubsub#node_config</value></field>" +
                           "<field var='pubsub#access_model' type='list-single'><value>open</value></field>" +
                           "</x></configure></pubsub>" +
                           "<error type='auth'><forbidden xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/></error></iq>");

            Assert.That(await bob.PubSubGetNodeConfigAsync(Node, bob.BareJid), Is.Null);

        }

        #endregion

        #region AResultWithoutAForm_IsNoNodeConfiguration()

        /// <summary>
        /// With the node too it holds: a <c>result</c> without a form is no
        /// information.
        /// </summary>
        /// <remarks>
        /// Here the default would be especially misleading, because it says
        /// <c>open</c> - the client would show a protected node as an open
        /// one.
        /// </remarks>
        [Test]
        public async Task AResultWithoutAForm_IsNoNodeConfiguration()
        {

            var bob = await PublishingBobAsync();

            PlayTheService("<configure", "<iq type='result' id='{id}'/>");

            Assert.That(await bob.PubSubGetNodeConfigAsync(Node, bob.BareJid), Is.Null);

        }

        #endregion

        #region AResultWithoutAForm_IsNoAnswerAboutTheOptions()

        /// <summary>
        /// A <c>result</c> without a form says nothing about the options.
        /// </summary>
        /// <remarks>
        /// The same place as with the confirmation in D71, only one level
        /// deeper: to conclude a state from the absence of an error is the
        /// most comfortable way of imagining something. To put in the defaults
        /// would be especially delicate here, because they say "will be
        /// delivered" - the client would take a silenced subscription for a
        /// loud one.
        /// </remarks>
        [Test]
        public async Task AResultWithoutAForm_IsNoAnswerAboutTheOptions()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");
            var sub   = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            PlayTheService("<options", "<iq type='result' id='{id}'/>");

            Assert.That(await alice.PubSubGetOptionsAsync(Node, JID.Parse(BobsJid), sub!.SubId), Is.Null);

        }

        #endregion

        #region SettingOptions_MarksOnlyTheNamedSubscription()

        /// <summary>
        /// The setting belongs to a subscription, not to the node.
        /// </summary>
        /// <remarks>
        /// The mistake would be in the own books and therefore silent: the
        /// service sets the right one, the client notes it down at the wrong
        /// one - and from then on shows a state that does not exist.
        /// </remarks>
        [Test]
        public async Task SettingOptions_MarksOnlyTheNamedSubscription()
        {

            await PublishingBobAsync();

            var alice  = await ConnectClientAsync("alice");

            var first  = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));
            var second = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            Assert.That(await alice.PubSubSetOptionsAsync(Node,
                                                          new PubSubSubscriptionOptions(Deliver: false),
                                                          JID.Parse(BobsJid),
                                                          first!.SubId),
                        Is.True);

            var subs = alice.Connection.PubSub!.SubscriptionsOf(Node);

            Assert.Multiple(() =>
            {

                Assert.That(subs.First(a => a.SubId == first!.SubId).Options?.Deliver, Is.False);

                Assert.That(subs.First(a => a.SubId == second!.SubId).Options, Is.Null,
                            "About the other subscription nothing is known - and nothing to be claimed.");

            });

        }

        #endregion

        #region TheOfferedForm_IsReadLeniently()

        /// <summary>
        /// An offer is information: what this client does not know it passes
        /// over - what does not stand there it does not invent.
        /// </summary>
        /// <remarks>
        /// <b>The other direction from <c>TryRead</c></b>, which reads a form
        /// being sent off strictly. No contradiction, but the direction: a
        /// foreign service offers a dozen fields of which this client can set
        /// only one - whoever fails at that cannot speak with any real
        /// service. A field passed over in an <i>instruction</i>, on the other
        /// hand, is a discarded instruction.
        /// </remarks>
        [Test]
        public void TheOfferedForm_IsReadLeniently()
        {

            static System.Xml.Linq.XElement Offer(String content)
                => System.Xml.Linq.XElement.Parse($"<x xmlns='jabber:x:data' type='form'>{content}</x>");

            const String deliver = "<field var='pubsub#deliver' type='boolean'><value>0</value></field>";

            Assert.Multiple(() =>
            {

                Assert.That(PubSubSubscriptionOptions.TryReadForm(
                                Offer(deliver +
                                      "<field var='pubsub#digest' type='boolean'><value>1</value></field>"),
                                out var loaded),
                            Is.True,
                            "A field this client cannot set must not hold it up.");

                Assert.That(loaded!.Deliver, Is.False);

                Assert.That(PubSubSubscriptionOptions.TryReadForm(
                                Offer("<field var='pubsub#digest' type='boolean'><value>1</value></field>"),
                                out _),
                            Is.False,
                            "An offer without the delivery says nothing about it - " +
                            "to take the default would mean inventing it.");

            });

        }

        #endregion

        #region OptionsWithoutASubId_WhenThereAreSeveral_AreRefused()

        /// <summary>
        /// With the setting too the client picks none.
        /// </summary>
        [Test]
        public async Task OptionsWithoutASubId_WhenThereAreSeveral_AreRefused()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));
            await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            var session = Server.SessionOf(alice.FullJid.ToString())!;
            var before  = session.Received.Count(f => f.Contains("<options", StringComparison.Ordinal));

            var loaded = await alice.PubSubGetOptionsAsync(Node, JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {

                Assert.That(loaded, Is.Null);

                Assert.That(session.Received.Count(f => f.Contains("<options", StringComparison.Ordinal)),
                            Is.EqualTo(before),
                            "A request that can only be refused does not have to be put.");

            });

        }

        #endregion

        #region Unsubscribing_SendsTheSubId_AndClearsTheRecord()

        /// <summary>
        /// On unsubscribing the id the service granted goes along.
        /// </summary>
        [Test]
        public async Task Unsubscribing_SendsTheSubId_AndClearsTheRecord()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");
            var sub   = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            Assert.That(sub?.SubId, Is.Not.Null);

            var session = Server.SessionOf(alice.FullJid.ToString())!;

            Assert.That(await alice.PubSubUnsubscribeAsync(Node, JID.Parse(BobsJid)), Is.True);

            Assert.Multiple(() =>
            {

                Assert.That(session.Received.Any(f => f.Contains("<unsubscribe", StringComparison.Ordinal) &&
                                                      f.Contains($"subid='{sub!.SubId}'", StringComparison.Ordinal)),
                            Is.True,
                            "The id from the confirmation belongs into the unsubscribing.");

                Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.False);

            });

        }

        #endregion

        #region ARejectedPublish_IsReported()

        /// <summary>
        /// Into a foreign PEP node nobody may write - and the caller learns of
        /// it.
        /// </summary>
        [Test]
        public async Task ARejectedPublish_IsReported()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            Assert.That(await alice.PubSubPublishAsync(Node, "99", Payload("forged"), JID.Parse(BobsJid)),
                        Is.False,
                        "A refused publishing must not count as one that succeeded.");

        }

        #endregion

        #region AfterSubscribing_TheEventsReachTheClient()

        /// <summary>
        /// And so that the subscription is worth something: the notification
        /// gets through to the caller.
        /// </summary>
        /// <remarks>
        /// <b>Up to here it did not.</b> The spoofing protection compared the
        /// sender with the PubSub service of the domain - but a PEP event
        /// comes from the account itself (XEP-0163) and was therefore thrown
        /// away as a forgery every time. It was never noticed, because up to
        /// this point nobody had a subscription whose events anybody expected.
        /// </remarks>
        [Test]
        public async Task AfterSubscribing_TheEventsReachTheClient()
        {

            var bob    = await PublishingBobAsync();
            var alice  = await ConnectClientAsync("alice");

            Assert.That(await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid)), Is.Not.Null);

            PubSubEvent? reported = null;
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            Assert.That(await bob.PubSubPublishAsync(Node, "2", Payload("rain"), bob.BareJid), Is.True);

            await WaitFor(() => reported is not null, "the reported event");

            Assert.Multiple(() =>
            {
                Assert.That(reported!.NodeId, Is.EqualTo(Node));
                Assert.That(reported!.Items,  Has.Count.EqualTo(1));
                Assert.That(reported!.Items[0].Payload, Does.Contain("rain"));
            });

        }

        #endregion

        #region AnEventFromSomebodyElse_IsStillRejected()

        /// <summary>
        /// The spoofing protection stays: a subscription at Bob does not make
        /// Carol the source.
        /// </summary>
        [Test]
        public async Task AnEventFromSomebodyElse_IsStillRejected()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            Assert.That(await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid)), Is.Not.Null);

            PubSubEvent? reported = null;
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            await Server.SessionOf(alice.FullJid.ToString())!.SendAsync(
                $"<message from='carol@{Server.Domain}' type='headline' to='{alice.FullJid}'>" +
                "<event xmlns='http://jabber.org/protocol/pubsub#event'>" +
                $"<items node='{Node}'>" +
                "<item id='3'><weather xmlns='urn:example:x'>invented</weather></item>" +
                "</items></event></message>");

            await WaitAgainst(() => reported is not null,
                              "an event from somebody nobody subscribed to");

        }

        #endregion

        #region AnEventForAnotherNode_IsStillRejected()

        /// <summary>
        /// And the permission belongs to the node, not to the sender.
        /// </summary>
        /// <remarks>
        /// Without this test an implementation would stand that simply let
        /// everything from Bob through after the first subscription - he could
        /// then write into every made-up node this client never ordered.
        /// </remarks>
        [Test]
        public async Task AnEventForAnotherNode_IsStillRejected()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            Assert.That(await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid)), Is.Not.Null);

            PubSubEvent? reported = null;
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            await Server.SessionOf(alice.FullJid.ToString())!.SendAsync(
                $"<message from='{BobsJid}' type='headline' to='{alice.FullJid}'>" +
                "<event xmlns='http://jabber.org/protocol/pubsub#event'>" +
                "<items node='urn:example:notordered'>" +
                "<item id='4'><weather xmlns='urn:example:x'>unasked</weather></item>" +
                "</items></event></message>");

            await WaitAgainst(() => reported is not null,
                              "an event for a node nobody subscribed to");

        }

        #endregion

        #region TheSubscriberList_NamesJidAndSubId()

        /// <summary>
        /// XEP-0060, section 8.8.1: the owner reads who hangs on their node.
        /// </summary>
        /// <remarks>
        /// The other direction from section 5.6 and in build hardly to be told
        /// from it: the same enumeration, the same element name, and the entry
        /// names once a node and once a JID. The two can be told apart by the
        /// namespace alone.
        /// </remarks>
        [Test]
        public async Task TheSubscriberList_NamesJidAndSubId()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var first  = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));
            var second = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            var list = await bob.PubSubGetNodeSubscribersAsync(Node, JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {

                Assert.That(list, Is.Not.Null);

                Assert.That(list!.Select(e => (e.Jid, e.SubId)),
                            Is.EquivalentTo(new[] {
                                (alice.BareJid, first!.SubId),
                                (alice.BareJid, second!.SubId)
                            }));

                Assert.That(list!.Select(e => e.State).Distinct(),
                            Is.EqualTo(new[] { PubSubSubscriptionState.Subscribed }));

            });

        }

        #endregion

        #region AForeignNodesSubscribers_StayHidden()

        /// <summary>
        /// Who hangs on Bob's node the service tells Bob alone.
        /// </summary>
        [Test]
        public async Task AForeignNodesSubscribers_StayHidden()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            Assert.That(await alice.PubSubGetNodeSubscribersAsync(Node, JID.Parse(BobsJid)), Is.Null,
                        "A refusal is no empty list.");

        }

        #endregion

        #region AnUnreadableEntry_InvalidatesTheWholeSubscriberList()

        /// <summary>
        /// An entry with an unknown state makes the whole list fail.
        /// </summary>
        /// <remarks>
        /// <b>Here the reading is strict, unlike in the own confirmation.</b>
        /// There an unknown name as "not subscribed" is the cautious
        /// assumption - whoever wrongly holds themselves to be not subscribed
        /// asks once more. Here the same leniency would be the opposite of
        /// cautious: the owner would hold a subscriber the service keeps to be
        /// absent, and might well remove another one in their place.
        /// </remarks>
        [Test]
        public async Task AnUnreadableEntry_InvalidatesTheWholeSubscriberList()
        {

            var bob = await PublishingBobAsync();

            PlayTheService("<subscriptions",
                           "<iq type='result' id='{id}'>" +
                           "<pubsub xmlns='http://jabber.org/protocol/pubsub#owner'>" +
                           $"<subscriptions node='{Node}'>" +
                           $"<subscription jid='alice@{Server.Domain}' subid='a1' subscription='subscribed'/>" +
                           $"<subscription jid='carol@{Server.Domain}' subid='c1' subscription='almost'/>" +
                           "</subscriptions></pubsub></iq>");

            Assert.That(await bob.PubSubGetNodeSubscribersAsync(Node, JID.Parse(BobsJid)), Is.Null,
                        "A list from which lines disappear is worse than none.");

        }

        #endregion

        #region AnErrorCarryingASubscriberList_IsStillARejection()

        /// <summary>
        /// A <c>type='error'</c> stays a refusal, even when a complete list
        /// stands in it.
        /// </summary>
        /// <remarks>
        /// Without the check on the type, the refusing would hang on there
        /// happening to be no list in an error answer. The client would
        /// otherwise show a subscriber list it is not allowed to see - and the
        /// one asking would gather from it who hangs on a foreign node.
        /// </remarks>
        [Test]
        public async Task AnErrorCarryingASubscriberList_IsStillARejection()
        {

            var bob = await PublishingBobAsync();

            PlayTheService("<subscriptions",
                           "<iq type='error' id='{id}'>" +
                           "<pubsub xmlns='http://jabber.org/protocol/pubsub#owner'>" +
                           $"<subscriptions node='{Node}'>" +
                           $"<subscription jid='alice@{Server.Domain}' subid='a1' subscription='subscribed'/>" +
                           "</subscriptions></pubsub>" +
                           "<error type='auth'>" +
                           "<forbidden xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                           "</error></iq>");

            Assert.That(await bob.PubSubGetNodeSubscribersAsync(Node, JID.Parse(BobsJid)), Is.Null);

        }

        #endregion

        #region ASubscriberIsRemoved_AndBothSidesAgree()

        /// <summary>
        /// XEP-0060, section 8.8.2/8.8.4: the owner removes, and the removed
        /// one learns of it - both sets of books agree again afterwards.
        /// </summary>
        /// <remarks>
        /// The part that was missing without the event: Alice's client would
        /// go on holding the subscription to exist and would wait for events
        /// that no longer come.
        /// </remarks>
        [Test]
        public async Task ASubscriberIsRemoved_AndBothSidesAgree()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var sub = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            PubSubEvent? reported = null;
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            var removed = await bob.PubSubRemoveSubscriberAsync(Node, alice.BareJid, service: JID.Parse(BobsJid));

            await WaitFor(() => reported is not null, "the notice to the removed one");

            var list = await bob.PubSubGetNodeSubscribersAsync(Node, JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {

                Assert.That(removed,           Is.True);
                Assert.That(list,              Is.Not.Null.And.Empty);

                Assert.That(reported!.Type,    Is.EqualTo(PubSubEventType.SubscriptionEnded));
                Assert.That(reported!.NodeId,  Is.EqualTo(Node));
                Assert.That(reported!.SubId,   Is.EqualTo(sub!.SubId));

                Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.False,
                            "What has ended must not stand there as existing.");

            });

        }

        #endregion

        #region RemovingOneSubscription_LeavesTheOther()

        /// <summary>
        /// With an id the owner removes exactly one subscription.
        /// </summary>
        /// <remarks>
        /// Without it they mean the human being, with it one of their
        /// subscriptions - and the id has to go the whole way along, or more
        /// is lost than was instructed.
        /// </remarks>
        [Test]
        public async Task RemovingOneSubscription_LeavesTheOther()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var first  = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));
            var second = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            PubSubEvent? reported = null;
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            var removed = await bob.PubSubRemoveSubscriberAsync(Node, alice.BareJid,
                                                                first!.SubId, JID.Parse(BobsJid));

            await WaitFor(() => reported is not null, "the notice about the one subscription");

            var list = await bob.PubSubGetNodeSubscribersAsync(Node, JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {

                Assert.That(removed,         Is.True);
                Assert.That(reported!.SubId, Is.EqualTo(first!.SubId));

                Assert.That(list!.Select(e => e.SubId),
                            Is.EqualTo(new[] { second!.SubId }));

                Assert.That(alice.Connection.PubSub!.SubscriptionsOf(Node).Select(a => a.SubId),
                            Is.EqualTo(new[] { second!.SubId }),
                            "And at Alice too the other one stays standing.");

            });

        }

        #endregion

        #region ARemovalTheServiceRefuses_IsReported()

        /// <summary>
        /// Whoever has not subscribed cannot be removed - and the caller
        /// learns of it.
        /// </summary>
        [Test]
        public async Task ARemovalTheServiceRefuses_IsReported()
        {

            var bob = await PublishingBobAsync();

            Assert.That(await bob.PubSubRemoveSubscriberAsync(Node, JID.Parse($"carol@{Server.Domain}"), service: JID.Parse(BobsJid)),
                        Is.False);

        }

        #endregion

        #region AnEndingWithoutASubId_EndsAllOfThatNode()

        /// <summary>
        /// An ending without an id ends all subscriptions of that node.
        /// </summary>
        /// <remarks>
        /// A service names the id when it keeps several (section 12.19). If it
        /// names none and the client keeps several all the same, leaving one
        /// standing is the worse choice: the client would go on waiting for
        /// events that no longer come. The test server always names them -
        /// this way leads only over a foreign far side.
        /// </remarks>
        [Test]
        public async Task AnEndingWithoutASubId_EndsAllOfThatNode()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            Assert.That(await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid)), Is.Not.Null);
            Assert.That(await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid)), Is.Not.Null);

            PubSubEvent? reported = null;
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            await Server.SessionOf(alice.FullJid.ToString())!.SendAsync(
                $"<message from='{BobsJid}' type='headline' to='{alice.FullJid}'>" +
                "<event xmlns='http://jabber.org/protocol/pubsub#event'>" +
                $"<subscription node='{Node}' jid='{alice.BareJid}' subscription='none'/>" +
                "</event></message>");

            await WaitFor(() => reported is not null, "the ending without an id");

            Assert.Multiple(() =>
            {
                Assert.That(reported!.SubId, Is.Null);
                Assert.That(alice.Connection.PubSub!.SubscriptionsOf(Node), Is.Empty);
            });

        }

        #endregion

        #region ARetraction_ArrivesAsARetractEvent()

        /// <summary>
        /// XEP-0060, section 7.2: the item is retracted, and the subscriber
        /// gets its id.
        /// </summary>
        /// <remarks>
        /// <b>The id is the only thing that arrives</b> - a retraction has no
        /// payload. Whoever does not read it knows that something has changed
        /// but not what, and has to fetch the whole node anew.
        /// </remarks>
        [Test]
        public async Task ARetraction_ArrivesAsARetractEvent()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            Assert.That(await bob.PubSubPublishAsync(Node, "2", Payload("stormy"), JID.Parse(BobsJid)), Is.True);
            Assert.That(await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid)), Is.Not.Null);

            PubSubEvent? reported = null;
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            var back = await bob.PubSubRetractAsync(Node, "1", JID.Parse(BobsJid));

            await WaitFor(() => reported is not null, "the event about the retraction");

            var remaining = await alice.PubSubGetItemsAsync(Node, service: JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {

                Assert.That(back,                    Is.True);
                Assert.That(reported!.Type,          Is.EqualTo(PubSubEventType.Retract));
                Assert.That(reported!.NodeId,        Is.EqualTo(Node));
                Assert.That(reported!.RetractedIds, Is.EqualTo(new[] { "1" }));

                Assert.That(remaining?.Select(i => i.Id), Is.EqualTo(new[] { "2" }),
                            "The other item is still to be fetched.");

            });

        }

        #endregion

        #region ARetraction_LeavesTheSubscriptionAlone()

        /// <summary>
        /// A retraction concerns an item and not the node - the books stay
        /// untouched.
        /// </summary>
        /// <remarks>
        /// The cross-check to the deleting: there the subscription goes along.
        /// Here the same would be a loss without cause - the node goes on
        /// existing, and the next publication would come to an address this
        /// client no longer knows.
        /// </remarks>
        [Test]
        public async Task ARetraction_LeavesTheSubscriptionAlone()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var sub = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            PubSubEvent? reported = null;
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            Assert.That(await bob.PubSubRetractAsync(Node, "1", JID.Parse(BobsJid)), Is.True);

            await WaitFor(() => reported is not null, "the event about the retraction");

            reported = null;

            Assert.That(await bob.PubSubPublishAsync(Node, "3", Payload("back again"), JID.Parse(BobsJid)), Is.True);

            await WaitFor(() => reported is not null, "the next publication");

            Assert.Multiple(() =>
            {

                Assert.That(alice.Connection.PubSub!.SubscriptionsOf(Node).Select(a => a.SubId),
                            Is.EqualTo(new[] { sub!.SubId }),
                            "The subscription goes on existing.");

                Assert.That(reported!.Type,   Is.EqualTo(PubSubEventType.Items));
                Assert.That(reported!.SubId,  Is.EqualTo(sub!.SubId),
                            "And goes on delivering under the same id.");

            });

        }

        #endregion

        #region ARetractionTheServiceRefuses_IsReported()

        /// <summary>
        /// A foreign item cannot be retracted, and one that does not exist
        /// cannot either - and the caller learns both.
        /// </summary>
        [Test]
        public async Task ARetractionTheServiceRefuses_IsReported()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var foreign   = await alice.PubSubRetractAsync(Node, "1", JID.Parse(BobsJid));
            var invented  = await bob.PubSubRetractAsync  (Node, "doesnotexist", JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {
                Assert.That(foreign,  Is.False, "Without a role it does not work.");
                Assert.That(invented, Is.False, "And what does not exist is not retracted.");
            });

            Assert.That((await bob.PubSubGetItemsAsync(Node, service: JID.Parse(BobsJid)))?.Select(i => i.Id),
                        Is.EqualTo(new[] { "1" }),
                        "The item stands there untouched.");

        }

        #endregion

        #region TheWholeApproval_RunsThroughBothClients()

        /// <summary>
        /// XEP-0060, section 8.6: the whole procedure over both clients - ask,
        /// be presented with it, grant, be delivered to.
        /// </summary>
        /// <remarks>
        /// <b>The grant comes later than the question</b>, and in between lies
        /// a human being. This is why it comes as an event and not as an
        /// answer to the IQ - and why the applicant has to have recorded their
        /// own application in order to be able to assign it.
        /// </remarks>
        [Test]
        public async Task TheWholeApproval_RunsThroughBothClients()
        {

            var bob = await PublishingBobAsync();

            Assert.That(await bob.PubSubConfigureNodeAsync(Node,
                                                           new PubSubNodeConfiguration(PubSubAccessModel.Authorize),
                                                           JID.Parse(BobsJid)),
                        Is.True);

            PubSubSubscribeAuthorization? application = null;
            bob.OnPubSubSubscriptionRequest += (timestamp, sender, a, ct) => { application = a; return Task.CompletedTask; };

            var alice = await ConnectClientAsync("alice");

            // Collect them all instead of keeping the last: after the grant
            // comes the first delivery, and that would overwrite it.
            var events = new List<PubSubEvent>();
            alice.OnPubSubEvent += (timestamp, sender, pubSubEvent, ct) => { events.Add(pubSubEvent); return Task.CompletedTask; };

            var requested = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            await WaitFor(() => application is not null, "the application at the owner");

            Assert.Multiple(() =>
            {

                Assert.That(requested!.State, Is.EqualTo(PubSubSubscriptionState.Pending));
                Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.False,
                            "Before the grant it is no subscription.");

                Assert.That(application!.NodeId,        Is.EqualTo(Node));
                Assert.That(application!.SubscriberJid, Is.EqualTo(alice.BareJid));
                Assert.That(application!.SubId,         Is.EqualTo(requested!.SubId));

            });

            await bob.PubSubAnswerSubscriptionRequestAsync(application!, allow: true, JID.Parse(BobsJid));

            await WaitFor(() => events.Any(e => e.Type == PubSubEventType.SubscriptionApproved),
                          "the grant at the applicant");

            Assert.That(await bob.PubSubPublishAsync(Node, "2", Payload("at last"), JID.Parse(BobsJid)), Is.True);

            await WaitFor(() => events.Any(e => e.Type == PubSubEventType.Items),
                          "the first delivery after the grant");

            var grant = events.First(e => e.Type == PubSubEventType.SubscriptionApproved);

            Assert.Multiple(() =>
            {

                Assert.That(grant.NodeId, Is.EqualTo(Node));
                Assert.That(grant.SubId,  Is.EqualTo(requested!.SubId));

                Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.True,
                            "After the grant it is one.");

            });

        }

        #endregion

        #region ADeniedRequest_LeavesNothingBehind()

        /// <summary>
        /// A "no" strikes the application at the applicant as well.
        /// </summary>
        /// <remarks>
        /// They get the same event as somebody removed - and that is right:
        /// for them the outcome is the same, only the way there was another
        /// one.
        /// </remarks>
        [Test]
        public async Task ADeniedRequest_LeavesNothingBehind()
        {

            var bob = await PublishingBobAsync();

            Assert.That(await bob.PubSubConfigureNodeAsync(Node,
                                                           new PubSubNodeConfiguration(PubSubAccessModel.Authorize),
                                                           JID.Parse(BobsJid)),
                        Is.True);

            PubSubSubscribeAuthorization? application = null;
            bob.OnPubSubSubscriptionRequest += (timestamp, sender, a, ct) => { application = a; return Task.CompletedTask; };

            var alice = await ConnectClientAsync("alice");

            PubSubEvent? reported = null;
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            await WaitFor(() => application is not null, "the application");

            await bob.PubSubAnswerSubscriptionRequestAsync(application!, allow: false, JID.Parse(BobsJid));

            await WaitFor(() => reported is not null, "the refusal");

            Assert.Multiple(() =>
            {
                Assert.That(reported!.Type, Is.EqualTo(PubSubEventType.SubscriptionEnded));
                Assert.That(alice.Connection.PubSub!.SubscriptionsOf(Node), Is.Empty);
            });

        }

        #endregion

        #region AnApprovalWithoutARequest_IsNotRecorded()

        /// <summary>
        /// A grant without an application of one's own is not accepted.
        /// </summary>
        /// <remarks>
        /// <b>That is the rest of the rule from D86</b>, and it holds on:
        /// whoever records an unasked-for grant lets a service sign them up.
        /// New is only that there is a case in which it was asked for - and
        /// this client recognises that by its own open application.
        /// </remarks>
        [Test]
        public async Task AnApprovalWithoutARequest_IsNotRecorded()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");
            var sub   = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            Assert.That(sub!.State, Is.EqualTo(PubSubSubscriptionState.Subscribed),
                        "On an open node there is nothing to approve.");

            PubSubEvent? reported = null;
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            // A grant for an application there never was.
            await Server.SessionOf(alice.FullJid.ToString())!.SendAsync(
                $"<message from='{BobsJid}' type='headline' to='{alice.FullJid}'>" +
                "<event xmlns='http://jabber.org/protocol/pubsub#event'>" +
                $"<subscription node='{Node}' jid='{alice.BareJid}'" +
                " subid='never-asked' subscription='subscribed'/>" +
                "</event></message>");

            await WaitAgainst(() => reported is not null, "a grant without an application");

            // And the same grant for the existing subscription: <b>granted is
            // granted.</b> Without this second part the refusing would hang
            // solely on the foreign id - a grant for something that is already
            // granted would get through and report a change that is none.
            await Server.SessionOf(alice.FullJid.ToString())!.SendAsync(
                $"<message from='{BobsJid}' type='headline' to='{alice.FullJid}'>" +
                "<event xmlns='http://jabber.org/protocol/pubsub#event'>" +
                $"<subscription node='{Node}' jid='{alice.BareJid}'" +
                $" subid='{sub!.SubId}' subscription='subscribed'/>" +
                "</event></message>");

            await WaitAgainst(() => reported is not null,
                              "a grant for an existing subscription");

            Assert.That(alice.Connection.PubSub!.SubscriptionsOf(Node).Select(a => a.SubId),
                        Is.EqualTo(new[] { sub!.SubId }),
                        "It stays with the one that was asked for.");

        }

        #endregion

        #region ADeletedNode_TakesTheSubscriptionWithIt()

        /// <summary>
        /// XEP-0060, section 8.4.2: the node does not exist any more - so
        /// neither does a subscription to it.
        /// </summary>
        /// <remarks>
        /// To leave it standing would mean waiting for events from a node
        /// nobody publishes to any more - and sending along, on unsubscribing,
        /// an id the service no longer knows.
        /// </remarks>
        [Test]
        public async Task ADeletedNode_TakesTheSubscriptionWithIt()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            Assert.That(await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid)), Is.Not.Null);

            PubSubEvent? reported = null;
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            var deleted = await bob.PubSubDeleteNodeAsync(Node, JID.Parse(BobsJid));

            await WaitFor(() => reported is not null, "the event about the deleting");

            Assert.Multiple(() =>
            {

                Assert.That(deleted,          Is.True);
                Assert.That(reported!.Type,   Is.EqualTo(PubSubEventType.Delete));
                Assert.That(reported!.NodeId, Is.EqualTo(Node));

                Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.False,
                            "A subscription to a node that does not exist is none.");

            });

        }

        #endregion

        #region APurgedNode_KeepsTheSubscription()

        /// <summary>
        /// And the cross-check: the purging leaves the subscription alone.
        /// </summary>
        /// <remarks>
        /// The node goes on existing, the next publication comes to the same
        /// address. Whoever tidies up here as well has afterwards no record of
        /// a subscription that goes on existing - and gets events they have to
        /// take for forgeries.
        /// </remarks>
        [Test]
        public async Task APurgedNode_KeepsTheSubscription()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");

            var sub = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            PubSubEvent? reported = null;
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            var purged = await bob.PubSubPurgeNodeAsync(Node, JID.Parse(BobsJid));

            await WaitFor(() => reported is not null, "the event about the purging");

            Assert.Multiple(() =>
            {

                Assert.That(purged,           Is.True);
                Assert.That(reported!.Type,   Is.EqualTo(PubSubEventType.Purge));
                Assert.That(reported!.NodeId, Is.EqualTo(Node));

                Assert.That(alice.Connection.PubSub!.SubscriptionsOf(Node).Select(a => a.SubId),
                            Is.EqualTo(new[] { sub!.SubId }),
                            "The subscription goes on existing.");

            });

        }

        #endregion

        #region WhoDeletesHisOwnNode_ForgetsHisOwnSubscription()

        /// <summary>
        /// The deleter gets no event - and still has to tidy up.
        /// </summary>
        /// <remarks>
        /// The service sends the event of section 8.4.2 to everybody except
        /// the one who deleted. Whoever relied on that would be the only one
        /// left with a record about a node they removed themselves.
        /// </remarks>
        [Test]
        public async Task WhoDeletesHisOwnNode_ForgetsHisOwnSubscription()
        {

            var bob = await PublishingBobAsync();

            Assert.That(await bob.PubSubSubscribeAsync(Node, JID.Parse(BobsJid)), Is.Not.Null,
                        "The owner may subscribe to their own node.");

            Assert.That(await bob.PubSubDeleteNodeAsync(Node, JID.Parse(BobsJid)), Is.True);

            Assert.That(bob.Connection.PubSub!.IsSubscribed(Node), Is.False,
                        "And knows by itself afterwards that the subscription is gone.");

        }

        #endregion

        #region ADeletionElsewhere_LeavesTheSameNodeHereAlone()

        /// <summary>
        /// The node name alone is no node: what is deleted is deleted at a
        /// particular service.
        /// </summary>
        /// <remarks>
        /// <c>urn:xmpp:omemo:2:bundles</c> is called that at every account.
        /// Whoever strikes the name without the address ends, along with one
        /// deleted node, the subscription to the node of the same name of
        /// somebody else - and notices it only when their events fail to come.
        /// </remarks>
        [Test]
        public async Task ADeletionElsewhere_LeavesTheSameNodeHereAlone()
        {

            var bob   = await PublishingBobAsync();
            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");

            Assert.That(await carol.PubSubPublishAsync(Node, "1", Payload("cloudy"), carol.BareJid),
                        Is.True,
                        "Carol has a node of the same name.");

            var atBob   = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));
            var atCarol = await alice.PubSubSubscribeAsync(Node, carol.BareJid);

            Assert.That(atBob,   Is.Not.Null);
            Assert.That(atCarol, Is.Not.Null);

            PubSubEvent? reported = null;
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            await bob.PubSubDeleteNodeAsync(Node, JID.Parse(BobsJid));

            await WaitFor(() => reported is not null, "the event about Bob's deleted node");

            Assert.That(alice.Connection.PubSub!.SubscriptionsOf(Node).Select(a => a.ServiceJid),
                        Is.EqualTo(new[] { carol.BareJid }),
                        "Carol's node of the same name goes on standing in the books.");

        }

        #endregion

        #region ADeletionTheServiceRefuses_IsReported()

        /// <summary>
        /// A foreign node cannot be deleted - and the caller learns of it.
        /// </summary>
        [Test]
        public async Task ADeletionTheServiceRefuses_IsReported()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");

            Assert.That(await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid)), Is.Not.Null);

            // Await first, then check: Assert.Multiple takes an Action, and an
            // async lambda in it would run on as async void - the assertions
            // might fall after the block, that is, nowhere.
            var deleted = await alice.PubSubDeleteNodeAsync(Node, JID.Parse(BobsJid));
            var purged  = await alice.PubSubPurgeNodeAsync (Node, JID.Parse(BobsJid));

            Assert.Multiple(() =>
            {
                Assert.That(deleted, Is.False);
                Assert.That(purged,  Is.False);
            });

            Assert.That(alice.Connection.PubSub!.IsSubscribed(Node), Is.True,
                        "A refused deletion tidies nothing up.");

        }

        #endregion

        #region APromiseByMessage_IsNotRecorded()

        /// <summary>
        /// The other direction is not accepted: a grant comes on a request.
        /// </summary>
        /// <remarks>
        /// Whoever accepted it unasked would let a service sign them up - and
        /// that is exactly what the server of this project refuses on the
        /// other side (section 8.8.2: the owner may take away, not give).
        /// </remarks>
        [Test]
        public async Task APromiseByMessage_IsNotRecorded()
        {

            await PublishingBobAsync();

            var alice = await ConnectClientAsync("alice");
            var sub   = await alice.PubSubSubscribeAsync(Node, JID.Parse(BobsJid));

            PubSubEvent? reported = null;
            alice.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            await Server.SessionOf(alice.FullJid.ToString())!.SendAsync(
                $"<message from='{BobsJid}' type='headline' to='{alice.FullJid}'>" +
                "<event xmlns='http://jabber.org/protocol/pubsub#event'>" +
                $"<subscription node='{Node}' jid='{alice.BareJid}'" +
                " subid='unasked' subscription='subscribed'/>" +
                "</event></message>");

            await WaitAgainst(() => reported is not null, "an unasked-for grant");

            Assert.That(alice.Connection.PubSub!.SubscriptionsOf(Node).Select(a => a.SubId),
                        Is.EqualTo(new[] { sub!.SubId }),
                        "It stays with the one that was asked for.");

        }

        #endregion

    }

}
