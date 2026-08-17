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

using System.Net.WebSockets;
using System.Text;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// The same target picture as in <see cref="FederationTests"/> - two
    /// servers, two clients, a message across the domain border -, this time
    /// however over a real net: <see cref="WebSocketServerLinks"/> instead of
    /// <see cref="DirectServerLinks"/>.
    /// </summary>
    /// <remarks>
    /// The difference to <see cref="FederationTests"/> is precisely the line in
    /// the setup that connects the two servers. Everything else - routing,
    /// addressing, sender check - is checked there already and does not have to
    /// be checked here once more; here it is about the transport itself: does a
    /// stanza really arrive over a socket, through TLS, unfolded twice
    /// (WebSocket frame, then S2S frame) and put together again.
    /// </remarks>
    [TestFixture]
    public class WebSocketFederationTests
    {

        #region Data

        private XMPPServer _left  = null!;
        private XMPPServer _right = null!;
        private WebSocketServerLinks _leftLinks = null!;
        private WebSocketServerLinks _rightLinks = null!;
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

            WebSocketServerLinks.Connect(_left, _right);

            _leftLinks   = (WebSocketServerLinks) _left.ServerLinks!;
            _rightLinks  = (WebSocketServerLinks) _right.ServerLinks!;

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

            var connection = new XMPPConnection($"{localPart}@{server.Domain}",
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

        /// <summary>
        /// An impostor: connects by hand to the S2S entrance of a server and
        /// claims to speak for a foreign domain.
        /// </summary>
        /// <remarks>
        /// On foot over a raw <see cref="ClientWebSocket"/> on purpose and not
        /// over <see cref="WebSocketServerLinks"/>: its outbound branch would
        /// dutifully produce a correct key. An attacker however does not have
        /// the secret of the domain - precisely that is what is to be checked.
        /// </remarks>
        private sealed class Impostor : IAsyncDisposable
        {

            private readonly ClientWebSocket _socket = new();

            public List<String> Received { get; } = [];

            public async Task ConnectAsync(WebSocketServerLinks target, XMPPServer targetServer)
            {

                _socket.Options.AddSubProtocol("xmpp-server");
                _socket.Options.RemoteCertificateValidationCallback = targetServer.IsOwnCertificate;

                await _socket.ConnectAsync(new Uri(target.Uri), CancellationToken.None);

                _ = ReadAsync();

            }

            public async Task SendAsync(String frame)
                => await _socket.SendAsync(Encoding.UTF8.GetBytes(frame),
                                           WebSocketMessageType.Text, true, CancellationToken.None);

            private async Task ReadAsync()
            {

                var buffer = new Byte[8192];

                try
                {
                    while (_socket.State == WebSocketState.Open)
                    {

                        var result = await _socket.ReceiveAsync(buffer, CancellationToken.None);

                        if (result.MessageType == WebSocketMessageType.Close)
                            break;

                        lock (Received)
                            Received.Add(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    }
                }
                catch (Exception)
                {
                    // Connection shut - in the test the expected outcome.
                }

            }

            public Boolean Saw(String text)
            {
                lock (Received)
                    return Received.Any(f => f.Contains(text, StringComparison.Ordinal));
            }

            public ValueTask DisposeAsync()
            {
                try { _socket.Dispose(); } catch { /* does not matter */ }
                return ValueTask.CompletedTask;
            }

        }

        #endregion


        #region MessageCrossesTheDomainBoundaryOverWebSocket()

        /// <summary>
        /// The core: a message goes through two real servers, connected over a
        /// real WebSocket S2S link.
        /// </summary>
        [Test]
        public async Task MessageCrossesTheDomainBoundaryOverWebSocket()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { received.Add(m); return Task.CompletedTask; };

            await alice.SendMessageAsync(bob.BareJid, "Hello over the real wire!");

            await WaitFor(() => received.Count > 0, "the message on the other server");

            Assert.Multiple(() =>
            {
                Assert.That(received[0].Body,         Is.EqualTo("Hello over the real wire!"));
                Assert.That(received[0].FromBareJid,  Is.EqualTo("alice@left.example"));
            });

        }

        #endregion

        #region TheAnswerFindsItsWayBackOverWebSocket()

        /// <summary>
        /// Back the answer runs over the second, independently built link in
        /// the reverse direction.
        /// </summary>
        [Test]
        public async Task TheAnswerFindsItsWayBackOverWebSocket()
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

        /// <summary>
        /// The second and third message do not build a new connection any more
        /// - the connection cache takes hold.
        /// </summary>
        /// <remarks>
        /// What is checked is that the number of connections does not
        /// <b>grow</b>, not that it has a particular value. How many the first
        /// exchange needs hangs on what else goes across the border: with
        /// dialback a verification connection comes along per direction, and
        /// Bob's automatic delivery receipt (XEP-0184/0333) builds the reverse
        /// direction straight away too. To fix a particular number here would
        /// mean pulling the test along at every such change without it saying
        /// any more about the reuse for it.
        /// </remarks>
        [Test]
        public async Task SeveralMessagesReuseTheSameConnection()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { received.Add(m); return Task.CompletedTask; };

            await alice.SendMessageAsync(bob.BareJid, "one");
            await WaitFor(() => received.Count == 1, "the first message");

            // Give the reverse direction time to build itself up as well -
            // otherwise the comparison below would count a connection that had
            // only just come about anyway.
            await Task.Delay(TimeSpan.FromSeconds(1));

            var afterTheSetup = _rightLinks.InboundConnectionCount;

            await alice.SendMessageAsync(bob.BareJid, "two");
            await alice.SendMessageAsync(bob.BareJid, "three");
            await WaitFor(() => received.Count == 3, "all three messages");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(afterTheSetup, Is.GreaterThan(0),
                            "The first exchange must have needed a connection at all.");
                Assert.That(_rightLinks.InboundConnectionCount, Is.EqualTo(afterTheSetup),
                            "Further messages must not build a new S2S connection.");
            });

        }

        #endregion

        #region ADomainWithoutAPeer_StillYieldsAnError()

        /// <summary>
        /// An unknown domain still leads to the error, now over the real
        /// transport instead of over <see cref="DirectServerLinks"/>.
        /// </summary>
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

        #region AnImpostorWithoutTheSecret_FailsDialback()

        /// <summary>
        /// The point of dialback: whoever merely claims the domain does not get
        /// through (XEP-0220).
        /// </summary>
        /// <remarks>
        /// The impostor builds up regularly and presents a self-invented key
        /// for <c>left.example</c>. The accepting server thereupon asks not it
        /// but the address <b>it itself</b> has deposited for
        /// <c>left.example</c> - and the real <c>left.example</c> does not know
        /// the key. Precisely on that the procedure rests: the check never asks
        /// the one being checked.
        /// </remarks>
        [Test]
        public async Task AnImpostorWithoutTheSecret_FailsDialback()
        {

            var bob = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { received.Add(m); return Task.CompletedTask; };

            await using var evil = new Impostor();
            await evil.ConnectAsync(_rightLinks, _right);

            await evil.SendAsync(
              "<open xmlns='urn:ietf:params:xml:ns:xmpp-framing' " +
                "from='left.example' to='right.example' version='1.0'/>");

            await WaitFor(() => evil.Saw("<open"), "the stream header of the far end");

            // A freely invented key - the attacker does not have the secret of
            // left.example.
            await evil.SendAsync(
              "<db:result xmlns:db='jabber:server:dialback' " +
                "from='left.example' to='right.example'>" +
                "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff" +
                "</db:result>");

            await WaitFor(() => evil.Saw("db:result") && evil.Saw("type="),
                          "the dialback answer");

            // And the attempt to deliver nevertheless.
            await evil.SendAsync(
              $"<message from='alice@left.example' to='{bob.BareJid}' type='chat'>" +
                "<body>Slipped through?</body></message>");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(evil.Saw("type='invalid'"), Is.True,
                            "The invented key has to come back as invalid.");
                Assert.That(evil.Saw("type='valid'"),   Is.False);
                Assert.That(received, Is.Empty,
                            "Without a passed dialback no stanza may be delivered.");
            });

        }

        #endregion

        #region AnImpostorForAnUnknownDomain_CannotBeVerifiedAtAll()

        /// <summary>
        /// For a domain there is no deposited address for, nobody can be asked
        /// - so nothing is taken in either.
        /// </summary>
        /// <remarks>
        /// The counter-check to the previous test: there the key failed, here
        /// the very possibility of checking fails. Both have to lead to a
        /// refusal, otherwise the unknown domain would be the more convenient
        /// way in.
        /// </remarks>
        [Test]
        public async Task AnImpostorForAnUnknownDomain_CannotBeVerifiedAtAll()
        {

            var bob = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { received.Add(m); return Task.CompletedTask; };

            await using var evil = new Impostor();
            await evil.ConnectAsync(_rightLinks, _right);

            await evil.SendAsync(
              "<open xmlns='urn:ietf:params:xml:ns:xmpp-framing' " +
                "from='nobody.example' to='right.example' version='1.0'/>");

            await WaitFor(() => evil.Saw("<open"), "the stream header of the far end");

            await evil.SendAsync(
              "<db:result xmlns:db='jabber:server:dialback' " +
                "from='nobody.example' to='right.example'>" +
                "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff" +
                "</db:result>");

            await WaitFor(() => evil.Saw("db:result") && evil.Saw("type="),
                          "the dialback answer");

            await evil.SendAsync(
              $"<message from='who@nobody.example' to='{bob.BareJid}' type='chat'>" +
                "<body>And like this?</body></message>");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(evil.Saw("type='invalid'"), Is.True);
                Assert.That(received, Is.Empty);
            });

        }

        #endregion

        #region SpoofedSender_IsRejectedAndEndsTheStream()

        /// <summary>
        /// Over the real transport the sender check now has a consequence
        /// <see cref="DirectServerLinks"/> could not offer: the stream ends,
        /// and the connection is torn down instead of hanging on as a corpse.
        /// </summary>
        /// <remarks>
        /// <see cref="WebSocketServerLinks.DeliverAsync"/> reports only whether
        /// the frame was written onto an open stream - for a real S2S
        /// connection there is no synchronous "arrived and accepted" per
        /// stanza, that would be XEP-0198 and no property of S2S. The refusal
        /// is therefore observable not at the return value but at the client
        /// never seeing the message and at the next delivery to the same domain
        /// building a new connection, because the old one is dead.
        /// </remarks>
        [Test]
        public async Task SpoofedSender_IsRejectedAndEndsTheStream()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            var refused  = new List<(String Peer, String Reason)>();

            bob.OnMessage                  += (timestamp, sender, m, ct) => { received.Add(m); return Task.CompletedTask; };
            _right.OnRemoteStanzaRejected += (peer, reason) => refused.Add((peer, reason));

            // left.example builds up regularly (the connection therefore
            // identifies itself correctly as "left.example"), but claims in the
            // stanza itself to speak for a third domain.
            await _leftLinks.DeliverAsync(
               "right.example",
                $"<message from='boss@bank.example' to='{bob.BareJid}' type='chat'>" +
                "<body>Please transfer 10000 euros.</body></message>");

            await WaitFor(() => refused.Count > 0, "the turning away by the sender check");

            Assert.That(received, Is.Empty, "The forged message must not reach the client.");

            await Task.Delay(TimeSpan.FromSeconds(1));

            var beforeTheRealOne = _rightLinks.InboundConnectionCount;

            // The stream from before is shut. A real message has to arrive
            // nevertheless - over a newly built connection.
            await alice.SendMessageAsync(bob.BareJid, "Here anyway.");
            await WaitFor(() => received.Count > 0, "the real message after the stream error");

            Assert.Multiple(() =>
            {
                Assert.That(received[0].FromBareJid, Is.EqualTo("alice@left.example"));
                Assert.That(_rightLinks.InboundConnectionCount, Is.GreaterThan(beforeTheRealOne),
                            "After the stream error the next delivery has to build a new connection.");
            });

        }

        #endregion

    }

}
