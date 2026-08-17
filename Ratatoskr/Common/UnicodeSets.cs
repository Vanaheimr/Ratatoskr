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

using System.Globalization;
using System.Text;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// The sets from RFC 5892, section 2 - the shared substructure of
/// <see cref="Precis"/> (RFC 8264) and <see cref="Idna"/> (RFC 5892).
/// </summary>
/// <remarks>
/// <b>Both specifications build their ladder from the same blocks and arrive at
/// different results.</b> The blocks therefore belong in one place, the ladders
/// do not: an underscore is permitted in a localpart (ASCII7) and not in a
/// domain label (LDH); a symbol is permitted in a resourcepart (FreeformClass)
/// and not in a label. Merging the two ladders would mean translating those
/// differences into special cases - and special cases are what you cannot look
/// up again later.
///
/// <b>What .NET does not ship sits here as a range table.</b> It is named with
/// the Unicode version it came from: a copy that can go stale, but not one that
/// can be beside the point.
/// </remarks>
internal static class UnicodeSets
{

    #region Data

    /// <summary>
    /// The Unicode version the recorded ranges came from.
    /// </summary>
    internal const String UnicodeVersion = "15.1.0";

    /// <summary>
    /// The three values RFC 5892, section 2.6 can give an exception.
    /// </summary>
    internal enum ExceptionValue
    {
        PValid,
        ContextO,
        Disallowed
    }

    /// <summary>
    /// RFC 5892, section 2.6: code points treated differently from what their
    /// category would suggest.
    /// </summary>
    private static readonly Dictionary<UInt32, ExceptionValue> _exceptions = new()
    {

        // PVALID - would otherwise be DISALLOWED
        [0x00DF] = ExceptionValue.PValid,      // LATIN SMALL LETTER SHARP S
        [0x03C2] = ExceptionValue.PValid,      // GREEK SMALL LETTER FINAL SIGMA
        [0x06FD] = ExceptionValue.PValid,      // ARABIC SIGN SINDHI AMPERSAND
        [0x06FE] = ExceptionValue.PValid,      // ARABIC SIGN SINDHI POSTPOSITION MEN
        [0x0F0B] = ExceptionValue.PValid,      // TIBETAN MARK INTERSYLLABIC TSHEG
        [0x3007] = ExceptionValue.PValid,      // IDEOGRAPHIC NUMBER ZERO

        // CONTEXTO - would otherwise be DISALLOWED
        [0x00B7] = ExceptionValue.ContextO,    // MIDDLE DOT
        [0x0375] = ExceptionValue.ContextO,    // GREEK LOWER NUMERAL SIGN
        [0x05F3] = ExceptionValue.ContextO,    // HEBREW PUNCTUATION GERESH
        [0x05F4] = ExceptionValue.ContextO,    // HEBREW PUNCTUATION GERSHAYIM
        [0x30FB] = ExceptionValue.ContextO,    // KATAKANA MIDDLE DOT

        // DISALLOWED - would otherwise be PVALID
        [0x0640] = ExceptionValue.Disallowed,  // ARABIC TATWEEL
        [0x07FA] = ExceptionValue.Disallowed,  // NKO LAJANYALAN
        [0x302E] = ExceptionValue.Disallowed,  // HANGUL SINGLE DOT TONE MARK
        [0x302F] = ExceptionValue.Disallowed,  // HANGUL DOUBLE DOT TONE MARK
        [0x3031] = ExceptionValue.Disallowed,  // VERTICAL KANA REPEAT MARK
        [0x3032] = ExceptionValue.Disallowed,  // VERTICAL KANA REPEAT WITH VOICED SOUND MARK
        [0x3033] = ExceptionValue.Disallowed,  // VERTICAL KANA REPEAT MARK UPPER HALF
        [0x3034] = ExceptionValue.Disallowed,  // VERTICAL KANA REPEAT WITH VOICED SOUND MARK UPPER HALF
        [0x3035] = ExceptionValue.Disallowed,  // VERTICAL KANA REPEAT MARK LOWER HALF
        [0x303B] = ExceptionValue.Disallowed   // VERTICAL IDEOGRAPHIC ITERATION MARK

    };

    /// <summary>
    /// The first and the last Arabic-Indic digit.
    /// </summary>
    internal const UInt32 ArabicIndicZero          = 0x0660;
    internal const UInt32 ArabicIndicNine          = 0x0669;

