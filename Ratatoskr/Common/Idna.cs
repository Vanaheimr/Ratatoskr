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
using System.Net;
using System.Net.Sockets;
using System.Text;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// The derived property of a code point per RFC 5892, section 1.
/// </summary>
public enum IdnaProperty
{

    /// <summary>Permitted in a label.</summary>
    PValid,

    /// <summary>
    /// Permitted if the rule from RFC 5892, appendix A.1/A.2 is satisfied (the
    /// two joiners).
    /// </summary>
    ContextJ,

    /// <summary>
    /// Permitted if the rule from RFC 5892, appendix A.3 to A.9 is satisfied.
    /// </summary>
    ContextO,

    /// <summary>Not permitted in a label.</summary>
    Disallowed,

    /// <summary>
    /// Unassigned in the underlying Unicode version - and therefore not
    /// permitted in a label (RFC 5891, section 4.2.2 allows unassigned only on
    /// lookup, not on registration).
    /// </summary>
    Unassigned

}

/// <summary>
/// IDNA2008 at the code point level: RFC 5892, section 1.
/// </summary>
/// <remarks>
/// <b>The same building blocks as PRECIS, a different ladder.</b> Both ladders
/// stand on <see cref="UnicodeSets"/>, and the differences are not subtleties:
///
/// <list type="bullet">
///   <item>Instead of ASCII7 there is <b>LDH</b> here - hyphen, digits,
///         lowercase letters. An underscore, a plus sign, an uppercase letter:
///         none of them are label characters.</item>
///   <item><b>Unstable</b> exists only here, and that branch throws out
///         everything that changes under normalisation and lowercasing.</item>
///   <item><b>IgnorableProperties</b> includes <c>White_Space</c> here as
///         well.</item>
///   <item>At the end stands <b>DISALLOWED</b> and no catch-all branch for
///         symbols and punctuation: whatever is not explicitly permitted does
///         not belong in a domain name.</item>
/// </list>
///
/// That is the reason the two ladders stay separate. A shared procedure with
/// switches would be shorter and would raise the question "does this apply to
/// labels or to localparts now?" anew on every line you read.
/// </remarks>
public static class Idna
{

    #region DerivedProperty(CodePoint)

    /// <summary>
    /// The derived property per RFC 5892, section 1.
    /// </summary>
    public static IdnaProperty DerivedProperty(UInt32 CodePoint)
    {

        // Exceptions (section 2.6)
        if (UnicodeSets.TryException(CodePoint, out var exception))
            return exception switch {
                       UnicodeSets.ExceptionValue.PValid    => IdnaProperty.PValid,
                       UnicodeSets.ExceptionValue.ContextO  => IdnaProperty.ContextO,
                       _                                    => IdnaProperty.Disallowed
                   };

        if (UnicodeSets.IsContextODigit(CodePoint))
            return IdnaProperty.ContextO;

        // BackwardCompatible (section 2.7) is empty.

        if (UnicodeSets.IsUnassigned(CodePoint))
            return IdnaProperty.Unassigned;

        // LDH (section 2.5) - and not ASCII7 as with PRECIS.
        if (UnicodeSets.IsLdh(CodePoint))
            return IdnaProperty.PValid;

        if (UnicodeSets.IsJoinControl(CodePoint))
            return IdnaProperty.ContextJ;

        // Unstable (section 2.2): whatever changes under normalisation and
        // lowercasing has no business in a domain name - otherwise there would
        // be two spellings for the same address.
        if (UnicodeSets.IsUnstable(CodePoint))
            return IdnaProperty.Disallowed;

        if (UnicodeSets.IsIgnorableProperties(CodePoint))
            return IdnaProperty.Disallowed;

        if (UnicodeSets.IsIgnorableBlocks(CodePoint))
            return IdnaProperty.Disallowed;

        if (UnicodeSets.IsOldHangulJamo(CodePoint))
            return IdnaProperty.Disallowed;

        if (UnicodeSets.IsLetterDigits(CodePoint))
            return IdnaProperty.PValid;

        // No catch-all branch: whatever got this far does not belong in a
        // domain name.
        return IdnaProperty.Disallowed;

    }

    #endregion

    #region IsValidDomain(Domain, out Reason)

    /// <summary>The prefix of an A-label (RFC 5890, section 2.3.2.1).</summary>
    public const String AcePrefix = "xn--";

    /// <summary>The maximum length of a label in octets (RFC 1035).</summary>
    public const Int32 MaxLabelOctets = 63;

