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
/// JIDs per RFC 7622: splitting, preparing, comparing.
/// </summary>
/// <remarks>
/// At the heart of it is an asymmetry that is easily missed: <b>local and domain
/// part are independent of spelling, the resourcepart is not.</b> Before, the
/// comparison ran through <c>OrdinalIgnoreCase</c> over the whole string
/// everywhere, and so <c>alice@example.com/Phone</c> and
/// <c>alice@example.com/phone</c> counted as the same address - two different
/// devices of the same account. The server's resource assignment always did tell
/// them apart (it compares ordinally); only the lookup did not, and so a message
/// could end up on the wrong device.
///
/// <b>Splitting happens in the order given by section 3.2</b>, and that order is
/// not arbitrary: split at the first <c>/</c> first, then in the front piece at
/// the first <c>@</c>. The other way round, RFC 7622's example 15
/// <c>a.example.com/b@example.net</c> would become a JID with the localpart
/// <c>a.example.com/b</c> - a resourcepart may contain an <c>@</c>, a localpart
/// may not.
///
/// <b>Local and resource part</b> are, per RFC 7622, instances of the PRECIS
/// profiles UsernameCaseMapped and OpaqueString (RFC 8264/8265). The mapping
/// rules - width mapping, lowercasing, NFC, space mapping - live here; class
/// membership comes from <see cref="Precis"/> and thus from the derived
/// properties per RFC 8264, section 8.
///
/// <b>The domainpart</b> is an internationalised domain name; it goes label by
/// label through <see cref="Idna"/> (RFC 5891/5892). An address literal - IPv4
/// or bracketed IPv6 - is exempt from that, just as RFC 7622, section 3.2
/// prescribes.
/// </remarks>
public static class JidUtilities
{

    #region Data

    /// <summary>
    /// The maximum length of each part in octets (RFC 7622, sections 3.2 to
    /// 3.4) - measured on the UTF-8 encoding, not on the number of characters.
    /// </summary>
    public const Int32 MaxPartOctets = 1023;

    /// <summary>
    /// Characters that RFC 7622, section 3.3.1 additionally excludes from the
    /// localpart, although the IdentifierClass would permit them.
    /// </summary>
    /// <remarks>
    /// All of them have a meaning in addressing itself or a special role in XML.
    /// XEP-0106 describes how they can be escaped when needed.
    /// </remarks>
    public const String LocalpartExcluded = "\"&'/:<>@";

    #endregion

    #region Bare(jid)

    /// <summary>
    /// The bare JID (<c>localpart@domainpart</c>) in prepared form.
    /// </summary>
    /// <remarks>
    /// Deliberately does not throw: this function runs at dozens of places over
    /// whatever comes off the wire, and an unusable JID should lead to "matches
    /// nothing" there and not to an exception in the middle of stanza handling.
    /// Whoever wants to know whether something <i>is</i> a JID asks
    /// <see cref="TryParse"/>.
    /// </remarks>
    public static String Bare(String jid)
    {

        if (TryParse(jid, out var parts))
            return parts.Bare;

        // Not splittable: as before, the part in front of the first '/',
        // lowercased.
        var slash = jid.IndexOf('/');

        return (slash > 0 ? jid[..slash] : jid).ToLowerInvariant();

    }

    #endregion

    #region Resource(jid)

    /// <summary>
    /// The resourcepart, or null - unchanged in its spelling.
    /// </summary>
    public static String? Resource(String jid)
    {

        var slash = jid.IndexOf('/');

        return slash >= 0 && slash + 1 < jid.Length
                   ? jid[(slash + 1)..]
                   : null;

    }

    #endregion

    #region AreEqual(a, b)

    /// <summary>
    /// Do the two JIDs denote the same address (RFC 7622, section 3.4)?
    /// </summary>
    /// <remarks>
    /// Local and domain part are compared without regard to spelling, the
    /// resourcepart with it.
    /// </remarks>
    public static Boolean AreEqual(String? a, String? b)
    {

        if (a is null || b is null)
            return a is null && b is null;

        if (!TryParse(a, out var left) || !TryParse(b, out var right))
            // At least one of them is not a JID - then only the literal
            // comparison helps, and that is the safe answer here.
            return String.Equals(a, b, StringComparison.Ordinal);

        return left == right;

    }

    #endregion

    #region TryParse(jid, out Parts) / Parse(jid)

