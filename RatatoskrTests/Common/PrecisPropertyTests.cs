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
    /// RFC 8264, section 8: the derived property of a code point - branch by
    /// branch.
    /// </summary>
    /// <remarks>
    /// The ladder from section 8 is not merely an enumeration of categories but
    /// an <b>order</b>, and several code points belong in more than one of them.
    /// Whoever reads it as a set instead of as a ladder gets other answers:
    ///
    /// <list type="bullet">
    ///   <item>U+0640 (ARABIC TATWEEL) is a modifier letter and thereby in
    ///         LetterDigits — the exception list, however, stands before it and
    ///         forbids it.</item>
    ///   <item>U+2163 (ROMAN NUMERAL FOUR) is Nl and thereby in
    ///         OtherLetterDigits — HasCompat stands before it.</item>
    ///   <item>U+00DF (ß) would be PVALID without the exception list, by way of
    ///         LetterDigits; the exception says the same thing, but for another
    ///         reason.</item>
    /// </list>
    ///
    /// That is why for every case it is stated <i>which branch</i> answers it. A
    /// test that checks only the result would take a ladder with swapped rungs
    /// for right, as long as the cases do not overlap.
    /// </remarks>
    [TestFixture]
    public class PrecisPropertyTests
    {

        #region TheLadderOfSection8()

        /// <summary>
        /// One case per branch, in the order of the section.
        /// </summary>
        [Test]
        public void TheLadderOfSection8()
        {

            var cases = new (UInt32 CodePoint, PrecisProperty Expected, String Branch)[]
            {
                (0x00DF, PrecisProperty.PValid,      "Exceptions: LATIN SMALL LETTER SHARP S"),
                (0x03C2, PrecisProperty.PValid,      "Exceptions: GREEK SMALL LETTER FINAL SIGMA"),
                (0x3007, PrecisProperty.PValid,      "Exceptions: IDEOGRAPHIC NUMBER ZERO"),
                (0x00B7, PrecisProperty.ContextO,    "Exceptions: MIDDLE DOT"),
                (0x0660, PrecisProperty.ContextO,    "Exceptions: ARABIC-INDIC DIGIT ZERO"),
                (0x06F9, PrecisProperty.ContextO,    "Exceptions: EXTENDED ARABIC-INDIC DIGIT NINE"),
                (0x0640, PrecisProperty.Disallowed,  "Exceptions: ARABIC TATWEEL - despite category Lm"),
                (0x07FA, PrecisProperty.Disallowed,  "Exceptions: NKO LAJANYALAN - despite category Lm"),
                (0x3031, PrecisProperty.Disallowed,  "Exceptions: VERTICAL KANA REPEAT MARK"),
                (0x0378, PrecisProperty.Unassigned,  "Unassigned: not assigned"),
                (0x0061, PrecisProperty.PValid,      "ASCII7: 'a'"),
                (0x007E, PrecisProperty.PValid,      "ASCII7: '~' - the upper bound"),
                (0x200C, PrecisProperty.ContextJ,    "JoinControl: ZERO WIDTH NON-JOINER"),
                (0x200D, PrecisProperty.ContextJ,    "JoinControl: ZERO WIDTH JOINER"),
                (0x1100, PrecisProperty.Disallowed,  "OldHangulJamo: HANGUL CHOSEONG KIYEOK (L)"),
                (0x11A8, PrecisProperty.Disallowed,  "OldHangulJamo: HANGUL JONGSEONG KIYEOK (T)"),
                (0x00AD, PrecisProperty.Disallowed,  "PrecisIgnorableProperties: SOFT HYPHEN"),
                (0xFDD0, PrecisProperty.Disallowed,  "PrecisIgnorableProperties: non-characters"),
                (0xFFFE, PrecisProperty.Disallowed,  "PrecisIgnorableProperties: non-characters at the end of a block"),
                (0x3164, PrecisProperty.Disallowed,  "PrecisIgnorableProperties: HANGUL FILLER - despite category Lo"),
                (0x0009, PrecisProperty.Disallowed,  "Controls: tabulator"),
                (0x007F, PrecisProperty.Disallowed,  "Controls: DEL - ASCII7 ends at 7E"),
                (0x2163, PrecisProperty.FreePValid,  "HasCompat: ROMAN NUMERAL FOUR - decomposes into 'IV'"),
                (0xFB01, PrecisProperty.FreePValid,  "HasCompat: ligature fi"),
                (0x00E9, PrecisProperty.PValid,      "LetterDigits: é"),
                (0x05D0, PrecisProperty.PValid,      "LetterDigits: ALEF"),
                (0x0488, PrecisProperty.FreePValid,  "OtherLetterDigits: Me"),
                (0x16EE, PrecisProperty.FreePValid,  "OtherLetterDigits: RUNIC ARLAUG SYMBOL (Nl)"),
                (0x0020, PrecisProperty.FreePValid,  "Spaces: the space is no ASCII7"),
                (0x00A0, PrecisProperty.FreePValid,  "Spaces: NO-BREAK SPACE"),
                (0x265A, PrecisProperty.FreePValid,  "Symbols: BLACK CHESS KING"),
                (0x2E00, PrecisProperty.FreePValid,  "Punctuation: RIGHT ANGLE SUBSTITUTION MARKER"),
                (0xE000, PrecisProperty.Disallowed,  "Rest: Private Use"),
                (0x0600, PrecisProperty.Disallowed,  "Rest: ARABIC NUMBER SIGN (Cf, not ignorable)")
            };

            Assert.Multiple(() =>
            {
                foreach (var (codePoint, expected, branch) in cases)
                    Assert.That(Precis.DerivedProperty(codePoint), Is.EqualTo(expected),
                                $"U+{codePoint:X4} - {branch}");
            });

        }

        #endregion

        #region TheTwoClasses()

        /// <summary>
        /// IdentifierClass (RFC 8264, section 4.2) takes only PVALID,
        /// FreeformClass (section 4.3) takes FREE_PVAL as well.
        /// </summary>
        /// <remarks>
        /// That is the whole difference between the two classes, and it is the
        /// reason why a resource part may carry a space and a chess symbol and a
        /// local part may not.
        /// </remarks>
        [Test]
        public void TheTwoClasses()
        {

            Assert.Multiple(() =>
            {

                Assert.That(Precis.IsIdentifierClass(0x0061), Is.True,  "'a' belongs in both classes.");
                Assert.That(Precis.IsFreeformClass  (0x0061), Is.True);

                Assert.That(Precis.IsIdentifierClass(0x265A), Is.False, "A symbol is no identifier character.");
                Assert.That(Precis.IsFreeformClass  (0x265A), Is.True);

                Assert.That(Precis.IsIdentifierClass(0x0020), Is.False, "A space is no identifier character.");
                Assert.That(Precis.IsFreeformClass  (0x0020), Is.True);

                Assert.That(Precis.IsIdentifierClass(0x0640), Is.False, "The tatweel is in no class.");
                Assert.That(Precis.IsFreeformClass  (0x0640), Is.False);

                Assert.That(Precis.IsIdentifierClass(0x0378), Is.False, "What is unassigned is in no class.");
                Assert.That(Precis.IsFreeformClass  (0x0378), Is.False);

            });

        }

        #endregion

        #region Helper functions

        /// <summary>
        /// Is the rule satisfied for the first occurrence of this code point?
        /// </summary>
        private static Boolean Rule(String Text, UInt32 CodePoint)
        {

            var points = Text.EnumerateRunes().Select(r => (UInt32) r.Value).ToArray();
            var position = Array.IndexOf(points, CodePoint);

            Assert.That(position, Is.GreaterThanOrEqualTo(0),
                        $"U+{CodePoint:X4} does not occur in '{Text}' at all.");

            return Precis.ContextRuleSatisfied(points, position);

        }

        #endregion


        #region TheJoinersNeedAReasonToBeThere()

        /// <summary>
        /// RFC 5892, appendices A.1 and A.2: the two joiners are permitted where
        /// they bring something about - and only there.
        /// </summary>
        /// <remarks>
        /// Both are invisible. In an address an invisible character is first of
        /// all a way to make two different addresses look the same. The rules
        /// name the places where they are needed all the same:
        ///
        /// <list type="bullet">
        ///   <item>After a virama (A.1 and A.2): the virama removes the built-in
        ///         vowel, the joiner decides about the ligature.</item>
        ///   <item>Between two joining letters (A.1 only): there the non-joiner
        ///         prevents a joining that would otherwise happen.</item>
        /// </list>
        /// </remarks>
        [Test]
        public void TheJoinersNeedAReasonToBeThere()
        {

            const String Zwnj    = "‌";
            const String Zwj     = "‍";
            const String Virama  = "्";  // DEVANAGARI SIGN VIRAMA
            const String Ka      = "क";  // DEVANAGARI LETTER KA

            // Arabic: BEH and YEH join on both sides (Joining_Type D).
            const String Beh     = "ب";
            const String Yeh     = "ي";
            const String Shadda  = "ّ";  // ARABIC SHADDA, Joining_Type T

            Assert.Multiple(() =>
            {

                Assert.That(Rule(Ka + Virama + Zwnj + Ka, 0x200C), Is.True,
                            "A.1, first route: after a virama.");

                Assert.That(Rule(Ka + Virama + Zwj + Ka, 0x200D), Is.True,
                            "A.2: after a virama.");

                Assert.That(Rule(Beh + Zwnj + Yeh, 0x200C), Is.True,
                            "A.1, second route: between two joining letters.");

                Assert.That(Rule("a" + Zwnj + "b", 0x200C), Is.False,
                            "Between two Latin letters nothing joins.");

                Assert.That(Rule("a" + Zwj + "b", 0x200D), Is.False,
                            "For the joiner the second route does not exist at all.");

                Assert.That(Rule(Beh + Zwj + Yeh, 0x200D), Is.False,
                            "Not between joining letters either.");

                // The three cases that show that both sides and the transparent
                // characters between them are really checked. Without them it
                // would suffice to look at one of the two sides: the cases above
                // each already founder on the other one.
                Assert.That(Rule("a" + Zwnj + Yeh, 0x200C), Is.False,
                            "On the left there is no joining letter.");

                Assert.That(Rule(Beh + Zwnj + "b", 0x200C), Is.False,
                            "On the right there is none.");

                Assert.That(Rule(Beh + Shadda + Zwnj + Yeh, 0x200C), Is.True,
                            "A transparent character in between does not count.");

            });

        }

        #endregion

        #region TheMiddleDotBelongsBetweenTwoLs()

        /// <summary>
        /// RFC 5892, appendix A.3: the middle dot stands between two <c>l</c> -
        /// the Catalan <c>l·l</c> - and nowhere else.
        /// </summary>
        [Test]
        public void TheMiddleDotBelongsBetweenTwoLs()
        {

            Assert.Multiple(() =>
            {
                Assert.That(Rule("col·la",  0x00B7), Is.True);
                Assert.That(Rule("co·lla",  0x00B7), Is.False, "no 'l' before it");
                Assert.That(Rule("coll·a",  0x00B7), Is.False, "no 'l' after it");
                Assert.That(Rule("·la",     0x00B7), Is.False, "at the start");
            });

        }

        #endregion

        #region TheGreekAndHebrewMarks()

        /// <summary>
        /// RFC 5892, appendices A.4 to A.6: the keraia stands before Greek
        /// script, geresh and gershayim stand after Hebrew.
        /// </summary>
        /// <remarks>
        /// The three characters belong to their script like a letter. Outside it
        /// they are punctuation in an address - and punctuation is the tool one
        /// makes an address resemble another with.
        /// </remarks>
        [Test]
        public void TheGreekAndHebrewMarks()
        {

            const String Keraia     = "͵";
            const String Geresh     = "׳";
            const String Gershayim  = "״";

            Assert.Multiple(() =>
            {

                Assert.That(Rule(Keraia + "α", 0x0375), Is.True,  "A.4: before Greek");
                Assert.That(Rule(Keraia + "a", 0x0375), Is.False, "A.4: before Latin");
                Assert.That(Rule("α" + Keraia, 0x0375), Is.False, "A.4: at the end");

                Assert.That(Rule("א" + Geresh,    0x05F3), Is.True,  "A.5: after Hebrew");
                Assert.That(Rule("a" + Geresh,    0x05F3), Is.False, "A.5: after Latin");

                Assert.That(Rule("א" + Gershayim, 0x05F4), Is.True,  "A.6: after Hebrew");
                Assert.That(Rule(Gershayim + "א", 0x05F4), Is.False, "A.6: at the start");

            });

        }

        #endregion

        #region TheKatakanaMiddleDotNeedsJapanese()

        /// <summary>
        /// RFC 5892, appendix A.7: the Katakana middle dot is permitted when
        /// Japanese script stands somewhere in the string.
        /// </summary>
        /// <remarks>
        /// This rule is the only one of the seven that looks not at the
        /// neighbours but at the whole. In Japanese text the middle dot
        /// separates the parts of a foreign word; without Japanese characters it
        /// separates nothing.
        /// </remarks>
        [Test]
        public void TheKatakanaMiddleDotNeedsJapanese()
        {

            const String MiddleDot = "・";

            Assert.Multiple(() =>
            {
                Assert.That(Rule("ア" + MiddleDot + "ア", 0x30FB), Is.True,  "Katakana");
                Assert.That(Rule("あ" + MiddleDot + "あ", 0x30FB), Is.True,  "Hiragana");
                Assert.That(Rule("中" + MiddleDot + "中", 0x30FB), Is.True,  "Han");
                Assert.That(Rule("a"  + MiddleDot + "b",  0x30FB), Is.False, "no Japanese character");
            });

        }

        #endregion

        #region TheArabicIndicDigitsRule()

        /// <summary>
        /// RFC 5892, appendices A.8 and A.9: the two sets of Arabic-Indic digits
        /// must not stand in the same string.
        /// </summary>
        /// <remarks>
        /// They resemble each other and mean the same. Two accounts that differ
        /// only in that would be the same account for the reader - hence either
        /// the one set or the other.
        /// </remarks>
        [Test]
        public void TheArabicIndicDigitsRule()
        {

            const String ArabicIndic = "٠١٢";
            const String Extended       = "۰۱۲";

            Assert.Multiple(() =>
            {

                Assert.That(Rule(ArabicIndic, 0x0660), Is.True,
                            "One set on its own is permitted.");

                Assert.That(Rule(Extended, 0x06F0), Is.True);

                Assert.That(Rule(ArabicIndic + Extended, 0x0660), Is.False,
                            "Mixed it is not.");

                Assert.That(Rule(ArabicIndic + Extended, 0x06F0), Is.False);

            });

        }

        #endregion

        #region WhatIsNotContextual()

        /// <summary>
        /// What is not context-dependent at all gets no permission here.
        /// </summary>
        /// <remarks>
        /// This function answers only the question "may this special case stand
        /// here". An ordinary letter is none - for it the ladder decides, and a
        /// <c>true</c> at this place would be a second, quieter permission
        /// beside it.
        /// </remarks>
        [Test]
        public void WhatIsNotContextual()
        {

            Assert.Multiple(() =>
            {
                Assert.That(Rule("abc", 0x0061), Is.False, "'a'");
                Assert.That(Rule("♚",   0x265A), Is.False, "a symbol");
            });

        }

        #endregion

    }

}
