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

using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// OMEMO in a room, from one end to the other - with the room played by
    /// this test.
    /// </summary>
    /// <remarks>
    /// <b>The test server has no room service</b>, so the occupant presences
    /// and the reflected message are written straight into the two clients'
    /// sessions, as <see cref="MultiUserChatWiringTests"/> has done since D116.
    /// That is not a weaker test than one against a real service, for what is
    /// being asked here: a room <i>is</i> a reflector, and everything this lane
    /// gets wrong is on the client side of it. What a real service settles -
    /// whether it writes the real addresses in at all when asked to - is a
    /// question for Prosody and ejabberd, and is asked there.
    ///
    /// Playing the room by hand also buys the one round that could not be had
    /// otherwise: a service that <b>lies</b> about who is behind a nickname.
    /// </remarks>
    [TestFixture]
    public class OmemoRoomEndToEndTests : AXMPPTests
    {

        #region Data

        private JID    RoomJid  => JID.Parse($"chat@conference.{Server.Domain}");
        private JID    AliceJid => JID.Parse($"alice@{Server.Domain}");
        private JID    BobJid   => JID.Parse($"bob@{Server.Domain}");

        #endregion

        #region Helper functions

        private async Task<XMPPSession> SessionOfAsync(XMPPClient client)
        {

            await WaitFor(() => Server.SessionOf(client.FullJid.ToString()) is not null,
                          "the server session for the client");

            return Server.SessionOf(client.FullJid.ToString())!;

        }

        /// <summary>
        /// The presence a non-anonymous room sends for an occupant - with the
        /// real address in it, which is the whole reason this lane needs
        /// <c>muc#roomconfig_whois = anyone</c>.
        /// </summary>
        private string OccupantPresence(string   nick,
                                        JID      to,
                                        JID?     realJid  = null,
                                        bool     self     = false)

            => $"<presence from='{RoomJid}/{nick}' to='{to}'>" +
                   "<x xmlns='http://jabber.org/protocol/muc#user'>" +
                       $"<item affiliation='none' role='participant'" +
                       (realJid is not null ? $" jid='{realJid}/device'" : "") + "/>" +
                       // 100 is what says the room is non-anonymous, and 110
                       // what says this presence is about the recipient.
                       "<status code='100'/>" +
                       (self ? $"<status code='{MucStatus.Self}'/>" : "") +
                   "</x>" +
               "</presence>";

        /// <summary>
        /// Walks a client into the room with the two others present.
        /// </summary>
        private async Task JoinAsync(XMPPClient   client,
                                     XMPPSession  session,
                                     string       ownNick,
                                     params (string Nick, JID? Real)[] others)
        {

            var joining = client.JoinRoomAsync(RoomJid, ownNick);

            foreach (var (nick, real) in others)
                await session.SendAsync(OccupantPresence(nick, client.FullJid, real));

            await session.SendAsync(OccupantPresence(ownNick, client.FullJid,
                                                     client.BareJid, self: true));

            var outcome = await joining;

            Assert.That(outcome.Joined, Is.True,
                        $"{ownNick} did not get into the room, so nothing below says anything.");

        }

        /// <summary>
        /// The encrypted stanza Alice sent to the room, as the server saw it.
        /// </summary>
        /// <remarks>
        /// Waited for rather than read straight off. Sending returns once the
        /// stanza is on the socket, and the server records it a moment later -
        /// so reading the list at once finds nothing, sometimes.
        /// </remarks>
        private static async Task<string> EncryptedStanzaInAsync(XMPPSession session, string messageId)
        {

            bool There() => session.Received.Any(stanza => stanza.Contains($"id='{messageId}'", StringComparison.Ordinal));

            await WaitFor(There, $"the stanza {messageId} to reach the server");

            return session.Received.First(stanza => stanza.Contains($"id='{messageId}'", StringComparison.Ordinal));

        }

        /// <summary>
        /// The room handing a message on: the same stanza, re-addressed.
        /// </summary>
        /// <remarks>
        /// Which is all a room does with a message - it changes the
        /// <c>from</c> to the occupant address and sends it to everybody. The
        /// <c>to</c> is rewritten as well, because the stanza as sent was
        /// addressed to the room.
        /// </remarks>
        private string Reflected(string stanza, string fromNick, JID to)
        {

            var body = stanza[(stanza.IndexOf('>') + 1)..];

            return $"<message from='{RoomJid}/{fromNick}' to='{to}' type='groupchat' " +
                   $"id='{ExtractId(stanza)}'>{body}";

        }

        private static string ExtractId(string stanza)
        {

            var at = stanza.IndexOf("id='", StringComparison.Ordinal) + 4;

            return stanza[at..stanza.IndexOf('\'', at)];

        }

        private async Task<(XMPPClient Alice, XMPPSession AliceSession,
                            XMPPClient Bob,   XMPPSession BobSession)> TwoInARoomAsync()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);
            var bob   = await ConnectClientAsync("bob",   createAccount: false);

            Assert.Multiple(() =>
            {
                Assert.That(alice.EnableOmemoAsync().GetAwaiter().GetResult(), Is.True,
                            "Alice could not switch OMEMO on.");
                Assert.That(bob.EnableOmemoAsync().GetAwaiter().GetResult(), Is.True,
                            "Bob could not switch OMEMO on.");
            });

            var aliceSession = await SessionOfAsync(alice);
            var bobSession   = await SessionOfAsync(bob);

            await JoinAsync(alice, aliceSession, "alice", ("bob",   BobJid));
            await JoinAsync(bob,   bobSession,   "bob",   ("alice", AliceJid));

            return (alice, aliceSession, bob, bobSession);

        }

        #endregion


        #region WhatIsSaidInARoomIsReadOnlyByThePeopleInIt()

        /// <summary>
        /// Alice writes into the room, Bob reads it - and the plaintext stands
        /// on no stanza the server saw.
        /// </summary>
        /// <remarks>
        /// The last assertion is the one without which this round would be
        /// worth nothing: a groupchat message that was never encrypted at all
        /// reaches Bob just as well, and every other assertion here would pass.
        /// </remarks>
        [Test]
        public async Task WhatIsSaidInARoomIsReadOnlyByThePeopleInIt()
        {

            var (alice, aliceSession, bob, bobSession) = await TwoInARoomAsync();

            XMPPMessage?     received  = null;
            OmemoDecrypted?  info      = null;

            bob.OnEncryptedMessage += (timestamp, sender, message, omemo, ct) =>
            {
                received = message;
                info     = omemo;
                return Task.CompletedTask;
            };

            const String secret = "The meeting is at eight, and not where it says.";

            var sent = await alice.SendEncryptedRoomMessageAsync(RoomJid, secret);

            Assert.Multiple(() =>
            {

                Assert.That(sent.Refusal,    Is.Null, "The room refused: " + sent.Refusal);
                Assert.That(sent.Sent,       Is.True);
                Assert.That(sent.Recipients, Is.EqualTo(new[] { BobJid }),
                            "Encrypted to the wrong set of people.");

                Assert.That(sent.Skipped, Is.Empty,
                            "A device was left out: " +
                            String.Join(", ", sent.Skipped.Select(s => $"{s.Jid}/{s.DeviceId}: {s.Reason}")));

            });

            // The room hands it on.
            var stanza = await EncryptedStanzaInAsync(aliceSession, sent.MessageId!);

            await bobSession.SendAsync(Reflected(stanza, "alice", bob.FullJid));

            await WaitFor(() => received is not null, "the decrypted message at Bob");

            Assert.Multiple(() =>
            {

                Assert.That(received!.Body, Is.EqualTo(secret));

                Assert.That(received.Type, Is.EqualTo(MessageType.GroupChat),
                            "A line out of a room arrived as an ordinary chat message.");

                Assert.That(received.From, Is.EqualTo(JID.Parse($"{RoomJid}/alice")),
                            "The message is attributed to a real address. In a room somebody speaks " +
                            "from their place in it, and the room shows that name nowhere else.");

                Assert.That(info!.EnvelopeFrom, Is.EqualTo(AliceJid),
                            "The envelope does not name the sender, so nothing checked who wrote this.");

                Assert.That(info.SenderDeviceId, Is.EqualTo(alice.Omemo!.Identity.DeviceId));

                var everything = Server.Sessions.SelectMany(s => s.Received.Concat(s.Sent)).ToList();

                Assert.That(everything.Any(f => f.Contains(secret, StringComparison.Ordinal)), Is.False,
                            "The plaintext stands in a stanza the server has seen.");

                Assert.That(everything.Any(f => f.Contains("urn:xmpp:omemo:2", StringComparison.Ordinal)), Is.True,
                            "No OMEMO stanza went over the wire at all - then this is checking " +
                            "something else.");

            });

        }

        #endregion

        #region ARoomThatLiesAboutWhoSomebodyIsGetsNothing()

        /// <summary>
        /// <b>The round this lane exists for.</b>
        /// </summary>
        /// <remarks>
        /// In a room the mapping from a nickname to a real address comes from
        /// the service and from nowhere else. So a service that wanted to read
        /// along could put its own account behind a nickname and be encrypted
        /// to - there is no second source to check it against.
        ///
        /// Except inside the encryption. XEP-0420 has the sender write their own
        /// address into the envelope, and it is compared with the sender the
        /// stanza claims. Here the room tells Bob that "alice" is really
        /// <c>mallory</c>: Bob looks up a session for mallory, the message was
        /// written by Alice, and the two do not meet.
        ///
        /// <b>What this does not do is make the room trustworthy</b>, and the
        /// distinction matters. The service still decides who is in the
        /// conversation - it can add an occupant, and then that occupant is
        /// encrypted to quite correctly. What it cannot do is make one person's
        /// words appear to be another's.
        /// </remarks>
        [Test]
        public async Task ARoomThatLiesAboutWhoSomebodyIsGetsNothing()
        {

            var (alice, aliceSession, bob, bobSession) = await TwoInARoomAsync();

            Server.AddAccount("mallory");

            // The room changes its story: "alice" is now said to be somebody
            // else entirely. Bob has no reason to doubt it - the service is the
            // only source there is.
            await bobSession.SendAsync(
                      OccupantPresence("alice", bob.FullJid,
                                       JID.Parse($"mallory@{Server.Domain}")));

            await WaitFor(() => bob.Room(RoomJid)!.Occupants["alice"].RealJid?.Bare
                                    == JID.Parse($"mallory@{Server.Domain}"),
                          "the room's new story about who Alice is");

            var arrived = 0;

            bob.OnEncryptedMessage += (t, s, m, o, ct) => { Interlocked.Increment(ref arrived); return Task.CompletedTask; };

            var sent = await alice.SendEncryptedRoomMessageAsync(RoomJid, "Only for the people here.");

            Assert.That(sent.Sent, Is.True, "Alice could not send at all: " + sent.Refusal);

            await bobSession.SendAsync(
                      Reflected(await EncryptedStanzaInAsync(aliceSession, sent.MessageId!), "alice", bob.FullJid));

            // Nothing is expected to happen, so the wait is for it to have had
            // every chance to.
            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.That(arrived, Is.Zero,
                        "A message was accepted under an identity the room invented. The envelope " +
                        "from XEP-0420 is the only thing standing between a room service and " +
                        "putting words in somebody's mouth.");

        }

        #endregion

        #region OurOwnLineComesBackAndIsNotAnError()

        /// <summary>
        /// Every line one writes in a room returns unreadable.
        /// </summary>
        /// <remarks>
        /// A room sends every message to everybody, the sender included, and an
        /// OMEMO element holds no key for the device that made it. So the
        /// reflection of one's own message is by construction one there is
        /// nothing in for us - not an edge case but every single line.
        ///
        /// Checked by counting what Alice is told about her own message, which
        /// has to be nothing at all.
        /// </remarks>
        [Test]
        public async Task OurOwnLineComesBackAndIsNotAnError()
        {

            var (alice, aliceSession, _, _) = await TwoInARoomAsync();

            var toldAboutSomething = 0;

            alice.OnEncryptedMessage += (t, s, m, o, ct) => { Interlocked.Increment(ref toldAboutSomething); return Task.CompletedTask; };
            alice.OnMessage          += (t, s, m,    ct) => { Interlocked.Increment(ref toldAboutSomething); return Task.CompletedTask; };

            var sent = await alice.SendEncryptedRoomMessageAsync(RoomJid, "Said once.");

            Assert.That(sent.Sent, Is.True, sent.Refusal);

            // The room hands the message back to the one who wrote it.
            await aliceSession.SendAsync(
                      Reflected(await EncryptedStanzaInAsync(aliceSession, sent.MessageId!), "alice", alice.FullJid));

            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.That(toldAboutSomething, Is.Zero,
                        "Alice was told something about her own line coming back. It cannot be " +
                        "decrypted - no OMEMO element carries a key for the device that made it - " +
                        "so a client without this branch reports every line it writes as unreadable.");

        }

        #endregion

        #region ASemiAnonymousRoomRefusesBeforeAnythingIsEncrypted()

        /// <summary>
        /// The ordinary room, and the refusal that says why.
        /// </summary>
        /// <remarks>
        /// Nothing goes on the wire, which is the half worth checking: a
        /// refusal after the encrypting would still have asked a server for
        /// bundles and told it who was about to be written to.
        /// </remarks>
        [Test]
        public async Task ASemiAnonymousRoomRefusesBeforeAnythingIsEncrypted()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);

            Assert.That(await alice.EnableOmemoAsync(), Is.True);

            var session = await SessionOfAsync(alice);

            // The default room: nicknames and nothing else. No 'jid' attribute
            // on the other occupant, which is what a semi-anonymous service
            // sends everybody who is not a moderator.
            var joining = alice.JoinRoomAsync(RoomJid, "alice");

            await session.SendAsync($"<presence from='{RoomJid}/bob' to='{alice.FullJid}'>" +
                                        "<x xmlns='http://jabber.org/protocol/muc#user'>" +
                                            "<item affiliation='none' role='participant'/>" +
                                        "</x></presence>");

            await session.SendAsync($"<presence from='{RoomJid}/alice' to='{alice.FullJid}'>" +
                                        "<x xmlns='http://jabber.org/protocol/muc#user'>" +
                                            "<item affiliation='none' role='participant'/>" +
                                            $"<status code='{MucStatus.Self}'/>" +
                                        "</x></presence>");

            Assert.That((await joining).Joined, Is.True);

            var before = session.Received.Count;

            var sent   = await alice.SendEncryptedRoomMessageAsync(RoomJid, "Not going anywhere.");

            Assert.Multiple(() =>
            {

                Assert.That(sent.Sent,    Is.False, "A message went into a room of nicknames.");

                Assert.That(sent.Refusal, Does.Contain("semi-anonymous"),
                            "The refusal does not name the setting, so nobody reading it can act " +
                            "on it: it is one configuration away.");

                Assert.That(session.Received.Count, Is.EqualTo(before),
                            "Something went to the server anyway - a refusal that first asks for " +
                            "bundles has told the server who was about to be written to.");

            });

            Assert.That(alice.CannotEncryptInRoom(RoomJid), Is.EqualTo(sent.Refusal),
                        "The question asked beforehand and the refusal afterwards disagree, so a " +
                        "client cannot draw its lock from the one and trust the other.");

        }

        #endregion

    }

}
