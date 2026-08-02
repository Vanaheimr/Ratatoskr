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

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// The <c>type</c> of a message, read and written.
/// </summary>
public static class MessageTypeExtensions
{

    #region Parse(Value)

    /// <summary>
    /// Reads the <c>type</c> attribute of a message.
    /// </summary>
    /// <remarks>
    /// RFC 6121, section 5.2.2 is unusually clear at this point: if the
    /// attribute is missing <b>or the recipient does not understand its
    /// value</b>, the message MUST be treated as <c>normal</c>. An unknown
    /// value is therefore not an error and must not make the message disappear
    /// - a later extension shall simply arrive at old recipients as an
    /// ordinary message.
    /// </remarks>
    public static MessageType Parse(String? Value)

        => Value switch {
               "chat"       => MessageType.Chat,
               "groupchat"  => MessageType.GroupChat,
               "headline"   => MessageType.Headline,
               "error"      => MessageType.Error,
               _            => MessageType.Normal
           };

    #endregion

    #region AsAttribute(Type)

    /// <summary>
    /// The value for the <c>type</c> attribute, or null for
    /// <see cref="MessageType.Normal"/> - the default value is not written.
    /// </summary>
    public static String? AsAttribute(this MessageType Type)

        => Type switch {
               MessageType.Chat       => "chat",
               MessageType.GroupChat  => "groupchat",
               MessageType.Headline   => "headline",
               MessageType.Error      => "error",
               _                      => null
           };

    #endregion

    #region ExpectsAReply(Type)

    /// <summary>
    /// May a message of this kind be answered by itself - a delivery receipt
    /// (XEP-0184) or a marker (XEP-0333)?
    /// </summary>
    /// <remarks>
    /// For <see cref="MessageType.Headline"/> RFC 6121, section 5.2.2 says it
    /// itself: "no reply is expected". A delivery receipt to a news source is
    /// useless at best.
    ///
    /// For <see cref="MessageType.GroupChat"/> the reason is more tangible: the
    /// sender is the room, not a human being. A receipt would go to the room,
    /// and the room passes it on to everyone in it - a silent acknowledgement
    /// would turn into a contribution in front of an audience, and that from
    /// everyone present for every message.
    /// </remarks>
    public static Boolean ExpectsAReply(this MessageType Type)

        => Type is MessageType.Normal or MessageType.Chat;

    #endregion

}