    /// <summary>
    /// Splits and validates a JID per RFC 7622.
    /// </summary>
    /// <returns>false if it is not one.</returns>
    public static Boolean TryParse(String? jid, out JidParts Parts)
    {

        Parts = null!;

        try
        {
            Parts = Parse(jid ?? "");
            return true;
        }
        catch (JidFormatException)
        {
            return false;
        }

    }

    /// <summary>
    /// Splits and validates a JID per RFC 7622 and returns it in prepared form.
    /// </summary>
    /// <exception cref="JidFormatException">If it is not one.</exception>
    public static JidParts Parse(String jid)
    {

        if (String.IsNullOrEmpty(jid))
            throw new JidFormatException(jid, "A JID is not the empty string.");

        // RFC 7622, section 3.2: first at the first '/', then at the first '@'.
        // The order decides - see example 15.
        var slash          = jid.IndexOf('/');
        var beforeSlash    = slash >= 0 ? jid[..slash]        : jid;
        var resourcepart   = slash >= 0 ? jid[(slash + 1)..]  : null;

        var at             = beforeSlash.IndexOf('@');
        var localpart      = at >= 0 ? beforeSlash[..at]        : null;
        var domainpart     = at >= 0 ? beforeSlash[(at + 1)..]  : beforeSlash;

        return new JidParts(localpart  is null ? null : PrepareLocalpart (jid, localpart),
                            PrepareDomainpart(jid, domainpart),
                            resourcepart is null ? null : PrepareResourcepart(jid, resourcepart));

    }

    #endregion

    #region (private) PrepareDomainpart(jid, value)

    /// <summary>
    /// RFC 7622, section 3.2: the domainpart is the only mandatory piece.
    /// </summary>
    private static String PrepareDomainpart(String jid, String value)
    {

        if (value.Length == 0)
            throw new JidFormatException(jid, "A JID needs a domainpart.");

        // Lowercasing and NFC - for comparing two domains the spelling is of no
        // consequence.
        var prepared = value.ToLowerInvariant().Normalize(NormalizationForm.FormC);

        CheckLength(jid, prepared, "domainpart");

        // Per RFC 7622, section 3.2 the domainpart is an internationalised
        // domain name - and therefore IDNA2008 applies, label by label.
        if (!Idna.IsValidDomain(prepared, out var reason))
            throw new JidFormatException(jid, reason!);

        return prepared;

    }

    #endregion

    #region (private) PrepareLocalpart(jid, value)

    /// <summary>
    /// RFC 7622, section 3.3: UsernameCaseMapped from RFC 8265, plus the
    /// additionally excluded characters from section 3.3.1.
    /// </summary>
    private static String PrepareLocalpart(String jid, String value)
    {

        if (value.Length == 0)
            throw new JidFormatException(jid, "A localpart must not be empty.");

        // RFC 8265, section 3.3: width mapping, then lowercasing, then NFC. The
        // width mapping is part of NFKC; it is applied here character by
        // character so that it only hits widths and does not also decompose
        // characters such as U+2163 - those are meant to stand out.
        var prepared = MapWidth(value).ToLowerInvariant().Normalize(NormalizationForm.FormC);

        CheckLength(jid, prepared, "localpart");

        // As an array and not as a sequence: the contextual rules ask about the
        // character before and after (RFC 5892, appendix A).
        var points = CodePoints(jid, prepared).ToArray();

        for (var i = 0; i < points.Length; i++)
        {

            var codePoint = points[i];

            if (codePoint < 0x80 && LocalpartExcluded.Contains((Char) codePoint))
                throw new JidFormatException(
                          jid,
                          $"'{(Char) codePoint}' is excluded from a localpart " +
                          "(RFC 7622, section 3.3.1).");

            if (!IsAllowed(points, i, freeform: false))
                throw new JidFormatException(
                          jid,
                          $"U+{codePoint:X4} does not belong to the PRECIS IdentifierClass " +
                          "and therefore not into a localpart.");

        }

        return prepared;

    }

    #endregion

    #region (private) PrepareResourcepart(jid, value)

