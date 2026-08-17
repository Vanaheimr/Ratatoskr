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

using System.Text;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// SASLprep (RFC 4013), the StringPrep profile for user names and passwords.
/// </summary>
/// <remarks>
/// Two people type the same password and mean the same thing - only they have
/// different keyboards, different input methods, different operating systems.
/// The <c>ä</c> arrives once as a single character and once as an <c>a</c> with
/// a trailing diaeresis; a space is sometimes U+0020 and sometimes a
/// non-breaking one. SASLprep leads both back to the same form, so that the
/// same input becomes the same key.
///
/// Up to here this preparation consisted of a single line - NFKC. That covers
/// the second case and none of the rest: the mappings were missing (a soft
/// hyphen in the password stayed instead of disappearing), the prohibitions
/// were missing (a control character went through), and the bidi check was
/// missing entirely.
///
/// In practice that means: a password outside of ASCII was prepared here
/// differently than at Prosody or ejabberd, and the login failed without
/// anyone being able to say why. That is exactly what the profile is for.
///
/// The four steps per RFC 3454, section 7:
///
/// <list type="number">
///   <item>map (RFC 4013, section 2.1),</item>
///   <item>normalise to NFKC (2.2),</item>
///   <item>reject prohibited characters (2.3) - and with them the unassigned ones (2.5),</item>
///   <item>check the bidi rules (2.4).</item>
/// </list>
///
/// Rejection happens through an exception and not through silent replacement.
/// A password that cannot be prepared unambiguously is none: whoever bends it
/// into shape ends up letting two different inputs lead to the same key.
/// </remarks>
public static class SaslPrep
{

    #region Prepare(Input, AllowUnassigned = false)

    /// <summary>
    /// Prepares a string per SASLprep.
    /// </summary>
    /// <param name="Input">User name or password.</param>
    /// <param name="AllowUnassigned">
    /// Let unassigned code points through. The default is <c>false</c>, that is
    /// the treatment as a "stored string" per RFC 4013, section 2.5: whatever
    /// had no meaning yet in Unicode 3.2 does not belong in a password, because
    /// two peers could normalise it differently.
    /// </param>
    /// <exception cref="AuthenticationException">
    /// On a prohibited character or a violation of the bidi rules.
    /// </exception>
    public static String Prepare(String   Input,
                                 Boolean  AllowUnassigned   = false)
    {

        var mapped      = Map(Input);
        var normalised  = mapped.Normalize(NormalizationForm.FormKC);

        Prohibit(normalised, AllowUnassigned);
        CheckBidi(normalised);

        return normalised;

    }

    #endregion

    #region (private) Map(Input)

    /// <summary>
    /// RFC 4013, section 2.1: spaces outside of ASCII become U+0020, and
    /// whatever is in table B.1 falls away.
    /// </summary>
    /// <remarks>
    /// The falling away is the part that surprises: a soft hyphen or a
    /// variation selector in a password is invisible, so it is easily lost
    /// while typing or ends up in there by accident. Both versions are meant to
    /// be the same password.
    /// </remarks>
    private static String Map(String Input)
    {

        var sb = new StringBuilder(Input.Length);

        foreach (var codePoint in CodePoints(Input))
        {

            if (StringPrepTables.Contains(StringPrepTables.MappedToNothing, codePoint))
                continue;

            if (StringPrepTables.Contains(StringPrepTables.NonAsciiSpace, codePoint))
            {
                sb.Append(' ');
                continue;
            }

            sb.Append(Char.ConvertFromUtf32((Int32) codePoint));

        }

        return sb.ToString();

    }

    #endregion

    #region (private) Prohibit(Value, AllowUnassigned)

    /// <summary>
    /// RFC 4013, sections 2.3 and 2.5: the prohibited characters.
    /// </summary>
    private static void Prohibit(String Value, Boolean AllowUnassigned)
    {

        foreach (var codePoint in CodePoints(Value))
        {

            var reason = Forbidden(codePoint);

            if (reason is not null)
                throw new AuthenticationException(
                          $"SASLprep: U+{codePoint:X4} is not permitted ({reason}, RFC 3454).");

            if (!AllowUnassigned &&
                StringPrepTables.Contains(StringPrepTables.Unassigned, codePoint))
                throw new AuthenticationException(
                          $"SASLprep: U+{codePoint:X4} was unassigned in Unicode 3.2 " +
                          "(table A.1, RFC 3454).");

        }

    }

