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

using System.Xml.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// RFC 6120: the negotiation before the first stanza - stream features, SASL
/// and resource binding.
///
/// Deliberately pure functions on the parsed <see cref="XElement"/>: the
/// negotiation is the part of the client that is hardest to check
/// integratively, because it only runs while the connection is being
/// established and every error there tears the connection down with it. This
/// way the decisions can be checked one by one.
/// </summary>
internal static class StreamNegotiation
{

    #region Namespaces

    /// <summary>Namespace of the stream layer (RFC 6120, section 4.8.2).</summary>
    public const string StreamNamespace   = "http://etherx.jabber.org/streams";

    /// <summary>Namespace of the WebSocket framing (RFC 7395).</summary>
    public const string FramingNamespace  = "urn:ietf:params:xml:ns:xmpp-framing";

    /// <summary>Namespace of SASL (RFC 6120, section 6).</summary>
    public const string SaslNamespace     = "urn:ietf:params:xml:ns:xmpp-sasl";

    /// <summary>Namespace of the SASL2 profile (XEP-0388).</summary>
    public const string Sasl2Namespace    = "urn:xmpp:sasl:2";

    /// <summary>Namespace of the resource binding (RFC 6120, section 7).</summary>
    public const string BindNamespace     = "urn:ietf:params:xml:ns:xmpp-bind";

    /// <summary>Namespace of the legacy session (RFC 3921, dropped in RFC 6121).</summary>
    public const string SessionNamespace  = "urn:ietf:params:xml:ns:xmpp-session";

    #endregion

    #region The frame of the negotiation

    /// <summary>
    /// Is that the stream header? Over WebSocket it is the
    /// <c>&lt;open/&gt;</c> per RFC 7395, over TCP the
    /// <c>&lt;stream:stream&gt;</c>.
    /// </summary>
    public static bool IsStreamOpen(XElement element)
        => element.Name.LocalName is "open" or "stream";

    /// <summary>Are those the stream features?</summary>
    public static bool IsFeatures(XElement element)
        => element.Name.LocalName     == "features" &&
           element.Name.NamespaceName == StreamNamespace;

    #endregion

    #region SASL

    /// <summary>
    /// Is that a SASL element with this name? The namespace belongs to the
    /// check: the earlier search for the character sequence
    /// <c>&quot;&lt;success&quot;</c> in the raw text also hit a
    /// <c>&lt;success/&gt;</c> of any other extension.
    /// </summary>
    public static bool IsSasl(XElement element, string localName)
        => element.Name.LocalName     == localName &&
           element.Name.NamespaceName == SaslNamespace;

    /// <summary>
    /// The same question for the SASL2 profile (XEP-0388).
    /// </summary>
    /// <remarks>
    /// A separate method rather than a namespace parameter on the one above,
    /// because the caller always knows which profile its exchange is in - it
    /// chose - and a frame that arrives in the other one is not a variant of
    /// the answer but a server contradicting itself.
    /// </remarks>
    public static bool IsSasl2(XElement element, string localName)
        => element.Name.LocalName     == localName &&
           element.Name.NamespaceName == Sasl2Namespace;

    /// <summary>
    /// The base64 content of a <c>&lt;challenge/&gt;</c> or
    /// <c>&lt;success/&gt;</c>. Empty when the element carries none - with
    /// SCRAM that is an error and not a triviality, because without the
    /// server-final-message the server signature cannot be checked.
    /// </summary>
    public static string SaslPayload(XElement element)
        => element.Value.Trim();

    /// <summary>
    /// The condition of a <c>&lt;failure/&gt;</c>, that is the local name of
    /// the first child element - <c>not-authorized</c>,
    /// <c>invalid-mechanism</c> and so on (RFC 6120, section 6.5).
    /// </summary>
    public static string? SaslFailureCondition(XElement failure)
        => failure.Elements()
                  .FirstOrDefault(child => child.Name.LocalName != "text")
                  ?.Name.LocalName;

