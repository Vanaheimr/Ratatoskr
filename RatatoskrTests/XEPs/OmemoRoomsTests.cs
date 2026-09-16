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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0384 in a room: who may be encrypted to, and when nobody may.
    /// </summary>
    /// <remarks>
    /// The rules on their own, with no connection and no service - a room here
    /// is a <see cref="MucRoom"/> with occupants written into it. What these
    /// pin is the set of decisions a client makes <b>before</b> a single byte is
    /// encrypted, and every one of them has a way of being wrong that produces
    /// a working-looking room somebody cannot read.
    ///
    /// The round through a real encryption stands in
    /// <see cref="OmemoRoomEndToEndTests"/>; the round against Prosody and
    /// ejabberd in the conformance suite.
    /// </remarks>
    [TestFixture]
    public class OmemoRoomsTests
    {

        #region Data

        private static readonly JID Room   = JID.Parse("chat@conference.example.org");
        private static readonly JID Alice  = JID.Parse("alice@example.org");
        private static readonly JID Bob    = JID.Parse("bob@example.org");

        /// <summary>
        /// A room this client is in under the given nickname.
        /// </summary>
        private static MucRoom ARoom(string  ownNick        = "me",
                                     bool    nonAnonymous   = true)
        {

            var room = new MucRoom(Room, ownNick) {
                           State           = MucRoomState.Joined,
                           IsNonAnonymous  = nonAnonymous
                       };

            room.Set(new MucOccupant(ownNick, MucAffiliation.None, MucRole.Participant));

            return room;

        }

        private static void Enters(MucRoom  room,
                                   string   nick,
                                   JID?     realJid = null)

            => room.Set(new MucOccupant(nick, MucAffiliation.None, MucRole.Participant, realJid));

        #endregion


        #region EverybodyPresentIsARecipientAndWeAreNot()

        /// <summary>
        /// The set to encrypt to is the room, minus ourselves.
        /// </summary>
        /// <remarks>
        /// Ourselves not because our other devices should not see it - they
        /// must, and <c>EncryptAsync</c> appends our own address for exactly
        /// that. It is that the same person twice in the list makes the count
        /// say something untrue, and the count is what a client shows when it
        /// says who can read this.
        /// </remarks>
        [Test]
        public void EverybodyPresentIsARecipientAndWeAreNot()
        {

            var room = ARoom();

            Enters(room, "alice", Alice);
            Enters(room, "bob",   Bob);

            var recipients = OmemoRooms.RecipientsOf(room);

            Assert.Multiple(() =>
            {

                Assert.That(recipients.Jids,      Is.EquivalentTo(new[] { Alice, Bob }));
                Assert.That(recipients.Complete,  Is.True);
                Assert.That(recipients.Anonymous, Is.Empty);

                Assert.That(OmemoRooms.WhyNot(room), Is.Null,
                            "A non-anonymous room with two named occupants cannot be written to.");

            });

        }

        #endregion

        #region OnePersonUnderTwoNicknamesIsOnePerson()

        /// <summary>
        /// The same account in a room twice - from a telephone and a laptop.
        /// </summary>
        /// <remarks>
        /// Nothing forbids it and services allow it. What it must not do is put
        /// the address in the recipient list twice: OMEMO encrypts to the
        /// devices of a bare address, so the second entry adds no device and
        /// only makes the number of people who can read this wrong.
        /// </remarks>
        [Test]
        public void OnePersonUnderTwoNicknamesIsOnePerson()
        {

            var room = ARoom();

            Enters(room, "alice",        Alice);
            Enters(room, "alice-phone",  Alice);

            Assert.That(OmemoRooms.RecipientsOf(room).Jids, Is.EqualTo(new[] { Alice }));

        }

        #endregion

        #region ASemiAnonymousRoomCannotCarryIt()

        /// <summary>
        /// The default room, and the reason this whole lane needed room
        /// configuration first.
        /// </summary>
        /// <remarks>
        /// A service is semi-anonymous unless told otherwise and gives real
        /// addresses to moderators only. A participant therefore has nicknames
        /// and nothing else - and there is no repair for that at this layer,
        /// only a setting one level up.
        ///
        /// The refusal has to <b>say</b> that, which is why this asserts on the
        /// text: a lock that is greyed out and silent tells somebody encryption
        /// is impossible, when it is one configuration away.
        /// </remarks>
        [Test]
        public void ASemiAnonymousRoomCannotCarryIt()
        {

            var room = ARoom(nonAnonymous: false);

            Enters(room, "alice");
            Enters(room, "bob");

            var why = OmemoRooms.WhyNot(room);

            Assert.Multiple(() =>
            {

                Assert.That(OmemoRooms.RecipientsOf(room).Jids,      Is.Empty);
                Assert.That(OmemoRooms.RecipientsOf(room).Complete,  Is.False);

                Assert.That(why, Is.Not.Null, "A room of nicknames was taken for one that can be written to.");

                Assert.That(why, Does.Contain("semi-anonymous"),
                            "The refusal does not name the setting, so nobody reading it can do " +
                            "anything about it.");

            });

        }

        #endregion

        #region OneOccupantWithoutAnAddressStopsTheWholeMessage()

        /// <summary>
        /// <b>The decision this lane turns on</b>, and it is the opposite of the
        /// one for a missing device.
        /// </summary>
        /// <remarks>
        /// A contact's fourth device with no fetchable bundle is skipped and
        /// named, because refusing would make a person unreachable over one
        /// broken machine. An <i>occupant</i> with no address is not the same
        /// case: a device is invisible and a person standing in the room is not.
        /// Sending to the rest produces a conversation that looks whole to
        /// everybody in it while one of them cannot read a word - and neither
        /// they nor the sender is told.
        ///
        /// So: refuse, and name who is missing.
        /// </remarks>
        [Test]
        public void OneOccupantWithoutAnAddressStopsTheWholeMessage()
        {

            var room = ARoom();

            Enters(room, "alice",   Alice);
            Enters(room, "unknown");

            var recipients = OmemoRooms.RecipientsOf(room);

            Assert.Multiple(() =>
            {

                Assert.That(recipients.Jids,      Is.EqualTo(new[] { Alice }),
                            "The ones that are known are still known.");

                Assert.That(recipients.Complete,  Is.False,
                            "A room with somebody unnamed in it counted as complete.");

                Assert.That(recipients.Anonymous, Is.EqualTo(new[] { "unknown" }));

            });

            var why = OmemoRooms.WhyNot(room);

            Assert.Multiple(() =>
            {

                Assert.That(why, Is.Not.Null,
                            "A message would have gone out that one person present cannot read, and " +
                            "nobody would have been told.");

                Assert.That(why, Does.Contain("unknown"),
                            "The refusal does not say who is missing.");

            });

        }

        #endregion

        #region ARoomOfOneIsNotARoom()

        /// <summary>
        /// Alone in a room.
        /// </summary>
        /// <remarks>
        /// Encrypting would work - it would go to our own other devices - and
        /// is refused all the same, because the useful answer to somebody
        /// writing into an empty room is that nobody is there. Without this the
        /// recipient set is empty and everything downstream reports success.
        /// </remarks>
        [Test]
        public void ARoomOfOneIsNotARoom()
        {

            var room = ARoom();

            Assert.That(OmemoRooms.WhyNot(room), Does.Contain("nobody else"),
                        "Writing into an empty room reported success.");

        }

        #endregion

        #region ARoomNotEnteredIsRefusedBeforeAnythingElse()

        [Test]
        public void ARoomNotEnteredIsRefusedBeforeAnythingElse()
        {

            var room = new MucRoom(Room, "me") { State = MucRoomState.Joining };

            Enters(room, "alice", Alice);

            Assert.That(OmemoRooms.WhyNot(room), Does.Contain("not been entered"),
                        "A room this client is still walking into was written to.");

        }

        #endregion

        #region OurOwnLineComingBackIsRecognised()

        /// <summary>
        /// Every single line one writes in a room comes back unreadable.
        /// </summary>
        /// <remarks>
        /// <b>Not an error and not an edge case - the ordinary path.</b> A room
        /// sends every message to everybody including the sender, and an OMEMO
        /// element carries no key for the device that made it, because a device
        /// cannot keep a ratchet with itself. So the reflection of one's own
        /// message is by construction one there is nothing in for us.
        ///
        /// A client without this reports "could not be read" for everything it
        /// says.
        /// </remarks>
        [Test]
        public void OurOwnLineComingBackIsRecognised()
        {

            var room = ARoom(ownNick: "me");

            Enters(room, "alice", Alice);

            Assert.Multiple(() =>
            {

                Assert.That(OmemoRooms.IsOwnReflection(room, JID.Parse($"{Room}/me")),    Is.True,
                            "Our own line coming back is taken for somebody else's.");

                Assert.That(OmemoRooms.IsOwnReflection(room, JID.Parse($"{Room}/alice")), Is.False,
                            "Somebody else's line is taken for our own and dropped.");

                Assert.That(OmemoRooms.IsOwnReflection(room, Room),                       Is.False,
                            "A message from the room itself is taken for our own.");

            });

        }

        #endregion

        #region TheSenderIsLookedUpAndNeverGuessed()

        /// <summary>
        /// A nickname resolves to an address, or to nothing.
        /// </summary>
        /// <remarks>
        /// The obvious repairs are both wrong and both silent. Taking the room's
        /// bare address for the sender files every occupant under one identity -
        /// so the first person to write in a room establishes a session that the
        /// next person's message is then decrypted against. Taking the nickname
        /// sends the lookup somewhere that can never have a session, which at
        /// least fails loudly.
        /// </remarks>
        [Test]
        public void TheSenderIsLookedUpAndNeverGuessed()
        {

            var room = ARoom();

            Enters(room, "alice",   Alice);
            Enters(room, "unknown");

            Assert.Multiple(() =>
            {

                Assert.That(OmemoRooms.SenderOf(room, JID.Parse($"{Room}/alice")),   Is.EqualTo(Alice));

                Assert.That(OmemoRooms.SenderOf(room, JID.Parse($"{Room}/unknown")), Is.Null,
                            "An occupant the room will not name was given an identity anyway.");

                Assert.That(OmemoRooms.SenderOf(room, JID.Parse($"{Room}/nobody")),  Is.Null,
                            "A nickname nobody in the room has was resolved to something.");

                Assert.That(OmemoRooms.SenderOf(room, Room),                          Is.Null,
                            "The room itself was taken for an occupant.");

            });

        }

        #endregion

        #region OnlyAGroupChatGoesTheRoomWay()

        /// <summary>
        /// The branch that decides which of the two encrypted paths a message
        /// takes.
        /// </summary>
        /// <remarks>
        /// It has to be both halves. Without the type, a private message from
        /// an occupant - which is <c>chat</c> from <c>room@service/nick</c> -
        /// would go the room way; without the element, every ordinary line in a
        /// room would.
        /// </remarks>
        [Test]
        public void OnlyAGroupChatGoesTheRoomWay()
        {

            XNamespace omemo = OmemoEncryptedElement.Namespace;

            var encrypted = new XElement(omemo + "encrypted",
                                new XElement(omemo + "header",
                                    new XAttribute("sid", "1")));

            var inRoom = new XElement("message", new XAttribute("type", "groupchat"), new XElement(encrypted));
            var plain  = new XElement("message", new XAttribute("type", "groupchat"), new XElement("body", "hello"));
            var direct = new XElement("message", new XAttribute("type", "chat"),      new XElement(encrypted));

            Assert.Multiple(() =>
            {

                Assert.That(OmemoRooms.IsEncryptedGroupChat(inRoom), Is.True);

                Assert.That(OmemoRooms.IsEncryptedGroupChat(plain),  Is.False,
                            "An ordinary line in a room was sent down the encrypted path.");

                Assert.That(OmemoRooms.IsEncryptedGroupChat(direct), Is.False,
                            "A private message from an occupant would be decrypted against the " +
                            "room instead of against the person.");

            });

        }

        #endregion

    }

}
