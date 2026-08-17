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

using System.Text.RegularExpressions;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// RFC 6120, section 8.3: the content of an <c>&lt;error/&gt;</c> element from a
/// stanza of type <c>error</c>.
/// </summary>
/// <param name="Type">The error type; determines whether and how a retry is allowed.</param>
/// <param name="Condition">
/// The defined condition from section 8.3.3, such as <c>service-unavailable</c>
/// or <c>item-not-found</c>. Kept as a string, so that application-specific and
/// future conditions come through unaltered as well.
/// </param>
/// <param name="Text">Optional text intended for humans.</param>
/// <param name="By">
/// Optional: who produced the error. For an error from a server along the
/// delivery path that is not necessarily the original recipient.
/// </param>
public sealed record StanzaError(StanzaErrorType  Type,
                                 string           Condition,
                                 string?          Text  = null,
                                 string?          By    = null)
{

    /// <summary>
    /// The namespace of the defined conditions.
    /// </summary>
    public const string Namespace = "urn:ietf:params:xml:ns:xmpp-stanzas";

    /// <summary>
    /// Reads the <c>&lt;error/&gt;</c> element out of a stanza.
    /// </summary>
    /// <returns>False if the stanza contains no error element.</returns>
    public static bool TryParse(string stanza, out StanzaError? error)
    {

        error = null;

        var errorElement = Regex.Match(stanza,
                                       @"<error\b[^>]*>.*?</error\s*>|<error\b[^>]*/>",
                                       RegexOptions.Singleline);

        if (!errorElement.Success)
            return false;

        var xml = errorElement.Value;

        // RFC 6120, 8.3.2: the type attribute is mandatory. If it is missing or
        // unknown, 'cancel' is assumed - the most cautious assumption, because
        // it does not lead to a retry.
        var type = ParseType(Attribute(xml, "type"));

        error = new StanzaError(type,
                                ParseCondition(xml),
                                ParseText(xml),
                                Attribute(xml, "by"));

        return true;

    }

    private static StanzaErrorType ParseType(string? value)
        => value switch {
               "auth"      => StanzaErrorType.Auth,
               "continue"  => StanzaErrorType.Continue,
               "modify"    => StanzaErrorType.Modify,
               "wait"      => StanzaErrorType.Wait,
               _           => StanzaErrorType.Cancel
           };

    /// <summary>
    /// The defined condition is the first child element in the stanzas namespace
    /// that is not called <c>text</c>.
    /// </summary>
    private static string ParseCondition(string errorXml)
    {

        // The regular case: the condition carries the namespace itself.
        foreach (Match m in Regex.Matches(errorXml,
                                          @"<([a-zA-Z][\w\-]*)\s[^>]*xmlns\s*=\s*['""]" +
                                          Regex.Escape(Namespace) + @"['""]"))
        {
            if (m.Groups[1].Value != "text")
                return m.Groups[1].Value;
        }

        // Fallback for servers that set the namespace on the error element:
        // the first child element that is not 'text'.
        foreach (Match m in Regex.Matches(errorXml, @"<([a-zA-Z][\w\-]*)[\s/>]"))
        {
            var name = m.Groups[1].Value;
            if (name != "error" && name != "text")
                return name;
        }

        // RFC 6120, 8.3.3: 'undefined-condition' is the prescribed fallback.
        return "undefined-condition";

    }

    private static string? ParseText(string errorXml)
    {

        var m = Regex.Match(errorXml, @"<text\b[^>]*>(.*?)</text\s*>", RegexOptions.Singleline);

        if (!m.Success)
            return null;

        var text = m.Groups[1].Value.Trim();

        return text.Length > 0 ? text : null;

    }

    private static string? Attribute(string xml, string name)
    {
        var m = Regex.Match(xml, @"^<error\b[^>]*?\s" + name + @"\s*=\s*['""]([^'""]*)['""]");
        return m.Success ? m.Groups[1].Value : null;
    }

    public override string ToString()
        => Text is null
               ? $"{Condition} ({Type.ToString().ToLowerInvariant()})"
               : $"{Condition} ({Type.ToString().ToLowerInvariant()}): {Text}";

}
