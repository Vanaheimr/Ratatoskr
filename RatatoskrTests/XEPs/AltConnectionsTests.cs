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
    /// XEP-0156: reading the WebSocket endpoint of a domain out of its
    /// <c>host-meta</c> - without a net, with an inserted fetcher.
    /// </summary>
    /// <remarks>
    /// Two rules of the XEP are security rules and therefore stand at the
    /// centre: "host-meta files MUST be fetched only over HTTPS, and MUST only
    /// use connection URLs starting with 'https://' or 'wss://'."
    ///
    /// Both are the same thought. Whoever fetches the information in plaintext
    /// lets every man in the middle determine where the client logs in;
    /// whoever accepts a <c>ws://</c> from information fetched that way sends
    /// user and password there afterwards. The one is worthless without the
    /// other.
    ///
    /// The DNS path over <c>_xmppconnect</c> TXT records, which earlier
    /// versions of the XEP knew, is therefore not implemented at all: it was
    /// removed from the document - "this was insecure and has been removed".
    /// </remarks>
    [TestFixture]
    public class AltConnectionsTests
    {

        #region Examples from the XEP

        private const String Xrd =
            "<?xml version='1.0' encoding='utf-8'?>" +
            "<XRD xmlns='http://docs.oasis-open.org/ns/xri/xrd-1.0'>" +
            "<Link rel='urn:xmpp:alt-connections:xbosh' href='https://web.example.com:5280/bosh'/>" +
            "<Link rel='urn:xmpp:alt-connections:websocket' href='wss://web.example.com:443/ws'/>" +
            "</XRD>";

        private const String Jrd =
            "{ \"links\": [" +
            "{ \"rel\": \"urn:xmpp:alt-connections:xbosh\", \"href\": \"https://web.example.com:5280/bosh\" }," +
            "{ \"rel\": \"urn:xmpp:alt-connections:websocket\", \"href\": \"wss://web.example.com:443/ws\" }" +
            "] }";

        #endregion

        #region Helper functions

        /// <summary>
        /// A fetcher that writes the requested addresses down and gives a
        /// deposited answer depending on the address.
        /// </summary>
        private static AltConnectionsResolver Resolver(List<String>                 queried,
                                                       IDictionary<String, String>  replies)

            => new ((uri, ct) =>
               {
                   queried.Add(uri);
                   return Task.FromResult(replies.TryGetValue(uri, out var reply) ? reply : null);
               });

        #endregion


        #region TheWebsocketLinkIsTakenFromTheXrd()

        /// <summary>
        /// The XRD example from the XEP: the websocket link is found, the BOSH
        /// link is not taken along.
        /// </summary>
        [Test]
        public void TheWebsocketLinkIsTakenFromTheXrd()
        {

            var endpoints = AltConnectionsResolver.WebSocketEndpointsFromXrd(Xrd);

            Assert.Multiple(() =>
            {
                Assert.That(endpoints, Has.Count.EqualTo(1), $"Found: {String.Join(", ", endpoints)}");
                Assert.That(endpoints[0], Is.EqualTo("wss://web.example.com:443/ws"));
            });

        }

        #endregion

        #region TheWebsocketLinkIsTakenFromTheJrd()

        /// <summary>
        /// The same for the JSON version.
        /// </summary>
        [Test]
        public void TheWebsocketLinkIsTakenFromTheJrd()
        {

            var endpoints = AltConnectionsResolver.WebSocketEndpointsFromJrd(Jrd);

            Assert.Multiple(() =>
            {
                Assert.That(endpoints, Has.Count.EqualTo(1), $"Found: {String.Join(", ", endpoints)}");
                Assert.That(endpoints[0], Is.EqualTo("wss://web.example.com:443/ws"));
            });

        }

        #endregion

        #region APlaintextEndpoint_IsRejected()

        /// <summary>
        /// A <c>ws://</c> endpoint is not taken - in both formats.
        /// </summary>
        /// <remarks>
        /// That is no formality: The endpoint determines where user and
        /// password go in a moment. Information that comes over TLS and then
        /// points into the plaintext net lifts the protection again.
        /// </remarks>
        [Test]
        public void APlaintextEndpoint_IsRejected()
        {

            var xrd = "<XRD xmlns='http://docs.oasis-open.org/ns/xri/xrd-1.0'>" +
                      "<Link rel='urn:xmpp:alt-connections:websocket' href='ws://web.example.com:5280/ws'/>" +
                      "</XRD>";

            var jrd = "{ \"links\": [ { \"rel\": \"urn:xmpp:alt-connections:websocket\"," +
                      " \"href\": \"ws://web.example.com:5280/ws\" } ] }";

            Assert.Multiple(() =>
            {
                Assert.That(AltConnectionsResolver.WebSocketEndpointsFromXrd(xrd), Is.Empty);
                Assert.That(AltConnectionsResolver.WebSocketEndpointsFromJrd(jrd), Is.Empty);
            });

        }

        #endregion

        #region AnotherRelation_IsNotAnEndpoint()

        /// <summary>
        /// The link type decides, not the scheme: a <c>wss://</c> under another
        /// <c>rel</c> is no WebSocket endpoint.
        /// </summary>
        /// <remarks>
        /// A <c>host-meta</c> is not made for XMPP - there stand <c>lrdd</c>,
        /// <c>webfinger</c> and whatever else the operator publishes. Whoever
        /// looks only at the scheme takes the first entry to hand that happens
        /// to be encrypted.
        /// </remarks>
        [Test]
        public void AnotherRelation_IsNotAnEndpoint()
        {

            var xrd = "<XRD xmlns='http://docs.oasis-open.org/ns/xri/xrd-1.0'>" +
                      "<Link rel='urn:xmpp:alt-connections:xbosh' href='wss://web.example.com:443/bosh'/>" +
                      "</XRD>";

            var jrd = "{ \"links\": [ { \"rel\": \"urn:xmpp:alt-connections:xbosh\"," +
                      " \"href\": \"wss://web.example.com:443/bosh\" } ] }";

            Assert.Multiple(() =>
            {
                Assert.That(AltConnectionsResolver.WebSocketEndpointsFromXrd(xrd), Is.Empty);
                Assert.That(AltConnectionsResolver.WebSocketEndpointsFromJrd(jrd), Is.Empty);
            });

        }

        #endregion

        #region TheFirstEndpointWins()

        /// <summary>
        /// If a domain names several endpoints, the first one holds - and the
        /// order of the document is preserved.
        /// </summary>
        [Test]
        public async Task TheFirstEndpointWins()
        {

            var jrd       = "{ \"links\": [" +
                            "{ \"rel\": \"urn:xmpp:alt-connections:websocket\", \"href\": \"wss://first.example/ws\" }," +
                            "{ \"rel\": \"urn:xmpp:alt-connections:websocket\", \"href\": \"wss://then.example/ws\" }" +
                            "] }";

            var resolver  = Resolver(
                                [],
                                new Dictionary<String, String> {
                                    [ "https://example.test/.well-known/host-meta.json" ] = jrd
                                }
                            );

            Assert.Multiple(async () => {

                Assert.That(AltConnectionsResolver.WebSocketEndpointsFromJrd(jrd),
                            Is.EqualTo([ "wss://first.example/ws", "wss://then.example/ws" ]));

                Assert.That(await resolver.DiscoverWebSocketAsync("example.test"),
                            Is.EqualTo(URL.Parse("wss://first.example/ws")));

            });

        }

        #endregion

        #region Garbage_IsNotAnException()

        /// <summary>
        /// What is no valid file yields no endpoints - and no error.
        /// </summary>
        /// <remarks>
        /// The content comes from a foreign web server that can deliver
        /// anything: an error page, a redirect as HTML, half a file. If an
        /// exception flies here, the connection setup fails at the discovery
        /// instead of carrying on without it.
        /// </remarks>
        [Test]
        public void Garbage_IsNotAnException()
        {

            Assert.Multiple(() =>
            {
                Assert.That(AltConnectionsResolver.WebSocketEndpointsFromXrd("<html><body>404"), Is.Empty);
                Assert.That(AltConnectionsResolver.WebSocketEndpointsFromJrd("<html><body>404"), Is.Empty);
                Assert.That(AltConnectionsResolver.WebSocketEndpointsFromXrd(""),                Is.Empty);
                Assert.That(AltConnectionsResolver.WebSocketEndpointsFromJrd("{ \"links\": 5 }"), Is.Empty);
            });

        }

        #endregion

        #region TheJsonFormIsTriedFirst()

        /// <summary>
        /// The question goes over HTTPS after
        /// <c>/.well-known/host-meta.json</c>; if an endpoint is found there,
        /// it stays with this one query.
        /// </summary>
        [Test]
        public async Task TheJsonFormIsTriedFirst()
        {

            var queried = new List<String>();

            var resolver = Resolver(queried,
                               new Dictionary<String, String> {
                                   ["https://example.test/.well-known/host-meta.json"] = Jrd,
                                   ["https://example.test/.well-known/host-meta"]      = Xrd
                               });

            var endpoint = await resolver.DiscoverWebSocketAsync("example.test");

            Assert.Multiple(() =>
            {
                Assert.That(endpoint, Is.EqualTo(URL.Parse("wss://web.example.com:443/ws")));
                Assert.That(queried, Is.EqualTo([ "https://example.test/.well-known/host-meta.json" ]),
                            $"What was queried: {String.Join(", ", queried)}");
            });

        }

        #endregion

        #region WithoutTheJsonForm_TheXrdIsUsed()

        /// <summary>
        /// If the JSON version delivers nothing, the XML version is queried
        /// afterwards.
        /// </summary>
        [Test]
        public async Task WithoutTheJsonForm_TheXrdIsUsed()
        {

            var queried   = new List<String>();

            var resolver  = Resolver(
                                queried,
                                new Dictionary<String, String> {
                                    [ "https://example.test/.well-known/host-meta" ] = Xrd
                                }
                            );

            var endpoint  = await resolver.DiscoverWebSocketAsync("example.test");

            Assert.Multiple(() => {
                Assert.That(endpoint, Is.EqualTo(URL.Parse("wss://web.example.com:443/ws")));
                Assert.That(queried, Is.EqualTo([
                                "https://example.test/.well-known/host-meta.json",
                                "https://example.test/.well-known/host-meta"
                            ]),
                            $"What was queried: {String.Join(", ", queried)}");
            });

        }

        #endregion

        #region WithoutAnyHostMeta_NothingIsDiscovered()

        /// <summary>
        /// A domain without a <c>host-meta</c> yields no endpoint - and no
        /// error. The caller then stays with their default.
        /// </summary>
        [Test]
        public async Task WithoutAnyHostMeta_NothingIsDiscovered()
        {

            var queried  = new List<String>();
            var resolver = Resolver(queried, new Dictionary<String, String>());

            var endpoint  = await resolver.DiscoverWebSocketAsync("example.test");

            Assert.That(endpoint, Is.Null);

        }

        #endregion

        #region AnAnswerThatCameOverHttp_IsNotRead()

        /// <summary>
        /// The MUST of the XEP, asked of the address the answer came from.
        /// </summary>
        /// <remarks>
        /// A security review described the attack this stands against: the
        /// host-meta is asked for over https, the answer is a redirect to
        /// http, and whoever sits on the cleartext hop then decides where the
        /// client signs on - and with it where the SCRAM proof goes.
        ///
        /// It does not work, and the reason it does not is not in this
        /// repository: .NET refuses a redirect from https to http in
        /// <c>RedirectHandler.GetUriForRedirect</c>. That was worth checking
        /// rather than believing, and what it left behind is this - a defence
        /// that was real, inherited, unstated and untested, so that anybody
        /// installing a handler of their own would have removed it without a
        /// single test going red.
        ///
        /// A rule nothing tests is a rule that leaves quietly.
        /// </remarks>
        [Test]
        public void AnAnswerThatCameOverHttp_IsNotRead()
        {

            Assert.Multiple(() => {

                Assert.That(AltConnectionsResolver.MayBeRead(new Uri("https://example.test/.well-known/host-meta.json")),
                            Is.True,
                            "An answer that came over https is the ordinary case and has to be readable.");

                Assert.That(AltConnectionsResolver.MayBeRead(new Uri("http://example.test/.well-known/host-meta.json")),
                            Is.False,
                            "An answer that came over http was read all the same, so a redirect out of TLS " +
                            "decides where this client sends its password.");

                Assert.That(AltConnectionsResolver.MayBeRead(null),
                            Is.False,
                            "No address at all counted as good enough.");

            });

        }

        #endregion

        #region AForeignHostIsNotRefusedHere()

        /// <summary>
        /// What is deliberately <i>not</i> checked, and why the review's second
        /// half was not implemented.
        /// </summary>
        /// <remarks>
        /// The review asked for the <c>wss://</c> host to be bound to the JID
        /// domain. That would break the specification it is meant to protect:
        /// XEP-0156 exists in part so that a domain can put its XMPP service
        /// somewhere else, and the XRD example in this very file - answering
        /// for example.test with <c>web.example.com</c> - is that case.
        ///
        /// The XEP's own rule is one about the certificate, not about the
        /// name: "validate that the certificate is valid for that host or the
        /// XMPP domain". That belongs where the socket is opened and not here.
        ///
        /// This test exists so the omission is a decision on the record rather
        /// than a gap somebody closes on a quiet afternoon.
        /// </remarks>
        [Test]
        public async Task AForeignHostIsNotRefusedHere()
        {

            var queried   = new List<String>();

            var resolver  = Resolver(
                                queried,
                                new Dictionary<String, String> {
                                    [ "https://example.test/.well-known/host-meta.json" ] = Jrd
                                }
                            );

            var endpoint  = await resolver.DiscoverWebSocketAsync("example.test");

            Assert.That(endpoint, Is.EqualTo(URL.Parse("wss://web.example.com:443/ws")),
                        "The endpoint of a domain that hosts its XMPP service elsewhere was refused, " +
                        "which is the delegation XEP-0156 is there to allow.");

        }

        #endregion

    }

}
