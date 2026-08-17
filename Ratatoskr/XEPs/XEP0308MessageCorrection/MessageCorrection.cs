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
/// XEP-0308: "I meant: tomorrow." - one message replaces the previous one.
/// </summary>
/// <remarks>
/// The correction is an ordinary message with an <c>id</c> of its own and a
/// complete <c>&lt;body/&gt;</c>; the <c>&lt;replace/&gt;</c> only names which
/// one it supersedes. <b>That is deliberate:</b> a recipient who does not know
/// the extension displays it as a new message - unlovely, but complete.
/// Whoever sent only the changed part instead would leave an empty line at
/// theirs.
///
/// <b>Only the last one.</b> Section 5 permits a correction only for the
/// message sent last to the same recipient. The reason is dependability:
/// without a limit every recipient would have to keep their whole history and
/// would never be allowed to regard anything as final. This side keeps to it
/// when sending; when receiving, the replacement is reported and the decision
/// left to the interface - a console cannot take back what has been written
/// anyway.
/// </remarks>
public static class MessageCorrection
{

    /// <summary>
    /// The namespace of XEP-0308.
    /// </summary>
    public const string Namespace = "urn:xmpp:message-correct:0";

    /// <summary>
    /// The <c>&lt;replace/&gt;</c> for a correction.
    /// </summary>
    /// <param name="replacesId">The <c>id</c> of the message that is replaced.</param>
    public static string ReplaceXml(string replacesId)
        => $"<replace id='{XmlEscaping.Escape(replacesId)}' xmlns='{Namespace}'/>";

    /// <summary>
    /// Which message this one replaces, or null.
    /// </summary>
    /// <remarks>
    /// <b>Only direct children</b> - for the same reason as with the delay
    /// stamp (XEP-0203, see D59): a carbon brings a message of its own along in
    /// its <c>&lt;forwarded/&gt;</c>, and its correction note does not belong
    /// to the outer one.
    ///
    /// An empty <c>id</c> counts like none. It points at nothing, and a
    /// replacement without a target is none.
    /// </remarks>
    public static string? ReplacedId(XElement message)
    {

        var replace = message.Child(Namespace, "replace");

        if (replace is null)
            return null;

        var id = replace.Attribute("id")?.Value;

        return string.IsNullOrEmpty(id) ? null : id;

    }

}