    /// <summary>
    /// The mechanisms offered under the SASL2 profile (XEP-0388), empty when
    /// the server announces no <c>&lt;authentication/&gt;</c> at all.
    /// </summary>
    /// <remarks>
    /// Read separately from <see cref="SaslMechanisms"/> and not merged with
    /// it, although a server will normally list the same names in both. They
    /// are two offers, and a server is entitled to make them differ - to keep
    /// PLAIN out of the newer profile, say. Merging them would let a mechanism
    /// announced under one profile be attempted under the other, which is a
    /// downgrade this client would have performed on itself.
    /// </remarks>
    public static List<string> Sasl2Mechanisms(XElement features)
    {

        var mechanisms = new List<string>();
        var container  = features.Child(Sasl2Namespace, "authentication");

        if (container is null)
            return mechanisms;

        foreach (var mechanism in container.Elements().Where(e => e.Name.LocalName == "mechanism"))
        {

            var name = mechanism.Value.Trim();

            if (name.Length > 0)
                mechanisms.Add(name);

        }

        return mechanisms;

    }

    /// <summary>
    /// Does the server offer an inline resource binding (XEP-0386)?
    /// </summary>
    /// <remarks>
    /// Inside the <c>&lt;inline/&gt;</c> of the SASL2 feature, which is what
    /// distinguishes it from the RFC 6120 <c>&lt;bind/&gt;</c> that appears in
    /// the features *after* the login. The two look alike at a glance and are
    /// different namespaces for different moments.
    /// </remarks>
    public static bool OffersInlineBind(XElement features)

        => features.Child(Sasl2Namespace, "authentication")?.
                    Child("inline")?.
                    Child("urn:xmpp:bind:0", "bind") is not null;

    /// <summary>
    /// The SASL upgrade tasks the server offers (XEP-0480), as task names.
    /// </summary>
    /// <remarks>
    /// Inside <c>&lt;authentication/&gt;</c>, because an upgrade is something
    /// that happens during a SASL2 exchange and nowhere else. A server that
    /// announces none - which is nearly all of them - yields an empty array,
    /// and the client then asks for nothing.
    /// </remarks>
    public static string[] Sasl2UpgradeTasks(XElement features)
    {

        var container = features.Child(Sasl2Namespace, "authentication");

        if (container is null)
            return [];

        return [.. container.Elements().
                             Where (e => e.Name.LocalName     == "upgrade" &&
                                         e.Name.NamespaceName == "urn:xmpp:sasl:upgrade:0").
                             Select(e => e.Value.Trim()).
                             Where (t => t.Length > 0)];

    }

    /// <summary>
    /// The channel-binding types offered (XEP-0440).
    /// </summary>
    /// <remarks>
    /// Read although nothing here can use one: this list is the second half of
    /// the string XEP-0474 hashes, so a client that ignores it computes a
    /// different hash than the server did and refuses a login that was never
    /// under attack. Reading the announcement is therefore not the same as
    /// implementing channel binding, and only the first is needed to check the
    /// downgrade protection.
    ///
    /// Channel binding itself is still open - it is finding 8 of the review,
    /// where <c>tls-server-end-point</c> is reachable and <c>tls-exporter</c>
    /// is not through SslStream.
    /// </remarks>
    public static List<string> SaslChannelBindingTypes(XElement features)
    {

        var types      = new List<string>();
        var container  = features.Child("urn:xmpp:sasl-cb:0", "sasl-channel-binding");

        if (container is null)
            return types;

        foreach (var binding in container.Elements().Where(e => e.Name.LocalName == "channel-binding"))
        {

            var type = binding.Attribute("type")?.Value.Trim();

            if (type is not null && type.Length > 0)
                types.Add(type);

        }

        return types;

    }

    /// <summary>
    /// The SASL mechanisms offered.
    ///
    /// The earlier pattern <c>&lt;mechanism&gt;([^&lt;]+)&lt;/mechanism&gt;</c>
    /// demanded an element entirely without attributes and returned the content
    /// untrimmed. A server that indents its features or repeats the namespace
    /// on the child element - both valid - looked to the client like one
    /// entirely without SASL.
    /// </summary>
    public static List<string> SaslMechanisms(XElement features)
    {

        var mechanisms = new List<string>();
        var container  = features.Child(SaslNamespace, "mechanisms");

        if (container is null)
            return mechanisms;

        foreach (var mechanism in container.Elements().Where(e => e.Name.LocalName == "mechanism"))
        {

            var name = mechanism.Value.Trim();

            if (name.Length > 0)
                mechanisms.Add(name);

        }

        return mechanisms;

    }

