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

using System.Globalization;
using System.Xml.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// One message out of an archive.
/// </summary>
/// <param name="ArchiveId">
/// What the archive calls it. Not the <c>id</c> of the stanza and not
/// interchangeable with it: this one is the archive's own, and it is what
/// paging refers to.
/// </param>
/// <param name="Timestamp">
/// When it was written, out of the <c>&lt;delay/&gt;</c> the archive puts
/// beside it (XEP-0203). <b>Not the moment it arrived here</b>, which for an
/// archive is always now and says nothing.
/// </param>
/// <param name="Message">
/// The message itself, as it was when it was archived.
/// </param>
/// <remarks>
/// <b>Deliberately not an <see cref="XMPPMessage"/> handed to
/// <c>OnMessage</c>.</b> What comes out of an archive is not news: it arrived
/// once already, possibly years ago, possibly on another device. A client that
/// lets the two travel the same path replays its own history as new arrivals
/// every time somebody opens a conversation.
/// </remarks>
public sealed record ArchivedMessage(string          ArchiveId,
                                     DateTimeOffset  Timestamp,
                                     XMPPMessage     Message);


/// <summary>
/// What one query brought back.
/// </summary>
/// <param name="Messages">In the order the archive sent them - oldest first.</param>
/// <param name="Complete">
/// Whether this page is the end of what was asked for. <b>False means there is
/// more</b>, not that anything went wrong: an archive answers in pages, and a
/// client that stops at the first one shows a fraction of a conversation
/// without saying so.
/// </param>
/// <param name="First">The archive id of the first message of this page, for paging.</param>
/// <param name="Last">The archive id of the last one.</param>
/// <param name="Count">
/// How many there are in total, when the archive says. Optional in RSM and
/// often absent.
/// </param>
public sealed record ArchivePage(IReadOnlyList<ArchivedMessage>  Messages,
                                 bool                            Complete,
                                 string?                         First  = null,
                                 string?                         Last   = null,
                                 int?                            Count  = null)
{

    /// <summary>
    /// Nothing came back at all.
    /// </summary>
    public bool Empty => Messages.Count == 0;

}


/// <summary>
/// XEP-0313: what a server kept.
/// </summary>
/// <remarks>
/// <b>An archive is not a store that gets handed over.</b> That was XEP-0013,
/// which this project decided against in D37: an offline store is emptied once
/// and is then gone. An archive stays, and is asked questions - who with, from
/// when, how much - which is what makes it useful on a second device, and what
/// makes it a different kind of thing to hold.
///
/// Two archives matter here and they are asked the same way:
/// <list type="bullet">
///   <item>one's <b>own</b>, which the server keeps - asked with no address at
///         all, which is how XMPP says "my own server";</item>
///   <item>a <b>room's</b> (XEP-0045), asked by addressing the room. That one
///         is how anybody sees what was said before they walked in, and until
///         D116 there were no rooms here to ask.</item>
/// </list>
///
/// <b>The results do not come back in the answer.</b> They arrive beforehand,
/// as ordinary-looking messages carrying a <c>&lt;result/&gt;</c>, and the
/// answer to the query only says that they are all there. That shape is the
/// whole difficulty of the extension: those messages must reach the archive and
/// nothing else, because a client that hands them on as ordinary messages
/// replays its own history as new arrivals.
/// </remarks>
public static class MessageArchive
{

    /// <summary>
    /// The namespace of XEP-0313, version 2.
    /// </summary>
    public const string Namespace = "urn:xmpp:mam:2";

    /// <summary>
    /// XEP-0059: the namespace paging is asked in.
    /// </summary>
    public const string ResultSetNamespace = "http://jabber.org/protocol/rsm";

    /// <summary>
    /// The namespace a forwarded stanza travels in.
    /// </summary>
    public const string ForwardNamespace = "urn:xmpp:forward:0";


    #region The query

    /// <summary>
    /// The <c>&lt;query/&gt;</c> that asks an archive something.
    /// </summary>
    /// <param name="queryId">
    /// The name this query goes by. Every result carries it back, which is what
    /// tells one query's results from another's when two are running.
    /// </param>
    /// <param name="with">Only messages with this address, or null for all.</param>
    /// <param name="start">Not before this moment.</param>
    /// <param name="end">Not after it.</param>
    /// <param name="max">How many at most - one page.</param>
    /// <param name="before">
    /// Page backwards from this archive id. An empty string means "the last
    /// page", which is the usual way to open a conversation: the end of it is
    /// what somebody wants to see first.
    /// </param>
    /// <param name="after">Page forwards from this archive id.</param>
    /// <remarks>
    /// The filters travel in a data form (XEP-0004) rather than as attributes,
    /// and the <c>FORM_TYPE</c> naming the extension is not decoration: it is
    /// what lets an archive add fields of its own without two servers meaning
    /// different things by the same word.
    /// </remarks>
    public static XElement Query(string           queryId,
                                 JID?             with    = null,
                                 DateTimeOffset?  start   = null,
                                 DateTimeOffset?  end     = null,
                                 int?             max     = null,
                                 string?          before  = null,
                                 string?          after   = null)
    {

        var form = new XElement(XName.Get("x", "jabber:x:data"),
                       new XAttribute("type", "submit"),
                       Field("FORM_TYPE", Namespace, "hidden"));

        if (with is not null)
            form.Add(Field("with", with.Value.ToString()));

        if (start.HasValue)
            form.Add(Field("start", Stamp(start.Value)));

        if (end.HasValue)
            form.Add(Field("end", Stamp(end.Value)));

        var query = new XElement(XName.Get("query", Namespace),
                        new XAttribute("queryid", queryId),
                        form);

        if (max.HasValue || before is not null || after is not null)
        {

            var set = new XElement(XName.Get("set", ResultSetNamespace));

            if (max.HasValue)
                set.Add(new XElement(XName.Get("max", ResultSetNamespace),
                                     max.Value.ToString(CultureInfo.InvariantCulture)));

            // An empty <before/> is not the same as no <before/>: it means the
            // last page rather than the first, which is what opening a
            // conversation wants.
            if (before is not null)
                set.Add(new XElement(XName.Get("before", ResultSetNamespace), before));

            if (after is not null)
                set.Add(new XElement(XName.Get("after", ResultSetNamespace), after));

            query.Add(set);

        }

        return query;

    }

