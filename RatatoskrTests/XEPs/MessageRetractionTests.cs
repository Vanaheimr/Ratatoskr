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
    /// XEP-0424: taking back something that was said.
    /// </summary>
    /// <remarks>
    /// What a far side cannot be asked about, because it is all on this side of
    /// the wire: which name a retraction has to carry, and what a stanza has to
    /// hold for the three kinds of reader it has.
    /// </remarks>
    [TestFixture]
    public class MessageRetractionTests
    {

        #region ARetractionCarriesSomethingForEveryKindOfReader()

        /// <summary>
        /// The three elements, and who each of them is for.
        /// </summary>
        /// <remarks>
        /// A client that understands the extension reads the
        /// <c>&lt;retract/&gt;</c>; one that understands XEP-0428 but not this
        /// hides the body instead of showing it as something somebody typed; and
        /// the archive is told to keep it, because a message whose point is that
        /// there is nothing to read is exactly the kind a server decides not to
        /// store - and XEP-0424 requires it stored.
        /// </remarks>
        [Test]
        public void ARetractionCarriesSomethingForEveryKindOfReader()
        {

            var extras = MessageRetraction.Extras("room-42");

            Assert.Multiple(() =>
            {

                Assert.That(extras, Does.Contain("urn:xmpp:message-retract:1"),
                            "The namespace is not the one at version 1, so a client speaking " +
                            "this extension sees nothing at all rather than something wrong.");

                Assert.That(extras, Does.Contain("id='room-42'"));

                Assert.That(extras, Does.Contain("urn:xmpp:fallback:0"),
                            "Nothing marks the body as a substitute, so a client that cannot " +
                            "retract shows the sentence as though somebody had typed it.");

                Assert.That(extras, Does.Contain("<store xmlns='urn:xmpp:hints'/>"),
                            "The archive is not asked to keep it, and a message with nothing to " +
                            "read is the kind a server drops.");

            });

        }

        #endregion

        #region ARetractionNamingNothingIsNotARetraction()

        /// <summary>
        /// What has to be there to be read as one.
        /// </summary>
        /// <remarks>
        /// <b>An id and not a Boolean.</b> A retraction naming nothing cannot
        /// say which line it is about, and a client acting on it would have to
        /// guess - most likely at the last one, which is the case where guessing
        /// looks right often enough to be trusted.
        /// </remarks>
        [Test]
        public void ARetractionNamingNothingIsNotARetraction()
        {

            var proper = XElement.Parse(
                "<message xmlns='jabber:client' from='a@example/x' to='b@example/y' type='chat'>" +
                    "<retract id='wrong-1' xmlns='urn:xmpp:message-retract:1'/>" +
                    "<body>A previous message was retracted.</body>" +
                "</message>");

            var nameless = XElement.Parse(
                "<message xmlns='jabber:client' from='a@example/x' to='b@example/y' type='chat'>" +
                    "<retract xmlns='urn:xmpp:message-retract:1'/>" +
                    "<body>A previous message was retracted.</body>" +
                "</message>");

            var old = XElement.Parse(
                "<message xmlns='jabber:client' from='a@example/x' to='b@example/y' type='chat'>" +
                    "<apply-to id='wrong-1' xmlns='urn:xmpp:fasten:0'>" +
                        "<retract xmlns='urn:xmpp:message-retract:0'/>" +
                    "</apply-to>" +
                "</message>");

            var ordinary = XElement.Parse(
                "<message xmlns='jabber:client' from='a@example/x' to='b@example/y' type='chat'>" +
                    "<body>Hello</body>" +
                "</message>");

            Assert.Multiple(() =>
            {

                Assert.That(MessageRetraction.RetractedId(proper),    Is.EqualTo("wrong-1"));

                Assert.That(MessageRetraction.RetractedId(nameless),  Is.Null,
                            "A retraction that names nothing was read as one, so something will " +
                            "be taken back and nobody knows what.");

                Assert.That(MessageRetraction.RetractedId(old),       Is.Null,
                            "The old XEP-0422 shape was read as the new one. The two are not " +
                            "compatible, and reading one as the other is worse than not reading " +
                            "it: the id belongs to a fastening and means something else.");

                Assert.That(MessageRetraction.RetractedId(ordinary),  Is.Null);

            });

        }

        #endregion

        #region InARoomTheNameIsTheRoomsAndNotTheSendersOwn()

        /// <summary>
        /// XEP-0424: <i>in group chats, the ID assigned to the stanza by the
        /// group chat itself must be used</i>.
        /// </summary>
        /// <remarks>
        /// <b>The one extension of the three that says so in its own text.</b>
        /// XEP-0461 forbids the stanza's id in a room and arrives at the same
        /// place from the other direction (D114); XEP-0308 leaves it open and
        /// was answered by measuring two services (D135). Which is why
        /// <c>RetractableId</c> is the same expression as <c>ReplyableId</c> -
        /// one question, one answer, and one place to change it.
        ///
        /// Getting it wrong is not a display fault: a retraction naming the
        /// sender's own id names, for every other reader, a line that is not the
        /// one that was meant - or no line at all, which is a retraction that
        /// silently did not happen.
        /// </remarks>
        [Test]
        public void InARoomTheNameIsTheRoomsAndNotTheSendersOwn()
        {

            var inARoom = new XMPPMessage(
                              JID.Parse("chat@conference.example/alice"),
                              JID.Parse("me@example/home"),
                              "something better left unsaid",
                              "alices-own",
                              DateTime.Now,
                              MessageType.GroupChat,
                              StanzaId: "room-42"
                          );

            var betweenTwo = new XMPPMessage(
                                 JID.Parse("alice@example/home"),
                                 JID.Parse("me@example/home"),
                                 "something better left unsaid",
                                 "alices-own",
                                 DateTime.Now,
                                 MessageType.Chat,
                                 StanzaId: "server-7"
                             );

            var nameless = new XMPPMessage(
                               JID.Parse("chat@conference.example/alice"),
                               JID.Parse("me@example/home"),
                               "said in a room that archives nothing",
                               "alices-own",
                               DateTime.Now,
                               MessageType.GroupChat
                           );

            Assert.Multiple(() =>
            {

                Assert.That(inARoom.RetractableId, Is.EqualTo("room-42"),
                            "In a room the retraction has to name what the room called the " +
                            "message, and this names what the sender called it - which is a " +
                            "different line for every reader, or none.");

                Assert.That(betweenTwo.RetractableId, Is.EqualTo("alices-own"),
                            "Outside a room the sender's own name holds, and the server's " +
                            "stanza-id is not it.");

                Assert.That(nameless.RetractableId, Is.Null,
                            "A room that assigns no name is one in which nothing can be taken " +
                            "back, and saying so is better than naming somebody else's line.");

                Assert.That(inARoom.RetractableId, Is.EqualTo(inARoom.ReplyableId),
                            "The two extensions have come apart. They answer one question - what " +
                            "is this message called, in a way everybody who can see it agrees " +
                            "on - and a client that answered it twice would answer it twice " +
                            "differently one day.");

            });

        }

        #endregion

    }

}
