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
/// XEP-0424: taking back something that was said.
/// </summary>
/// <remarks>
/// <b>Not a correction with an empty text.</b> XEP-0308 says "what I wrote was
/// this instead"; this says "forget that I wrote it". The difference matters to
/// the person reading: a corrected line is still a line somebody said, and a
/// retracted one is a claim that it should never have been there. Clients show
/// them differently and archives are allowed to treat them differently.
///
/// <b>It no longer hangs on XEP-0422.</b> Until version 0.4.0 a retraction was a
/// fastening - an <c>&lt;apply-to/&gt;</c> wrapper around a marker - and the
/// dependency was dropped in 2023. What goes on the wire now is a plain
/// <c>&lt;retract/&gt;</c>, which is why this file is short and has no
/// neighbour.
///
/// <b>What it cannot do is make anything disappear.</b> The recipient's client
/// decides what to do, the archive keeps its own copy, and a client that has
/// never heard of the extension shows the fallback body instead. So it is a
/// request and never a deletion, and nothing here pretends otherwise.
/// </remarks>
public static class MessageRetraction
{

    #region Data

    /// <summary>
    /// The namespace of XEP-0424, at version 1.
    /// </summary>
    /// <remarks>
    /// The <c>:1</c> is the interesting part. <c>urn:xmpp:message-retract:0</c>
    /// is the old shape that wrapped itself in XEP-0422, and the two are not
    /// compatible: a client speaking one sees nothing at all from a client
    /// speaking the other, rather than seeing something wrong. Only this one is
    /// implemented here.
    /// </remarks>
    public const String Namespace = "urn:xmpp:message-retract:1";

    /// <summary>
    /// What a client that has never heard of retraction shows instead.
    /// </summary>
    /// <remarks>
    /// <b>A sentence and not a <c>/me</c>.</b> The example in the specification
    /// writes it as one, which is XEP-0245 and is exactly the kind of thing a
    /// client that understands neither extension renders literally - so the
    /// reader of a client too old for retraction would be shown the characters
    /// <c>/me</c> at the front of a sentence about their own client's age.
    ///
    /// It says what happened rather than apologising for it: somebody took
    /// something back, and this reader is seeing that fact instead of the
    /// retraction working.
    /// </remarks>
    public const String FallbackBody = "A previous message was retracted, which this client cannot show properly.";

    #endregion

    #region Extras(RetractedId)

    /// <summary>
    /// The elements a retraction carries beside its body.
    /// </summary>
    /// <param name="RetractedId">
    /// The message being taken back - by the name everybody who can see it
    /// agrees on. See <see cref="XMPPMessage.RetractableId"/>, which works it
    /// out.
    /// </param>
    /// <remarks>
    /// Three things, and each is there for somebody different:
    ///
    /// <list type="bullet">
    ///   <item>the <c>&lt;retract/&gt;</c> for a client that understands it;</item>
    ///   <item>the <c>&lt;fallback/&gt;</c> for one that does not - it marks the
    ///         whole body as the substitute, so a client which understands
    ///         XEP-0428 but not this can at least hide the sentence rather than
    ///         show it as something somebody typed;</item>
    ///   <item>the <c>&lt;store/&gt;</c> hint (XEP-0334) for the archive, because
    ///         a message with no body of its own is exactly the kind a server
    ///         decides not to keep - and the archiving service MUST store the
    ///         retraction, whatever it does with what was retracted.</item>
    /// </list>
    ///
    /// The <c>&lt;fallback/&gt;</c> carries no range: with no <c>&lt;body/&gt;</c>
    /// child it means the whole body, which is what this is.
    /// </remarks>
    public static String Extras(String RetractedId)

        => $"<retract id='{XmlEscaping.Escape(RetractedId)}' xmlns='{Namespace}'/>" +
           $"<fallback xmlns='{FallbackIndication.Namespace}' for='{Namespace}'/>" +
            "<store xmlns='urn:xmpp:hints'/>";

    #endregion

    #region RetractedId(Message)

    /// <summary>
    /// The message this one takes back, or null when it takes back nothing.
    /// </summary>
    /// <remarks>
    /// <b>An id and not a Boolean</b>, because a retraction naming nothing is
    /// not a retraction: there would be no way to say which line it is about,
    /// and a client acting on it would have to guess - most likely at the last
    /// one, which is the one case where guessing looks right often enough to be
    /// trusted.
    /// </remarks>
    public static String? RetractedId(XElement Message)
    {

        var retract = Message.Child(Namespace, "retract");

        var id      = retract?.Attr("id");

        return String.IsNullOrEmpty(id)
                   ? null
                   : id;

    }

    #endregion

}
