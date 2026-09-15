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
using System.Xml.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// A part of a body, in the indices .NET counts in.
/// </summary>
/// <remarks>
/// <b>Deliberately not the numbers from the wire.</b> Those are characters in
/// the sense of XEP-0426; these are places in a <c>string</c>. Keeping the two
/// apart in the type is the cheapest way to stop them being confused, because
/// confusing them costs nothing until somebody quotes a text with an emoji in
/// it.
/// </remarks>
/// <param name="Start">The first index that belongs to the part.</param>
/// <param name="End">The index after the last one - as everywhere else.</param>
public readonly record struct BodyRange(int Start, int End)
{

    /// <summary>
    /// How many .NET chars the part is long.
    /// </summary>
    public int Length => End - Start;

}


/// <summary>
/// XEP-0428: which part of a body is only there for those who do not
/// understand the message.
/// </summary>
/// <remarks>
/// A message may say the same thing twice: once so that every client can show
/// something, and once in a form only a client that knows the extension can
/// read. The quotation of XEP-0461 is exactly that - the <c>&lt;reply/&gt;</c>
/// carries the reference, and the <c>&gt; </c> lines in the body carry it again
/// for everybody else.
///
/// <b>The point is that both sides can be right.</b> Whoever knows the
/// extension hides the duplicate; whoever does not shows it and loses nothing.
/// Without the indication the second kind is the only kind, and every reply
/// arrives with its quotation stuck to the front of it - which is what chat
/// looked like before, and why it looked like that.
///
/// What is read here is one contiguous part of one <c>&lt;body/&gt;</c>. The
/// specification allows several, and a subject as well; a quotation is one
/// piece at the front, and more than that would be a list where a range does.
/// </remarks>
public static class FallbackIndication
{

    /// <summary>
    /// The namespace of XEP-0428.
    /// </summary>
    public const string Namespace = "urn:xmpp:fallback:0";

    /// <summary>
    /// The indication for a part of the body.
    /// </summary>
    /// <param name="forNamespace">
    /// Whose fallback this is - the namespace of the extension that says the
    /// same thing properly.
    /// </param>
    /// <param name="body">The complete body the part lies in.</param>
    /// <param name="range">The part, in .NET indices.</param>
    /// <remarks>
    /// <b>The conversion happens here and nowhere else.</b> The caller counts
    /// in the units its language gives it, the wire wants the units of
    /// XEP-0426, and a method that took the wire numbers straight would be a
    /// method that any call site can get wrong quietly. One place that converts
    /// is one place to check.
    /// </remarks>
    public static string BodyXml(string     forNamespace,
                                 string     body,
                                 BodyRange  range)
    {

        var start = CharacterCounting.Characters(body, range.Start);
        var end   = CharacterCounting.Characters(body, range.End);

        return $"<fallback xmlns='{Namespace}' for='{XmlEscaping.Escape(forNamespace)}'>" +
                   $"<body start='{start}' end='{end}'/>" +
               $"</fallback>";

    }

    /// <summary>
    /// Which part of this body is fallback for that extension - or null, when
    /// none is or the statement cannot be used.
    /// </summary>
    /// <param name="message">The message stanza.</param>
    /// <param name="forNamespace">The extension being asked about.</param>
    /// <param name="body">The body, as it came out of the parser.</param>
    /// <remarks>
    /// <b>Only direct children</b>, for the reason the delay stamp has it
    /// (D59): a carbon brings a whole message of its own along in its
    /// <c>&lt;forwarded/&gt;</c>, and that one's indications are about that
    /// one's body.
    ///
    /// Missing numbers mean the whole body - section 3, and it is the sensible
    /// reading: an indication that says nothing about where says it is about
    /// everything. Numbers that cannot be read, or that run backwards, mean
    /// <b>null</b> and not a guess. Showing a quotation that should have been
    /// hidden is untidy; hiding a piece of somebody's sentence because their
    /// offsets were odd is losing what they said.
    /// </remarks>
    public static BodyRange? RangeIn(XElement  message,
                                     string    forNamespace,
                                     string    body)
    {

        var fallback = message.Children(Namespace, "fallback").
                               FirstOrDefault(element => element.Attr("for") == forNamespace);

        if (fallback is null)
            return null;

        var part = fallback.Child(Namespace, "body");

        // Section 3: no child element, or one without offsets, is about the
        // whole body.
        if (part is null)
            return fallback.Elements().Any()
                       ? null
                       : new BodyRange(0, body.Length);

        var startText = part.Attr("start");
        var endText   = part.Attr("end");

        if (startText is null && endText is null)
            return new BodyRange(0, body.Length);

        if (!TryRead(startText, out var start) ||
            !TryRead(endText,   out var end)   ||
            start > end)
        {
            return null;
        }

        return new BodyRange(CharacterCounting.Index(body, start),
                             CharacterCounting.Index(body, end));

    }

    /// <summary>
    /// An offset from the wire: an unsigned number, and nothing else.
    /// </summary>
    /// <remarks>
    /// Read with the invariant culture, because the number came from a foreign
    /// machine and not from this one's idea of how numbers are written.
    /// </remarks>
    private static bool TryRead(string? text, out int value)
    {

        value = 0;

        return text is not null &&
               int.TryParse(text,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out value);

    }

}
