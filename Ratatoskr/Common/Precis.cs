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
/// The derived property of a code point per RFC 8264, section 8.
/// </summary>
public enum PrecisProperty
{

    /// <summary>Permitted in both classes.</summary>
    PValid,

    /// <summary>
    /// Permitted in the FreeformClass only (in the RFC <c>ID_DIS or FREE_PVAL</c>).
    /// </summary>
    FreePValid,

    /// <summary>
    /// Permitted if the rule from RFC 5892, appendix A.1/A.2 is satisfied (the
    /// two joiners).
    /// </summary>
    ContextJ,

    /// <summary>
    /// Permitted if the rule from RFC 5892, appendix A.3 to A.9 is satisfied.
    /// </summary>
    ContextO,

    /// <summary>Permitted in neither class.</summary>
    Disallowed,

    /// <summary>
    /// Unassigned in the underlying Unicode version - and therefore permitted
    /// nowhere.
    /// </summary>
    Unassigned

}

/// <summary>
/// PRECIS per RFC 8264: the IdentifierClass and the FreeformClass, determined
/// from the derived properties.
/// </summary>
/// <remarks>
/// <b>The ladder from section 8 is an order, not a set.</b> Many code points
/// belong to several categories, and which one bites first decides the answer:
/// U+0640 (ARABIC TATWEEL) is a modifier letter and therefore in LetterDigits —
/// but the exception list comes before it and forbids it. U+2163 (ROMAN NUMERAL
/// FOUR) is Nl and therefore in OtherLetterDigits — HasCompat comes before it.
/// Whoever checks the categories as a set gets the wrong answer in exactly those
/// cases.
///
/// What stood here before was an approximation: category plus the question of
/// whether the code point has a compatibility decomposition. It matched the
/// examples from RFC 7622 and left the exception list, the joiners and the old
/// Hangul jamo out of it.
///
/// <b>What .NET does not know sits here as a table.</b> The runtime does not
/// provide <c>Default_Ignorable_Code_Point</c>, <c>Noncharacter_Code_Point</c>
/// or <c>Hangul_Syllable_Type</c>; they are entered as ranges and named with
/// their Unicode version. That is not an approximation but a copy — it can go
/// stale, but it cannot be beside the point.
/// </remarks>
public static class Precis
{

    #region Data

    /// <summary>
    /// The Unicode version the ranges in use came from.
    /// </summary>
    public const String UnicodeVersion = UnicodeSets.UnicodeVersion;

    #endregion

    #region DerivedProperty(CodePoint)

