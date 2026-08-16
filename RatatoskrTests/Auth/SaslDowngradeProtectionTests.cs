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
    /// XEP-0474: the string that gets hashed, and the hash of it.
    /// </summary>
    /// <remarks>
    /// Kept apart from the exchange on purpose. Everything here is a pure
    /// function of two lists, and it is the half that is easy to get subtly
    /// wrong - a sort order, two invisible separators, a section that is
    /// written only sometimes. Against the far side those mistakes all look
    /// alike: the login is refused and the reason reads "somebody in between
    /// has taken something out of it", which is the least helpful thing a
    /// wrong implementation could say about itself.
    /// </remarks>
    [TestFixture]
    public class SaslDowngradeProtectionTests
    {

        #region TheExampleFromTheXep_IsReproduced()

        /// <summary>
        /// XEP-0474, version 0.5.0, section 4 - the one vector the document
        /// publishes, and the only place in this fixture where the expected
        /// value comes from somebody else.
        /// </summary>
        /// <remarks>
        /// It is worth more than its size: it pins the sort order, both
        /// separators, the direction of the sections and the choice of hash in
        /// a single comparison. An implementation that agrees with this string
        /// agrees with every other implementation of the XEP; one that only
        /// agrees with itself passes every test written from its own
        /// behaviour.
        /// </remarks>
        [Test]
        public void TheExampleFromTheXep_IsReproduced()
        {

            var input = SaslDowngradeProtection.HashInput(
                            ["SCRAM-SHA-1", "SCRAM-SHA-1-PLUS"],
                            ["tls-exporter", "tls-server-end-point"]
                        );

            Assert.Multiple(() =>
            {

                Assert.That(input,
                            Is.EqualTo("SCRAM-SHA-1\u001ESCRAM-SHA-1-PLUS\u001F" +
                                       "tls-exporter\u001Etls-server-end-point"));

                Assert.That(SaslDowngradeProtection.Hash(SCRAMMechanism.ScramSha1, input),
                            Is.EqualTo("G6k/rBLDqgOhRRaCuuatSDFkJ08="));

            });

        }

        #endregion

        #region TheOrderOfTheAnnouncement_DoesNotMatter()

        /// <summary>
        /// Sorted by the octet collation before hashing, so the server's
        /// announcement order - which nothing constrains - cannot decide
        /// whether a login succeeds.
        /// </summary>
        [Test]
        public void TheOrderOfTheAnnouncement_DoesNotMatter()
        {

            var one = SaslDowngradeProtection.HashInput(["SCRAM-SHA-256", "PLAIN", "SCRAM-SHA-1"]);
            var two = SaslDowngradeProtection.HashInput(["PLAIN", "SCRAM-SHA-1", "SCRAM-SHA-256"]);

            Assert.Multiple(() =>
            {
                Assert.That(one, Is.EqualTo(two));
                Assert.That(one, Is.EqualTo("PLAIN\u001ESCRAM-SHA-1\u001ESCRAM-SHA-256"),
                            "Octet order, which for these names is plain alphabetical.");
            });

        }

        #endregion

        #region WithoutChannelBindings_TheSectionIsAbsent()

        /// <summary>
        /// The separator is written only when something follows it. A trailing
        /// <c>%x1F</c> over an empty list would hash differently from no list
        /// at all, and the two mean the same thing.
        /// </summary>
        /// <remarks>
        /// This is the case that matters here rather than a curiosity: neither
        /// Ratatoskr nor the server it talks to announces a channel binding
        /// today, so every exchange in this repository takes this branch. An
        /// implementation that appended the separator unconditionally would
        /// still agree with itself on both ends and fail against everybody
        /// else.
        /// </remarks>
        [Test]
        public void WithoutChannelBindings_TheSectionIsAbsent()
        {

            var nothing = SaslDowngradeProtection.HashInput(["SCRAM-SHA-256"]);
            var empty   = SaslDowngradeProtection.HashInput(["SCRAM-SHA-256"], []);

            Assert.Multiple(() =>
            {
                Assert.That(nothing, Is.EqualTo("SCRAM-SHA-256"));
                Assert.That(empty,   Is.EqualTo(nothing),
                            "An empty list and no list are the same announcement.");
                // String.Contains(Char) and not Does.Not.Contain("\u001F"), and
                // the difference is not style. NUnit's substring constraint
                // compares culture-sensitively, and U+001F carries no weight in
                // that collation - so "SCRAM-SHA-256" *does* contain it as far
                // as the current culture is concerned, and this assertion failed
                // against a string that plainly has no such character in it. The
                // overload taking a Char is ordinal by definition.
                //
                // Worth knowing beyond this line: the same trap would be a real
                // defect in the comparison of the two hashes, where a
                // culture-aware Equals could call two different announcements
                // equal and wave a downgrade through. That comparison says
                // StringComparison.Ordinal, and so does the sort.
                Assert.That(nothing.Contains('\u001F'), Is.False,
                            "No section separator when there is no section.");
            });

        }

        #endregion

        #region TheHashFollowsTheMechanism()

        /// <summary>
        /// SHA-1 for SCRAM-SHA-1, SHA-256 for SCRAM-SHA-256 - the same hash the
        /// mechanism itself uses, so the two sides agree without negotiating
        /// anything further.
        /// </summary>
        [Test]
        public void TheHashFollowsTheMechanism()
        {

            var input = SaslDowngradeProtection.HashInput(["PLAIN", "SCRAM-SHA-256"]);

            var withSha1   = SaslDowngradeProtection.Hash(SCRAMMechanism.ScramSha1,   input);
            var withSha256 = SaslDowngradeProtection.Hash(SCRAMMechanism.ScramSha256, input);

            Assert.Multiple(() =>
            {
                Assert.That(Convert.FromBase64String(withSha1).  Length, Is.EqualTo(20));
                Assert.That(Convert.FromBase64String(withSha256).Length, Is.EqualTo(32));
                Assert.That(withSha1, Is.Not.EqualTo(withSha256));
            });

        }

        #endregion

        #region AMechanismTakenAway_ChangesTheHash()

        /// <summary>
        /// The whole point, stated as a comparison: the announcement a man in
        /// the middle would produce does not hash to what the server signed.
        /// </summary>
        [Test]
        public void AMechanismTakenAway_ChangesTheHash()
        {

            var whole    = SaslDowngradeProtection.Expected(SCRAMMechanism.ScramSha1,
                                                            ["PLAIN", "SCRAM-SHA-1", "SCRAM-SHA-256"]);

            var shortened = SaslDowngradeProtection.Expected(SCRAMMechanism.ScramSha1,
                                                             ["PLAIN", "SCRAM-SHA-1"]);

            Assert.That(shortened, Is.Not.EqualTo(whole));

        }

        #endregion

    }

}
