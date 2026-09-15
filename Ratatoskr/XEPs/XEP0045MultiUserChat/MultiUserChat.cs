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
/// What a room said about somebody with one stanza.
/// </summary>
/// <param name="Item">
/// The occupant the stanza is about, or null when it carried no
/// <c>&lt;item/&gt;</c> - which happens: a bare <c>&lt;status/&gt;</c> is a
/// statement about the room.
/// </param>
/// <param name="Status">
/// The numbers from section 15.6, in the order they stood. An unavailable
/// presence is somebody leaving, being kicked, being banned or renaming
/// themselves, and these are the only thing that tells the four apart.
/// </param>
/// <param name="NewNick">
/// With <see cref="MucStatus.NickChanged"/>: the name the occupant is called
/// from now on.
/// </param>
/// <param name="Reason">Why, if anybody said.</param>
/// <param name="Actor">Who did it, if the room says - a kick has an author.</param>
public sealed record MucUserInfo(MucOccupant?        Item,
                                 IReadOnlyList<int>  Status,
                                 string?             NewNick  = null,
                                 string?             Reason   = null,
                                 JID?                Actor    = null)
{

    /// <summary>Does the room report this number?</summary>
    public bool Has(int code)
        => Status.Contains(code);

    /// <summary>
    /// Is this stanza about us?
    /// </summary>
    /// <remarks>
    /// <b>The number and nothing else.</b> Comparing nicknames would be the
    /// obvious alternative and is wrong twice over: the service may assign a
    /// different nickname than the one asked for (status 210), and in a room
    /// where somebody else already holds ours the join fails rather than
    /// matching.
    /// </remarks>
    public bool IsSelf
        => Has(MucStatus.Self);

}


/// <summary>
/// XEP-0045: a room, and everybody in it.
/// </summary>
/// <remarks>
/// <b>A second presence model beside the roster, and that is the whole
/// difficulty.</b> Everything else in this library treats a presence as news
/// about a contact; here it is news about somebody in a room, who may be a
/// complete stranger and whose real address is usually not even given. Both
/// arrive over the same connection, in the same stanza shape, and telling them
/// apart is a matter of one question: is the sender's bare address a room this
/// client has joined.
///
/// What is built here is the visiting half: entering, being there, and
/// leaving. Configuring a room, administering one, and inviting people are the
/// owner's and the moderator's halves and are not implemented - see the README.
/// </remarks>
public static class MultiUserChat
{

    /// <summary>
    /// The namespace one enters with.
    /// </summary>
    public const string Namespace = "http://jabber.org/protocol/muc";

    /// <summary>
    /// The namespace a room speaks in: roles, affiliations, status codes.
    /// </summary>
    public const string UserNamespace = "http://jabber.org/protocol/muc#user";


    #region Stanzas that go out

    /// <summary>
    /// The presence that enters a room.
    /// </summary>
    /// <param name="roomAndNick">
    /// <c>room@service/nick</c> - the address one wants to be known by in there.
    /// </param>
    /// <param name="password">For a password-protected room (section 7.2.6).</param>
    /// <param name="historyMaxStanzas">
    /// How much of what was said before the room should send along. Zero asks
    /// for none.
    /// </param>
    /// <remarks>
    /// <b>The <c>&lt;x/&gt;</c> is what makes this a join</b> rather than an
    /// ordinary presence to an address that happens to be a room. Without it
    /// section 7.2.2 lets the service refuse, and several do - a client that
    /// leaves it out works against the one server that is lenient and nowhere
    /// else.
    /// </remarks>
    public static string JoinXml(JID      roomAndNick,
                                 string?  password           = null,
                                 int?     historyMaxStanzas  = null)
    {

        var inside = "";

        if (historyMaxStanzas.HasValue)
            inside += $"<history maxstanzas='{historyMaxStanzas.Value.ToString(CultureInfo.InvariantCulture)}'/>";

        if (password is not null)
            inside += $"<password>{XmlEscaping.Escape(password)}</password>";

        return $"<presence to='{XmlEscaping.Escape(roomAndNick.ToString())}'>" +
                   $"<x xmlns='{Namespace}'>{inside}</x>" +
               "</presence>";

    }

