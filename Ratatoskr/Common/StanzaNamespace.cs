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
/// The namespace a stanza sits in - and how it travels along when moving from
/// one stream to another.
/// </summary>
/// <remarks>
/// RFC 6120, section 4.8.1 gives every stream a content namespace:
/// <c>jabber:client</c> on the client connection, <c>jabber:server</c> between
/// servers. Over TCP it appears once on the <c>&lt;stream:stream&gt;</c> and
/// applies to everything inside; a stanza itself never carries it there.
///
/// Two places break that convenience open:
///
/// <list type="bullet">
///   <item>
///     <b>WebSocket</b> (RFC 7395, section 3.3.3) has no enclosing element.
///     Every frame has to be readable on its own, "complete with all relevant
///     namespace and language declarations" - a stanza without a declaration of
///     its own sits in <i>no</i> namespace there.
///   </item>
///   <item>
///     <b>The domain boundary.</b> What comes in from a client sits in
///     <c>jabber:client</c>; out it goes on a stream that speaks
///     <c>jabber:server</c>. If the stanza carries its old namespace along, it
///     is no longer a valid stanza on the new stream.
///   </item>
/// </list>
///
/// Neither ever showed up against our own server, because it recognises stanzas
/// by their local name and never looks at the namespace at all. Prosody does
/// look: a bind IQ without a namespace it answered with
/// <c>&lt;unsupported-stanza-type/&gt;</c>, and a <c>jabber:client</c> IQ on the
/// S2S stream with an error.
/// </remarks>
internal static class StanzaNamespace
{

    /// <summary>The content namespace of the client connection.</summary>
    public const String Client = "jabber:client";

    /// <summary>The content namespace between servers.</summary>
    public const String Server = "jabber:server";


    #region Apply(xml, ns)

    /// <summary>
    /// Sets the namespace of a stanza to <paramref name="ns"/>.
    /// </summary>
    /// <remarks>
    /// Only <c>message</c>, <c>presence</c> and <c>iq</c> are touched. Nonzas
    /// bring their namespace along themselves and keep it - re-hanging an
    /// <c>&lt;enable/&gt;</c> onto <c>jabber:client</c> would make it
    /// unreadable.
    ///
    /// What is inspected is exclusively the start tag of the root element. A
    /// mere "there is an xmlns somewhere" would fall for the first child
    /// element - with the bind IQ, for instance, for
    /// <c>&lt;bind xmlns='…xmpp-bind'/&gt;</c>, and exactly that stanza would
    /// then go untreated.
    ///
    /// If the desired namespace is already there, the string comes back
    /// unchanged - including its choice of quotation marks.
    /// </remarks>
    public static String Apply(String xml, String ns)
    {

        if (String.IsNullOrEmpty(xml))
            return xml;

        var i = 0;
        while (i < xml.Length && Char.IsWhiteSpace(xml[i]))
            i++;

        if (i >= xml.Length || xml[i] != '<')
            return xml;

        i++;
        var nameStart = i;

        while (i < xml.Length &&
               (Char.IsLetterOrDigit(xml[i]) || xml[i] == '-' || xml[i] == '_' || xml[i] == ':'))
            i++;

        var nameEnd = i;

        if (xml[nameStart..nameEnd] is not ("message" or "presence" or "iq"))
            return xml;

        // Walk the start tag, minding quotation marks: an attribute value may
        // contain a '>'.
        var quote           = '\0';
        var attributeStart  = -1;
        var attributeEnd    = -1;
        var valueStart      = -1;
        var valueEnd        = -1;

        while (i < xml.Length)
        {

            var c = xml[i];

            if (quote != '\0')
            {

                if (c == quote)
                {

                    quote = '\0';

                    if (attributeStart >= 0 && valueEnd < 0)
                    {
                        valueEnd        = i;
                        attributeEnd    = i + 1;
                        break;
                    }

                }

            }

            else if (c is '\'' or '"')
            {

                quote = c;

                if (attributeStart >= 0 && valueStart < 0)
                    valueStart = i + 1;

            }

            else if (c == '>')
                break;

            // Exactly "xmlns", not "xmlns:something" - a prefix declares no
            // default namespace and is none of this function's business.
            else if (c == 'x' &&
                     attributeStart < 0 &&
                     Char.IsWhiteSpace(xml[i - 1]) &&
                     xml.AsSpan(i).StartsWith("xmlns", StringComparison.Ordinal) &&
                     i + 5 < xml.Length &&
                     (xml[i + 5] == '=' || Char.IsWhiteSpace(xml[i + 5])))
            {
                attributeStart = i;
            }

            i++;

        }

        // No namespace present: insert one behind the element name.
        if (attributeStart < 0 || valueStart < 0 || valueEnd < 0)
            return String.Concat(xml.AsSpan(0, nameEnd),
                                 $" xmlns='{ns}'",
                                 xml.AsSpan(nameEnd));

        // Already the right one: touch nothing, not even the quotation marks.
        if (xml.AsSpan(valueStart, valueEnd - valueStart).SequenceEqual(ns))
            return xml;

        return String.Concat(xml.AsSpan(0, attributeStart),
                             $"xmlns='{ns}'",
                             xml.AsSpan(attributeEnd));

    }

    #endregion

}