    /// <summary>
    /// Is this domainpart valid per IDNA2008?
    /// </summary>
    /// <param name="Domain">The already lowercased domainpart.</param>
    /// <param name="Reason">What it fails on - for the error message.</param>
    /// <remarks>
    /// <b>Address literals go past this</b>, and they do so by the book:
    /// RFC 7622, section 3.2 explicitly permits an IPv4 address and a bracketed
    /// IPv6 literal alongside the domain name. They are not domain names, and
    /// IDNA has nothing to say about them.
    ///
    /// <b>The bidi rule comes last</b> (RFC 5893, section 2): it applies only
    /// once a label carries right-to-left characters - but then to all labels of
    /// that name. Which is why it sits here and not in the label check: a label
    /// on its own cannot answer the question.
    /// </remarks>
    public static Boolean IsValidDomain(String Domain, out String? Reason)
    {

        Reason = null;

        if (Domain.Length == 0)
        {
            Reason = "A JID needs a domainpart.";
            return false;
        }

        // RFC 7622, section 3.2: address literals are allowed and are not
        // domain names.
        if (IsAddressLiteral(Domain))
            return true;

        var uLabels = new List<String>();

        foreach (var label in Domain.Split('.'))
        {

            if (!IsValidLabel(label, out Reason))
                return false;

            uLabels.Add(label.StartsWith(AcePrefix, StringComparison.Ordinal)
                            ? Punycode.Decode(label[AcePrefix.Length..])!
                            : label);

        }

        // RFC 5893, section 2: the bidi rule applies to a "bidi domain name" -
        // and one it is as soon as a single label carries right-to-left
        // characters. Then it applies to all labels, including the pure-ASCII
        // ones.
        if (!uLabels.Any(IsRtlLabel))
            return true;

        foreach (var uLabel in uLabels)
            if (!SatisfiesBidiRule(uLabel, out var violation))
            {
                Reason = $"The label '{uLabel}' violates the bidi rule " +
                         $"(RFC 5893, section 2): {violation}";
                return false;
            }

        return true;

    }

    #endregion

    #region (private) IsValidLabel(Label, out Reason)

    /// <summary>
    /// A label per RFC 5891, section 4.2 - including the A-label re-encoding
    /// check.
    /// </summary>
    private static Boolean IsValidLabel(String Label, out String? Reason)
    {

        Reason = null;

        if (Label.Length == 0)
        {
            Reason = "A domain label must not be empty.";
            return false;
        }

        // A-label: the ASCII text is only the wrapping. What is checked is what
        // is inside it - and that the wrapping is the only possible one.
        if (Label.StartsWith(AcePrefix, StringComparison.Ordinal))
        {

            if (Label.Length > MaxLabelOctets)
            {
                Reason = $"The label '{Label}' is longer than {MaxLabelOctets} octets.";
                return false;
            }

            var uLabel = Punycode.Decode(Label[AcePrefix.Length..]);

            if (uLabel is null)
            {
                Reason = $"'{Label}' begins like an A-label but is not Punycode.";
                return false;
            }

            // RFC 5890, section 2.3.2.1: a U-label carries at least one
            // character outside ASCII. Otherwise the same label would exist
            // twice - once as itself and once wrapped.
            if (uLabel.All(Char.IsAscii))
            {
                Reason = $"'{Label}' wraps pure ASCII ('{uLabel}') as an A-label.";
                return false;
            }

            // RFC 5891, section 5.4: one meaning has exactly one spelling. If
            // the re-encoding produces something else, this A-label is a second
            // address for the same thing.
            if (Punycode.Encode(uLabel) is not String reEncoded ||
                !String.Equals(AcePrefix + reEncoded, Label, StringComparison.Ordinal))
            {
                Reason = $"'{Label}' is not the canonical spelling of '{uLabel}'.";
                return false;
            }

            return IsValidULabel(uLabel, Label, out Reason);

        }

        if (Encoding.UTF8.GetByteCount(Label) > MaxLabelOctets)
        {
            Reason = $"The label '{Label}' is longer than {MaxLabelOctets} octets.";
            return false;
        }

        return IsValidULabel(Label, Label, out Reason);

    }

    #endregion

    #region (private) IsValidULabel(ULabel, Shown, out Reason)

    /// <summary>
    /// The rules from RFC 5891, sections 4.2.3 and 4.2.4 over the Unicode
    /// label.
    /// </summary>
    private static Boolean IsValidULabel(String ULabel, String Shown, out String? Reason)
    {

        Reason = null;

        // Section 4.2.3.1: no hyphen at the beginning or the end ...
        if (ULabel[0] == '-' || ULabel[^1] == '-')
        {
            Reason = $"The label '{Shown}' begins or ends with a hyphen.";
            return false;
        }

        // ... and no two at the third and fourth position. That is where an
        // A-label's prefix sits, and a U-label must not look like one.
        if (ULabel.Length >= 4 && ULabel[2] == '-' && ULabel[3] == '-')
        {
            Reason = $"The label '{Shown}' carries '--' at the third and fourth position.";
            return false;
        }

        // Section 4.2.3.2: no combining character at the start - it would have
        // nothing to combine with.
        if (Char.GetUnicodeCategory(ULabel, 0) is UnicodeCategory.NonSpacingMark       or
                                                  UnicodeCategory.SpacingCombiningMark or
                                                  UnicodeCategory.EnclosingMark)
        {
            Reason = $"The label '{Shown}' begins with a combining character.";
            return false;
        }

        // As an array and not as a sequence: the contextual rules ask about the
        // character before and after (RFC 5892, appendix A).
        var points = CodePoints(ULabel).ToArray();

        for (var i = 0; i < points.Length; i++)
        {

            var codePoint = points[i];
            var property  = DerivedProperty(codePoint);

            if (property == IdnaProperty.PValid)
                continue;

            if (property is IdnaProperty.ContextJ or IdnaProperty.ContextO &&
                Precis.ContextRuleSatisfied(points, i))
                continue;

            Reason = $"U+{codePoint:X4} does not belong in a domain label " +
                     $"('{Shown}', RFC 5892: {property}).";

            return false;

        }

        return true;

    }

