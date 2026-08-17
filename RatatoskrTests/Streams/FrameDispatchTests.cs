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
    /// The switch for incoming frames decides on the <b>element name</b> and
    /// not on a prefix — and what it does not know ends the stream with
    /// <c>&lt;unsupported-stanza-type/&gt;</c> (RFC 6120, section 4.9.3.24).
    /// </summary>
    /// <remarks>
    /// A comparison with <c>StartsWith("&lt;iq")</c> also matches
    /// <c>&lt;iqbogus/&gt;</c>, <c>StartsWith("&lt;presence")</c> also
    /// <c>&lt;presence-probe/&gt;</c>, <c>StartsWith("&lt;open")</c> also
    /// <c>&lt;opencast/&gt;</c>. The element name is to be read up to the first
    /// character that no longer belongs to the name; everything else is
    /// guesswork.
    ///
    /// The damage is not theoretical, and with the <c>iq</c> it is still the
    /// most harmless. A <c>&lt;presence-probe/&gt;</c> ran into the presence
    /// handling and counted there as <b>presence</b> — the sender was reported
    /// to their contacts as online, because their element happens to begin with
    /// the same eight characters. And an <c>&lt;opencast/&gt;</c> counted as a
    /// stream opening.
    ///
    /// That the right check already existed in the house does not make it
    /// better: <c>StreamManagementManager.IsCountableStanza</c> has read the
    /// name in full all along — only it answers a different question and stood
    /// in a different place. It is now the common denominator.
    /// </remarks>
    [TestFixture]
    public class FrameDispatchTests : AXMPPTests
    {

        #region Helper functions

        /// <summary>
        /// Collects the stream errors of a client.
        /// </summary>
        private static ConcurrentQueue<StreamError> ErrorBasket(XMPPClient client)
        {

            var basket = new ConcurrentQueue<StreamError>();
            client.OnStreamError += (timestamp, sender, e, ct) => { basket.Enqueue(e); return Task.CompletedTask; };

            return basket;

        }

        /// <summary>
        /// A connected client that does not come back of its own accord after a
        /// tear — otherwise it would run into the test's measurement.
        /// </summary>
        private async Task<XMPPClient> AloneAsync(String localPart = "alice")
        {

            Server.AddAccount(localPart);

            var client = CreateClient(localPart, maxReconnectAttempts: 0);

            await client.ConnectAsync();

            return client;

        }

        #endregion


        #region AnElementThatOnlyBeginsLikeAStanza_IsNotOne()

        /// <summary>
        /// The heart of it: three elements that <b>begin</b> with the name of a
        /// stanza and are none.
        /// </summary>
        /// <remarks>
        /// All three used to take the way of the element they begin with. The
        /// check runs over the stream error, because it carries both statements
        /// in one: it comes only if the switch did <b>not</b> assign the
        /// element, and it names the reason.
        /// </remarks>
        [Test]
        [TestCase("<iqbogus id='x'/>",       TestName = "AnIqbogus_IsNotAnIq")]
        [TestCase("<messages id='x'/>",      TestName = "AMessages_IsNotAMessage")]
        [TestCase("<presence-probe/>",       TestName = "APresenceProbe_IsNotAPresence")]
        [TestCase("<closet/>",               TestName = "ACloset_IsNotAStreamClose")]
        [TestCase("<nonsense xmlns='urn:example:no'/>",
                                             TestName = "AnUnknownElement_IsRefusedToo")]
        public async Task AnElementThatOnlyBeginsLikeAStanza_IsNotOne(String frame)
        {

            var alice   = await AloneAsync();
            var session = Server.SessionOf(alice.FullJid!.ToString())!;
            var errors  = ErrorBasket(alice);

            await alice.SendRawAsync(frame);

            await WaitFor(() => !errors.IsEmpty, "the stream error");

            errors.TryDequeue(out var reported);

            Assert.Multiple(() =>
            {

                Assert.That(reported!.Condition, Is.EqualTo("unsupported-stanza-type"));

                // RFC 6120, section 4.9.1.1: stream errors are beyond recall. A
                // stream that carried on afterwards would be a contradiction in
                // itself.
                Assert.That(reported.IsRecoverable, Is.False,
                            "Whoever sends the same thing again gets the same " +
                            "back - a reconnect does not help.");

            });

            await WaitFor(() => !session.IsOpen, "the end of the stream");

        }

        #endregion

        #region TheRefusalIsNotAStanzaError()

        /// <summary>
        /// And expressly <b>no</b> <c>&lt;bad-request/&gt;</c>: that would be
        /// information about an IQ that does not exist.
        /// </summary>
        /// <remarks>
        /// That is exactly what the server did from D25 on. The type check from
        /// section 8.2.3 rule 2 took hold on an element that is no IQ stanza at
        /// all, and answered it with the stanza kind <c>iq</c> — an answer to a
        /// question nobody asked. The fault lay not in the check but in the
        /// switch ahead of it; it became visible only when the check started
        /// answering.
        /// </remarks>
        [Test]
        public async Task TheRefusalIsNotAStanzaError()
        {

            var alice = await AloneAsync();

            var rawFrames = new ConcurrentQueue<String>();

            alice.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal))
                    rawFrames.Enqueue(x);

                return Task.CompletedTask;

            };

            var errors = ErrorBasket(alice);

            await alice.SendRawAsync("<iqbogus id='no-question'/>");

            await WaitFor(() => !errors.IsEmpty, "the stream error");

            Assert.That(rawFrames.Any(x => x.Contains("bad-request", StringComparison.Ordinal)),
                        Is.False,
                        "An element that is no IQ gets no IQ answer.");

        }

        #endregion

        #region APresenceLookalike_DoesNotMakeAnyoneAvailable()

        /// <summary>
        /// The most tangible damage: <c>&lt;presence-probe/&gt;</c> counted as
        /// presence.
        /// </summary>
        /// <remarks>
        /// The presence handling reads a missing <c>type</c> as "is there". An
        /// element that merely happens to begin with the same eight characters
        /// thereby reported the sender to their contacts as online — a statement
        /// about a person, derived from a string comparison.
        ///
        /// The check runs at the session and not at Bob's client: the state
        /// stands where the handling writes it, and a client that receives
        /// nothing would prove nothing about the moment either.
        ///
        /// The detour over the sign-off is necessary because the client signs on
        /// of its own accord when connecting: without it the availability would
        /// already stand before the test sent anything, and the proof would be
        /// none.
        /// </remarks>
        [Test]
        public async Task APresenceLookalike_DoesNotMakeAnyoneAvailable()
        {

            var alice   = await AloneAsync();
            var session = Server.SessionOf(alice.FullJid!.ToString())!;

            await alice.SendRawAsync("<presence type='unavailable'/>");

            await WaitFor(() => !session.IsAvailable, "the sign-off");

            var errors = ErrorBasket(alice);

            await alice.SendRawAsync("<presence-probe/>");

            await WaitFor(() => !errors.IsEmpty, "the stream error");

            Assert.That(session.IsAvailable, Is.False,
                        "An element that is no <presence/> makes nobody available.");

        }

        #endregion

        #region ALookalikeOfTheStreamOpen_DoesNotCount()

        /// <summary>
        /// <c>&lt;opencast/&gt;</c> is no stream opening.
        /// </summary>
        /// <remarks>
        /// The count of the openings decides whether the server begins the
        /// negotiation afresh. An element counted in by mistake would shift it
        /// into the middle of a running session.
        /// </remarks>
        [Test]
        public async Task ALookalikeOfTheStreamOpen_DoesNotCount()
        {

            var alice   = await AloneAsync();
            var session = Server.SessionOf(alice.FullJid!.ToString())!;

            var before = session.OpenCount;
            var errors = ErrorBasket(alice);

            await alice.SendRawAsync("<opencast/>");

            await WaitFor(() => !errors.IsEmpty, "the stream error");

            Assert.That(session.OpenCount, Is.EqualTo(before),
                        "Only an <open/> opens a stream.");

        }

        #endregion

        #region AnUnknownElementInAKnownNamespace_IsRefusedToo()

        /// <summary>
        /// A known namespace does not make the element known.
        /// </summary>
        /// <remarks>
        /// Section 4.9.3.24 expressly names both: "because the receiving entity
        /// does not understand the namespace <b>or</b> because the receiving
        /// entity does not understand the element name for the applicable
        /// namespace". The branch for XEP-0198 checked only the namespace until
        /// now and let everything inside it drop that it did not know — the last
        /// place in the house where a frame still fell out the back in silence.
        ///
        /// The second case is the more interesting one: <c>&lt;enabled/&gt;</c>
        /// is a <b>real</b> element from XEP-0198 — only the server sends it to
        /// the client and not the other way round. Known does not mean "known in
        /// this direction".
        /// </remarks>
        [Test]
        [TestCase("<nonsense xmlns='urn:xmpp:sm:3'/>",
                  TestName = "AnInventedElementInTheSmNamespace_IsRefused")]
        [TestCase("<enabled xmlns='urn:xmpp:sm:3' id='x'/>",
                  TestName = "AServerToClientElement_IsRefusedFromAClient")]
        public async Task AnUnknownElementInAKnownNamespace_IsRefusedToo(String frame)
        {

            var alice   = await AloneAsync();
            var session = Server.SessionOf(alice.FullJid!.ToString())!;
            var errors  = ErrorBasket(alice);

            await alice.SendRawAsync(frame);

            await WaitFor(() => !errors.IsEmpty, "the stream error");

            errors.TryDequeue(out var reported);

            Assert.That(reported!.Condition, Is.EqualTo("unsupported-stanza-type"));

            await WaitFor(() => !session.IsOpen, "the end of the stream");

        }

        #endregion

        #region TheKnownSmElements_StillReachTheirHandler()

        /// <summary>
        /// The counter-check: what XEP-0198 provides for in this direction goes
        /// on being answered.
        /// </summary>
        /// <remarks>
        /// Without it the collection would pass even if the branch turned
        /// <b>everything</b> in the namespace away — and stream management would
        /// be unusable without a test noticing.
        /// </remarks>
        [Test]
        public async Task TheKnownSmElements_StillReachTheirHandler()
        {

            var alice   = await AloneAsync();
            var session = Server.SessionOf(alice.FullJid!.ToString())!;

            var acknowledgements = new ConcurrentQueue<String>();

            alice.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<",         StringComparison.Ordinal) &&
                    x.Contains("<a ",           StringComparison.Ordinal) &&
                    x.Contains("urn:xmpp:sm:3", StringComparison.Ordinal))
                {
                    acknowledgements.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            await alice.SendRawAsync("<r xmlns='urn:xmpp:sm:3'/>");

            await WaitFor(() => !acknowledgements.IsEmpty, "the acknowledgement of the server");

            Assert.That(session.IsOpen, Is.True);

        }

        #endregion

        #region AnAckFromTheClient_IsProcessedAndClearsTheQueue()

        /// <summary>
        /// The acknowledgement of the client arrives: it is noted and clears the
        /// queue of unacknowledged stanzas.
        /// </summary>
        /// <remarks>
        /// This test was missing, and it showed at a mutation: it declared the
        /// <c>&lt;a/&gt;</c> branch out of scope — with which the
        /// acknowledgement of the client would have ended the stream ever since
        /// D29 — and <b>not a single test</b> fell over it. Over a real
        /// connection no client has ever sent an <c>&lt;a/&gt;</c> to the
        /// server; what was checked was the counter on its own, in
        /// <see cref="StanzaCountingTests"/>.
        ///
        /// The gap is older than the line that made it visible. The branch used
        /// to give nothing back, and whether it ran was therefore not to be seen
        /// from outside — a branch whose effect nobody observes looks like one
        /// nobody needs.
        ///
        /// Both halves belong together: <c>LastAckFromClient</c> shows that the
        /// number was read, the queue that it was applied as well.
        ///
        /// <b>What is measured is the sequence number and not the count</b>, and
        /// that is the correction from D32. At first it said here "after the
        /// acknowledgement fewer stanzas are outstanding than before" — a
        /// statement about a number that also rises for another reason: presence
        /// from Bob comes in after the measurement and before the check. The test
        /// therefore failed in about every third full run with "Expected: less
        /// than 2, But was: 3".
        ///
        /// But an acknowledgement says nothing at all about the count. It says:
        /// <b>everything up to this sequence number is done.</b> That is exactly
        /// what stands there now — and what comes in afterwards may let the
        /// queue grow without disturbing the test.
        /// </remarks>
        [Test]
        public async Task AnAckFromTheClient_IsProcessedAndClearsTheQueue()
        {

            MakeContacts("alice", "bob");

            var alice = await AloneAsync();
            var bob   = await ConnectClientAsync("bob");

            var session = Server.SessionOf(alice.FullJid!.ToString())!;

            // Something the server sends to Alice and that stays lying about
            // unacknowledged.
            await bob.SendMessageAsync(alice.Connection.BareJid, "Hello");

            await WaitFor(() => session.UnacknowledgedToClient > 0,
                          "an unacknowledged stanza at the server");

            var acknowledged = session.PendingToClient[^1].Seq;

            await alice.SendRawAsync($"<a xmlns='urn:xmpp:sm:3' h='{acknowledged}'/>");

            await WaitFor(() => session.LastAckFromClient is not null,
                          "the acknowledgement of the client at the server");

            Assert.Multiple(() =>
            {

                Assert.That(session.LastAckFromClient, Is.EqualTo(acknowledged),
                            "The number reported must have been read.");

                Assert.That(session.PendingToClient.Where(p => p.Seq <= acknowledged),
                            Is.Empty,
                            "Everything up to this sequence number must have " +
                            "disappeared from the queue.");

                Assert.That(session.IsOpen, Is.True,
                            "And it is a known element, no reason to break off.");

            });

        }

        #endregion

        #region AFrameWithoutAnElement_IsIgnored()

        /// <summary>
        /// An empty frame is not an unknown element but none at all — and ends
        /// nothing.
        /// </summary>
        /// <remarks>
        /// Section 4.9.3.24 speaks of "a first-level child of the stream that is
        /// not supported". An empty frame is not a child that is unsupported; it
        /// is not a child.
        ///
        /// In D26 it still fell under the stream error — one line too far,
        /// noticed only when D27 wrote the same rule down for the S2S stream and
        /// the question was unavoidable there (whitespace as a keepalive is
        /// allowed under section 4.6.1).
        ///
        /// The ping afterwards is the real proof: on one stream things are
        /// worked through in order. Once its answer arrives, the server has
        /// already held the empty frame in its hands and made up its mind. So
        /// this test needs no waiting time during which nothing may happen.
        /// </remarks>
        [Test]
        public async Task AFrameWithoutAnElement_IsIgnored()
        {

            var alice   = await AloneAsync();
            var session = Server.SessionOf(alice.FullJid!.ToString())!;
            var errors  = ErrorBasket(alice);

            var replies = new ConcurrentQueue<String>();

            alice.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<",             StringComparison.Ordinal) &&
                    x.Contains("id='afterwards'",       StringComparison.Ordinal))
                {
                    replies.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            await alice.SendRawAsync("   ");

            await alice.SendRawAsync("<iq type='get' id='afterwards'><ping xmlns='urn:xmpp:ping'/></iq>");

            await WaitFor(() => !replies.IsEmpty, "the answer to the ping afterwards");

            Assert.Multiple(() =>
            {

                // Without this precondition the test would check nothing: if the
                // empty frame never arrived at all, it would pass even when the
                // server found it fatal.
                Assert.That(session.Received.Any(f => f.Trim().Length == 0), Is.True,
                            "Precondition: the empty frame must have reached the server.");

                Assert.That(errors,          Is.Empty, "An empty frame is no stream error.");
                Assert.That(session.IsOpen,  Is.True);

            });

        }

        #endregion

        #region TheThreeStanzas_StillReachTheirHandlers()

        /// <summary>
        /// The counter-check: the three real stanzas go on taking their way.
        /// </summary>
        /// <remarks>
        /// Without it this collection would pass even if the switch turned
        /// <b>everything</b> away. A ping suffices as proof for <c>iq</c>,
        /// because it is answered; for <c>message</c> and <c>presence</c> what
        /// counts is that the stream stays up — with a refusal it would be shut
        /// after the first stanza.
        /// </remarks>
        [Test]
        public async Task TheThreeStanzas_StillReachTheirHandlers()
        {

            var alice   = await AloneAsync();
            var session = Server.SessionOf(alice.FullJid!.ToString())!;

            var replies = new ConcurrentQueue<String>();

            alice.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<",                    StringComparison.Ordinal) &&
                    x.Contains("id='still-here'",             StringComparison.Ordinal))
                {
                    replies.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            await alice.SendRawAsync("<presence><show>away</show></presence>");
            await alice.SendRawAsync($"<message to='alice@{Server.Domain}'><body>to myself</body></message>");
            await alice.SendRawAsync("<iq type='get' id='still-here'><ping xmlns='urn:xmpp:ping'/></iq>");

            await WaitFor(() => !replies.IsEmpty, "the answer to the ping");

            Assert.That(session.IsOpen, Is.True,
                        "None of the three may end the stream.");

        }

        #endregion

        #region APrefixedStanza_IsStillAStanza()

        /// <summary>
        /// A namespace prefix does not change the stanza type:
        /// <c>&lt;client:iq/&gt;</c> is an <c>iq</c>.
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 4.8.1 prescribes no particular prefix, only the
        /// namespace. A server that fails at the prefix fails at a freedom the
        /// RFC expressly leaves.
        ///
        /// Only the assignment is checked — that no
        /// <c>&lt;unsupported-stanza-type/&gt;</c> comes of it. What the IQ way
        /// goes on to do with a prefixed element is another question and stands
        /// under "later".
        /// </remarks>
        [Test]
        public async Task APrefixedStanza_IsStillAStanza()
        {

            var alice   = await AloneAsync();
            var session = Server.SessionOf(alice.FullJid!.ToString())!;
            var errors  = ErrorBasket(alice);

            await alice.SendRawAsync(
                      "<client:iq xmlns:client='jabber:client' type='get' id='with-prefix'>" +
                      "<ping xmlns='urn:xmpp:ping'/></client:iq>");

            await WaitAgainst(() => !errors.IsEmpty, "a stream error");

            Assert.That(session.IsOpen, Is.True);

        }

        #endregion

    }

}
