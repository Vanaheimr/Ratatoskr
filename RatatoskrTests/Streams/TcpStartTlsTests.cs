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

using System.Net;
using System.Net.Sockets;
using System.Text;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    // As in XMPPServer.cs: Hermod brings a type IPAddress of its own along.
    // The alias has to stand inside the namespace declaration.
    using IPAddress = System.Net.IPAddress;

    /// <summary>
    /// The negotiation itself (RFC 6120, section 5.4) - not that a message
    /// arrives, but that under the wrong circumstances it does <b>not</b>
    /// arrive.
    /// </summary>
    /// <remarks>
    /// This file is the answer to a mutation run in which four out of five
    /// interventions in the negotiation stayed green. The reason was the same
    /// every time: the federation tests play both sides correctly, and as long
    /// as both keep to the rules it makes no difference whether one side also
    /// <i>checks</i> them. A rule is only checked by a counterpart that breaks
    /// it - and that one has to be built on purpose.
    /// </remarks>
    [TestFixture]
    public class TcpStartTlsTests
    {

        #region Data

        private XMPPServer _server = null!;
        private readonly List<IAsyncDisposable> _toDispose = [];
        private readonly InternalErrorGuard _guard = new();

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void OneServer()
        {
            _guard.Reset();

            _server = _guard.Watched(new XMPPServer("left.example"));
            _server.Start();
        }

        [TearDown]
        public async Task CleanUp()
        {

            foreach (var d in _toDispose)
            {
                try { await d.DisposeAsync(); }
                catch { /* never mind in the teardown */ }
            }

            _toDispose.Clear();

            await _server.DisposeAsync();

            _guard.AssertClean();

        }

        #endregion

        #region Helper functions

        /// <summary>
        /// A server that answers from a script - for counterparts that do not
        /// keep to RFC 6120.
        /// </summary>
        private sealed class ScriptedServer : IAsyncDisposable
        {

            private readonly TcpListener             _listener;
            private readonly CancellationTokenSource _cts = new();

            public Int32 Port { get; }

            public ScriptedServer(Func<NetworkStream, CancellationToken, Task> script)
            {

                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();

                Port = ((IPEndPoint) _listener.LocalEndpoint).Port;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!_cts.IsCancellationRequested)
                        {
                            var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                            _ = Task.Run(async () =>
                            {
                                try { await script(client.GetStream(), _cts.Token); }
                                catch (Exception) { /* never mind */ }
                                finally { try { client.Dispose(); } catch { } }
                            });
                        }
                    }
                    catch (Exception) { /* ended */ }
                });

            }

            public async ValueTask DisposeAsync()
            {
                await _cts.CancelAsync();
                try { _listener.Stop(); } catch { }
                _cts.Dispose();
            }

        }

        private static async Task Write(NetworkStream net, String text)
            => await net.WriteAsync(Encoding.UTF8.GetBytes(text));

        private static async Task<String> ReadUntil(NetworkStream      net,
                                                  Func<String, Boolean>  finished,
                                                  CancellationToken  ct)
        {

            var buffer  = new Byte[8192];
            var all   = "";

            while (!finished(all))
            {

                var n = await net.ReadAsync(buffer, ct);

                if (n <= 0)
                    break;

                all += Encoding.UTF8.GetString(buffer, 0, n);

            }

            return all;

        }

        /// <summary>
        /// Wires the server to a scripted counterpart.
        /// </summary>
        private TcpServerLinks LinksTo(ScriptedServer peer)
        {

            var links = new TcpServerLinks(_server, mode: TcpTlsMode.StartTls);
            _toDispose.Add(links);

            links.AddPeer("foreign.example",
                          IPAddress.Loopback.ToString(),
                          peer.Port,
                          TcpTlsMode.StartTls,
                          validator: (_, _, _, _) => true);

            return links;

        }

        /// <summary>
        /// Records everything that arrives from now on - until the connection
        /// ends.
        /// </summary>
        /// <remarks>
        /// That is the difference between "the delivery failed" and "the client
        /// stopped talking". Only the second vouches for its having checked the
        /// rule: the delivery would fail just as well if it simply ran into the
        /// time limit.
        /// </remarks>
        private static async Task RecordEverything(NetworkStream      net,
                                               List<Byte>         target,
                                               CancellationToken  ct)
        {

            var buffer = new Byte[4096];

            while (true)
            {

                var n = await net.ReadAsync(buffer, ct);

                if (n <= 0)
                    break;

                lock (target)
                    target.AddRange(buffer[..n]);

            }

        }

        private const String Stanza =
            "<message from='alice@left.example' to='bob@foreign.example'><body>hello</body></message>";

        #endregion


        #region APeerThatDoesNotOfferStartTls_IsNotUsed()

        /// <summary>
        /// A counterpart without STARTTLS in its offer gets nothing - least of
        /// all in the clear.
        /// </summary>
        /// <remarks>
        /// Without this check the negotiation would be a request instead of a
        /// condition: a man in the middle would only have to strike the offer
        /// out of the features, and the stream would carry on unencrypted. That
        /// is exactly the classic downgrade attack on STARTTLS.
        /// </remarks>
        [Test]
        public async Task APeerThatDoesNotOfferStartTls_IsNotUsed()
        {

            var afterTheOffer = new List<Byte>();

            await using var peer = new ScriptedServer(async (net, ct) =>
            {
                await ReadUntil(net, t => t.Contains("<stream:stream", StringComparison.Ordinal), ct);
                await Write(net,
                    "<stream:stream xmlns='jabber:server' " +
                    "xmlns:stream='http://etherx.jabber.org/streams' " +
                    "from='foreign.example' to='left.example' id='x' version='1.0'>");
                await Write(net,
                    "<stream:features xmlns:stream='http://etherx.jabber.org/streams'/>");

                await RecordEverything(net, afterTheOffer, ct);
            });

            var links = LinksTo(peer);

            var delivered = await links.DeliverAsync("foreign.example", Stanza)
                                        .WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Multiple(() =>
            {
                Assert.That(delivered, Is.False,
                            "Without an offer of STARTTLS no stanza may go out.");

                // The real proof: the client stops talking instead of merely
                // running into a time limit.
                lock (afterTheOffer)
                    Assert.That(afterTheOffer, Is.Empty,
                                "After an offer of STARTTLS fails to come, nothing more may be sent.");
            });

        }

        #endregion

        #region AFailureInsteadOfProceed_AbortsTheHandshake()

        /// <summary>
        /// If the counterpart answers <c>&lt;starttls/&gt;</c> with
        /// <c>&lt;failure/&gt;</c>, the setup ends (RFC 6120, section 5.4.2.2).
        /// </summary>
        [Test]
        public async Task AFailureInsteadOfProceed_AbortsTheHandshake()
        {

            await using var peer = new ScriptedServer(async (net, ct) =>
            {
                await ReadUntil(net, t => t.Contains("<stream:stream", StringComparison.Ordinal), ct);
                await Write(net,
                    "<stream:stream xmlns='jabber:server' " +
                    "xmlns:stream='http://etherx.jabber.org/streams' " +
                    "from='foreign.example' to='left.example' id='x' version='1.0'>");
                await Write(net,
                    "<stream:features xmlns:stream='http://etherx.jabber.org/streams'>" +
                    "<starttls xmlns='urn:ietf:params:xml:ns:xmpp-tls'><required/></starttls>" +
                    "</stream:features>");

                await ReadUntil(net, t => t.Contains("<starttls", StringComparison.Ordinal), ct);
                await Write(net, "<failure xmlns='urn:ietf:params:xml:ns:xmpp-tls'/>");

                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            });

            var links = LinksTo(peer);

            var delivered = await links.DeliverAsync("foreign.example", Stanza)
                                        .WaitAsync(TimeSpan.FromSeconds(20));

            Assert.That(delivered, Is.False,
                        "After a <failure/> nothing may go out.");

        }

        #endregion

        #region SomethingOtherThanProceed_IsNotTakenAsProceed()

        /// <summary>
        /// And the sharper version: just any answer is no consent.
        /// </summary>
        /// <remarks>
        /// Without this test the previous one would pass even if the client
        /// only checked <i>that</i> an answer came. Here one comes, it just is
        /// not called <c>&lt;proceed/&gt;</c>.
        /// </remarks>
        [Test]
        public async Task SomethingOtherThanProceed_IsNotTakenAsProceed()
        {

            var afterTheAnswer = new List<Byte>();

            await using var peer = new ScriptedServer(async (net, ct) =>
            {
                await ReadUntil(net, t => t.Contains("<stream:stream", StringComparison.Ordinal), ct);
                await Write(net,
                    "<stream:stream xmlns='jabber:server' " +
                    "xmlns:stream='http://etherx.jabber.org/streams' " +
                    "from='foreign.example' to='left.example' id='x' version='1.0'>");
                await Write(net,
                    "<stream:features xmlns:stream='http://etherx.jabber.org/streams'>" +
                    "<starttls xmlns='urn:ietf:params:xml:ns:xmpp-tls'><required/></starttls>" +
                    "</stream:features>");

                await ReadUntil(net, t => t.Contains("<starttls", StringComparison.Ordinal), ct);

                // An answer, but not the one demanded.
                await Write(net, "<anything xmlns='urn:ietf:params:xml:ns:xmpp-tls'/>");

                await RecordEverything(net, afterTheAnswer, ct);
            });

            var links = LinksTo(peer);

            var delivered = await links.DeliverAsync("foreign.example", Stanza)
                                        .WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Multiple(() =>
            {
                Assert.That(delivered, Is.False);

                // If the client took the answer for consent, a TLS ClientHello
                // would come now.
                lock (afterTheAnswer)
                    Assert.That(afterTheAnswer, Is.Empty,
                                "Without a <proceed/> the client must not start with TLS.");
            });

        }

        #endregion

        #region PipelinedPlaintextAfterStartTls_GetsNoProceed()

        /// <summary>
        /// Whoever pushes plaintext in behind the <c>&lt;starttls/&gt;</c> gets
        /// no consent.
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 5.4.3.3: after the <c>&lt;starttls/&gt;</c>
        /// nothing more may follow in the clear. If something does stand in the
        /// buffer, it is either a broken counterpart or an attempt to smuggle
        /// plaintext into the stream that is about to be encrypted - either way
        /// a reason to stop.
        /// </remarks>
        [Test]
        public async Task PipelinedPlaintextAfterStartTls_GetsNoProceed()
        {

            await using var links = new TcpServerLinks(_server, mode: TcpTlsMode.StartTls);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, links.Port);

            var net = client.GetStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            await Write(net,
                "<stream:stream xmlns='jabber:server' " +
                "xmlns:stream='http://etherx.jabber.org/streams' " +
                "from='foreign.example' to='left.example' version='1.0'>");

            await ReadUntil(net,
                          t => t.Contains("urn:ietf:params:xml:ns:xmpp-tls", StringComparison.Ordinal),
                          cts.Token);

            // <starttls/> and a stanza in one single write.
            await Write(net,
                "<starttls xmlns='urn:ietf:params:xml:ns:xmpp-tls'/>" +
                "<message from='alice@foreign.example' to='bob@left.example'><body>x</body></message>");

            var reply = await ReadUntil(net, t => t.Length > 0, cts.Token)
                              .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(reply, Does.Not.Contain("proceed"),
                        "Plaintext sent ahead has to end the setup.");

        }

        #endregion

        #region SomethingOtherThanStartTls_GetsFailureAndNoStream()

        /// <summary>
        /// A stanza straight away instead of <c>&lt;starttls/&gt;</c>: that
        /// gives a <c>&lt;failure/&gt;</c> and no stream.
        /// </summary>
        /// <remarks>
        /// The counter-check to the negotiation being a condition. A server that
        /// carried on here would have made the encryption a courtesy.
        /// </remarks>
        [Test]
        public async Task SomethingOtherThanStartTls_GetsFailureAndNoStream()
        {

            await using var links = new TcpServerLinks(_server, mode: TcpTlsMode.StartTls);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, links.Port);

            var net = client.GetStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            await Write(net,
                "<stream:stream xmlns='jabber:server' " +
                "xmlns:stream='http://etherx.jabber.org/streams' " +
                "from='foreign.example' to='left.example' version='1.0'>");

            var greeting = await ReadUntil(net,
                                            t => t.Contains("urn:ietf:params:xml:ns:xmpp-tls", StringComparison.Ordinal),
                                            cts.Token);

            Assert.That(greeting, Does.Contain("<required/>"),
                        "STARTTLS has to be announced as required.");

            await Write(net,
                "<message from='alice@foreign.example' to='bob@left.example'><body>x</body></message>");

            var reply = await ReadUntil(net,
                                        t => t.Contains("failure", StringComparison.Ordinal),
                                        cts.Token)
                              .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("failure"),
                            "Anything other than a <starttls/> deserves a <failure/>.");
                Assert.That(reply, Does.Not.Contain("proceed"));
            });

        }

        #endregion

    }

}
