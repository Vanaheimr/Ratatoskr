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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using org.GraphDefined.Vanaheimr.Illias;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

#region (delegate) OnCarbon...Delegate

/// <summary>
/// A message was mirrored from or to another device of our own.
/// </summary>
public delegate Task OnCarbonReceivedDelegate   (DateTimeOffset     Timestamp,
                                                 CarbonManager      Sender,
                                                 CarbonMessage      Carbon,
                                                 CancellationToken  CancellationToken);

/// <summary>
/// A carbon arrived that could not be taken apart.
/// </summary>
public delegate Task OnCarbonParseErrorDelegate (DateTimeOffset     Timestamp,
                                                 CarbonManager      Sender,
                                                 String             Reason,
                                                 CancellationToken  CancellationToken);

#endregion


/// <summary>
/// XEP-0280: Message carbons - mirrors messages onto all of one's own devices.
/// </summary>
public sealed class CarbonManager
{

    /// <summary>The namespace of XEP-0280.</summary>
    public const string Namespace = "urn:xmpp:carbons:2";

    /// <summary>The namespace of XEP-0297, in which the message sits.</summary>
    public const string ForwardNamespace = "urn:xmpp:forward:0";

    private readonly JID      _myBareJid;
    private readonly ILogger  _logger;
    private bool _enabled;

    public bool IsEnabled => _enabled;

    public event OnCarbonReceivedDelegate?    OnCarbonReceived;
    public event OnCarbonParseErrorDelegate?  OnParseError;

    public CarbonManager(JID       myBareJid,
                         ILogger?  logger   = null)
    {
        _myBareJid  = myBareJid.Bare;
        _logger     = logger ?? NullLogger.Instance;
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
    public async Task<CarbonResult> ProcessCarbonAsync(XElement           message,
                                                       JID                from,
                                                       CancellationToken  CancellationToken   = default)
    {

        // CRITICAL SPOOFING PROTECTION:
        // carbons may come ONLY from one's own bare JID (= from the server)!
        if (!IsFromOwnAccount(from))
            return CarbonResult.SpoofingDetected;

        var carbonElement = CarbonElement(message);

        if (carbonElement is null)
            return CarbonResult.NotACarbon;

        var isSent = carbonElement.Name.LocalName == "sent";

        var inner = InnerMessage(carbonElement);

        if (inner is null)
        {
            await OnParseError.InvokeAllAsync(handler => handler(Timestamp.Now, this, "carbon without an embedded message", CancellationToken), _logger);
            return CarbonResult.ParseError;
        }

        // Addresses, not text. An unreadable one is a parse error like any
        // other: a carbon whose sender this side cannot name is one it cannot
        // file under any conversation, and letting it through with the raw
        // characters only postpones that to whoever displays it.
        var originalFrom  = JID.TryParse(inner.Attr("from"));
        var originalTo    = JID.TryParse(inner.Attr("to"));

        if (originalFrom is null && originalTo is null)
        {
            await OnParseError.InvokeAllAsync(handler => handler(Timestamp.Now, this, "could not extract from/to out of the carbon", CancellationToken), _logger);
            return CarbonResult.ParseError;
        }

        var carbon = new CarbonMessage(isSent,
                                       originalFrom ?? default,
                                       originalTo   ?? default,
                                       inner.ChildValue("body"),
                                       inner.Attr("id"));

        await OnCarbonReceived.InvokeAllAsync(handler => handler(Timestamp.Now, this, carbon, CancellationToken), _logger);

        return CarbonResult.Success;

    }

    /// <summary>
    /// Did this stanza come from one's own account - which for a carbon means:
    /// from one's own server?
    /// </summary>
    private bool IsFromOwnAccount(JID from)

        => from.Bare == _myBareJid;

    /// <summary>
    /// The <c>&lt;sent/&gt;</c> or <c>&lt;received/&gt;</c> of a carbon, or null
    /// when this message is none.
    /// </summary>
    private static XElement? CarbonElement(XElement message)

        => message.Elements()
                  .FirstOrDefault(child => child.Name.NamespaceName == Namespace &&
                                           (child.Name.LocalName == "sent" ||
                                            child.Name.LocalName == "received"));

    /// <summary>
    /// The message wrapped in a carbon element.
    /// </summary>
    /// <remarks>
    /// Direct children at every step, and that is the point of walking it here
    /// rather than searching. A search through all descendants finds a
    /// <c>&lt;forwarded/&gt;</c> that somebody hung anywhere inside an ordinary
    /// message - and whatever is found that way gets treated as something one's
    /// own server vouched for.
    /// </remarks>
    private static XElement? InnerMessage(XElement carbonElement)

        => carbonElement.Elements()
                        .FirstOrDefault(child => child.Name.NamespaceName == ForwardNamespace &&
                                                 child.Name.LocalName     == "forwarded")
                       ?.Elements()
                        .FirstOrDefault(child => child.Name.LocalName == "message");

    /// <summary>
    /// The message a carbon carries - <b>only</b> when the carbon may be
    /// believed.
    /// </summary>
    /// <remarks>
    /// It exists so that the check and the unwrapping cannot come apart. The
    /// OMEMO branch in <c>ProcessMessage</c> used to do its own unwrapping, and
    /// with it its own reading of what a carbon is: it looked for the carbons
    /// namespace anywhere in the stanza and for a <c>&lt;forwarded/&gt;</c>
    /// among all descendants, and it did all that <i>before</i> anybody had
    /// asked where the stanza came from. So the one path on which a wrapped
    /// message got decrypted was the one path with no sender check on it at
    /// all - XEP-0280's only real rule, missing exactly where it was needed.
    /// </remarks>
    public XElement? UnwrapVerified(XElement message, JID from)

        => IsFromOwnAccount(from) && CarbonElement(message) is XElement carbon
               ? InnerMessage(carbon)
               : null;

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
