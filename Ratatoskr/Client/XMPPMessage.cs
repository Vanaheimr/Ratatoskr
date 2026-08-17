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
/// A received chat message.
/// </summary>
/// <param name="From">Sender (full JID)</param>
/// <param name="To">Recipient (usually one's own full JID)</param>
/// <param name="Body">Message text</param>
/// <param name="MessageId">Stanza ID, if the sender set one</param>
/// <param name="Timestamp">
/// When the message <b>came into being</b>, on the local clock: the stamp from
/// XEP-0203 if it carries one, otherwise the moment of reception.
///
/// Until D59 this always held the reception. For everything live that is the
/// same thing; for a delivered-late message it was wrong, and in the most
/// unpleasant way: the time of day stood there and was not true.
/// </param>
/// <param name="Type">
/// The kind of the message (RFC 6121, section 5.2.2). Without it the line from
/// a room could not be told apart from the line of an acquaintance - and with
/// the room the sender is not even a human being but the room itself.
/// </param>
/// <param name="ReceivedAt">
/// When it arrived here. If this differs from <paramref name="Timestamp"/>, it
/// was held somewhere on the way.
/// </param>
/// <param name="DelayedBy">
/// Who held it, if they said so (XEP-0203, section 4) - the server, a room.
/// Voluntary, therefore often null, even for a delivered-late message.
/// </param>
/// <param name="ReplacesId">
/// XEP-0308: The <c>id</c> of the message this one replaces - or null for an
/// ordinary one. The <c>Body</c> is then, too, the complete new text and not
/// the change to it.
/// </param>
public sealed record XMPPMessage(JID          From,
                                 JID          To,
                                 string       Body,
                                 string?      MessageId,
                                 DateTime     Timestamp,
                                 MessageType  Type        = MessageType.Normal,
                                 DateTime?    ReceivedAt  = null,
                                 JID?         DelayedBy   = null,
                                 string?      ReplacesId  = null)
{

    /// <summary>Does this message correct an earlier one (XEP-0308)?</summary>
    public bool IsCorrection => ReplacesId is not null;

    /// <summary>
    /// Sender without resource.
    /// </summary>
    public JID FromBareJid => From.Bare;

    /// <summary>
    /// Was this message held and delivered late?
    /// </summary>
    /// <remarks>
    /// By the time difference and not by <see cref="DelayedBy"/>: the
    /// <c>from</c> of the stamp is voluntary, so its absence says nothing. The
    /// comparison is the only evidence that always exists.
    /// </remarks>
    public bool IsDelayed
        => ReceivedAt.HasValue && ReceivedAt.Value != Timestamp;

}
