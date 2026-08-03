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
    /// What the derived properties change at the JID - the cases where the
    /// approximation differs from RFC 8264.
    /// </summary>
    /// <remarks>
    /// The old check asked for the Unicode category and the compatibility
    /// decomposition. It hit the examples from RFC 7622, and those still pass -
    /// <see cref="JidFormatTests"/> keeps both tables. Here stand the cases it
    /// did <b>not</b> hit: every single one would have gone through or been
    /// refused before, and wrongly at that.
    /// </remarks>
    [TestFixture]
    public class JidClassMembershipTests
    {

        #region Data

        private const String Tatweel        = "ـ";  // ARABIC TATWEEL
        private const String NkoLajanyalan  = "ߺ";  // NKO LAJANYALAN
        private const String MiddleDot      = "·";  // MIDDLE DOT
        private const String HangulChoseong = "ᄀ";  // HANGUL CHOSEONG KIYEOK
        private const String SoftHyphen     = "­";  // SOFT HYPHEN
        private const String ArabicIndic    = "٠١";
        private const String ExtArabicIndic = "۰۱";
        private const String ChessKing      = "♚";  // BLACK CHESS KING

        private static Boolean IsJid(String jid)
            => JidUtilities.TryParse(jid, out _);

        #endregion


        #region ExceptionsBeatTheCategory()

        /// <summary>
        /// The exception list stands before the category: two modifier letters
        /// no local part may carry.
        /// </summary>
        /// <remarks>
        /// U+0640 and U+07FA are letters by their category (Lm) and therefore
        /// came through. But they are not: the tatweel is an elongation stroke
        /// that can be inserted anywhere and any number of times without meaning
        /// anything. Out of one account arbitrarily many are thereby made, all
        /// of which look the same.
        /// </remarks>
        [Test]
        public void ExceptionsBeatTheCategory()
        {

            Assert.Multiple(() =>
            {
                Assert.That(IsJid($"ju{Tatweel}liet@example.com"),       Is.False, "ARABIC TATWEEL");
                Assert.That(IsJid($"ju{NkoLajanyalan}liet@example.com"), Is.False, "NKO LAJANYALAN");
            });

        }

        #endregion

        #region TheDigitsOfOneKind_AreAllowed()

        /// <summary>
        /// Arabic-Indic digits are permitted context-dependently - on their own
        /// yes, mixed no (RFC 5892, appendices A.8 and A.9).
        /// </summary>
        [Test]
        public void TheDigitsOfOneKind_AreAllowed()
        {

            Assert.Multiple(() =>
            {

                Assert.That(IsJid($"{ArabicIndic}@example.com"),    Is.True,
                            "A digit series on its own is a valid local part.");

                Assert.That(IsJid($"{ExtArabicIndic}@example.com"), Is.True);

                Assert.That(IsJid($"{ArabicIndic}{ExtArabicIndic}@example.com"), Is.False,
                            "Both series beside each other look the same and mean the same.");

            });

        }

        #endregion

        #region TheContextualOnesDependOnTheirNeighbours()

        /// <summary>
        /// A context-dependent code point hangs on its surroundings - the middle
        /// dot belongs between two <c>l</c> (RFC 5892, appendix A.3).
        /// </summary>
        /// <remarks>
        /// <c>col·la</c> is a Catalan word and a valid local part;
        /// <c>co·lla</c> is the same set of characters in another order and
        /// none. That the two do <b>not</b> have the same result is the whole
        /// content of "context-dependent".
        /// </remarks>
        [Test]
        public void TheContextualOnesDependOnTheirNeighbours()
        {

            Assert.Multiple(() =>
            {
                Assert.That(IsJid($"col{MiddleDot}la@example.com"), Is.True);
                Assert.That(IsJid($"co{MiddleDot}lla@example.com"), Is.False);
            });

        }

        #endregion

        #region TheResourcepartIsFreeformNotAnything()

        /// <summary>
        /// The resource part takes the FreeformClass - symbols and spaces yes,
        /// old Hangul jamo and invisible characters no.
        /// </summary>
        /// <remarks>
        /// U+1100 is a letter (Lo) and therefore came through. RFC 8264,
        /// section 9.9 excludes the old jamo: they combine into syllables that
        /// exist ready-made as code points of their own - two spellings for the
        /// same word, and no normalisation clears that up.
        /// </remarks>
        [Test]
        public void TheResourcepartIsFreeformNotAnything()
        {

            Assert.Multiple(() =>
            {

                Assert.That(IsJid($"juliet@example.com/{ChessKing}"),      Is.True,
                            "A symbol belongs to the FreeformClass.");

                Assert.That(IsJid("juliet@example.com/my device"),        Is.True,
                            "A space likewise.");

                Assert.That(IsJid($"juliet@example.com/{HangulChoseong}"), Is.False,
                            "An old Hangul jamo does not.");

                Assert.That(IsJid($"juliet@example.com/a{SoftHyphen}b"),   Is.False,
                            "An invisible character does not.");

                Assert.That(IsJid($"juliet@example.com/a{Tatweel}b"),      Is.False,
                            "And the exception list holds in both classes.");

            });

        }

        #endregion

        #region TheSymbolStaysOutOfTheLocalpart()

        /// <summary>
        /// The counter-check: what the resource part carries, the local part
        /// does not.
        /// </summary>
        /// <remarks>
        /// Without it "both parts take the FreeformClass" would be a passing
        /// solution - and the difference between the two classes would vanish
        /// without a test noticing.
        /// </remarks>
        [Test]
        public void TheSymbolStaysOutOfTheLocalpart()
        {

            Assert.Multiple(() =>
            {
                Assert.That(IsJid($"{ChessKing}@example.com"),   Is.False, "symbol");
                Assert.That(IsJid("my device@example.com"),     Is.False, "space");
            });

        }

        #endregion

    }

}
