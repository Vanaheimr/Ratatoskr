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
/// XEP-0045, section 7.8.1: the room would not pass an invitation on.
/// </summary>
/// <param name="Room">The room that refused.</param>
/// <param name="Who">The person who was never asked.</param>
/// <param name="Error">What the room gave as the reason.</param>
/// <remarks>
/// <b>An invitation is a message, and a message has no answer</b> - so the
/// only sign that one was refused is the stanza coming back. Found in D129:
/// the default room on ejabberd has <c>muc#roomconfig_allowinvites</c> at 0,
/// which lets nobody but the owner ask anybody in. Prosody allows it. Both are
/// within XEP-0045 section 7.8.1, and every round in the suite had the owner do
/// the inviting - the one person for whom it can never fail.
///
/// What made it worth an event of its own: the refusal did arrive, as a
/// message error like any other, and nothing could tell it apart from a
/// message to the room that was refused. Meanwhile the caller of
/// <c>InviteToRoomAsync</c> had already been told <c>true</c>. The sender saw
/// success and the room saw silence, which is the failure D127 was written to
/// refuse.
/// </remarks>
public sealed record MucInviteRefused(JID Room, JID Who, StanzaError Error);

/// <summary>
/// XEP-0045, section 10.9: the room is gone.
/// </summary>
/// <param name="Room">Which one.</param>
/// <param name="Alternate">
/// Where to go instead, when the owner named somewhere - and it is the reason
/// this is a record and not a flag. A destruction without an alternative leaves
/// everybody nowhere; one with an alternative is a move, and a client that
/// drops the address has turned the second into the first.
/// </param>
/// <param name="Reason">Why, if the owner said.</param>
/// <param name="Password">
/// For the alternative, when it needs one. Useless on its own and useless
/// without <see cref="Alternate"/>, which is why they travel together.
/// </param>
/// <remarks>
/// <b>A destroyed room is not a left room</b>, and until D130 this client could
/// not tell them apart: both arrive as an unavailable presence for one's own
/// nickname, and the only difference is the <c>&lt;destroy/&gt;</c> inside it.
/// Read as a departure, a destruction says "you have left" to somebody who did
/// nothing, and throws away the address of the room they were meant to move to.
/// </remarks>
public sealed record MucRoomDestroyed(JID      Room,
                                      JID?     Alternate  = null,
                                      string?  Reason     = null,
                                      string?  Password   = null);


/// <summary>
/// XEP-0045, section 9.5: one line of a room's affiliation list.
/// </summary>
/// <param name="Jid">
/// The <b>real</b> address. An affiliation outlives a visit, so it cannot be
/// held against a nickname - which is the whole difference from a role.
/// </param>
/// <param name="Affiliation">What they are to the room.</param>
/// <param name="Nick">
/// What they are called in it <i>at the moment</i>, when the service says -
/// which it does only for somebody who is actually there. Not a key.
/// </param>
/// <param name="Reason">Why they were put on the list, when it was recorded.</param>
public sealed record MucAffiliated(JID             Jid,
                                   MucAffiliation  Affiliation,
                                   string?         Nick    = null,
                                   string?         Reason  = null);

/// <summary>
/// XEP-0045, section 8.6: somebody in a moderated room is asking to be allowed
/// to speak.
/// </summary>
/// <param name="Room">Which room.</param>
/// <param name="Jid">
/// Their <b>real</b> address, when the room gave it - and in a semi-anonymous
/// room it does, to moderators, because a moderator who cannot tell who is
/// asking cannot decide. Null where the service withheld it.
/// </param>
/// <param name="Nick">What they are called in the room.</param>
/// <param name="Role">What they are asking for. In practice always participant.</param>
/// <param name="Form">
/// The form as it arrived. Carried rather than taken apart, because the answer
/// is the same form sent back - see <see cref="MultiUserChat.VoiceAnswerXml"/>.
/// </param>
/// <remarks>
/// <b>It arrives as an ordinary message.</b> No <c>&lt;body/&gt;</c>, no status
/// code, nothing to mark it as being about the room except the form inside it -
/// so a client that reads bodies shows nothing and a client that reads status
/// codes sees nothing, and the person in the room goes on waiting to be let
/// speak. The same shape as the configuration notice D125 found, and for the
/// same reason it was missed.
/// </remarks>
public sealed record MucVoiceRequest(JID       Room,
                                     JID?      Jid,
                                     string?   Nick,
                                     MucRole   Role,
                                     XElement  Form);


