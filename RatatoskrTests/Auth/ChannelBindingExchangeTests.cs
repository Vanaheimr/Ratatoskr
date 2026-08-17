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
    /// Channel binding through a real exchange: RFC 5802 section 6 with the
    /// <c>tls-server-end-point</c> of RFC 5929, announced per XEP-0440.
    /// </summary>
    /// <remarks>
    /// The value itself is never sent - it only ever appears inside a proof -
    /// so a disagreement between the two ends does not surface as a mismatch
    /// anybody can read. It surfaces as "authentication failed", which is what
    /// a wrong password looks like. These tests are the only place the
    /// difference is visible.
    /// </remarks>
    [TestFixture]
    public class ChannelBindingExchangeTests : AXMPPTests
    {

        #region TheServerAnnouncesWhatItCanBindTo()

        /// <summary>
        /// XEP-0440: the type has to be announced, or a client has no way to
        /// know it may bind - and the announcement is also the second half of
        /// the string XEP-0474 hashes.
        /// </summary>
        [Test]
        public void TheServerAnnouncesWhatItCanBindTo()
        {

            Assert.Multiple(() =>
            {

                Assert.That(Server.ChannelBindingData, Is.Not.Null,
                            "The test server runs with TLS and an RSA/SHA-256 certificate, " +
                            "which RFC 5929 defines a binding for.");

                Assert.That(Server.AnnouncedSaslMechanisms,
                            Does.Contain("SCRAM-SHA-256-PLUS"));

                // The bare variants stay: a client that cannot bind still has
                // to be able to log in.
                Assert.That(Server.AnnouncedSaslMechanisms,
                            Does.Contain("SCRAM-SHA-256"));

            });

        }

        #endregion

        #region AnOrdinaryLogin_BindsToTheChannel()

        /// <summary>
        /// The client takes the strongest mechanism it can actually perform,
        /// and over TLS that is now the bound one.
        /// </summary>
        /// <remarks>
        /// Worth asserting rather than assuming, because everything about
        /// channel binding is invisible when it works: the login succeeds
        /// either way, and the only difference between a bound and an unbound
        /// exchange is which mechanism name went across.
        /// </remarks>
        [Test]
        public async Task AnOrdinaryLogin_BindsToTheChannel()
        {

            var alice = await ConnectClientAsync("alice");

            Assert.That(alice.Connection.NegotiatedSaslMechanism,
                        Is.EqualTo("SCRAM-SHA-256-PLUS"),
                        "Over TLS, with a binding available on both sides, the bound " +
                        "mechanism is the one to take.");

        }

        #endregion

        #region ABindingLostOnTheSecondConnect_IsRefused()

        /// <summary>
        /// Once a login has been bound, an unbound one from the same server is
        /// a downgrade - and the pin refuses it.
        /// </summary>
        /// <remarks>
        /// This is the ranking doing what it was told: SCRAM-SHA-256-PLUS
        /// outranks SCRAM-SHA-256, so dropping the binding falls below the pin
        /// exactly as dropping SHA-256 for SHA-1 would. That is the behaviour
        /// worth having - losing channel binding is precisely what a man in the
        /// middle needs to happen.
        ///
        /// <b>It has a sharp edge, and it belongs written down rather than
        /// discovered.</b> The same refusal fires when nothing is wrong: a
        /// certificate renewed to Ed25519 has no binding RFC 5929 defines, and
        /// a server moved behind a TLS-terminating proxy has none to offer.
        /// Both look like this from the client, and the client will go on
        /// refusing until somebody clears the pin. Fail-closed is still right -
        /// the alternative is accepting every downgrade in case it was
        /// innocent - but whoever meets it deserves to find this test rather
        /// than guess.
        /// </remarks>
        [Test]
        public async Task ABindingLostOnTheSecondConnect_IsRefused()
        {

            var alice = await ConnectClientAsync("alice");

            Assert.That(alice.Connection.PinnedSaslMechanism,
                        Is.EqualTo("SCRAM-SHA-256-PLUS"),
                        "The first login binds, and that is what gets pinned.");

            await alice.DisconnectAsync();

            // The server loses its binding - a renewed certificate, a proxy in
            // front, or an attacker who would like the exchange unbound.
            Server.OfferChannelBinding = false;

            Assert.ThrowsAsync<SaslDowngradeException>(
                async () => await alice.ConnectAsync(),
                "An unbound offer is below the bound mechanism that was pinned."
            );

            await Task.CompletedTask;

        }

        #endregion

        #region WithoutTls_TheLoginIsUnbound()

        /// <summary>
        /// The counter-check that keeps the feature honest: with nothing to
        /// bind to, the client must not claim a binding - and must still get
        /// in.
        /// </summary>
        /// <remarks>
        /// A -PLUS mechanism chosen without a channel would send a GS2 header
        /// promising a binding that is not there, and the exchange would die at
        /// the proof with nothing a reader could act on. This is also the case
        /// every plaintext deployment is in.
        /// </remarks>
        [Test]
        public async Task WithoutTls_TheLoginIsUnbound()
        {

            await using var plain = new XMPPServer("localhost", useTLS: false);

            plain.Start();

            Assert.That(plain.ChannelBindingData, Is.Null,
                        "No TLS, nothing to bind to.");

            Assert.That(plain.AnnouncedSaslMechanisms,
                        Has.None.EndsWith("-PLUS"),
                        "A -PLUS announced over plaintext is an invitation to fail.");

            plain.AddAccount("alice");

            // No certificate validator: there is no certificate. That absence
            // is the whole premise of this test.
            await using var client = new XMPPClient(
                                         new XMPPConnection(JID.Parse($"alice@{plain.Domain}"), "pw", plain.Uri)
                                     );

            await client.ConnectAsync();

            Assert.That(client.Connection.NegotiatedSaslMechanism,
                        Is.EqualTo("SCRAM-SHA-256"));

        }

        #endregion

    }

}
