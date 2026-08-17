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

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// Extension methods for JIDs.
/// </summary>
public static class JIDExtensions
{

    /// <summary>Is this JID absent or unset?</summary>
    public static Boolean IsNullOrEmpty(this JID? JID)
        => !JID.HasValue || JID.Value.IsNullOrEmpty;

    /// <summary>Is this JID present?</summary>
    public static Boolean IsNotNullOrEmpty(this JID? JID)
        => JID.HasValue && JID.Value.IsNotNullOrEmpty;

}


/// <summary>
/// An XMPP address per RFC 7622.
/// </summary>
/// <remarks>
/// <b>A type rather than a string, because the comparison is the whole
/// problem.</b> Local and domain part are independent of spelling, the
/// resourcepart is not: <c>alice@example.com/Phone</c> and
/// <c>alice@example.com/phone</c> are two devices of the same person. As long
/// as a JID was a <see cref="String"/>, <c>==</c> gave the wrong answer and the
/// right one had to be remembered and called by hand - which is a rule that
/// holds until the first person who has not read it. Here the right comparison
/// is the one you get by default.
///
/// It also stops the re-parsing. The three parts were computed, used once and
/// thrown away, dozens of times per stanza; now they are computed where the
/// address enters the process and carried from there.
///
/// <b>Splitting happens in the order given by section 3.2</b>, and that order
/// is not arbitrary: at the first <c>/</c> first, then in the front piece at
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
///
/// <b>There is no implicit conversion, in either direction</b>, and that is
/// deliberate. One from <see cref="String"/> would put an unchecked parse -
/// which can throw - at every call site that merely looks like an assignment,
/// and one to <see cref="String"/> would quietly hand the comparison back to
/// <c>==</c> on strings, which is the thing this type exists to prevent.
/// <see cref="Parse(String)"/> in, <see cref="ToString()"/> out, both visible.
///
/// <b><c>default(JID)</c> is not an address.</b> A struct cannot forbid its own
/// default, so this one answers <see cref="IsNullOrEmpty"/> with true and
/// <see cref="ToString()"/> with the empty string rather than throwing from
/// somewhere deeper.
/// </remarks>
public readonly struct JID : IEquatable<JID>,
                             IComparable<JID>,
                             IComparable
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
    /// All of them have a meaning in addressing itself or a special role in
    /// XML. XEP-0106 describes how they can be escaped when needed.
    /// </remarks>
    public const String LocalpartExcluded = "\"&'/:<>@";

    #endregion

    #region Properties

    /// <summary>The part before the <c>@</c>, prepared - or null when the JID names a domain.</summary>
    public String?  Localpart       { get; }

    /// <summary>The part behind it, prepared - the only mandatory piece.</summary>
    public String   Domainpart      { get; }

    /// <summary>The part behind the first <c>/</c> - unchanged in its spelling, or null.</summary>
    public String?  Resourcepart    { get; }


    /// <summary>Is this the default, which names nothing?</summary>
    [MemberNotNullWhen(false, nameof(Domainpart))]
    public Boolean  IsNullOrEmpty

        => String.IsNullOrEmpty(Domainpart);

    /// <summary>Does this name an address?</summary>
    [MemberNotNullWhen(true, nameof(Domainpart))]
    public Boolean  IsNotNullOrEmpty

        => !String.IsNullOrEmpty(Domainpart);

    /// <summary>Is there no resourcepart - does this name an account rather than a device?</summary>
    public Boolean  IsBare

        => Resourcepart is null;

    /// <summary>Is there a resourcepart - does this name one particular device?</summary>
    public Boolean  IsFull

        => Resourcepart is not null;

    /// <summary>Does this name a domain rather than an account on one?</summary>
    public Boolean  IsDomainOnly

        => Localpart is null && Resourcepart is null;

    /// <summary>The same address without its resourcepart.</summary>
    public JID      Bare

        => new (Localpart, Domainpart, null);

    /// <summary>The domain this address lives on, as an address of its own.</summary>
    public JID      Domain

        => new (null, Domainpart, null);

    /// <summary>The length of the whole address in characters.</summary>
    public UInt64   Length

        => (UInt64) ToString().Length;

    #endregion

    #region Constructor(s)

    /// <summary>
    /// Creates a JID out of parts that have already been prepared.
    /// </summary>
    /// <remarks>
    /// Private on purpose: everything that comes from outside goes through
    /// <see cref="Parse(String)"/>, so that no unprepared part can get in here.
    /// The properties would otherwise hold what was typed rather than what it
    /// means, and two spellings of one address would stop being equal.
    /// </remarks>
    private JID(String?  Localpart,
                String   Domainpart,
                String?  Resourcepart)
    {

        this.Localpart     = Localpart;
        this.Domainpart    = Domainpart;
        this.Resourcepart  = Resourcepart;

    }

    #endregion


    #region (static) Parse    (Text)

    /// <summary>
    /// Splits and validates a JID per RFC 7622 and returns it in prepared form.
    /// </summary>
    /// <exception cref="JidFormatException">If it is not one.</exception>
    public static JID Parse(String Text)
    {

        if (String.IsNullOrEmpty(Text))
            throw new JidFormatException(Text, "A JID is not the empty string.");

        // RFC 7622, section 3.2: first at the first '/', then at the first '@'.
        // The order decides - see example 15.
        var slash          = Text.IndexOf('/');
        var beforeSlash    = slash >= 0 ? Text[..slash]        : Text;
        var resourcepart   = slash >= 0 ? Text[(slash + 1)..]  : null;

        var at             = beforeSlash.IndexOf('@');
        var localpart      = at >= 0 ? beforeSlash[..at]        : null;
        var domainpart     = at >= 0 ? beforeSlash[(at + 1)..]  : beforeSlash;

        return new JID(localpart    is null ? null : PrepareLocalpart   (Text, localpart),
                       PrepareDomainpart(Text, domainpart),
                       resourcepart is null ? null : PrepareResourcepart(Text, resourcepart));

    }

    #endregion

    #region (static) TryParse (Text, out JID)

    /// <summary>
    /// Splits and validates a JID per RFC 7622.
    /// </summary>
    /// <returns>false if it is not one.</returns>
    public static Boolean TryParse(String? Text, out JID JID)
    {

        try
        {
            JID = Parse(Text ?? "");
            return true;
        }
        catch (JidFormatException)
        {
            JID = default;
            return false;
        }

    }

    /// <summary>
    /// The JID, or null when the text is not one.
    /// </summary>
    public static JID? TryParse(String? Text)

        => TryParse(Text, out var jid)
               ? jid
               : null;

    #endregion

    #region (static) BareTextOf(Text)

    /// <summary>
    /// The bare address as text, for something that may not be a JID at all.
    /// </summary>
    /// <remarks>
    /// The forgiving path, and the only one left: what does not parse is cut at
    /// the first <c>/</c> and lowercased, exactly as before this type existed.
    ///
    /// It matters because this runs over whatever comes off the wire. A stanza
    /// from a sender this side cannot parse should match nothing and go no
    /// further - not throw in the middle of stanza handling, and not silently
    /// become a different address either. Whoever wants to know whether
    /// something <i>is</i> an address asks <see cref="TryParse(String?)"/>.
    /// </remarks>
    public static String BareTextOf(String Text)
    {

        if (TryParse(Text, out var jid))
            return jid.Bare.ToString();

        var slash = Text.IndexOf('/');

        return (slash > 0 ? Text[..slash] : Text).ToLowerInvariant();

    }

    #endregion


    #region Operator overloading

    /// <summary>Do the two denote the same address (RFC 7622, section 3.4)?</summary>
    public static Boolean operator == (JID JID1, JID JID2)
        =>  JID1.Equals(JID2);

    /// <summary>Do the two denote different addresses?</summary>
    public static Boolean operator != (JID JID1, JID JID2)
        => !JID1.Equals(JID2);

    public static Boolean operator <  (JID JID1, JID JID2)
        => JID1.CompareTo(JID2) <  0;

    public static Boolean operator <= (JID JID1, JID JID2)
        => JID1.CompareTo(JID2) <= 0;

    public static Boolean operator >  (JID JID1, JID JID2)
        => JID1.CompareTo(JID2) >  0;

    public static Boolean operator >= (JID JID1, JID JID2)
        => JID1.CompareTo(JID2) >= 0;

    #endregion

    #region IComparable<JID> Members

    /// <summary>
    /// Orders by domain, then account, then device - so that a sorted list of
    /// addresses groups the way a person reading it expects.
    /// </summary>
    public Int32 CompareTo(JID Other)
    {

        var domain = String.Compare(Domainpart, Other.Domainpart, StringComparison.OrdinalIgnoreCase);
        if (domain != 0)
            return domain;

        var local  = String.Compare(Localpart,  Other.Localpart,  StringComparison.OrdinalIgnoreCase);
        if (local  != 0)
            return local;

        // Ordinal, and not IgnoreCase: two resourceparts differing only in
        // spelling are two devices, and an ordering that called them equal
        // would let a sort drop one of them.
        return String.Compare(Resourcepart, Other.Resourcepart, StringComparison.Ordinal);

    }

    public Int32 CompareTo(Object? Object)

        => Object is JID jid
               ? CompareTo(jid)
               : throw new ArgumentException("The given object is not a JID!", nameof(Object));

    #endregion

    #region IEquatable<JID> Members

    /// <summary>
    /// RFC 7622, section 3.4: local and domain part without regard to spelling,
    /// the resourcepart with it.
    /// </summary>
    /// <remarks>
    /// Both parts are already lowercased by <see cref="Parse(String)"/>, so an
    /// ordinal comparison would do for anything that came through it. It is
    /// case-insensitive anyway, because that is the rule, and a comparison that
    /// only holds while every instance took one particular route is a
    /// comparison waiting for the route that is added later.
    /// </remarks>
    public Boolean Equals(JID Other)

        => String.Equals(Localpart,    Other.Localpart,    StringComparison.OrdinalIgnoreCase) &&
           String.Equals(Domainpart,   Other.Domainpart,   StringComparison.OrdinalIgnoreCase) &&
           String.Equals(Resourcepart, Other.Resourcepart, StringComparison.Ordinal);

    public override Boolean Equals(Object? Object)

        => Object is JID jid &&
           Equals(jid);

    public override Int32 GetHashCode()

        => HashCode.Combine(Localpart    is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Localpart),
                            Domainpart   is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Domainpart),
                            Resourcepart is null ? 0 : StringComparer.Ordinal.          GetHashCode(Resourcepart));

    #endregion

    #region ToString()

    /// <summary>
    /// The complete address in its prepared form.
    /// </summary>
    public override String ToString()

        => IsNullOrEmpty

               ? String.Empty

               : Localpart is null
                     ? Resourcepart is null
                           ? Domainpart
                           : $"{Domainpart}/{Resourcepart}"
                     : Resourcepart is null
                           ? $"{Localpart}@{Domainpart}"
                           : $"{Localpart}@{Domainpart}/{Resourcepart}";

    #endregion


    #region (private) PrepareDomainpart  (Text, Value)

    /// <summary>
    /// RFC 7622, section 3.2: the domainpart is the only mandatory piece.
    /// </summary>
    private static String PrepareDomainpart(String Text, String Value)
    {

        if (Value.Length == 0)
            throw new JidFormatException(Text, "A JID needs a domainpart.");

        // Lowercasing and NFC - for comparing two domains the spelling is of no
        // consequence.
        var prepared = Value.ToLowerInvariant().Normalize(NormalizationForm.FormC);

        CheckLength(Text, prepared, "domainpart");

        // Per RFC 7622, section 3.2 the domainpart is an internationalised
        // domain name - and therefore IDNA2008 applies, label by label.
        if (!Idna.IsValidDomain(prepared, out var reason))
            throw new JidFormatException(Text, reason!);

        return prepared;

    }

    #endregion

    #region (private) PrepareLocalpart   (Text, Value)

    /// <summary>
    /// RFC 7622, section 3.3: UsernameCaseMapped from RFC 8265, plus the
    /// additionally excluded characters from section 3.3.1.
    /// </summary>
    private static String PrepareLocalpart(String Text, String Value)
    {

        if (Value.Length == 0)
            throw new JidFormatException(Text, "A localpart must not be empty.");

        // RFC 8265, section 3.3: width mapping, then lowercasing, then NFC. The
        // width mapping is part of NFKC; it is applied here character by
        // character so that it only hits widths and does not also decompose
        // characters such as U+2163 - those are meant to stand out.
        var prepared = MapWidth(Value).ToLowerInvariant().Normalize(NormalizationForm.FormC);

        CheckLength(Text, prepared, "localpart");

        // As an array and not as a sequence: the contextual rules ask about the
        // character before and after (RFC 5892, appendix A).
        var points = CodePoints(Text, prepared).ToArray();

        for (var i = 0; i < points.Length; i++)
        {

            var codePoint = points[i];

            if (codePoint < 0x80 && LocalpartExcluded.Contains((Char) codePoint))
                throw new JidFormatException(
                          Text,
                          $"'{(Char) codePoint}' is excluded from a localpart " +
                          "(RFC 7622, section 3.3.1).");

            if (!IsAllowed(points, i, freeform: false))
                throw new JidFormatException(
                          Text,
                          $"U+{codePoint:X4} does not belong to the PRECIS IdentifierClass " +
                          "and therefore not into a localpart.");

        }

        return prepared;

    }

    #endregion

    #region (private) PrepareResourcepart(Text, Value)

    /// <summary>
    /// RFC 7622, section 3.4: OpaqueString from RFC 8265, section 4.2.
    /// </summary>
    /// <remarks>
    /// No width mapping, <b>no</b> lowercasing, spaces outside ASCII become
    /// U+0020, then NFC.
    /// </remarks>
    private static String PrepareResourcepart(String Text, String Value)
    {

        if (Value.Length == 0)
            throw new JidFormatException(Text, "A resourcepart must not be empty.");

        var sb = new StringBuilder(Value.Length);

        var points = CodePoints(Text, Value).ToArray();

        for (var i = 0; i < points.Length; i++)
        {

            var codePoint = points[i];

            if (!IsAllowed(points, i, freeform: true))
                throw new JidFormatException(
                          Text,
                          $"U+{codePoint:X4} does not belong to the PRECIS FreeformClass " +
                          "and therefore not into a resourcepart.");

            var character = Char.ConvertFromUtf32((Int32) codePoint);

            sb.Append(codePoint != ' ' &&
                      CharUnicodeInfo.GetUnicodeCategory(character, 0) == UnicodeCategory.SpaceSeparator
                          ? " "
                          : character);

        }

        var prepared = sb.ToString().Normalize(NormalizationForm.FormC);

        CheckLength(Text, prepared, "resourcepart");

        return prepared;

    }

    #endregion

    #region (private) Character classes

    /// <summary>
    /// The width mapping from RFC 8265: full-width and half-width characters
    /// are mapped onto their decomposition.
    /// </summary>
    /// <remarks>
    /// Character by character and only for the category in question. An NFKC
    /// over the whole string would also map U+2163 (ROMAN NUMERAL FOUR) onto
    /// "IV" - and that very character is meant to make the localpart invalid
    /// per RFC 7622, example 20, rather than silently become something else.
    /// </remarks>
    private static String MapWidth(String Value)
    {

        var sb = new StringBuilder(Value.Length);

        foreach (var rune in Value.EnumerateRunes())
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
    private static void CheckLength(String Text, String Value, String Part)
    {

        var octets = Encoding.UTF8.GetByteCount(Value);

        if (octets > MaxPartOctets)
            throw new JidFormatException(
                      Text,
                      $"The {Part} is {octets} octets long, {MaxPartOctets} are allowed.");

    }

    /// <summary>
    /// The code points - with an intelligible message instead of an exception
    /// from the depths when half a character is in there.
    /// </summary>
    private static IEnumerable<UInt32> CodePoints(String Text, String Value)
    {

        for (var i = 0; i < Value.Length; i++)
        {

            var c = Value[i];

            if (Char.IsHighSurrogate(c) && i + 1 < Value.Length && Char.IsLowSurrogate(Value[i + 1]))
            {
                yield return (UInt32) Char.ConvertToUtf32(c, Value[i + 1]);
                i++;
                continue;
            }

            if (Char.IsSurrogate(c))
                throw new JidFormatException(
                          Text,
                          $"U+{(UInt32) c:X4} stands there as half a character.");

            yield return c;

        }

    }

    #endregion

}
