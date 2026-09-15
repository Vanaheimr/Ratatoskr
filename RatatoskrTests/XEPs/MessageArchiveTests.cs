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
    /// XEP-0313: asking an archive what was said.
    /// </summary>
    /// <remarks>
    /// The shape of this extension is what the tests are about. <b>The results
    /// do not come back in the answer</b> - they arrive beforehand, as
    /// ordinary-looking messages, and the answer only says they are all there.
    /// So the archive is driven here the way a server drives it: the results
    /// are put in first and the answer afterwards, in that order, because any
    /// other order is not the protocol.
    /// </remarks>
    [TestFixture]
    public class MessageArchiveTests
    {

        #region Data

        private static readonly JID Me    = JID.Parse("me@example.org");
        private static readonly JID Alice = JID.Parse("alice@example.org");

        private MamManager                 _mam     = null!;
        private ConcurrentQueue<XElement>  _asked   = null!;

        /// <summary>
        /// What the archive will do while the query waits: put these results in,
        /// then answer with this.
        /// </summary>
        private Func<String, Task>?        _whileAsking;
        private String                     _answer  = "";

        #endregion

        #region SetUp

        [SetUp]
        public void SetUp()
        {

            _asked        = new ConcurrentQueue<XElement>();
            _whileAsking  = null;
            _answer       = "<fin xmlns='urn:xmpp:mam:2' complete='true'/>";

            _mam = new MamManager(

                       Me,

                       async (to, type, payload, ct) =>
                       {

                           _asked.Enqueue(payload);

                           var queryId = payload.Attr("queryid")!;

                           // The results first, the answer after - which is the
                           // order a server uses and the only one in which the
                           // extension makes sense.
                           if (_whileAsking is not null)
                               await _whileAsking(queryId);

                           return XElement.Parse($"<iq xmlns='jabber:client' type='result' id='q'>{_answer}</iq>");

                       }

                   );

        }

        #endregion

        #region Helper functions

        /// <summary>
        /// A result as an archive sends one.
        /// </summary>
        private static XElement Result(String   queryId,
                                       String   archiveId,
                                       String   body,
                                       String   stamp    = "2026-01-02T03:04:05Z",
                                       String?  from     = null,
                                       Boolean  delayed  = true)

            => XElement.Parse(
                   "<message xmlns='jabber:client' from='me@example.org' to='me@example.org/home' " +
                           "id='the-carrying-stanza'>" +
                       $"<result xmlns='urn:xmpp:mam:2' queryid='{queryId}' id='{archiveId}'>" +
                           "<forwarded xmlns='urn:xmpp:forward:0'>" +
                               (delayed ? $"<delay xmlns='urn:xmpp:delay' stamp='{stamp}'/>" : "") +
                               $"<message xmlns='jabber:client' type='chat' id='original-{archiveId}' " +
                                       $"from='{from ?? "alice@example.org/home"}' to='me@example.org'>" +
                                   $"<body>{body}</body>" +
                               "</message>" +
                           "</forwarded>" +
                       "</result>" +
                   "</message>");

        private Task Put(XElement message)
            => _mam.ProcessMessageAsync(message);

        #endregion


        #region TheQueryCarriesItsFiltersInAForm()

        /// <summary>
        /// The filters travel in a data form, under the name of the extension.
        /// </summary>
        /// <remarks>
        /// The <c>FORM_TYPE</c> is not decoration: it is what lets an archive
        /// add fields of its own without two servers meaning different things
        /// by the same word.
        /// </remarks>
        [Test]
        public async Task TheQueryCarriesItsFiltersInAForm()
        {

            await _mam.QueryAsync(with:   Alice,
                                  start:  new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
                                  max:    10);

            Assert.That(_asked.TryDequeue(out var query), Is.True, "Nothing was asked at all.");

            var xml = query!.ToString();

            Assert.Multiple(() =>
            {

                Assert.That(xml, Does.Contain("urn:xmpp:mam:2"));

                Assert.That(xml, Does.Contain("FORM_TYPE"),
                            "Without it an archive cannot tell which extension's fields these are.");

                Assert.That(xml, Does.Contain("alice@example.org"));

                Assert.That(xml, Does.Contain("2026-01-02T03:04:05Z"),
                            "A moment goes out as UTC to the second (XEP-0082); several archives " +
                            "compare the text.");

                Assert.That(xml, Does.Contain("<max"));

                Assert.That(query.Attr("queryid"), Is.Not.Null.And.Not.Empty,
                            "Without a queryid the results of two queries cannot be told apart.");

            });

        }

        #endregion

        #region AnEmptyBeforeIsTheLastPageAndNotTheFirst()

        /// <summary>
        /// XEP-0059: <c>&lt;before/&gt;</c> with nothing in it means the end.
        /// </summary>
        /// <remarks>
        /// <b>The one thing about paging that is easy to get backwards.</b> An
        /// archive counts from the beginning; what somebody opening a
        /// conversation wants is the end of it. Leaving the element out asks for
        /// the oldest messages there are - which is a perfectly good query and
        /// the wrong one, and looks like an empty conversation whenever the
        /// oldest page is older than anybody remembers.
        /// </remarks>
        [Test]
        public async Task AnEmptyBeforeIsTheLastPageAndNotTheFirst()
        {

            await _mam.QueryAsync(with: Alice, max: 20, before: "");
            await _mam.QueryAsync(with: Alice, max: 20);

            _asked.TryDequeue(out var lastPage);
            _asked.TryDequeue(out var oldest);

            Assert.Multiple(() =>
            {

                Assert.That(lastPage!.ToString(), Does.Contain("<before"),
                            "The last page was not asked for, so this opens a conversation at its " +
                            "beginning.");

                Assert.That(oldest!.ToString(), Does.Not.Contain("<before"),
                            "A query without paging asked for the last page all the same, so the " +
                            "two cannot be told apart.");

            });

        }

        #endregion

        #region TheResultsArriveBeforeTheAnswerAndAreCollected()

        /// <summary>
        /// The shape of the whole extension, in one test.
        /// </summary>
        [Test]
        public async Task TheResultsArriveBeforeTheAnswerAndAreCollected()
        {

            _whileAsking = async queryId =>
            {
                await Put(Result(queryId, "a1", "first"));
                await Put(Result(queryId, "a2", "second"));
            };

            _answer = "<fin xmlns='urn:xmpp:mam:2' complete='true'>" +
                          "<set xmlns='http://jabber.org/protocol/rsm'>" +
                              "<first>a1</first><last>a2</last><count>2</count>" +
                          "</set>" +
                      "</fin>";

            var page = await _mam.QueryAsync(with: Alice);

            Assert.That(page, Is.Not.Null, "The archive answered and nothing came back.");

            Assert.Multiple(() =>
            {

                Assert.That(page!.Messages.Select(m => m.Message.Body),
                            Is.EqualTo(new[] { "first", "second" }),
                            "The order an archive sends in is the order it means.");

                Assert.That(page.Messages[0].ArchiveId, Is.EqualTo("a1"),
                            "The archive's own name for the message is what paging refers to - not " +
                            "the id of the stanza.");

                Assert.That(page.Messages[0].Message.MessageId, Is.EqualTo("original-a1"),
                            "The stanza's own id is still there; it is only not the archive's.");

                Assert.That(page.Complete, Is.True);
                Assert.That(page.First,    Is.EqualTo("a1"));
                Assert.That(page.Last,     Is.EqualTo("a2"));
                Assert.That(page.Count,    Is.EqualTo(2));

            });

        }

        #endregion

        #region AResultNeverTravelsOnAsAMessage()

        /// <summary>
        /// <b>The one that matters.</b> What comes out of an archive is not
        /// news.
        /// </summary>
        /// <remarks>
        /// Every result really is a message that really was sent, so every
        /// branch further along in the connection would handle it correctly -
        /// and the conversation would fill up with its own history as if it were
        /// happening now. A result is claimed here and goes no further, even
        /// when nobody is waiting for it: a query that was given up on still
        /// produces stanzas, and they are archive entries either way.
        /// </remarks>
        [Test]
        public async Task AResultNeverTravelsOnAsAMessage()
        {

            var claimed = await _mam.ProcessMessageAsync(Result("nobody-is-waiting", "a1", "old news"));

            var ordinary = await _mam.ProcessMessageAsync(
                XElement.Parse("<message xmlns='jabber:client' from='alice@example.org/home' " +
                               "to='me@example.org/home' type='chat'><body>hello</body></message>"));

            Assert.Multiple(() =>
            {

                Assert.That(claimed, Is.True,
                            "A result for a query nobody is waiting for was handed on as a message, " +
                            "and it will arrive looking perfectly genuine.");

                Assert.That(ordinary, Is.False,
                            "An ordinary message was swallowed by the archive.");

            });

        }

        #endregion

        #region AResultWithoutAStampIsRefused()

        /// <summary>
        /// A message whose place in the conversation cannot be known.
        /// </summary>
        /// <remarks>
        /// For anything out of an archive the moment of arrival is now, and now
        /// is not when it was said. Filing it under today would put a sentence
        /// from last year at the bottom of the conversation, which is worse than
        /// leaving it out - it is wrong and it looks right.
        /// </remarks>
        [Test]
        public async Task AResultWithoutAStampIsRefused()
        {

            _whileAsking = async queryId =>
            {
                await Put(Result(queryId, "a1", "when was this?", delayed: false));
                await Put(Result(queryId, "a2", "yesterday"));
            };

            var page = await _mam.QueryAsync();

            Assert.Multiple(() =>
            {

                Assert.That(page!.Messages, Has.Count.EqualTo(1),
                            "A result without a stamp was taken in, and its place in the " +
                            "conversation is a guess.");

                Assert.That(page.Messages[0].ArchiveId, Is.EqualTo("a2"));

            });

        }

        #endregion

        #region TheTimeIsTheOneFromTheStamp()

        /// <summary>
        /// When it was said, not when it arrived.
        /// </summary>
        [Test]
        public async Task TheTimeIsTheOneFromTheStamp()
        {

            _whileAsking = async queryId =>
                await Put(Result(queryId, "a1", "long ago", stamp: "2019-05-04T13:37:00Z"));

            var page = await _mam.QueryAsync();

            Assert.That(page!.Messages[0].Timestamp.ToUniversalTime(),
                        Is.EqualTo(new DateTimeOffset(2019, 5, 4, 13, 37, 0, TimeSpan.Zero)),
                        "The moment of arrival was taken for the moment it was written, and for an " +
                        "archive the moment of arrival is always now.");

        }

        #endregion

        #region TwoQueriesAtOnceDoNotMixTheirResults()

        /// <summary>
        /// Two conversations opened in the same moment.
        /// </summary>
        /// <remarks>
        /// Their results are interleaved on the connection and the
        /// <c>queryid</c> is the only thing that tells them apart. A single
        /// pending query instead of a table would put half of one conversation
        /// into the other.
        /// </remarks>
        [Test]
        public async Task TwoQueriesAtOnceDoNotMixTheirResults()
        {

            var other = "";

            _whileAsking = async queryId =>
            {

                // While this query runs, a result of the other one arrives.
                if (other.Length > 0)
                    await Put(Result(other, "wrong", "belongs to the first query"));

                other = queryId;

                await Put(Result(queryId, "right", "belongs to this one"));

            };

            var first   = await _mam.QueryAsync(with: Alice);
            var second  = await _mam.QueryAsync(with: JID.Parse("bob@example.org"));

            Assert.Multiple(() =>
            {

                Assert.That(first!.Messages,  Has.Count.EqualTo(1));

                Assert.That(second!.Messages, Has.Count.EqualTo(1),
                            "A result of the finished query landed in the running one.");

                Assert.That(second.Messages[0].Message.Body, Is.EqualTo("belongs to this one"));

            });

        }

        #endregion

        #region TheEndSaysWhetherThereIsMore()

        /// <summary>
        /// A page that is not the end has to say so.
        /// </summary>
        /// <remarks>
        /// <c>complete='true'</c> is the archive saying there is nothing further
        /// in the direction asked. Its absence means the opposite - and a client
        /// that does not look shows a fraction of a conversation without
        /// mentioning it.
        /// </remarks>
        [Test]
        public async Task TheEndSaysWhetherThereIsMore()
        {

            _answer = "<fin xmlns='urn:xmpp:mam:2'>" +
                          "<set xmlns='http://jabber.org/protocol/rsm'><last>a9</last></set>" +
                      "</fin>";

            var page = await _mam.QueryAsync(max: 1);

            Assert.Multiple(() =>
            {

                Assert.That(page!.Complete, Is.False,
                            "A page without complete='true' was taken for the end of the archive.");

                Assert.That(page.Last, Is.EqualTo("a9"),
                            "Without the last id there is no way to ask for the next page.");

            });

        }

        #endregion

        #region ARefusedQueryIsNotAnEmptyArchive()

        /// <summary>
        /// Null and empty are different answers.
        /// </summary>
        /// <remarks>
        /// An archive that kept nothing has answered; one that refused has not.
        /// A caller deciding whether to ask again needs to know which of the two
        /// happened, and "no messages" reads like a conversation that never took
        /// place.
        /// </remarks>
        [Test]
        public async Task ARefusedQueryIsNotAnEmptyArchive()
        {

            var empty = await _mam.QueryAsync();

            var refusing = new MamManager(Me, (to, type, payload, ct)
                               => Task.FromResult<XElement?>(
                                      XElement.Parse("<iq xmlns='jabber:client' type='error' id='q'/>")));

            var refused = await refusing.QueryAsync();

            Assert.Multiple(() =>
            {
                Assert.That(empty,          Is.Not.Null);
                Assert.That(empty!.Empty,   Is.True);
                Assert.That(refused,        Is.Null, "A refusal came back as an empty archive.");
            });

        }

        #endregion

    }

}
