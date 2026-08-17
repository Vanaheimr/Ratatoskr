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
/// A key for exactly one device of a recipient.
/// </summary>
/// <param name="DeviceId">The device identifier (<c>rid</c>).</param>
/// <param name="Data">
/// The 48 bytes encrypted by the ratchet - depending on the case as an
/// <c>OMEMOAuthenticatedMessage</c> or as an <c>OMEMOKeyExchange</c>.
/// </param>
/// <param name="IsKeyExchange">
/// Does this entry carry a key exchange (<c>kex='true'</c>)?
/// </param>
public sealed record OmemoKey(UInt32 DeviceId, Byte[] Data, Boolean IsKeyExchange);

/// <summary>
/// The <c>&lt;encrypted/&gt;</c> element (XEP-0384, section 4.5).
/// </summary>
/// <param name="SenderDeviceId">One's own device (<c>sid</c>).</param>
/// <param name="Keys">
/// Per recipient JID the keys for their devices.
/// </param>
/// <param name="Payload">
/// The encrypted payload, or null for a message without content.
/// </param>
/// <remarks>
/// <b>Why the recipients are grouped by JID.</b> A message goes to all devices
/// of all participants - also to one's own, otherwise one's own computer would
/// not see what one's own telephone has written. The grouping holds fast
/// <i>whose</i> device is meant, and that is more than tidiness: without it a
/// key entry could be given out for a device that belongs to somebody else
/// entirely.
///
/// <b>A message without a <c>&lt;payload/&gt;</c> is no error.</b> It means "I
/// have rebuilt the session" and carries nothing but the key exchange - that is
/// how a counterpart gets a session without a human being having to write
/// anything.
/// </remarks>
public sealed record OmemoEncryptedElement(UInt32                                        SenderDeviceId,
                                           IReadOnlyDictionary<JID, IReadOnlyList<OmemoKey>>     Keys,
                                           Byte[]?                                       Payload)
{

    /// <summary>
    /// The namespace of OMEMO 2.
    /// </summary>
    public const String Namespace = "urn:xmpp:omemo:2";

    #region ToXml()

    /// <summary>
    /// The element as XML.
    /// </summary>
    public XElement ToXml()
    {

        XNamespace ns = Namespace;

        var header = new XElement(ns + "header", new XAttribute("sid", SenderDeviceId));

        foreach (var (jid, deviceKeys) in Keys)
        {

            var keys = new XElement(ns + "keys", new XAttribute("jid", jid));

            foreach (var k in deviceKeys)
                keys.Add(new XElement(ns + "key",
                                      new XAttribute("rid", k.DeviceId),
                                      // The attribute stands only where it says
                                      // something: section 4.5 gives it the
                                      // default value 'false', and a written-out
                                      // default value is a line that travels
                                      // along with every message without ever
                                      // meaning anything.
                                      k.IsKeyExchange ? new XAttribute("kex", "true") : null,
                                      Convert.ToBase64String(k.Data)));

            header.Add(keys);

        }

        var encrypted = new XElement(ns + "encrypted", header);

        if (Payload is not null)
            encrypted.Add(new XElement(ns + "payload", Convert.ToBase64String(Payload)));

        return encrypted;

    }

    #endregion

    #region TryRead(stanza, out ...)

    /// <summary>
    /// Reads an <c>&lt;encrypted/&gt;</c> out of a stanza.
    /// </summary>
    /// <remarks>
    /// <b>Only direct children</b> - the same trap as with the delay stamp
    /// (D59) and with the correction (D60): a carbon brings a complete message
    /// of its own along in its <c>&lt;forwarded/&gt;</c>, and its encryption
    /// does not belong to the outer one.
    ///
    /// What cannot be read yields <c>false</c> and no exception: an
    /// unintelligible message is, for the recipient, the same as none, and a
    /// crash would be the worse answer - it could be triggered by anyone who
    /// sends a <c>&lt;key/&gt;</c> with crooked base64.
    /// </remarks>
    public static Boolean TryRead(XElement stanza, out OmemoEncryptedElement? element)
    {

        element = null;

        var encrypted = stanza.Child(Namespace, "encrypted");

        if (encrypted is null)
            return false;

        try
        {

            var header = encrypted.Child(Namespace, "header");

            if (header is null || !UInt32.TryParse(header.Attr("sid"), out var sid))
                return false;

            // No comparer: the JID compares itself, and by RFC 7622 - which
            // OrdinalIgnoreCase over a whole address was not.
            var all = new Dictionary<JID, IReadOnlyList<OmemoKey>>();

            foreach (var keys in header.Elements().Where(e => e.Name.LocalName == "keys"))
            {

                // An address this side cannot read makes the header unusable:
                // whom these keys are for would be a guess, and guessing wrong
                // here means reaching for somebody else's key material.
                if (!JID.TryParse(keys.Attr("jid"), out var jid))
                    return false;

                var list = new List<OmemoKey>();

                foreach (var key in keys.Elements().Where(e => e.Name.LocalName == "key"))
                {

                    if (!UInt32.TryParse(key.Attr("rid"), out var rid))
                        return false;

                    list.Add(new OmemoKey(rid,
                                           Convert.FromBase64String(key.Value.Trim()),
                                           key.Attr("kex") is "true" or "1"));

                }

                all[jid] = list;

            }

            var payload = encrypted.Child(Namespace, "payload")?.Value.Trim();

            element = new OmemoEncryptedElement(
                          sid,
                          all,
                          String.IsNullOrEmpty(payload) ? null : Convert.FromBase64String(payload));

            return true;

        }
        catch (Exception)
        {
            return false;
        }

    }

    #endregion

    #region KeyFor(jid, deviceId)

    /// <summary>
    /// The entry for this device of this JID, or null.
    /// </summary>
    /// <remarks>
    /// Both together and not only the device identifier: two accounts can carry
    /// the same identifier - it is a random number per device and known to
    /// nobody else. Whoever searched only by it would under some circumstances
    /// take the entry that was meant for a foreign account, and would then
    /// founder on a decryption whose reason they do not see.
    /// </remarks>
    public OmemoKey? KeyFor(JID bareJid, UInt32 deviceId)
        => Keys.TryGetValue(bareJid, out var list)
               ? list.FirstOrDefault(k => k.DeviceId == deviceId)
               : null;

    #endregion

}
