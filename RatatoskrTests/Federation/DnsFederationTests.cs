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

using System.Net.Sockets;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// Federation as it looks in operation: no far end is entered by hand,
    /// both servers find each other exclusively over DNS.
    /// </summary>
    /// <remarks>
    /// The difference to <see cref="SrvResolutionTests"/> is not only the
    /// amount. There one side resolves and the other one gets the way back
    /// entered; here there is no list at all any more - the <b>dialback
    /// query</b> has to find its way over the resolution too. That is
    /// precisely the case XEP-0220 means, and at the same time the one in
    /// which the root of trust wanders from the operator to the DNS.
    ///
    /// The SRV targets are IP addresses. That is necessary in the test,
    /// because an invented machine name could not be resolved, and it checks
    /// along the way that the certificates are checked against the <i>domain
    /// that was looked for</i> and not against what stands in the SRV record -
    /// otherwise neither of the two connections would come about.
    /// </remarks>
    [TestFixture]
    public class DnsFederationTests
    {

        #region Data

        private DNSServer _dnsServer = null!;
        private XMPPServer _left = null!;
        private XMPPServer _right = null!;
        private TcpServerLinks _leftLinks = null!;
        private TcpServerLinks _rightLinks = null!;
        private readonly List<XMPPClient> _clients = [];
        private readonly InternalErrorGuard _guard = new();

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task TwoServersAndOneDNS()
        {

            // The guard on both: An error on the one server often comes about
            // through a stanza the other one sent.
            _guard.Reset();

            _left   = _guard.Watched(new XMPPServer("left.example"));
            _right  = _guard.Watched(new XMPPServer("right.example"));

            _left.Start();
            _right.Start();

            // The ports are chosen beforehand, because otherwise the cat bites
            // its own tail: the zone file needs the ports, and the S2S branches
            // need the resolver, which cannot answer without a zone. The same
            // procedure as everywhere else in the project where a free port is
            // needed.
            var portLeft   = FreeTcpPort();
            var portRight  = FreeTcpPort();

            var zone = new InMemoryDNSZone();

            zone.Add(SrvEntry("_xmpp-server._tcp.left.example.",  portLeft),
                     SrvEntry("_xmpp-server._tcp.right.example.", portRight));

            var dnsPort = FreeUdpPort();

            _dnsServer = new DNSServer(
                             new AuthoritativeDNSRequestHandler(zone),
                             new DNSServerOptions {
                                 EnableUDPUnicast    = true,
                                 EnableUDPMulticast  = false,
                                 EnableTCPUnicast    = false,
                                 UDPUnicastSocket    = new IPSocket(IPv4Address.Localhost,
                                                                    IPPort.Parse(dnsPort))
                             });

            await _dnsServer.Start();

            var dnsClient = new DNSClient(IPv4Address.Localhost,
                                          IPPort.Parse(dnsPort),
                                          QueryTimeout:   TimeSpan.FromSeconds(5),
                                          UseQueryCache:  false);

            // Both sides get the same resolver and not a single far end by
            // hand.
            _leftLinks = new TcpServerLinks(_left, portLeft, TcpTlsMode.StartTls)
                          {
                              AddressResolver       = new DnsS2SAddressResolver(dnsClient),
                              DefaultPeerValidator  = _right.IsOwnCertificate
                          };

            _rightLinks = new TcpServerLinks(_right, portRight, TcpTlsMode.StartTls)
                           {
                               AddressResolver       = new DnsS2SAddressResolver(dnsClient),
                               DefaultPeerValidator  = _left.IsOwnCertificate
                           };

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

            try { await _dnsServer.Stop(); }
            catch { /* does not matter in the teardown */ }

            await _left.DisposeAsync();
            await _right.DisposeAsync();

            _guard.AssertClean();

        }

        #endregion

        #region Helper functions

        private static Int32 FreeUdpPort()
        {

            var l     = new UdpClient(0, AddressFamily.InterNetwork);
            var port  = ((System.Net.IPEndPoint) l.Client.LocalEndPoint!).Port;
            l.Close();

            return port;

        }

        private static Int32 FreeTcpPort()
        {

            var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            var port = ((System.Net.IPEndPoint) l.LocalEndpoint).Port;
            l.Stop();

            return port;

        }

        private static SRV SrvEntry(String serviceName, Int32 port)

            => new (DNSServiceName.Parse(serviceName),
                    DNSQueryClasses.IN,
                    TimeSpan.FromMinutes(5),
                    0, 0,
                    IPPort.Parse((UInt16) port),
                    DomainName.Parse(IPv4Address.Localhost.ToString()));

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


        #region TwoServersFindEachOtherThroughDnsAlone()

        /// <summary>
        /// A message across the border, without an address being deposited
        /// anywhere.
        /// </summary>
        /// <remarks>
        /// That includes the dialback query: <c>right.example</c> has to
        /// resolve <c>left.example</c> itself in order to be able to check the
        /// key it was presented with.
        /// </remarks>
        [Test]
        public async Task TwoServersFindEachOtherThroughDnsAlone()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { received.Add(m); return Task.CompletedTask; };

            await alice.SendMessageAsync(bob.BareJid, "Hello over DNS!");

            await WaitFor(() => received.Count > 0, "the message on the other server");

            Assert.Multiple(() =>
            {
                Assert.That(received[0].Body,        Is.EqualTo("Hello over DNS!"));
                Assert.That(received[0].FromBareJid.ToString(), Is.EqualTo("alice@left.example"));

                Assert.That(_rightLinks.DialbackVerificationCount, Is.GreaterThan(0),
                            "The query must have taken place - and have found its way over DNS.");
            });

        }

        #endregion

        #region TheAnswerFindsItsWayBack()

        [Test]
        public async Task TheAnswerFindsItsWayBack()
        {

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

        #region ADomainWithoutRecords_YieldsAnError()

        /// <summary>
        /// A domain that does not stand in the DNS leads to the
        /// <c>&lt;remote-server-not-found/&gt;</c>.
        /// </summary>
        /// <remarks>
        /// The fallback from RFC 6120 §3.2.1 does take hold and tries the
        /// domain itself - but that one does not exist, and then it stays with
        /// the error.
        /// </remarks>
        [Test]
        public async Task ADomainWithoutRecords_YieldsAnError()
        {

            var alice   = await ConnectAsync(_left, "alice");
            var errors  = new List<StanzaError>();

            alice.OnStanzaError += (timestamp, sender, _, e, ct) => { errors.Add(e); return Task.CompletedTask; };

            await alice.SendMessageAsync(JID.Parse("who@nobody.example"), "Hello?");

            await WaitFor(() => errors.Count > 0, "the error for the unknown domain");

            Assert.That(errors[0].Condition, Is.EqualTo("remote-server-not-found"));

        }

        #endregion

    }

}
