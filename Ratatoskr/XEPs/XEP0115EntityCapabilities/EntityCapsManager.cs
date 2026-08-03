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

using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// XEP-0115: Entity Capabilities - shortens repeated disco#info queries by way
/// of a hash of one's own or a foreign feature list.
/// </summary>
public sealed class EntityCapsManager
{

    /// <summary>The namespace of XEP-0115.</summary>
    public const string Namespace = "http://jabber.org/protocol/caps";

    /// <summary>
    /// The only hash algorithm this client can recompute (XEP-0115,
    /// section 5.1).
    /// </summary>
    public const string Sha1Algorithm = "sha-1";

    /// <summary>The namespace of the data forms (XEP-0004).</summary>
    private const string DataFormNamespace = "jabber:x:data";

    private readonly DiscoManager _disco;
    private readonly Dictionary<string, DiscoInfo> _cache = new();
    private readonly object _lock = new();

    public string Node { get; set; } = "https://github.com/xmpp-console";

    public event Action<string, DiscoInfo>? OnCapsDiscovered;

    /// <summary>
    /// A disco#info answer was not taken into the cache because it does not
    /// substantiate the announced verification string. The second parameter
    /// names the reason.
    /// </summary>
    /// <remarks>
    /// The answer itself is reported through <see cref="OnCapsDiscovered"/> all
    /// the same: it is what this entity says about itself, and precisely that is
    /// what an ordinary disco#info query would have yielded too. What is refused
    /// is only the bundling - to store it under <c>node#ver</c> and thereby
    /// ascribe it to everybody else who announces the same pair.
    /// </remarks>
    public event Action<string, string>? OnCapsRejected;

    public EntityCapsManager(DiscoManager disco)
    {
        _disco = disco;
    }

    /// <summary>
    /// Computes the verification string (SHA-1 hash of the features)
    /// </summary>
    /// <remarks>
    /// One's own data forms go into it - they stand in one's own disco#info
    /// answer after all. If they stayed out here, this client would announce a
    /// hash its own answer does not yield, and every other side that recomputes
    /// it per XEP-0115, section 5.4 would take it for a forger.
    /// </remarks>
    public string CalculateVerificationString()
        => VerificationString(_disco.LocalIdentities, _disco.LocalFeatures, _disco.LocalForms);

    /// <summary>
    /// The verification string per XEP-0115, section 5.1, over arbitrary
    /// information.
    /// </summary>
    /// <remarks>
    /// The computation was until then applicable only to one's own information -
    /// and with that the hash was a value this client produces but never checks.
    /// Precisely the checking is the purpose of the procedure: the <c>ver</c>
    /// value is not an identifier an entity picks for itself but the hash over
    /// what it answers to disco#info.
    /// </remarks>
    public static string VerificationString(IEnumerable<DiscoIdentity>  Identities,
                                            IEnumerable<string>         Features,
                                            IEnumerable<DiscoForm>?     Forms   = null)
    {

        var sb = new StringBuilder();

        // Identities as category/type/xml:lang/name - every slash stands there
        // even without a value (XEP-0115, section 5.1). Sorted over exactly the
        // string that is also emitted: because '/' (0x2F) lies below all the
        // characters that occur in category, type and language, that coincides
        // with the sorting over the four fields the XEP demands.
        foreach (var identity in Identities
                                     .Select(id => $"{id.Category}/{id.Type}/{id.Language ?? ""}/{id.Name ?? ""}")
                                     .Order(StringComparer.Ordinal))
        {
            sb.Append(identity).Append('<');
        }

        // Features sorted - XEP-0115, section 5.1 demands octet order, not the
        // culture-dependent default comparison ('B' 0x42 before 'a' 0x61).
        foreach (var feature in Features.Order(StringComparer.Ordinal))
        {
            sb.Append($"{feature}<");
        }

        // XEP-0128 data forms, sorted by their FORM_TYPE. Forms without a valid
        // FORM_TYPE stay out - XEP-0115, section 5.4 says expressly "ignore the
        // form but continue processing", and that is the difference that matters:
        // they do not make the answer invalid, they only do not count.
        foreach (var form in (Forms ?? [])
                                 .Where  (f => f.FormType is not null)
                                 .OrderBy(f => f.FormType, StringComparer.Ordinal))
        {

            sb.Append(form.FormType).Append('<');

            foreach (var field in form.Fields
                                      .Where  (f => !f.IsFormType)
                                      .OrderBy(f => f.Var, StringComparer.Ordinal))
            {

                sb.Append(field.Var).Append('<');

                foreach (var value in field.Values.Order(StringComparer.Ordinal))
                    sb.Append(value).Append('<');

            }

        }

        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToBase64String(hash);

    }

    /// <summary>
    /// Creates the <c>&lt;c/&gt;</c> element for presence
    /// </summary>
    public string GetCapsElement()
    {
        var ver = CalculateVerificationString();
        return $"<c xmlns='{Namespace}' hash='sha-1' node='{Node}' ver='{ver}'/>";
    }

    /// <summary>
    /// Does this disco node denote this entity in its <b>present</b> state?
    /// </summary>
    /// <remarks>
    /// Two forms count. <c>node#ver</c> is the one from XEP-0115, section 6.2:
    /// whoever has seen our <c>&lt;c/&gt;</c> in a presence asks exactly that
    /// way. The bare node without <c>#ver</c> counts likewise - there it says
    /// "SHOULD", not "MUST", and whoever names only the node asks about this
    /// entity without nailing down a state.
    ///
    /// A <b>different</b> <c>ver</c> does not count, not even one that was once
    /// our own. It asks about the feature list of back then, and that does not
    /// exist here any more. Whoever sends today's in answer to it answers a
    /// different question than the one asked: the asker recomputes the announced
    /// hash against the answer per section 5.4 and gets a different one out.
    /// </remarks>
    public bool IsOwnNode(string node)

