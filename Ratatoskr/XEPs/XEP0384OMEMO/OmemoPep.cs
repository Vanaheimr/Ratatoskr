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
/// A device in the device list.
/// </summary>
/// <param name="Id">The device identifier.</param>
/// <param name="Label">
/// A name a human being can read - "telephone", "computer in the office". It is
/// voluntary and unauthenticated.
/// </param>
public sealed record OmemoDevice(UInt32 Id, String? Label = null);

/// <summary>
/// The device list of an account (XEP-0384, section 5.2).
/// </summary>
/// <remarks>
/// <b>It is public, and that is the price of the procedure.</b> Whoever wants
/// to know with how many devices somebody hangs on the net has only to query
/// this node. That cannot be avoided without giving up reachability: a sender
/// has to encrypt for every device, so they have to know every one.
///
/// The label is therefore to be chosen with care - "Achim's telephone" stands
/// there readable for everyone who fetches the node.
/// </remarks>
public sealed record OmemoDeviceList(IReadOnlyList<OmemoDevice> Devices)
{

    /// <summary>The PEP node of the device list.</summary>
    public const String Node = "urn:xmpp:omemo:2:devices";

    /// <summary>
    /// The identifier of the only entry in this node.
    /// </summary>
    /// <remarks>
    /// A fixed value and not a running number: the node carries exactly one
    /// list, and a second entry beside it would be no second list but an
    /// unclarity about which one holds.
    /// </remarks>
    public const String ItemId = "current";

    #region ToXml() / TryRead(xml, out list)

    /// <summary>The list as XML.</summary>
    public XElement ToXml()
    {

        XNamespace ns = OmemoEncryptedElement.Namespace;

        return new XElement(ns + "devices",
                            Devices.Select(d => new XElement(ns + "device",
                                                             new XAttribute("id", d.Id),
                                                             d.Label is not null
                                                                 ? new XAttribute("label", d.Label)
                                                                 : null)));

    }

    /// <summary>
    /// Reads a device list.
    /// </summary>
    /// <remarks>
    /// A device without a readable identifier is <b>passed over</b> and does not
    /// become an error of the whole list. The reason is reachability: a list
    /// with one crooked entry is still a list, and whoever discarded it entirely
    /// could no longer write to any of the remaining devices. A single unusable
    /// entry must not take all the others with it.
    /// </remarks>
    public static Boolean TryRead(XElement xml, out OmemoDeviceList? list)
    {

        list = null;

        if (xml.Name.LocalName     != "devices" ||
            xml.Name.NamespaceName != OmemoEncryptedElement.Namespace)
            return false;

        var devices = new List<OmemoDevice>();

        foreach (var device in xml.Elements().Where(e => e.Name.LocalName == "device"))
            if (UInt32.TryParse(device.Attr("id"), out var id) && id > 0)
                devices.Add(new OmemoDevice(id, device.Attr("label")));

        list = new OmemoDeviceList(devices);

        return true;

    }

    #endregion

    #region Contains(deviceId) / With(device)

    /// <summary>Does this device stand in the list?</summary>
    public Boolean Contains(UInt32 deviceId)
        => Devices.Any(d => d.Id == deviceId);

    /// <summary>
    /// The list with this device - unchanged when it already stands in it.
    /// </summary>
    /// <remarks>
    /// Adds to and does not replace: section 5.2 demands of the client that it
    /// enter itself again when it has vanished from the list - <b>without
    /// removing the others</b>. Whoever published a new list here with only
    /// their own device would turn a re-entry into a displacement of all the
    /// other devices of the human being.
    /// </remarks>
    public OmemoDeviceList With(OmemoDevice device)
        => Contains(device.Id)
               ? this
               : new OmemoDeviceList([.. Devices, device]);

    #endregion

}

/// <summary>
/// The PEP side of OMEMO: device list and bundles (XEP-0384, section 5.2).
/// </summary>
/// <remarks>
/// <b>Why the bundles get an entry of their own per device.</b> The node
/// <c>urn:xmpp:omemo:2:bundles</c> carries one entry per device, with the
/// device identifier as the entry identifier. That way a sender fetches exactly
/// the bundle they need instead of all of them - and a device that has used up
/// its prekey writes only its own entry anew and does not disturb the others.
/// </remarks>
public static class OmemoPep
{

    /// <summary>The PEP node of the bundles.</summary>
    public const String BundlesNode = "urn:xmpp:omemo:2:bundles";

    /// <summary>The namespace of XEP-0060.</summary>
    public const String PubSubNamespace = "http://jabber.org/protocol/pubsub";

    #region The bundle as XML