    /// <summary>
    /// RFC 7622, section 3.4: OpaqueString from RFC 8265, section 4.2.
    /// </summary>
    /// <remarks>
    /// No width mapping, <b>no</b> lowercasing, spaces outside ASCII become
    /// U+0020, then NFC.
    /// </remarks>
    private static String PrepareResourcepart(String jid, String value)
    {

        if (value.Length == 0)
            throw new JidFormatException(jid, "A resourcepart must not be empty.");

        var sb = new StringBuilder(value.Length);

        var points = CodePoints(jid, value).ToArray();

        for (var i = 0; i < points.Length; i++)
        {

            var codePoint = points[i];

            if (!IsAllowed(points, i, freeform: true))
                throw new JidFormatException(
                          jid,
                          $"U+{codePoint:X4} does not belong to the PRECIS FreeformClass " +
                          "and therefore not into a resourcepart.");

            var character = Char.ConvertFromUtf32((Int32) codePoint);

            sb.Append(codePoint != ' ' &&
                      CharUnicodeInfo.GetUnicodeCategory(character, 0) == UnicodeCategory.SpaceSeparator
                          ? " "
                          : character);

        }

        var prepared = sb.ToString().Normalize(NormalizationForm.FormC);

        CheckLength(jid, prepared, "resourcepart");

        return prepared;

    }

    #endregion

    #region (private) Character classes

    /// <summary>
    /// The width mapping from RFC 8265: full-width and half-width characters are
    /// mapped onto their decomposition.
    /// </summary>
    /// <remarks>
    /// Character by character and only for the category in question. An NFKC
    /// over the whole string would also map U+2163 (ROMAN NUMERAL FOUR) onto
    /// "IV" - and that very character is meant to make the localpart invalid per
    /// RFC 7622, example 20, rather than silently become something else.
    /// </remarks>
    private static String MapWidth(String value)
    {

        var sb = new StringBuilder(value.Length);

        foreach (var rune in value.EnumerateRunes())
        {

            var character  = rune.ToString();
            var decomposed = character.Normalize(NormalizationForm.FormKC);

            // The full- and half-width forms live in these two blocks.
            sb.Append(rune.Value is (>= 0xFF00 and <= 0xFFEF) or 0x3000
                          ? decomposed
                          : character);

        }

        return sb.ToString();

    }

    /// <summary>
    /// May the code point stand at this position - IdentifierClass for the
    /// localpart, FreeformClass for the resourcepart?
    /// </summary>
    /// <remarks>
    /// Class membership comes from <see cref="Precis"/> and thus from the
    /// derived properties per RFC 8264, section 8. Only for the contextual code
    /// points is the decision not made by the code point alone but by the whole
    /// string - which is why it goes in here as well.
    /// </remarks>
    private static Boolean IsAllowed(IReadOnlyList<UInt32> CodePoints, Int32 Index, Boolean freeform)

        => Precis.DerivedProperty(CodePoints[Index]) switch {

               PrecisProperty.PValid      => true,
               PrecisProperty.FreePValid  => freeform,

               // Both classes permit them under the same condition
               // (RFC 8264, sections 4.2.1 and 4.3.1).
               PrecisProperty.ContextO or
               PrecisProperty.ContextJ    => Precis.ContextRuleSatisfied(CodePoints, Index),

               _                          => false

           };


    #endregion

    #region (private) Helpers

    /// <summary>
    /// The maximum length applies in octets after preparation, not in
    /// characters before it (RFC 7622, section 3.3).
    /// </summary>
    private static void CheckLength(String jid, String value, String part)
    {

        var octets = Encoding.UTF8.GetByteCount(value);

        if (octets > MaxPartOctets)
            throw new JidFormatException(
                      jid,
                      $"The {part} is {octets} octets long, {MaxPartOctets} are allowed.");

    }

    /// <summary>
    /// The code points - with an intelligible message instead of an exception
    /// from the depths when half a character is in there.
    /// </summary>
    private static IEnumerable<UInt32> CodePoints(String jid, String value)
    {

        for (var i = 0; i < value.Length; i++)
        {

            var c = value[i];

            if (Char.IsHighSurrogate(c) && i + 1 < value.Length && Char.IsLowSurrogate(value[i + 1]))
            {
                yield return (UInt32) Char.ConvertToUtf32(c, value[i + 1]);
                i++;
                continue;
            }

            if (Char.IsSurrogate(c))
                throw new JidFormatException(
                          jid,
                          $"U+{(UInt32) c:X4} stands there as half a character.");

            yield return c;

        }

    }

    #endregion

}
