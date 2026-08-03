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

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// RFC 6120, section 5: XMPP runs over TLS.
    ///
    /// The test server spoke <c>ws://</c>, so the passwords of the SASL PLAIN
    /// login went over the wire in the clear. Everything else in this project
    /// was academic while that was so.
    ///
    /// These tests check two things, and the second is the harder one: that a
    /// connection comes about, and that it does so for the right reason. A TLS
    /// check that waves everything through can only be told from one that
    /// recognises the right certificate by the counter-checks.
    /// </summary>
    [TestFixture]
    public class TlsTests : AXMPPTests
    {

        #region ServerUri_IsWss()

        /// <summary>
        /// The way in: the server offers <c>wss://</c>, not <c>ws://</c>.
        /// </summary>
        [Test]
        public void ServerUri_IsWss()
        {

            Assert.Multiple(() =>
            {
                Assert.That(Server.Uri, Does.StartWith("wss://"),
                            "The server has to be reachable over TLS.");

                Assert.That(Server.Certificate, Is.Not.Null,
                            "Without a certificate there is no TLS.");
            });

        }

        #endregion

        #region Client_ConnectsOverTls()

        /// <summary>
        /// A client connects over TLS and can log in - the whole negotiation
        /// then runs encrypted.
        /// </summary>
        [Test]
        public async Task Client_ConnectsOverTls()
        {

            var client = await ConnectClientAsync();

            Assert.Multiple(() =>
            {
                Assert.That(client.IsConnected, Is.True);
                Assert.That(client.Connection.WebSocketUri, Does.StartWith("wss://"));
            });

        }

        #endregion

        #region RejectedCertificate_PreventsTheConnection()

        /// <summary>
        /// The first counter-check: if the client turns the certificate away,
        /// no connection comes about.
        ///
        /// Without this test the previous one would pass even if no TLS were
        /// involved at all - a certificate check that is never called can
        /// prevent nothing.
        /// </summary>
        [Test]
        public async Task RejectedCertificate_PreventsTheConnection()
        {

            Server.AddAccount("alice");

            var client = CreateClient();

            client.Connection.MaxReconnectAttempts       = 0;
            client.Connection.ServerCertificateValidator = (_, _, _, _) => false;

            var errors = new List<String>();
            client.OnError += e => errors.Add(e);

            await FailingConnectAsync(client);

            Assert.Multiple(() =>
            {
                Assert.That(client.IsConnected, Is.False,
                            "A certificate that was turned away must not yield a connection.");

                Assert.That(errors, Is.Not.Empty,
                            "The break-off has to be reported.");
            });

        }

        #endregion

        #region DefaultValidation_RejectsTheSelfSignedCertificate()

        /// <summary>
        /// The second counter-check: without a check of one's own the operating
        /// system's takes hold, and that refuses a self-signed certificate.
        ///
        /// That vouches for the connections of the other tests hanging on the
        /// pinned fingerprint and not on a check being missing somewhere.
        /// </summary>
        [Test]
        public async Task DefaultValidation_RejectsTheSelfSignedCertificate()
        {

            Server.AddAccount("alice");

            var client = CreateClient();

            client.Connection.MaxReconnectAttempts       = 0;
            client.Connection.ServerCertificateValidator = null;

            await FailingConnectAsync(client);

            Assert.That(client.IsConnected, Is.False,
                        "A self-signed certificate must not pass the standard check.");

        }

        #endregion

        #region PinnedCertificate_AcceptsOnlyTheOwnOne()

        /// <summary>
        /// The check all the tests work with accepts exactly the certificate of
        /// this server - another one, built the same way, it does not.
        /// </summary>
        /// <remarks>
        /// A second server makes itself a certificate of its own with the same
        /// name. If the check looked only at the name, this would go through.
        /// </remarks>
        [Test]
        public async Task PinnedCertificate_AcceptsOnlyTheOwnOne()
        {

            await using var other = Watched(new XMPPServer());

            Assert.Multiple(() =>
            {

                Assert.That(Server.IsOwnCertificate(this, Server.Certificate, null, System.Net.Security.SslPolicyErrors.None),
                            Is.True,
                            "The own certificate has to be accepted.");

                Assert.That(Server.IsOwnCertificate(this, other.Certificate, null, System.Net.Security.SslPolicyErrors.None),
                            Is.False,
                            "The certificate of another server must not go through.");

            });

        }

        #endregion

        #region PlainServer_StillSpeaksWs()

        /// <summary>
        /// The way out stays open: without TLS the server goes on speaking
        /// <c>ws://</c>. That is useful for hunting faults with a recording -
        /// and the switch should not be merely claimed.
        /// </summary>
        [Test]
        public async Task PlainServer_StillSpeaksWs()
        {

            await using var plain = Watched(new XMPPServer(useTLS: false));

            plain.Start();
            plain.AddAccount("alice");

            Assert.That(plain.Uri, Does.StartWith("ws://"));
            Assert.That(plain.Certificate, Is.Null);

            var connection = new XMPPConnection($"alice@{plain.Domain}",
                                                "pw",
                                                plain.Uri)
            {
                KeepaliveEnabled      = false,
                MaxReconnectAttempts  = 0
            };

            await using var client = new XMPPClient(connection);

            await client.ConnectAsync();

            Assert.That(client.IsConnected, Is.True);

        }

        #endregion

        #region ASuppliedCertificate_IsUsedInsteadOfASelfSignedOne()

        /// <summary>
        /// The server certificate may come from outside.
        /// </summary>
        /// <remarks>
        /// A self-signed certificate cannot be checked by a foreign
        /// counterpart: it would have to know this one particular certificate,
        /// and that arises anew at every start. For a run against Prosody - and
        /// for any service that is not a test - it has to come from a chain
        /// both sides trust. That is what the attempt against a foreign
        /// counterpart failed at, before a single byte of protocol had been
        /// exchanged.
        /// </remarks>
        [Test]
        public async Task ASuppliedCertificate_IsUsedInsteadOfASelfSignedOne()
        {

            using var own = MakeCertificate("example.test");

            await using var server = Watched(new XMPPServer("example.test", certificate: own));

            server.Start();
            server.AddAccount("alice");

            Assert.That(server.Certificate?.Thumbprint, Is.EqualTo(own.Thumbprint),
                        "The server built itself one all the same.");

            // And it really does carry the handshake: the check on the client
            // side pins the fingerprint of exactly this certificate.
            var connection = new XMPPConnection($"alice@{server.Domain}", "pw", server.Uri) {
                                 KeepaliveEnabled            = false,
                                 MaxReconnectAttempts        = 0,
                                 ServerCertificateValidator  = (_, c, _, _) =>
                                     c is not null &&
                                     c.GetCertHashString(HashAlgorithmName.SHA256)
                                      .Equals(own.GetCertHashString(HashAlgorithmName.SHA256),
                                              StringComparison.OrdinalIgnoreCase)
                             };

            await using var client = new XMPPClient(connection);

            await client.ConnectAsync();

            Assert.That(client.IsConnected, Is.True);

        }

        private static X509Certificate2 MakeCertificate(String domain)
        {

            using var key = RSA.Create(2048);

            var request = new CertificateRequest($"CN={domain}", key,
                                                 HashAlgorithmName.SHA256,
                                                 RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));

            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(domain);
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());

            var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                                                      DateTimeOffset.UtcNow.AddDays(1));

            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);

        }

        #endregion

    }

}
