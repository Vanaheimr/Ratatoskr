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

using System.Net.Security;
using System.Net.Sockets;
using System.Text;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// The same target picture as in <see cref="FederationTests"/> and
    /// <see cref="WebSocketFederationTests"/>, this time over the classic
    /// framing: TCP, <c>jabber:server</c> streams (RFC 6120).
    /// </summary>
    /// <remarks>
    /// The point of this file is the comparison. It checks the same things as
    /// the WebSocket version, and it does so with the same protocol layer -
    /// what has changed is only what lies underneath. Were something to stay
    /// red here that is green there, the separation from S4b-1 would not have
    /// been clean at precisely that place.
    /// </remarks>
    [TestFixture(TcpTlsMode.StartTls)]
    [TestFixture(TcpTlsMode.Direct)]
    public class TcpFederationTests
    {

        #region Data

        /// <summary>
        /// Every test runs twice: once with STARTTLS (RFC 6120 §5.4) and once
        /// with TLS from the first byte on.
        /// </summary>
        /// <remarks>
        /// Both paths end in the same protocol layer but differ in everything
        /// before it. The questions are the same, so they shall be put twice as
        /// well - instead of writing a second set of tests that would have to
        /// be pulled along at every change.
        /// </remarks>
        private readonly TcpTlsMode _mode;

        public TcpFederationTests(TcpTlsMode mode)
        {
            _mode = mode;
        }

        private XMPPServer _left           = null!;
        private XMPPServer _right          = null!;
        private TcpServerLinks _leftLinks  = null!;
        private TcpServerLinks _rightLinks = null!;
        private readonly List<XMPPClient> _clients = [];
        private readonly InternalErrorGuard _guard = new();

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void TwoServers()
        {

            // The guard on both: An error on the one server often comes about
            // through a stanza the other one sent.
            _guard.Reset();

            _left   = _guard.Watched(new XMPPServer("left.example"));
            _right  = _guard.Watched(new XMPPServer("right.example"));

            _left.Start();
            _right.Start();

            TcpServerLinks.Connect(_left, _right, _mode);

            _leftLinks   = (TcpServerLinks) _left.ServerLinks!;
            _rightLinks  = (TcpServerLinks) _right.ServerLinks!;

        }

        [TearDown]
        public async Task CleanUp()
        {

            foreach (var client in _clients)
            {
                try { await client.DisposeAsync(); }
                catch { /* does not matter in the teardown */ }
            }

            _clients.Clear();

            await _leftLinks.DisposeAsync();
            await _rightLinks.DisposeAsync();

            await _left.DisposeAsync();
            await _right.DisposeAsync();

            _guard.AssertClean();

        }

        #endregion

        #region Helper functions

        private async Task<XMPPClient> ConnectAsync(XMPPServer server, String localPart)
        {

            if (server.GetAccount($"{localPart}@{server.Domain}") is null)
                server.AddAccount(localPart);

            var connection = new XMPPConnection(JID.Parse($"{localPart}@{server.Domain}"),
                                                "pw",
                                                server.Uri)
            {
                KeepaliveEnabled            = false,
                MaxReconnectAttempts        = 0,
                ServerCertificateValidator  = server.IsOwnCertificate
            };

            var client = new XMPPClient(connection);
            _clients.Add(client);

            await client.ConnectAsync();

            return client;

        }

        private static async Task WaitFor(Func<Boolean> condition, String what)
        {
            Assert.That(await XMPPServer.WaitUntilAsync(condition),
                        Is.True, $"Timeout while waiting for: {what}");
        }

        #endregion


        #region MessageCrossesTheDomainBoundaryOverTcp()

        /// <summary>
        /// The core: a message over a real <c>jabber:server</c> stream.
        /// </summary>
        [Test]
        public async Task MessageCrossesTheDomainBoundaryOverTcp()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { received.Add(m); return Task.CompletedTask; };

            await alice.SendMessageAsync(bob.BareJid, "Hello over TCP!");

            await WaitFor(() => received.Count > 0, "the message on the other server");

            Assert.Multiple(() =>
            {
                Assert.That(received[0].Body,         Is.EqualTo("Hello over TCP!"));
                Assert.That(received[0].FromBareJid.ToString(), Is.EqualTo("alice@left.example"));
            });

        }

        #endregion

        #region TheAnswerFindsItsWayBackOverTcp()

        [Test]
        public async Task TheAnswerFindsItsWayBackOverTcp()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var atBob    = new List<XMPPMessage>();
            var atAlice  = new List<XMPPMessage>();

            bob.OnMessage    += (timestamp, sender, m, ct) => { atBob.Add(m); return Task.CompletedTask; };
            alice.OnMessage  += (timestamp, sender, m, ct) => { atAlice.Add(m); return Task.CompletedTask; };

            await alice.SendMessageAsync(bob.BareJid, "Question");
            await WaitFor(() => atBob.Count > 0, "the question at Bob's");

            await bob.SendMessageAsync(atBob[0].FromBareJid, "Answer");
            await WaitFor(() => atAlice.Count > 0, "the answer at Alice's");

            Assert.That(atAlice[0].Body, Is.EqualTo("Answer"));

        }

        #endregion

        #region SeveralMessagesReuseTheSameConnection()

        [Test]
        public async Task SeveralMessagesReuseTheSameConnection()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { received.Add(m); return Task.CompletedTask; };

            await alice.SendMessageAsync(bob.BareJid, "one");
            await WaitFor(() => received.Count == 1, "the first message");

            await Task.Delay(TimeSpan.FromSeconds(1));

            var afterTheSetup = _rightLinks.InboundConnectionCount;

            await alice.SendMessageAsync(bob.BareJid, "two");
            await alice.SendMessageAsync(bob.BareJid, "three");
            await WaitFor(() => received.Count == 3, "all three messages");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(afterTheSetup, Is.GreaterThan(0));
                Assert.That(_rightLinks.InboundConnectionCount, Is.EqualTo(afterTheSetup),
                            "Further messages must not build a new connection.");
            });

        }

        #endregion

        #region ALongMessageSurvivesBeingSplitAcrossPackets()

        /// <summary>
        /// A stanza that does not fit into one TCP packet.
        /// </summary>
        /// <remarks>
        /// Over WebSocket this question does not exist - a frame is an element.
        /// Over TCP it is <b>the</b> question, and it never stands out with
        /// short test messages over localhost. Hence a body here that exceeds
        /// every usual packet size.
        /// </remarks>
        [Test]
        public async Task ALongMessageSurvivesBeingSplitAcrossPackets()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { received.Add(m); return Task.CompletedTask; };

            var longBody = String.Concat(Enumerable.Repeat("Long message. ", 4000));

            await alice.SendMessageAsync(bob.BareJid, longBody);

            await WaitFor(() => received.Count > 0, "the long message");

            Assert.That(received[0].Body, Is.EqualTo(longBody));

        }

        #endregion

        #region ADomainWithoutAPeer_StillYieldsAnError()

        [Test]
        public async Task ADomainWithoutAPeer_StillYieldsAnError()
        {

            var alice   = await ConnectAsync(_left, "alice");
            var errors  = new List<StanzaError>();

            alice.OnStanzaError += (timestamp, sender, _, e, ct) => { errors.Add(e); return Task.CompletedTask; };

            await alice.SendMessageAsync(JID.Parse("who@faraway.example"), "Hello?");

            await WaitFor(() => errors.Count > 0, "the error for the unknown domain");

            Assert.That(errors[0].Condition, Is.EqualTo("remote-server-not-found"));

        }

        #endregion

        #region AnImpostorWithoutTheSecret_FailsDialbackOverTcp()

        /// <summary>
        /// Dialback over TCP - whoever merely claims the domain does not get
        /// through.
        /// </summary>
        /// <remarks>
        /// The impostor builds a raw TLS connection up and writes the stream by
        /// hand. That is at the same time the counter-check that the framing
        /// really is RFC 6120 and that nothing of RFC 7395 shows through after
        /// all: a stream header in the wrong format would not even arrive here.
        /// </remarks>
        [Test]
        public Task AnImpostorWithoutTheSecret_FailsDialbackOverTcp()
            => ImpostorFails("left.example");

        #endregion

        #region AnImpostorForAnUnknownDomain_CannotBeVerifiedAtAll()

        /// <summary>
        /// For a domain without a deposited address nobody can be asked - so
        /// nothing is taken in either.
        /// </summary>
        /// <remarks>
        /// The counter-check to the previous test: there the key fails, here
        /// the very possibility of checking fails. Without this case the line
        /// refusing an unknown domain would stay unchecked - and the unknown
        /// domain would be the more convenient way in than the known one.
        /// </remarks>
        [Test]
        public Task AnImpostorForAnUnknownDomain_CannotBeVerifiedAtAll()
            => ImpostorFails("nobody.example");

        #endregion

        #region (helper function) ImpostorFails(claimedDomain)

        /// <summary>
        /// Connects by hand to the TCP S2S entrance, claims a foreign domain,
        /// presents an invented dialback key and tries to deliver afterwards.
        /// </summary>
        private async Task ImpostorFails(String claimedDomain)
        {

            var bob = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { received.Add(m); return Task.CompletedTask; };

            using var client = new TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, _rightLinks.Port);

            // Depending on the mode of operation TLS comes at once or only
            // after the negotiation. The impostor has to take the same path as
            // a real server - otherwise the test only checks that two sides
            // talk past each other.
            await using var tls = _mode == TcpTlsMode.StartTls
                                      ? await StartTlsByHandAsync(client)
                                      : await ImmediateTlsAsync(client);

            var loaded = new StringBuilder();

            _ = Task.Run(async () =>
            {
                var buffer = new Byte[8192];
                try
                {
                    while (true)
                    {
                        var n = await tls.ReadAsync(buffer);
                        if (n <= 0) break;
                        lock (loaded) loaded.Append(Encoding.UTF8.GetString(buffer, 0, n));
                    }
                }
                catch (Exception) { /* connection shut - expected */ }
            });

            async Task Send(String text)
                => await tls.WriteAsync(Encoding.UTF8.GetBytes(text));

            Boolean Saw(String text)
            {
                lock (loaded) return loaded.ToString().Contains(text, StringComparison.Ordinal);
            }

            await Send("<stream:stream xmlns='jabber:server' " +
                       "xmlns:stream='http://etherx.jabber.org/streams' " +
                        "xmlns:db='jabber:server:dialback' " +
                        $"from='{claimedDomain}' to='right.example' version='1.0'>");

            await WaitFor(() => Saw("<stream:stream"), "the stream header of the far end");

            await Send($"<db:result from='{claimedDomain}' to='right.example'>" +
                       "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff" +
                        "</db:result>");

            await WaitFor(() => Saw("db:result") && Saw("type="), "the dialback answer");

            await Send($"<message from='who@{claimedDomain}' to='{bob.BareJid}' type='chat'>" +
                       "<body>Slipped through?</body></message>");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(Saw("type='invalid'"), Is.True,
                            "The invented key has to come back as invalid.");
                Assert.That(Saw("type='valid'"),   Is.False);
                Assert.That(received, Is.Empty,
                            "Without a passed dialback no stanza may be delivered.");
            });

        }

        #endregion

        #region PlaintextGetsNoStream()

        /// <summary>
        /// The core of STARTTLS: whoever declines the encryption gets no stream
        /// - and no unencrypted one.
        /// </summary>
        /// <remarks>
        /// Only meaningful in STARTTLS operation; with TLS from the first byte
        /// on the question does not exist, because in plaintext nothing would
        /// arrive at all.
        ///
        /// That is the line at which the negotiation has its worth. A server
        /// that simply carried on in plaintext after a declined
        /// <c>&lt;starttls/&gt;</c> would have made the encryption into a
        /// politeness that every man in the middle can negotiate away.
        /// </remarks>
        [Test]
        public async Task PlaintextGetsNoStream()
        {

            if (_mode != TcpTlsMode.StartTls)
                Assert.Ignore("Only meaningful in STARTTLS operation.");

            var bob = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { received.Add(m); return Task.CompletedTask; };

            using var client = new TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, _rightLinks.Port);

            var net     = client.GetStream();
            var buffer  = new Byte[8192];

            await net.WriteAsync(Encoding.UTF8.GetBytes(
               "<stream:stream xmlns='jabber:server' " +
                "xmlns:stream='http://etherx.jabber.org/streams' " +
                "from='left.example' to='right.example' version='1.0'>"));

            var greeting = "";

            while (!greeting.Contains("urn:ietf:params:xml:ns:xmpp-tls", StringComparison.Ordinal))
            {
                var n = await net.ReadAsync(buffer);
                if (n <= 0) break;
                greeting += Encoding.UTF8.GetString(buffer, 0, n);
            }

            Assert.That(greeting, Does.Contain("<required/>"),
                        "STARTTLS has to be announced as mandatory.");

            // Instead of <starttls/> a stanza straight away - in plaintext.
            await net.WriteAsync(Encoding.UTF8.GetBytes(
               $"<message from='alice@left.example' to='{bob.BareJid}' type='chat'>" +
                "<body>Without encryption, please.</body></message>"));

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(received, Is.Empty,
                            "In plaintext no stanza may be delivered.");
                Assert.That(greeting, Does.Not.Contain("proceed"),
                            "Without <starttls/> there is no <proceed/>.");
            });

        }

        #endregion

        #region (helper functions) ImmediateTlsAsync / StartTlsByHandAsync

        /// <summary>
        /// TLS from the first byte on.
        /// </summary>
        private async Task<SslStream> ImmediateTlsAsync(TcpClient client)
        {

            var tls = new SslStream(client.GetStream(),
                                    leaveInnerStreamOpen: false,
                                    userCertificateValidationCallback: _right.IsOwnCertificate);

            await tls.AuthenticateAsClientAsync("right.example");

            return tls;

        }

        /// <summary>
        /// The STARTTLS negotiation from RFC 6120, section 5.4 by hand -
        /// plaintext stream, <c>&lt;starttls/&gt;</c>, <c>&lt;proceed/&gt;</c>,
        /// then TLS.
        /// </summary>
        /// <remarks>
        /// By hand and not over <see cref="TcpServerLinks"/>, so that the test
        /// really checks the far end and not merely our own implementation
        /// against itself.
        /// </remarks>
        private async Task<SslStream> StartTlsByHandAsync(TcpClient client)
        {

            var net     = client.GetStream();
            var buffer  = new Byte[8192];

            async Task Raw(String text)
                => await net.WriteAsync(Encoding.UTF8.GetBytes(text));

            async Task<String> Read()
            {
                var n = await net.ReadAsync(buffer);
                return Encoding.UTF8.GetString(buffer, 0, n);
            }

            await Raw("<stream:stream xmlns='jabber:server' " +
                      "xmlns:stream='http://etherx.jabber.org/streams' " +
                      "from='arbitrary.example' to='right.example' version='1.0'>");

            var greeting = "";

            while (!greeting.Contains("urn:ietf:params:xml:ns:xmpp-tls", StringComparison.Ordinal))
                greeting += await Read();

            Assert.That(greeting, Does.Contain("<required/>"),
                        "The server has to announce STARTTLS as mandatory.");

            await Raw("<starttls xmlns='urn:ietf:params:xml:ns:xmpp-tls'/>");

            var reply = "";

            while (!reply.Contains("proceed", StringComparison.Ordinal) &&
                   !reply.Contains("failure", StringComparison.Ordinal))
                reply += await Read();

            Assert.That(reply, Does.Contain("proceed"));

            var tls = new SslStream(net,
                                    leaveInnerStreamOpen: false,
                                    userCertificateValidationCallback: _right.IsOwnCertificate);

            await tls.AuthenticateAsClientAsync("right.example");

            return tls;

        }

        #endregion

        #region DisposingTheLinks_ClosesEstablishedInboundConnections()

        /// <summary>
        /// A finished S2S branch leaves no accepted connection open.
        /// </summary>
        /// <remarks>
        /// Cancelling the token is not enough for that: the reading on a socket
        /// does not break off reliably with it, the loop stays put until the
        /// far end hangs up. Until then <b>it</b> considers the connection
        /// usable and goes on sending over it - all of that is lost, and nobody
        /// learns of it.
        ///
        /// Found in the run against Prosody: after the end of a test server
        /// Prosody went on answering the next request over the long dead socket
        /// for thirty seconds. Between two instances of this server that never
        /// stood out, because there both sides disappear at the same time.
        ///
        /// Without TLS, because this is about the socket and not about the
        /// handshake over it.
        /// </remarks>
        [Test]
        public async Task DisposingTheLinks_ClosesEstablishedInboundConnections()
        {

            await using var server = _guard.Watched(new XMPPServer("alone.example", useTLS: false));
            server.Start();

            var links = new TcpServerLinks(server, mode: TcpTlsMode.None);

            using var peer = new TcpClient();
            await peer.ConnectAsync(System.Net.IPAddress.Loopback, links.Port);

            await WaitFor(() => links.InboundConnectionCount > 0,
                          "the accepted connection");

            await links.DisposeAsync();

            // A closed socket delivers 0 bytes on reading. If it stays open,
            // the time limit runs out and the test fails on precisely that.
            var buffer = new Byte[1];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var loaded = await peer.GetStream().ReadAsync(buffer, cts.Token);

            Assert.That(loaded, Is.Zero,
                        "The far end still considers the connection open.");

        }

        #endregion

    }

}
