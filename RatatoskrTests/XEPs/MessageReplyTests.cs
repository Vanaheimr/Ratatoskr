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
    /// XEP-0461: this message is about that one — and XEP-0428, which says
    /// which part of it only repeats what is already being pointed at.
    /// </summary>
    /// <remarks>
    /// The reference itself is two attributes and hard to get wrong. What is
    /// easy to get wrong is the quotation beside it, because the numbers that
    /// say where it ends are counted in Unicode code points (XEP-0426) and .NET
    /// counts in UTF-16 units. For every text anybody writes a test with, the
    /// two are the same number. They part company at the first emoji.
    ///
    /// That is why the data here is deliberately awkward: an astral character
    /// in the quotation, a line ending from Windows, offsets that run
    /// backwards. Each one is a place where a reasonable implementation is
    /// quietly wrong and every ASCII test stays green.
    ///
    /// <b>What this file cannot decide</b> is whether anybody else reads what we
    /// write the way we meant it. Both sides here are the same code, so both
    /// agree even where both are wrong — the finding of D62 to D65, five times
    /// over. The answer to that is not in this repository but next to it:
    /// <c>XMPPConformanceTests</c> holds the same stanzas up against slixmpp,
    /// which counts in Python, where a string is code points by nature.
    /// </remarks>
    [TestFixture]
    public class MessageReplyTests : AXMPPTests
    {

        #region Data

        /// <summary>
        /// A globe: one code point, two chars in .NET. The whole point of the
        /// awkward data.
        /// </summary>
        private const String Globe = "\U0001F30D";

        #endregion

        #region Helper functions

        /// <summary>
        /// A message stanza with the given extension elements.
        /// </summary>
        private static XElement Message(String extras, String body = "Yes")
            => XElement.Parse($"<message xmlns='jabber:client' from='bob@example' " +
                              $"to='alice@example' id='new'>{extras}" +
                              $"<body>{XmlEscaping.Escape(body)}</body></message>");

        /// <summary>
        /// An answer put together the way the library puts one together, and
        /// then taken through a real parser — because the parser is where the
        /// characters get counted that the offsets are about.
        /// </summary>
        private static XElement Built(String   quotedText,
                                      String?  quotedAuthor,
                                      String   answer,
                                      String   replyToId = "first")
        {

            var composed = MessageReply.Compose(answer,
                                                replyToId,
                                                quotedText:    quotedText,
                                                quotedAuthor:  quotedAuthor);

            return XElement.Parse(
                       "<message xmlns='jabber:client' from='bob@example' to='alice@example' id='new'>" +
                           $"<body>{XmlEscaping.Escape(composed.Body)}</body>" +
                           composed.Extras +
                       "</message>"
                   );

        }

        /// <summary>
        /// What is left of the body once the quotation has been taken out of
        /// it — the reading side, in one line.
        /// </summary>
        private static String Read(XElement stanza)
        {

            var body  = stanza.ChildValue("body")!;
            var range = MessageReply.QuoteRangeIn(stanza, body);

            return range is BodyRange found
                       ? body.Remove(found.Start, found.Length)
                       : body;

        }

        /// <summary>
        /// The <c>end</c> that went onto the wire.
        /// </summary>
        private static String? WireEnd(XElement stanza)
            => stanza.Child(FallbackIndication.Namespace, "fallback")?.
                      Child(FallbackIndication.Namespace, "body")?.
                      Attr("end");

        #endregion


        #region TheAnsweredMessageIsRead()

        /// <summary>
        /// The ordinary case: a reference and an author.
        /// </summary>
        [Test]
        public void TheAnsweredMessageIsRead()
        {

            var reply = MessageReply.RepliesTo(
                            Message("<reply xmlns='urn:xmpp:reply:0' id='before' to='alice@example/home'/>"));

            Assert.Multiple(() =>
            {
                Assert.That(reply?.Id,             Is.EqualTo("before"));
                Assert.That(reply?.To?.ToString(), Is.EqualTo("alice@example/home"));
            });

        }

        #endregion

        #region WithoutAReplyNothingIsRead()

        /// <summary>
        /// An ordinary message answers nothing in particular.
        /// </summary>
        [Test]
        public void WithoutAReplyNothingIsRead()
        {
            Assert.That(MessageReply.RepliesTo(Message("")), Is.Null);
        }

        #endregion

        #region AnEmptyIdCountsAsNone()

        /// <summary>
        /// An empty <c>id</c> is not a reference.
        /// </summary>
        /// <remarks>
        /// It points at nothing. Taken as one it would make the interface look
        /// for a message that cannot exist, and the honest answer to that search
        /// is that there was never a question.
        /// </remarks>
        [Test]
        public void AnEmptyIdCountsAsNone()
        {
            Assert.That(MessageReply.RepliesTo(Message("<reply xmlns='urn:xmpp:reply:0' id=''/>")),
                        Is.Null);
        }

        #endregion

        #region AnUnreadableAuthorDoesNotCostTheReference()

        /// <summary>
        /// A <c>to</c> that is not an address is dropped; the answer stays one.
        /// </summary>
        /// <remarks>
        /// The <c>to</c> is optional in the specification and it is a
        /// convenience — the reference is the id. Throwing the whole reply away
        /// over the decoration would lose what the message is about because of
        /// something it did not need to say.
        /// </remarks>
        [Test]
        public void AnUnreadableAuthorDoesNotCostTheReference()
        {

            var reply = MessageReply.RepliesTo(
                            Message("<reply xmlns='urn:xmpp:reply:0' id='before' to='not an address'/>"));

            Assert.Multiple(() =>
            {
                Assert.That(reply?.Id, Is.EqualTo("before"));
                Assert.That(reply?.To, Is.Null);
            });

        }

        #endregion

        #region AReplyInsideAForwardedMessageIsNotTheOuterOne()

        /// <summary>
        /// The reference of a packed-in message does not belong to the outer
        /// one.
        /// </summary>
        /// <remarks>
        /// The same trap as the delay stamp in D59 and the correction in
        /// XEP-0308: a carbon brings a complete message of its own along.
        /// Whoever searches the whole stanza declares the outer one an answer to
        /// something it was never about.
        /// </remarks>
        [Test]
        public void AReplyInsideAForwardedMessageIsNotTheOuterOne()
        {

            var carbon = Message(
                             "<received xmlns='urn:xmpp:carbons:2'>" +
                                 "<forwarded xmlns='urn:xmpp:forward:0'>" +
                                     "<message xmlns='jabber:client'>" +
                                         "<reply xmlns='urn:xmpp:reply:0' id='inner'/>" +
                                         "<body>inner</body>" +
                                     "</message>" +
                                 "</forwarded>" +
                             "</received>");

            Assert.That(MessageReply.RepliesTo(carbon), Is.Null);

        }

        #endregion


        #region AnEmojiInTheQuoteIsOneCharacterAndNotTwo()

        /// <summary>
        /// The counting of XEP-0426: code points, not what .NET has in its
        /// <c>Length</c>.
        /// </summary>
        /// <remarks>
        /// <b>This is the test the others are decoration around.</b> The
        /// quotation is twelve characters in the counting the wire uses and
        /// thirteen in the counting <c>Length</c> gives. Both numbers cut
        /// somewhere; only one of them cuts where the quotation ends.
        ///
        /// Written the obvious way — <c>quote.Length</c> straight into the
        /// attribute — everything works, for every text anybody is likely to
        /// try. The first person to quote a message with an emoji in it gets an
        /// answer with the first letter missing, and nothing anywhere reports a
        /// fault.
        /// </remarks>
        [Test]
        public void AnEmojiInTheQuoteIsOneCharacterAndNotTwo()
        {

            var stanza = Built($"Sea {Globe} end", null, "Yes");

            Assert.Multiple(() =>
            {

                Assert.That(WireEnd(stanza), Is.EqualTo("12"),
                            "The offset was counted in UTF-16 units. That is 13 for this quotation and " +
                            "names a place one character further on than the one meant.");

                Assert.That(Read(stanza), Is.EqualTo("Yes"),
                            "The quotation was cut in the wrong place.");

            });

        }

        #endregion

        #region AQuotationFromWindowsIsCountedAfterTheParserHasSeenIt()

        /// <summary>
        /// An XML parser eats the carriage return, and the offsets have to know
        /// that beforehand.
        /// </summary>
        /// <remarks>
        /// XML 1.0, section 2.11: every <c>CR LF</c> in text content becomes a
        /// single <c>LF</c> before any application sees it. So a quotation put
        /// together on Windows is one character shorter at the far end than it
        /// was here — per line. Two lines, two characters, and the answer
        /// arrives with its first two letters eaten.
        ///
        /// The check is deliberately made on a stanza that has been through a
        /// real parser rather than on the string that went in. Anything else
        /// would be measuring our own arithmetic against itself.
        /// </remarks>
        [Test]
        public void AQuotationFromWindowsIsCountedAfterTheParserHasSeenIt()
        {

            var stanza = Built("first\r\nsecond", null, "Yes");

            Assert.Multiple(() =>
            {

                Assert.That(stanza.ChildValue("body"), Does.Not.Contain("\r"),
                            "The parser left a carriage return standing, so this test is measuring " +
                            "nothing. The premise of the counting no longer holds.");

                Assert.That(Read(stanza), Is.EqualTo("Yes"),
                            "The line endings were counted as they were written and not as they arrive.");

            });

        }

        #endregion

        #region TheQuotationSurvivesTheRoundTrip()

        /// <summary>
        /// What was quoted comes back out of the message unchanged.
        /// </summary>
        /// <remarks>
        /// The counterpart to the two above: they check that the right thing is
        /// removed, this one that the removed thing is still readable. A client
        /// that does not have the answered message — it arrived before this
        /// session, or into a room nobody here was in — has nothing else to
        /// show.
        /// </remarks>
        [Test]
        public void TheQuotationSurvivesTheRoundTrip()
        {

            var stanza = Built($"Sea {Globe} end", "alice", "Yes");
            var body   = stanza.ChildValue("body")!;
            var range  = FallbackIndication.RangeIn(stanza, MessageReply.Namespace, body)!.Value;

            Assert.Multiple(() =>
            {

                Assert.That(body.Substring(range.Start, range.Length),
                            Is.EqualTo($"> alice:\n> Sea {Globe} end\n"));

                Assert.That(body.Remove(range.Start, range.Length), Is.EqualTo("Yes"));

            });

        }

        #endregion

        #region WithoutOffsetsTheWholeBodyIsFallback()

        /// <summary>
        /// XEP-0428, section 3: an indication that does not say where is about
        /// everything.
        /// </summary>
        [Test]
        public void WithoutOffsetsTheWholeBodyIsFallback()
        {

            var stanza = Message("<reply xmlns='urn:xmpp:reply:0' id='before'/>" +
                                 "<fallback xmlns='urn:xmpp:fallback:0' for='urn:xmpp:reply:0'/>");

            Assert.That(Read(stanza), Is.Empty);

        }

        #endregion

        #region OffsetsThatRunBackwardsCutNothing()

        /// <summary>
        /// Numbers that cannot mean anything mean nothing is removed.
        /// </summary>
        /// <remarks>
        /// The two errors are not equally expensive. Showing a quotation that
        /// should have been hidden is untidy; hiding a piece of somebody's
        /// sentence because their offsets were odd is losing what they said. So:
        /// when the numbers do not hold together, everything stands.
        /// </remarks>
        [Test]
        public void OffsetsThatRunBackwardsCutNothing()
        {

            var backwards = Message("<reply xmlns='urn:xmpp:reply:0' id='before'/>" +
                                    "<fallback xmlns='urn:xmpp:fallback:0' for='urn:xmpp:reply:0'>" +
                                        "<body start='3' end='1'/></fallback>",
                                    "Yes indeed");

            var nonsense  = Message("<reply xmlns='urn:xmpp:reply:0' id='before'/>" +
                                    "<fallback xmlns='urn:xmpp:fallback:0' for='urn:xmpp:reply:0'>" +
                                        "<body start='0' end='two'/></fallback>",
                                    "Yes indeed");

            var negative  = Message("<reply xmlns='urn:xmpp:reply:0' id='before'/>" +
                                    "<fallback xmlns='urn:xmpp:fallback:0' for='urn:xmpp:reply:0'>" +
                                        "<body start='-1' end='4'/></fallback>",
                                    "Yes indeed");

            Assert.Multiple(() =>
            {
                Assert.That(Read(backwards), Is.EqualTo("Yes indeed"));
                Assert.That(Read(nonsense),  Is.EqualTo("Yes indeed"));
                Assert.That(Read(negative),  Is.EqualTo("Yes indeed"));
            });

        }

        #endregion

        #region AnEndBeyondTheBodyStopsAtIt()

        /// <summary>
        /// An offset past the end of the text is the end of the text.
        /// </summary>
        /// <remarks>
        /// Different from the case above, and deliberately so: this one holds
        /// together, it is only too large. A message that says the whole body is
        /// quotation and counts one too far has still said something usable, and
        /// an exception over it would end a conversation on account of an
        /// off-by-one in somebody else's client.
        /// </remarks>
        [Test]
        public void AnEndBeyondTheBodyStopsAtIt()
        {

            var stanza = Message("<reply xmlns='urn:xmpp:reply:0' id='before'/>" +
                                 "<fallback xmlns='urn:xmpp:fallback:0' for='urn:xmpp:reply:0'>" +
                                     "<body start='0' end='999'/></fallback>",
                                 "Yes indeed");

            Assert.That(Read(stanza), Is.Empty);

        }

        #endregion

        #region AFallbackWithoutAReplyCutsNothing()

        /// <summary>
        /// A marking for an answer, in a message that answers nothing, removes
        /// nothing.
        /// </summary>
        /// <remarks>
        /// The stanza contradicts itself, and the two readings cost different
        /// amounts. Acting on the marking takes a piece out of somebody's
        /// sentence on the strength of one element while the rest of the stanza
        /// says there is no quotation there to take.
        /// </remarks>
        [Test]
        public void AFallbackWithoutAReplyCutsNothing()
        {

            var stanza = Message("<fallback xmlns='urn:xmpp:fallback:0' for='urn:xmpp:reply:0'>" +
                                     "<body start='0' end='4'/></fallback>",
                                 "Yes indeed");

            Assert.That(Read(stanza), Is.EqualTo("Yes indeed"));

        }

        #endregion

        #region AFallbackForAnotherExtensionIsNotOurs()

        /// <summary>
        /// The <c>for</c> decides. A fallback belongs to whoever it names.
        /// </summary>
        /// <remarks>
        /// One message may carry several: a quotation for XEP-0461, a
        /// replacement text for a reaction, an explanation for an encrypted part
        /// nobody can read. Whoever takes the first one they find cuts with a
        /// ruler somebody else brought.
        /// </remarks>
        [Test]
        public void AFallbackForAnotherExtensionIsNotOurs()
        {

            var stanza = Message("<reply xmlns='urn:xmpp:reply:0' id='before'/>" +
                                 "<fallback xmlns='urn:xmpp:fallback:0' for='urn:xmpp:some-other:0'>" +
                                     "<body start='0' end='4'/></fallback>",
                                 "Yes indeed");

            Assert.That(Read(stanza), Is.EqualTo("Yes indeed"));

        }

        #endregion

        #region AFallbackOnlyAboutTheSubjectLeavesTheBodyAlone()

        /// <summary>
        /// A marking that speaks about the subject says nothing about the body.
        /// </summary>
        /// <remarks>
        /// The difference to an empty <c>&lt;fallback/&gt;</c>, which is about
        /// everything: this one has a child element, and the child names
        /// something else. Silence about the body and a statement about the body
        /// are not the same thing.
        /// </remarks>
        [Test]
        public void AFallbackOnlyAboutTheSubjectLeavesTheBodyAlone()
        {

            var stanza = Message("<reply xmlns='urn:xmpp:reply:0' id='before'/>" +
                                 "<fallback xmlns='urn:xmpp:fallback:0' for='urn:xmpp:reply:0'>" +
                                     "<subject start='0' end='4'/></fallback>",
                                 "Yes indeed");

            Assert.That(Read(stanza), Is.EqualTo("Yes indeed"));

        }

        #endregion


        #region InARoomTheStanzaIdIsTheOnlyNameThatWorks()

        /// <summary>
        /// XEP-0461, section 4: in a room the <c>id</c> of the stanza must not
        /// be used.
        /// </summary>
        /// <remarks>
        /// The room passes on what the sender wrote, and what the sender wrote
        /// was for the sender. The only name everybody present shares is the one
        /// the room assigned — and a room that assigns none is a room in which
        /// nothing can be answered. Null says that, and it is better than a
        /// reference that points at a different message for every reader.
        /// </remarks>
        [Test]
        public void InARoomTheStanzaIdIsTheOnlyNameThatWorks()
        {

            var from = JID.Parse("room@conference.example/alice");
            var to   = JID.Parse("bob@example/home");

            var withOne    = new XMPPMessage(from, to, "Hello", "sender-id", DateTime.Now,
                                             MessageType.GroupChat, StanzaId: "room-42");

            var withoutOne = new XMPPMessage(from, to, "Hello", "sender-id", DateTime.Now,
                                             MessageType.GroupChat);

            Assert.Multiple(() =>
            {

                Assert.That(withOne.ReplyableId, Is.EqualTo("room-42"));

                Assert.That(withoutOne.ReplyableId, Is.Null,
                            "The id of the stanza was used for a room message. Everybody present sees " +
                            "a different one, so the answer points somewhere else for every reader.");

            });

        }

        #endregion

        #region OutsideARoomTheSendersOwnNameHolds()

        /// <summary>
        /// An <c>&lt;origin-id/&gt;</c> outranks the <c>id</c>; without one the
        /// <c>id</c> holds.
        /// </summary>
        /// <remarks>
        /// XEP-0359 exists because the <c>id</c> is the sender's and only the
        /// sender's. Whoever ignores the origin-id of a client that sets one
        /// answers with the wrong name as soon as anything on the way renames
        /// the stanza.
        /// </remarks>
        [Test]
        public void OutsideARoomTheSendersOwnNameHolds()
        {

            var from = JID.Parse("alice@example/home");
            var to   = JID.Parse("bob@example/home");

            var stable = new XMPPMessage(from, to, "Hello", "stanza-id", DateTime.Now,
                                         MessageType.Chat, OriginId: "mine-1");

            var plain  = new XMPPMessage(from, to, "Hello", "stanza-id", DateTime.Now,
                                         MessageType.Chat);

            Assert.Multiple(() =>
            {
                Assert.That(stable.ReplyableId, Is.EqualTo("mine-1"));
                Assert.That(plain.ReplyableId,  Is.EqualTo("stanza-id"));
            });

        }

        #endregion

        #region AStanzaIdFromSomebodyElseIsNotTheRooms()

        /// <summary>
        /// The <c>by</c> is not decoration.
        /// </summary>
        /// <remarks>
        /// A message out of a room carries the room's stanza-id and the account
        /// archive's, and the two are different numbers for the same message.
        /// Taking the first one in the stanza is not a shortcut, it is a
        /// different answer.
        /// </remarks>
        [Test]
        public void AStanzaIdFromSomebodyElseIsNotTheRooms()
        {

            var stanza = Message("<stanza-id xmlns='urn:xmpp:sid:0' id='archive-7' by='bob@example'/>" +
                                 "<stanza-id xmlns='urn:xmpp:sid:0' id='room-42' by='room@conference.example'/>");

            Assert.Multiple(() =>
            {

                Assert.That(StableIds.StanzaId(stanza, JID.Parse("room@conference.example")),
                            Is.EqualTo("room-42"));

                Assert.That(StableIds.StanzaId(stanza, JID.Parse("carol@example")),
                            Is.Null,
                            "A stanza-id was returned for an entity that never assigned one.");

            });

        }

        #endregion

        #region AReplyWithoutAMessageToAnswerIsRefused()

        /// <summary>
        /// At this point the caller has claimed there is one.
        /// </summary>
        /// <remarks>
        /// <c>ReplyToAsync</c> answers with null when a message cannot be
        /// answered — that is a real case. Handing an empty id to the layer
        /// underneath is not; it would put an answer to nothing on the wire and
        /// leave the far side to decide what that means.
        /// </remarks>
        [Test]
        public void AReplyWithoutAMessageToAnswerIsRefused()
        {

            var alice = CreateClient();

            Assert.ThrowsAsync<ArgumentException>(
                async () => await alice.SendReplyAsync(JID.Parse($"bob@{Server.Domain}"), "Yes", ""));

        }

        #endregion


        #region AnAnswerArrivesAsOne()

        /// <summary>
        /// Over the wire: Alice asks, Bob answers her message, and Alice sees
        /// which one.
        /// </summary>
        /// <remarks>
        /// The id is the whole point, and only a round trip says whether both
        /// sides mean the same one. The quotation travels beside it, in the
        /// body, where a client that has never heard of the extension will show
        /// it — and is taken out again here, where one has.
        /// </remarks>
        [Test]
        public async Task AnAnswerArrivesAsOne()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync();
            var bob   = await ConnectClientAsync("bob", createAccount: false);

            var toAlice = new ConcurrentQueue<XMPPMessage>();
            var toBob   = new ConcurrentQueue<XMPPMessage>();

            alice.OnMessage += (timestamp, sender, m, ct) => { toAlice.Enqueue(m); return Task.CompletedTask; };
            bob.  OnMessage += (timestamp, sender, m, ct) => { toBob.  Enqueue(m); return Task.CompletedTask; };

            var question = await alice.SendMessageAsync(JID.Parse($"bob@{Server.Domain}"),
                                                        $"Sea {Globe} end");

            await WaitFor(() => toBob.Count == 1, "the question");
            toBob.TryDequeue(out var asked);

            var answerId = await bob.ReplyToAsync(asked!, "Yes");

            await WaitFor(() => toAlice.Count == 1, "the answer");
            toAlice.TryDequeue(out var answer);

            Assert.Multiple(() =>
            {

                Assert.That(asked!.IsReply, Is.False,
                            "The question answers nothing.");

                Assert.That(answer!.IsReply, Is.True);

                Assert.That(answer.RepliesTo!.Id, Is.EqualTo(question),
                            "The answer does not point at the message it is about.");

                Assert.That(answer.RepliesTo.To?.Bare.ToString(), Is.EqualTo($"alice@{Server.Domain}"),
                            "The answer names somebody else as the author of the question.");

                Assert.That(answer.Body, Is.EqualTo($"> Sea {Globe} end\nYes"),
                            "The quotation has to be in the body — that is what a client without the " +
                            "extension shows, and it is the whole reason for the duplicate.");

                Assert.That(answer.Text, Is.EqualTo("Yes"),
                            "The quotation was not cut where it ends.");

                Assert.That(answer.Quote, Is.EqualTo($"> Sea {Globe} end\n"));

                Assert.That(answer.MessageId, Is.EqualTo(answerId));

            });

        }

        #endregion

        #region AnAnswerToAnAnswerDoesNotQuoteTheQuotation()

        /// <summary>
        /// Quoted is what was said, not what was quoted while saying it.
        /// </summary>
        /// <remarks>
        /// <b>This is what decides whether the feature is usable at all.</b> The
        /// body of an answer still holds the quotation it came with, so quoting
        /// the body would carry the whole conversation forward one <c>&gt;</c>
        /// deeper with every turn. Four exchanges and the message is mostly
        /// other people's words.
        /// </remarks>
        [Test]
        public async Task AnAnswerToAnAnswerDoesNotQuoteTheQuotation()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync();
            var bob   = await ConnectClientAsync("bob", createAccount: false);

            var toAlice = new ConcurrentQueue<XMPPMessage>();
            var toBob   = new ConcurrentQueue<XMPPMessage>();

            alice.OnMessage += (timestamp, sender, m, ct) => { toAlice.Enqueue(m); return Task.CompletedTask; };
            bob.  OnMessage += (timestamp, sender, m, ct) => { toBob.  Enqueue(m); return Task.CompletedTask; };

            await alice.SendMessageAsync(JID.Parse($"bob@{Server.Domain}"), "Coming along?");

            await WaitFor(() => toBob.Count == 1, "the question");
            toBob.TryDequeue(out var asked);

            await bob.ReplyToAsync(asked!, "Gladly");

            await WaitFor(() => toAlice.Count == 1, "the answer");
            toAlice.TryDequeue(out var answer);

            await alice.ReplyToAsync(answer!, "Eight o'clock then");

            await WaitFor(() => toBob.Count == 1, "the answer to the answer");
            toBob.TryDequeue(out var second);

            Assert.Multiple(() =>
            {

                Assert.That(second!.Quote, Is.EqualTo("> Gladly\n"),
                            "The quotation of the previous turn was quoted along with it. Every " +
                            "exchange then adds a layer, and after four the message is mostly other " +
                            "people's words.");

                Assert.That(second.Text, Is.EqualTo("Eight o'clock then"));

                Assert.That(second.RepliesTo!.Id, Is.EqualTo(answer!.MessageId));

            });

        }

        #endregion

        #region BothNamespacesAreAnnounced()

        /// <summary>
        /// A peer has to be able to find out that this is understood here.
        /// </summary>
        /// <remarks>
        /// <b>The half that decides whether anybody ever sends one.</b> Without
        /// the announcement everything works and nothing happens: the far side
        /// reads the feature list to find out what may be sent, does not find
        /// <c>urn:xmpp:reply:0</c>, and sends an ordinary message. The answers
        /// arrive, correctly, and are not answers.
        ///
        /// Both namespaces, and the second is the one easy to leave out. A
        /// client that learns replies are understood but not that fallback
        /// markings are may leave the quoted lines out — and then a message that
        /// would have read fine anywhere arrives here as an answer to nothing
        /// visible.
        ///
        /// Asked over the wire rather than of the list in memory, because the
        /// list is what we believe and the answer is what we say.
        /// </remarks>
        [Test]
        public async Task BothNamespacesAreAnnounced()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync();
            var bob   = await ConnectClientAsync("bob", createAccount: false);

            var info = await alice.DiscoverInfoAsync(bob.FullJid);

            Assert.Multiple(() =>
            {

                Assert.That(info, Is.Not.Null,
                            "No answer to disco#info at all.");

                Assert.That(info!.Features, Does.Contain("urn:xmpp:reply:0"),
                            "Replies are understood here and nobody is told, so nobody sends one.");

                Assert.That(info.Features, Does.Contain("urn:xmpp:fallback:0"),
                            "The quotation would be hidden here and the far side does not know it may " +
                            "send one.");

            });

        }

        #endregion

    }

}