    /// <summary>
    /// The derived property per RFC 8264, section 8.
    /// </summary>
    /// <remarks>
    /// The branches stand in the order of that section, and that must not be
    /// touched - see the examples in the class description.
    /// </remarks>
    public static PrecisProperty DerivedProperty(UInt32 CodePoint)
    {

        // Exceptions (RFC 5892, section 2.6)
        if (UnicodeSets.TryException(CodePoint, out var exception))
            return exception switch {
                       UnicodeSets.ExceptionValue.PValid    => PrecisProperty.PValid,
                       UnicodeSets.ExceptionValue.ContextO  => PrecisProperty.ContextO,
                       _                                    => PrecisProperty.Disallowed
                   };

        // Exceptions as well, only as a range: the two digit series would
        // otherwise be PVALID through their category Nd.
        if (UnicodeSets.IsContextODigit(CodePoint))
            return PrecisProperty.ContextO;

        // BackwardCompatible (section 2.7) is empty to this day. The branch is
        // named here nonetheless, because it is not an oversight of the RFC: it
        // catches what a new Unicode version would otherwise reverse.

        if (UnicodeSets.IsUnassigned(CodePoint))
            return PrecisProperty.Unassigned;

        // ASCII7: printable ASCII without the space
        if (CodePoint is >= 0x21 and <= 0x7E)
            return PrecisProperty.PValid;

        // JoinControl (section 2.8)
        if (UnicodeSets.IsJoinControl(CodePoint))
            return PrecisProperty.ContextJ;

        if (UnicodeSets.IsOldHangulJamo(CodePoint))
            return PrecisProperty.Disallowed;

        if (UnicodeSets.IsDefaultIgnorable(CodePoint) || UnicodeSets.IsNoncharacter(CodePoint))
            return PrecisProperty.Disallowed;

        var category = UnicodeSets.Category(CodePoint);

        if (category == UnicodeCategory.Control)
            return PrecisProperty.Disallowed;

        // HasCompat: has a compatibility decomposition. Stands before
        // LetterDigits, and that is the difference that decides everything -
        // otherwise the fi ligature would be a letter like any other.
        if (UnicodeSets.HasCompat(CodePoint))
            return PrecisProperty.FreePValid;

        if (UnicodeSets.IsLetterDigits(CodePoint))
            return PrecisProperty.PValid;

        if (category is UnicodeCategory.TitlecaseLetter      or
                        UnicodeCategory.LetterNumber         or
                        UnicodeCategory.OtherNumber          or
                        UnicodeCategory.EnclosingMark        or
                        UnicodeCategory.SpaceSeparator       or
                        UnicodeCategory.MathSymbol           or
                        UnicodeCategory.CurrencySymbol       or
                        UnicodeCategory.ModifierSymbol       or
                        UnicodeCategory.OtherSymbol          or
                        UnicodeCategory.ConnectorPunctuation or
                        UnicodeCategory.DashPunctuation      or
                        UnicodeCategory.OpenPunctuation      or
                        UnicodeCategory.ClosePunctuation     or
                        UnicodeCategory.InitialQuotePunctuation or
                        UnicodeCategory.FinalQuotePunctuation   or
                        UnicodeCategory.OtherPunctuation)
            return PrecisProperty.FreePValid;

        return PrecisProperty.Disallowed;

    }

    #endregion

    #region IsIdentifierClass(CodePoint) / IsFreeformClass(CodePoint)

    /// <summary>
    /// Does the code point belong to the IdentifierClass (RFC 8264, section 4.2)?
    /// </summary>
    /// <remarks>
    /// Contextual code points do not count here - whether they are permitted
    /// depends on the whole string, and that is answered by
    /// <see cref="ContextRuleSatisfied"/>.
    /// </remarks>
    public static Boolean IsIdentifierClass(UInt32 CodePoint)

        => DerivedProperty(CodePoint) == PrecisProperty.PValid;

    /// <summary>
    /// Does the code point belong to the FreeformClass (RFC 8264, section 4.3)?
    /// </summary>
    public static Boolean IsFreeformClass(UInt32 CodePoint)

        => DerivedProperty(CodePoint) is PrecisProperty.PValid or
                                         PrecisProperty.FreePValid;

    #endregion

    #region ContextRuleSatisfied(CodePoints, Index)

