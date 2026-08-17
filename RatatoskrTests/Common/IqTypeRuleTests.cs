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
    /// RFC 6120, section 8.2.3, rule 2: the <c>type</c> attribute of an IQ
    /// stanza is mandatory and has to be <c>get</c>, <c>set</c>, <c>result</c>
    /// or <c>error</c> — otherwise "the recipient or an intermediate router"
    /// answers with <c>&lt;bad-request/&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The half sentence "or an intermediate router" is the actual content of
    /// this rule. With every other stanza a server may pass it through and let
    /// the recipient judge; here it may not. The reason lies in the nature of
    /// IQ: a question-answer pair hangs on <c>type</c> and <c>id</c>, and
    /// whatever carries none of the four values is neither question nor answer.
    /// A server that passes such a thing on merely moves the problem — and if
    /// the counterpart does the same, a stanza wanders through the net that
    /// nobody can answer and that the sender never gets back.
    ///
    /// This server passed it through, and by the most unfavourable route
    /// imaginable: the delivery route treated everything except <c>result</c>
    /// and <c>error</c> as a <b>request</b>. An <c>&lt;iq type='maybe'&gt;</c>
    /// was therefore delivered to a recipient as though they had something to
    /// answer.
    ///
    /// Both are checked: that the four known values still arrive, and that the
    /// fifth does not. To check only the first half would mean not noticing a
    /// barrier against everything.
    /// </remarks>
    [TestFixture]
    public class IqTypeRuleTests : AXMPPTests
    {

        #region Helper functions

        private String Bob => $"bob@{Server.Domain}";

        /// <summary>
        /// Collects the stanza errors of a client.
        /// </summary>
        private static ConcurrentQueue<StanzaError> ErrorBasket(XMPPClient client)
        {

            var basket = new ConcurrentQueue<StanzaError>();
            client.Connection.OnStanzaError += (timestamp, sender, from, e, ct) => { basket.Enqueue(e); return Task.CompletedTask; };

            return basket;

        }

        /// <summary>
        /// Collects the raw incoming stanzas with this id.
        /// </summary>
        private static ConcurrentQueue<String> InboxFor(XMPPClient client, String id)
        {

            var basket = new ConcurrentQueue<String>();

            client.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<",              StringComparison.Ordinal) &&
                    x.Contains($"id='{id}'",         StringComparison.Ordinal))
                {
                    basket.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            return basket;

        }

        /// <summary>
        /// An IQ stanza with a freely chosen type — including none at all.
        /// </summary>
        private static String Stanza(String? type, String? to, String id)
            => "<iq" +
               (type is not null ? $" type='{type}'" : "") +
               $" id='{id}'" +
               (to is not null ? $" to='{to}'" : "") +
               "><ping xmlns='urn:xmpp:ping'/></iq>";

        #endregion


        #region AnIqWithoutAType_IsRefused()

        /// <summary>
        /// The missing attribute: the server answers itself instead of staying
        /// silent or delivering.
        /// </summary>
        [Test]
        public async Task AnIqWithoutAType_IsRefused()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            var errors = ErrorBasket(alice);

            await alice.SendRawAsync(Stanza(null, Bob, "no-type"));

            await WaitFor(() => !errors.IsEmpty, "the refusal by the server");

            errors.TryDequeue(out var refused);

            Assert.That(refused!.Condition, Is.EqualTo("bad-request"));

        }

        #endregion

        #region AnIqWithAnUnknownType_IsRefused()

        /// <summary>
        /// And the same error with an attribute that stands there and means
        /// nothing.
        /// </summary>
        [Test]
        public async Task AnIqWithAnUnknownType_IsRefused()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            var errors = ErrorBasket(alice);

            await alice.SendRawAsync(Stanza("maybe", Bob, "wrong-type"));

            await WaitFor(() => !errors.IsEmpty, "the refusal by the server");

            errors.TryDequeue(out var refused);

            Assert.That(refused!.Condition, Is.EqualTo("bad-request"));

        }

        #endregion

        #region AnIqToTheServerItselfWithoutAType_IsRefused()

        /// <summary>
        /// Without a <c>to</c> as well, that is, directed at one's own server.
        /// </summary>
        /// <remarks>
        /// The test that holds the place of the check fast. A check in the
        /// delivery route — there, where a request is passed on to another
        /// address — would pass the two tests above and let precisely this case
        /// through: what goes to the server itself never comes by there and
        /// would fall out at the back silently. Before, it did that too.
        /// </remarks>
        [Test]
        public async Task AnIqToTheServerItselfWithoutAType_IsRefused()
        {

            var alice = await ConnectClientAsync("alice");

            var errors = ErrorBasket(alice);

            await alice.SendRawAsync(Stanza(null, null, "to-the-server"));

            await WaitFor(() => !errors.IsEmpty, "the refusal by the server");

            errors.TryDequeue(out var refused);

            Assert.That(refused!.Condition, Is.EqualTo("bad-request"));

        }

        #endregion

        #region TheRefusalKeepsTheIdAndAsksToModify()

        /// <summary>
        /// The form of the refusal: the same <c>id</c>, error type
        /// <c>modify</c>, sender the server.
        /// </summary>
        /// <remarks>
        /// The <c>id</c> holds the pair together (rule 3) — without it a refusal
        /// lies with the sender that belongs to none of their pending questions.
        ///
        /// <c>modify</c> and not <c>cancel</c>, because section 8.3.3.1 provides
        /// for it that way with <c>&lt;bad-request/&gt;</c>, and that is no
        /// formality: the type tells the sender whether it is worth trying
        /// again. Here it is worth it — they only have to set the attribute
        /// right.
        ///
        /// And the sender is this server and not the intended recipient. The
        /// difference from <c>&lt;service-unavailable/&gt;</c>, which answers in
        /// the name of the recipient, is one of substance: there the server
        /// answered for somebody, here it did not even accept the stanza. A
        /// recipient as the sender would claim that somebody had looked into it.
        /// </remarks>
        [Test]
        public async Task TheRefusalKeepsTheIdAndAsksToModify()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            var inbox = InboxFor(alice, "with-form");

            await alice.SendRawAsync(Stanza("maybe", Bob, "with-form"));

            await WaitFor(() => !inbox.IsEmpty, "the refusal on the wire");

            inbox.TryDequeue(out var stanza);

            Assert.Multiple(() =>
            {

                Assert.That(stanza, Does.Contain("type='error'"));
                Assert.That(stanza, Does.Contain("id='with-form'"));
                Assert.That(stanza, Does.Contain("<error type='modify'"));
                Assert.That(stanza, Does.Contain("<bad-request "));
                Assert.That(stanza, Does.Contain($"from='{Server.Domain}'"));

            });

        }

        #endregion

        #region TheRefusalComesEvenWithoutAnId()

        /// <summary>
        /// Without an <c>id</c> the refusal is sent all the same — and then
        /// carries none.
        /// </summary>
        /// <remarks>
        /// Rule 2 puts the refusal under no proviso, and the reason carries:
        /// where an unanswered request merely lets the sender wait, this answer
        /// says something about the stanza itself — that its form is not right.
        /// They can use that even when they cannot assign it to any pending
        /// question; all the more so as the missing <c>id</c> belongs to it
        /// itself per rule 1.
        ///
        /// An empty <c>id=''</c> would be the worst outcome: it belongs to no
        /// question and looks as though it belonged to one.
        /// </remarks>
        [Test]
        public async Task TheRefusalComesEvenWithoutAnId()
        {

            var alice = await ConnectClientAsync("alice");

            var inbox = new ConcurrentQueue<String>();

            alice.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<",         StringComparison.Ordinal) &&
                    x.Contains("bad-request",   StringComparison.Ordinal))
                {
                    inbox.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            await alice.SendRawAsync("<iq><ping xmlns='urn:xmpp:ping'/></iq>");

            await WaitFor(() => !inbox.IsEmpty, "the refusal without an id");

            inbox.TryDequeue(out var stanza);

            Assert.That(stanza, Does.Not.Contain("id="),
                        "What had no id gets no empty one back either.");

        }

        #endregion

        #region TheFourKnownTypes_ReachTheResource()

        /// <summary>
        /// The counter-check: all four values provided for still arrive.
        /// </summary>
        /// <remarks>
        /// To the full JID and with two-sided permission, because then all four
        /// take the same route: <c>get</c> and <c>set</c> by way of the presence
        /// check from section 8.5.3.1, <c>result</c> and <c>error</c> by way of
        /// the assignment to the asking resource. A difference in the result
        /// would then come only from the type itself.
        /// </remarks>
        [Test]
        [TestCase("get")]
        [TestCase("set")]
        [TestCase("result")]
        [TestCase("error")]
        public async Task TheFourKnownTypes_ReachTheResource(String type)
        {

            MakeContacts("alice", "bob");

            var alice  = await ConnectClientAsync("alice");
            var bob    = await ConnectClientAsync("bob");

            var atBob = InboxFor(bob, $"type-{type}");

            await alice.SendRawAsync(Stanza(type, bob.FullJid.ToString(), $"type-{type}"));

            await WaitFor(() => !atBob.IsEmpty, $"the delivery of an iq '{type}'");

        }

        #endregion

        #region AnUnknownType_ReachesNoResource()

        /// <summary>
        /// And the same setup with a fifth value: it does not reach the
        /// resource.
        /// </summary>
        /// <remarks>
        /// The heart of the whole thing. Before, this stanza was delivered, and
        /// as a request at that — the delivery route asked only whether the type
        /// was <c>result</c> or <c>error</c>, and treated everything else as
        /// requiring an answer. Bob was thereby presented with something he
        /// would have to answer per rule 3 and that no answer fits.
        ///
        /// Both halves belong in one test: "does not arrive" alone would also be
        /// met if the stanza vanished without a trace, and that would again be
        /// silence instead of an answer.
        /// </remarks>
        [Test]
        public async Task AnUnknownType_ReachesNoResource()
        {

            MakeContacts("alice", "bob");

            var alice  = await ConnectClientAsync("alice");
            var bob    = await ConnectClientAsync("bob");

            var atBob = InboxFor(bob, "type-maybe");
            var errors = ErrorBasket(alice);

            await alice.SendRawAsync(Stanza("maybe", bob.FullJid.ToString(), "type-maybe"));

            await WaitFor(() => !errors.IsEmpty, "the refusal by the server");

            // And give Bob time to get it after all.
            await WaitAgainst(() => !atBob.IsEmpty, "the delivery to Bob");

            errors.TryDequeue(out var refused);

            Assert.That(refused!.Condition, Is.EqualTo("bad-request"));

        }

        #endregion

    }

}