/// <summary>
/// XEP-0045, section 7.10: what a room says about a nickname held there.
/// </summary>
/// <param name="Registered">Whether this account has one at all.</param>
/// <param name="Nick">
/// The nickname it holds, when the room says. A room that answers
/// <c>&lt;registered/&gt;</c> without naming it has still answered the question
/// that matters.
/// </param>
/// <remarks>
/// <b>A reservation is not an affiliation.</b> Being on the member list says one
/// may enter; holding a nickname says nobody else may enter under that name.
/// Rooms and services differ over whether they offer it at all, which is why
/// this is asked for rather than assumed.
/// </remarks>
public sealed record MucNicknameRegistration(bool Registered, string? Nick = null);




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
    /// The namespace a voice request and its answer are carried in
    /// (section 8.6).
    /// </summary>
    public const String RequestNamespace = "http://jabber.org/protocol/muc#request";

    /// <summary>
    /// The namespace a room is asked for a nickname in (section 7.10).
    /// </summary>
    /// <remarks>
    /// <b>XEP-0077's, not one of XEP-0045's own.</b> Registering with a room is
    /// in-band registration pointed at a room instead of at a server, which is
    /// why nothing in this file looked like it until D133 - it was being
    /// searched for under the wrong name.
    /// </remarks>
    public const String RegisterNamespace = "jabber:iq:register";

    /// <summary>
    /// The field a room's registration form holds the nickname in.
    /// </summary>
    public const String RoomNickField = "muc#register_roomnick";

    /// <summary>
    /// The field a moderator says yes or no in (section 8.6).
    /// </summary>
    public const String RequestAllowField = "muc#request_allow";

    /// <summary>
    /// XEP-0045, section 7.5: the mark a private message in a room carries.
    /// </summary>
    /// <remarks>
    /// <b>Sent and never trusted.</b> The section asks a sending client to add
    /// it, and says in the same breath that a receiving one must not depend on
    /// it: the requirement arrived in revision 1.28 and everything written
    /// before that sends nothing. So it goes out, because a room that has to
    /// add it for us is doing our work, and the reading is done from the room
    /// table instead.
    /// </remarks>
    public static XElement PrivateMark()

        => new (XName.Get("x", UserNamespace));

    /// <summary>
    /// XEP-0045, section 8.6: asks a moderated room to be allowed to speak.
    /// </summary>
    /// <remarks>
    /// A message and not an IQ, so <b>nothing answers it</b>: what comes back,
    /// if anything comes back, is a presence with a new role in it, whenever a
    /// moderator gets round to it. A client that waits for a result waits for
    /// ever.
    /// </remarks>
    public static XElement VoiceRequestXml(MucRole role = MucRole.Participant)

        => DataForm.Form("submit",
                         RequestNamespace,
                         DataForm.Field("muc#role", "list-single", "Requested role", role.AsText()));

    /// <summary>
    /// Somebody asking a room for voice, or null.
    /// </summary>
    /// <remarks>
    /// The form arrives as <c>type='form'</c> - it is a question being put to a
    /// moderator, not a decision. That is what tells it from the submit a
    /// moderator sends back, and both travel through the same room in the same
    /// kind of stanza, so the type is the only thing that separates them.
    /// </remarks>
    public static MucVoiceRequest? VoiceRequest(XElement message)
    {

        var x = message.Child(DataForm.Namespace, "x");

        if (x is null ||
            !DataForm.Is(x, "form") ||
            !JID.TryParse(message.Attr("from"), out var room))
        {
            return null;
        }

        String? valueOf(String name)
            => DataForm.Fields(x).
                        Where    (field => field.Attr("var") == name).
                        Select   (DataForm.ValueOf).
                        FirstOrDefault();

        if (valueOf("FORM_TYPE") != RequestNamespace)
            return null;

        return new MucVoiceRequest(
                   room.Bare,
                   JID.TryParse(valueOf("muc#jid")),
                   valueOf("muc#roomnick"),
                   MucRoles.ToRole(valueOf("muc#role")),
                   x
               );

    }

    /// <summary>
    /// XEP-0045, section 8.6: a moderator's answer to a voice request.
    /// </summary>
    /// <remarks>
    /// <b>The form goes back whole</b>, for the reason section 10.2 gives about
    /// a different form and D125 learnt the hard way: a submit is a state and
    /// not a patch. Here it costs less than there - nothing is stored - but the
    /// room matches the answer to the request by what is in it, and an answer
    /// carrying only the decision names nobody.
    ///
    /// The one field that may not be there already is the decision itself: a
    /// room is free to leave it out of the question it asks. Then it is added,
    /// because an answer without it is not an answer.
    /// </remarks>
    public static XElement VoiceAnswerXml(MucVoiceRequest request, Boolean allow)
    {

        var submit = ConfigWith(request.Form,
                                new Dictionary<String, String> {
                                    [RequestAllowField] = DataForm.Boolean(allow)
                                },
                                out var missing);

        if (missing.Contains(RequestAllowField))
            submit.Add(DataForm.Field(RequestAllowField, "boolean", null, DataForm.Boolean(allow)));

        return submit;

    }

    /// <summary>
    /// XEP-0045, section 7.10: asks a room about a nickname of one's own.
    /// </summary>
    public static XElement RegisterQuery()

        => new (XName.Get("query", RegisterNamespace));

    /// <summary>
    /// What a room answered about a nickname of ours.
    /// </summary>
    /// <remarks>
    /// <b>Two different things are being read out of one answer.</b>
    /// <c>&lt;registered/&gt;</c> says there is a reservation; the form beside
    /// it says what it is for. A room may send the first without the second,
    /// and a client that only looked for the name would report no reservation
    /// where there is one.
    /// </remarks>
    public static MucNicknameRegistration Registered(XElement? query)
    {

        if (query is null)
            return new MucNicknameRegistration(false);

        var registered = query.Child(RegisterNamespace, "registered") is not null;

        var nick = query.Child(DataForm.Namespace, "x") is XElement form
                       ? DataForm.Fields(form).
                                  Where    (field => field.Attr("var") == RoomNickField).
                                  Select   (DataForm.ValueOf).
                                  FirstOrDefault()
                       : null;

        return new MucNicknameRegistration(registered, String.IsNullOrEmpty(nick) ? null : nick);

    }

    /// <summary>
    /// XEP-0045, section 7.10: claims a nickname in a room.
    /// </summary>
    /// <remarks>
    /// The form the room sent goes back with the nickname in it, whole - the
    /// same rule as everywhere else a form is answered here.
    /// </remarks>
    public static XElement RegisterSubmit(XElement form, String nick, out IReadOnlyList<String> Missing)
    {

        var submit = ConfigWith(form,
                                new Dictionary<String, String> { [RoomNickField] = nick },
                                out Missing);

        return new XElement(XName.Get("query", RegisterNamespace), submit);

    }

    /// <summary>
    /// XEP-0045, section 10.9: take the room down.
    /// </summary>
    /// <param name="alternate">
    /// Where everybody should go instead. The service passes it on to every
    /// occupant, which makes it the one part of a destruction that is of any
    /// use to them.
    /// </param>
    /// <param name="reason">Why.</param>
    /// <param name="password">For the alternative, if it has one.</param>
    /// <remarks>
    /// In the <b>owner</b> namespace and not the admin one. The two look alike
    /// and are not: an admin may ban and make members, an owner may configure
    /// and destroy. A destruction sent as an admin query is refused by both
    /// services, and rightly.
    /// </remarks>
    public static XElement DestroyQuery(JID?     alternate  = null,
                                        string?  reason     = null,
                                        string?  password   = null)
    {

        var destroy = new XElement(XName.Get("destroy", OwnerNamespace));

        if (alternate is not null)
            destroy.Add(new XAttribute("jid", alternate.ToString()));

        if (reason is not null)
            destroy.Add(new XElement(XName.Get("reason", OwnerNamespace), reason));

        if (password is not null)
            destroy.Add(new XElement(XName.Get("password", OwnerNamespace), password));

        return new XElement(XName.Get("query", OwnerNamespace), destroy);

    }

    /// <summary>
    /// The room was destroyed, said in a presence - or null.
    /// </summary>
    /// <remarks>
    /// <b>The element and not a status code.</b> Section 10.9 gives the
    /// destruction no number of its own: what arrives is an ordinary unavailable
    /// presence with a <c>&lt;destroy/&gt;</c> inside the <c>muc#user</c>
    /// wrapper, so a client reading only the codes sees somebody leaving.
    ///
    /// And <b>not</b> conditioned on 110 either. The presence is addressed to
    /// each occupant under their own nickname, but whether a service marks it as
    /// theirs is left open by the section, which shows the example without any
    /// status code at all - and a <c>&lt;destroy/&gt;</c> is news about the room
    /// however it is labelled.
    ///
    /// The inner namespace is the <b>user</b> one here, not the owner one: the
    /// same element name lives in both, and which applies is decided by the
    /// wrapper it arrives in. Owner for the request, user for the news.
    /// </remarks>
    public static (JID? Alternate, string? Reason, string? Password)? Destruction(XElement presence)
    {

        var destroy = presence.Child(UserNamespace, "x")?.
                               Child(UserNamespace, "destroy");

        if (destroy is null)
            return null;

        return (JID.TryParse(destroy.Attr("jid")),
                destroy.Child(UserNamespace, "reason")?.Value,
                destroy.Child(UserNamespace, "password")?.Value);

    }

    /// <summary>
    /// XEP-0045, section 9.5: asks a room who is on one of its lists.
    /// </summary>
    /// <remarks>
    /// A <b>get</b>, and the item carries nothing but the affiliation being
    /// asked about. One list per question - the protocol has no way to ask for
    /// two, and a service handed two items answers about one of them without
    /// saying which.
    /// </remarks>
    public static XElement AffiliationListQuery(MucAffiliation affiliation)

        => new (XName.Get("query", AdminNamespace),
                new XElement(XName.Get("item", AdminNamespace),
                    new XAttribute("affiliation", affiliation.AsText())));

    /// <summary>
    /// The list a room answered with.
    /// </summary>
    /// <remarks>
    /// <b>An entry without a real address is dropped rather than carried.</b> An
    /// affiliation is held against an address and nothing else; an item that has
    /// only a nickname names somebody who is in the room now and says nothing
    /// about who is on the list, and keeping it would put a name in front of a
    /// caller that cannot be banned, promoted or removed.
    /// </remarks>
    public static IReadOnlyList<MucAffiliated> Affiliated(XElement? query)
    {

        var list = new List<MucAffiliated>();

        if (query is null)
            return list;

        foreach (var item in query.Children(AdminNamespace, "item"))
        {

            if (JID.TryParse(item.Attr("jid")) is not JID jid)
                continue;

            list.Add(new MucAffiliated(
                         jid,
                         MucRoles.ToAffiliation(item.Attr("affiliation")),
                         item.Attr("nick"),
                         item.Child(AdminNamespace, "reason")?.Value
                     ));

        }

        return list;

    }

    /// <summary>
    /// Our own invitation, come back refused - or null.
    /// </summary>
    /// <remarks>
    /// Recognised by the error type and the <c>&lt;invite/&gt;</c> together. The
    /// element alone is not enough: a room forwarding an invitation sends the
    /// same one, and the difference between being invited and having an
    /// invitation thrown back is the <c>type</c> on the message.
    ///
    /// The <c>to</c> of the invite is who was never asked. It is the one thing
    /// here worth carrying: the room and the reason are on the stanza anyway,
    /// the person is not.
    /// </remarks>
    public static (JID Room, JID Who)? RefusedInvitation(XElement message)
    {

        if (message.Attr("type") != "error")
            return null;

        var invite = message.Child(UserNamespace, "x")?.
                             Child(UserNamespace, "invite");

        if (invite is null ||
            !JID.TryParse(message.Attr("from"), out var room) ||
            !JID.TryParse(invite.Attr("to"),    out var who))
        {
            return null;
        }

        return (room.Bare, who);

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
