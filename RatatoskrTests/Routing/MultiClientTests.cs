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

using System.Collections.Concurrent;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// Tests with several real clients at the same test server: delivery
    /// between accounts, several resources of one account and the XEPs built on
    /// that.
    /// </summary>
    [TestFixture]
    public class MultiClientTests : AXMPPTests
    {

        #region TwoClients_ExchangeMessage()

        /// <summary>
        /// A message from Alice has to arrive at Bob - with the right sender
        /// and content.
        /// </summary>
        [Test]
        public async Task TwoClients_ExchangeMessage()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var inbox = new ConcurrentQueue<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { inbox.Enqueue(m); return Task.CompletedTask; };

            await alice.SendMessageAsync(bob.BareJid, "Hello Bob!");

            await WaitFor(() => !inbox.IsEmpty, "the delivery of the message at Bob");

            inbox.TryDequeue(out var received);

            Assert.Multiple(() =>
            {
                Assert.That(received!.Body,        Is.EqualTo("Hello Bob!"));
                Assert.That(received.FromBareJid,  Is.EqualTo(alice.BareJid));
                Assert.That(received.MessageId,    Is.Not.Null);
            });

        }

        #endregion

        #region TwoResourcesDifferingOnlyInCase_AreTwoDevices()

        /// <summary>
        /// Two resources of the same account differing only in their spelling
        /// are two devices — and a message to the one must not land at the
        /// other.
        /// </summary>
        /// <remarks>
        /// RFC 7622, section 3.4: the resourcepart depends on the spelling. The
        /// handing out of resources in the server has always kept to that —
        /// otherwise the second login would have been refused as a conflict.
        /// Looking a session up, by contrast, ran over
        /// <c>OrdinalIgnoreCase</c> on the whole full JID.
        ///
        /// Both together give exactly the fault nobody notices: the server
        /// accepts two devices and then delivers the traffic of the one to
        /// both. The message lands on the wrong one, and at the sender
        /// everything looks like success.
        /// </remarks>
        [Test]
        public async Task TwoResourcesDifferingOnlyInCase_AreTwoDevices()
        {

            // The RFC 6120 binding, because this test needs to *choose* two
            // resources that differ only in case, and XEP-0386 gives a client
            // no way to choose one at all - the server generates
            // 'Mobile/kZ8p…' around the tag and the pair would then differ in
            // far more than their spelling. What is measured is the routing,
            // not the binding, and this is the only route that lets the case be
            // set up. Routing over an inline binding is covered by
            // InlineBindTests.
            Server.OfferBind2 = false;

            var bob = await ConnectClientAsync("bob");

            Server.AddAccount("alice");

            var upperClient = CreateClient("alice");
            upperClient.Connection.Resource = "Mobile";
            await upperClient.ConnectAsync();

            var lowerClient = CreateClient("alice");
            lowerClient.Connection.Resource = "mobile";
            await lowerClient.ConnectAsync();

            Assert.Multiple(() =>
            {
                Assert.That(upperClient.FullJid, Does.EndWith("/Mobile"));
                Assert.That(lowerClient.FullJid, Does.EndWith("/mobile"),
                            "The second login has to get a resource of its own.");
            });

            var atUpper = new ConcurrentQueue<XMPPMessage>();
            var atLower = new ConcurrentQueue<XMPPMessage>();

            upperClient.OnMessage += (timestamp, sender, m, ct) => { atUpper.Enqueue(m); return Task.CompletedTask; };
            lowerClient.OnMessage += (timestamp, sender, m, ct) => { atLower.Enqueue(m); return Task.CompletedTask; };

            await bob.SendMessageAsync(lowerClient.FullJid, "Only to the lower-case mobile");

            await WaitFor(() => !atLower.IsEmpty, "the delivery to alice/mobile");

            Assert.Multiple(() =>
            {

                Assert.That(atLower, Has.Count.EqualTo(1));

                Assert.That(atUpper, Is.Empty,
                            "The message to /mobile must not reach /Mobile.");

                Assert.That(Server.SessionOf(upperClient.FullJid)?.Resource, Is.EqualTo("Mobile"));
                Assert.That(Server.SessionOf(lowerClient.FullJid)?.Resource, Is.EqualTo("mobile"));

            });

        }

        #endregion

        #region MessageDelivery_TriggersReceiptAndChatMarker()

        /// <summary>
        /// The recipient acknowledges automatically: the XEP-0184 delivery
        /// receipt and the XEP-0333 received marker have to arrive at the
        /// sender.
        /// </summary>
        [Test]
        public async Task MessageDelivery_TriggersReceiptAndChatMarker()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var receipts = new ConcurrentQueue<String>();
            var markers  = new ConcurrentQueue<ChatMarker>();

            alice.OnReceiptReceived += (timestamp, sender, from, id, ct) => { receipts.Enqueue(id); return Task.CompletedTask; };
            alice.OnChatMarker      += (timestamp, sender, m, ct)           => { markers.Enqueue(m); return Task.CompletedTask; };

            var messageId = await alice.SendMessageAsync(bob.BareJid, "Please confirm");

            await WaitFor(() => !receipts.IsEmpty, "the XEP-0184 delivery receipt at Alice");
            await WaitFor(() => !markers.IsEmpty,  "the XEP-0333 received marker at Alice");

            receipts.TryDequeue(out var receiptId);
            markers.TryDequeue(out var marker);

            Assert.Multiple(() =>
            {
                Assert.That(receiptId,          Is.EqualTo(messageId));
                Assert.That(marker!.Type,       Is.EqualTo(ChatMarkerType.Received));
                Assert.That(marker.MessageId,   Is.EqualTo(messageId));
            });

        }

        #endregion

        #region TypingIndicator_ReachesOtherClient()

        /// <summary>
        /// XEP-0085: the typing state has to arrive at the other end.
        /// </summary>
        [Test]
        public async Task TypingIndicator_ReachesOtherClient()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var states = new ConcurrentQueue<ChatState>();
            bob.OnChatState += (timestamp, sender, from, state, ct) => { states.Enqueue(state); return Task.CompletedTask; };

            await alice.SetChatPartnerAsync(bob.BareJid);
            await alice.SendChatStateAsync(ChatState.Composing);

            await WaitFor(() => states.Contains(ChatState.Composing),
                          "the typing state at Bob");

            Assert.Pass();

        }

        #endregion

        #region TwoResourcesOfSameAccount_GetDistinctFullJids()

        /// <summary>
        /// Two clients of the same account have to get different resources.
        /// </summary>
        /// <remarks>
        /// XMPPConnection asks fixedly for console-{ProcessId} as its resource.
        /// If two clients run in the same process, both demand the same
        /// resource; only the server hands out a differing one. Against a
        /// server that answers with a conflict instead, the second client would
        /// fail - the client does not handle bind errors.
        /// </remarks>
        [Test]
        public async Task TwoResourcesOfSameAccount_GetDistinctFullJids()
        {

            var first   = await ConnectClientAsync("alice");
            var second  = await ConnectClientAsync("alice");

            Assert.Multiple(() =>
            {
                Assert.That(first.BareJid, Is.EqualTo(second.BareJid));
                Assert.That(first.FullJid, Is.Not.EqualTo(second.FullJid),
                            "Both resources were given the same full JID.");
                Assert.That(Server.SessionsOf(first.BareJid), Has.Count.EqualTo(2));
            });

        }

        #endregion

        #region SecondResource_ReceivesSentCarbon()

        /// <summary>
        /// XEP-0280: if one resource sends a message, the other resource of the
        /// same account gets a sent copy.
        /// </summary>
        [Test]
        public async Task SecondResource_ReceivesSentCarbon()
        {

            var phone    = await ConnectClientAsync("alice");
            var desktop  = await ConnectClientAsync("alice");
            var bob      = await ConnectClientAsync("bob");

            await WaitFor(() => Server.SessionsOf(phone.BareJid).All(s => s.CarbonsEnabled),
                          "the switching on of carbons for both resources");

            var carbons = new ConcurrentQueue<CarbonMessage>();
            desktop.OnCarbonMessage += (timestamp, sender, c, ct) => { carbons.Enqueue(c); return Task.CompletedTask; };

            await phone.SendMessageAsync(bob.BareJid, "Written from the phone");

            await WaitFor(() => !carbons.IsEmpty, "the sent carbon on the desktop");

            carbons.TryDequeue(out var carbon);

            Assert.Multiple(() =>
            {
                Assert.That(carbon!.IsSent,   Is.True, "The carbon was not recognised as 'sent'.");
                Assert.That(carbon.Body,      Is.EqualTo("Written from the phone"));
                Assert.That(carbon.OriginalTo, Does.StartWith(bob.BareJid));
            });

        }

        #endregion

        #region PingBetweenClients_MeasuresRoundTrip()

        /// <summary>
        /// XEP-0199: a client can ping another; the other end answers
        /// automatically.
        /// </summary>
        /// <remarks>
        /// The two have to know each other. RFC 6121, section 8.5.3.1 lets a
        /// request to a resource through only if the asker may see the
        /// recipient's presence - otherwise the answer alone gives away that
        /// this resource is logged in right now. A ping between two strangers
        /// is exactly the case the rule turns away; it only stood here because
        /// the server did not yet know the rule.
        /// </remarks>
        [Test]
        public async Task PingBetweenClients_MeasuresRoundTrip()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            MakeContacts("alice", "bob");

            var rtt = await alice.PingAsync(bob.FullJid);

            Assert.Multiple(() =>
            {
                Assert.That(rtt,        Is.Not.Null, "Bob did not answer the ping.");
                Assert.That(rtt!.Value, Is.GreaterThanOrEqualTo(TimeSpan.Zero));
            });

        }

        #endregion

        #region PresenceOfOtherClient_IsObserved()

        /// <summary>
        /// The presence of another client has to arrive at the other end -
        /// provided they may see it. The subscription on both sides has been a
        /// precondition since the filtering under RFC 6121, section 4; who
        /// actually gets it and who does not the
        /// <c>PresenceSubscriptionTests</c> check.
        /// </summary>
        [Test]
        public async Task PresenceOfOtherClient_IsObserved()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice");

            var presences = new ConcurrentQueue<String>();
            alice.OnPresenceChanged += (timestamp, sender, from, type, ct) => { presences.Enqueue($"{from}|{type}"); return Task.CompletedTask; };

            var bob = await ConnectClientAsync("bob");
            await bob.SetPresenceAsync("away", "Back in a moment");

            await WaitFor(() => presences.Any(p => p.StartsWith(bob.BareJid, StringComparison.OrdinalIgnoreCase)),
                          "Bob's presence at Alice");

            Assert.Pass();

        }

        #endregion

    }

}
