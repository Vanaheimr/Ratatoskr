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
/// The name of the outermost element of a frame.
/// </summary>
/// <remarks>
/// Sounds like a detail, and is the difference between a dispatch that decides
/// and one that guesses. A comparison with <c>StartsWith("&lt;iq")</c> also
/// matches <c>&lt;iqbogus/&gt;</c>, <c>StartsWith("&lt;presence")</c> also
/// <c>&lt;presence-probe/&gt;</c>, and <c>StartsWith("&lt;open")</c> also
/// <c>&lt;opencast/&gt;</c>. The name ends at the first character that no
/// longer belongs to it, and exactly that far is what has to be read.
///
/// This reading already existed in the house — in
/// <c>StreamManagementManager.IsCountableStanza</c>, where it answers whether a
/// frame counts for XEP-0198. That the dispatch next to it was guessing was not
/// for lack of knowledge, but because the knowledge sat in the wrong place.
/// </remarks>
public static class StanzaElement
{

    /// <summary>
    /// The name of the outermost element, without a namespace prefix — or
    /// <c>null</c> if the frame does not begin with an element.
    /// </summary>
    /// <remarks>
    /// The prefix is dropped because it does not change the type: RFC 6120,
    /// section 4.8.1 fixes the namespace and not the abbreviation it is
    /// addressed by. <c>&lt;client:iq/&gt;</c> is an <c>iq</c>.
    /// </remarks>
    public static String? NameOf(String xml)
    {

        if (String.IsNullOrEmpty(xml))
            return null;

        var i = 0;

        while (i < xml.Length && Char.IsWhiteSpace(xml[i]))
            i++;

        if (i >= xml.Length || xml[i] != '<')
            return null;

        i++;

        var start = i;

        while (i < xml.Length &&
               (Char.IsLetterOrDigit(xml[i]) || xml[i] == '-' || xml[i] == '_' || xml[i] == ':'))
        {
            i++;
        }

        if (i == start)
            return null;

        var name   = xml[start..i];
        var colon  = name.LastIndexOf(':');

        return colon >= 0
                   ? name[(colon + 1)..]
                   : name;

    }

    /// <summary>
    /// Is the outermost element called that?
    /// </summary>
    public static Boolean Is(String xml, String name)

        => String.Equals(NameOf(xml), name, StringComparison.Ordinal);

    /// <summary>
    /// Is that one of the three stanzas from RFC 6120, section 8.1 —
    /// <c>message</c>, <c>presence</c> or <c>iq</c>?
    /// </summary>
    public static Boolean IsStanza(String xml)

        => NameOf(xml) is "message" or "presence" or "iq";

}
