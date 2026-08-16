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
using System.Xml.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// XEP-0420: Stanza Content Encryption - the envelope that gets encrypted.
/// </summary>
/// <remarks>
/// <b>What is encrypted is not the text but a whole stanza envelope.</b> That
/// is the difference from older procedures, and it is the reason for this XEP:
/// whoever encrypts only the <c>&lt;body/&gt;</c> leaves everything else
/// standing in the clear - chat states, delivery receipts, correction notes -
/// and somebody reading along then knows when who is typing, what arrived and
/// what was put right. The content would be protected, the conversation not.
///
/// <b>The affix elements are against displacement.</b> A ciphertext without
/// them could be intercepted and passed on to somebody else - the encryption
/// would stay valid, the recipient would see a message that was never directed
/// at them. That is why the sender stands <b>inside</b> the envelope: outside,
/// anybody can change it.
///
/// <b>And the <c>&lt;rpad/&gt;</c> is no decoration.</b> Without it the length
/// of the ciphertext betrays the length of the message - with "yes" and "no"
/// that is the whole content. The padding has a random length so that nothing
/// can be read off the size any more.
/// </remarks>
public sealed record SceEnvelope(IReadOnlyList<XElement>  Content,
                                 String?                  From    = null,
                                 String?                  To      = null,
                                 DateTimeOffset?          Time    = null)
{

    /// <summary>The namespace of XEP-0420.</summary>
    public const String Namespace = "urn:xmpp:sce:1";

    /// <summary>The upper limit of the padding in characters (XEP-0420, section 4).</summary>
    public const Int32 MaxPadding = 200;

    #region ToXml()

    /// <summary>
    /// The envelope as XML, with freshly drawn padding.
    /// </summary>
    /// <remarks>
    /// The padding is drawn anew at every call - also for the same message. If
    /// it were not, two identical messages would again have identical length,
    /// and the measure would be ineffective exactly as far as it was meant to
    /// reach.
    /// </remarks>
    public XElement ToXml()
    {

        XNamespace ns = Namespace;

        var envelope = new XElement(ns + "envelope",
                                    new XElement(ns + "content", Content));

        if (From is not null)
            envelope.Add(new XElement(ns + "from", new XAttribute("jid", From)));

        if (To is not null)
            envelope.Add(new XElement(ns + "to", new XAttribute("jid", To)));

        if (Time.HasValue)
            envelope.Add(new XElement(ns + "time",
                                      new XAttribute("stamp",
                                                     Time.Value.ToUniversalTime()
                                                         .ToString("yyyy-MM-ddTHH:mm:ssZ"))));

        envelope.Add(new XElement(ns + "rpad", RandomPadding()));

        return envelope;

    }

    /// <summary>
    /// Random characters of random length between 0 and
    /// <see cref="MaxPadding"/>.
    /// </summary>
    /// <remarks>
    /// From the cryptographic random generator and not from
    /// <see cref="Random"/>: a predictable padding pads nothing. Whoever knows
    /// the sequence subtracts it from the length and reads the message length
    /// as before.
    /// </remarks>
    private static String RandomPadding()
    {

        const String characters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        var length = RandomNumberGenerator.GetInt32(0, MaxPadding + 1);
        var buffer = new Char[length];

        for (var i = 0; i < length; i++)
            buffer[i] = characters[RandomNumberGenerator.GetInt32(characters.Length)];

        return new String(buffer);

    }

    #endregion

    #region TryRead(xml, out envelope)

    /// <summary>
    /// Reads an envelope.
    /// </summary>
    /// <param name="expectedFrom">
    /// Whom the message comes from according to the stanza. If another sender
    /// stands in the envelope, it is refused.
    /// </param>
    /// <remarks>
    /// <b>The comparison is the purpose of the affix</b>, and it belongs here
    /// and not in the interface: an envelope whose sender nobody looks at is a
    /// field that costs space. The attack it wards off is the passing on -
    /// somebody intercepts a ciphertext and sends it on under their own name.
    /// </remarks>
    public static Boolean TryRead(XElement          xml,
                                  out SceEnvelope?  envelope,
                                  String?           expectedFrom = null)
    {

        envelope = null;

        if (xml.Name.LocalName != "envelope" || xml.Name.NamespaceName != Namespace)
            return false;

        var content = xml.Child(Namespace, "content");

        if (content is null)
            return false;

        var from = xml.Child(Namespace, "from")?.Attr("jid");
        var to   = xml.Child(Namespace, "to")?.Attr("jid");

        // A missing sender is refused and not waved through. Skipping the
        // comparison when there is nothing to compare turned the affix into
        // something the sender decides on: whoever wanted it checked wrote it
        // in, whoever did not, did not - and an attacker passing a ciphertext
        // on under their own name is exactly the second sort. The check was
        // there and it could be switched off from outside.
        //
        // This implementation has always written the sender in; python-omemo,
        // which the interop suite measures against, leaves the envelope to the
        // application entirely and therefore has no say here either.
        if (expectedFrom is not null &&
            (from is null ||
             !String.Equals(JidUtilities.Bare(from), JidUtilities.Bare(expectedFrom),
                            StringComparison.OrdinalIgnoreCase)))
            return false;

        DateTimeOffset? time = null;

        if (xml.Child(Namespace, "time")?.Attr("stamp") is String stamp &&
            DateTimeOffset.TryParse(stamp, null,
                                    System.Globalization.DateTimeStyles.RoundtripKind,
                                    out var read))
            time = read;

        envelope = new SceEnvelope([.. content.Elements()], from, to, time);

        return true;

    }

    #endregion

}
