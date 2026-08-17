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
    /// SASL-EXTERNAL over real sockets: two servers identify themselves with
    /// their certificates instead of asking back by dialback.
    /// </summary>
    /// <remarks>
    /// The visible difference to dialback is the <b>missing</b> second
    /// connection. Dialback needs one call back at the authoritative server per
    /// direction; SASL-EXTERNAL reads the certificate that lay there in the TLS
    /// handshake anyway. That is also what makes it possible to tell from the
    /// outside which procedure took hold - and the first test here rests on
    /// that.
    /// </remarks>
    [TestFixture]
    public class SaslExternalTests
    {

        #region Data

        private XMPPServer _left = null!;
        private XMPPServer _right = null!;
        private TcpServerLinks _leftLinks = null!;
        private TcpServerLinks _rightLinks = null!;
        private readonly List<XMPPClient> _clients = [];
        private readonly InternalErrorGuard _guard = new();

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void TwoServers()
        {

            // The watch on both: an error on the one server often arises from a
            // stanza the other one sent.
            _guard.Reset();

            _left   = _guard.Watched(new XMPPServer("left.example"));
            _right  = _guard.Watched(new XMPPServer("right.example"));

            _left.Start();
            _right.Start();

        }

        [TearDown]
        public async Task CleanUp()
        {

            foreach (var client in _clients)
            {
                try { await client.DisposeAsync(); }
                catch { /* never mind in the teardown */ }
            }

            _clients.Clear();

            if (_leftLinks  is not null) await _leftLinks.DisposeAsync();
            if (_rightLinks is not null) await _rightLinks.DisposeAsync();

            await _left.DisposeAsync();
            await _right.DisposeAsync();

            _guard.AssertClean();

        }

        #endregion

        #region Helper functions

        /// <summary>
        /// Wires both servers together, optionally with SASL-EXTERNAL.
        /// </summary>
        private void Wire(Boolean withExternal)
        {

            TcpServerLinks.Connect(_left, _right,
                                   TcpTlsMode.StartTls,
                                   useSaslExternal: withExternal);

            _leftLinks   = (TcpServerLinks) _left.ServerLinks!;
            _rightLinks  = (TcpServerLinks) _right.ServerLinks!;

        }

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

        #endregion


        #region MessageCrossesTheBoundaryWithoutDialback()

        /// <summary>
        /// The heart of it: the message arrives, and the domain was vouched for
        /// by the certificate instead of by a question back.
        /// </summary>
        /// <remarks>
        /// What is measured is how often the counterpart asked after a dialback
        /// key - the only trace visible from the outside that tells the two
        /// procedures apart. The number of connections is <b>no</b> good for
        /// that: other things cross the boundary too, among them Bob's
        /// automatic delivery receipt, which for its part builds a connection
        /// in the opposite direction. A first version of this test counted
        /// connections and failed on exactly that.
        /// </remarks>
        [Test]
        public async Task MessageCrossesTheBoundaryWithoutDialback()
        {

            Wire(withExternal: true);

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { received.Add(m); return Task.CompletedTask; };

            await alice.SendMessageAsync(bob.BareJid, "Hello by certificate!");

            await WaitFor(() => received.Count > 0, "the message on the other server");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(received[0].Body,        Is.EqualTo("Hello by certificate!"));
                Assert.That(received[0].FromBareJid.ToString(), Is.EqualTo("alice@left.example"));

                Assert.That(_rightLinks.DialbackVerificationCount, Is.Zero,
                            "With SASL-EXTERNAL the counterpart must not ask back.");
                Assert.That(_leftLinks.DialbackVerificationCount, Is.Zero);
            });

        }

        #endregion

        #region WithoutExternal_DialbackCallsBack()

        /// <summary>
        /// The counter-check: without SASL-EXTERNAL comes exactly the question
        /// back that has to stay away above.
        /// </summary>
        /// <remarks>
        /// Without this test the previous one would prove nothing: a zero in
        /// the incoming connections would also be seen if simply nobody ever
        /// called back.
        /// </remarks>
        [Test]
        public async Task WithoutExternal_DialbackCallsBack()
        {

            Wire(withExternal: false);

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { received.Add(m); return Task.CompletedTask; };

            await alice.SendMessageAsync(bob.BareJid, "Hello by dialback!");

            await WaitFor(() => received.Count > 0, "the message on the other server");

            await WaitFor(() => _rightLinks.DialbackVerificationCount > 0,
                           "the dialback question back from right.example");

        }

        #endregion

        #region TheAnswerFindsItsWayBack()

        [Test]
        public async Task TheAnswerFindsItsWayBack()
        {

            Wire(withExternal: true);

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var atBob    = new List<XMPPMessage>();
            var atAlice  = new List<XMPPMessage>();

            bob.OnMessage    += (timestamp, sender, m, ct) => { atBob.Add(m); return Task.CompletedTask; };
            alice.OnMessage  += (timestamp, sender, m, ct) => { atAlice.Add(m); return Task.CompletedTask; };

            await alice.SendMessageAsync(bob.BareJid, "Question");
            await WaitFor(() => atBob.Count > 0, "the question at Bob");

            await bob.SendMessageAsync(atBob[0].FromBareJid, "Answer");
            await WaitFor(() => atAlice.Count > 0, "the answer at Alice");

            Assert.That(atAlice[0].Body, Is.EqualTo("Answer"));

        }

        #endregion

        #region ACertificateForAnotherDomain_GetsNothingThrough()

        /// <summary>
        /// Whoever gives themselves out as <c>left.example</c> but presents a
        /// certificate for <c>impostor.example</c> does not get through.
        /// </summary>
        /// <remarks>
        /// The stream is driven by hand here, and that is necessary: built with
        /// <see cref="TcpServerLinks"/> the attacker would always have a
        /// certificate matching their domain - and then getting through would
        /// be right and no fault. A first version of this test did exactly that
        /// and failed, because it took the permitted behaviour for an attack.
        ///
        /// What is checked is that the transport really has the certificate
        /// check wired up. That the check itself decides rightly stands in
        /// <c>S2SStreamTests.ACertificateThatDoesNotCoverTheDomain_IsRefused</c>
        /// and in <see cref="CertificateIdentityTests"/>.
        /// </remarks>
        [Test]
        public async Task ACertificateForAnotherDomain_GetsNothingThrough()
        {

            Wire(withExternal: true);

            var bob = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { received.Add(m); return Task.CompletedTask; };

            // Only as a supplier of certificates: this server is called
            // impostor.example, and its certificate says so too.
            await using var impostor = _guard.Watched(new XMPPServer("impostor.example"));

            using var client = new TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, _rightLinks.Port);

            var net     = client.GetStream();
            var buffer  = new Byte[8192];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            async Task Raw(String text)
                => await net.WriteAsync(Encoding.UTF8.GetBytes(text), cts.Token);

            async Task<String> RawReadUntil(String what)
            {
                var all = "";
                while (!all.Contains(what, StringComparison.Ordinal))
                {
                    var n = await net.ReadAsync(buffer, cts.Token);
                    if (n <= 0) break;
                    all += Encoding.UTF8.GetString(buffer, 0, n);
                }
                return all;
            }

            const String Header = "<stream:stream xmlns='jabber:server' " +
                                "xmlns:stream='http://etherx.jabber.org/streams' " +
                                "xmlns:db='jabber:server:dialback' " +
                                "from='left.example' to='right.example' version='1.0'>";

            await Raw(Header);
            await RawReadUntil("urn:ietf:params:xml:ns:xmpp-tls");
            await Raw("<starttls xmlns='urn:ietf:params:xml:ns:xmpp-tls'/>");
            await RawReadUntil("proceed");

            await using var tls = new SslStream(net,
                                                leaveInnerStreamOpen: false,
                                                userCertificateValidationCallback: _right.IsOwnCertificate);

            await tls.AuthenticateAsClientAsync(
                      new SslClientAuthenticationOptions {
                          TargetHost          = "right.example",
                          ClientCertificates  = [impostor.Certificate!]
                      },
                      cts.Token);

            var read = new StringBuilder();

            _ = Task.Run(async () =>
            {
                var p2 = new Byte[8192];
                try
                {
                    while (true)
                    {
                        var n = await tls.ReadAsync(p2);
                        if (n <= 0) break;
                        lock (read) read.Append(Encoding.UTF8.GetString(p2, 0, n));
                    }
                }
                catch (Exception) { /* connection closed - expected */ }
            });

            async Task Send(String text)
                => await tls.WriteAsync(Encoding.UTF8.GetBytes(text), cts.Token);

            Boolean Saw(String text)
            {
                lock (read) return read.ToString().Contains(text, StringComparison.Ordinal);
            }

            // After STARTTLS the stream starts from the beginning again - still
            // under the foreign name.
            await Send(Header);

            await WaitFor(() => Saw("EXTERNAL"), "the SASL offer");

            var authzid = Convert.ToBase64String(Encoding.UTF8.GetBytes("left.example"));

            await Send($"<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='EXTERNAL'>{authzid}</auth>");

            await WaitFor(() => Saw("failure") || Saw("success"), "the SASL answer");

            await Send($"<message from='who@left.example' to='{bob.BareJid}' type='chat'>" +
                        "<body>Slipped through?</body></message>");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(Saw("not-authorized"), Is.True,
                            "A certificate for another domain must not be enough.");
                Assert.That(Saw("<success"), Is.False);
                Assert.That(received, Is.Empty,
                            "The stanza must not reach the client.");
            });

        }

        #endregion

    }

}
