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

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// Builder for roster IQ stanzas
/// </summary>
public static class RosterStanzaBuilder
{

    /// <summary>The namespace of the roster (RFC 6121, section 2).</summary>
    public const string Namespace = "jabber:iq:roster";

    /// <summary>
    /// The roster request (RFC 6121, section 2.1).
    /// </summary>
    /// <param name="version">
    /// The known version for the versioning (section 2.6), or <c>null</c>
    /// without it.
    /// </param>
    /// <remarks>
    /// An <b>empty</b> <c>ver=''</c> is not a placeholder but a statement:
    /// "I can do versioning, but I have nothing yet" (section 2.6.1). The
    /// server thereupon sends the full roster and, this time, a version along
    /// with it. That is why <c>null</c> decides against the attribute and not
    /// the empty string - the two cases mean different things, and whoever
    /// throws them together loses exactly that statement.
    ///
    /// This version stood beside it unused until D57, while
    /// <c>XMPPConnection</c> assembled the same stanza on the spot. Two
    /// spellings of the same request are one too many; the subtlety above was
    /// only in one of them.
    /// </remarks>
    public static string GetRoster(string? version = null)
    {
        var ver = version != null ? $" ver='{XmlEscaping.Escape(version)}'" : "";
        return $"<iq type='get' id='roster1'>" +
               $"<query xmlns='{Namespace}'{ver}/>" +
               $"</iq>";
    }

    public static string SetItem(JID jid, string? name = null, IEnumerable<string>? groups = null)
    {
        var nameAttr = name != null ? $" name='{XmlEscaping.Escape(name)}'" : "";
        var groupsXml = groups != null
            ? string.Join("", groups.Select(g => $"<group>{XmlEscaping.Escape(g)}</group>"))
            : "";

        return $"<iq type='set' id='roster-set-{Guid.NewGuid():N}'>" +
               $"<query xmlns='jabber:iq:roster'>" +
               $"<item jid='{XmlEscaping.Escape(jid.ToString())}'{nameAttr}>{groupsXml}</item>" +
               $"</query></iq>";
    }

    public static string RemoveItem(JID jid)
    {
        return $"<iq type='set' id='roster-remove-{Guid.NewGuid():N}'>" +
               $"<query xmlns='jabber:iq:roster'>" +
               $"<item jid='{XmlEscaping.Escape(jid.ToString())}' subscription='remove'/>" +
               $"</query></iq>";
    }

    public static string Subscribe(JID jid) =>
        $"<presence to='{XmlEscaping.Escape(jid.ToString())}' type='subscribe'/>";

    public static string Subscribed(JID jid) =>
        $"<presence to='{XmlEscaping.Escape(jid.ToString())}' type='subscribed'/>";

    public static string Unsubscribed(JID jid) =>
        $"<presence to='{XmlEscaping.Escape(jid.ToString())}' type='unsubscribed'/>";

    public static string Unsubscribe(JID jid) =>
        $"<presence to='{XmlEscaping.Escape(jid.ToString())}' type='unsubscribe'/>";
}
