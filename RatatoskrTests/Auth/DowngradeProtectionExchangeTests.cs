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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0474 through a real exchange rather than as a pure function.
    /// </summary>
    /// <remarks>
    /// The hash is checked next door, against the vector the XEP publishes.
    /// What is left over is everything around it, and it is the part that
    /// cannot be checked by agreeing with oneself: that the server puts the
    /// attribute where RFC 5802 already carries it, that the client looks for
    /// it, that a login still works when it is right - and that one stops when
    /// it is wrong.
    /// </remarks>
    [TestFixture]
    public class DowngradeProtectionExchangeTests : AXMPPTests
    {

        #region AnOrdinaryLogin_HasItsAnnouncementVerified()

        /// <summary>
        /// The happy path, and it is not decoration: a check that refuses
        /// nothing is indistinguishable from an absent check as long as nobody
        /// attacks, and a check that refuses everything only shows up as "the
        /// client cannot log in any more".
        /// </summary>
        /// <remarks>
        /// The assertion is on <c>Verified</c> and not merely on "the login
        /// worked". Those are different facts, and the whole reason the result
        /// is a three-valued property rather than a boolean: a server that does
        /// not implement the XEP also logs in perfectly well, and if this
        /// implementation quietly stopped sending or reading the attribute the
        /// login would go on succeeding and nothing would say so.
        /// </remarks>
        [Test]
        public async Task AnOrdinaryLogin_HasItsAnnouncementVerified()
        {

            var alice = await ConnectClientAsync("alice");

            Assert.That(alice.Connection.DowngradeProtection,
                        Is.EqualTo(SaslDowngradeProtectionResult.Verified),
                        "The server signs its announcement and the client checks it.");

        }

        #endregion

        #region AnAnnouncementSignedOtherwise_StopsTheLogin()

        /// <summary>
        /// The finding this exists for. The server hashes a list other than the
        /// one it announced, which is what the client sees when somebody in
        /// between has taken a mechanism out on the way.
        /// </summary>
        /// <remarks>
        /// Note where it stops: <c>ProcessServerFirstMessage</c>, before the key
        /// derivation and therefore before any answer goes back. That ordering
        /// is worth a test of its own only in that this one would still pass
        /// with the check placed later - so the reason it sits early is written
        /// beside it in the source rather than measured here: PBKDF2 over an
        /// iteration count the far side just chose is the most expensive thing
        /// in the exchange, and a forged announcement should not buy it.
        /// </remarks>
        [Test]
        public async Task AnAnnouncementSignedOtherwise_StopsTheLogin()
        {

            Server.SignAnotherSaslAnnouncement = true;

            var thrown = Assert.ThrowsAsync<SaslDowngradeException>(
                             async () => await ConnectClientAsync("mallory")
                         );

            Assert.Multiple(() =>
            {

                Assert.That(thrown!.Message, Does.Contain("XEP-0474"));

                Assert.That(thrown!.Cause,
                            Is.EqualTo(SaslDowngradeCause.ForgedAnnouncement));

                Assert.That(thrown!.IsAnswerableByConfiguration, Is.False,
                            "A list that does not match what arrived is an alarm, not a " +
                            "server that happens to be configured modestly - so it must not " +
                            "be answered with 'lower your demand and try again'.");

            });

            await Task.CompletedTask;

        }

        #endregion

        #region ATolerantClient_LogsInButIsNotToldItWasVerified()

        /// <summary>
        /// The escape hatch, and what it deliberately does not do.
        /// </summary>
        /// <remarks>
        /// XEP-0474 is Experimental at 0.5.0 and this check is fail-closed
        /// against it. Change the construction of the hashed string in a later
        /// revision and a server on that revision is, from here,
        /// indistinguishable from an attacker - the login would be refused,
        /// correctly by this code's lights and wrongly in fact. So there is a
        /// way through.
        ///
        /// What is measured here is the half that matters: it lets the login
        /// happen and it still reports Mismatch. A switch that also reported
        /// Verified would be worse than no check at all, because it would
        /// answer "was the announcement confirmed" with yes on the strength of
        /// a configuration flag.
        /// </remarks>
        [Test]
        public async Task ATolerantClient_LogsInButIsNotToldItWasVerified()
        {

            Server.SignAnotherSaslAnnouncement = true;
            Server.AddAccount("alice");

            // CreateClient rather than ConnectClientAsync: the switch has to be
            // set before the handshake, and the base fixture hands out an
            // unconnected client for exactly this.
            var alice = CreateClient("alice");
            alice.Connection.RefuseOnAnnouncementMismatch = false;

            await alice.ConnectAsync();

            Assert.That(alice.Connection.DowngradeProtection,
                        Is.EqualTo(SaslDowngradeProtectionResult.Mismatch),
                        "Tolerated is not verified, and must never read as it.");

        }

        #endregion

        #region WithoutTheAttribute_TheLoginStillWorks()

        /// <summary>
        /// XEP-0474 is experimental and almost nothing implements it. A client
        /// that demanded the attribute would refuse every server in the world,
        /// including the ejabberd this was first pointed at.
        /// </summary>
        /// <remarks>
        /// Checked on the authenticator rather than through a connection,
        /// because our own server always sends the attribute now and a switch
        /// to turn it off would exist only to be read by this one test.
        ///
        /// The server-first-message here is the RFC 5802, section 5 example -
        /// which predates the XEP and therefore carries no <c>h</c>, which is
        /// exactly the shape being tested.
        /// </remarks>
        [Test]
        public void WithoutTheAttribute_TheLoginStillWorks()
        {

            var scram = new SCRAMAuthenticator("user",
                                               "pencil",
                                               SCRAMMechanism.ScramSha1,
                                               ["SCRAM-SHA-1", "PLAIN"],
                                               null)
                        {
                            FixedClientNonce = "fyko+d2lbbFgONRv9qkxdawL"
                        };

            scram.CreateClientFirstMessage();

            var serverFirst = Convert.ToBase64String(
                                  System.Text.Encoding.UTF8.GetBytes(
                                      "r=fyko+d2lbbFgONRv9qkxdawL3rfcNHYJY1ZVvWVs7j," +
                                      "s=QSXCR+Q6sek8bf92," +
                                      "i=4096"));

            Assert.DoesNotThrow(() => scram.ProcessServerFirstMessage(serverFirst));

            Assert.That(scram.DowngradeProtection,
                        Is.EqualTo(SaslDowngradeProtectionResult.NotOffered),
                        "Not checked is its own answer, and must not read as checked.");

        }

        #endregion

    }

}