    /// <summary>A bundle as XML (section 5.2).</summary>
    public static XElement ToXml(this OmemoBundle bundle)
    {

        XNamespace ns = OmemoEncryptedElement.Namespace;

        return new XElement(ns + "bundle",
                            new XElement(ns + "spk",
                                         new XAttribute("id", bundle.SignedPreKeyId),
                                         Convert.ToBase64String(bundle.SignedPreKey)),
                            new XElement(ns + "spks",
                                         Convert.ToBase64String(bundle.SignedPreKeySignature)),
                            new XElement(ns + "ik",
                                         Convert.ToBase64String(bundle.IdentityKey)),
                            new XElement(ns + "prekeys",
                                         bundle.PreKeys.Select(p =>
                                             new XElement(ns + "pk",
                                                          new XAttribute("id", p.Id),
                                                          Convert.ToBase64String(p.PublicKey)))));

    }

    /// <summary>
    /// Reads a bundle.
    /// </summary>
    /// <remarks>
    /// <b>Here it is read strictly, unlike with the device list.</b> A bundle
    /// with a missing part is unusable - without an identity key the signature
    /// cannot be checked, without a signed prekey nothing can be agreed. To
    /// accept half a bundle would mean building a session on something whose
    /// origin nobody has checked.
    ///
    /// A single unreadable prekey, however, does not take the whole bundle with
    /// it: out of a hundred one suffices, and the session even comes about
    /// entirely without one.
    /// </remarks>
    public static Boolean TryReadBundle(XElement xml, out OmemoBundle? bundle)
    {

        bundle = null;

        if (xml.Name.LocalName     != "bundle" ||
            xml.Name.NamespaceName != OmemoEncryptedElement.Namespace)
            return false;

        var ns = OmemoEncryptedElement.Namespace;

        try
        {

            var spk   = xml.Child(ns, "spk");
            var spks  = xml.Child(ns, "spks")?.Value.Trim();
            var ik    = xml.Child(ns, "ik")?.Value.Trim();

            if (spk is null || String.IsNullOrEmpty(spk.Value.Trim()) ||
                String.IsNullOrEmpty(spks) || String.IsNullOrEmpty(ik) ||
                !UInt32.TryParse(spk.Attr("id"), out var spkId))
                return false;

            var preKeys = new List<OmemoPreKey>();

            foreach (var pk in xml.Child(ns, "prekeys")?.Elements()
                                                        .Where(e => e.Name.LocalName == "pk")
                                   ?? [])
            {

                if (!UInt32.TryParse(pk.Attr("id"), out var pkId))
                    continue;

                try
                {
                    preKeys.Add(new OmemoPreKey(pkId, Convert.FromBase64String(pk.Value.Trim())));
                }
                catch (FormatException)
                {
                    // One crooked prekey out of a hundred does not take the
                    // others with it.
                }

            }

            var read = new OmemoBundle(Convert.FromBase64String(ik),
                                          spkId,
                                          Convert.FromBase64String(spk.Value.Trim()),
                                          Convert.FromBase64String(spks),
                                          preKeys);

            // The lengths belong here and not with the caller.
            //
            // An empty <spk/> is valid base64 and yields a field of zero bytes -
            // that came through until a surviving mutation forced the test for
            // it. Further down it would have become an exception out of the
            // curve arithmetic, with a message that tells nobody a bundle was
            // unusable.
            if (read.IdentityKey.Length            != Curve25519.KeyLength ||
                read.SignedPreKey.Length           != Curve25519.KeyLength ||
                read.SignedPreKeySignature.Length  != Curve25519.SignatureLength)
                return false;

            bundle = read;

            return true;

        }
        catch (Exception)
        {
            return false;
        }

    }

    #endregion

    #region The IQs

    /// <summary>Publishes an entry in a PEP node of one's own.</summary>
    public static String PublishIq(String id, String node, String itemId, XElement payload)
        => $"<iq type='set' id='{XmlEscaping.Escape(id)}'>" +
           $"<pubsub xmlns='{PubSubNamespace}'>" +
           $"<publish node='{XmlEscaping.Escape(node)}'>" +
           $"<item id='{XmlEscaping.Escape(itemId)}'>{payload}</item>" +
           "</publish>" +

           // Section 5.2 demands an open access model: whoever wants to write in
           // encrypted form has to be able to read the bundle, and in case of
           // doubt that is somebody who stands in no roster yet.
           $"<publish-options><x xmlns='jabber:x:data' type='submit'>" +
           "<field var='FORM_TYPE' type='hidden'>" +
           "<value>http://jabber.org/protocol/pubsub#publish-options</value></field>" +
           "<field var='pubsub#access_model'><value>open</value></field>" +
           "</x></publish-options>" +

           "</pubsub></iq>";

    /// <summary>Fetches an entry out of the PEP node of somebody else.</summary>
    /// <param name="itemId">
    /// Which entry; without a value the one published last.
    /// </param>
    public static String FetchIq(String id, String to, String node, String? itemId = null)
        => $"<iq type='get' id='{XmlEscaping.Escape(id)}' to='{XmlEscaping.Escape(to)}'>" +
           $"<pubsub xmlns='{PubSubNamespace}'>" +
           $"<items node='{XmlEscaping.Escape(node)}'" +
           (itemId is null ? " max_items='1'>" : $"><item id='{XmlEscaping.Escape(itemId)}'/>") +
           "</items>" +
           "</pubsub></iq>";

    #endregion

}