    /// <summary>
    /// The same for the extended series.
    /// </summary>
    internal const UInt32 ExtendedArabicIndicZero  = 0x06F0;
    internal const UInt32 ExtendedArabicIndicNine  = 0x06F9;

    /// <summary>
    /// <c>Default_Ignorable_Code_Point</c> (Unicode 15.1, DerivedCoreProperties).
    /// </summary>
    private static readonly (UInt32 From, UInt32 To)[] _defaultIgnorable =
    [
        (0x00AD,  0x00AD),  (0x034F,  0x034F),  (0x061C,  0x061C),
        (0x115F,  0x1160),  (0x17B4,  0x17B5),  (0x180B,  0x180F),
        (0x200B,  0x200F),  (0x202A,  0x202E),  (0x2060,  0x206F),
        (0x3164,  0x3164),  (0xFE00,  0xFE0F),  (0xFEFF,  0xFEFF),
        (0xFFA0,  0xFFA0),  (0xFFF0,  0xFFF8),  (0x1BCA0, 0x1BCA3),
        (0x1D173, 0x1D17A), (0xE0000, 0xE0FFF)
    ];

    /// <summary>
    /// <c>Hangul_Syllable_Type</c> in {L, V, T} - the old jamo, from which
    /// syllables used to be composed (Unicode 15.1).
    /// </summary>
    private static readonly (UInt32 From, UInt32 To)[] _oldHangulJamo =
    [
        (0x1100, 0x115F),  // L
        (0x1160, 0x11A7),  // V
        (0x11A8, 0x11FF),  // T
        (0xA960, 0xA97C),  // L (Jamo Extended-A)
        (0xD7B0, 0xD7C6),  // V (Jamo Extended-B)
        (0xD7CB, 0xD7FB)   // T (Jamo Extended-B)
    ];

    /// <summary>
    /// RFC 5892, section 2.4: three blocks whose characters have no business in
    /// a domain name.
    /// </summary>
    private static readonly (UInt32 From, UInt32 To)[] _ignorableBlocks =
    [
        (0x20D0,  0x20FF),   // Combining Diacritical Marks for Symbols
        (0x1D100, 0x1D1FF),  // Musical Symbols
        (0x1D200, 0x1D24F)   // Ancient Greek Musical Notation
    ];

    #endregion


    #region Sets from RFC 5892, section 2

    /// <summary>
    /// Section 2.6: the exception list.
    /// </summary>
    internal static Boolean TryException(UInt32 CodePoint, out ExceptionValue Value)

        => _exceptions.TryGetValue(CodePoint, out Value);

    /// <summary>
    /// Section 2.6, as a range: the two digit series are CONTEXTO, although
    /// their category (Nd) would make them PVALID.
    /// </summary>
    internal static Boolean IsContextODigit(UInt32 CodePoint)

        => CodePoint is >= ArabicIndicZero         and <= ArabicIndicNine or
                        >= ExtendedArabicIndicZero and <= ExtendedArabicIndicNine;

    /// <summary>
    /// Section 2.1: <c>{Ll, Lu, Lo, Nd, Lm, Mn, Mc}</c>.
    /// </summary>
    internal static Boolean IsLetterDigits(UInt32 CodePoint)

        => Category(CodePoint) is UnicodeCategory.LowercaseLetter      or
                                  UnicodeCategory.UppercaseLetter      or
                                  UnicodeCategory.OtherLetter          or
                                  UnicodeCategory.DecimalDigitNumber   or
                                  UnicodeCategory.ModifierLetter       or
                                  UnicodeCategory.NonSpacingMark       or
                                  UnicodeCategory.SpacingCombiningMark;

