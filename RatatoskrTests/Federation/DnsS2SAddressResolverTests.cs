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
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// The DNS resolution itself - against a real DNS server, over real DNS
    /// packets.
    /// </summary>
    /// <remarks>
    /// Hermod brings a DNS server along, so there is no reason to work with a
    /// rebuilt answer here. The difference is not cosmetic: what is checked
    /// thereby is also whether the query name is right
    /// (<c>_xmpp-server._tcp.&lt;domain&gt;</c>), whether the SRV fields
    /// arrive in the right order and whether a domain without an entry really
    /// comes through as "nothing found". A self-built resolver would have
    /// confirmed all of that, because it would have made the same assumptions
    /// as the code it is supposed to check.
    ///
    /// The server listens on the loopback and on a free port; neither
    /// multicast nor TCP are switched on, so that the test run triggers no
    /// firewall query.
    /// </remarks>
    [TestFixture]
    public class DnsS2SAddressResolverTests
    {

        #region Data

        private DNSServer? _dnsServer;
        private DNSClient? _dnsClient;

        #endregion

        #region SetUp / TearDown

        [TearDown]
        public async Task CleanUp()
        {

            if (_dnsServer is not null)
            {
                try { await _dnsServer.Stop(); }
                catch { /* does not matter in the teardown */ }
            }

            _dnsServer = null;
            _dnsClient = null;

        }

        #endregion

        #region Helper functions

        private static Int32 FreePort()
        {

            var l = new UdpClient(0, AddressFamily.InterNetwork);
            var port = ((System.Net.IPEndPoint) l.Client.LocalEndPoint!).Port;
            l.Close();

            return port;

        }

        /// <summary>
        /// Starts an authoritative DNS server with the given entries and
        /// delivers a client that queries it.
        /// </summary>
        private async Task<DnsS2SAddressResolver> ServerWith(params IDNSResourceRecord[] entries)
        {

            var zone = new InMemoryDNSZone();
            zone.Add(entries);

            var port = FreePort();

            _dnsServer = new DNSServer(
                             new AuthoritativeDNSRequestHandler(zone),
                             new DNSServerOptions {
                                 EnableUDPUnicast    = true,
                                 EnableUDPMulticast  = false,
                                 EnableTCPUnicast    = false,
                                 UDPUnicastSocket    = new IPSocket(IPv4Address.Localhost,
                                                                    IPPort.Parse(port))
                             });

            await _dnsServer.Start();

            _dnsClient = new DNSClient(IPv4Address.Localhost,
                                       IPPort.Parse(port),
                                       QueryTimeout:   TimeSpan.FromSeconds(5),
                                       UseQueryCache:  false);

            return new DnsS2SAddressResolver(_dnsClient);

        }

        private static SRV Entry(String  serviceName,
                                 UInt16  priority,
                                 UInt16  weight,
                                 String  target,
                                 UInt16  port)

            // Fully qualified, with a dot at the end: the zone looks up over
            // the name from the question, and that is how it arrives from the
            // client.
            => new (DNSServiceName.Parse(serviceName.TrimEnd('.') + "."),
                    DNSQueryClasses.IN,
                    TimeSpan.FromMinutes(5),
                    priority,
                    weight,
                    IPPort.Parse(port),
                    DomainName.Parse(target == "." ? "." : target.TrimEnd('.') + "."));

        #endregion


        #region ASrvRecord_IsFoundAndTranslated()

        /// <summary>
        /// The normal case: an SRV record is found, and all four fields arrive
        /// correctly.
        /// </summary>
        [Test]
        public async Task ASrvRecord_IsFoundAndTranslated()
        {

            var resolver = await ServerWith(
                                Entry("_xmpp-server._tcp.right.example", 10, 20, "im.right.example", 5269));

            var targets = await resolver.ResolveAsync("right.example");

            Assert.Multiple(() =>
            {
                Assert.That(targets,             Has.Count.EqualTo(1));
                Assert.That(targets[0].Host,     Is.EqualTo("im.right.example"));
                Assert.That(targets[0].Port,     Is.EqualTo(5269));
                Assert.That(targets[0].Priority, Is.EqualTo(10));
                Assert.That(targets[0].Weight,   Is.EqualTo(20));
            });

        }

        #endregion

        #region ThePortComesFromTheRecord_NotFromTheDefault()

        /// <summary>
        /// The port comes out of the record - otherwise half the statement of
        /// an SRV record would be given away.
        /// </summary>
        [Test]
        public async Task ThePortComesFromTheRecord_NotFromTheDefault()
        {

            var resolver = await ServerWith(
                                Entry("_xmpp-server._tcp.right.example", 0, 0, "im.right.example", 15269));

            var targets = await resolver.ResolveAsync("right.example");

            Assert.That(targets[0].Port, Is.EqualTo(15269));

        }

        #endregion

        #region TheQueryUsesTheServicePrefix()

        /// <summary>
        /// What is asked for is <c>_xmpp-server._tcp.&lt;domain&gt;</c> and not
        /// the domain itself.
        /// </summary>
        /// <remarks>
        /// The entry lies under a different service name here. Were the
        /// resolver to ask wrongly, it would find it - and the test would
        /// notice.
        /// </remarks>
        [Test]
        public async Task TheQueryUsesTheServicePrefix()
        {

            var resolver = await ServerWith(
                                Entry("_xmpp-client._tcp.right.example", 0, 0, "wrong.right.example", 5222));

            var targets = await resolver.ResolveAsync("right.example");

            Assert.Multiple(() =>
            {
                Assert.That(targets, Has.Count.EqualTo(1));
                Assert.That(targets[0].Host, Is.EqualTo("right.example"),
                            "Without a matching SRV record the fallback to the domain itself holds.");
                Assert.That(targets[0].Host, Is.Not.EqualTo("wrong.right.example"));
            });

        }

        #endregion

        #region SeveralRecords_ComeBackInPriorityOrder()

        /// <summary>
        /// Several entries come back in the order in which they are to be
        /// tried.
        /// </summary>
        [Test]
        public async Task SeveralRecords_ComeBackInPriorityOrder()
        {

            var resolver = await ServerWith(
                                Entry("_xmpp-server._tcp.right.example", 30, 0, "third.example",  5269),
                               Entry("_xmpp-server._tcp.right.example", 10, 0, "first.example",  5269),
                               Entry("_xmpp-server._tcp.right.example", 20, 0, "second.example", 5269));

            var targets = await resolver.ResolveAsync("right.example");

            Assert.That(targets.Select(t => t.Host),
                        Is.EqualTo(new[] { "first.example", "second.example", "third.example" }));

        }

        #endregion

        #region WithoutAnyRecord_TheDomainItselfIsTried()

        /// <summary>
        /// Without an SRV record the fallback from RFC 6120, section 3.2.1
        /// holds: the domain itself on port 5269.
        /// </summary>
        [Test]
        public async Task WithoutAnyRecord_TheDomainItselfIsTried()
        {

            var resolver = await ServerWith(
                                Entry("_xmpp-server._tcp.elsewhere.example", 0, 0, "whatever.example", 5269));

            var targets = await resolver.ResolveAsync("right.example");

            Assert.Multiple(() =>
            {
                Assert.That(targets,         Has.Count.EqualTo(1));
                Assert.That(targets[0].Host, Is.EqualTo("right.example"));
                Assert.That(targets[0].Port, Is.EqualTo(5269));
            });

        }

        #endregion

        #region WithTheFallbackTurnedOff_NothingIsTried()

        /// <summary>
        /// Whoever switches the fallback off gets nothing without an SRV
        /// record.
        /// </summary>
        /// <remarks>
        /// Meant for operators who want to permit targets published over SRV
        /// exclusively - otherwise a connection would silently be made
        /// somewhere else.
        /// </remarks>
        [Test]
        public async Task WithTheFallbackTurnedOff_NothingIsTried()
        {

            await ServerWith(
                 Entry("_xmpp-server._tcp.elsewhere.example", 0, 0, "whatever.example", 5269));

            var strict = new DnsS2SAddressResolver(_dnsClient!) { FallBackToDomain = false };

            Assert.That(await strict.ResolveAsync("right.example"), Is.Empty);

        }

        #endregion

        #region ADotTarget_MeansTheServiceIsNotOffered()

        /// <summary>
        /// A target of "." is a refusal and no missing entry - the fallback
        /// does <b>not</b> take hold then.
        /// </summary>
        /// <remarks>
        /// The difference is the whole point of this spelling. Whoever reads it
        /// as silence connects to a domain that has expressly said that it does
        /// not offer the service.
        /// </remarks>
        [Test]
        public async Task ADotTarget_MeansTheServiceIsNotOffered()
        {

            var resolver = await ServerWith(
                                Entry("_xmpp-server._tcp.right.example", 0, 0, ".", 0));

            Assert.That(await resolver.ResolveAsync("right.example"), Is.Empty);

        }

        #endregion

        #region AnUnreachableDnsServer_YieldsTheFallback()

        /// <summary>
        /// If no DNS server answers at all, the fallback to the domain stays.
        /// </summary>
        /// <remarks>
        /// An answer that fails to come is not the same as a refusal, and it
        /// must certainly not become an exception - the caller would otherwise
        /// fail differently depending on the state of the net.
        /// </remarks>
        [Test]
        public async Task AnUnreachableDnsServer_YieldsTheFallback()
        {

            var deadTarget = new DNSClient(IPv4Address.Localhost,
                                           IPPort.Parse(FreePort()),
                                          QueryTimeout:   TimeSpan.FromSeconds(1),
                                          UseQueryCache:  false);

            var resolver = new DnsS2SAddressResolver(deadTarget)
                           {
                               Timeout = TimeSpan.FromSeconds(1)
                           };

            var targets = await resolver.ResolveAsync("right.example");

            Assert.Multiple(() =>
            {
                Assert.That(targets,         Has.Count.EqualTo(1));
                Assert.That(targets[0].Host, Is.EqualTo("right.example"));
            });

        }

        #endregion

    }

}
