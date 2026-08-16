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
    /// The ranking of the SASL mechanisms and the two lower bounds upon it.
    /// </summary>
    /// <remarks>
    /// Checked on its own, because of all these decisions the connection shows
    /// only the one the test server happens to offer - and that one offers from
    /// the strongest to the weakest. A choice that in truth only takes the first
    /// entry would look exactly the same there as one that reads the ranking.
    /// </remarks>
    [TestFixture]
    public class SaslMechanismPolicyTests
    {

        #region Strongest_ReadsTheRankingAndNotTheOrder()

        /// <summary>
        /// The choice goes by strength, not by the order of the announcement.
        /// </summary>
        /// <remarks>
        /// The order is set by the server, and at this point the server is
        /// precisely the party not to be trusted: whoever takes the first entry
        /// lets the downgrade simply be written out for them.
        /// </remarks>
        [Test]
        public void Strongest_ReadsTheRankingAndNotTheOrder()
        {

            Assert.Multiple(() =>
            {

                Assert.That(SaslMechanismPolicy.Strongest(["PLAIN", "SCRAM-SHA-1", "SCRAM-SHA-256"]),
                            Is.EqualTo("SCRAM-SHA-256"));

                Assert.That(SaslMechanismPolicy.Strongest(["SCRAM-SHA-256", "SCRAM-SHA-1", "PLAIN"]),
                            Is.EqualTo("SCRAM-SHA-256"));

                Assert.That(SaslMechanismPolicy.Strongest(["PLAIN", "SCRAM-SHA-1"]),
                            Is.EqualTo("SCRAM-SHA-1"));

                Assert.That(SaslMechanismPolicy.Strongest(["PLAIN"]),
                            Is.EqualTo("PLAIN"));

            });

        }

        #endregion

        #region Strongest_IgnoresWhatItCannotSpeak()

        /// <summary>
        /// Unknown mechanisms do not count - not even when they sound stronger.
        /// </summary>
        /// <remarks>
        /// EXTERNAL, ANONYMOUS and X-OAUTH2 do occur out there; the client
        /// speaks none of them. To choose one of those would mean starting with
        /// a mechanism for which there is no procedure.
        /// </remarks>
        [Test]
        public void Strongest_IgnoresWhatItCannotSpeak()
        {

            Assert.Multiple(() =>
            {

                Assert.That(SaslMechanismPolicy.Strongest(["EXTERNAL", "ANONYMOUS", "SCRAM-SHA-1"]),
                            Is.EqualTo("SCRAM-SHA-1"));

                Assert.That(SaslMechanismPolicy.Strongest(["EXTERNAL", "ANONYMOUS"]),
                            Is.Null);

                Assert.That(SaslMechanismPolicy.Strongest([]),
                            Is.Null);

                // In lower case it is not the same name: SASL mechanisms are
                // upper case under RFC 4422, section 3.1.
                Assert.That(SaslMechanismPolicy.Strongest(["scram-sha-256"]),
                            Is.Null);

            });

        }

        #endregion

        #region Pinned_RefusesTheWeakerAndAllowsTheStronger()

        /// <summary>
        /// The pinned lower bound lets through what is equally strong and
        /// stronger, and refuses only downwards.
        /// </summary>
        /// <remarks>
        /// The point is the second half: a server that adds SCRAM-SHA-256 must
        /// not fail because SCRAM-SHA-1 was in use last time. A pinning that
        /// checks for equality would be more convenient to write and would do
        /// exactly that.
        /// </remarks>
        [Test]
        public void Pinned_RefusesTheWeakerAndAllowsTheStronger()
        {

            var policy = new SaslMechanismPolicy();

            // Before the first login nothing is pinned.
            Assert.That(() => policy.EnsureAcceptable("PLAIN"), Throws.Nothing);

            policy.Remember("SCRAM-SHA-1");

            Assert.Multiple(() =>
            {

                Assert.That(policy.Pinned, Is.EqualTo("SCRAM-SHA-1"));

                Assert.That(() => policy.EnsureAcceptable("SCRAM-SHA-1"),   Throws.Nothing);
                Assert.That(() => policy.EnsureAcceptable("SCRAM-SHA-256"), Throws.Nothing);

                // The type narrowed when SaslDowngradeException came in, and
                // Throws.TypeOf demands the exact one - which is why this had
                // to be touched at all. Narrowed further rather than loosened
                // to InstanceOf: which of the three causes refused it is the
                // thing an application acts on, and only one of them may be
                // answered by lowering the demand. This is not that one.
                Assert.That(() => policy.EnsureAcceptable("PLAIN"),
                            Throws.TypeOf<SaslDowngradeException>().
                                   With.Property("Cause").
                                   EqualTo(SaslDowngradeCause.BelowPinnedMechanism));

            });

        }

        #endregion

        #region Minimum_HoldsWithoutAnyPreviousLogin()

        /// <summary>
        /// The lower bound that was set takes effect at once - it is what the
        /// pinning cannot yet be on the very first occasion.
        /// </summary>
        [Test]
        public void Minimum_HoldsWithoutAnyPreviousLogin()
        {

            var policy = new SaslMechanismPolicy
            {
                Minimum = "SCRAM-SHA-256"
            };

            Assert.Multiple(() =>
            {

                Assert.That(policy.Pinned, Is.Null, "Without a login nothing may be pinned.");

                Assert.That(() => policy.EnsureAcceptable("SCRAM-SHA-256"), Throws.Nothing);

                // BelowConfiguredMinimum and not BelowPinnedMechanism, and the
                // distinction is the whole point of naming the cause: nothing
                // is pinned here, so this is a demand that may simply be wrong
                // for the server - the one case an application is entitled to
                // answer with "say what your server really offers".
                Assert.That(() => policy.EnsureAcceptable("SCRAM-SHA-1"),
                            Throws.TypeOf<SaslDowngradeException>().
                                   With.Property("Cause").
                                   EqualTo(SaslDowngradeCause.BelowConfiguredMinimum));

                Assert.That(() => policy.EnsureAcceptable("PLAIN"),
                            Throws.TypeOf<SaslDowngradeException>().
                                   With.Property("Cause").
                                   EqualTo(SaslDowngradeCause.BelowConfiguredMinimum));

            });

        }

        #endregion

        #region Minimum_RefusesAnUnknownName()

        /// <summary>
        /// A name the ranking does not know is refused when it is set.
        /// </summary>
        /// <remarks>
        /// Otherwise a typo would be the most dangerous input of all: an unknown
        /// name has strength 0, and a lower bound of 0 demands nothing at all.
        /// The caller would silently get the opposite of what they wrote down.
        /// </remarks>
        [Test]
        public void Minimum_RefusesAnUnknownName()
        {

            var policy = new SaslMechanismPolicy();

            Assert.Multiple(() =>
            {

                Assert.That(() => policy.Minimum = "SCRAM-SHA-512",
                            Throws.TypeOf<ArgumentException>());

                Assert.That(() => policy.Minimum = "scram-sha-256",
                            Throws.TypeOf<ArgumentException>());

                // And null stays admissible: it is the switching-off.
                Assert.That(() => policy.Minimum = null, Throws.Nothing);

            });

        }

        #endregion

    }

}