        => node == Node ||
           node == $"{Node}#{CalculateVerificationString()}";

    /// <summary>
    /// Processes a caps element from presence.
    /// </summary>
    /// <remarks>
    /// XEP-0115, section 5.4: the answer is stored under <c>node#ver</c> only
    /// once its hash actually yields the announced value.
    ///
    /// Without this check the cache was poisonable, and by everyone whose
    /// presence arrives here. The move is short: the attacker announces in their
    /// presence the <c>node#ver</c> pair of a widespread client, but answers the
    /// following disco#info query with a list of their choosing. Under this pair
    /// their list lies from then on - and it is delivered to every further
    /// contact who announces the same pair, without that one ever being asked.
    /// The attacker thereby determines what this client believes about third
    /// parties: which encryption they can do, whether they understand delivery
    /// receipts, what can be sent to them.
    ///
    /// The <c>ver</c> value is built precisely against that - it is the hash over
    /// the answer, not a freely chosen identifier. One only has to recompute it.
    /// </remarks>
    /// <param name="hash">
    /// The algorithm from the <c>hash</c> attribute. If it is missing or is one
    /// other than <c>sha-1</c>, the <c>ver</c> value is not recomputable (with
    /// the legacy form from XEP-0115 before version 1.4 it is a version number) -
    /// it is then still queried, but no longer stored.
    /// </param>
    public async Task ProcessCapsAsync(string             from,
                                       string             node,
                                       string             ver,
                                       string?            hash   = null,
                                       CancellationToken  ct     = default)
    {
        var cacheKey = $"{node}#{ver}";

        lock (_lock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                OnCapsDiscovered?.Invoke(from, cached);
                return;
            }
        }

        // Not in the cache yet - query disco#info
        var info = await _disco.QueryInfoAsync(from, cacheKey, ct: ct);

        if (info is null)
            return;

        if (VerificationFailure(info, ver, hash) is string reason)
            OnCapsRejected?.Invoke(from, reason);

        else
            lock (_lock)
            {
                _cache[cacheKey] = info;
            }

        OnCapsDiscovered?.Invoke(from, info);
    }

    /// <summary>
    /// The reason why this answer does not substantiate the announced
    /// verification string - or null when it does substantiate it.
    /// </summary>
    private static string? VerificationFailure(DiscoInfo Info, string Ver, string? Hash)
    {

        if (Hash is null)
            return "The caps element carries no hash attribute (legacy form before XEP-0115 1.4); " +
                   "the ver value is therefore no hash and cannot be recomputed.";

        if (Hash != Sha1Algorithm)
            return $"Unknown hash algorithm '{Hash}'; only {Sha1Algorithm} can be recomputed.";

        if (IllFormed(Info) is string defect)
            return defect;

        var computed = VerificationString(Info.Identities, Info.Features, Info.Forms);

        if (!String.Equals(computed, Ver, StringComparison.Ordinal))
            return $"The hash of the answer is {computed}, announced was {Ver}.";

        return null;

    }

    /// <summary>
    /// The answer is ambiguous in itself (XEP-0115, section 5.4) - or null when
    /// it is not.
    /// </summary>
    /// <remarks>
    /// These three rules are no formal strictness. The verification string comes
    /// into being by an answer being carried over into exactly one string; where
    /// duplications stand, there is more than one such string, and with that a
    /// second answer can be built to a given hash. The XEP therefore demands
    /// discarding the whole answer instead of deciding on one reading.
    /// </remarks>
    private static string? IllFormed(DiscoInfo Info)
    {

        if (Info.Identities.Count != Info.Identities.Distinct().Count())
            return "The answer lists the same identity several times.";

        if (Info.Features.Count != Info.Features.Distinct(StringComparer.Ordinal).Count())
            return "The answer lists the same feature several times.";

        // A FORM_TYPE with several different values - which of them is supposed
        // to sort the form?
        foreach (var form in Info.Forms)
        {

            var values = form.FormTypeField?.Values.Distinct(StringComparer.Ordinal).ToList();

            if (values is not null && values.Count > 1)
                return $"A data form carries {values.Count} different FORM_TYPE values.";

        }

        var types = Info.Forms.Select(f => f.FormType)
                              .Where (t => t is not null)
                              .ToList();

        if (types.Count != types.Distinct(StringComparer.Ordinal).Count())
            return "The answer contains several data forms with the same FORM_TYPE.";

        return null;

    }

    /// <summary>
    /// Checks whether a JID supports a feature (from the cache)
    /// </summary>
    public DiscoInfo? GetCachedInfo(string verString)
    {
        lock (_lock)
        {
            return _cache.TryGetValue(verString, out var info) ? info : null;
        }
    }

    /// <summary>
    /// Extracts caps out of a presence.
    ///
    /// What is sought are the direct child elements in the caps namespace. The
    /// earlier pattern found a <c>&lt;c/&gt;</c> anywhere in the stanza and
    /// demanded an unprefixed element.
    /// </summary>
    public static (string Node, string Ver, string? Hash)? ParseCaps(XElement presence)
    {

        var caps = presence.Elements()
                           .FirstOrDefault(child => child.Name.NamespaceName == Namespace &&
                                                    child.Name.LocalName     == "c");

        if (caps is null)
            return null;

        var node = caps.Attr("node");
        var ver  = caps.Attr("ver");

        if (node is null || ver is null)
            return null;

        return (node, ver, caps.Attr("hash"));

    }
}
