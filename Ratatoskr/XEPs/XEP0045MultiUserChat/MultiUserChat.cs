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
/// XEP-0045, section 7.8: somebody wants us in a room.
/// </summary>
/// <param name="Room">The room it is about.</param>
/// <param name="From">
/// Who is asking - and <b>in one of two shapes</b>, which is not a detail a
/// client may skip over. See <see cref="FromAnOccupantAddress"/>.
/// </param>
/// <param name="Reason">Why, if they said.</param>
/// <param name="Password">
/// For a password-protected room, when the inviter passed it on. Without it an
/// invitation into such a room is one nobody can act on.
/// </param>
/// <remarks>
/// <b>The one thing a room says about a room one is not in.</b> Every other
/// stanza from a room concerns somewhere this client already is, and is
/// recognised by exactly that; an invitation has to be recognised without it,
/// which is why it is looked for before the room table is asked at all.
///
/// The address to answer is <see cref="From"/> and not the room: a refusal goes
/// to the person, through the room. A client that declines to the room declines
/// to nobody.
/// </remarks>
public sealed record MucInvitation(JID      Room,
                                   JID      From,
                                   string?  Reason    = null,
                                   string?  Password  = null)
{

    /// <summary>
    /// Is the inviter named by their address <b>in the room</b> rather than by
    /// their real one?
    /// </summary>
    /// <remarks>
    /// <b>Measured, not assumed - and the two services differ.</b> XEP-0045
    /// section 7.8.2 shows the inviter's real address in the example, and
    /// ejabberd sends that. Prosody sends the occupant address,
    /// <c>room@service/nick</c>, which tells the invitee who asked without
    /// telling them who that is - the sensible thing for a semi-anonymous room,
    /// and not what the example shows.
    ///
    /// Neither breaks anything: a refusal addressed either way reaches the
    /// inviter, because the room routes it. What breaks is a client that
    /// assumes one of them - it will either show a room address where a person
    /// belongs, or treat a perfectly good invitation as malformed. So the shape
    /// is reported rather than normalised: whoever wants a name takes
    /// <c>From.Resourcepart</c> when this is true, and the localpart when it is
    /// not.
    /// </remarks>
    public bool FromAnOccupantAddress
        => From.Bare == Room;

}


