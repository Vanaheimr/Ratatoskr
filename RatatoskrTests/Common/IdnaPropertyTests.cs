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
    /// RFC 5892, section 1: the derived property of a code point for IDNA2008 -
    /// branch by branch.
    /// </summary>
    /// <remarks>
    /// <b>The same building blocks, another ladder, other answers.</b> The
    /// comparison with <see cref="PrecisPropertyTests"/> is the content of this
    /// collection:
    ///
    /// <list type="bullet">
    ///   <item><c>_</c> is permitted in a local part (ASCII7) and not in a
    ///         domain label (LDH knows only hyphen, digits and lower-case
    ///         letters).</item>
    ///   <item><c>A</c> likewise: in a domain name there are no capital
    ///         letters, they are unstable per section 2.2.</item>
    ///   <item>A symbol is permitted in a resource part (FreeformClass) and not
    ///         in a label - the IDNA ladder ends without a catching branch for
    ///         symbols and punctuation.</item>
    ///   <item>U+2163 is FREE_PVAL for PRECIS (HasCompat), DISALLOWED for IDNA
    ///         (Unstable).</item>
    /// </list>
    /// </remarks>
    [TestFixture]
    public class IdnaPropertyTests
    {

        #region TheLadderOfRfc5892()

        /// <summary>
        /// One case per branch, in the order of the section.
        /// </summary>
        [Test]
        public void TheLadderOfRfc5892()
        {

            var cases = new (UInt32 CodePoint, IdnaProperty Expected, String Branch)[]
            {
                (0x00DF, IdnaProperty.PValid,      "Exceptions: ß - the most famous case of IDNA2008"),
                (0x03C2, IdnaProperty.PValid,      "Exceptions: final sigma"),
                (0x00B7, IdnaProperty.ContextO,    "Exceptions: MIDDLE DOT"),
                (0x0660, IdnaProperty.ContextO,    "Exceptions: ARABIC-INDIC DIGIT ZERO"),
                (0x0640, IdnaProperty.Disallowed,  "Exceptions: ARABIC TATWEEL"),
                (0x0378, IdnaProperty.Unassigned,  "Unassigned"),
                (0x0061, IdnaProperty.PValid,      "LDH: 'a'"),
                (0x0039, IdnaProperty.PValid,      "LDH: '9'"),
                (0x002D, IdnaProperty.PValid,      "LDH: the hyphen"),
                (0x0041, IdnaProperty.Disallowed,  "Unstable: 'A' - domain names are written in lower case"),
                (0x005F, IdnaProperty.Disallowed,  "Rest: '_' is no LDH"),
                (0x002B, IdnaProperty.Disallowed,  "Rest: '+' is no LDH"),
                (0x200C, IdnaProperty.ContextJ,    "JoinControl: ZWNJ"),
                (0x2163, IdnaProperty.Disallowed,  "Unstable: ROMAN NUMERAL FOUR"),
                (0x0130, IdnaProperty.Disallowed,  "Unstable: LATIN CAPITAL LETTER I WITH DOT ABOVE"),
                (0x00AD, IdnaProperty.Disallowed,  "IgnorableProperties: SOFT HYPHEN"),
                (0x3164, IdnaProperty.Disallowed,  "IgnorableProperties: HANGUL FILLER - despite category Lo"),
                (0xFE00, IdnaProperty.Disallowed,  "IgnorableProperties: VARIATION SELECTOR-1 - despite category Mn"),
                (0x180B, IdnaProperty.Disallowed,  "IgnorableProperties: MONGOLIAN FREE VARIATION SELECTOR - despite category Mn"),
                (0x0020, IdnaProperty.Disallowed,  "IgnorableProperties: White_Space"),
                (0xFDD0, IdnaProperty.Disallowed,  "IgnorableProperties: non-characters"),
                (0x20D0, IdnaProperty.Disallowed,  "IgnorableBlocks: Combining Marks for Symbols - despite category Mn"),
                (0x1D165, IdnaProperty.Disallowed, "IgnorableBlocks: Musical Symbols - despite category Mc"),
                (0x1100, IdnaProperty.Disallowed,  "OldHangulJamo"),
                (0x00E9, IdnaProperty.PValid,      "LetterDigits: é"),
                (0x05D0, IdnaProperty.PValid,      "LetterDigits: ALEF"),
                (0x4E2D, IdnaProperty.PValid,      "LetterDigits: 中"),
                (0x265A, IdnaProperty.Disallowed,  "Rest: a symbol, and the ladder has no catching branch"),
                (0x002E, IdnaProperty.Disallowed,  "Rest: the dot separates labels, it does not stand in one")
            };

            Assert.Multiple(() =>
            {
                foreach (var (codePoint, expected, branch) in cases)
                    Assert.That(Idna.DerivedProperty(codePoint), Is.EqualTo(expected),
                                $"U+{codePoint:X4} - {branch}");
            });

        }

        #endregion

        #region WhereTheTwoLaddersDisagree()

        /// <summary>
        /// The same code points, two prescriptions, two answers.
        /// </summary>
        /// <remarks>
        /// This table is the reason why the two ladders stay separate. If one
        /// put them together, all four rows would have to become special cases -
        /// and special cases are what one cannot look up later.
        /// </remarks>
        [Test]
        public void WhereTheTwoLaddersDisagree()
        {

            Assert.Multiple(() =>
            {

                Assert.That(Precis.IsIdentifierClass(0x005F),  Is.True,
                            "The underscore belongs in a local part ...");
                Assert.That(Idna.DerivedProperty(0x005F),      Is.EqualTo(IdnaProperty.Disallowed),
                            "... and not in a domain label.");

                Assert.That(Precis.IsFreeformClass(0x265A),    Is.True,
                            "A symbol belongs in a resource part ...");
                Assert.That(Idna.DerivedProperty(0x265A),      Is.EqualTo(IdnaProperty.Disallowed),
                            "... and not in a domain label.");

                Assert.That(Precis.IsFreeformClass(0x2163),    Is.True,
                            "The Roman four is a freeform character for PRECIS ...");
                Assert.That(Idna.DerivedProperty(0x2163),      Is.EqualTo(IdnaProperty.Disallowed),
                            "... and unstable for IDNA.");

                Assert.That(Precis.IsIdentifierClass(0x0041),  Is.True,
                            "An 'A' may stand in a local part (it is lower-cased) ...");
                Assert.That(Idna.DerivedProperty(0x0041),      Is.EqualTo(IdnaProperty.Disallowed),
                            "... and is impermissible as the code point of a label.");

            });

        }

        #endregion

    }

}
