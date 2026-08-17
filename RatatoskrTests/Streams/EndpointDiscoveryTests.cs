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
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0156 at the connection setup: whoever names no endpoint gets the one
    /// from the <c>host-meta</c> of their domain.
    /// </summary>
    /// <remarks>
    /// The order in the XEP is expressly a subordinate one: "HTTPS queries for
    /// host-meta information MUST be used only as a fallback after the methods
    /// specified in RFC 6120 have been exhausted." For this client that means:
    /// an endpoint that was given is never overruled. The question is asked only
    /// when the caller named none - and if that fails too, the built-in default
    /// stands.
    /// </remarks>
    [TestFixture]
    public class EndpointDiscoveryTests : AXMPPTests
    {

        #region Helper functions

        /// <summary>
        /// A resolver that gives the same answer for every address.
        /// </summary>
        private static AltConnectionsResolver Answers(String? hostMeta)
            => new ((uri, ct) => Task.FromResult(hostMeta));

        #endregion


        #region TheDiscoveredEndpointIsUsed()

        /// <summary>
        /// The heart of it: without a given endpoint the client logs in where
        /// the <c>host-meta</c> points.
        /// </summary>
        [Test]
        public async Task TheDiscoveredEndpointIsUsed()
        {

            Server.AddAccount("alice");

            var connection = new XMPPConnection(JID.Parse($"alice@{Server.Domain}"), "pw")
            {
                ServerCertificateValidator  = Server.IsOwnCertificate,
                EndpointDiscovery           = Answers(
                    "{ \"links\": [ { \"rel\": \"urn:xmpp:alt-connections:websocket\", \"href\": \"" +
                    Server.Uri + "\" } ] }")
            };

            var client = new XMPPClient(connection);

            await client.ConnectAsync();

            Assert.Multiple(() =>
            {
                Assert.That(connection.WebSocketUri, Is.EqualTo(Server.Uri));
                Assert.That(connection.State,        Is.EqualTo(ConnectionState.Connected));
            });

            await client.DisconnectAsync();

        }

        #endregion

        #region WithoutAHostMeta_TheDefaultRemains()

        /// <summary>
        /// If the discovery finds nothing, the built-in default stands - and the
        /// connection setup fails there, not at the discovery.
        /// </summary>
        /// <remarks>
        /// Both are checked: the endpoint it stays at, and that the error names
        /// it. The transport itself does not - it reports "Unable to connect to
        /// the remote server" and says nothing about where to (see D47).
        /// </remarks>
        [Test]
        public async Task WithoutAHostMeta_TheDefaultRemains()
        {

            var asked       = 0;

            var connection  = new XMPPConnection(
                                  JID.Parse($"alice@{Server.Domain}"),
                                  "pw"
                              ) {

                                    EndpointDiscovery      = new AltConnectionsResolver(
                                                                 (uri, ct) => {
                                                                     asked++;
                                                                     return Task.FromResult<String?>(null);
                                                                 }
                                                             ),

                                    // Nothing listens on 5443; every attempt ends at once. The
                                    // default would be five of them with growing waits - for a
                                    // statement the first one already makes.
                                    MaxReconnectAttempts   = 1,

                                    InitialReconnectDelay  = TimeSpan.FromMilliseconds(50)

                                };

            var client     = new XMPPClient(connection);

            var error      = await FailingConnectAsync(client);

            Assert.Multiple(() => {

                Assert.That(connection.WebSocketUri, Is.EqualTo(URL.Parse($"wss://{Server.Domain}:5443/ws")));

                Assert.That(error.Message, Does.Contain(connection.WebSocketUri.ToString()),
                            $"The error does not name the endpoint: {error.Message}");

                Assert.That(error.InnerException, Is.Not.Null,
                            "And it carries the original error with it.");

                // Two addresses (host-meta.json and host-meta) - but only once,
                // although the client makes a second connection attempt
                // afterwards. Whoever searches afresh at every attempt waits,
                // with a server that is gone, every time again for an HTTPS
                // answer that does not exist.
                Assert.That(asked, Is.EqualTo(2),
                            $"The discovery ran more than once: {asked} queries.");

            });

        }

        #endregion

        #region TheErrorNamesTheDiscoveredEndpoint()

        /// <summary>
        /// If the setup fails at a <b>discovered</b> endpoint, the error names
        /// that one - not the default.
        /// </summary>
        /// <remarks>
        /// That is the case the endpoint belongs in the message for at all: it
        /// comes from the <c>host-meta</c> of a foreign domain and stands in no
        /// source the caller could read. "Unable to connect to the remote
        /// server" then leaves them guessing.
        /// </remarks>
        [Test]
        public async Task TheErrorNamesTheDiscoveredEndpoint()
        {

            // Nothing listens on port 1, and reliably so.
            var discovered  = URL.Parse("wss://127.0.0.1:1/ws");

            var connection  = new XMPPConnection(
                                  JID.Parse($"alice@{Server.Domain}"),
                                  "pw"
                              ) {

                                    EndpointDiscovery      = Answers(
                                                                 "{ \"links\": [ { \"rel\": \"urn:xmpp:alt-connections:websocket\", \"href\": \"" +
                                                                 discovered + "\" } ] }"
                                                             ),

                                    MaxReconnectAttempts   = 1,

                                    InitialReconnectDelay  = TimeSpan.FromMilliseconds(50)

                              };

            var client      = new XMPPClient(connection);

            var error       = await FailingConnectAsync(client);

            Assert.Multiple(() => {
                Assert.That(connection.WebSocketUri,  Is.  EqualTo(discovered));
                Assert.That(error.Message,            Does.Contain(discovered.ToString()),
                            $"The error does not name the discovered endpoint: {error.Message}");
            });

        }

        #endregion

        #region ADeliberateCancel_StaysACancel()

        /// <summary>
        /// A cancelled connection setup stays a cancellation and is not
        /// reinterpreted as a protocol error.
        /// </summary>
        /// <remarks>
        /// The message with the endpoint is meant for what goes wrong - not for
        /// what the caller brought about themselves. Whoever pulls their token
        /// gets their <c>OperationCanceledException</c>, otherwise they can no
        /// longer tell their own cancellation from a failure.
        /// </remarks>
        [Test]
        public void ADeliberateCancel_StaysACancel()
        {

            var connection = new XMPPConnection(
                                 JID.Parse($"alice@{Server.Domain}"),
                                 "pw",
                                 Server.Uri
                             ) {
                ServerCertificateValidator = Server.IsOwnCertificate
            };

            using var cancel = new CancellationTokenSource();
            cancel.Cancel();

            Assert.That(async () => await connection.ConnectAsync(cancel.Token),
                        Throws.InstanceOf<OperationCanceledException>());

        }

        #endregion

        #region AGivenEndpoint_IsNeverOverruled()

        /// <summary>
        /// Whoever gives an endpoint is not asked - the discovery does not run
        /// at all.
        /// </summary>
        /// <remarks>
        /// Without this test "always look first" would be a passing solution. It
        /// would be wrong and expensive: a caller who knows their endpoint would
        /// pay an HTTPS query for every connection, and a foreign
        /// <c>host-meta</c> could send them somewhere else.
        /// </remarks>
        [Test]
        public async Task AGivenEndpoint_IsNeverOverruled()
        {

            Server.AddAccount("alice");

            var asked = false;

            var connection = new XMPPConnection(JID.Parse($"alice@{Server.Domain}"), "pw", Server.Uri)
            {
                ServerCertificateValidator  = Server.IsOwnCertificate,
                EndpointDiscovery           = new AltConnectionsResolver((uri, ct) =>
                                              {
                                                  asked = true;
                                                  return Task.FromResult<String?>(
                                                      "{ \"links\": [ { \"rel\": \"urn:xmpp:alt-connections:websocket\"," +
                                                      " \"href\": \"wss://elsewhere.example:443/ws\" } ] }");
                                              })
            };

            var client = new XMPPClient(connection);

            await client.ConnectAsync();

            Assert.Multiple(() =>
            {
                Assert.That(asked,                 Is.False, "The endpoint that was given is not up for debate.");
                Assert.That(connection.WebSocketUri, Is.EqualTo(Server.Uri));
            });

            await client.DisconnectAsync();

        }

        #endregion

    }

}