    /// <summary>
    /// The presence that leaves a room (section 7.14).
    /// </summary>
    public static string LeaveXml(JID roomAndNick, string? status = null)

        => $"<presence to='{XmlEscaping.Escape(roomAndNick.ToString())}' type='unavailable'>" +
               (status is not null ? $"<status>{XmlEscaping.Escape(status)}</status>" : "") +
           "</presence>";

    /// <summary>
    /// The presence that changes one's nickname in a room (section 7.6).
    /// </summary>
    /// <remarks>
    /// The same stanza as entering, only without the <c>&lt;x/&gt;</c> and to a
    /// different resource. That is not an oversight in the specification: to the
    /// room it is the same occupant under a new name, and the
    /// <c>&lt;x/&gt;</c> would say one wants to enter again.
    /// </remarks>
    public static string NickChangeXml(JID roomAndNewNick)
        => $"<presence to='{XmlEscaping.Escape(roomAndNewNick.ToString())}'/>";

    /// <summary>
    /// The message that sets the subject of a room (section 7.2.16).
    /// </summary>
    /// <remarks>
    /// To the bare address of the room and without a <c>&lt;body/&gt;</c> - with
    /// one it would be an ordinary message that happens to mention a subject,
    /// and the room would not change anything.
    /// </remarks>
    public static string SubjectXml(JID room, string subject)

        => $"<message to='{XmlEscaping.Escape(room.Bare.ToString())}' type='groupchat'>" +
               $"<subject>{XmlEscaping.Escape(subject)}</subject>" +
           "</message>";

    #endregion

    #region Stanzas that come in

    /// <summary>
    /// What the room said about somebody - or null when the stanza carries
    /// nothing of the kind.
    /// </summary>
    /// <remarks>
    /// Only direct children, as everywhere in this library: a carbon brings a
    /// whole message of its own along, and its room news is not the outer
    /// stanza's.
    /// </remarks>
    public static MucUserInfo? UserInfo(XElement stanza)
    {

        var x = stanza.Child(UserNamespace, "x");

        if (x is null)
            return null;

        var status = new List<int>();

        foreach (var element in x.Children(UserNamespace, "status"))
            if (int.TryParse(element.Attr("code"),
                             NumberStyles.None,
                             CultureInfo.InvariantCulture,
                             out var code))
            {
                status.Add(code);
            }

        var item = x.Child(UserNamespace, "item");

        if (item is null)
            return new MucUserInfo(null, status);

        // The nickname of the occupant is the resource of the sender - except in
        // a nick change, where the item carries the new one and the sender is
        // still the old.
        var nick = JID.TryParse(stanza.Attr("from"), out var from)
                       ? from.Resourcepart ?? ""
                       : "";

        var actor = item.Child(UserNamespace, "actor");

        return new MucUserInfo(

                   new MucOccupant(
                       nick,
                       MucRoles.ToAffiliation(item.Attr("affiliation")),
                       MucRoles.ToRole       (item.Attr("role")),
                       JID.TryParse(item.Attr("jid"), out var real) ? real : null,
                       stanza.ChildValue("show"),
                       stanza.ChildValue("status")
                   ),

                   status,

                   string.IsNullOrEmpty(item.Attr("nick")) ? null : item.Attr("nick"),
                   item.Child(UserNamespace, "reason")?.Value,
                   actor is not null && JID.TryParse(actor.Attr("jid"), out var who) ? who : null

               );

    }

    /// <summary>
    /// Is this message a change of the room's subject?
    /// </summary>
    /// <remarks>
    /// <b>A <c>&lt;subject/&gt;</c> and no <c>&lt;body/&gt;</c></b>, section
    /// 7.2.16. Both halves matter: a room sends the subject at the end of every
    /// join, so a client that goes by the element alone announces a change that
    /// nobody made - and a client that ignores the missing body treats an
    /// ordinary message carrying a subject line as one.
    ///
    /// An empty <c>&lt;subject/&gt;</c> is a change too: it is how a subject is
    /// removed. Hence the distinction between "no element" and "an element with
    /// nothing in it", which <see cref="StanzaExtensions.ChildValue"/> would
    /// otherwise blur.
    /// </remarks>
    public static bool IsSubjectChange(XElement message)

        => message.Child("subject") is not null &&
           message.Child("body")    is null;

    #endregion

}