    #endregion

    #region Announced features

    /// <summary>
    /// The namespaces of the announced features.
    ///
    /// Read are the direct children of <c>&lt;features/&gt;</c>. The earlier
    /// pattern searched for <c>xmlns</c> as the first attribute somewhere in
    /// the text - a <c>&lt;c hash='sha-1' ver='…' xmlns='…/caps'/&gt;</c> fell
    /// out that way, and that is exactly how the BCL serialises: the namespace
    /// stands at the end.
    /// </summary>
    public static List<string> FeatureNamespaces(XElement features)
        => features.Elements()
                   .Select(child => child.Name.NamespaceName)
                   .Where(ns => ns.Length > 0)
                   .Distinct()
                   .ToList();

    /// <summary>Does the server offer resource binding?</summary>
    public static bool OffersBind(XElement features)
        => features.Child(BindNamespace, "bind") is not null;

    /// <summary>Does the server offer XEP-0198 stream management?</summary>
    public static bool OffersStreamManagement(XElement features)
        => features.Child(StreamManagementManager.Namespace, "sm") is not null;

    /// <summary>
    /// Does the server offer XEP-0352 client state indication?
    /// </summary>
    /// <remarks>
    /// Without this announcement the client must not send an
    /// <c>&lt;inactive/&gt;</c>. The reason is not politeness: a server that
    /// does not know the extension sees an unknown element on the stream layer
    /// - and RFC 6120, section 4.9.3.24 allows it a stream error for that. A
    /// saving measure would turn into a dropped connection.
    /// </remarks>
    public static bool OffersClientStateIndication(XElement features)
        => features.Child(ClientStateIndication.Namespace, "csi") is not null;

    /// <summary>
    /// Does the server offer roster versioning (RFC 6121, section 2.6.1)?
    /// </summary>
    /// <remarks>
    /// Without this announcement a client must not append a <c>ver</c> to its
    /// roster request. The reason is not politeness: a server without
    /// versioning ignores the attribute and answers with the full roster -
    /// that would still be fine. Dangerous would be the reverse case, that an
    /// empty result is read as "unchanged" where it means "empty roster".
    /// </remarks>
    public static bool OffersRosterVersioning(XElement features)
        => features.Child("urn:xmpp:features:rosterver", "ver") is not null;

    /// <summary>
    /// Does the legacy session (RFC 3921) have to be requested?
    ///
    /// The earlier check was <c>Contains("&lt;session")</c> and
    /// <c>!Contains("optional")</c> over the whole frame. The
    /// <c>&lt;optional/&gt;</c> belongs to exactly one feature each, though,
    /// and XEP-0198 puts it into its own: with a server announcing
    /// <c>&lt;sm&gt;&lt;optional/&gt;&lt;/sm&gt;</c> the mandatory session was
    /// omitted.
    /// </summary>
    public static bool RequiresSession(XElement features)
    {

        var session = features.Child(SessionNamespace, "session");

        if (session is null)
            return false;

        return !session.Elements().Any(child => child.Name.LocalName == "optional");

    }

    #endregion

    #region Resource Binding

    /// <summary>
    /// The full JID assigned in the bind answer, or null when the answer is
    /// not a result or carries no JID.
    ///
    /// That null really means "refused" here and not "just keep looking" is
    /// the point: previously a <c>&lt;jid&gt;([^&lt;]+)&lt;/jid&gt;</c>
    /// searched the raw text, and if it stayed unsuccessful, the client
    /// assumed the JID it had wished for itself. A refused binding was
    /// indistinguishable from a successful one.
    /// </summary>
    public static string? ReadBoundJid(XElement iq)
    {

        if (iq.Name.LocalName != "iq" || iq.Attr("type") != "result")
            return null;

        var jid = iq.Child(BindNamespace, "bind")?.ChildValue("jid")?.Trim();

        return string.IsNullOrEmpty(jid) ? null : jid;

    }

    #endregion

}
