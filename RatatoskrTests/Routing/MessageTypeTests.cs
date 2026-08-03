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
    /// The kind of a message (RFC 6121, section 5.2.2).
    /// </summary>
    /// <remarks>
    /// Up to here everything arrived alike: the recipient could not tell the
    /// shout of a news source from the line of an acquaintance, and the line
    /// from a room not from one addressed to them alone. Where that concerns
    /// not merely the display but the behaviour, it gets delicate — the client
    /// acknowledged every message, including the ones from a room.
    /// </remarks>
    [TestFixture]
    public class MessageTypeTests : AXMPPTests
    {

        #region TheDefaultIsNormal()

        /// <summary>
        /// If the attribute is missing or its value unknown, the message counts
        /// as <c>normal</c>.
        /// </summary>
        /// <remarks>
        /// RFC 6121, section 5.2.2 is unusually plain here and says MUST. The
        /// reason lies in the future: a later extension is meant to arrive at
        /// old recipients as an ordinary message and not to disappear.
        /// </remarks>
        [Test]
        public void TheDefaultIsNormal()
        {

            Assert.Multiple(() =>
            {

                Assert.That(MessageTypeExtensions.Parse(null),        Is.EqualTo(MessageType.Normal));
                Assert.That(MessageTypeExtensions.Parse(""),          Is.EqualTo(MessageType.Normal));
                Assert.That(MessageTypeExtensions.Parse("normal"),    Is.EqualTo(MessageType.Normal));

                // Unknown - and no refusal all the same.
                Assert.That(MessageTypeExtensions.Parse("shout"),     Is.EqualTo(MessageType.Normal));

                // Written in upper case it is not the same value; XML
                // attributes of this kind are laid down in lower case in RFC
                // 6121.
                Assert.That(MessageTypeExtensions.Parse("Chat"),      Is.EqualTo(MessageType.Normal));

                Assert.That(MessageTypeExtensions.Parse("chat"),      Is.EqualTo(MessageType.Chat));
                Assert.That(MessageTypeExtensions.Parse("groupchat"), Is.EqualTo(MessageType.GroupChat));
                Assert.That(MessageTypeExtensions.Parse("headline"),  Is.EqualTo(MessageType.Headline));
                Assert.That(MessageTypeExtensions.Parse("error"),     Is.EqualTo(MessageType.Error));

            });

        }

        #endregion

        #region TheDefaultIsNotWrittenOut()

        /// <summary>
        /// <c>normal</c> is the default and is not written out.
        /// </summary>
        [Test]
        public void TheDefaultIsNotWrittenOut()
        {

            Assert.Multiple(() =>
            {

                Assert.That(MessageType.Normal.AsAttribute(),    Is.Null);

                Assert.That(MessageType.Chat.AsAttribute(),      Is.EqualTo("chat"));
                Assert.That(MessageType.GroupChat.AsAttribute(), Is.EqualTo("groupchat"));
                Assert.That(MessageType.Headline.AsAttribute(),  Is.EqualTo("headline"));
                Assert.That(MessageType.Error.AsAttribute(),     Is.EqualTo("error"));

                // And back again: what is written is also read.
                foreach (var type in Enum.GetValues<MessageType>())
                    Assert.That(MessageTypeExtensions.Parse(type.AsAttribute()), Is.EqualTo(type),
                                $"Lost there and back: {type}");

            });

        }

        #endregion

        #region TheTypeReachesTheApplication()

        /// <summary>
        /// The kind arrives at the recipient — otherwise nobody would have it.
        /// </summary>
        [Test]
        public async Task TheTypeReachesTheApplication()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var inbox = new ConcurrentQueue<XMPPMessage>();
            bob.OnMessage += m => inbox.Enqueue(m);

            await alice.SendMessageAsync(bob.FullJid!, "From the room", MessageType.GroupChat);

            await WaitFor(() => !inbox.IsEmpty, "the delivery");

            inbox.TryDequeue(out var received);

            Assert.Multiple(() =>
            {
                Assert.That(received!.Type, Is.EqualTo(MessageType.GroupChat));
                Assert.That(received.Body,  Is.EqualTo("From the room"));
            });

        }

        #endregion

        #region AGroupchatMessage_IsNotAcknowledged()

        /// <summary>
        /// The heart of it: a message from a room is not answered of its own
        /// accord.
        /// </summary>
        /// <remarks>
        /// The sender there is the room and not a person. A delivery receipt
        /// would go to the room, and that passes it on to everyone in it — out
        /// of a quiet acknowledgement would come a contribution before an
        /// audience, and from every person present for every message. With
        /// twenty people in the room that is four hundred acknowledgements for
        /// twenty lines.
        ///
        /// Checked through the counter-check in the same test: the same message
        /// as <c>chat</c> is acknowledged. Without it the test would pass even
        /// if nothing were acknowledged any more at all.
        /// </remarks>
        [Test]
        public async Task AGroupchatMessage_IsNotAcknowledged()
        {

            var alice      = await ConnectClientAsync("alice");
            var bob        = await ConnectClientAsync("bob");
            var bobSession = Server.SessionOf(bob.FullJid!)!;

            var inbox = new ConcurrentQueue<XMPPMessage>();
            bob.OnMessage += m => inbox.Enqueue(m);

            // By hand, because SendMessageAsync asks for no acknowledgement of
            // its own accord for a room - here the recipient is precisely the
            // one who is to decide.
            await alice.SendRawAsync(
                      $"<message to='{bob.FullJid}' type='groupchat' id='room-1'>" +
                      "<body>From the room</body>" +
                      "<request xmlns='urn:xmpp:receipts'/>" +
                      "<markable xmlns='urn:xmpp:chat-markers:0'/>" +
                      "</message>");

            await WaitFor(() => inbox.Any(m => m.MessageId == "room-1"),
                          "the delivery of the room message");

            // And now the same message as a conversation face to face.
            await alice.SendRawAsync(
                      $"<message to='{bob.BareJid}' type='chat' id='direct-1'>" +
                      "<body>Only to you</body>" +
                      "<request xmlns='urn:xmpp:receipts'/>" +
                      "</message>");

            // What is observed is what Bob sends out - not what arrives at
            // Alice: Alice's receipt tracking knows only messages she sent off
            // through SendMessageAsync herself, and reported an acknowledgement
            // to a raw stanza as an attempt at forgery.
            await WaitFor(() => bobSession.Received.Any(f => f.Contains("id='direct-1'",
                                                                        StringComparison.Ordinal)),
                          "the acknowledgement for the direct message");

            Assert.That(bobSession.Received.Any(f => f.Contains("id='room-1'",
                                                                StringComparison.Ordinal)),
                        Is.False,
                        "A message from a room deserves neither acknowledgement nor marker.");

        }

        #endregion

        #region AHeadline_IsNotAcknowledged()

        /// <summary>
        /// And a shout just as little — RFC 6121, section 5.2.2: "no reply is
        /// expected".
        /// </summary>
        [Test]
        public async Task AHeadline_IsNotAcknowledged()
        {

            var alice      = await ConnectClientAsync("alice");
            var bob        = await ConnectClientAsync("bob");
            var bobSession = Server.SessionOf(bob.FullJid!)!;

            var inbox = new ConcurrentQueue<XMPPMessage>();
            bob.OnMessage += m => inbox.Enqueue(m);

            await alice.SendRawAsync(
                      $"<message to='{bob.FullJid}' type='headline' id='shout-1'>" +
                      "<body>Price fell</body>" +
                      "<request xmlns='urn:xmpp:receipts'/>" +
                      "</message>");

            await WaitFor(() => inbox.Any(m => m.MessageId == "shout-1"),
                          "the delivery of the shout");

            await alice.SendRawAsync(
                      $"<message to='{bob.BareJid}' type='chat' id='direct-2'>" +
                      "<body>Only to you</body>" +
                      "<request xmlns='urn:xmpp:receipts'/>" +
                      "</message>");

            await WaitFor(() => bobSession.Received.Any(f => f.Contains("id='direct-2'",
                                                                        StringComparison.Ordinal)),
                          "the acknowledgement for the direct message");

            Assert.Multiple(() =>
            {

                Assert.That(bobSession.Received.Any(f => f.Contains("id='shout-1'",
                                                                    StringComparison.Ordinal)),
                            Is.False,
                            "A shout expects no answer.");

                Assert.That(inbox.First(m => m.MessageId == "shout-1").Type,
                            Is.EqualTo(MessageType.Headline));

            });

        }

        #endregion

        #region ARoomMessage_RequestsNoReceipt()

        /// <summary>
        /// And the other direction: whoever writes into a room asks for no
        /// acknowledgement.
        /// </summary>
        /// <remarks>
        /// XEP-0184, section 5.3 expressly advises the sender against it. The
        /// reason is the same as at the recipient, only one level earlier: what
        /// is not asked for nobody has to pass over.
        /// </remarks>
        [Test]
        public async Task ARoomMessage_RequestsNoReceipt()
        {

            var alice   = await ConnectClientAsync("alice");
            var session = Server.SessionOf(alice.FullJid!)!;

            await alice.SendMessageAsync($"bob@{Server.Domain}", "Into the room",
                                         MessageType.GroupChat);

            await WaitFor(() => session.Received.Any(f => f.Contains("Into the room",
                                                                     StringComparison.Ordinal)),
                          "the message sent off");

            var outgoing = session.Received.First(f => f.Contains("Into the room", StringComparison.Ordinal));

            Assert.Multiple(() =>
            {

                Assert.That(outgoing, Does.Contain("type='groupchat'"));

                Assert.That(outgoing, Does.Not.Contain("urn:xmpp:receipts"),
                            "Into a room no acknowledgement is asked for.");

                Assert.That(outgoing, Does.Not.Contain("urn:xmpp:chat-markers"),
                            "And no marker.");

            });

        }

        #endregion

    }

}