    /// <summary>
    /// Section 2.2: <c>toNFKC(toCaseFold(toNFKC(cp))) != cp</c>.
    /// </summary>
    /// <remarks>
    /// <b>This says <c>ToLowerInvariant</c> instead of <c>toCaseFold</c></b>,
    /// because .NET has no case folding - and the two diverge. The case that
    /// shows it is U+0130 (I with dot above): case folding turns it into
    /// <c>i</c> + dot, <c>ToLowerInvariant</c> leaves it <b>unchanged</b>,
    /// because .NET refuses to settle the Turkish I question in the invariant
    /// culture. Going by the computation alone it would come out stable and
    /// therefore as a permitted label character, although the IANA table forbids
    /// it.
    ///
    /// Hence the second condition: <b>an uppercase or titlecase letter is never
    /// fold-stable.</b> That is precisely what case folding says - it maps onto
    /// the lowercase form. A domain label is lowercased, and a code point of
    /// category Lu or Lt belongs in none.
    /// </remarks>
    internal static Boolean IsUnstable(UInt32 CodePoint)
    {

        if (Category(CodePoint) is UnicodeCategory.UppercaseLetter or
                                   UnicodeCategory.TitlecaseLetter)
            return true;

        var character = Char.ConvertFromUtf32((Int32) CodePoint);

        var folded = character.Normalize(NormalizationForm.FormKC).
                               ToLowerInvariant().
                               Normalize(NormalizationForm.FormKC);

        return folded != character;

    }

    /// <summary>
    /// Section 2.3: <c>Default_Ignorable_Code_Point</c> or <c>White_Space</c> or
    /// <c>Noncharacter_Code_Point</c>.
    /// </summary>
    internal static Boolean IsIgnorableProperties(UInt32 CodePoint)

        => IsDefaultIgnorable(CodePoint) ||
           IsNoncharacter(CodePoint)     ||
           Rune.IsWhiteSpace(new Rune(CodePoint));

    /// <summary>
    /// Section 2.4: the three blocks.
    /// </summary>
    internal static Boolean IsIgnorableBlocks(UInt32 CodePoint)

        => InRanges(CodePoint, _ignorableBlocks);

    /// <summary>
    /// Section 2.5: <c>{002D, 0030..0039, 0061..007A}</c> - hyphen, digits,
    /// lowercase letters.
    /// </summary>
    /// <remarks>
    /// Uppercase letters are not missing by accident: they are unstable under
    /// section 2.2, and a domain label is lowercased.
    /// </remarks>
    internal static Boolean IsLdh(UInt32 CodePoint)

        => CodePoint is 0x2D or (>= 0x30 and <= 0x39) or (>= 0x61 and <= 0x7A);

    /// <summary>
    /// Section 2.8: the two joiners.
    /// </summary>
    internal static Boolean IsJoinControl(UInt32 CodePoint)

        => CodePoint is 0x200C or 0x200D;

    /// <summary>
    /// Section 2.9: the old Hangul jamo.
    /// </summary>
    internal static Boolean IsOldHangulJamo(UInt32 CodePoint)

        => InRanges(CodePoint, _oldHangulJamo);

    /// <summary>
    /// Section 2.10: unassigned - and none of the noncharacters that carry the
    /// same category.
    /// </summary>
    internal static Boolean IsUnassigned(UInt32 CodePoint)

        => Category(CodePoint) == UnicodeCategory.OtherNotAssigned &&
           !IsNoncharacter(CodePoint);

    #endregion

    #region Further sets (RFC 8264, section 9)

    /// <summary>
    /// <c>Noncharacter_Code_Point</c>: the 32 from the Arabic block and the two
    /// at the end of every plane.
    /// </summary>
    internal static Boolean IsNoncharacter(UInt32 CodePoint)

        => CodePoint is >= 0xFDD0 and <= 0xFDEF ||
           (CodePoint & 0xFFFE) == 0xFFFE;

    internal static Boolean IsDefaultIgnorable(UInt32 CodePoint)

        => InRanges(CodePoint, _defaultIgnorable);

    /// <summary>
    /// RFC 8264, section 9.17: <c>toNFKC(cp) != cp</c>.
    /// </summary>
    internal static Boolean HasCompat(UInt32 CodePoint)
    {

        var character = Char.ConvertFromUtf32((Int32) CodePoint);

        return character.Normalize(NormalizationForm.FormKC) != character;

    }

    internal static UnicodeCategory Category(UInt32 CodePoint)

        => CharUnicodeInfo.GetUnicodeCategory(Char.ConvertFromUtf32((Int32) CodePoint), 0);

    #endregion

    #region (private) InRanges(CodePoint, Ranges)

    private static Boolean InRanges(UInt32 CodePoint, (UInt32 From, UInt32 To)[] Ranges)
    {

        foreach (var (from, to) in Ranges)
            if (CodePoint >= from && CodePoint <= to)
                return true;

        return false;

    }

    #endregion

}