    #endregion

    #region SatisfiesBidiRule(ULabel, out Reason)

    /// <summary>
    /// Does this label carry at least one right-to-left character (RFC 5893,
    /// section 1.4)?
    /// </summary>
    private static Boolean IsRtlLabel(String ULabel)

        => CodePoints(ULabel).Any(cp => BidiClasses.ClassOf(cp) is BidiClass.R  or
                                                                   BidiClass.AL or
                                                                   BidiClass.AN);

    /// <summary>
    /// The six conditions of the bidi rule (RFC 5893, section 2).
    /// </summary>
    /// <remarks>
    /// <b>A label's direction is determined by its first character</b>, and
    /// everything else hangs on that: a label beginning with a Latin letter and
    /// containing a Hebrew character is not a right-to-left label with a guest
    /// in it, but a left-to-right one with a violation (conditions 1 and 5).
    ///
    /// Conditions 3 and 6 - what a label may end with - are unreachable through
    /// <see cref="IsValidDomain"/>: the characters a label could wrongly end
    /// with already drop out at the code point level. They stand here
    /// nonetheless, because this function is the rule from the RFC and not the
    /// subset one particular caller happens to leave over.
    /// </remarks>
    internal static Boolean SatisfiesBidiRule(String ULabel, out String? Reason)
    {

        Reason = null;

        var classes = CodePoints(ULabel).Select(BidiClasses.ClassOf).ToList();

        if (classes.Count == 0)
        {
            Reason = "The label is empty.";
            return false;
        }

        // Condition 1
        if (classes[0] is not (BidiClass.L or BidiClass.R or BidiClass.AL))
        {
            Reason = $"The first character is {classes[0]} and neither L nor R nor AL.";
            return false;
        }

        var rightToLeft = classes[0] is BidiClass.R or BidiClass.AL;

        // The last character that is not an NSM - conditions 3 and 6 allow any
        // number of NSM after it.
        var last = classes.FindLastIndex(k => k != BidiClass.NSM);

        if (rightToLeft)
        {

            // Condition 2
            foreach (var cls in classes)
                if (cls is not (BidiClass.R  or BidiClass.AL or BidiClass.AN or
                                BidiClass.EN or BidiClass.ES or BidiClass.CS or
                                BidiClass.ET or BidiClass.ON or BidiClass.BN or
                                BidiClass.NSM))
                {
                    Reason = $"In a right-to-left label {cls} is not permitted.";
                    return false;
                }

            // Condition 3
            if (last < 0 || classes[last] is not (BidiClass.R  or BidiClass.AL or
                                                  BidiClass.EN or BidiClass.AN))
            {
                Reason = "A right-to-left label ends in R, AL, EN or AN.";
                return false;
            }

            // Condition 4
            if (classes.Contains(BidiClass.EN) && classes.Contains(BidiClass.AN))
            {
                Reason = "European and Arabic digits do not appear in the same label.";
                return false;
            }

        }

        else
        {

            // Condition 5
            foreach (var cls in classes)
                if (cls is not (BidiClass.L  or BidiClass.EN or BidiClass.ES or
                                BidiClass.CS or BidiClass.ET or BidiClass.ON or
                                BidiClass.BN or BidiClass.NSM))
                {
                    Reason = $"In a left-to-right label {cls} is not permitted.";
                    return false;
                }

            // Condition 6
            if (last < 0 || classes[last] is not (BidiClass.L or BidiClass.EN))
            {
                Reason = "A left-to-right label ends in L or EN.";
                return false;
            }

        }

        return true;

    }

    #endregion

    #region (private) IsAddressLiteral(Domain) / CodePoints(Text)

    /// <summary>
    /// An IPv4 literal or a bracketed IPv6 literal (RFC 7622, section 3.2).
    /// </summary>
    private static Boolean IsAddressLiteral(String Domain)

        // Spelled out in full: Hermod brings a type of its own by that name, and
        // that one answers a different question.
        => Domain.Length > 2 && Domain[0] == '[' && Domain[^1] == ']'
               ? System.Net.IPAddress.TryParse(Domain[1..^1], out _)
               : System.Net.IPAddress.TryParse(Domain, out var address) &&
                 address.AddressFamily == AddressFamily.InterNetwork;

    private static IEnumerable<UInt32> CodePoints(String Text)
    {

        foreach (var rune in Text.EnumerateRunes())
            yield return (UInt32) rune.Value;

    }

    #endregion

}
