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
/// XEP-0280: A message mirrored by a message carbon.
/// </summary>
public sealed class CarbonMessage
{
    public bool IsSent { get; }          // true = sent by me, false = received
    public JID OriginalFrom { get; }
    public JID OriginalTo { get; }
    public string? Body { get; }
    public string? MessageId { get; }
    public DateTime ReceivedAt { get; }

    /// <summary>
    /// XEP-0461: which message the mirrored one answers, or null.
    /// </summary>
    /// <remarks>
    /// The same reading <see cref="XMPPMessage"/> gets, and for the same
    /// reason: an answer written on another device of one's own is still an
    /// answer, and without this it arrived here as a line whose quotation sat
    /// in the middle of its own text.
    /// </remarks>
    public MessageReplyTo? RepliesTo { get; }

    /// <summary>
    /// XEP-0428: which part of <see cref="Body"/> is only the quotation of the
    /// message answered, or null. <see cref="Text"/> and <see cref="Quote"/>
    /// are the two views of it.
    /// </summary>
    public BodyRange? QuoteRange { get; }

    /// <summary>
    /// XEP-0308: the id of the message the mirrored one replaces, or null.
    /// </summary>
    /// <remarks>
    /// A correction made on another device of one's own is still a
    /// correction; without this it arrived here as a second line beside the
    /// one it corrects.
    /// </remarks>
    public string? ReplacesId { get; }

    /// <summary>
    /// XEP-0424: the id of the message the mirrored one takes back, or null.
    /// </summary>
    /// <remarks>
    /// Without this a line taken back on another device of one's own stayed
    /// here with its words, and the fallback sentence stood beside it as
    /// though somebody had typed it.
    /// </remarks>
    public string? RetractsId { get; }

    /// <summary>
    /// XEP-0066: the address the mirrored message is about, or null when it is
    /// about none.
    /// </summary>
    /// <remarks>
    /// <b>A file received on another device of one's own is a file
    /// received.</b> Read off the inner message by the one rule a message of
    /// this connection's own gets - the <c>&lt;x/&gt;</c> has to name what the
    /// body names, see <see cref="OutOfBandData.UrlIn"/> - because the inner
    /// message is the one that was about the file.
    ///
    /// Without it the mirrored file arrived here as a line of text that
    /// happened to look like an address, and whoever showed it had to guess
    /// from the shape of the text whether it was one. Which is the guess this
    /// element exists to replace.
    /// </remarks>
    public Uri? FileUrl { get; }

    /// <summary>
    /// Is the mirrored message about a file rather than about its own text
    /// (XEP-0066/XEP-0363)?
    /// </summary>
    public bool IsFile => FileUrl is not null;

    /// <summary>
    /// The mirrored message without the quoted lines - what was actually
    /// written. The same as <see cref="Body"/> whenever nothing was marked.
    /// </summary>
    public string? Text

        => Body is not null && QuoteRange is BodyRange range
               ? Body.Remove(range.Start, range.Length)
               : Body;

    /// <summary>
    /// The quoted lines, or null when the mirrored message carries none.
    /// </summary>
    public string? Quote

        => Body is not null && QuoteRange is BodyRange range
               ? Body.Substring(range.Start, range.Length)
               : null;

    public CarbonMessage(bool             isSent,
                         JID              originalFrom,
                         JID              originalTo,
                         string?          body,
                         string?          messageId,
                         MessageReplyTo?  repliesTo   = null,
                         BodyRange?       quoteRange  = null,
                         string?          replacesId  = null,
                         string?          retractsId  = null,
                         Uri?             fileUrl     = null)
    {
        IsSent = isSent;
        OriginalFrom = originalFrom;
        OriginalTo = originalTo;
        Body = body;
        MessageId = messageId;
        RepliesTo = repliesTo;
        QuoteRange = quoteRange;
        ReplacesId = replacesId;
        RetractsId = retractsId;
        FileUrl = fileUrl;
        ReceivedAt = DateTime.UtcNow;
    }
}
