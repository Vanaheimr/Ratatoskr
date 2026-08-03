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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// Finding far ends over the resolution instead of over the list
    /// (RFC 6120, section 3.2).
    /// </summary>
    /// <remarks>
    /// The resolver is put in here and not queried. A test using real DNS
    /// checks the world instead of the code: it hangs on a net connection, on
    /// foreign zone files and on answer times, and when it turns red, nobody
    /// knows what it was.
    /// </remarks>
    [TestFixture]
    public class SrvResolutionTests
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

            // The guard on both: An error on the one server often comes about
            // through a stanza the other one sent.
            _guard.Reset();

            _left   = _guard.Watched(new XMPPServer("left.example"));
            _right  = _guard.Watched(new XMPPServer("right.example"));

            _left.Start();
            _right.Start();

            // Only the recipient side is wired up; the sender side is supposed
            // to resolve its address.
            _rightLinks = new TcpServerLinks(_right, mode: TcpTlsMode.StartTls);

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

            if (_leftLinks  is not null) await _leftLinks.DisposeAsync();
            if (_rightLinks is not null) await _rightLinks.DisposeAsync();

            await _left.DisposeAsync();
            await _right.DisposeAsync();

            _guard.AssertClean();

        }

        #endregion

        #region Helper functions

        /// <summary>A resolver giving a fixed answer and counting along.</summary>
        private sealed class FixedResolver : IS2SAddressResolver
        {

            private readonly Func<String, IReadOnlyList<SrvTarget>> _reply;

            public List<String> Asked { get; } = [];

            public FixedResolver(Func<String, IReadOnlyList<SrvTarget>> reply)
            {
                _reply = reply;
            }

            public Task<IReadOnlyList<SrvTarget>> ResolveAsync(String             domain,
                                                               CancellationToken  cancellationToken = default)
            {
                lock (Asked) Asked.Add(domain);
                return Task.FromResult(_reply(domain));
            }

        }

        /// <summary>Wires the sender side up over a resolver.</summary>
        private FixedResolver SenderWithResolver(Func<String, IReadOnlyList<SrvTarget>> reply)
        {

            var resolver = new FixedResolver(reply);

            _leftLinks = new TcpServerLinks(_left, mode: TcpTlsMode.StartTls)
                          {
                              AddressResolver       = resolver,
                              DefaultPeerValidator  = _right.IsOwnCertificate
                          };

            AddTheWayBack();

            return resolver;

        }

        /// <summary>
        /// Enters the way back at the recipient side.
        /// </summary>
        /// <remarks>
        /// Not part of what is checked here, but necessary: the dialback query
        /// goes out from <c>right.example</c>, and without an address for
        /// <c>left.example</c> it could ask nobody. In the remaining federation
        /// tests <c>TcpServerLinks.Connect</c> takes care of that for both
        /// directions; here only one side is wired up over the resolver, so the
        /// other one has to be pulled along by hand.
        /// </remarks>
        private void AddTheWayBack()

            => _rightLinks.AddPeer("left.example",
                                   System.Net.IPAddress.Loopback.ToString(),
                                    _leftLinks.Port,
                                    TcpTlsMode.StartTls,
                                    validator: _left.IsOwnCertificate);

        private SrvTarget TargetOnTheRight(UInt16 priority = 0, UInt16 weight = 0)
            => new (priority, weight, System.Net.IPAddress.Loopback.ToString(), _rightLinks.Port);

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

        private const String Stanza =
            "<message from='alice@left.example' to='bob@right.example'><body>resolved</body></message>";

        #endregion


        #region AResolvedTarget_IsUsed()

        /// <summary>
        /// A domain without an entry by hand is resolved and reached.
        /// </summary>
        [Test]
        public async Task AResolvedTarget_IsUsed()
        {

            var resolver = SenderWithResolver(_ => [TargetOnTheRight()]);

            var bob = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += m => received.Add(m);

            var delivered = await _leftLinks.DeliverAsync("right.example", Stanza)
                                              .WaitAsync(TimeSpan.FromSeconds(25));

            await WaitFor(() => received.Count > 0, "the message over the resolved target");

            Assert.Multiple(() =>
            {
                Assert.That(delivered, Is.True);
                Assert.That(resolver.Asked, Is.EquivalentTo(new[] { "right.example" }));
            });

        }

        #endregion

        #region AManualEntry_WinsOverTheResolver()

        /// <summary>
        /// An entry by hand takes precedence - the resolver is not even asked.
        /// </summary>
        /// <remarks>
        /// A decision of the operator weighs more than a piece of information
        /// out of the net. Without this precedence a deposited address could be
        /// bypassed through a forged DNS answer.
        /// </remarks>
        [Test]
        public async Task AManualEntry_WinsOverTheResolver()
        {

            var resolver = SenderWithResolver(_ => throw new InvalidOperationException(
                                                       "The resolver must not be asked here."));

            _leftLinks.AddPeer("right.example",
                               System.Net.IPAddress.Loopback.ToString(),
                                _rightLinks.Port,
                                TcpTlsMode.StartTls,
                                validator: _right.IsOwnCertificate);

            var bob = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += m => received.Add(m);

            await _leftLinks.DeliverAsync("right.example", Stanza)
                             .WaitAsync(TimeSpan.FromSeconds(25));

            await WaitFor(() => received.Count > 0, "the message over the entry by hand");

            Assert.That(resolver.Asked, Is.Empty);

        }

        #endregion

        #region AnUnreachableFirstTarget_FallsThroughToTheNext()

        /// <summary>
        /// If the first target is not reachable, the next one is tried.
        /// </summary>
        /// <remarks>
        /// SRV records name fallback machines. To list them and then dial only
        /// the first one would be half an implementation - and one that stands
        /// out only when a machine fails.
        /// </remarks>
        [Test]
        public async Task AnUnreachableFirstTarget_FallsThroughToTheNext()
        {

            // A surely dead port first, the real target afterwards.
            var deadTarget = new SrvTarget(10, 0, System.Net.IPAddress.Loopback.ToString(), 1);

            SenderWithResolver(_ => [deadTarget, TargetOnTheRight(priority: 20)]);

            var bob = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += m => received.Add(m);

            var delivered = await _leftLinks.DeliverAsync("right.example", Stanza)
                                              .WaitAsync(TimeSpan.FromSeconds(30));

            await WaitFor(() => received.Count > 0, "the message over the second target");

            Assert.That(delivered, Is.True);

        }

        #endregion

        #region ADomainThatResolvesToNothing_YieldsAnError()

        /// <summary>
        /// If a domain resolves to nothing, it stays with the
        /// <c>&lt;remote-server-not-found/&gt;</c>.
        /// </summary>
        [Test]
        public async Task ADomainThatResolvesToNothing_YieldsAnError()
        {

            SenderWithResolver(_ => []);

            var alice   = await ConnectAsync(_left, "alice");
            var errors  = new List<StanzaError>();

            alice.OnStanzaError += (_, e) => errors.Add(e);

            await alice.SendMessageAsync("nobody.example", "Hello?");

            await WaitFor(() => errors.Count > 0, "the error for the unresolvable domain");

            Assert.That(errors[0].Condition, Is.EqualTo("remote-server-not-found"));

        }

        #endregion

        #region TheCertificateIsCheckedAgainstTheDomain_NotTheSrvTarget()

        /// <summary>
        /// What is checked against is the <b>domain that was looked for</b>,
        /// not the machine name from the SRV record (RFC 6120,
        /// section 13.7.2.1).
        /// </summary>
        /// <remarks>
        /// The most important test of this file. Without DNSSEC an SRV record
        /// is not attested; whoever can forge it dictates the target. Were the
        /// certificate check to follow the target, the attacker would bring
        /// along the very yardstick they are measured by.
        ///
        /// What that is checked by here is that the entry in the SRV record is
        /// a bare IP address, which stands in no certificate as a domain. The
        /// connection succeeds nevertheless - so the yardstick does not come
        /// from there. The counter-check stands in
        /// <see cref="SaslExternalTests"/>: a certificate not covering the
        /// domain that was looked for does not get through.
        /// </remarks>
        [Test]
        public async Task TheCertificateIsCheckedAgainstTheDomain_NotTheSrvTarget()
        {

            var checkedNames = new List<String>();

            var resolver = new FixedResolver(_ => [TargetOnTheRight()]);

            _leftLinks = new TcpServerLinks(_left, mode: TcpTlsMode.StartTls)
                          {
                              AddressResolver       = resolver,
                              DefaultPeerValidator  = (sender, cert, chain, errors) =>
                                                      {
                                                          if (sender is System.Net.Security.SslStream s)
                                                              lock (checkedNames)
                                                                  checkedNames.Add(s.TargetHostName);

                                                          return _right.IsOwnCertificate(sender, cert, chain, errors);
                                                      }
                          };

            AddTheWayBack();

            var bob = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += m => received.Add(m);

            await _leftLinks.DeliverAsync("right.example", Stanza)
                             .WaitAsync(TimeSpan.FromSeconds(25));

            await WaitFor(() => received.Count > 0, "the message");

            lock (checkedNames)
                Assert.That(checkedNames, Has.All.EqualTo("right.example"),
                            "TLS has to run against the domain that was looked for, not against the SRV target.");

        }

        #endregion

    }

}
