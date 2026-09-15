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

using System.Text;
using System.Xml.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// Which message a reply is about (XEP-0461).
/// </summary>
/// <param name="Id">
/// The name of the message being answered - the one the other side will
/// recognise, which is not always its <c>id</c>. See <see cref="StableIds"/>.
/// </param>
/// <param name="To">
/// Who wrote it, if the reply says so. Optional in the specification and
/// genuinely so: the id is the reference, the address is a convenience for a
/// client that has not got the message any more.
/// </param>
public sealed record MessageReplyTo(string Id, JID? To);


/// <summary>
/// An answer, ready to be put into a stanza.
/// </summary>
/// <param name="Body">
/// The text that goes into the <c>&lt;body/&gt;</c> - the quotation and the
/// answer, in that order.
/// </param>
/// <param name="Extras">
/// The elements that explain it: the reference, and the marking that says how
/// much of the body is only the quotation.
/// </param>
/// <remarks>
/// The two belong together and are handed over together for exactly that
/// reason. The numbers in the second only mean anything against the first, and
/// anybody who can hold one without the other will eventually hold a stale one.
/// </remarks>
public readonly record struct ComposedReply(string Body, string Extras);


/// <summary>
/// XEP-0461: this message is about that one.
/// </summary>
/// <remarks>
/// The whole extension is one empty element with two attributes. What it buys
/// is that a conversation stops being a single column in which everything
/// refers to whatever came last - and it buys it without a thread, without
/// state and without either side having to remember anything.
///
/// <b>Two things are said at once</b>, and that is the part worth
/// understanding. The <c>&lt;reply/&gt;</c> names the message; the body
/// additionally carries the old text as <c>&gt; </c> lines, so that a client
/// which has never heard of XEP-0461 still shows something that reads. The
/// duplicate is then marked as fallback (XEP-0428) so that a client which
/// <em>has</em> heard of it can hide the lines again and show its own
/// quotation. Nobody is worse off than before, which is the reason this design
/// is worth the two elements.
///
/// <b>Not for encrypted messages here.</b> An OMEMO message would carry the
/// <c>&lt;reply/&gt;</c> outside the encryption, and that element says who
/// answered whom and when - the shape of a conversation, in clear, for anyone
/// watching the connection. Where it belongs is inside the envelope of
/// XEP-0420, next to the body it is about; until it is put there, this side
/// offers a reply only for a message that was not going to be secret anyway.
/// </remarks>
public static class MessageReply
{

    /// <summary>
    /// The namespace of XEP-0461.
    /// </summary>
    public const string Namespace = "urn:xmpp:reply:0";

    /// <summary>
    /// The <c>&lt;reply/&gt;</c> for an answer.
    /// </summary>
    /// <param name="id">The name of the message being answered.</param>
    /// <param name="to">Its author, or null.</param>
    public static string ReplyXml(string id, JID? to = null)

        => to is null

               ? $"<reply xmlns='{Namespace}' id='{XmlEscaping.Escape(id)}'/>"

               : $"<reply xmlns='{Namespace}' id='{XmlEscaping.Escape(id)}' " +
                        $"to='{XmlEscaping.Escape(to.Value.ToString())}'/>";

    /// <summary>
    /// Which message this one answers, or null.
    /// </summary>
    /// <remarks>
    /// Only direct children, for the reason the correction has it (D59): a
    /// carbon brings a whole message along in its <c>&lt;forwarded/&gt;</c>,
    /// and whoever searches the entire stanza declares the outer one an answer
    /// to something it was never about.
    ///
    /// An empty <c>id</c> counts as none; it points at nothing. A <c>to</c>
    /// that is not an address is dropped and the rest kept - the reference is
    /// the id, and throwing the answer away over a malformed convenience would
    /// lose the useful part of a message because of the decorative one.
    /// </remarks>
    public static MessageReplyTo? RepliesTo(XElement message)
    {

        var reply = message.Child(Namespace, "reply");

        if (reply is null)
            return null;

        var id = reply.Attr("id");

        if (string.IsNullOrEmpty(id))
            return null;

        return new MessageReplyTo(id,
                                  JID.TryParse(reply.Attr("to"), out var author)
                                      ? author
                                      : null);

    }

