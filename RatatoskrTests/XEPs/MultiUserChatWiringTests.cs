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
    /// XEP-0045 where it meets everything else: a room's stanzas arriving over a
    /// real connection.
    /// </summary>
    /// <remarks>
    /// <see cref="MultiUserChatTests"/> drives the manager directly and checks
    /// what it makes of what it is given. What it cannot check is the question
    /// the whole extension turns on: <b>a room's presences and a contact's
    /// presences are the same stanza shape arriving over the same
    /// connection</b>, and everything in this library that reads a presence
    /// files it in the roster. One missing branch and a room of fifty people
    /// becomes fifty contacts, each of them a stranger with an address that
    /// exists only inside that room.
    ///
    /// There is no room service here to ask, so the room's half of the
    /// conversation is put in by hand through the server session - the same way
    /// <c>ErrorHandlingTests</c> puts in a stream error. What is under test is
    /// not the room; it is our side of the wire.
    /// </remarks>
    [TestFixture]
    public class MultiUserChatWiringTests : AXMPPTests
    {

        #region Helper functions

        private async Task<XMPPSession> SessionOfAsync(XMPPClient client)
        {

            await WaitFor(() => Server.SessionOf(client.FullJid.ToString()) is not null,
                          "the server session for the client");

            return Server.SessionOf(client.FullJid.ToString())!;

        }

        private static String OccupantPresence(JID      room,
                                               String   nick,
                                               JID      to,
                                               String   affiliation  = "none",
                                               String   role         = "participant",
                                               Int32?   status       = null)

            => $"<presence from='{room}/{nick}' to='{to}'>" +
                   "<x xmlns='http://jabber.org/protocol/muc#user'>" +
                       $"<item affiliation='{affiliation}' role='{role}'/>" +
                       (status.HasValue ? $"<status code='{status.Value}'/>" : "") +
                   "</x>" +
               "</presence>";

        #endregion


        #region ARoomIsNotTheRoster()

        /// <summary>
        /// Nobody in a room becomes a contact, and the room does not become one
        /// either.
        /// </summary>
        /// <remarks>
        /// <b>The one test this whole extension needed.</b> Without the branch
        /// that asks whether the sender is a room, every occupant runs into
        /// <c>Roster.UpdatePresenceAsync</c> - and a roster is a list of people
        /// one has a subscription with, not a list of everybody who happened to
        /// be in a room once.
        ///
        /// It is not only untidy. A contact list built that way survives the
        /// visit, fills up with nicknames that mean nothing outside the room,
        /// and reports them as online for as long as nobody notices.
        /// </remarks>
        [Test]
        public async Task ARoomIsNotTheRoster()
        {

            var client   = await ConnectClientAsync();
            var session  = await SessionOfAsync(client);

            var room     = JID.Parse($"chat@conference.{Server.Domain}");
            var before   = client.GetContacts().Count;

            var joining  = client.JoinRoomAsync(room, "me");

            await session.SendAsync(OccupantPresence(room, "alice", client.FullJid,
                                                     affiliation: "owner", role: "moderator"));

            await session.SendAsync(OccupantPresence(room, "bob", client.FullJid));

            await session.SendAsync(OccupantPresence(room, "me", client.FullJid,
                                                     status: MucStatus.Self));

            var outcome = await joining;

            Assert.Multiple(() =>
            {

                Assert.That(outcome.Joined, Is.True,
                            "The join did not come back over a real connection - so nothing below " +
                            "says anything.");

                Assert.That(outcome.Room!.Occupants.Count, Is.EqualTo(3));

                Assert.That(client.GetContacts().Count, Is.EqualTo(before),
                            "Somebody from the room landed in the roster. A roster is a list of " +
                            "people one has a subscription with.");

                Assert.That(client.GetContact(room), Is.Null,
                            "The room itself became a contact.");

                Assert.That(client.GetContact(JID.Parse($"{room}/alice")), Is.Null,
                            "An occupant became a contact, under an address that exists only " +
                            "inside that room.");

            });

        }

        #endregion

        #region TheSubjectArrivesAsASubjectAndNotAsAMessage()

        /// <summary>
        /// A room sends its subject at the end of every join.
        /// </summary>
        /// <remarks>
        /// Handed on as an ordinary message it would appear in the conversation
        /// as a line somebody wrote - once per join, from somebody who said
        /// nothing.
        /// </remarks>
        [Test]
        public async Task TheSubjectArrivesAsASubjectAndNotAsAMessage()
        {

            var client   = await ConnectClientAsync();
            var session  = await SessionOfAsync(client);

            var room     = JID.Parse($"chat@conference.{Server.Domain}");

            var messages = new ConcurrentQueue<XMPPMessage>();
            var subjects = new ConcurrentQueue<String>();

            client.OnMessage     += (t, s, m, ct) => { messages.Enqueue(m); return Task.CompletedTask; };
            client.OnRoomSubject += (t, s, r, subject, by, ct) => { subjects.Enqueue(subject); return Task.CompletedTask; };

            var joining = client.JoinRoomAsync(room, "me");
            await session.SendAsync(OccupantPresence(room, "me", client.FullJid, status: MucStatus.Self));
            await joining;

            await session.SendAsync($"<message from='{room}/alice' to='{client.FullJid}' type='groupchat'>" +
                                    "<subject>Deployment on Friday</subject></message>");

            await WaitFor(() => subjects.Count == 1, "the subject of the room");

            await session.SendAsync($"<message from='{room}/alice' to='{client.FullJid}' type='groupchat'>" +
                                    "<body>Is it?</body></message>");

            await WaitFor(() => messages.Count == 1, "the message from the room");

            Assert.Multiple(() =>
            {

                Assert.That(client.Room(room)!.Subject, Is.EqualTo("Deployment on Friday"));

                Assert.That(messages.Single().Body, Is.EqualTo("Is it?"),
                            "The subject arrived as a message as well, so the conversation now " +
                            "holds a line nobody wrote.");

                Assert.That(messages.Single().Type, Is.EqualTo(MessageType.GroupChat));

            });

        }

        #endregion

        #region ARoomMessageCanBeAnswered()

        /// <summary>
        /// XEP-0461 in a room: the reference is the name the room gave the
        /// message, never the <c>id</c> of the stanza.
        /// </summary>
        /// <remarks>
        /// Written in D114 and until now not checkable: the rule needs a room
        /// message with a <c>&lt;stanza-id/&gt;</c> of the room's on it, and
        /// without XEP-0045 there was no room for one to come from. This is that
        /// check, with the room's half put in by hand.
        /// </remarks>
        [Test]
        public async Task ARoomMessageCanBeAnswered()
        {

            var client   = await ConnectClientAsync();
            var session  = await SessionOfAsync(client);

            var room     = JID.Parse($"chat@conference.{Server.Domain}");

            var messages = new ConcurrentQueue<XMPPMessage>();
            client.OnMessage += (t, s, m, ct) => { messages.Enqueue(m); return Task.CompletedTask; };

            var joining = client.JoinRoomAsync(room, "me");
            await session.SendAsync(OccupantPresence(room, "me", client.FullJid, status: MucStatus.Self));
            await joining;

            await session.SendAsync($"<message from='{room}/alice' to='{client.FullJid}' type='groupchat' id='alices-own'>" +
                                    "<body>Coming along?</body>" +
                                    $"<stanza-id xmlns='urn:xmpp:sid:0' id='room-42' by='{room}'/>" +
                                    "</message>");

            await WaitFor(() => messages.Count == 1, "the message from the room");

            var arrived = messages.Single();

            Assert.Multiple(() =>
            {

                Assert.That(arrived.ReplyableId, Is.EqualTo("room-42"),
                            "In a room the id of the stanza must not be used (XEP-0461, section 4) - " +
                            "everybody present sees a different one.");

                Assert.That(arrived.MessageId, Is.EqualTo("alices-own"),
                            "The stanza's own id is still there; it is only not the one to point at.");

            });

        }

        #endregion

        #region APrivateWordInARoomIsNotAChatWithAContact()

        /// <summary>
        /// XEP-0045, section 7.5: the one thing that tells them apart.
        /// </summary>
        /// <remarks>
        /// <b>Both are a <c>chat</c> from a full address</b>, and nothing in the
        /// stanza says which is which. The section does ask a sender to add an
        /// empty <c>&lt;x/&gt;</c> and then says a receiver must not depend on
        /// it, so what decides is the room table - which is why this is checked
        /// here, over a connection, and not in the record's own tests.
        ///
        /// Three cases, because two of them are ways of being wrong in the other
        /// direction: what the room says to everybody is not private, and a
        /// contact who happens to be online is not in a room.
        /// </remarks>
        [Test]
        public async Task APrivateWordInARoomIsNotAChatWithAContact()
        {

            var client   = await ConnectClientAsync();
            var session  = await SessionOfAsync(client);

            var room     = JID.Parse($"chat@conference.{Server.Domain}");

            var messages = new ConcurrentQueue<XMPPMessage>();
            client.OnMessage += (t, s, m, ct) => { messages.Enqueue(m); return Task.CompletedTask; };

            var joining = client.JoinRoomAsync(room, "me");
            await session.SendAsync(OccupantPresence(room, "me", client.FullJid, status: MucStatus.Self));
            await joining;

            // 1. one occupant to another - a private word. No marker on it, on
            //    purpose: everything written before revision 1.28 sends none.
            await session.SendAsync($"<message from='{room}/alice' to='{client.FullJid}' type='chat' id='p1'>" +
                                    "<body>for you alone</body>" +
                                    "</message>");

            // 2. the room to everybody.
            await session.SendAsync($"<message from='{room}/alice' to='{client.FullJid}' type='groupchat' id='p2'>" +
                                    "<body>to everybody</body>" +
                                    "</message>");

            // 3. somebody who is not in a room at all.
            await session.SendAsync($"<message from='alice@{Server.Domain}/home' to='{client.FullJid}' type='chat' id='p3'>" +
                                    "<body>from outside</body>" +
                                    "</message>");

            await WaitFor(() => messages.Count == 3, "all three messages");

            var privately  = messages.First(m => m.MessageId == "p1");
            var toEverybody = messages.First(m => m.MessageId == "p2");
            var fromOutside = messages.First(m => m.MessageId == "p3");

            Assert.Multiple(() =>
            {

                Assert.That(privately.IsRoomPrivate, Is.True,
                            "A private word in a room arrived looking like a chat with a " +
                            "contact. Answered that way it goes to the room.");

                Assert.That(toEverybody.IsRoomPrivate, Is.False,
                            "What the room said to everybody was taken for something said in " +
                            "confidence, which is the same mistake the other way about.");

                Assert.That(fromOutside.IsRoomPrivate, Is.False,
                            "A chat with an ordinary contact was taken for a room's business.");

            });

        }

        #endregion

        #region ACorrectionInARoomGoesToTheOneItWasSentTo()

        /// <summary>
        /// XEP-0308 against XEP-0045, section 7.5: the table that was keyed
        /// wrongly.
        /// </summary>
        /// <remarks>
        /// <b>The last-sent table was keyed by bare address</b>, which is right
        /// everywhere except here. A private message goes to
        /// <c>room@service/nick</c>, whose bare address is the room - so every
        /// occupant of one room shared one entry, and the correction that
        /// followed two private messages carried the id of the one sent to
        /// somebody else.
        ///
        /// Section 5 of XEP-0308 has a correction replace a message from the
        /// same sender to the same recipient. That one was for neither: the
        /// person receiving it had never seen what it claimed to replace, and
        /// what it did replace for them was whatever else they had been sent.
        ///
        /// Nothing had ever been addressed to an occupant before D134, which is
        /// why the table had been right for eighteen entries.
        /// </remarks>
        [Test]
        public async Task ACorrectionInARoomGoesToTheOneItWasSentTo()
        {

            var client   = await ConnectClientAsync();
            var session  = await SessionOfAsync(client);

            var room     = JID.Parse($"chat@conference.{Server.Domain}");

            var joining  = client.JoinRoomAsync(room, "me");
            await session.SendAsync(OccupantPresence(room, "me", client.FullJid, status: MucStatus.Self));
            await joining;

            var toAlice = await client.SendRoomPrivateMessageAsync(room, "alice", "for Alice");
            var toBob   = await client.SendRoomPrivateMessageAsync(room, "bob",   "for Bob");

            Assert.That(toAlice, Is.Not.Null);
            Assert.That(toBob,   Is.Not.Null);
            Assert.That(toAlice, Is.Not.EqualTo(toBob));

            var sent = new ConcurrentQueue<String>();
            client.OnRawXml += (t, s, xml, ct) => { sent.Enqueue(xml); return Task.CompletedTask; };

            Assert.That(await client.CorrectLastMessageAsync("for Alice, rather",
                                                             JID.Parse($"{room.Bare}/alice")),
                        Is.Not.Null,
                        "There was nothing to correct, so the table is not keyed by the " +
                        "occupant at all.");

            await WaitFor(() => sent.Any(xml => xml.Contains("for Alice, rather")),
                          "the correction going out");

            var correction = sent.First(xml => xml.Contains("for Alice, rather"));

            Assert.Multiple(() =>
            {

                Assert.That(correction, Does.Contain($"id=\"{toAlice}\"").Or.Contain($"id='{toAlice}'"),
                            "The correction points at the message sent to somebody else in the " +
                            "same room, so Alice is told that a line she never saw has been " +
                            $"replaced. It points at: {correction}");

                Assert.That(correction, Does.Not.Contain($"replace id=\"{toBob}\"").
                                        And.Not.Contain($"replace id='{toBob}'"));

            });

        }

        #endregion

    }

}
