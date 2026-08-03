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
    /// JIDs per RFC 7622, against the two example tables from section 3.5.
    /// </summary>
    /// <remarks>
    /// Fifteen strings that are JIDs, and eight that are not. The section is
    /// built as a touchstone: almost every row hits exactly one rule, and
    /// several of them are ones nobody would come up with on their own - the
    /// splitting order, say, or that a character with a compatibility
    /// decomposition has no business in a local part.
    ///
    /// As in the SASLprep collection, every special character stands there as a
    /// named constant instead of as a literal.
    /// </remarks>
    [TestFixture]
    public class JidFormatTests
    {

        #region Data

        private const String SharpS       = "ß";   // LATIN SMALL LETTER SHARP S
        private const String Pi           = "π";   // GREEK SMALL LETTER PI
        private const String CapitalSigma = "Σ";   // GREEK CAPITAL LETTER SIGMA
        private const String SmallSigma   = "σ";   // GREEK SMALL LETTER SIGMA
        private const String FinalSigma   = "ς";   // GREEK SMALL LETTER FINAL SIGMA
        private const String ChessKing    = "♚";   // BLACK CHESS KING
        private const String RomanFour    = "Ⅳ";   // ROMAN NUMERAL FOUR
        private const String LigatureFi   = "ﬁ";   // LATIN SMALL LIGATURE FI

        #endregion

        #region Rfc7622_Table1_AreAllJids()

        /// <summary>
        /// Table 1: fifteen valid JIDs.
        /// </summary>
        [Test]
        public void Rfc7622_Table1_AreAllJids()
        {

            var valid = new (Int32 Number, String Jid, String Why)[]
            {
                ( 1, "juliet@example.com",              "bare JID"),
                ( 2, "juliet@example.com/foo",          "full JID"),
                ( 3, "juliet@example.com/foo bar",      "space in the resource part"),
                ( 4, "juliet@example.com/foo@bar",      "at sign in the resource part"),
                ( 5, "foo\\20bar@example.com",          "XEP-0106 escaping in the local part"),
                ( 6, "fussball@example.com",            "bare JID"),
                ( 7, "fu" + SharpS + "ball@example.com","sharp s in the local part"),
                ( 8, Pi + "@example.com",               "local part of a Greek pi"),
                ( 9, CapitalSigma + "@example.com/foo", "local part of a capital sigma"),
                (10, SmallSigma + "@example.com/foo",   "local part of a small sigma"),
                (11, FinalSigma + "@example.com/foo",   "local part of a final sigma"),
                (12, "king@example.com/" + ChessKing,   "symbol in the resource part"),
                (13, "example.com",                     "only a domain part"),
                (14, "example.com/foobar",              "domain part and resource part"),
                (15, "a.example.com/b@example.net",     "resource part with an at sign")
            };

            Assert.Multiple(() =>
            {
                foreach (var (number, jid, why) in valid)
                    Assert.That(JidUtilities.TryParse(jid, out _), Is.True,
                                $"Example {number} ({why}) has to be a JID.");
            });

        }

        #endregion

        #region Rfc7622_Table2_AreNoJids()

        /// <summary>
        /// Table 2: strings that are no JIDs.
        /// </summary>
        /// <remarks>
        /// Example 18 is missing here on purpose and is treated on its own just
        /// below.
        /// </remarks>
        [Test]
        public void Rfc7622_Table2_AreNoJids()
        {

            var invalid = new (Int32 Number, String Jid, String Why)[]
            {
                (16, "\"juliet\"@example.com",           "quotation marks in the local part"),
                (17, "foo bar@example.com",              "space in the local part"),
                (19, "@example.com/",                    "local and resource part empty"),
                (20, "henry" + RomanFour + "@example.com", "Roman four in the local part"),
                (21, ChessKing + "@example.com",         "symbol in the local part"),
                (22, "juliet@",                          "local part without a domain part"),
                (23, "/foobar",                          "resource part without a domain part")
            };

            Assert.Multiple(() =>
            {
                foreach (var (number, jid, why) in invalid)
                    Assert.That(JidUtilities.TryParse(jid, out _), Is.False,
                                $"Example {number} ({why}) must not be a JID.");
            });

        }

        #endregion

        #region Rfc7622_Example18_LeadingSpaceInResource_IsAccepted()

        /// <summary>
        /// Example 18 - a leading space in the resource part - is
        /// <b>accepted</b> here, contrary to the table.
        /// </summary>
        /// <remarks>
        /// That is a deliberate deviation and no gap. RFC 7622 lists the string
        /// as a non-JID, but the rule for it is missing: the resource part is an
        /// instance of the OpaqueString profile, and that permits spaces
        /// expressly (RFC 8265, section 4.2.2, rule 2 merely maps spaces outside
        /// ASCII onto U+0020). A prohibition of leading spaces stands neither
        /// there nor anywhere else in the rules.
        ///
        /// For a router, accepting is besides the more cautious choice: to
        /// refuse an address other servers hold for valid loses messages - and
        /// ours at that.
        ///
        /// The test stands here so that the deviation has a place where it shows
        /// if somebody later decides it differently.
        /// </remarks>
        [Test]
        public void Rfc7622_Example18_LeadingSpaceInResource_IsAccepted()
        {

            Assert.That(JidUtilities.TryParse("juliet@example.com/ foo", out var parts), Is.True);
            Assert.That(parts.Resourcepart, Is.EqualTo(" foo"));

        }

        #endregion

        #region CompatibilityCharacters_AreRefusedInLocalpart()

        /// <summary>
        /// Characters with a compatibility decomposition do not belong in a
        /// local part (HasCompat, RFC 8264, section 9.6).
        /// </summary>
        /// <remarks>
        /// Example 20 from RFC 7622 - the Roman four - already falls over the
        /// category: it is a number-as-letter (Nl) and thereby no letter in the
        /// sense of the IdentifierClass anyway. The HasCompat rule stays
        /// unchecked in the process.
        ///
        /// The ligature ﬁ hits it precisely, by contrast: it is a lower-case
        /// letter, so it comes through the category check, and decomposes
        /// compatibly into "fi". Without the rule <c>ﬁle@example.com</c> and
        /// <c>file@example.com</c> would be two accounts that are the same to
        /// the eye - precisely the confusion PRECIS is built against.
        /// </remarks>
        [Test]
        public void CompatibilityCharacters_AreRefusedInLocalpart()
        {

            Assert.Multiple(() =>
            {

                Assert.That(JidUtilities.TryParse(LigatureFi + "le@example.com", out _),
                            Is.False,
                            "The ligature has a compatibility decomposition.");

                Assert.That(JidUtilities.TryParse("file@example.com", out _),
                            Is.True,
                            "The written-out version is of course permitted.");

                // In the resource part it is permitted, by contrast: the
                // FreeformClass does not exclude HasCompat.
                Assert.That(JidUtilities.TryParse("juliet@example.com/" + LigatureFi, out _),
                            Is.True);

            });

        }

        #endregion

        #region EmptyParts_AreRefusedEachOnTheirOwn()

        /// <summary>
        /// Local and resource part must not be empty when their separator stands
        /// there - each on its own.
        /// </summary>
        /// <remarks>
        /// Example 19 from the table (<c>@example.com/</c>) has both errors at
        /// once and therefore proves neither of them: the first check that
        /// strikes suffices, and the second stays unrun.
        /// </remarks>
        [Test]
        public void EmptyParts_AreRefusedEachOnTheirOwn()
        {

            Assert.Multiple(() =>
            {

                Assert.That(JidUtilities.TryParse("juliet@example.com/", out _), Is.False,
                            "A slash without a resource behind it.");

                Assert.That(JidUtilities.TryParse("@example.com", out _), Is.False,
                            "An at sign without a local part in front of it.");

            });

        }

        #endregion

        #region TheSplitOrderMatters()

        /// <summary>
        /// Split at the <c>/</c> first, then at the <c>@</c> - example 15.
        /// </summary>
        /// <remarks>
        /// The other way round, <c>a.example.com/b@example.net</c> would yield a
        /// local part <c>a.example.com/b</c>, and that would contain a <c>/</c>,
        /// which is excluded there. A valid JID would become an invalid one.
        /// </remarks>
        [Test]
        public void TheSplitOrderMatters()
        {

            var example15 = JidUtilities.Parse("a.example.com/b@example.net");

            // RFC 7622, section 3.4: a second slash belongs to the resource -
            // JIDs are not hierarchical. The split happens at the *first*, not
            // at the last.
            var twoSlashes = JidUtilities.Parse("juliet@example.com/foo/bar");

            Assert.Multiple(() =>
            {

                Assert.That(example15.Localpart,    Is.Null);
                Assert.That(example15.Domainpart,   Is.EqualTo("a.example.com"));
                Assert.That(example15.Resourcepart, Is.EqualTo("b@example.net"));

                Assert.That(twoSlashes.Localpart,    Is.EqualTo("juliet"));
                Assert.That(twoSlashes.Domainpart,   Is.EqualTo("example.com"));
                Assert.That(twoSlashes.Resourcepart, Is.EqualTo("foo/bar"),
                            "The second slash belongs in the resource.");

            });

        }

        #endregion

        #region TheResourcepartKeepsItsCase()

        /// <summary>
        /// The core: local and domain part are independent of the case, the
        /// resource part is not (RFC 7622, section 3.4).
        /// </summary>
        [Test]
        public void TheResourcepartKeepsItsCase()
        {

            var parts = JidUtilities.Parse("Juliet@Example.COM/Balcony");

            Assert.Multiple(() =>
            {

                Assert.That(parts.Localpart,    Is.EqualTo("juliet"));
                Assert.That(parts.Domainpart,   Is.EqualTo("example.com"));
                Assert.That(parts.Resourcepart, Is.EqualTo("Balcony"),
                            "The resource part must not be lower-cased.");

                Assert.That(JidUtilities.AreEqual("juliet@example.com/Balcony",
                                                  "JULIET@EXAMPLE.COM/Balcony"),
                            Is.True,
                            "Local and domain part without regard for the case.");

                Assert.That(JidUtilities.AreEqual("juliet@example.com/Balcony",
                                                  "juliet@example.com/balcony"),
                            Is.False,
                            "Two resources that differ only in the case " +
                            "are two devices.");

            });

        }

        #endregion

        #region Rfc7622_CaseMappingNotes()

        /// <summary>
        /// The notes on examples 6/7 and 9/10/11.
        /// </summary>
        /// <remarks>
        /// Two subtleties the text especially highlights. First: sharp s and
        /// "ss" stay different - the rule is lower-casing (toLowerCase), not
        /// case folding, which would make <c>ss</c> out of it. Second: capital
        /// sigma becomes small, whereas the final sigma stays itself.
        /// </remarks>
        [Test]
        public void Rfc7622_CaseMappingNotes()
        {

            Assert.Multiple(() =>
            {

                Assert.That(JidUtilities.AreEqual("fu" + SharpS + "ball@example.com",
                                                  "fussball@example.com"),
                            Is.False,
                            "Sharp s and ss are two different local parts.");

                Assert.That(JidUtilities.AreEqual(CapitalSigma + "@example.com",
                                                  SmallSigma   + "@example.com"),
                            Is.True,
                            "Capital and small sigma fall together.");

                Assert.That(JidUtilities.AreEqual(FinalSigma + "@example.com",
                                                  SmallSigma + "@example.com"),
                            Is.False,
                            "The final sigma stays a character of its own.");

            });

        }

        #endregion

        #region PartsLongerThan1023Octets_AreRefused()

        /// <summary>
        /// RFC 7622: every part is limited to 1023 octets - measured after the
        /// preparation and on the UTF-8 encoding.
        /// </summary>
        /// <remarks>
        /// The difference between characters and octets is no subtlety here: a
        /// local part of 600 Greek letters has 600 characters and 1200 octets.
        /// </remarks>
        [Test]
        public void PartsLongerThan1023Octets_AreRefused()
        {

            Assert.Multiple(() =>
            {

                Assert.That(JidUtilities.TryParse(new String('a', 1023) + "@example.com", out _),
                            Is.True,
                            "1023 octets are permitted.");

                Assert.That(JidUtilities.TryParse(new String('a', 1024) + "@example.com", out _),
                            Is.False);

                // 600 characters, but 1200 octets.
                Assert.That(JidUtilities.TryParse(String.Concat(Enumerable.Repeat(Pi, 600)) +
                                                  "@example.com", out _),
                            Is.False,
                            "What is measured are octets, not characters.");

            });

        }

        #endregion

        #region Bare_NeverThrows()

        /// <summary>
        /// <c>Bare</c> runs over everything that comes from the wire and must
        /// therefore founder on no input.
        /// </summary>
        /// <remarks>
        /// An exception in the middle of the stanza handling would be the worst
        /// of all outcomes: a sender who sends nonsense would thereby bring the
        /// connection down. What is unusable shall match nothing, not stop
        /// everything.
        /// </remarks>
        [Test]
        public void Bare_NeverThrows()
        {

            var nonsense = new[] { "", "@", "/", "@/", "juliet@", "/foobar",
                                 "\"juliet\"@example.com", "a@b@c" };

            Assert.Multiple(() =>
            {
                foreach (var input in nonsense)
                    Assert.That(() => JidUtilities.Bare(input), Throws.Nothing,
                                $"Stumbled over: '{input}'");
            });

        }

        #endregion

    }

}