    /// <summary>
    /// The table that prohibits this code point - or null.
    /// </summary>
    private static String? Forbidden(UInt32 CodePoint)
    {

        if (StringPrepTables.Contains(StringPrepTables.NonAsciiSpace,               CodePoint)) return "table C.1.2";
        if (StringPrepTables.Contains(StringPrepTables.AsciiControl,                CodePoint)) return "table C.2.1";
        if (StringPrepTables.Contains(StringPrepTables.NonAsciiControl,             CodePoint)) return "table C.2.2";
        if (StringPrepTables.Contains(StringPrepTables.PrivateUse,                  CodePoint)) return "table C.3";
        if (StringPrepTables.Contains(StringPrepTables.NonCharacter,                CodePoint)) return "table C.4";
        if (StringPrepTables.Contains(StringPrepTables.Surrogate,                   CodePoint)) return "table C.5";
        if (StringPrepTables.Contains(StringPrepTables.InappropriateForPlainText,   CodePoint)) return "table C.6";
        if (StringPrepTables.Contains(StringPrepTables.InappropriateForCanonical,   CodePoint)) return "table C.7";
        if (StringPrepTables.Contains(StringPrepTables.DisplayOrDeprecated,         CodePoint)) return "table C.8";
        if (StringPrepTables.Contains(StringPrepTables.Tagging,                     CodePoint)) return "table C.9";

        return null;

    }

    #endregion

    #region (private) CheckBidi(Value)

    /// <summary>
    /// RFC 3454, section 6: the rules for mixed writing directions.
    /// </summary>
    /// <remarks>
    /// A string made of right-to-left and left-to-right script is displayed
    /// differently depending on its surroundings. Whoever reads it therefore
    /// does not necessarily see what is in it - and an attacker can put
    /// together a name that looks like another one. The two rules rule out the
    /// cases in which the display would become ambiguous.
    /// </remarks>
    private static void CheckBidi(String Value)
    {

        var codePoints = CodePoints(Value).ToList();

        if (codePoints.Count == 0)
            return;

        var hasRandAL = codePoints.Any(cp => StringPrepTables.Contains(StringPrepTables.RandALCat, cp));

        if (!hasRandAL)
            return;

        // Rule 2: a right-to-left and a left-to-right character together - prohibited.
        if (codePoints.Any(cp => StringPrepTables.Contains(StringPrepTables.LCat, cp)))
            throw new AuthenticationException(
                      "SASLprep: right-to-left and left-to-right characters together " +
                      "(RFC 3454, section 6, rule 2).");

        // Rule 3: if a right-to-left character is in there, the first and the
        // last character have to be right-to-left.
        if (!StringPrepTables.Contains(StringPrepTables.RandALCat, codePoints[0]) ||
            !StringPrepTables.Contains(StringPrepTables.RandALCat, codePoints[^1]))
            throw new AuthenticationException(
                      "SASLprep: a right-to-left string has to begin and end " +
                      "right-to-left (RFC 3454, section 6, rule 3).");

    }

    #endregion

    #region (private) CodePoints(Value)

    /// <summary>
    /// The code points of the string.
    /// </summary>
    /// <remarks>
    /// By hand and not through <c>EnumerateRunes</c>: that one silently
    /// replaces a lone surrogate with U+FFFD, and silently is the wrong thing
    /// here - half a character is prohibited by table C.5 and shall be reported
    /// as such.
    /// </remarks>
    private static IEnumerable<UInt32> CodePoints(String Value)
    {

        for (var i = 0; i < Value.Length; i++)
        {

            var c = Value[i];

            if (Char.IsHighSurrogate(c))
            {

                if (i + 1 < Value.Length && Char.IsLowSurrogate(Value[i + 1]))
                {
                    yield return (UInt32) Char.ConvertToUtf32(c, Value[i + 1]);
                    i++;
                    continue;
                }

                throw new AuthenticationException(
                          $"SASLprep: U+{(UInt32) c:X4} stands there as half a character " +
                          "(table C.5, RFC 3454).");

            }

            if (Char.IsLowSurrogate(c))
                throw new AuthenticationException(
                          $"SASLprep: U+{(UInt32) c:X4} stands there as half a character " +
                          "(table C.5, RFC 3454).");

            yield return c;

        }

    }

    #endregion

}
