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
/// <param name="RepliesTo">
/// XEP-0461: Which message this one answers - or null for one that answers
/// none.
/// </param>
/// <param name="QuoteRange">
/// XEP-0428: Which part of the <c>Body</c> is only there for a client that does
/// not know XEP-0461 - the quoted lines. Null when the message brings none.
///
/// <b>The <c>Body</c> keeps them.</b> What came over the wire is what stands
/// here; <see cref="Text"/> and <see cref="Quote"/> are the two views of it.
/// Cutting at the parser would mean the one thing an interface may need most -
/// the message exactly as it was sent - is the one thing it can no longer get.
/// </param>
/// <param name="OriginId">
/// XEP-0359: The name the sender gave this message themselves, or null.
/// </param>
/// <param name="StanzaId">
/// XEP-0359: The name the sender's own domain gave it - for a room message, the
/// room's. Null when nobody assigned one.
/// </param>
/// <param name="InARoom">
/// Whether <see cref="From"/> is a room this client is standing in.
/// </param>
/// <remarks>
/// <b>Not read off the stanza, because it cannot be.</b> A private message in a
/// room (XEP-0045, section 7.5) is an ordinary <c>chat</c> from
/// <c>room@service/nick</c>, and that address is indistinguishable from a
/// contact's full address by looking at it. Only the room table knows.
///
/// The specification does add a marker - an empty <c>&lt;x/&gt;</c> in the
/// <c>muc#user</c> namespace - and says in the same breath not to depend on it:
/// <i>because this requirement was only added in revision 1.28 of this XEP,
/// receiving entities MUST NOT rely on the existence of the &lt;x/&gt; element
/// on private messages for proper processing.</i> So the connection sets this
/// from what it knows, and the marker is sent but never trusted.
/// </remarks>
public sealed record XMPPMessage(JID              From,
                                 JID              To,
                                 string           Body,
                                 string?          MessageId,
                                 DateTime         Timestamp,
                                 MessageType      Type        = MessageType.Normal,
                                 DateTime?        ReceivedAt  = null,
                                 JID?             DelayedBy   = null,
                                 string?          ReplacesId  = null,
                                 MessageReplyTo?  RepliesTo   = null,
                                 BodyRange?       QuoteRange  = null,
                                 string?          OriginId    = null,
                                 string?          StanzaId    = null,
                                 Uri?             FileUrl     = null,
                                 bool             InARoom     = false,
                                 string?          RetractsId  = null)
{

    /// <summary>
    /// XEP-0045, section 7.5: was this said to us alone, inside a room?
    /// </summary>
    /// <remarks>
    /// <b>The difference an interface may not blur.</b> It arrives from a room
    /// address like everything else the room sends, so a client that files by
    /// the bare address puts it in the room's conversation - where it was never
    /// said - and a client that answers to the bare address says out loud what
    /// was told to it in confidence.
    ///
    /// <c>groupchat</c> is what the room says to everybody and is excluded here
    /// whatever else is true of it; a resource is required because a message
    /// from the bare room is about the room and not from anybody in it.
    /// </remarks>
    public bool IsRoomPrivate

        => InARoom &&
           Type != MessageType.GroupChat &&
           From.Resourcepart is not null;

    /// <summary>
    /// Is this message about a file rather than about its own text
    /// (XEP-0066/XEP-0363)?
    /// </summary>
    /// <remarks>
    /// The body of such a message is the address and nothing else, so an
    /// interface that shows it as text shows a person a URL. That is not wrong
    /// - it is exactly what a client without this extension does, and the
    /// reason section 5 asks for the address in the body as well - but it is
    /// not what was sent.
    /// </remarks>
    public bool IsFile => FileUrl is not null;

    /// <summary>
    /// Does this message correct an earlier one (XEP-0308)?
    /// </summary>
    public bool IsCorrection => ReplacesId is not null;

    /// <summary>
    /// Does this message answer an earlier one (XEP-0461)?
    /// </summary>
    public bool IsReply => RepliesTo is not null;

    /// <summary>
    /// The message without the quoted lines - what was actually written.
    /// </summary>
    /// <remarks>
    /// The same as <see cref="Body"/> whenever nothing was marked as fallback,
    /// so an interface can simply use this one and never think about it again.
    /// That is the point of the property existing at all: the alternative is
    /// every caller doing the arithmetic, and one of them getting it wrong.
    /// </remarks>
    public string Text

        => QuoteRange is BodyRange range
               ? Body.Remove(range.Start, range.Length)
               : Body;

    /// <summary>
    /// The quoted lines, or null when the message carries none.
    /// </summary>
    /// <remarks>
    /// Worth having even for a client that shows the answered message itself:
    /// when that message is not to hand - it was written before this session,
    /// or into a room this client was not in - the quotation is the only copy
    /// of what is being answered.
    /// </remarks>
    public string? Quote

        => QuoteRange is BodyRange range
               ? Body.Substring(range.Start, range.Length)
               : null;

    /// <summary>
    /// The name to use when answering this message - or null when there is
    /// none to use.
    /// </summary>
    /// <remarks>
    /// <b>In a room the <c>id</c> of the stanza must not be used</b> (XEP-0461,
    /// section 4). Everyone present sees a different one: the room passes on
    /// what the sender wrote, and what the sender wrote was for the sender. The
    /// only name everybody shares is the room's own, and a room that assigns
    /// none is a room in which nothing can be answered. Null says exactly that,
    /// and it is better than an answer that points at somebody else's message
    /// for every reader.
    ///
    /// Outside a room the sender's own name holds: their
    /// <c>&lt;origin-id/&gt;</c> if they gave one, otherwise the <c>id</c>.
    /// </remarks>
    public string? ReplyableId

        => Type == MessageType.GroupChat
               ? StanzaId
               : OriginId ?? MessageId;

    /// <summary>
    /// XEP-0424: the name to use when taking this message back - or null
    /// when there is none to use.
    /// </summary>
    /// <remarks>
    /// <b>The same name as for an answer, and that is not an accident.</b>
    /// Both extensions are asking one question - <i>what is this message
    /// called, in a way everybody who can see it agrees on</i> - and arrive
    /// at the same rule from different directions. XEP-0461 forbids the
    /// stanza's id in a room because everybody present sees a different one;
    /// XEP-0424 requires the room's id there, in so many words. One
    /// expression, so that a change to what a shared name is cannot quietly
    /// hold for one of them and not the other.
    ///
    /// Null means the same here as there and is worth as much: a room that
    /// assigns no name is a room in which nothing can be taken back either,
    /// and saying so is better than retracting somebody else's line.
    /// </remarks>
    public string? RetractableId

        => ReplyableId;

    /// <summary>
    /// XEP-0424: does this message take an earlier one back?
    /// </summary>
    public bool IsRetraction => RetractsId is not null;

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
