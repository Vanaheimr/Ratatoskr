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
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// D142: <see cref="XMPPConnection.DialAddress"/> - reaching a server at an
    /// address that is not its name, without asking it for the wrong origin.
    /// </summary>
    /// <remarks>
    /// A test server for jabber.test listens on the loopback, and the only way
    /// to reach it used to be wss://localhost. That puts "localhost" into the
    /// TLS server name and the Host field, so the request asks for an origin the
    /// server's certificate does not cover - and RFC 9110, Section 7.4 says such
    /// a request "MUST be rejected". Hermod does so since 6c94974d, with 421
    /// (Misdirected Request), and the conformance suite's federation round over
    /// a bidirectional stream was the first to find out.
    ///
    /// The certificates here name <c>chat.invalid</c> and nothing else -
    /// deliberately not "localhost" as well, which is what lets the other TLS
    /// tests in this project pass a check of that kind without meaning to. And
    /// .invalid is reserved never to resolve (RFC 6761, Section 6.4), so a
    /// connection that gets anywhere at all got there by the dial address.
    /// </remarks>
    [TestFixture]
    public class DialAddressTests : AXMPPTests
    {

        private const String Origin = "chat.invalid";

        #region TheOriginIsNamedAndTheAddressIsDialled()

        /// <summary>
        /// What reaches the far end: the name in SNI and in the Host field,
        /// while the TCP connection went to the hand-given address.
        /// </summary>
        /// <remarks>
        /// Measured at a listener of the test's own, not at a server, because
        /// the property is about what the client sends - and a server that
        /// does not yet check the authority would accept the wrong one and say
        /// nothing. This one writes down what it was asked for and refuses.
        /// </remarks>
        [Test]
        public async Task TheOriginIsNamedAndTheAddressIsDialled()
        {

            using var certificate = MakeCertificate(Origin);

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            try
            {

                var port      = ((IPEndPoint) listener.LocalEndpoint).Port;
                var sni       = (String?) null;
                var host      = (String?) null;

                var farEnd    = Task.Run(async () => {

                    using var tcp = await listener.AcceptTcpClientAsync();
                    using var tls = new SslStream(tcp.GetStream());

                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                              ServerCertificateSelectionCallback = (_, name) => {
                                  sni = name;
                                  return certificate;
                              }
                          });

                    // The upgrade request up to its empty line; the Host field
                    // is the one line wanted out of it.
                    var request = new StringBuilder();
                    var buffer  = new Byte[1];

                    while (!request.ToString().EndsWith("\r\n\r\n") &&
                           await tls.ReadAsync(buffer) == 1)
                        request.Append((Char) buffer[0]);

                    foreach (var line in request.ToString().Split("\r\n"))
                        if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                            host = line[5..].Trim();

                    // Refused, so that the client gives up rather than waiting
                    // for a stream that will not come.
                    await tls.WriteAsync(Encoding.ASCII.GetBytes(
                              "HTTP/1.1 421 Misdirected Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));

                });

                var connection = new XMPPConnection(JID.Parse($"alice@{Origin}"),
                                                    "pw",
                                                    URL.Parse($"wss://{Origin}:{port}/xmpp")) {
                                     KeepaliveEnabled            = false,
                                     MaxReconnectAttempts        = 0,
                                     DialAddress                 = IPAddress.Loopback,
                                     ServerCertificateValidator  = (_, _, _, _) => true
                                 };

                await using var client = new XMPPClient(connection);

                await Assert.CatchAsync(async () => await client.ConnectAsync());

                await farEnd.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.Multiple(() => {

                    Assert.That(sni,  Is.EqualTo(Origin),
                                "The TLS server name was not the origin. A server with one " +
                                "certificate per name would have chosen by it, and chosen wrong.");

                    Assert.That(host, Is.EqualTo($"{Origin}:{port}"),
                                "The Host field named something other than the origin - which is " +
                                "the request RFC 9110, Section 7.4 has a server refuse with 421.");

                });

            }
            finally
            {
                listener.Stop();
            }

        }

        #endregion

        #region AStreamThroughADialAddressCompletes()

        /// <summary>
        /// The whole way: TLS, the upgrade, SASL and binding, to a server for a
        /// name that resolves nowhere, reached by its address alone.
        /// </summary>
        /// <remarks>
        /// The second path in <see cref="XMPPConnection"/> - through an invoker
        /// of its own rather than ClientWebSocket's defaults - had never carried
        /// a stream before this test. In particular the certificate check has
        /// to have moved with it: the certificate here is self-signed, so a
        /// handler that fell back to the platform's judgement would refuse it,
        /// and the pinned fingerprint is what lets this through.
        /// </remarks>
        [Test]
        public async Task AStreamThroughADialAddressCompletes()
        {

            using var own = MakeCertificate(Origin);

            await using var server = Watched(new XMPPServer(Origin, certificate: own));

            server.Start();
            server.AddAccount("alice");

            // The server's own Uri says localhost; the origin replaces it, the
            // port and the path stay.
            var endpoint   = new UriBuilder(server.Uri.ToString()) { Host = Origin };

            var connection = new XMPPConnection(JID.Parse($"alice@{server.Domain}"),
                                                "pw",
                                                URL.Parse(endpoint.Uri.ToString())) {
                                 KeepaliveEnabled            = false,
                                 MaxReconnectAttempts        = 0,
                                 DialAddress                 = IPAddress.Loopback,
                                 ServerCertificateValidator  = (_, c, _, _) =>
                                     c is not null &&
                                     c.GetCertHashString(HashAlgorithmName.SHA256)
                                      .Equals(own.GetCertHashString(HashAlgorithmName.SHA256),
                                              StringComparison.OrdinalIgnoreCase)
                             };

            await using var client = new XMPPClient(connection);

            await client.ConnectAsync();

            Assert.That(client.IsConnected, Is.True,
                        "A stream to a server reached by its address alone did not come up.");

        }

        #endregion


        #region (private static) MakeCertificate(Domain)

        /// <summary>
        /// A certificate for exactly one name, and not for "localhost" as well -
        /// see the remarks on the class.
        /// </summary>
        private static X509Certificate2 MakeCertificate(String Domain)
        {

            using var key = RSA.Create(2048);

            var request = new CertificateRequest($"CN={Domain}", key,
                                                 HashAlgorithmName.SHA256,
                                                 RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));

            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(Domain);
            request.CertificateExtensions.Add(names.Build());

            var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                                                      DateTimeOffset.UtcNow.AddDays(1));

            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);

        }

        #endregion

    }

}
