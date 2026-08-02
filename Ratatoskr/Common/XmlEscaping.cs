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
/// Escaping of XML special characters, for attribute values and text content.
///
/// Replaces the private XmlEscape helpers that used to be duplicated across six
/// classes. The old copies in PingManager, DiscoManager and ChatMarkers did not
/// escape the double quote - harmless for the stanzas built in here (all
/// attributes use single quotes), but inconsistent.
/// </summary>
public static class XmlEscaping
{
    public static string Escape(string text) =>
        text.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("'", "&apos;")
            .Replace("\"", "&quot;");

    /// <summary>
    /// The way back - for the places that read a stanza with a pattern instead
    /// of taking it apart.
    /// </summary>
    /// <remarks>
    /// <b>The ampersand last</b>, and that is the whole of the care needed
    /// here: whoever replaces it first turns <c>&amp;amp;lt;</c> into a
    /// <c>&lt;</c> - a text about a character becomes the character. An XML
    /// reader does not have this problem; a pattern over the raw frame does.
    /// </remarks>
    public static string Unescape(string text) =>
        text.Replace("&lt;",   "<")
            .Replace("&gt;",   ">")
            .Replace("&apos;", "'")
            .Replace("&quot;", "\"")
            .Replace("&amp;",  "&");
}
