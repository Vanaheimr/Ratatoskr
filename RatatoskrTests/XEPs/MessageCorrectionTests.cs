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
    /// XEP-0308: "I meant: tomorrow." - one message replaces the previous one.
    /// </summary>
    /// <remarks>
    /// Both are checked: the reading at the stanza and the path over two real
    /// clients. The second one is no ornament here - the correction names an
    /// <c>id</c> that has to be <b>recognised</b> on the other side, and
    /// whether the two sides mean the same one only a round trip says.
    /// </remarks>
    [TestFixture]
    public class MessageCorrectionTests : AXMPPTests
    {

        #region Helper functions

        private static XElement Message(String content)
            => XElement.Parse($"<message xmlns='jabber:client' from='bob@example' " +
                              $"to='alice@example' id='new'>{content}<body>Not after all</body></message>");

        #endregion


        #region TheReplacedId_IsRead()

        /// <summary>The ordinary case.</summary>
        [Test]
        public void TheReplacedId_IsRead()
        {
            Assert.That(MessageCorrection.ReplacedId(
                            Message("<replace id='before' xmlns='urn:xmpp:message-correct:0'/>")),
                        Is.EqualTo("before"));
        }

        #endregion

        #region WithoutAReplace_NothingIsRead()

        /// <summary>An ordinary message corrects nothing.</summary>
        [Test]
        public void WithoutAReplace_NothingIsRead()
        {
            Assert.That(MessageCorrection.ReplacedId(Message("")), Is.Null);
        }

        #endregion

        #region AnEmptyId_CountsAsNone()

        /// <summary>
        /// An empty <c>id</c> counts as none.
        /// </summary>
        /// <remarks>
        /// It points at nothing, and a replacement without a target is none.
        /// Without this check the message would appear as the correction of a
        /// message without a name - and the interface would look for something
        /// that does not exist.
        /// </remarks>
        [Test]
        public void AnEmptyId_CountsAsNone()
        {
            Assert.That(MessageCorrection.ReplacedId(
                            Message("<replace id='' xmlns='urn:xmpp:message-correct:0'/>")),
                        Is.Null);
        }

        #endregion

        #region AReplaceInsideAForwardedMessage_IsNotTheOuterOne()

        /// <summary>
        /// The correction note of a packed-in message does not belong to the
        /// outer one.
        /// </summary>
        /// <remarks>
        /// The same trap as with the delay stamp in D59: A carbon brings a
        /// complete message of its own along in its <c>&lt;forwarded/&gt;</c>.
        /// Whoever searches the whole stanza declares the outer one the
        /// correction of something it never sent.
        /// </remarks>
        [Test]
        public void AReplaceInsideAForwardedMessage_IsNotTheOuterOne()
        {

            var carbon = Message(
                           "<received xmlns='urn:xmpp:carbons:2'>" +
                             "<forwarded xmlns='urn:xmpp:forward:0'>" +
                             "<message xmlns='jabber:client'>" +
                             "<replace id='inner' xmlns='urn:xmpp:message-correct:0'/>" +
                             "<body>inner</body></message>" +
                             "</forwarded></received>");

            Assert.That(MessageCorrection.ReplacedId(carbon), Is.Null);

        }

        #endregion

        #region ACorrection_ArrivesAsSuch()

        /// <summary>
        /// Over the wire: Alice corrects, Bob sees the correction - and knows
        /// which message it supersedes.
        /// </summary>
        /// <remarks>
        /// The <c>id</c> is the whole point. Without it the correction would be
        /// a second message, and the recipient would stand before two lines
        /// without a clue which one holds.
        /// </remarks>
        [Test]
        public async Task ACorrection_ArrivesAsSuch()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync();
            var bob   = await ConnectClientAsync("bob", createAccount: false);

            var inbox = new ConcurrentQueue<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { inbox.Enqueue(m); return Task.CompletedTask; };

            var first = await alice.SendMessageAsync($"bob@{Server.Domain}", "Until this evening");

            await WaitFor(() => inbox.Count == 1, "the first message");

            var correction = await alice.CorrectLastMessageAsync("Until tomorrow evening",
                                                                 $"bob@{Server.Domain}");

            await WaitFor(() => inbox.Count == 2, "the correction");

            inbox.TryDequeue(out var older);
            inbox.TryDequeue(out var newer);

            Assert.Multiple(() =>
            {

                Assert.That(older!.IsCorrection, Is.False,
                            "The first message corrects nothing.");

                Assert.That(newer!.IsCorrection, Is.True);

                Assert.That(newer.ReplacesId, Is.EqualTo(first),
                            "The correction does not point at the message it supersedes.");

                Assert.That(newer.MessageId, Is.Not.EqualTo(first),
                            "XEP-0308: The correction carries an id of its own.");

                Assert.That(newer.Body, Is.EqualTo("Until tomorrow evening"),
                            "The body is the full new text and not the change to it.");

                Assert.That(correction, Is.EqualTo(newer.MessageId));

            });

        }

        #endregion

        #region ACorrectionCanBeCorrected()

        /// <summary>
        /// A correction itself becomes the last message.
        /// </summary>
        /// <remarks>
        /// No special case but the usual one: whoever mistypes mistypes in the
        /// correction as well. Were the second correction to go on pointing at
        /// the original, the first correction would hang in the air at the
        /// recipient's - it would be superseded by nothing and would stand next
        /// to the second one.
        /// </remarks>
        [Test]
        public async Task ACorrectionCanBeCorrected()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync();
            var bob   = await ConnectClientAsync("bob", createAccount: false);

            var inbox = new ConcurrentQueue<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { inbox.Enqueue(m); return Task.CompletedTask; };

            var bob_jid = $"bob@{Server.Domain}";

            await alice.SendMessageAsync(bob_jid, "Until today");

            await WaitFor(() => inbox.Count == 1, "the first message");

            var first = await alice.CorrectLastMessageAsync("Until tomorrow", bob_jid);

            await WaitFor(() => inbox.Count == 2, "the first correction");

            await alice.CorrectLastMessageAsync("Until the day after tomorrow", bob_jid);

            await WaitFor(() => inbox.Count == 3, "the second correction");

            var all = inbox.ToArray();

            Assert.That(all[2].ReplacesId, Is.EqualTo(first),
                        "The second correction supersedes the first, not the original.");

        }

        #endregion

        #region WithoutAPreviousMessage_ThereIsNothingToCorrect()

        /// <summary>
        /// To a recipient nothing has gone out to yet, nothing can be
        /// corrected.
        /// </summary>
        /// <remarks>
        /// The caller gets null and no invented replacement. A correction with
        /// a guessed <c>id</c> would be worse than none: at the recipient's it
        /// supersedes a message they never got, or - worse - a foreign one.
        /// </remarks>
        [Test]
        public async Task WithoutAPreviousMessage_ThereIsNothingToCorrect()
        {

            var alice = await ConnectClientAsync();

            Assert.That(await alice.CorrectLastMessageAsync("too late", $"nobody@{Server.Domain}"),
                        Is.Null);

        }

        #endregion

        #region TheFeature_IsAnnounced()

        /// <summary>
        /// The client announces XEP-0308 in disco#info.
        /// </summary>
        /// <remarks>
        /// Section 4 demands it, and the reason is practical: without the
        /// announcement a counterpart has to assume that their correction
        /// appears as a second message - and then rather sends none.
        /// </remarks>
        [Test]
        public async Task TheFeature_IsAnnounced()
        {

            var alice = await ConnectClientAsync();

            Assert.That(alice.Connection.Disco!.LocalFeatures,
                        Does.Contain("urn:xmpp:message-correct:0"));

        }

        #endregion

    }

}
