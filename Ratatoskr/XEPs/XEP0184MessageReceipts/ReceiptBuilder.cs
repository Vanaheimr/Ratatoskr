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
/// XEP-0184: Creates and recognises receipt elements.
/// </summary>
public static class ReceiptBuilder
{

    /// <summary>
    /// The namespace of XEP-0184.
    /// </summary>
    public const string Namespace = "urn:xmpp:receipts";

    /// <summary>
    /// Creates the XML for a receipt request (to be inserted into an outgoing message)
    /// </summary>
    public static string RequestXml => $"<request xmlns='{Namespace}'/>";

    /// <summary>
    /// Creates a receipt answer
    /// </summary>
    public static string CreateReceipt(JID to, string originalMessageId)
    {
        return $"<message to='{XmlEscaping.Escape(to.ToString())}'>" +
               $"<received xmlns='{Namespace}' id='{XmlEscaping.Escape(originalMessageId)}'/>" +
               $"</message>";
    }

    /// <summary>
    /// Checks whether a message asks for a receipt.
    ///
    /// The earlier check looked literally for
    /// <c>xmlns='urn:xmpp:receipts'</c>, that is, only with single quotation
    /// marks - against a server that uses double ones every receipt stayed
    /// away. Besides that, a <c>&lt;request/&gt;</c> anywhere in the message
    /// counted, so one in a forwarded message as well.
    /// </summary>
    public static bool HasReceiptRequest(XElement message)
        => message.Elements()
                  .Any(child => child.Name.NamespaceName == Namespace &&
                                child.Name.LocalName     == "request");

    /// <summary>
    /// Extracts the receipt id out of a receipt.
    ///
    /// The namespace check separates it from the <c>&lt;received/&gt;</c> of
    /// the same name from XEP-0333.
    /// </summary>
    public static string? ExtractReceiptId(XElement message)
        => message.Elements()
                  .FirstOrDefault(child => child.Name.NamespaceName == Namespace &&
                                           child.Name.LocalName     == "received")
                  ?.Attr("id");
}
