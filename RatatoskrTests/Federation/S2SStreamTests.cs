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

using System.Text;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// The S2S protocol layer, checked without a transport underneath.
    /// </summary>
    /// <remarks>
    /// No socket, no server, no time windows: the frames go into a list and
    /// come out of a string. Precisely that is the point - what TCP and
    /// WebSocket have in common shall be checked in common as well and not
    /// twice over the detour of a transport.
    ///
    /// The handshakes are built by hand instead of over a second
    /// <see cref="S2SStream"/> instance. Could both roles be let run against
    /// each other, the test would only check that the class fits itself - an
    /// error in its idea of RFC 7395 would stay invisible.
    /// </remarks>
    [TestFixture]
    public class S2SStreamTests
    {

        #region Data

        private List<String> _sent = null!;

        #endregion

        #region SetUp

        [SetUp]
        public void Clear()
        {
            _sent = [];
        }

        #endregion

        #region Helper functions

        private Task Send(String frame, CancellationToken _)
        {
            _sent.Add(frame);
            return Task.CompletedTask;
        }

        /// <summary>
        /// An inbound stream that takes everything in.
        /// </summary>
        private S2SStream Incoming(List<String>? delivered = null)

            => S2SStream.Accept(
                   "right.example",
                   Send,
                   (peer, stanza) =>
                   {
                       delivered?.Add(stanza);
                       return Task.FromResult(RemoteStanzaResult.Accepted);
                   });

        /// <summary>
        /// An inbound stream that answers with a fixed verdict.
        /// </summary>
        private S2SStream IncomingWith(RemoteStanzaResult verdict)

            => S2SStream.Accept(
                   "right.example",
                   Send,
                   (_, _) => Task.FromResult(verdict));

        private static String OpenFrom(String from, String? to = "right.example", String? id = null)

            => $"<open xmlns='urn:ietf:params:xml:ns:xmpp-framing' from='{from}'" +
               (to is not null ? $" to='{to}'" : "") +
               (id is not null ? $" id='{id}'" : "") +
               " version='1.0'/>";

        private Boolean WasSent(String contains)
            => _sent.Any(f => f.Contains(contains, StringComparison.Ordinal));

        #endregion


        #region TheInitiatorSendsItsDomainInTheStreamHeader()

        /// <summary>
        /// The stream header of the initiator names both domains (RFC 7395,
        /// section 3.4).
        /// </summary>
        [Test]
        public async Task TheInitiatorSendsItsDomainInTheStreamHeader()
        {

            var stream = S2SStream.Initiate("left.example", "right.example", Send);

            await stream.OpenAsync();

            Assert.Multiple(() =>
            {
                Assert.That(_sent, Has.Count.EqualTo(1));
                Assert.That(_sent[0], Does.Contain("urn:ietf:params:xml:ns:xmpp-framing"));
                Assert.That(_sent[0], Does.Contain("from='left.example'"));
                Assert.That(_sent[0], Does.Contain("to='right.example'"));
            });

        }

        #endregion

        #region TheResponderAnswersWithAStreamIdAndFeatures()

        /// <summary>
        /// The recipient hands out the stream id (RFC 7395, section 3.4) and
        /// sends its features (RFC 6120, section 4.3.2).
        /// </summary>
        /// <remarks>
        /// On the stream id dialback hangs later - it is no accessory but the
        /// anchor of the only check that can establish the domain of the far
        /// end.
        /// </remarks>
        [Test]
        public async Task TheResponderAnswersWithAStreamIdAndFeatures()
        {

            var stream = Incoming();

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            Assert.Multiple(() =>
            {
                Assert.That(stream.IsOpen,       Is.True);
                Assert.That(stream.RemoteDomain, Is.EqualTo("left.example"));
                Assert.That(stream.StreamId,     Is.Not.Null.And.Not.Empty);
                Assert.That(WasSent($"id='{stream.StreamId}'"), Is.True,
                            "The identifier handed out has to go out as well.");
                Assert.That(WasSent("stream:features"), Is.True);
            });

        }

        #endregion

        #region AStreamHeaderForAnotherHost_IsRefused()

        /// <summary>
        /// A <c>to</c> that this server does not serve is
        /// <c>&lt;host-unknown/&gt;</c> (RFC 6120, section 4.9.3.6).
        /// </summary>
        [Test]
        public async Task AStreamHeaderForAnotherHost_IsRefused()
        {

            var stream = Incoming();

            await stream.ProcessFrameAsync(OpenFrom("left.example", to: "faraway.example"));

            Assert.Multiple(() =>
            {
                Assert.That(WasSent("host-unknown"), Is.True);
                Assert.That(stream.IsOpen,            Is.False);
                Assert.That(stream.IsClosed,          Is.True);
            });

        }

        #endregion

        #region AStreamHeaderWithoutFrom_IsRefused()

        /// <summary>
        /// Without a <c>from</c> the sender check would have nothing to hold on
        /// to - then there is no stream at all.
        /// </summary>
        [Test]
        public async Task AStreamHeaderWithoutFrom_IsRefused()
        {

            var stream = Incoming();

            await stream.ProcessFrameAsync(
                      "<open xmlns='urn:ietf:params:xml:ns:xmpp-framing' to='right.example' version='1.0'/>");

            Assert.Multiple(() =>
            {
                Assert.That(WasSent("improper-addressing"), Is.True);
                Assert.That(stream.IsOpen,                   Is.False);
                Assert.That(stream.RemoteDomain,             Is.Null);
            });

        }

        #endregion

        #region TheInitiatorRefusesAnAnswerFromAnotherDomain()

        /// <summary>
        /// Whoever answers as somebody other than the one dialled gets no
        /// stream.
        /// </summary>
        /// <remarks>
        /// Without this check the address of the far end would be the only
        /// thing the initiator relies on - and that comes out of a
        /// configuration file or later out of the DNS.
        /// </remarks>
        [Test]
        public async Task TheInitiatorRefusesAnAnswerFromAnotherDomain()
        {

            var stream = S2SStream.Initiate("left.example", "right.example", Send);

            await stream.OpenAsync();
            await stream.ProcessFrameAsync(OpenFrom("evil.example", to: "left.example", id: "abc"));

            Assert.Multiple(() =>
            {
                Assert.That(WasSent("invalid-from"), Is.True);
                Assert.That(stream.IsOpen,            Is.False);
                Assert.That(stream.IsClosed,          Is.True);
            });

        }

        #endregion

        #region TheInitiatorTakesTheStreamIdFromTheAnswer()

        /// <summary>
        /// The identifier is handed out by the recipient; the initiator takes
        /// it over.
        /// </summary>
        [Test]
        public async Task TheInitiatorTakesTheStreamIdFromTheAnswer()
        {

            var stream = S2SStream.Initiate("left.example", "right.example", Send);

            await stream.OpenAsync();
            await stream.ProcessFrameAsync(OpenFrom("right.example", to: "left.example", id: "s-4711"));

            Assert.Multiple(() =>
            {
                Assert.That(stream.IsOpen,   Is.True);
                Assert.That(stream.StreamId, Is.EqualTo("s-4711"));
            });

        }

        #endregion

        #region AStanzaBeforeTheStreamHeader_EndsTheStream()

        /// <summary>
        /// Before the <c>&lt;open/&gt;</c> there are no stanzas.
        /// </summary>
        [Test]
        public async Task AStanzaBeforeTheStreamHeader_EndsTheStream()
        {

            var delivered = new List<String>();
            var stream    = Incoming(delivered);

            await stream.ProcessFrameAsync(
                      "<message from='alice@left.example' to='bob@right.example'><body>Hello</body></message>");

            Assert.Multiple(() =>
            {
                Assert.That(delivered,    Is.Empty, "Without a stream nothing may be delivered.");
                Assert.That(stream.IsClosed, Is.True);
            });

        }

        #endregion

        #region AnAcceptedStanza_ReachesTheRouting()

        /// <summary>
        /// The normal case: after the handshake the stanza goes to the routing
        /// together with the domain of the far end.
        /// </summary>
        [Test]
        public async Task AnAcceptedStanza_ReachesTheRouting()
        {

            var delivered = new List<String>();
            var stream    = Incoming(delivered);

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            var understood = await stream.ProcessFrameAsync(
                                 "<message from='alice@left.example' to='bob@right.example'><body>Hello</body></message>");

            Assert.Multiple(() =>
            {
                Assert.That(understood, Is.True);
                Assert.That(delivered, Has.Count.EqualTo(1));
                Assert.That(delivered[0], Does.Contain("Hello"));
            });

        }

        #endregion

        #region AForeignSender_EndsTheStream()

        /// <summary>
        /// RFC 6120, section 8.1.1.1: a <c>from</c> the far end may not speak
        /// for ends the stream with <c>&lt;invalid-from/&gt;</c>.
        /// </summary>
        /// <remarks>
        /// That is the one point at which the real transport can do more than
        /// <see cref="DirectServerLinks"/>: there the stanza was discarded and
        /// the far end could try again as often as it liked. Here the
        /// connection is shut afterwards.
        /// </remarks>
        [Test]
        public async Task AForeignSender_EndsTheStream()
        {

            var stream = IncomingWith(RemoteStanzaResult.ForeignSender);

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            var refused = new List<String>();
            stream.OnStanzaRefused += (timestamp, sender, reason, ct) => { refused.Add(reason); return Task.CompletedTask; };

            await stream.ProcessFrameAsync(
                      "<message from='boss@bank.example' to='bob@right.example'><body>Transfer, please.</body></message>");

            Assert.Multiple(() =>
            {
                Assert.That(refused,               Is.Not.Empty);
                Assert.That(WasSent("invalid-from"), Is.True);
                Assert.That(stream.IsClosed,          Is.True);
            });

        }

        #endregion

        #region AMalformedSender_EndsTheStreamAsWell()

        /// <summary>
        /// A <c>from</c> that is no JID at all ends the stream just the same.
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 8.1.1.1 does not tell the two cases apart: a
        /// <c>from</c> the far end may not carry, and one that is no address,
        /// are both "invalid". And the reason the first one ends the stream
        /// carries here just as well — whoever sends something once that has no
        /// address does it again at the next attempt.
        ///
        /// Without this test the new verdict from D53 would arrive nowhere in
        /// the stream: it hands everything that is not <c>Accepted</c> on as a
        /// discarded stanza, and the connection would stay open.
        /// </remarks>
        [Test]
        public async Task AMalformedSender_EndsTheStreamAsWell()
        {

            var stream = IncomingWith(RemoteStanzaResult.MalformedSender);

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            var refused = new List<String>();
            stream.OnStanzaRefused += (timestamp, sender, reason, ct) => { refused.Add(reason); return Task.CompletedTask; };

            await stream.ProcessFrameAsync(
                      "<message from='al ice@left.example' to='bob@right.example'><body>Hello</body></message>");

            Assert.Multiple(() =>
            {
                Assert.That(refused,                Is.Not.Empty);
                Assert.That(WasSent("invalid-from"),  Is.True);
                Assert.That(stream.IsClosed,           Is.True);
            });

        }

        #endregion

        #region AMalformedRecipient_DropsOnlyThatStanza()

        /// <summary>
        /// An impossible <b>recipient</b> on the other hand costs only that one
        /// stanza.
        /// </summary>
        /// <remarks>
        /// The counter-check to the previous test, and it draws the line this
        /// is about: with the sender the question stands who is speaking there
        /// — with the recipient a typo in an address. Were that to tear the
        /// federation down, the check would be worse than its use.
        /// </remarks>
        [Test]
        public async Task AMalformedRecipient_DropsOnlyThatStanza()
        {

            var stream = IncomingWith(RemoteStanzaResult.MalformedRecipient);

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            var refused = new List<String>();
            stream.OnStanzaRefused += (timestamp, sender, reason, ct) => { refused.Add(reason); return Task.CompletedTask; };

            await stream.ProcessFrameAsync(
                      "<message from='alice@left.example' to='b ob@right.example'><body>Hello</body></message>");

            Assert.Multiple(() =>
            {
                Assert.That(refused,                Is.Not.Empty);
                Assert.That(WasSent("invalid-from"),  Is.False, "Only the stanza is wrong, not the stream.");
                Assert.That(stream.IsClosed,           Is.False);
                Assert.That(stream.IsOpen,             Is.True);
            });

        }

        #endregion

        #region AForeignRecipient_DropsOnlyThatStanza()

        /// <summary>
        /// The counter-check: a stanza to a third domain is discarded, but the
        /// stream stays.
        /// </summary>
        /// <remarks>
        /// Without this test the previous one would pass even if every refusal
        /// ended the stream - and a single typo in a <c>to</c> would tear the
        /// federation down.
        /// </remarks>
        [Test]
        public async Task AForeignRecipient_DropsOnlyThatStanza()
        {

            var stream = IncomingWith(RemoteStanzaResult.ForeignRecipient);

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            var refused = new List<String>();
            stream.OnStanzaRefused += (timestamp, sender, reason, ct) => { refused.Add(reason); return Task.CompletedTask; };

            await stream.ProcessFrameAsync(
                      "<message from='alice@left.example' to='who@faraway.example'><body>Onwards</body></message>");

            Assert.Multiple(() =>
            {
                Assert.That(refused,                Is.Not.Empty);
                Assert.That(WasSent("invalid-from"),  Is.False, "Only the stanza is wrong, not the stream.");
                Assert.That(stream.IsClosed,           Is.False);
                Assert.That(stream.IsOpen,             Is.True);
            });

        }

        #endregion

        #region AnOutgoingStream_TakesNoStanzas()

        /// <summary>
        /// RFC 6120, section 4.1: a stream carries in one direction. What
        /// arrives on the outbound one is reported and discarded.
        /// </summary>
        /// <remarks>
        /// To carry both over one connection would be XEP-0288 and would have
        /// to be negotiated. Without this boundary it would be unclear which
        /// domain the far end may speak for on which stream - and precisely on
        /// that the sender check hangs.
        /// </remarks>
        [Test]
        public async Task AnOutgoingStream_TakesNoStanzas()
        {

            var stream = S2SStream.Initiate("left.example", "right.example", Send);

            await stream.OpenAsync();
            await stream.ProcessFrameAsync(OpenFrom("right.example", to: "left.example", id: "s-1"));

            var refused = new List<String>();
            stream.OnStanzaRefused += (timestamp, sender, reason, ct) => { refused.Add(reason); return Task.CompletedTask; };

            var understood = await stream.ProcessFrameAsync(
                                 "<message from='bob@right.example' to='alice@left.example'><body>Answer</body></message>");

            Assert.Multiple(() =>
            {
                Assert.That(understood, Is.False);
                Assert.That(refused,  Is.Not.Empty);
            });

        }

        #endregion

        #region SendingBeforeTheHandshake_IsRefused()

        /// <summary>
        /// Before the handshake stands, no stanza goes out - it would otherwise
        /// go to a far end that has not answered yet.
        /// </summary>
        [Test]
        public async Task SendingBeforeTheHandshake_IsRefused()
        {

            var stream = S2SStream.Initiate("left.example", "right.example", Send);

            await stream.OpenAsync();

            var sent = await stream.SendStanzaAsync(
                           "<message from='alice@left.example' to='bob@right.example'/>");

            Assert.Multiple(() =>
            {
                Assert.That(sent,  Is.False);
                Assert.That(_sent, Has.Count.EqualTo(1), "Only the stream header.");
            });

        }

        #endregion

        #region AClosedStream_TakesNothingMore()

        /// <summary>
        /// After the <c>&lt;close/&gt;</c> of the far end it is over.
        /// </summary>
        [Test]
        public async Task AClosedStream_TakesNothingMore()
        {

            var stream = S2SStream.Initiate("left.example", "right.example", Send);

            await stream.OpenAsync();
            await stream.ProcessFrameAsync(OpenFrom("right.example", to: "left.example", id: "s-1"));

            var reason   = "not ended yet";
            stream.OnClosed += (timestamp, sender, r, ct) => { reason = r ?? "(orderly)"; return Task.CompletedTask; };

            await stream.ProcessFrameAsync("<close xmlns='urn:ietf:params:xml:ns:xmpp-framing'/>");

            var sent = await stream.SendStanzaAsync(
                           "<message from='alice@left.example' to='bob@right.example'/>");

            Assert.Multiple(() =>
            {
                Assert.That(stream.IsClosed, Is.True);
                Assert.That(reason,           Is.EqualTo("(orderly)"));
                Assert.That(sent,        Is.False);
            });

        }

        #endregion

        #region TheSameLayerAlsoSpeaksTcpFraming()

        /// <summary>
        /// The same protocol layer, a different framing: over TCP the stream
        /// header is called <c>&lt;stream:stream&gt;</c> and is an open tag
        /// (RFC 6120, section 4.7).
        /// </summary>
        /// <remarks>
        /// The proof for S4b-1. What differs is exclusively the framing;
        /// handshake sequence, stream id, sender check and delivery run
        /// unchanged. That is precisely why this test stands here at the
        /// protocol layer and not at the transport - it manages without a
        /// socket.
        /// </remarks>
        [Test]
        public async Task TheSameLayerAlsoSpeaksTcpFraming()
        {

            var delivered = new List<String>();

            var stream = S2SStream.Accept(
                             "right.example",
                             Send,
                             (peer, stanza) =>
                             {
                                 delivered.Add(stanza);
                                 return Task.FromResult(RemoteStanzaResult.Accepted);
                             },
                             framing: TcpStreamFraming.Instance);

            await stream.ProcessFrameAsync(
                      "<stream:stream xmlns='jabber:server' " +
                      "xmlns:stream='http://etherx.jabber.org/streams' " +
                      "from='left.example' to='right.example' version='1.0'>");

            await stream.ProcessFrameAsync(
                      "<message from='alice@left.example' to='bob@right.example'><body>Hello</body></message>");

            Assert.Multiple(() =>
            {
                Assert.That(stream.IsOpen,        Is.True);
                Assert.That(stream.RemoteDomain,  Is.EqualTo("left.example"));
                Assert.That(stream.StreamId,      Is.Not.Null.And.Not.Empty);
                Assert.That(delivered,           Has.Count.EqualTo(1));

                // The answer carries the TCP framing, not the one from RFC 7395.
                Assert.That(WasSent("<stream:stream"), Is.True);
                Assert.That(WasSent("jabber:server"),  Is.True);
                Assert.That(WasSent("<open "),         Is.False);
            });

        }

        #endregion

        #region TcpFramingClosesWithTheRootElement()

        /// <summary>
        /// Over TCP the stream ends with <c>&lt;/stream:stream&gt;</c>, not
        /// with <c>&lt;close/&gt;</c>.
        /// </summary>
        [Test]
        public async Task TcpFramingClosesWithTheRootElement()
        {

            var stream = S2SStream.Initiate("left.example", "right.example", Send,
                                            framing: TcpStreamFraming.Instance);

            await stream.OpenAsync();
            await stream.ProcessFrameAsync(
                      "<stream:stream xmlns='jabber:server' " +
                      "xmlns:stream='http://etherx.jabber.org/streams' " +
                      "from='right.example' to='left.example' id='s-9' version='1.0'>");

            Assert.That(stream.StreamId, Is.EqualTo("s-9"));

            await stream.CloseAsync();

            Assert.Multiple(() =>
            {
                Assert.That(_sent[0], Does.StartWith("<stream:stream"));
                Assert.That(_sent[0], Does.Not.Contain("/>"),
                            "The stream header is an open tag.");
                Assert.That(_sent[^1], Is.EqualTo("</stream:stream>"));
                Assert.That(stream.IsClosed, Is.True);
            });

        }

        #endregion

        #region TcpFramingCarriesDialbackThroughUnchanged()

        /// <summary>
        /// Dialback runs unchanged over the TCP framing as well - the key hangs
        /// on the stream id, and that exists in both framings.
        /// </summary>
        /// <remarks>
        /// That was the open question from the work plan: dialback is defined
        /// over XML streams, and the WebSocket mapping was a decision of its
        /// own. Here it shows that the layer above notices nothing of it.
        /// </remarks>
        [Test]
        public async Task TcpFramingCarriesDialbackThroughUnchanged()
        {

            var stream = S2SStream.Initiate("left.example", "right.example", Send,
                                            secret:  "s3cr3tf0rd14lb4ck",
                                            framing: TcpStreamFraming.Instance);

            await stream.OpenAsync();
            await stream.ProcessFrameAsync(
                      "<stream:stream xmlns='jabber:server' " +
                      "xmlns:stream='http://etherx.jabber.org/streams' " +
                      "xmlns:db='jabber:server:dialback' " +
                      "from='right.example' to='left.example' id='D60000229F' version='1.0'>");

            // The same vector as in DialbackKeyTests, only with the example
            // domains swapped: the target here is right.example.
            var expected = DialbackKey.Generate("s3cr3tf0rd14lb4ck",
                                                "right.example", "left.example", "D60000229F");

            Assert.Multiple(() =>
            {
                Assert.That(WasSent("<db:result"), Is.True);
                Assert.That(WasSent(expected),     Is.True);
                Assert.That(stream.IsAuthenticated, Is.False, "The confirmation is still missing.");
            });

            await stream.ProcessFrameAsync(
                      "<db:result from='right.example' to='left.example' type='valid'/>");

            Assert.That(stream.IsAuthenticated, Is.True);

        }

        #endregion

        #region ExternalIsOfferedOnlyWhenACertificateCanBeChecked()

        /// <summary>
        /// SASL-EXTERNAL is offered only when there is something to check.
        /// </summary>
        /// <remarks>
        /// An offer without a checkable certificate would be an invitation into
        /// a dead end: the far end would send its <c>&lt;auth/&gt;</c> and
        /// would inevitably get a <c>&lt;failure/&gt;</c>.
        /// </remarks>
        [Test]
        public async Task ExternalIsOfferedOnlyWhenACertificateCanBeChecked()
        {

            var withoutCertificate = Incoming();
            await withoutCertificate.ProcessFrameAsync(OpenFrom("left.example"));

            Assert.That(WasSent("EXTERNAL"), Is.False,
                        "Without a certificate EXTERNAL must not be offered.");

            _sent.Clear();

            var withCertificate = S2SStream.Accept("right.example", Send,
                                                   (_, _) => Task.FromResult(RemoteStanzaResult.Accepted),
                                       externalIdentity: _ => true);

            await withCertificate.ProcessFrameAsync(OpenFrom("left.example"));

            Assert.Multiple(() =>
            {
                Assert.That(WasSent("EXTERNAL"), Is.True);
                Assert.That(WasSent("urn:ietf:params:xml:ns:xmpp-sasl"), Is.True);
            });

        }

        #endregion

        #region AMatchingCertificate_AuthenticatesTheStream()

        /// <summary>
        /// The normal case: the certificate covers the domain, the stream is
        /// identified - without dialback and without a second connection.
        /// </summary>
        [Test]
        public async Task AMatchingCertificate_AuthenticatesTheStream()
        {

            var checkedDomains = new List<String>();

            var stream = S2SStream.Accept("right.example", Send,
                                          (_, _) => Task.FromResult(RemoteStanzaResult.Accepted),
                                          externalIdentity: d => { checkedDomains.Add(d); return true; });

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            var authzid = Convert.ToBase64String(Encoding.UTF8.GetBytes("left.example"));

            await stream.ProcessFrameAsync(
                      $"<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='EXTERNAL'>{authzid}</auth>");

            Assert.Multiple(() =>
            {
                Assert.That(checkedDomains,                 Is.EquivalentTo(new[] { "left.example" }));
                Assert.That(WasSent("<success"),     Is.True);
                Assert.That(stream.IsAuthenticated,   Is.True);
                Assert.That(stream.AuthenticatedBy,   Is.EqualTo("SASL-EXTERNAL"));

                // RFC 6120, section 6.4.6: the stream starts from the beginning.
                Assert.That(stream.IsOpen, Is.False, "After <success/> the stream is opened anew.");
            });

        }

        #endregion

        #region ACertificateThatDoesNotCoverTheDomain_IsRefused()

        /// <summary>
        /// If the certificate does not cover the domain, there is
        /// <c>&lt;not-authorized/&gt;</c> - and no identified stream.
        /// </summary>
        /// <remarks>
        /// The one line on which SASL-EXTERNAL hangs. Were it to fall away, the
        /// procedure would be nothing but a polite request for
        /// self-declaration.
        /// </remarks>
        [Test]
        public async Task ACertificateThatDoesNotCoverTheDomain_IsRefused()
        {

            var stream = S2SStream.Accept("right.example", Send,
                                          (_, _) => Task.FromResult(RemoteStanzaResult.Accepted),
                                          externalIdentity: _ => false);

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            var authzid = Convert.ToBase64String(Encoding.UTF8.GetBytes("left.example"));

            await stream.ProcessFrameAsync(
                      $"<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='EXTERNAL'>{authzid}</auth>");

            Assert.Multiple(() =>
            {
                Assert.That(WasSent("not-authorized"), Is.True);
                Assert.That(WasSent("<success"),       Is.False);
                Assert.That(stream.IsAuthenticated,     Is.False);
            });

        }

        #endregion

        #region ClaimingADifferentDomainThanTheStreamHeader_IsRefused()

        /// <summary>
        /// The authzid has to match what the stream header named.
        /// </summary>
        /// <remarks>
        /// Without this check a stream could be rewritten onto a second
        /// identity afterwards: header for the one domain, identification for
        /// the other. The test deliberately passes a certificate that covers
        /// <b>everything</b> - what is refused here is not refused because of
        /// the certificate but because of the contradiction.
        /// </remarks>
        [Test]
        public async Task ClaimingADifferentDomainThanTheStreamHeader_IsRefused()
        {

            var stream = S2SStream.Accept("right.example", Send,
                                          (_, _) => Task.FromResult(RemoteStanzaResult.Accepted),
                                          externalIdentity: _ => true);

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            var authzid = Convert.ToBase64String(Encoding.UTF8.GetBytes("bank.example"));

            await stream.ProcessFrameAsync(
                      $"<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='EXTERNAL'>{authzid}</auth>");

            Assert.Multiple(() =>
            {
                Assert.That(WasSent("not-authorized"), Is.True);
                Assert.That(stream.IsAuthenticated,     Is.False);
            });

        }

        #endregion

        #region AnEmptyAuthzid_MeansTheStreamHeaderDomain()

        /// <summary>
        /// An empty authzid (<c>=</c>) means "take the identity out of the
        /// certificate" (RFC 6120, section 6.4.2).
        /// </summary>
        [Test]
        public async Task AnEmptyAuthzid_MeansTheStreamHeaderDomain()
        {

            var checkedDomains = new List<String>();

            var stream = S2SStream.Accept("right.example", Send,
                                          (_, _) => Task.FromResult(RemoteStanzaResult.Accepted),
                                          externalIdentity: d => { checkedDomains.Add(d); return true; });

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            await stream.ProcessFrameAsync(
                      "<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='EXTERNAL'>=</auth>");

            Assert.Multiple(() =>
            {
                Assert.That(checkedDomains,               Is.EquivalentTo(new[] { "left.example" }));
                Assert.That(stream.IsAuthenticated, Is.True);
            });

        }

        #endregion

        #region AnUnofferedMechanism_IsRefused()

        /// <summary>
        /// A mechanism other than EXTERNAL is refused.
        /// </summary>
        [Test]
        public async Task AnUnofferedMechanism_IsRefused()
        {

            var stream = S2SStream.Accept("right.example", Send,
                                          (_, _) => Task.FromResult(RemoteStanzaResult.Accepted),
                                          externalIdentity: _ => true);

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            await stream.ProcessFrameAsync(
                      "<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='PLAIN'>eA==</auth>");

            Assert.Multiple(() =>
            {
                Assert.That(WasSent("invalid-mechanism"), Is.True);
                Assert.That(stream.IsAuthenticated,        Is.False);
            });

        }

        #endregion

        #region NoStanzasBeforeExternalHasSucceeded()

        /// <summary>
        /// With SASL-EXTERNAL too it holds: before the identification no
        /// stanza.
        /// </summary>
        [Test]
        public async Task NoStanzasBeforeExternalHasSucceeded()
        {

            var delivered = new List<String>();

            var stream = S2SStream.Accept("right.example", Send,
                                          (_, stanza) =>
                                          {
                                              delivered.Add(stanza);
                                              return Task.FromResult(RemoteStanzaResult.Accepted);
                                          },
                                          verifyKey: (_, _, _) => Task.FromResult(false),
                                          externalIdentity: _ => true);

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            await stream.ProcessFrameAsync(
                      "<message from='alice@left.example' to='bob@right.example'><body>Hello</body></message>");

            Assert.That(delivered, Is.Empty);

        }

        #endregion

        #region TheInitiatorRestartsTheStreamAfterSuccess()

        /// <summary>
        /// On the building side: after <c>&lt;success/&gt;</c> a new stream
        /// header goes out (RFC 6120, section 6.4.6).
        /// </summary>
        [Test]
        public async Task TheInitiatorRestartsTheStreamAfterSuccess()
        {

            var stream = S2SStream.Initiate("left.example", "right.example", Send,
                                            canOfferExternal: true);

            await stream.OpenAsync();
            await stream.ProcessFrameAsync(OpenFrom("right.example", to: "left.example", id: "s-1"));

            await stream.ProcessFrameAsync(
                      "<stream:features xmlns:stream='http://etherx.jabber.org/streams'>" +
                      "<mechanisms xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>" +
                      "<mechanism>EXTERNAL</mechanism></mechanisms></stream:features>");

            Assert.That(WasSent("mechanism='EXTERNAL'"), Is.True,
                        "The offer has to be followed by an <auth/>.");

            var before = _sent.Count;

            await stream.ProcessFrameAsync("<success xmlns='urn:ietf:params:xml:ns:xmpp-sasl'/>");

            Assert.Multiple(() =>
            {
                Assert.That(stream.IsAuthenticated, Is.True);
                Assert.That(_sent, Has.Count.GreaterThan(before));
                Assert.That(_sent[^1], Does.StartWith("<open"),
                            "After the success the stream starts from the beginning.");
            });

        }

        #endregion

        #region ARefusedExternal_DoesNotFallBackToDialback()

        /// <summary>
        /// After a <c>&lt;failure/&gt;</c> there is no second attempt with the
        /// weaker procedure.
        /// </summary>
        /// <remarks>
        /// A decision, not an omission: whoever wanted to identify themselves
        /// by certificate and was refused has a problem that dialback does not
        /// solve but covers up.
        /// </remarks>
        [Test]
        public async Task ARefusedExternal_DoesNotFallBackToDialback()
        {

            var stream = S2SStream.Initiate("left.example", "right.example", Send,
                                            secret: "s3cr3tf0rd14lb4ck",
                                            canOfferExternal: true);

            await stream.OpenAsync();
            await stream.ProcessFrameAsync(OpenFrom("right.example", to: "left.example", id: "s-1"));
            await stream.ProcessFrameAsync(
                      "<stream:features xmlns:stream='http://etherx.jabber.org/streams'>" +
                      "<mechanisms xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>" +
                      "<mechanism>EXTERNAL</mechanism></mechanisms></stream:features>");

            await stream.ProcessFrameAsync(
                      "<failure xmlns='urn:ietf:params:xml:ns:xmpp-sasl'><not-authorized/></failure>");

            Assert.Multiple(() =>
            {
                Assert.That(WasSent("<db:result"),   Is.False, "No fall back to dialback.");
                Assert.That(stream.IsAuthenticated,   Is.False);
                Assert.That(stream.IsClosed,          Is.True);
            });

        }

        #endregion

        #region WithoutExternalOnOffer_TheInitiatorUsesDialback()

        /// <summary>
        /// If the far end offers no EXTERNAL, it carries on with dialback.
        /// </summary>
        [Test]
        public async Task WithoutExternalOnOffer_TheInitiatorUsesDialback()
        {

            var stream = S2SStream.Initiate("left.example", "right.example", Send,
                                            secret: "s3cr3tf0rd14lb4ck",
                                            canOfferExternal: true);

            await stream.OpenAsync();
            await stream.ProcessFrameAsync(OpenFrom("right.example", to: "left.example", id: "s-1"));
            await stream.ProcessFrameAsync(
                      "<stream:features xmlns:stream='http://etherx.jabber.org/streams'/>");

            Assert.Multiple(() =>
            {
                Assert.That(WasSent("<db:result"),           Is.True);
                Assert.That(WasSent("mechanism='EXTERNAL'"), Is.False);
            });

        }

        #endregion

        #region WaitingForAStreamThatNeverOpens_GivesUp()

        /// <summary>
        /// If the stream ends before the handshake stands, nobody waits into
        /// the time limit.
        /// </summary>
        /// <remarks>
        /// Otherwise every delivery to a domain whose server answers the
        /// <c>&lt;open/&gt;</c> with an error would hang until the connection
        /// timeout - and the sender would get their
        /// <c>&lt;remote-server-not-found/&gt;</c> only afterwards.
        /// </remarks>
        [Test]
        public async Task WaitingForAStreamThatNeverOpens_GivesUp()
        {

            var stream = S2SStream.Initiate("left.example", "right.example", Send);

            await stream.OpenAsync();

            var waiting = stream.WaitUntilOpenAsync(TimeSpan.FromSeconds(30));

            await stream.ProcessFrameAsync(
                      "<stream:error xmlns:stream='http://etherx.jabber.org/streams'>" +
                      "<host-unknown xmlns='urn:ietf:params:xml:ns:xmpp-streams'/></stream:error>");

            Assert.That(await waiting.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);

        }

        #endregion

        #region AnUnknownElement_EndsTheStream()

        /// <summary>
        /// RFC 6120, section 4.9.3.24: An element this server does not know on
        /// the topmost level ends the stream with
        /// <c>&lt;unsupported-stanza-type/&gt;</c>.
        /// </summary>
        /// <remarks>
        /// The client connection has held this rule since D26; here an unknown
        /// element was left lying. That was <b>not</b> negligence but an openly
        /// noted gap: what was unmeasured was what Prosody and ejabberd
        /// actually send on an S2S stream — and to break a stream off because
        /// one does not know an element would have been a bet against a foreign
        /// implementation.
        ///
        /// It is measured now: over the full run against both far ends, in both
        /// directions, not a single frame fell through this switch.
        ///
        /// The three cases are the same as with the client — one invented
        /// element and two that only <b>begin</b> with the name of a stanza.
        /// </remarks>
        [Test]
        [TestCase("<nonsense xmlns='urn:example:no'/>")]
        [TestCase("<iqbogus/>")]
        [TestCase("<messages/>")]
        public async Task AnUnknownElement_EndsTheStream(String frame)
        {

            var stream = Incoming();

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            _sent.Clear();

            var handled = await stream.ProcessFrameAsync(frame);

            Assert.Multiple(() =>
            {

                Assert.That(WasSent("unsupported-stanza-type"), Is.True,
                            "The reason has to go over the wire.");

                Assert.That(stream.IsClosed, Is.True,
                            "A stream error is irretrievable (section 4.9.1.1).");

                Assert.That(handled, Is.True,
                            "Handled it is - with an error and not with silence.");

            });

        }

        #endregion

        #region AnAbort_IsAnsweredWithAborted()

        /// <summary>
        /// RFC 6120, section 6.4.4 holds here as well: if the far end breaks
        /// the SASL negotiation off,
        /// <c>&lt;failure&gt;&lt;aborted/&gt;&lt;/failure&gt;</c> follows and
        /// no stream error.
        /// </summary>
        /// <remarks>
        /// This gap came about in D27 and was not found there: before, an
        /// <c>&lt;abort/&gt;</c> was simply left lying here, since the
        /// strictness it ended the stream. Whoever makes a switch strict
        /// inherits every answer it does not know yet — and has to hand it in
        /// afterwards, not wait until somebody trips over it.
        /// </remarks>
        [Test]
        public async Task AnAbort_IsAnsweredWithAborted()
        {

            var stream = Incoming();

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            _sent.Clear();

            var handled = await stream.ProcessFrameAsync(
                              $"<abort xmlns='{S2SStream.SaslNamespace}'/>");

            Assert.Multiple(() =>
            {

                Assert.That(WasSent("<aborted/>"), Is.True);

                Assert.That(WasSent("unsupported-stanza-type"), Is.False,
                            "A break-off is an intended step, no violation.");

                Assert.That(stream.IsClosed, Is.False,
                            "The break-off ends the negotiation, not the stream.");

                Assert.That(handled, Is.True);

            });

        }

        #endregion

        #region AnAbortAtTheInitiator_IsNotAnswered()

        /// <summary>
        /// The other way round not: whoever dialled themselves answers no
        /// break-off — they would be the one sending it.
        /// </summary>
        /// <remarks>
        /// The same asymmetry as with <c>&lt;auth/&gt;</c>: the roles in the
        /// SASL negotiation are handed out, and to let both sides give the same
        /// answer would mean treating them as interchangeable.
        /// </remarks>
        [Test]
        public async Task AnAbortAtTheInitiator_IsNotAnswered()
        {

            var stream = S2SStream.Initiate("left.example", "right.example", Send);

            await stream.OpenAsync();

            _sent.Clear();

            var handled = await stream.ProcessFrameAsync(
                              $"<abort xmlns='{S2SStream.SaslNamespace}'/>");

            Assert.Multiple(() =>
            {
                Assert.That(WasSent("<aborted/>"), Is.False);
                Assert.That(handled,              Is.False, "Responsible for it they are not.");
            });

        }

        #endregion

        #region AFrameWithoutAnElement_IsIgnored()

        /// <summary>
        /// A frame without an element is no unknown element but none at all —
        /// and ends nothing.
        /// </summary>
        /// <remarks>
        /// Section 4.9.3.24 speaks of "a first-level child of the stream that
        /// is not supported". An empty frame is no child that is not supported;
        /// it is no child.
        ///
        /// Over TCP such a thing does not even arrive — <c>SkipProlog</c> in
        /// the parser swallows whitespace, XML declarations and comments
        /// between two elements, and whitespace as a keepalive is expressly
        /// permitted on a stream (section 4.6.1). Over WebSocket every frame is
        /// handed through, and there the distinction carries the whole case.
        /// </remarks>
        [Test]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("\r\n")]
        public async Task AFrameWithoutAnElement_IsIgnored(String frame)
        {

            var stream = Incoming();

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            _sent.Clear();

            await stream.ProcessFrameAsync(frame);

            Assert.Multiple(() =>
            {
                Assert.That(WasSent("unsupported-stanza-type"), Is.False);
                Assert.That(stream.IsClosed,                     Is.False);
            });

        }

        #endregion

    }

}
