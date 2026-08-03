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
/// XEP-0280: Message carbons - mirrors messages onto all of one's own devices.
/// </summary>
public sealed class CarbonManager
{

    /// <summary>The namespace of XEP-0280.</summary>
    public const string Namespace = "urn:xmpp:carbons:2";

    /// <summary>The namespace of XEP-0297, in which the message sits.</summary>
    public const string ForwardNamespace = "urn:xmpp:forward:0";

    private readonly string _myBareJid;
    private bool _enabled;

    public bool IsEnabled => _enabled;

    public event Action<CarbonMessage>? OnCarbonReceived;
    public event Action<string>? OnParseError;

    public CarbonManager(string myBareJid)
    {
        _myBareJid = JidUtilities.Bare(myBareJid);
    }

    public void SetEnabled(bool enabled) => _enabled = enabled;

    /// <summary>
    /// Processes a carbon message with spoofing protection.
    ///
    /// The distinction from XEP-0184 used to run over an exclusion
    /// (<c>!messageXml.Contains("urn:xmpp:receipts")</c>), because both
    /// extensions know a <c>&lt;received/&gt;</c>. With the namespace at the
    /// element the distinction is possible directly and without side effects.
    /// </summary>
    public CarbonResult ProcessCarbon(XElement message, string from)
    {

        var bareFrom = JidUtilities.Bare(from);

        // CRITICAL SPOOFING PROTECTION:
        // carbons may come ONLY from one's own bare JID (= from the server)!
        if (!string.Equals(bareFrom, _myBareJid, StringComparison.OrdinalIgnoreCase))
            return CarbonResult.SpoofingDetected;

        var carbonElement = message.Elements()
                                   .FirstOrDefault(child => child.Name.NamespaceName == Namespace &&
                                                            (child.Name.LocalName == "sent" ||
                                                             child.Name.LocalName == "received"));

        if (carbonElement is null)
            return CarbonResult.NotACarbon;

        var isSent = carbonElement.Name.LocalName == "sent";

        var inner = carbonElement.Elements()
                                 .FirstOrDefault(child => child.Name.NamespaceName == ForwardNamespace &&
                                                          child.Name.LocalName     == "forwarded")
                                ?.Elements()
                                 .FirstOrDefault(child => child.Name.LocalName == "message");

        if (inner is null)
        {
            OnParseError?.Invoke("carbon without an embedded message");
            return CarbonResult.ParseError;
        }

        var originalFrom  = inner.Attr("from");
        var originalTo    = inner.Attr("to");

        if (originalFrom is null && originalTo is null)
        {
            OnParseError?.Invoke("could not extract from/to out of the carbon");
            return CarbonResult.ParseError;
        }

        OnCarbonReceived?.Invoke(new CarbonMessage(isSent,
                                                   originalFrom ?? "",
                                                   originalTo   ?? "",
                                                   inner.ChildValue("body"),
                                                   inner.Attr("id")));

        return CarbonResult.Success;

    }

    /// <summary>
    /// Creates the IQ for enabling carbons
    /// </summary>
    public static string EnableIq(string id = "carbons-enable")
    {
        return $"<iq type='set' id='{id}'>" +
               $"<enable xmlns='{Namespace}'/>" +
               $"</iq>";
    }

}
