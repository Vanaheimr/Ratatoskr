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
using System.Xml.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0045: a room, and everybody in it.
    /// </summary>
    /// <remarks>
    /// The manager is driven directly here rather than over a connection,
    /// because this project's own test server has no room service and a room is
    /// not something a client can simulate for itself. What that buys is
    /// determinism: every stanza a real service would send can be put in
    /// exactly, including the ones that are hard to provoke - a nickname the
    /// service assigns instead of the one asked for, a kick, a ban, a room
    /// shutting down.
    ///
    /// <b>What it does not buy is any evidence that a real room agrees.</b> Both
    /// halves here are the same code, which is the finding of D62 to D65 all
    /// over again. The answer to that lies next door in
    /// <c>XMPPConformanceTests</c>, against the room service of Prosody and of
    /// ejabberd.
    /// </remarks>
    [TestFixture]
    public class MultiUserChatTests
    {

        #region Data

        private static readonly JID Room = JID.Parse("chat@conference.example");

        private MucManager                _muc   = null!;
        private ConcurrentQueue<String>   _sent  = null!;

        #endregion

        #region SetUp

        [SetUp]
        public void SetUp()
        {
            _sent  = new ConcurrentQueue<String>();
            _muc   = new MucManager(xml => { _sent.Enqueue(xml); return Task.CompletedTask; });
        }

        #endregion

        #region Helper functions

        /// <summary>
        /// A presence as a room writes one.
        /// </summary>
        private static XElement Presence(String   nick,
                                         String?  type         = null,
                                         String   affiliation  = "none",
                                         String   role         = "participant",
                                         String?  realJid      = null,
                                         String?  newNick      = null,
                                         String?  reason       = null,
                                         params Int32[] status)
        {

            var codes = String.Concat(status.Select(code =>
                            $"<status code='{code}'/>"));

            var item = $"<item affiliation='{affiliation}' role='{role}'" +
                       (realJid is not null ? $" jid='{realJid}'" : "") +
                       (newNick is not null ? $" nick='{newNick}'" : "") +
                       (reason  is not null
                            ? $"><reason>{reason}</reason></item>"
                            : "/>");

            return XElement.Parse(
                       $"<presence xmlns='jabber:client' from='{Room}/{nick}' to='me@example/home'" +
                       (type is not null ? $" type='{type}'" : "") + ">" +
                           $"<x xmlns='http://jabber.org/protocol/muc#user'>{item}{codes}</x>" +
                       "</presence>");

        }

        /// <summary>
        /// Puts a presence in the way the connection would.
        /// </summary>
        private Task<Boolean> Deliver(XElement presence)
            => _muc.ProcessPresenceAsync(presence,
                                         JID.Parse(presence.Attr("from")!),
                                         presence.Attr("type") ?? "available");

        /// <summary>
        /// Enters the room and answers the join the way a service would: two
        /// occupants, then our own presence.
        /// </summary>
        private async Task<MucJoinOutcome> JoinAsync(String nick = "me", params Int32[] selfStatus)
        {

            var joining = _muc.JoinAsync(Room, nick);

            await Deliver(Presence("alice", affiliation: "owner",  role: "moderator"));
            await Deliver(Presence("bob",   affiliation: "member", role: "participant"));
            await Deliver(Presence(nick, status: selfStatus.Length > 0
                                                     ? selfStatus
                                                     : [MucStatus.Self]));

            return await joining;

        }

        #endregion


        #region TheJoinPresenceSaysItIsOne()

        /// <summary>
        /// The <c>&lt;x/&gt;</c> is what makes this a join.
        /// </summary>
        /// <remarks>
        /// Without it, section 7.2.2 lets the service refuse - and several do.
        /// A client that leaves it out works against the one server that is
        /// lenient and against nothing else.
        /// </remarks>
        [Test]
        public async Task TheJoinPresenceSaysItIsOne()
        {

            _ = _muc.JoinAsync(Room, "me", password: "sesame", historyMaxStanzas: 0);

            await Task.Delay(50);

            Assert.That(_sent.TryDequeue(out var presence), Is.True, "Nothing was sent at all.");

            Assert.Multiple(() =>
            {

                Assert.That(presence, Does.Contain("to='chat@conference.example/me'"),
                            "A join goes to the address one wants to be known by, not to the room.");

                Assert.That(presence, Does.Contain("<x xmlns='http://jabber.org/protocol/muc'"),
                            "Without the <x/> this is an ordinary presence to an address that " +
                            "happens to be a room, and a service may refuse it.");

                Assert.That(presence, Does.Contain("<password>sesame</password>"));

                Assert.That(presence, Does.Contain("maxstanzas='0'"));

            });

        }

        #endregion

        #region TheJoinEndsWithOnesOwnPresence()

        /// <summary>
        /// A room sends the occupants first and one's own presence last.
        /// </summary>
        /// <remarks>
        /// Which is why the join waits for status 110 rather than for the
        /// nickname it asked for: at 110 the list of who is present is complete,
        /// and that is what a caller needs to go on.
        /// </remarks>
        [Test]
        public async Task TheJoinEndsWithOnesOwnPresence()
        {

            var outcome = await JoinAsync();

            Assert.Multiple(() =>
            {

                Assert.That(outcome.Joined, Is.True, "The join did not finish.");

                Assert.That(outcome.Room!.State, Is.EqualTo(MucRoomState.Joined));

                Assert.That(outcome.Room.Occupants.Count, Is.EqualTo(3),
                            "The occupants that arrived before our own presence were not kept.");

                Assert.That(outcome.Room.Occupants["alice"].Role, Is.EqualTo(MucRole.Moderator));

                Assert.That(outcome.Room.Occupants["alice"].Affiliation, Is.EqualTo(MucAffiliation.Owner));

                Assert.That(outcome.Room.Me, Is.Not.Null,
                            "The room does not know what we are in it.");

            });

        }

        #endregion

        #region AnAssignedNicknameIsTheOneThatHolds()

        /// <summary>
        /// Status 210: the service gave us a different name than we asked for.
        /// </summary>
        /// <remarks>
        /// <b>What comes back holds</b>, and a client that keeps the name it
        /// asked for addresses every later message under a nickname that is
        /// somebody else's or nobody's. It is also why the join waits for the
        /// status code instead of comparing nicknames: the one that arrives is
        /// not the one that was sent.
        /// </remarks>
        [Test]
        public async Task AnAssignedNicknameIsTheOneThatHolds()
        {

            var joining = _muc.JoinAsync(Room, "me");

            await Deliver(Presence("me-2", status: [MucStatus.Self, MucStatus.NickAssigned]));

            var outcome = await joining;

            Assert.Multiple(() =>
            {

                Assert.That(outcome.Joined, Is.True);

                Assert.That(outcome.Room!.Nick, Is.EqualTo("me-2"),
                            "The room calls us something else, and this client still thinks it is " +
                            "called what it asked to be called.");

                Assert.That(outcome.Room.SelfAddress.ToString(),
                            Is.EqualTo("chat@conference.example/me-2"));

            });

        }

        #endregion

        #region ARefusalEndsTheJoinWithTheReason()

        /// <summary>
        /// A room that will not let us in answers with an error presence.
        /// </summary>
        /// <remarks>
        /// <b>And the room must not stay in the table afterwards.</b> That is
        /// the expensive half: the table is what decides whether a presence from
        /// that address goes to the room or into the roster, so a room left
        /// behind after a refusal swallows every presence from that address for
        /// the rest of the session.
        /// </remarks>
        [Test]
        public async Task ARefusalEndsTheJoinWithTheReason()
        {

            var joining = _muc.JoinAsync(Room, "me");

            await _muc.ProcessPresenceAsync(
                      XElement.Parse(
                          $"<presence xmlns='jabber:client' from='{Room}/me' to='me@example/home' type='error'>" +
                              "<error type='cancel'>" +
                                  "<conflict xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                              "</error>" +
                          "</presence>"),
                      JID.Parse($"{Room}/me"),
                      "error");

            var outcome = await joining;

            Assert.Multiple(() =>
            {

                Assert.That(outcome.Joined,  Is.False);
                Assert.That(outcome.TimedOut, Is.False, "A refusal is not the same as no answer.");

                Assert.That(outcome.Refusal!.Condition, Is.EqualTo("conflict"),
                            "The reason is the whole of what a caller can act on: a taken nickname " +
                            "is worth trying again with another, a ban is not.");

                Assert.That(_muc.IsRoom(Room), Is.False,
                            "The refused room stayed in the table, so every presence from that " +
                            "address is now kept out of the roster for good.");

            });

        }

        #endregion

        #region ARenameKeepsTheRole()

        /// <summary>
        /// Status 303: somebody is called something else from now on.
        /// </summary>
        /// <remarks>
        /// It arrives as an <c>unavailable</c> presence, and read as a departure
        /// it empties the room of everybody who ever renamed themselves.
        ///
        /// The occupant is carried over rather than re-created: affiliation and
        /// role belong to the person and do not change because the label did.
        /// </remarks>
        [Test]
        public async Task ARenameKeepsTheRole()
        {

            await JoinAsync();

            var renamed = new ConcurrentQueue<(String Old, String New, Boolean Self)>();
            _muc.OnOccupantRenamed += (t, s, room, oldNick, newNick, isSelf, ct) =>
            {
                renamed.Enqueue((oldNick, newNick, isSelf));
                return Task.CompletedTask;
            };

            await Deliver(Presence("alice", type: "unavailable", affiliation: "owner", role: "none",
                                   newNick: "alice2", status: [MucStatus.NickChanged]));

            var room = _muc.Room(Room)!;

            Assert.Multiple(() =>
            {

                Assert.That(room.Occupants.ContainsKey("alice"), Is.False);

                Assert.That(room.Occupants.ContainsKey("alice2"), Is.True,
                            "A rename was read as a departure, so the room is now missing somebody " +
                            "who never left.");

                Assert.That(room.Occupants["alice2"].Role, Is.EqualTo(MucRole.Moderator),
                            "The moderator lost the role by changing their name.");

                Assert.That(renamed.Count, Is.EqualTo(1));

            });

        }

        #endregion

        #region OurOwnRenameChangesWhatWeAreCalled()

        /// <summary>
        /// The same stanza about ourselves changes our own name in the room.
        /// </summary>
        [Test]
        public async Task OurOwnRenameChangesWhatWeAreCalled()
        {

            await JoinAsync();

            await Deliver(Presence("me", type: "unavailable", role: "none",
                                   newNick: "me-too",
                                   status: [MucStatus.NickChanged, MucStatus.Self]));

            Assert.That(_muc.Room(Room)!.Nick, Is.EqualTo("me-too"));

        }

        #endregion

        #region BeingKickedIsNotTheSameAsLeaving()

        /// <summary>
        /// Four different things, one stanza, and the number tells them apart.
        /// </summary>
        /// <remarks>
        /// An <c>unavailable</c> presence about oneself is leaving (nothing),
        /// being kicked (307), being banned (301) or the service shutting down
        /// (332). A client that does not read the codes reports all four as
        /// "you have left the room", and the one thing a person needs to know -
        /// whether coming back is worth trying - is the one thing it drops.
        /// </remarks>
        [Test]
        public async Task BeingKickedIsNotTheSameAsLeaving()
        {

            await JoinAsync();

            MucUserInfo? why = null;
            _muc.OnRoomLeft += (t, s, room, info, ct) => { why = info; return Task.CompletedTask; };

            await Deliver(Presence("me", type: "unavailable", role: "none",
                                   reason: "Enough of that.",
                                   status: [MucStatus.Self, MucStatus.Kicked]));

            Assert.Multiple(() =>
            {

                Assert.That(why, Is.Not.Null, "Nobody was told we are out of the room.");

                Assert.That(why!.Has(MucStatus.Kicked), Is.True,
                            "A kick arrived as an ordinary departure.");

                Assert.That(why.Reason, Is.EqualTo("Enough of that."));

                Assert.That(_muc.IsRoom(Room), Is.False,
                            "The room stayed in the table after we were thrown out of it.");

            });

        }

        #endregion

        #region SomebodyElseLeavingIsNotUsLeaving()

        /// <summary>
        /// The same stanza without status 110 is about somebody else.
        /// </summary>
        [Test]
        public async Task SomebodyElseLeavingIsNotUsLeaving()
        {

            await JoinAsync();

            var left = 0;
            var weLeft = false;

            _muc.OnOccupantLeft += (t, s, room, occupant, info, ct) => { left++; return Task.CompletedTask; };
            _muc.OnRoomLeft     += (t, s, room, info, ct) => { weLeft = true; return Task.CompletedTask; };

            await Deliver(Presence("bob", type: "unavailable", role: "none"));

            Assert.Multiple(() =>
            {
                Assert.That(left,    Is.EqualTo(1));
                Assert.That(weLeft,  Is.False, "Somebody else leaving threw us out of the room.");
                Assert.That(_muc.IsRoom(Room), Is.True);
                Assert.That(_muc.Room(Room)!.Occupants.Count, Is.EqualTo(2));
            });

        }

        #endregion

        #region TheSubjectIsAChangeAndAMessageIsNot()

        /// <summary>
        /// A <c>&lt;subject/&gt;</c> and no <c>&lt;body/&gt;</c>, section
        /// 7.2.16.
        /// </summary>
        /// <remarks>
        /// Both halves matter. A client that goes by the element alone announces
        /// a change whenever somebody quotes a subject line in a sentence; one
        /// that ignores the missing body does the same.
        ///
        /// An empty subject is a change as well - it is how a subject is
        /// removed - which is why the distinction is between "no element" and
        /// "an element with nothing in it".
        /// </remarks>
        [Test]
        public async Task TheSubjectIsAChangeAndAMessageIsNot()
        {

            await JoinAsync();

            var subjects = new ConcurrentQueue<String>();
            _muc.OnRoomSubject += (t, s, room, subject, by, ct) =>
            {
                subjects.Enqueue(subject);
                return Task.CompletedTask;
            };

            var from = JID.Parse($"{Room}/alice");

            var handledSubject = await _muc.ProcessMessageAsync(
                XElement.Parse($"<message xmlns='jabber:client' from='{from}' type='groupchat'>" +
                               "<subject>Deployment on Friday</subject></message>"), from);

            var handledMessage = await _muc.ProcessMessageAsync(
                XElement.Parse($"<message xmlns='jabber:client' from='{from}' type='groupchat'>" +
                               "<subject>Deployment on Friday</subject><body>Is it?</body></message>"), from);

            var handledEmpty = await _muc.ProcessMessageAsync(
                XElement.Parse($"<message xmlns='jabber:client' from='{from}' type='groupchat'>" +
                               "<subject></subject></message>"), from);

            Assert.Multiple(() =>
            {

                Assert.That(handledSubject, Is.True);

                Assert.That(handledMessage, Is.False,
                            "A message with a body was swallowed as a subject change, so nobody " +
                            "ever saw what was said.");

                Assert.That(handledEmpty, Is.True,
                            "Removing the subject is a change too.");

                Assert.That(subjects.ToArray(), Is.EqualTo(new[] { "Deployment on Friday", "" }));

                Assert.That(_muc.Room(Room)!.Subject, Is.Empty);

            });

        }

        #endregion

        #region TheRealAddressIsUsuallyNotGiven()

        /// <summary>
        /// A room is semi-anonymous by default, and that is not a gap in the
        /// parsing.
        /// </summary>
        /// <remarks>
        /// Whoever treats the missing <c>jid</c> as an error cannot enter an
        /// ordinary room at all. Status 100 is the room saying that it does show
        /// them - worth knowing before writing anything, because there the
        /// connection between the nickname and the account is public to
        /// everybody present.
        /// </remarks>
        [Test]
        public async Task TheRealAddressIsUsuallyNotGiven()
        {

            var joining = _muc.JoinAsync(Room, "me");

            await Deliver(Presence("alice"));
            await Deliver(Presence("bob", realJid: "bob@example.org/phone"));
            await Deliver(Presence("me", status: [MucStatus.Self, MucStatus.NonAnonymous]));

            var room = (await joining).Room!;

            Assert.Multiple(() =>
            {

                Assert.That(room.Occupants["alice"].RealJid, Is.Null);

                Assert.That(room.Occupants["bob"].RealJid?.ToString(),
                            Is.EqualTo("bob@example.org/phone"));

                Assert.That(room.IsNonAnonymous, Is.True,
                            "The room says it shows everybody's address and this client does not " +
                            "pass that on.");

            });

        }

        #endregion

        #region AnUnknownRoleIsNoneAndNotTheHighest()

        /// <summary>
        /// A word this client has never heard of makes nobody a moderator.
        /// </summary>
        /// <remarks>
        /// The direction of the fallback is the whole point. Unknown means the
        /// least, not the most - otherwise a service inventing a value decides
        /// what this client lets somebody do.
        /// </remarks>
        [Test]
        public void AnUnknownRoleIsNoneAndNotTheHighest()
        {

            Assert.Multiple(() =>
            {

                Assert.That(MucRoles.ToRole("archduke"),        Is.EqualTo(MucRole.None));
                Assert.That(MucRoles.ToAffiliation("landlord"), Is.EqualTo(MucAffiliation.None));
                Assert.That(MucRoles.ToRole(null),              Is.EqualTo(MucRole.None));

                Assert.That(new MucOccupant("x", MucAffiliation.None, MucRole.Visitor).MaySpeak,
                            Is.False);

            });

        }

        #endregion

        #region RoomNewsInAForwardedMessageIsNotTheOuterOnes()

        /// <summary>
        /// A carbon brings a whole message of its own along.
        /// </summary>
        /// <remarks>
        /// The same trap as the delay stamp in D59, the correction in XEP-0308
        /// and the reply in D114. Here it would be worse than a wrong label: a
        /// status 307 read off a forwarded stanza reports a kick that never
        /// happened.
        /// </remarks>
        [Test]
        public void RoomNewsInAForwardedMessageIsNotTheOuterOnes()
        {

            var carbon = XElement.Parse(
                $"<message xmlns='jabber:client' from='{Room}/alice' to='me@example/home'>" +
                    "<received xmlns='urn:xmpp:carbons:2'>" +
                        "<forwarded xmlns='urn:xmpp:forward:0'>" +
                            "<presence xmlns='jabber:client'>" +
                                "<x xmlns='http://jabber.org/protocol/muc#user'>" +
                                    "<item affiliation='owner' role='moderator'/>" +
                                    "<status code='307'/>" +
                                "</x>" +
                            "</presence>" +
                        "</forwarded>" +
                    "</received>" +
                "</message>");

            Assert.That(MultiUserChat.UserInfo(carbon), Is.Null);

        }

        #endregion

        #region LeavingARoomOneIsNotInIsRefused()

        /// <summary>
        /// Nothing goes out for a room this client was never in.
        /// </summary>
        [Test]
        public async Task LeavingARoomOneIsNotInIsRefused()
        {

            Assert.Multiple(async () =>
            {
                Assert.That(await _muc.LeaveAsync(Room),                  Is.False);
                Assert.That(await _muc.ChangeNickAsync(Room, "other"),    Is.False);
                Assert.That(await _muc.SetSubjectAsync(Room, "anything"), Is.False);
                Assert.That(_sent.IsEmpty, Is.True, "Something was sent to a room nobody is in.");
            });

        }

        #endregion

        #region LeavingSendsTheDepartureUnderTheNameTheRoomGaveUs()

        /// <summary>
        /// The departure goes out under the nickname that holds, not the one
        /// that was asked for.
        /// </summary>
        [Test]
        public async Task LeavingSendsTheDepartureUnderTheNameTheRoomGaveUs()
        {

            var joining = _muc.JoinAsync(Room, "me");
            await Deliver(Presence("me-2", status: [MucStatus.Self, MucStatus.NickAssigned]));
            await joining;

            _sent.Clear();

            await _muc.LeaveAsync(Room, "Bye");

            Assert.That(_sent.TryDequeue(out var presence), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(presence, Does.Contain("to='chat@conference.example/me-2'"));
                Assert.That(presence, Does.Contain("type='unavailable'"));
                Assert.That(presence, Does.Contain("<status>Bye</status>"));
            });

        }

        #endregion

    }

}