    private static XElement Field(string name, string value, string? type = null)
    {

        var field = new XElement(XName.Get("field", "jabber:x:data"),
                        new XAttribute("var", name),
                        new XElement(XName.Get("value", "jabber:x:data"), value));

        if (type is not null)
            field.Add(new XAttribute("type", type));

        return field;

    }

    /// <summary>
    /// A moment as XEP-0082 writes one: UTC, to the second.
    /// </summary>
    /// <remarks>
    /// Converted to UTC here rather than trusted to be: a local time with an
    /// offset is legal and several archives compare the text.
    /// </remarks>
    private static string Stamp(DateTimeOffset when)
        => when.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    #endregion

    #region What comes back

    /// <summary>
    /// Which query a message belongs to, or null when it is not a result at
    /// all.
    /// </summary>
    /// <remarks>
    /// <b>Only direct children.</b> A result carries a whole message inside its
    /// <c>&lt;forwarded/&gt;</c>, and that message may itself be anything -
    /// including, in an archive of one's own, a carbon of something. Searching
    /// the stanza through would find the wrong one.
    /// </remarks>
    public static string? QueryIdOf(XElement message)
        => message.Child(Namespace, "result")?.Attr("queryid");

    /// <summary>
    /// The archived message inside a result - or null when the result carries
    /// nothing usable.
    /// </summary>
    /// <param name="message">The stanza that arrived.</param>
    /// <param name="ownJid">
    /// This client's own address, used when the archived stanza names no
    /// recipient.
    /// </param>
    /// <remarks>
    /// <b>The time comes out of the <c>&lt;delay/&gt;</c> and nowhere else.</b>
    /// For anything out of an archive the moment of arrival is now, and now is
    /// not when it was said. A result without a stamp is one whose place in the
    /// conversation cannot be known, so it is refused rather than filed under
    /// today.
    /// </remarks>
    public static ArchivedMessage? Read(XElement message, JID ownJid)
    {

        var result = message.Child(Namespace, "result");

        var id = result?.Attr("id");

        if (result is null || string.IsNullOrEmpty(id))
            return null;

        var forwarded = result.Child(ForwardNamespace, "forwarded");
        var inner     = forwarded?.Elements().
                                   FirstOrDefault(child => child.Name.LocalName == "message");

        if (forwarded is null || inner is null)
            return null;

        if (!DelayedDelivery.TryRead(forwarded, out var stamp, out _))
            return null;

        if (!JID.TryParse(inner.Attr("from"), out var from))
            return null;

        var body = inner.ChildValue("body");

        return new ArchivedMessage(

                   id,
                   stamp,

                   new XMPPMessage(
                       from,
                       JID.TryParse(inner.Attr("to")) ?? ownJid,
                       body ?? "",
                       inner.Attr("id"),
                       stamp.ToLocalTime().DateTime,
                       MessageTypeExtensions.Parse(inner.Attr("type")),
                       stamp.ToLocalTime().DateTime,
                       null,
                       MessageCorrection.ReplacedId(inner),
                       MessageReply.RepliesTo(inner),
                       MessageReply.QuoteRangeIn(inner, body ?? ""),
                       StableIds.OriginId(inner),
                       StableIds.StanzaId(inner, from),

                       // The same question the live branch asks. An archived
                       // message about a file is still about a file, and a
                       // client that reads it only when it arrives shows the
                       // picture once and the URL for ever after.
                       OutOfBandData.UrlIn(inner, body ?? "")
                   )

               );

    }

    /// <summary>
    /// The end of a query, out of the answer to it.
    /// </summary>
    /// <remarks>
    /// <c>complete='true'</c> is the archive saying there is nothing further in
    /// the direction that was asked. Its absence means the opposite, and a
    /// client that does not look shows a fraction of a conversation without
    /// saying so.
    /// </remarks>
    public static (bool Complete, string? First, string? Last, int? Count) ReadEnd(XElement answer)
    {

        var fin = answer.Child(Namespace, "fin");

        if (fin is null)
            return (true, null, null, null);

        var set = fin.Child(ResultSetNamespace, "set");

        return (

            fin.Attr("complete") == "true",

            set?.Child(ResultSetNamespace, "first")?.Value,
            set?.Child(ResultSetNamespace, "last")?.Value,

            int.TryParse(set?.Child(ResultSetNamespace, "count")?.Value,
                         NumberStyles.None,
                         CultureInfo.InvariantCulture,
                         out var count)
                ? count
                : null

        );

    }

    #endregion

}
