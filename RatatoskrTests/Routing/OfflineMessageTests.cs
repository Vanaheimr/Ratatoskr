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
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// The offline store according to RFC 6121, section 8.5.2.2.1 and
    /// XEP-0160: Whoever has no reachable resource right now does not lose
    /// their messages.
    /// </summary>
    /// <remarks>
    /// The section leaves the server two paths and forbids the third: It may
    /// store the message or answer the sender with
    /// <c>&lt;service-unavailable/&gt;</c> - discard it silently it may not.
    /// That is precisely what this server did up to here, and it is the worst
    /// conceivable outcome: The sender considers their message delivered, the
    /// recipient never learned that it existed, and nobody can notice the loss.
    ///
    /// Both permitted paths are implemented here, because they limit each
    /// other: Without the store the refusal would be the normal case, and
    /// without the refusal a store that has run full would have no way out any
    /// more.
    /// </remarks>
    [TestFixture]
    public class OfflineMessageTests : AXMPPTests
    {

        #region Helper functions

        private String Alice => $"alice@{Server.Domain}";
        private String Bob   => $"bob@{Server.Domain}";

        /// <summary>
        /// A not yet connected client with an attached inbox.
        /// </summary>
        /// <remarks>
        /// The inbox is attached <b>before</b> connecting: A handed-over
        /// message comes immediately after the first presence, and a recipient
        /// that logs in only afterwards misses it depending on the timing. A
        /// test failing that way looks like an error in the server.
        /// </remarks>
        private (XMPPClient, ConcurrentQueue<XMPPMessage>) PreparedClient(String   localPart,
                                                                          Int32?   priority = null)
        {

            var client   = CreateClient(localPart);
            var inbox    = new ConcurrentQueue<XMPPMessage>();

            client.Connection.PresencePriority  = priority;
            client.OnMessage                   += (timestamp, sender, m, ct) => { inbox.Enqueue(m); return Task.CompletedTask; };

            return (client, inbox);

        }

        /// <summary>Logs a client out and waits until the server sees it.</summary>
        private async Task DisconnectAndWaitAsync(XMPPClient client, String bareJid)
        {
            await client.DisconnectAsync();
            await WaitFor(() => Server.SessionsOf(bareJid).Count == 0,
                          $"the end of the session of {bareJid}");
        }

        /// <summary>Waits until this many messages are stored for an account.</summary>
        private async Task WaitForTheStore(String bareJid, Int32 count)
            => await WaitFor(() => Server.GetAccount(bareJid)?.OfflineMessages.Count == count,
                             $"{count} stored message(s) for {bareJid}");

        #endregion


        #region AChatToAnOfflineAccount_ArrivesAtTheNextLogin()

        /// <summary>
        /// The core: Bob is not there when Alice writes - and reads it at the
        /// next login, in the order of arrival.
        /// </summary>
        /// <remarks>
        /// Two messages and not one, because the order is part of the matter. A
        /// conversation handed over the wrong way round is harder to read than
        /// one missing entirely: The reader takes the answer for the question.
        /// </remarks>
        [Test]
        public async Task AChatToAnOfflineAccount_ArrivesAtTheNextLogin()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.SendMessageAsync(Bob, "First");
            await alice.SendMessageAsync(Bob, "Second");

            await WaitForTheStore(Bob, 2);

            // Only now does Bob come - the messages have been lying for a while.
            var (bob, inbox) = PreparedClient("bob");
            await bob.ConnectAsync();

            await WaitFor(() => inbox.Count == 2, "the handed-over messages");

            var loaded = inbox.ToArray();

            Assert.Multiple(() =>
            {

                Assert.That(loaded[0].Body,        Is.EqualTo("First"));
                Assert.That(loaded[1].Body,        Is.EqualTo("Second"));
                Assert.That(loaded[0].FromBareJid, Is.EqualTo(Alice),
                            "The handing over happens with the original sender.");

                Assert.That(Server.GetAccount(Bob)!.OfflineMessages, Is.Empty,
                            "Delivered is settled - the store does not stay.");

            });

        }

        #endregion

        #region AMessageWithoutAType_IsAlsoStored()

        /// <summary>
        /// Section 8.5.2.2.1 names <c>normal</c> and <c>chat</c> - and a
        /// message without a <c>type</c> is a <c>normal</c> according to
        /// section 5.2.2.
        /// </summary>
        /// <remarks>
        /// The case is not made up: A message without a <c>type</c> is what a
        /// sender sends who is not holding a conversation but leaving something
        /// behind - exactly the kind a store is made for.
        /// </remarks>
        [Test]
        public async Task AMessageWithoutAType_IsAlsoStored()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.SendRawAsync($"<message to='{Bob}' id='without-a-type'><body>Left behind</body></message>");

            await WaitForTheStore(Bob, 1);

            var (bob, inbox) = PreparedClient("bob");
            await bob.ConnectAsync();

            await WaitFor(() => !inbox.IsEmpty, "the handed-over message");

            inbox.TryDequeue(out var message);

            Assert.That(message!.Type, Is.EqualTo(MessageType.Normal));

        }

        #endregion

        #region AHeadlineToAnOfflineAccount_IsNotStored()

        /// <summary>
        /// The counter-check: a <c>headline</c> is not stored but discarded
        /// silently.
        /// </summary>
        /// <remarks>
        /// Section 8.5.2.2.1 demands that expressly, and XEP-0160 names the
        /// reason: A notice is bound to its time. Getting yesterday's price
        /// handed over at the login is not better than missing it but worse -
        /// it looks like today's one.
        ///
        /// Without this counter-check the collection would pass even if simply
        /// everything that was not deliverable were stored.
        /// </remarks>
        [Test]
        public async Task AHeadlineToAnOfflineAccount_IsNotStored()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.SendRawAsync(
                      $"<message to='{Bob}' type='headline' id='notice'><body>Price has fallen</body></message>");

            // And afterwards one that has to be stored - it is the proof that
            // the store ran at all while the notice fell through.
            await alice.SendMessageAsync(Bob, "And this one stays lying");

            await WaitForTheStore(Bob, 1);

            Assert.That(Server.GetAccount(Bob)!.OfflineMessages[0].Stanza,
                        Does.Contain("And this one stays lying"));

        }

        #endregion

        #region AChatStateOnly_IsNotStored()

        /// <summary>
        /// XEP-0160, section 3: A <c>chat</c> carrying nothing but a chat state
        /// is not stored - "such messages SHOULD NOT be stored offline".
        /// </summary>
        /// <remarks>
        /// A chat state is a statement about <i>now</i>. Handed over at the
        /// login it says somebody is typing right now - and that is not true
        /// any more by then. Ten of them in the store also push out the
        /// messages it is meant for.
        ///
        /// <b>And the sender gets no error here</b>, although D14 otherwise
        /// expressly rules out the silent discarding. The difference lies in
        /// the expectation: Whoever sends a message wants to know whether it
        /// arrived; whoever sends a chat state has lost nothing when it
        /// expires. An error for that would be noise - and one coming anew at
        /// every keystroke.
        /// </remarks>
        [Test]
        public async Task AChatStateOnly_IsNotStored()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            var errors = new ConcurrentQueue<StanzaError>();
            alice.Connection.OnStanzaError += (timestamp, sender, from, e, ct) => { errors.Enqueue(e); return Task.CompletedTask; };

            await alice.SendRawAsync(
                      $"<message to='{Bob}' type='chat' id='typing'>" +
                      "<composing xmlns='http://jabber.org/protocol/chatstates'/></message>");

            // Afterwards one that has to be stored - it is the proof that the
            // store ran while the chat state fell through.
            await alice.SendMessageAsync(Bob, "And this one stays lying");

            await WaitForTheStore(Bob, 1);

            Assert.Multiple(() =>
            {

                Assert.That(Server.GetAccount(Bob)!.OfflineMessages[0].Stanza,
                            Does.Contain("And this one stays lying"));

                Assert.That(errors, Is.Empty,
                            "An expired chat state is no error.");

            });

        }

        #endregion

        #region AChatStateWithABody_IsStored()

        /// <summary>
        /// The counter-check: If the same message carries a text as well, it is
        /// stored.
        /// </summary>
        /// <remarks>
        /// XEP-0085, section 5.3 expressly lets the chat state travel along on
        /// an ordinary message. The exception from XEP-0160 holds only if
        /// <b>nothing else</b> stands in it - without this counter-check
        /// "discard everything containing a chat state" would be a passing
        /// solution, and it would lose real messages.
        /// </remarks>
        [Test]
        public async Task AChatStateWithABody_IsStored()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.SendRawAsync(
                      $"<message to='{Bob}' type='chat' id='with-text'>" +
                      "<body>Be right there</body>" +
                      "<active xmlns='http://jabber.org/protocol/chatstates'/></message>");

            await WaitForTheStore(Bob, 1);

            Assert.That(Server.GetAccount(Bob)!.OfflineMessages[0].Stanza,
                        Does.Contain("Be right there"));

        }

        #endregion

        #region WhatIsNotAChatState_IsStored()

        /// <summary>
        /// The second counter-check: A message without a text is far from being
        /// a chat state for that reason.
        /// </summary>
        /// <remarks>
        /// The obvious shortcut would be "do not store without a
        /// <c>&lt;body/&gt;</c>". It would be wrong: A delivery receipt
        /// (XEP-0184) and a read marker (XEP-0333) have no text and shall reach
        /// the recipient nevertheless. A <c>thread</c> alone does not make a
        /// chat state out of a message either.
        /// </remarks>
        [Test]
        public async Task WhatIsNotAChatState_IsStored()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.SendRawAsync(
                      $"<message to='{Bob}' type='chat' id='receipt'>" +
                      "<received xmlns='urn:xmpp:receipts' id='before'/></message>");

            await WaitForTheStore(Bob, 1);

            Assert.That(Server.GetAccount(Bob)!.OfflineMessages[0].Stanza,
                        Does.Contain("urn:xmpp:receipts"));

        }

        #endregion

        #region AChatStateWithAThread_IsAlsoNotStored()

        /// <summary>
        /// A <c>thread</c> next to the chat state changes nothing: It is an
        /// identifier, no content.
        /// </summary>
        /// <remarks>
        /// XEP-0085, section 5.3 demonstrates precisely this form. Whoever
        /// counted the thread as content would store the chat state after all -
        /// and that in exactly the spelling the XEP recommends.
        /// </remarks>
        [Test]
        public async Task AChatStateWithAThread_IsAlsoNotStored()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.SendRawAsync(
                      $"<message to='{Bob}' type='chat' id='typing-in-the-thread'>" +
                      "<composing xmlns='http://jabber.org/protocol/chatstates'/>" +
                      "<thread>abcd</thread></message>");

            await alice.SendMessageAsync(Bob, "And this one stays lying");

            await WaitForTheStore(Bob, 1);

            Assert.That(Server.GetAccount(Bob)!.OfflineMessages[0].Stanza,
                        Does.Contain("And this one stays lying"));

        }

        #endregion

        #region ANormalMessageWithOnlyAChatState_IsStored()

        /// <summary>
        /// The exception stands at <c>chat</c> and nowhere else - a
        /// <c>normal</c> with the same content is stored.
        /// </summary>
        /// <remarks>
        /// That is the letter of XEP-0160, section 3: For <c>normal</c> it says
        /// "SHOULD be stored offline" without any restriction, for <c>chat</c>
        /// with one. To draw the rule further than it is written would mean
        /// inventing a provision of one's own and calling it someone else's.
        /// </remarks>
        [Test]
        public async Task ANormalMessageWithOnlyAChatState_IsStored()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.SendRawAsync(
                      $"<message to='{Bob}' type='normal' id='normal-typing'>" +
                      "<composing xmlns='http://jabber.org/protocol/chatstates'/></message>");

            await WaitForTheStore(Bob, 1);

            Assert.That(Server.GetAccount(Bob)!.OfflineMessages[0].Stanza,
                        Does.Contain("chatstates"));

        }

        #endregion

        #region WhatCountsAsAChatStateOnlyMessage()

        /// <summary>
        /// The rule itself, without a net - together with the two cases that
        /// are unreachable over the store.
        /// </summary>
        /// <remarks>
        /// A message without children and one that cannot be read do not get
        /// this far in running operation - the framing sieves them out
        /// beforehand. The rule has to hold on its own nevertheless: It decides
        /// whether a message is discarded, and whoever carries it into another
        /// environment does not bring the sieves along.
        ///
        /// <b>In case of doubt "it is a message" holds:</b> What cannot be
        /// established as a chat state is stored. The reverse error would lose
        /// a message.
        /// </remarks>
        [Test]
        public void WhatCountsAsAChatStateOnlyMessage()
        {

            const String ChatState = "<composing xmlns='http://jabber.org/protocol/chatstates'/>";

            Assert.Multiple(() =>
            {

                Assert.That(XMPPServer.IsChatStateOnly($"<message>{ChatState}</message>"),
                            Is.True);

                Assert.That(XMPPServer.IsChatStateOnly($"<message>{ChatState}<thread>a</thread></message>"),
                            Is.True);

                // Not only <composing/>: XEP-0085 knows five states, and the
                // namespace decides, not the name.
                Assert.That(XMPPServer.IsChatStateOnly("<message><active xmlns='http://jabber.org/protocol/chatstates'/></message>"),
                            Is.True);

                Assert.That(XMPPServer.IsChatStateOnly("<message><thread>a</thread></message>"),
                            Is.False, "A thread alone is no chat state.");

                Assert.That(XMPPServer.IsChatStateOnly("<message/>"),
                            Is.False, "A message without children neither.");

                Assert.That(XMPPServer.IsChatStateOnly("<message><composing/></message>"),
                            Is.False, "Without the namespace it is just some element.");

                Assert.That(XMPPServer.IsChatStateOnly("<message><composing"),
                            Is.False, "And what cannot be read is a message.");

            });

        }

        #endregion

        #region AStoredMessage_IsDeliveredOnlyOnce()

        /// <summary>
        /// The handing over happens once. Afterwards the store is empty.
        /// </summary>
        /// <remarks>
        /// The difference to the stored subscription request, which is
        /// presented again at <i>every</i> login until it is answered. With a
        /// message there is no answering to fix the end on - whoever got it
        /// anew at every login could never get rid of it.
        /// </remarks>
        [Test]
        public async Task AStoredMessage_IsDeliveredOnlyOnce()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.SendMessageAsync(Bob, "Once");
            await WaitForTheStore(Bob, 1);

            var (first, firstInbox) = PreparedClient("bob");
            await first.ConnectAsync();
            await WaitFor(() => !firstInbox.IsEmpty, "the message at the first login");

            await DisconnectAndWaitAsync(first, Bob);

            var (second, secondInbox) = PreparedClient("bob");
            await second.ConnectAsync();

            await WaitAgainst(() => !secondInbox.IsEmpty,
                              "an already delivered message once more");

        }

        #endregion

        #region ANegativePriority_DoesNotEmptyTheStore()

        /// <summary>
        /// XEP-0160: The handing over happens as soon as the recipient sends
        /// "non-negative available presence" - a resource with a negative
        /// priority does not empty the store.
        /// </summary>
        /// <remarks>
        /// It is the same wish that section 8.5 respects for running operation:
        /// This device shall get nothing that only went to the account. A store
        /// emptying itself at the first sign of life of any resource would
        /// defeat it - and that at the most sensitive place, because the
        /// messages then lie on a device the user is not looking at right now.
        ///
        /// The second half belongs to it and checks more than the inversion:
        /// The same resource raises its priority without logging in anew. The
        /// handing over therefore happens at every non-negative available
        /// presence and not only at the <i>becoming</i> available - otherwise
        /// the store would lie until the next login although the user has just
        /// said that they are looking again.
        /// </remarks>
        [Test]
        public async Task ANegativePriority_DoesNotEmptyTheStore()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.SendMessageAsync(Bob, "For the human being, not for the device");
            await WaitForTheStore(Bob, 1);

            var (bob, inbox) = PreparedClient("bob", priority: -1);
            await bob.ConnectAsync();

            await WaitFor(() => Server.SessionOf(bob.FullJid!)?.PresencePriority == -1,
                          "the negative priority at the server");

            await WaitAgainst(() => !inbox.IsEmpty,
                              "the store on a device that does not want it");

            Assert.That(Server.GetAccount(Bob)!.OfflineMessages, Has.Count.EqualTo(1),
                        "The message has to keep lying.");

            // Now the user is looking after all - the same resource, a new priority.
            bob.Connection.PresencePriority = 0;
            await bob.Connection.SendPresenceAsync();

            await WaitFor(() => !inbox.IsEmpty,
                          "the store as soon as the resource accepts it again");

        }

        #endregion

        #region AnUnavailablePresence_DoesNotEmptyTheStore()

        /// <summary>
        /// The handing over happens to an <i>available</i> resource - a
        /// sign-off is none.
        /// </summary>
        /// <remarks>
        /// The case is inconspicuous and the trap sharp: A sign-off resets the
        /// priority of the session to 0, because a signed-off resource has no
        /// state to report. Whoever only asks for the priority sees a 0 at
        /// precisely this moment and empties the store into a stream that is
        /// just saying goodbye - the messages are gone then, without ever
        /// having been read.
        ///
        /// The state before is negative on purpose: With the usual 0 the store
        /// would already be emptied at the login, and the test would check
        /// nothing any more.
        /// </remarks>
        [Test]
        public async Task AnUnavailablePresence_DoesNotEmptyTheStore()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.SendMessageAsync(Bob, "Stays lying");
            await WaitForTheStore(Bob, 1);

            var (bob, inbox) = PreparedClient("bob", priority: -1);
            await bob.ConnectAsync();

            await WaitFor(() => Server.SessionOf(bob.FullJid!)?.PresencePriority == -1,
                          "the negative priority at the server");

            await bob.SendRawAsync("<presence type='unavailable'/>");

            await WaitFor(() => Server.SessionOf(bob.FullJid!)?.IsAvailable == false,
                          "the sign-off at the server");

            await WaitAgainst(() => Server.GetAccount(Bob)!.OfflineMessages.Count == 0,
                              "a store emptying itself into a sign-off");

            Assert.That(inbox, Is.Empty);

        }

        #endregion

        #region WithoutPresenceBroadcast_TheStoreIsStillDelivered()

        /// <summary>
        /// The handing over does not hang on the distributing of presence.
        /// </summary>
        /// <remarks>
        /// The two have nothing to do with each other but stand at the same
        /// place: Both begin with the first presence of a resource. Whoever
        /// switches the distributing off - to narrow a test down to one aspect,
        /// say - does not thereby want to shut down the user's mail. And the
        /// loss would be silent: The message stays in the store and would come
        /// out only at a login one has switched it back on for.
        /// </remarks>
        [Test]
        public async Task WithoutPresenceBroadcast_TheStoreIsStillDelivered()
        {

            Server.BroadcastPresence = false;

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.SendMessageAsync(Bob, "Nevertheless");
            await WaitForTheStore(Bob, 1);

            var (bob, inbox) = PreparedClient("bob");
            await bob.ConnectAsync();

            await WaitFor(() => !inbox.IsEmpty, "the handed-over message");

            Assert.That(Server.GetAccount(Bob)!.OfflineMessages, Is.Empty);

        }

        #endregion

        #region AStoredMessage_KeepsTheTimeItWasWritten()

        /// <summary>
        /// The recipient shows the time at which the message was written - not
        /// the one at which it reaches them.
        /// </summary>
        /// <remarks>
        /// The other half of <see cref="AStoredMessage_CarriesADelayStamp"/>.
        /// The server has written the stamp all along and the client never read
        /// it: <c>XMPPMessage.Timestamp</c> was the moment of reception, and a
        /// message from yesterday evening appeared after the login with the
        /// time of now. <b>A time that stands there and is not right is worse
        /// than none</b> - it invites answering a question that has long
        /// settled itself.
        ///
        /// What is checked is a time window around the sending. The exact
        /// number the test does not know - it comes from the server -, but the
        /// bounds it does: before the sending it cannot lie and after the
        /// logging in again neither.
        /// </remarks>
        [Test]
        public async Task AStoredMessage_KeepsTheTimeItWasWritten()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            var beforeSending = DateTime.Now.AddSeconds(-1);

            await alice.SendMessageAsync(Bob, "From yesterday");

            await WaitForTheStore(Bob, 1);

            var afterSending = DateTime.Now.AddSeconds(1);

            var (bob, inbox) = PreparedClient("bob");
            await bob.ConnectAsync();

            await WaitFor(() => !inbox.IsEmpty, "the handed-over message");

            inbox.TryDequeue(out var message);

            Assert.Multiple(() =>
            {

                Assert.That(message!.IsDelayed, Is.True,
                            "A handed-over message has to be recognisable as such.");

                Assert.That(message.Timestamp, Is.InRange(beforeSending, afterSending),
                            "What is shown is the time of reception instead of the one of writing.");

                Assert.That(message.ReceivedAt, Is.GreaterThanOrEqualTo(message.Timestamp),
                            "It arrived after the writing, not before it.");

                Assert.That(message.DelayedBy, Is.EqualTo(Server.Domain),
                            "XEP-0203, section 4: the server is what kept it.");

            });

        }

        #endregion

        #region ALiveMessage_IsNotDelayed()

        /// <summary>
        /// The counter-check: A message to a present recipient does not count
        /// as handed over.
        /// </summary>
        /// <remarks>
        /// Without it "always handed over" would be a passing solution, and
        /// every running conversation would carry the note. At the same time
        /// the test holds fast that the shown time is still the one of
        /// reception in the normal case - for everything running the two are
        /// the same.
        /// </remarks>
        [Test]
        public async Task ALiveMessage_IsNotDelayed()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            var (bob, inbox) = PreparedClient("bob");

            await bob.ConnectAsync();

            var before = DateTime.Now.AddSeconds(-1);

            await alice.SendMessageAsync(Bob, "Right now");

            await WaitFor(() => !inbox.IsEmpty, "the delivered message");

            inbox.TryDequeue(out var message);

            Assert.Multiple(() =>
            {

                Assert.That(message!.IsDelayed, Is.False);

                Assert.That(message.Timestamp,  Is.InRange(before, DateTime.Now.AddSeconds(1)));

                Assert.That(message.DelayedBy,  Is.Null);

            });

        }

        #endregion

        #region AStoredMessage_CarriesADelayStamp()

        /// <summary>
        /// XEP-0160 and XEP-0203: A handed-over message carries a
        /// <c>&lt;delay/&gt;</c> with the moment of arrival.
        /// </summary>
        /// <remarks>
        /// Without the stamp a message from yesterday claims to be from now -
        /// the recipient cannot see the difference and answers a question that
        /// has long settled itself. The stamp is thereby no ornament but the
        /// only way to communicate the delay at all.
        /// </remarks>
        [Test]
        public async Task AStoredMessage_CarriesADelayStamp()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.SendRawAsync(
                      $"<message to='{Bob}' type='chat' id='with-stamp'><body>From yesterday</body></message>");

            await WaitForTheStore(Bob, 1);

            var arrived      = new ConcurrentQueue<String>();
            var bob          = CreateClient("bob");

            bob.Connection.OnRawXml += (timestamp, sender, xml, ct) =>
            {
                if (xml.Contains("with-stamp", StringComparison.Ordinal))
                    arrived.Enqueue(xml);

                return Task.CompletedTask;

            };

            await bob.ConnectAsync();

            await WaitFor(() => !arrived.IsEmpty, "the handed-over message on the wire");

            arrived.TryDequeue(out var stanza);

            Assert.Multiple(() =>
            {

                Assert.That(stanza, Does.Contain("urn:xmpp:delay"),
                            "A handed-over message has to communicate its delay.");

                Assert.That(stanza, Does.Contain($"from='{Server.Domain}'"),
                            "XEP-0203: The server sets the stamp, not the sender.");

                Assert.That(stanza, Does.Match(@"stamp='\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z'"),
                            "XEP-0082 demands a UTC time.");

            });

        }

        #endregion

        #region WithoutTheStore_TheSenderIsTold()

        /// <summary>
        /// The second path permitted by section 8.5.2.2.1: no storing, but
        /// <c>&lt;service-unavailable/&gt;</c> to the sender.
        /// </summary>
        /// <remarks>
        /// It is not the worse one - it is only the less convenient one. What
        /// the section demands of both is the honesty: The sender learns in
        /// every case where they stand. Without this test only the convenient
        /// path would be established, and a server without a store would fall
        /// back on the discarding silently.
        /// </remarks>
        [Test]
        public async Task WithoutTheStore_TheSenderIsTold()
        {

            Server.StoreOfflineMessages = false;

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            var errors = new ConcurrentQueue<StanzaError>();
            alice.Connection.OnStanzaError += (timestamp, sender, from, e, ct) => { errors.Enqueue(e); return Task.CompletedTask; };

            await alice.SendMessageAsync(Bob, "Does not arrive");

            await WaitFor(() => !errors.IsEmpty, "the refusal at the sender");

            errors.TryDequeue(out var refused);

            Assert.Multiple(() =>
            {

                Assert.That(refused!.Condition, Is.EqualTo("service-unavailable"));

                Assert.That(Server.GetAccount(Bob)!.OfflineMessages, Is.Empty,
                            "Without a store nothing is stored.");

            });

        }

        #endregion

        #region TheLimit_RefusesTheNewMessageAndKeepsTheStoredOnes()

        /// <summary>
        /// If the store is full, the new message is turned away - and no
        /// already stored one is displaced.
        /// </summary>
        /// <remarks>
        /// What is checked is not only <i>that</i> the limit takes hold, but in
        /// which direction. Both directions lose a message, but only one of
        /// them tells anybody: Whoever turns away answers the sender; whoever
        /// displaces discards a message the sender assumes to be lying ready
        /// and the recipient never learns existed. A limit that displaces would
        /// also be the attack itself - whoever fills the store up would thereby
        /// delete other people's mail.
        /// </remarks>
        [Test]
        public async Task TheLimit_RefusesTheNewMessageAndKeepsTheStoredOnes()
        {

            Server.MaxStoredOfflineMessages = 1;

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            var errors = new ConcurrentQueue<StanzaError>();
            alice.Connection.OnStanzaError += (timestamp, sender, from, e, ct) => { errors.Enqueue(e); return Task.CompletedTask; };

            await alice.SendMessageAsync(Bob, "The first one");
            await WaitForTheStore(Bob, 1);

            await alice.SendMessageAsync(Bob, "The second one");
            await WaitFor(() => !errors.IsEmpty, "the refusal of the second one");

            Assert.Multiple(() =>
            {

                Assert.That(Server.GetAccount(Bob)!.OfflineMessages, Has.Count.EqualTo(1));

                Assert.That(Server.GetAccount(Bob)!.OfflineMessages[0].Stanza,
                            Does.Contain("The first one"),
                            "The message stored first stays.");

            });

        }

        #endregion

        #region AnAbsentRecipient_DoesNotSuppressTheSentCarbon()

        /// <summary>
        /// XEP-0280: The other devices of the <i>sender</i> get their sent copy
        /// even when the message lands in the store.
        /// </summary>
        /// <remarks>
        /// The carbon says nothing about the delivery but about the writing:
        /// "That is what you sent." That stays true whether the recipient was
        /// there or not. Were it to fail here, the user would have a
        /// conversation trace in which their own messages to absent people are
        /// missing - and precisely the ones whose whereabouts interest them.
        ///
        /// The test holds fast a line that ran along unnoticed before: Up to
        /// here this case fell through the same code as the successful
        /// delivery, because nothing caught it beforehand. Now the offline
        /// branch does, and what it does not expressly take along would be lost
        /// silently.
        /// </remarks>
        [Test]
        public async Task AnAbsentRecipient_DoesNotSuppressTheSentCarbon()
        {

            var mobile   = await ConnectClientAsync("alice");
            var desktop  = await ConnectClientAsync("alice");

            Server.AddAccount("bob");

            await WaitFor(() => Server.SessionsOf(mobile.BareJid).All(s => s.CarbonsEnabled),
                          "the carbons on both resources");

            var carbons = new ConcurrentQueue<CarbonMessage>();
            desktop.OnCarbonMessage += (timestamp, sender, c, ct) => { carbons.Enqueue(c); return Task.CompletedTask; };

            await mobile.SendMessageAsync(Bob, "For later");

            await WaitForTheStore(Bob, 1);

            await WaitFor(() => !carbons.IsEmpty, "the sent copy on the other device");

            carbons.TryDequeue(out var carbon);

            Assert.Multiple(() =>
            {
                Assert.That(carbon!.IsSent, Is.True);
                Assert.That(carbon.Body,    Is.EqualTo("For later"));
            });

        }

        #endregion

        #region AChatToAnUnknownResource_IsHandledLikeTheAccount()

        /// <summary>
        /// Section 8.5.3.2.1: A <c>chat</c> to a resource that does not exist
        /// is handled as if it had gone to the account - stored when nobody is
        /// there, and delivered when somebody is.
        /// </summary>
        /// <remarks>
        /// Both halves belong into the same test. To implement only the storing
        /// would be worse than the state so far: The message would land in the
        /// store while the recipient sits next to it with another resource and
        /// waits.
        ///
        /// The case is an everyday one. A client answers to the full JID it saw
        /// last; if the conversation partner switches the device in the
        /// meantime, that resource is gone. That is precisely why the section
        /// makes an exception for <c>chat</c> from the rule that a resource
        /// that does not match is the end.
        /// </remarks>
        [Test]
        public async Task AChatToAnUnknownResource_IsHandledLikeTheAccount()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            var vanished = $"{Bob}/gone-already";

            await alice.SendRawAsync(
                      $"<message to='{vanished}' type='chat' id='stored'><body>Are you gone?</body></message>");

            await WaitForTheStore(Bob, 1);

            // Second half: Bob is there, only under a different name.
            var (bob, inbox) = PreparedClient("bob");
            bob.Connection.Resource = "New";
            await bob.ConnectAsync();

            await WaitFor(() => inbox.Any(m => m.MessageId == "stored"),
                          "the handed-over message");

            await alice.SendRawAsync(
                      $"<message to='{vanished}' type='chat' id='delivered'><body>Not after all</body></message>");

            await WaitFor(() => inbox.Any(m => m.MessageId == "delivered"),
                          "the delivery to the reachable resource");

            Assert.That(Server.GetAccount(Bob)!.OfflineMessages, Is.Empty,
                        "As long as a resource is reachable, nothing is stored.");

        }

        #endregion

        #region AnUnknownResource_StoresNothingForTheOtherTypes()

        /// <summary>
        /// The exception holds only for <c>chat</c>. A <c>normal</c> to a
        /// resource that does not exist is discarded silently according to
        /// section 8.5.3.2.1.
        /// </summary>
        /// <remarks>
        /// The difference looks quirky and has a reason: Whoever writes to a
        /// full JID means this resource. With a conversation that is a
        /// shorthand for "my counterpart", with everything else a statement the
        /// sender wanted that way.
        ///
        /// Without this test the collection would pass even if the exception
        /// held for every kind - and the store would fill up with messages to
        /// addresses that never existed.
        /// </remarks>
        [Test]
        public async Task AnUnknownResource_StoresNothingForTheOtherTypes()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            var vanished = $"{Bob}/gone-already";

            await alice.SendRawAsync(
                      $"<message to='{vanished}' id='without-a-type'><body>To exactly this resource</body></message>");

            await alice.SendRawAsync(
                      $"<message to='{vanished}' type='headline' id='notice'><body>Price has fallen</body></message>");

            // Afterwards a chat - it has to be stored and thereby establishes
            // that the store ran while the two others fell through.
            await alice.SendRawAsync(
                      $"<message to='{vanished}' type='chat' id='conversation'><body>Are you gone?</body></message>");

            await WaitForTheStore(Bob, 1);

            Assert.That(Server.GetAccount(Bob)!.OfflineMessages[0].Stanza,
                        Does.Contain("conversation"));

        }

        #endregion

        #region TheDelayStampIsAppendedToBothFormsOfAMessage()

        /// <summary>
        /// The stamp is appended, and that also when the message comes along as
        /// an empty element.
        /// </summary>
        /// <remarks>
        /// <c>&lt;message .../&gt;</c> is no made-up case: A client may send a
        /// message without child elements, it is a <c>chat</c> like any other
        /// and is therefore stored. Without resolving the empty element the
        /// stamp would land behind the end of the stanza - and would thereby be
        /// no part of the message any more.
        ///
        /// The moment carries an offset against UTC on purpose. With
        /// <c>TimeSpan.Zero</c> the local time would be the same number as the
        /// world time, and a stamp in local time - which XEP-0082 does not
        /// permit - would not stand out.
        /// </remarks>
        [Test]
        public void TheDelayStampIsAppendedToBothFormsOfAMessage()
        {

            var arrivedAt = new DateTimeOffset(2026, 7, 29, 16, 5, 9, TimeSpan.FromHours(2));

            var withContent = XMPPServer.WithDelay(
                                new OfflineMessage("<message to='bob@localhost'><body>Hello</body></message>",
                                                   arrivedAt),
                                "localhost");

            var empty     = XMPPServer.WithDelay(
                                new OfflineMessage("<message to='bob@localhost' type='chat'/>",
                                                   arrivedAt),
                                "localhost");

            Assert.Multiple(() =>
            {

                Assert.That(withContent,
                            Is.EqualTo("<message to='bob@localhost'><body>Hello</body>" +
                                       "<delay xmlns='urn:xmpp:delay' from='localhost' " +
                                       "stamp='2026-07-29T14:05:09Z'>Offline Storage</delay></message>"));

                Assert.That(empty,
                            Is.EqualTo("<message to='bob@localhost' type='chat'>" +
                                       "<delay xmlns='urn:xmpp:delay' from='localhost' " +
                                       "stamp='2026-07-29T14:05:09Z'>Offline Storage</delay></message>"));

            });

        }

        #endregion

        #region TheStoreIsAnnouncedInDiscoInfo()

        /// <summary>
        /// XEP-0160, section 4: A server with an offline store announces
        /// <c>msgoffline</c> in disco#info.
        /// </summary>
        /// <remarks>
        /// For the client that is the difference between "lies ready" and "is
        /// gone": Without the announcement it would have to conclude from the
        /// absence of an error that something was stored - and an error can be
        /// late.
        ///
        /// The counter-check is made as well. An announcement that is always
        /// there says nothing; it would then even be wrong, because a server
        /// without a store would promise something it does not do.
        /// </remarks>
        [Test]
        public async Task TheStoreIsAnnouncedInDiscoInfo()
        {

            var withStore    = await AskDiscoInfoAsync(true);
            var withoutStore = await AskDiscoInfoAsync(false);

            Assert.Multiple(() =>
            {

                Assert.That(withStore,    Does.Contain("msgoffline"));

                Assert.That(withoutStore, Does.Not.Contain("msgoffline"),
                            "Without a store the server must not announce it.");

            });

        }

        /// <summary>Asks the server for its features.</summary>
        private async Task<String> AskDiscoInfoAsync(Boolean storeOfflineMessages)
        {

            Server.StoreOfflineMessages = storeOfflineMessages;

            var client    = await ConnectClientAsync("alice");
            var replies   = new ConcurrentQueue<String>();
            var id        = $"disco-{storeOfflineMessages}";

            client.Connection.OnRawXml += (timestamp, sender, xml, ct) =>
            {
                if (xml.Contains("<<<", StringComparison.Ordinal) &&
                    xml.Contains(id,    StringComparison.Ordinal))
                {
                    replies.Enqueue(xml);
                }

                return Task.CompletedTask;

            };

            await client.SendRawAsync(
                      $"<iq type='get' id='{id}' to='{Server.Domain}'>" +
                      "<query xmlns='http://jabber.org/protocol/disco#info'/></iq>");

            await WaitFor(() => !replies.IsEmpty, "the disco#info reply of the server");

            replies.TryDequeue(out var reply);

            await client.DisconnectAsync();

            return reply!;

        }

        #endregion

    }

}