/// <summary>
/// XEP-0045, section 7.8.2: somebody we invited is not coming.
/// </summary>
public sealed record MucDecline(JID Room, JID From, string? Reason = null);


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

    /// <summary>
    /// The namespace a moderator or an admin asks in.
    /// </summary>
    public const string AdminNamespace = "http://jabber.org/protocol/muc#admin";

    /// <summary>
    /// The namespace an owner asks in.
    /// </summary>
    public const string OwnerNamespace = "http://jabber.org/protocol/muc#owner";

    /// <summary>
    /// The configuration field that decides who may see the real addresses of
    /// the occupants (section 10.2.1, and the registry at
    /// <c>muc#roomconfig</c>).
    /// </summary>
    /// <remarks>
    /// <c>anyone</c> or <c>moderators</c>, and the default of every service is
    /// <c>moderators</c> - a semi-anonymous room. <b>It is the one setting
    /// end-to-end encryption in a room stands or falls by</b>: OMEMO encrypts to
    /// the devices of a real address, and in a semi-anonymous room a participant
    /// has nothing but nicknames. See <see cref="OmemoRooms"/>.
    /// </remarks>
    public const string WhoIsField      = "muc#roomconfig_whois";

    /// <summary>
    /// The value of <see cref="WhoIsField"/> that makes a room non-anonymous.
    /// </summary>
    public const string WhoIsAnyone     = "anyone";

    /// <summary>
    /// The value of <see cref="WhoIsField"/> every service starts with.
    /// </summary>
    public const string WhoIsModerators = "moderators";

    /// <summary>
    /// Whether the room outlives the last occupant leaving (section 10.2.1).
    /// </summary>
    public const string PersistentField = "muc#roomconfig_persistentroom";

    /// <summary>
    /// Whether only members may enter (section 10.2.1).
    /// </summary>
    public const string MembersOnlyField = "muc#roomconfig_membersonly";


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

    /// <summary>
    /// XEP-0045, section 8: changes what somebody may do while they are here.
    /// </summary>
    /// <param name="nick">Whom - by their name in this room.</param>
    /// <param name="role">What they are to be. <c>None</c> is a kick.</param>
    /// <param name="reason">Why, for the person and for everybody watching.</param>
    /// <remarks>
    /// <b>By nickname, and that is not an accident.</b> A role lasts for the
    /// visit, and inside the visit the nickname is what identifies somebody -
    /// it is also all a moderator is given in an ordinary room. An affiliation
    /// outlives the visit and is therefore set by real address; see
    /// <see cref="AffiliationQuery"/>, where that difference costs something.
    /// </remarks>
    public static XElement RoleQuery(string   nick,
                                     MucRole  role,
                                     string?  reason = null)
    {

        var item = new XElement(XName.Get("item", AdminNamespace),
                       new XAttribute("nick", nick),
                       new XAttribute("role", role.AsText()));

        if (reason is not null)
            item.Add(new XElement(XName.Get("reason", AdminNamespace), reason));

        return new XElement(XName.Get("query", AdminNamespace), item);

    }

    /// <summary>
    /// XEP-0045, section 9: changes what somebody is to the room, beyond this
    /// visit.
    /// </summary>
    /// <param name="jid">
    /// Whom - by their <b>real</b> address.
    /// </param>
    /// <param name="affiliation">
    /// What they are to be. <c>Outcast</c> is a ban.
    /// </param>
    /// <param name="reason">Why.</param>
    /// <remarks>
    /// <b>The real address, and in an ordinary room one does not have it.</b> A
    /// room is semi-anonymous by default and gives the real addresses of its
    /// occupants to its moderators only - so a moderator can ban, and somebody
    /// who is merely annoyed cannot. That is the protocol working as intended
    /// and not a gap: an affiliation outlives the visit, so it has to name
    /// somebody who exists outside it, and a nickname does not.
    ///
    /// Which is why a ban can fail for a reason that has nothing to do with
    /// permissions: <see cref="MucOccupant.RealJid"/> is null, and there is
    /// nothing to put here.
    /// </remarks>
    public static XElement AffiliationQuery(JID             jid,
                                            MucAffiliation  affiliation,
                                            string?         reason = null)
    {

        var item = new XElement(XName.Get("item", AdminNamespace),
                       new XAttribute("jid",         jid.Bare.ToString()),
                       new XAttribute("affiliation", affiliation.AsText()));

        if (reason is not null)
            item.Add(new XElement(XName.Get("reason", AdminNamespace), reason));

        return new XElement(XName.Get("query", AdminNamespace), item);

    }

    /// <summary>
    /// XEP-0045, section 10.2: asks a room for its configuration form.
    /// </summary>
    /// <remarks>
    /// An empty <c>&lt;query/&gt;</c> in the owner namespace, sent as an
    /// <c>iq get</c>. What comes back is a data form (XEP-0004) whose fields
    /// are the room's settings, and which fields a service offers is the
    /// service's business - there is no fixed list.
    /// </remarks>
    public static XElement ConfigQuery()

        => new (XName.Get("query", OwnerNamespace));

    /// <summary>
    /// XEP-0045, section 10.2: the changed configuration, going back.
    /// </summary>
    /// <param name="form">
    /// The form that came back from <see cref="ConfigQuery"/>, with the wanted
    /// values written into it.
    /// </param>
    /// <remarks>
    /// <b>The whole form goes back, not the fields that changed</b>, and that is
    /// the one thing about section 10.2 that is easy to get wrong in a way
    /// nothing complains about. A configuration form is a state and not a patch:
    /// a submit carrying only <c>muc#roomconfig_whois</c> tells the service that
    /// every other field is now unset, and a service that takes it at its word
    /// quietly resets the room - the password, the member list, whether it
    /// persists. The answer is still <c>result</c>.
    ///
    /// So <see cref="ConfigWith"/> takes the form apart and puts it back
    /// together rather than building a new one.
    /// </remarks>
    public static XElement ConfigSubmit(XElement form)

        => new (XName.Get("query", OwnerNamespace), form);

    /// <summary>
    /// The same form with some fields given other values.
    /// </summary>
    /// <param name="form">The form as the service sent it.</param>
    /// <param name="values">
    /// What to change, by field name - <c>muc#roomconfig_whois</c> and the like.
    /// A field the form does not have is <b>not</b> added: a service that does
    /// not offer a setting does not have it, and inventing the field would ask
    /// for something that does not exist.
    /// </param>
    /// <param name="Missing">
    /// The names that were asked for and are not in the form. A caller that
    /// wanted a room to stop being anonymous needs to know that the room was
    /// never asked.
    /// </param>
    /// <remarks>
    /// Everything else travels unchanged, including the fields this library has
    /// never heard of: what is not understood here is still part of the room's
    /// state, and dropping it would be the reset described on
    /// <see cref="ConfigSubmit"/>.
    ///
    /// The <c>type</c> attributes go, as XEP-0004 section 3.2 asks of a submit -
    /// they describe how a form is drawn and mean nothing on the way back - and
    /// so do the labels. What stays is <c>var</c> and the values.
    /// </remarks>
    public static XElement ConfigWith(XElement                            form,
                                      IReadOnlyDictionary<string, string> values,
                                      out IReadOnlyList<string>           Missing)
    {

        XNamespace ns = DataForm.Namespace;

        var submit  = new XElement(ns + "x", new XAttribute("type", "submit"));
        var seen    = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in DataForm.Fields(form))
        {

            var name = field.Attr("var");

            if (name is null)
                continue;

            seen.Add(name);

            var copy = new XElement(ns + "field", new XAttribute("var", name));

            if (values.TryGetValue(name, out var wanted))
                copy.Add(new XElement(ns + "value", wanted));

            else
                foreach (var value in field.Children(DataForm.Namespace, "value"))
                    copy.Add(new XElement(ns + "value", value.Value));

            submit.Add(copy);

        }

        Missing = [.. values.Keys.Where(name => !seen.Contains(name))];

        return submit;

    }

    /// <summary>
    /// XEP-0045, section 7.8.1: asks somebody into a room, through the room.
    /// </summary>
    /// <remarks>
    /// <b>Mediated and not direct</b>, which is the whole point of the detour:
    /// the message goes to the room and the room passes it on. An invitation
    /// that came from the room is one the room will honour - it can put the
    /// invitee on the member list on the way - whereas one sent straight to the
    /// person is a stranger's word about a room they have never heard of.
    /// </remarks>
    public static string InviteXml(JID room, JID who, string? reason = null)

        => $"<message to='{XmlEscaping.Escape(room.Bare.ToString())}'>" +
               $"<x xmlns='{UserNamespace}'>" +
                   $"<invite to='{XmlEscaping.Escape(who.ToString())}'>" +
                       (reason is not null ? $"<reason>{XmlEscaping.Escape(reason)}</reason>" : "") +
                   "</invite>" +
               "</x>" +
           "</message>";

    /// <summary>
    /// XEP-0045, section 7.8.2: says no to an invitation.
    /// </summary>
    /// <remarks>
    /// Also through the room, and addressed to the person who asked. A refusal
    /// sent to the room itself is a refusal delivered to nobody.
    /// </remarks>
    public static string DeclineXml(JID room, JID inviter, string? reason = null)

        => $"<message to='{XmlEscaping.Escape(room.Bare.ToString())}'>" +
               $"<x xmlns='{UserNamespace}'>" +
                   $"<decline to='{XmlEscaping.Escape(inviter.Bare.ToString())}'>" +
                       (reason is not null ? $"<reason>{XmlEscaping.Escape(reason)}</reason>" : "") +
                   "</decline>" +
               "</x>" +
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
    /// An invitation into a room, or null.
    /// </summary>
    /// <remarks>
    /// The <c>from</c> of the stanza is the <b>room</b>; who is asking stands in
    /// the <c>from</c> of the <c>&lt;invite/&gt;</c>, which the room fills in on
    /// the way. Taking the stanza's sender for the inviter would answer every
    /// refusal to the room, which forwards it to nobody.
    /// </remarks>
    public static MucInvitation? Invitation(XElement message)
    {

        var x = message.Child(UserNamespace, "x");

        var invite = x?.Child(UserNamespace, "invite");

        if (invite is null ||
            !JID.TryParse(message.Attr("from"), out var room) ||
            !JID.TryParse(invite.Attr("from"), out var from))
        {
            return null;
        }

        return new MucInvitation(
                   room.Bare,
                   from,
                   invite.Child(UserNamespace, "reason")?.Value,
                   x?.Child(UserNamespace, "password")?.Value
               );

    }

    /// <summary>
    /// A refusal of an invitation we sent, or null.
    /// </summary>
    public static MucDecline? Decline(XElement message)
    {

        var decline = message.Child(UserNamespace, "x")?.
                              Child(UserNamespace, "decline");

        if (decline is null ||
            !JID.TryParse(message.Attr("from"), out var room) ||
            !JID.TryParse(decline.Attr("from"), out var from))
        {
            return null;
        }

        return new MucDecline(room.Bare,
                              from,
                              decline.Child(UserNamespace, "reason")?.Value);

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