    /// <summary>
    /// The body of an answer and the elements that go beside it.
    /// </summary>
    /// <param name="answer">What is being said.</param>
    /// <param name="replyToId">The name of the message it is about.</param>
    /// <param name="replyToAuthor">Who wrote that one, or null.</param>
    /// <param name="quotedText">
    /// Its text, to be carried along as quoted lines - or null to leave them
    /// out.
    /// </param>
    /// <param name="quotedAuthor">Whose text that is, above the quotation.</param>
    /// <remarks>
    /// <b>One method rather than three lines at every call site</b>, because
    /// the three lines have to agree with each other: the quotation is built,
    /// put in front of the answer, and then measured - and the measurement is
    /// only right for the text that was actually built. A caller that assembles
    /// the body itself and passes a number alongside has two chances to be
    /// right and takes both risks.
    ///
    /// It is public for a second reason. The far side of this is a foreign
    /// implementation, and what a foreign implementation has to be able to read
    /// is what this library really sends - not something a test put together
    /// the same way and called equivalent.
    /// </remarks>
    public static ComposedReply Compose(string   answer,
                                        string   replyToId,
                                        JID?     replyToAuthor  = null,
                                        string?  quotedText     = null,
                                        string?  quotedAuthor   = null)
    {

        var quote  = Quote(quotedText ?? "", quotedAuthor);
        var body   = quote + answer;

        var extras = new StringBuilder(ReplyXml(replyToId, replyToAuthor));

        if (quote.Length > 0)
            extras.Append(FallbackIndication.BodyXml(Namespace,
                                                     body,
                                                     new BodyRange(0, quote.Length)));

        return new ComposedReply(body, extras.ToString());

    }

    /// <summary>
    /// Which part of the body is only the quotation of the message being
    /// answered - or null, when there is nothing to take out of it.
    /// </summary>
    /// <param name="message">The message stanza.</param>
    /// <param name="body">Its body, as it came out of the parser.</param>
    /// <remarks>
    /// <b>Cut only where there is an answer to cut it from.</b> A fallback
    /// marking that names <c>urn:xmpp:reply:0</c> in a message which replies to
    /// nothing is a stanza contradicting itself, and the two readings do not
    /// cost the same. Acting on the marking takes a piece out of somebody's
    /// sentence on the strength of one element while the rest of the stanza
    /// says there is no quotation there to take.
    ///
    /// The two halves live in one method for a duller reason as well: a rule
    /// that is written out at every call site is a rule that half the call
    /// sites have their own version of, and the test that checks it then checks
    /// the copy in the test.
    /// </remarks>
    public static BodyRange? QuoteRangeIn(XElement message, string body)

        => RepliesTo(message) is null
               ? null
               : FallbackIndication.RangeIn(message, Namespace, body);

    /// <summary>
    /// The quoted text for the body, the way every client writes it: one
    /// <c>&gt; </c> in front of each line, and the author above it if there is
    /// one to name.
    /// </summary>
    /// <param name="text">What is being quoted.</param>
    /// <param name="author">Who said it, or null in a conversation of two.</param>
    /// <remarks>
    /// <b>The line endings are normalised, and that is not cosmetic.</b> An XML
    /// parser turns every <c>CR LF</c> in text content into a single <c>LF</c>
    /// before anybody sees it (XML 1.0, section 2.11). So a quotation written
    /// on Windows is one character per line shorter at the far end than it was
    /// here - and the offsets of XEP-0428 are counted in exactly those
    /// characters. Whoever counts before the parser does cuts the quotation in
    /// the wrong place, by as many characters as it has lines, and only ever
    /// for text that came from a machine that writes both.
    ///
    /// Nothing else is touched. Trailing blanks stay, indentation stays: the
    /// quoted text is somebody else's, and tidying it up is not this side's to
    /// do.
    /// </remarks>
    public static string Quote(string text, string? author = null)
    {

        if (string.IsNullOrEmpty(text))
            return "";

        var quoted = new StringBuilder();

        if (!string.IsNullOrEmpty(author))
            quoted.Append("> ").Append(author).Append(":\n");

        foreach (var line in text.Replace("\r\n", "\n").
                                  Replace("\r",   "\n").
                                  Split('\n'))
        {
            quoted.Append("> ").Append(line).Append('\n');
        }

        return quoted.ToString();

    }

}
