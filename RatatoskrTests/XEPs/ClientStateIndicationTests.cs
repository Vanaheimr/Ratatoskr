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

using System.Net.WebSockets;
using System.Text;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0352: Client State Indication - the client says whether a human
    /// being is looking.
    /// </summary>
    /// <remarks>
    /// Two levels, and both are necessary. The division - what can wait, what
    /// is dropped, what goes out at once - is a pure function and checkable on
    /// its own. Whether the server keeps to it only a round trip answers: the
    /// buffer sits in the same method that counts (XEP-0198) and keeps
    /// (resumption), and what it shifts there does not stand out at the
    /// function.
    /// </remarks>
    [TestFixture]
    public class ClientStateIndicationTests : AXMPPTests
    {

        #region Helper functions

        private String Presence(String sender = "bob", String resource = "x")
            => $"<presence from='{sender}@{Server.Domain}/{resource}' to='alice@{Server.Domain}/r'/>";

        /// <summary>
        /// Connects Alice and declares her session inactive - over the client,
        /// so that the path over the wire is checked as well.
        /// </summary>
        private async Task<(XMPPClient Client, XMPPSession Session)> InactiveAsync()
        {

            var client   = await ConnectClientAsync();
            var session  = Server.SessionOf(client.FullJid.ToString())!;

            Assert.That(await client.SetActiveAsync(false), Is.True,
                        "The server did not announce XEP-0352.");

            await WaitFor(() => !session.ClientIsActive,
                          "the inactivity taken over by the server");

            return (client, session);

        }

        /// <summary>
        /// The frames sent to the client that contain this text.
        /// </summary>
        private static IReadOnlyList<String> Delivered(XMPPSession session, String contains)
            => [.. session.Sent.Where(f => f.Contains(contains, StringComparison.Ordinal))];

        #endregion


        #region APresenceUpdate_CanWait()

        /// <summary>
        /// A presence change is the example XEP-0352 begins with.
        /// </summary>
        [Test]
        public void APresenceUpdate_CanWait()
        {
            Assert.That(ClientStateIndication.HandlingOf(
                            "<presence xmlns='jabber:client' from='bob@example/x'><show>away</show></presence>"),
                        Is.EqualTo(ClientStateHandling.Queued));
        }

        #endregion

        #region ASubscriptionRequest_CannotWait()

        /// <summary>
        /// A contact request is a presence and no presence notification
        /// nevertheless: it waits for the decision of a human being (RFC 6121,
        /// section 3.1.3).
        /// </summary>
        /// <remarks>
        /// The difference is the one between "true later as well" and "will
        /// never be answered". Whoever holds it back holds no traffic back but
        /// a conversation that has not begun yet.
        /// </remarks>
        [Test]
        public void ASubscriptionRequest_CannotWait()
        {
            Assert.Multiple(() =>
            {

                foreach (var kind in new[] { "subscribe", "subscribed", "unsubscribe", "unsubscribed" })
                    Assert.That(ClientStateIndication.HandlingOf(
                                    $"<presence xmlns='jabber:client' type='{kind}' from='bob@example'/>"),
                                Is.EqualTo(ClientStateHandling.Immediately),
                                $"type='{kind}'");

            });
        }

        #endregion

        #region AMessageWithText_IsTheReasonTheDeviceRings()

        /// <summary>
        /// A message with text goes out at once.
        /// </summary>
        /// <remarks>
        /// XEP-0352 is an economy measure for the battery and no do-not-disturb
        /// function for the human being in front of it. Whoever held back here
        /// would make a delivery delay out of a saving of traffic.
        /// </remarks>
        [Test]
        public void AMessageWithText_IsTheReasonTheDeviceRings()
        {
            Assert.That(ClientStateIndication.HandlingOf(
                            "<message xmlns='jabber:client' from='bob@example/x' type='chat'>" +
                            "<body>Hello</body></message>"),
                        Is.EqualTo(ClientStateHandling.Immediately));
        }

        #endregion

        #region AChatState_IsDiscardedAndNotHeld()

        /// <summary>
        /// "is typing" is dropped and not kept.
        /// </summary>
        /// <remarks>
        /// The reason is not thrift but truth: a held-back
        /// <c>&lt;composing/&gt;</c> would not be a late piece of information
        /// at the delivery any more but a wrong one - the contact stopped long
        /// ago. XEP-0352, section 3 names precisely that: "Discard messages
        /// containing only Chat State Notifications ... payloads."
        /// </remarks>
        [Test]
        public void AChatState_IsDiscardedAndNotHeld()
        {
            Assert.That(ClientStateIndication.HandlingOf(
                            "<message xmlns='jabber:client' from='bob@example/x' type='chat'>" +
                            "<composing xmlns='http://jabber.org/protocol/chatstates'/></message>"),
                        Is.EqualTo(ClientStateHandling.Discarded));
        }

        #endregion

        #region AChatStateWithAThread_IsStillOnlyAChatState()

        /// <summary>
        /// A <c>&lt;thread/&gt;</c> next to it does not make a message out of
        /// it.
        /// </summary>
        /// <remarks>
        /// XEP-0085 recommends precisely this combination. Whoever counts the
        /// children instead of looking at the extensions takes every chat state
        /// notification with a thread for something of substance - and then
        /// does keep what is a lie in five minutes after all.
        /// </remarks>
        [Test]
        public void AChatStateWithAThread_IsStillOnlyAChatState()
        {
            Assert.That(ClientStateIndication.HandlingOf(
                            "<message xmlns='jabber:client' from='bob@example/x' type='chat'>" +
                            "<composing xmlns='http://jabber.org/protocol/chatstates'/>" +
                            "<thread>abc</thread></message>"),
                        Is.EqualTo(ClientStateHandling.Discarded));
        }

        #endregion

        #region AnEmptyBody_IsNotText()

        /// <summary>
        /// An empty <c>&lt;body/&gt;</c> is no text.
        /// </summary>
        /// <remarks>
        /// Some clients carry it along next to their chat states. Were it to
        /// count as content, every "is typing" notification of these clients
        /// would go out at once - and the economy measure would be without
        /// effect towards precisely the clients that would have the most from
        /// it.
        /// </remarks>
        [Test]
        public void AnEmptyBody_IsNotText()
        {
            Assert.That(ClientStateIndication.HandlingOf(
                            "<message xmlns='jabber:client' from='bob@example/x' type='chat'>" +
                            "<body>   </body>" +
                            "<composing xmlns='http://jabber.org/protocol/chatstates'/></message>"),
                        Is.EqualTo(ClientStateHandling.Discarded));
        }

        #endregion

        #region AReceipt_IsHeldAndNotDiscarded()

        /// <summary>
        /// A delivery receipt (XEP-0184) waits, but it does not expire.
        /// </summary>
        /// <remarks>
        /// The difference to the chat state: "arrived" stays true. Whoever
        /// dropped it would take a piece of information from the sender that
        /// they never get again.
        /// </remarks>
        [Test]
        public void AReceipt_IsHeldAndNotDiscarded()
        {
            Assert.Multiple(() =>
            {

                Assert.That(ClientStateIndication.HandlingOf(
                                "<message xmlns='jabber:client' from='bob@example/x'>" +
                                "<received xmlns='urn:xmpp:receipts' id='m1'/></message>"),
                            Is.EqualTo(ClientStateHandling.Queued));

                // And a message entirely without an extension all the less:
                // "only chat states" means at least one. Without this lower
                // bound every message not bringing an extension along would
                // expire - a change of subject, say.
                Assert.That(ClientStateIndication.HandlingOf(
                                "<message xmlns='jabber:client' from='bob@example/x' type='groupchat'>" +
                                "<subject>Lunch</subject></message>"),
                            Is.EqualTo(ClientStateHandling.Queued));

            });
        }

        #endregion

        #region AnIq_IsNeverHeldBack()

        /// <summary>
        /// An <c>iq</c> is a question with a deadline.
        /// </summary>
        /// <remarks>
        /// Whoever holds it back lets the deadline run out at the sender and
        /// delivers it afterwards - the answer would come to a question nobody
        /// puts any more. The same holds for every nonza: an <c>&lt;a/&gt;</c>
        /// does not belong to the traffic but to the stream.
        /// </remarks>
        [Test]
        public void AnIq_IsNeverHeldBack()
        {
            Assert.Multiple(() =>
            {

                Assert.That(ClientStateIndication.HandlingOf(
                                "<iq xmlns='jabber:client' type='get' id='p1' from='example'>" +
                                "<ping xmlns='urn:xmpp:ping'/></iq>"),
                            Is.EqualTo(ClientStateHandling.Immediately));

                Assert.That(ClientStateIndication.HandlingOf("<a xmlns='urn:xmpp:sm:3' h='7'/>"),
                            Is.EqualTo(ClientStateHandling.Immediately));

                Assert.That(ClientStateIndication.HandlingOf(
                                "<message xmlns='jabber:client' type='error' from='bob@example'>" +
                                "<error type='cancel'><service-unavailable " +
                                "xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/></error></message>"),
                            Is.EqualTo(ClientStateHandling.Immediately));

            });
        }

        #endregion

        #region TheLatestPresencePerContact_Wins()

        /// <summary>
        /// What is superseded is superseded per full JID, not per human being.
        /// </summary>
        /// <remarks>
        /// Section 3: "push the latest presence from <b>each contact</b>". Two
        /// devices of the same human being are two presences - were the one to
        /// displace the other, their phone would vanish from the list because
        /// their desktop signed off.
        /// </remarks>
        [Test]
        public void TheLatestPresencePerContact_Wins()
        {

            var mobile  = "<presence xmlns='jabber:client' from='bob@example/mobile'/>";
            var gone    = "<presence xmlns='jabber:client' from='bob@example/mobile' type='unavailable'/>";
            var desktop = "<presence xmlns='jabber:client' from='bob@example/desktop'/>";

            Assert.Multiple(() =>
            {

                Assert.That(ClientStateIndication.SupersedeKey(gone),
                            Is.EqualTo(ClientStateIndication.SupersedeKey(mobile)),
                            "A sign-off supersedes the login of the same resource.");

                Assert.That(ClientStateIndication.SupersedeKey(desktop),
                            Is.Not.EqualTo(ClientStateIndication.SupersedeKey(mobile)),
                            "Two devices are two presences.");

                Assert.That(ClientStateIndication.SupersedeKey(
                                "<message xmlns='jabber:client' from='bob@example/mobile'>" +
                                "<received xmlns='urn:xmpp:receipts' id='m1'/></message>"),
                            Is.Null,
                            "A message is superseded by nothing.");

            });

        }

        #endregion


        #region TheFeature_IsAnnouncedAfterAuthentication()

        /// <summary>
        /// XEP-0352, section 4.1: The server announces the extension in the
        /// features after the login.
        /// </summary>
        [Test]
        public async Task TheFeature_IsAnnouncedAfterAuthentication()
        {

            var client   = await ConnectClientAsync();
            var session  = Server.SessionOf(client.FullJid.ToString())!;

            Assert.Multiple(() =>
            {

                Assert.That(client.SupportsClientStateIndication, Is.True,
                            "The client did not read the announcement.");

                Assert.That(session.Sent.Count(f => f.StartsWith("<stream:features", StringComparison.Ordinal) &&
                                                    f.Contains(ClientStateIndication.Namespace, StringComparison.Ordinal)),
                            Is.EqualTo(1),
                            "The announcement does not stand in exactly one of the two feature sets.");

            });

        }

        #endregion

        #region WithoutTheAnnouncement_TheClientSaysNothing()

        /// <summary>
        /// Without the announcement the client sends no
        /// <c>&lt;inactive/&gt;</c>.
        /// </summary>
        /// <remarks>
        /// A server not knowing the extension sees an unknown element at stream
        /// level and may end the stream (RFC 6120, section 4.9.3.24). Out of
        /// the economy measure would come a torn connection - and that
        /// precisely when nobody is looking.
        /// </remarks>
        [Test]
        public async Task WithoutTheAnnouncement_TheClientSaysNothing()
        {

            Server.OfferClientStateIndication = false;

            var client   = await ConnectClientAsync();
            var session  = Server.SessionOf(client.FullJid.ToString())!;

            var reported = await client.SetActiveAsync(false);

            Assert.Multiple(() =>
            {

                Assert.That(client.SupportsClientStateIndication, Is.False);

                Assert.That(reported, Is.False,
                            "The client reports a success there was none of.");

                Assert.That(client.IsActive, Is.True,
                            "The client considers itself inactive, the server knows nothing of it.");

                Assert.That(session.Received.Any(f => f.Contains(ClientStateIndication.Namespace,
                                                                 StringComparison.Ordinal)),
                            Is.False,
                            "Something did go out after all.");

            });

        }

        #endregion

        #region TheServerAnswersNothing()

        /// <summary>
        /// XEP-0352, section 4.2: "There is no reply from the server to either
        /// of these elements."
        /// </summary>
        /// <remarks>
        /// A confirmation would be the contradiction in itself: it would wake
        /// the device at precisely the moment it lies down to sleep.
        /// </remarks>
        [Test]
        public async Task TheServerAnswersNothing()
        {

            var client   = await ConnectClientAsync();
            var session  = Server.SessionOf(client.FullJid.ToString())!;

            var before = session.Sent.Count;

            await client.SetActiveAsync(false);

            await WaitFor(() => !session.ClientIsActive, "the inactivity taken over");

            Assert.That(session.Sent.Count, Is.EqualTo(before),
                        "The server answered the <inactive/>: " +
                        String.Join(" | ", session.Sent.Skip(before)));

        }

        #endregion

        #region AnInactiveClient_GetsNoPresence()

        /// <summary>
        /// Presence is held back as long as nobody is looking - and handed over
        /// at the <c>&lt;active/&gt;</c>.
        /// </summary>
        [Test]
        public async Task AnInactiveClient_GetsNoPresence()
        {

            var (client, session) = await InactiveAsync();

            await session.SendAsync(Presence());

            Assert.Multiple(() =>
            {
                Assert.That(Delivered(session, "bob@"), Is.Empty, "The presence went out nevertheless.");
                Assert.That(session.HeldWhileInactive, Is.EqualTo(1));
            });

            Assert.That(await client.SetActiveAsync(true), Is.True);

            await WaitFor(() => Delivered(session, "bob@").Count == 1,
                          "the presence handed over");

            Assert.That(session.HeldWhileInactive, Is.EqualTo(0));

        }

        #endregion

        #region AMessage_TakesTheHeldStanzasWithIt()

        /// <summary>
        /// What was held back goes out <b>before</b> the message emptying the
        /// buffer.
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 10.1 demands the order between two entities.
        /// Without this rule Bob's message would overtake his own presence:
        /// Alice would first see "Bob writes: on my way" and afterwards that
        /// Bob went online.
        /// </remarks>
        [Test]
        public async Task AMessage_TakesTheHeldStanzasWithIt()
        {

            var (_, session) = await InactiveAsync();

            await session.SendAsync(Presence());
            await session.SendAsync($"<message from='bob@{Server.Domain}/x' to='{session.FullJid}' " +
                                    "type='chat'><body>On my way</body></message>");

            var bob = Delivered(session, "bob@");

            Assert.Multiple(() =>
            {

                Assert.That(bob.Count, Is.EqualTo(2), "Not both arrived.");

                Assert.That(bob[0], Does.StartWith("<presence"),
                            "The message overtook the held-back presence.");

                Assert.That(session.HeldWhileInactive, Is.EqualTo(0));

            });

        }

        #endregion

        #region OnlyTheLatestPresence_ArrivesOnTheWire()

        /// <summary>
        /// Five changes of one contact leave one presence behind, not five.
        /// </summary>
        [Test]
        public async Task OnlyTheLatestPresence_ArrivesOnTheWire()
        {

            var (client, session) = await InactiveAsync();

            for (var i = 0; i < 4; i++)
            {
                await session.SendAsync(Presence());
                await session.SendAsync($"<presence from='bob@{Server.Domain}/x' " +
                                        $"to='alice@{Server.Domain}/r' type='unavailable'/>");
            }

            // A second device of the same contact is not displaced by it.
            await session.SendAsync(Presence(resource: "desktop"));

            Assert.That(session.HeldWhileInactive, Is.EqualTo(2),
                        "What should have been held back are exactly two presences: " +
                        String.Join(" | ", session.HeldStanzas));

            await client.SetActiveAsync(true);

            await WaitFor(() => Delivered(session, "bob@").Count == 2,
                          "the two presences handed over");

            var delivered = Delivered(session, "bob@");

            Assert.Multiple(() =>
            {

                Assert.That(delivered[0], Does.Contain("type='unavailable'"),
                            "What was handed over was not the last presence of the mobile.");

                Assert.That(delivered[1], Does.Contain("/desktop"),
                            "The second device is missing.");

            });

        }

        #endregion

        #region AChatStateWhileInactive_NeverArrives()

        /// <summary>
        /// A chat state is dropped and does not come later either.
        /// </summary>
        [Test]
        public async Task AChatStateWhileInactive_NeverArrives()
        {

            var (client, session) = await InactiveAsync();

            await session.SendAsync($"<message from='bob@{Server.Domain}/x' to='{session.FullJid}' " +
                                    "type='chat'><composing xmlns='http://jabber.org/protocol/chatstates'/></message>");

            Assert.That(session.DiscardedWhileInactive, Is.EqualTo(1));

            await client.SetActiveAsync(true);

            // The buffer goes out at the <active/>; had the chat state landed
            // in it, it would come now.
            await session.SendAsync($"<message from='bob@{Server.Domain}/x' to='{session.FullJid}' " +
                                    "type='chat'><body>Here I am</body></message>");

            await WaitFor(() => Delivered(session, "Here I am").Count == 1, "the message afterwards");

            Assert.That(Delivered(session, "chatstates"), Is.Empty,
                        "The chat state was kept instead of dropped.");

        }

        #endregion

        #region AnIqWhileInactive_ArrivesAtOnce()

        /// <summary>
        /// An <c>iq</c> goes out at once to a sleeping client as well.
        /// </summary>
        [Test]
        public async Task AnIqWhileInactive_ArrivesAtOnce()
        {

            var (_, session) = await InactiveAsync();

            await session.SendAsync($"<iq from='{Server.Domain}' to='{session.FullJid}' " +
                                    "type='get' id='csi-ping'><ping xmlns='urn:xmpp:ping'/></iq>");

            Assert.Multiple(() =>
            {
                Assert.That(Delivered(session, "csi-ping"), Is.Not.Empty, "The iq was held back.");
                Assert.That(session.HeldWhileInactive, Is.EqualTo(0));
            });

        }

        #endregion

        #region AFullBuffer_EmptiesItself()

        /// <summary>
        /// The buffer has an upper limit - and goes out at the overflow instead
        /// of throwing something away.
        /// </summary>
        /// <remarks>
        /// A client declaring itself inactive and then not coming back again
        /// would otherwise force unlimited memory on the server with a single
        /// <c>&lt;inactive/&gt;</c>. At the overflow it gets traffic it did not
        /// want just then - that is the friendlier of the two possibilities.
        /// </remarks>
        [Test]
        public async Task AFullBuffer_EmptiesItself()
        {

            var (_, session) = await InactiveAsync();

            session.MaxHeldWhileInactive = 2;

            // Three different contacts, so that nothing supersedes anything.
            await session.SendAsync(Presence("bob"));
            await session.SendAsync(Presence("carol"));

            Assert.That(session.HeldWhileInactive, Is.EqualTo(2), "Emptied too early.");

            await session.SendAsync(Presence("dave"));

            Assert.Multiple(() =>
            {

                Assert.That(session.HeldWhileInactive, Is.EqualTo(0), "The full buffer stayed put.");

                foreach (var contact in new[] { "bob@", "carol@", "dave@" })
                    Assert.That(Delivered(session, contact).Count, Is.EqualTo(1), contact);

            });

        }

        #endregion

        #region ANonza_DoesNotWakeTheBuffer()

        /// <summary>
        /// A nonza goes out without taking the buffer along.
        /// </summary>
        /// <remarks>
        /// An <c>&lt;r/&gt;</c> of the server (XEP-0198) asks after the receive
        /// counter and carries no order. Were it to empty the buffer, every
        /// counting query would be a wake-up call through the back door - and
        /// the server would defeat its own economy measure without the client
        /// ever having said <c>&lt;active/&gt;</c>.
        ///
        /// The counting stays consistent in this: what is held back is not sent
        /// and thereby not counted either - the client reports exactly as much
        /// as reached it.
        /// </remarks>
        [Test]
        public async Task ANonza_DoesNotWakeTheBuffer()
        {

            var (_, session) = await InactiveAsync();

            await session.SendAsync(Presence());
            await session.RequestAckAsync();

            Assert.Multiple(() =>
            {
                Assert.That(session.HeldWhileInactive, Is.EqualTo(1), "The <r/> emptied the buffer.");
                Assert.That(Delivered(session, "<r "),  Is.Not.Empty, "The <r/> itself did not go out.");
            });

        }

        #endregion

        #region WithoutTheAnnouncement_TheServerDoesNotObey()

        /// <summary>
        /// A server that has not offered the extension does not act on it
        /// either.
        /// </summary>
        /// <remarks>
        /// The reverse case would be the more dangerous one: a server keeping
        /// silent and holding back nevertheless would let the client take its
        /// contacts for quiet. That is why the <c>&lt;inactive/&gt;</c> counts
        /// here like every other unannounced element at stream level -
        /// RFC 6120, section 4.9.3.24.
        /// </remarks>
        [Test]
        public async Task WithoutTheAnnouncement_TheServerDoesNotObey()
        {

            Server.OfferClientStateIndication = false;

            var client   = await ConnectClientAsync(maxReconnectAttempts: 0);
            var session  = Server.SessionOf(client.FullJid.ToString())!;

            await client.SendRawAsync(ClientStateIndication.InactiveXml);

            await WaitFor(() => session.Sent.Any(f => f.Contains("unsupported-stanza-type",
                                                                 StringComparison.Ordinal)),
                          "the stream error on the unannounced element");

            Assert.That(session.ClientIsActive, Is.True,
                        "The server took over a state it never offered.");

        }

        #endregion

        #region BeforeAuthentication_TheStateIsNotAccepted()

        /// <summary>
        /// Before the login there is nobody whose state would have to be
        /// spared.
        /// </summary>
        /// <remarks>
        /// XEP-0352, section 4.1: the extension is announced in the features
        /// <b>after</b> the login. What was not announced yet does not hold yet
        /// either - otherwise somebody not logged in would have a state at a
        /// session that belongs to nobody yet.
        /// </remarks>
        [Test]
        public async Task BeforeAuthentication_TheStateIsNotAccepted()
        {

            using var socket = new ClientWebSocket();

            socket.Options.AddSubProtocol("xmpp");
            socket.Options.RemoteCertificateValidationCallback = Server.IsOwnCertificate;

            await socket.ConnectAsync(new Uri(Server.Uri.ToString()), CancellationToken.None);

            async Task Send(String frame)
                => await socket.SendAsync(Encoding.UTF8.GetBytes(frame),
                                          WebSocketMessageType.Text, true, CancellationToken.None);

            await Send("<open xmlns='urn:ietf:params:xml:ns:xmpp-framing' " +
                       $"to='{Server.Domain}' version='1.0'/>");

            await WaitFor(() => Server.Sessions.Any(s => s.Sent.Any(f => f.Contains("mechanisms",
                                                                                     StringComparison.Ordinal))),
                          "the features of the server");

            var session = Server.Sessions.Last();

            await Send(ClientStateIndication.InactiveXml);

            await WaitFor(() => session.Sent.Any(f => f.Contains("unsupported-stanza-type",
                                                                 StringComparison.Ordinal)),
                          "the stream error on the element before the login");

            Assert.That(session.ClientIsActive, Is.True,
                        "Somebody not logged in changed the state of the session.");

        }

        #endregion

        #region AtTheEndOfTheStream_NothingIsLeftBehind()

        /// <summary>
        /// If the connection tears while something is held back, it lands in
        /// the buffer of the unacknowledged stanzas - and goes along with the
        /// resumption.
        /// </summary>
        /// <remarks>
        /// Without that the economy measure would be a loss at every tear: the
        /// returning one would get everything handed over except what the
        /// server put aside especially for them. And nobody would learn of it -
        /// the stanza was never counted, so it is missing from no count either.
        /// </remarks>
        [Test]
        public async Task AtTheEndOfTheStream_NothingIsLeftBehind()
        {

            var client = await ConnectClientAsync(streamManagement: false, maxReconnectAttempts: 0);

            await client.SendRawAsync("<enable xmlns='urn:xmpp:sm:3' resume='true'/>");

            var session = Server.SessionOf(client.FullJid.ToString())!;

            await WaitFor(() => session.StreamManagementEnabled, "the negotiated stream management");

            await client.SendRawAsync(ClientStateIndication.InactiveXml);

            await WaitFor(() => !session.ClientIsActive, "the inactivity taken over");

            await session.SendAsync(Presence());

            Assert.That(session.HeldWhileInactive, Is.EqualTo(1));

            session.Kill();

            await WaitFor(() => Server.ResumableStreamCount > 0, "the stored session");

            Assert.Multiple(() =>
            {

                Assert.That(session.HeldWhileInactive, Is.EqualTo(0),
                            "The buffer stayed hanging on the dead session.");

                Assert.That(session.PendingToClient.Any(e => e.Stanza.Contains("bob@", StringComparison.Ordinal)),
                            Is.True,
                            "The held-back presence did not get into the buffer of the unacknowledged ones.");

            });

        }

        #endregion

        #region AfterAReconnect_TheClientSaysItAgain()

        /// <summary>
        /// XEP-0352, section 5.2: A resumed stream begins active as well - so
        /// the client declares itself anew.
        /// </summary>
        /// <remarks>
        /// "Stream resumption does not affect the current CSI state, which
        /// always defaults to 'active' for new and resumed streams." The server
        /// has forgotten the state, but the device lies in the same pocket as
        /// before. Without this repetition every disturbance would be a silent
        /// end of the economy measure - and nobody would notice it, because
        /// everything goes on working after all.
        /// </remarks>
        [Test]
        public async Task AfterAReconnect_TheClientSaysItAgain()
        {

            var client   = await ConnectClientAsync(streamManagement: true);
            var session  = Server.SessionOf(client.FullJid.ToString())!;

            await client.SetActiveAsync(false);

            await WaitFor(() => !session.ClientIsActive, "the inactivity taken over");

            session.Kill();

            await WaitFor(() => Server.Sessions.Any(s => s.IsOpen &&
                                                         !ReferenceEquals(s, session) &&
                                                         !s.ClientIsActive),
                          "the renewed declaration on the new stream");

        }

        #endregion

    }

}
