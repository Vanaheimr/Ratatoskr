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
using System.Text.RegularExpressions;
using System.Xml.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// XEP-0203: The note that a stanza did <b>not</b> come into being just now.
/// </summary>
/// <remarks>
/// The server sets it when it delivers something afterwards that it had kept -
/// a message from the offline storage, for instance. Without it the storage
/// would not be recognisable: what comes in at sign-on would look as though it
/// had just been written.
///
/// <b>This server has written the stamp all along and has never read it
/// itself.</b> The consequence was a lie with a time of day: a message from
/// yesterday evening appeared after the sign-on with the time of now. The
/// sender had said when it was really written; the recipient did not listen
/// (see D59).
/// </remarks>
public static class DelayedDelivery
{

    /// <summary>
    /// The namespace of XEP-0203.
    /// </summary>
    public const string Namespace = "urn:xmpp:delay";

    /// <summary>
    /// Reads the stamp of a stanza.
    /// </summary>
    /// <param name="stanza">The stanza.</param>
    /// <param name="stamp">When it came into being.</param>
    /// <param name="by">
    /// Who kept it - the server, a room. Voluntary per section 4.
    /// </param>
    /// <returns>false when it carries none or it is unreadable.</returns>
    /// <remarks>
    /// <b>Only direct children.</b> A carbon (XEP-0280) or a forwarded message
    /// (XEP-0297) brings a stamp of its own along in its
    /// <c>&lt;forwarded/&gt;</c> - that of the <i>inner</i> message. Whoever
    /// searches the whole stanza dates the outer one to the time of the inner
    /// one and is wrong precisely when it matters.
    ///
    /// An unreadable stamp counts like none. It comes from the other side, and
    /// what comes from there must overturn nothing here; the message is then
    /// just as old as it arrived.
    /// </remarks>
    public static bool TryRead(XElement stanza, out DateTimeOffset stamp, out string? by)
    {

        stamp  = default;
        by     = null;

        var delay = stanza.Child(Namespace, "delay");

        if (delay is null)
            return false;

        var value = delay.Attribute("stamp")?.Value;

        if (string.IsNullOrEmpty(value))
            return false;

        // XEP-0203, section 3 demands the form from XEP-0082, that is, RFC 3339
        // in UTC - and thereby a zone specification. Without it the stamp cannot
        // be evaluated: a time of day from a foreign machine whose zone one does
        // not know is no time of day. To read it as a local one would be the
        // worst choice - then the message shifts by exactly the zone difference,
        // and unnoticed at that.
        if (!Regex.IsMatch(value, @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase))
            return false;

        // RoundtripKind holds the zone part fast instead of interpreting it.
        // What is meant stands, after the check above, in the string in every
        // case and not in the surroundings.
        if (!DateTimeOffset.TryParse(value,
                                     CultureInfo.InvariantCulture,
                                     DateTimeStyles.RoundtripKind,
                                     out stamp))
        {
            return false;
        }

        by = delay.Attribute("from")?.Value;

        return true;

    }

}