    /// <summary>
    /// Is the contextual rule for this code point at this position satisfied
    /// (RFC 5892, appendix A)?
    /// </summary>
    /// <remarks>
    /// <b>Contextual means: the code point alone does not say.</b> Which is why
    /// the whole string and the position in it go in here and not just the
    /// character - three of the nine rules ask about the character before or
    /// after, one about all of them together.
    ///
    /// The properties needed for that - <c>Canonical_Combining_Class</c>,
    /// <c>Joining_Type</c> and <c>Script</c> - are not provided by .NET; they
    /// sit in <see cref="ContextTables"/>, generated from the Unicode database.
    ///
    /// What is not contextual gets <c>false</c> here: this function answers only
    /// the question "may this special case stand", not the general one about
    /// permissibility.
    /// </remarks>
    public static Boolean ContextRuleSatisfied(IReadOnlyList<UInt32> CodePoints, Int32 Index)
    {

        var codePoint = CodePoints[Index];

        return codePoint switch {

            // A.1: ZERO WIDTH NON-JOINER
            0x200C  => AfterVirama(CodePoints, Index) ||
                       BetweenJoiners(CodePoints, Index),

            // A.2: ZERO WIDTH JOINER
            0x200D  => AfterVirama(CodePoints, Index),

            // A.3: MIDDLE DOT - only between two 'l' (Catalan l·l)
            0x00B7  => Before(CodePoints, Index) == 0x006C &&
                       After (CodePoints, Index) == 0x006C,

            // A.4: GREEK LOWER NUMERAL SIGN - before a Greek character
            0x0375  => After(CodePoints, Index) is UInt32 following &&
                       ContextTables.Contains(ContextTables.ScriptGreek, following),

            // A.5 and A.6: GERESH and GERSHAYIM - after a Hebrew character
            0x05F3 or
            0x05F4  => Before(CodePoints, Index) is UInt32 preceding &&
                       ContextTables.Contains(ContextTables.ScriptHebrew, preceding),

            // A.7: KATAKANA MIDDLE DOT - only in Japanese text
            0x30FB  => CodePoints.Any(cp => ContextTables.Contains(ContextTables.ScriptHiragana, cp) ||
                                            ContextTables.Contains(ContextTables.ScriptKatakana, cp) ||
                                            ContextTables.Contains(ContextTables.ScriptHan,      cp)),

            // A.8: an Arabic-Indic digit does not get along with the extended
            // series - and A.9 says the same the other way round.
            >= UnicodeSets.ArabicIndicZero and
            <= UnicodeSets.ArabicIndicNine
                    => !CodePoints.Any(cp => cp is >= UnicodeSets.ExtendedArabicIndicZero
                                               and <= UnicodeSets.ExtendedArabicIndicNine),

            >= UnicodeSets.ExtendedArabicIndicZero and
            <= UnicodeSets.ExtendedArabicIndicNine
                    => !CodePoints.Any(cp => cp is >= UnicodeSets.ArabicIndicZero
                                               and <= UnicodeSets.ArabicIndicNine),

            _       => false

        };

    }

    #endregion

    #region (private) Neighbours and joining types

    private static UInt32? Before(IReadOnlyList<UInt32> CodePoints, Int32 Index)

        => Index > 0 ? CodePoints[Index - 1] : null;

    private static UInt32? After(IReadOnlyList<UInt32> CodePoints, Int32 Index)

        => Index + 1 < CodePoints.Count ? CodePoints[Index + 1] : null;

    /// <summary>
    /// Is there a virama immediately before (RFC 5892, appendix A.1 and A.2)?
    /// </summary>
    /// <remarks>
    /// A virama deletes the inherent vowel of the character before it; a joiner
    /// after it decides whether the two characters grow together into a
    /// ligature. In that position it carries meaning and is therefore permitted
    /// - everywhere else it would be an invisible character in an address.
    /// </remarks>
    private static Boolean AfterVirama(IReadOnlyList<UInt32> CodePoints, Int32 Index)

        => Before(CodePoints, Index) is UInt32 preceding &&
           ContextTables.Contains(ContextTables.Virama, preceding);

    /// <summary>
    /// The second route out of A.1: <c>(L|D) T* ZWNJ T* (R|D)</c>.
    /// </summary>
    /// <remarks>
    /// The RFC's expression in words: to the left of the joiner - across any
    /// number of transparent characters - there is a letter that joins to the
    /// right, and to its right one that joins to the left. In exactly that spot
    /// the joiner prevents a connection that would otherwise happen. Anywhere
    /// else it prevents nothing and is merely invisible.
    /// </remarks>
    private static Boolean BetweenJoiners(IReadOnlyList<UInt32> CodePoints, Int32 Index)
    {

        var left = Index - 1;

        while (left >= 0 && ContextTables.Contains(ContextTables.JoiningT, CodePoints[left]))
            left--;

        if (left < 0 ||
            !(ContextTables.Contains(ContextTables.JoiningL, CodePoints[left]) ||
              ContextTables.Contains(ContextTables.JoiningD, CodePoints[left])))
            return false;

        var right = Index + 1;

        while (right < CodePoints.Count && ContextTables.Contains(ContextTables.JoiningT, CodePoints[right]))
            right++;

        return right < CodePoints.Count &&
               (ContextTables.Contains(ContextTables.JoiningR, CodePoints[right]) ||
                ContextTables.Contains(ContextTables.JoiningD, CodePoints[right]));

    }

    #endregion

}
