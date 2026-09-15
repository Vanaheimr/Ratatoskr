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
/// XEP-0045: what somebody is to a room, for as long as the room exists.
/// </summary>
/// <remarks>
/// The lasting half of the pair. An affiliation outlives the visit: whoever is
/// a member stays one after leaving, and an outcast stays banned. It is kept in
/// the room, not in the session, which is why it can be set for somebody who is
/// not there.
///
/// <see cref="MucRole"/> is the other half and the one that changes by the
/// minute.
/// </remarks>
public enum MucAffiliation
{

    /// <summary>Banned. Cannot enter at all (section 5.2).</summary>
    Outcast,

    /// <summary>Nothing in particular - the ordinary visitor of an open room.</summary>
    None,

    /// <summary>On the list. In a members-only room, the reason one may enter.</summary>
    Member,

    /// <summary>May administer: ban, and make members.</summary>
    Admin,

    /// <summary>Owns the room: may configure and destroy it.</summary>
    Owner

}


/// <summary>
/// XEP-0045: what somebody may do in a room while they are in it.
/// </summary>
/// <remarks>
/// The temporary half. A role lasts for the visit and no longer: leave, and it
/// is gone. Which role follows from which affiliation is the room's decision
/// and not this client's - a moderated room hands out <see cref="Visitor"/> to
/// people who would be <see cref="Participant"/> anywhere else.
/// </remarks>
public enum MucRole
{

    /// <summary>Not in the room. Presence type 'unavailable' says so as well.</summary>
    None,

    /// <summary>Present, may not speak in a moderated room.</summary>
    Visitor,

    /// <summary>Present and may speak.</summary>
    Participant,

    /// <summary>May grant and take away the voice of others, and kick.</summary>
    Moderator

}


/// <summary>
/// The two strings XEP-0045 writes them as, and back.
/// </summary>
/// <remarks>
/// <b>An unknown value is not an error and not the highest.</b> It becomes
/// <see cref="MucAffiliation.None"/> or <see cref="MucRole.None"/>, which is the
/// reading that gives away nothing: a room that invents a word this client has
/// never heard of must not thereby make somebody a moderator here.
/// </remarks>
public static class MucRoles
{

    public static MucAffiliation ToAffiliation(string? text)

        => text switch {
               "owner"    => MucAffiliation.Owner,
               "admin"    => MucAffiliation.Admin,
               "member"   => MucAffiliation.Member,
               "outcast"  => MucAffiliation.Outcast,
               _          => MucAffiliation.None
           };

    public static MucRole ToRole(string? text)

        => text switch {
               "moderator"    => MucRole.Moderator,
               "participant"  => MucRole.Participant,
               "visitor"      => MucRole.Visitor,
               _              => MucRole.None
           };

    public static string AsText(this MucAffiliation affiliation)

        => affiliation switch {
               MucAffiliation.Owner    => "owner",
               MucAffiliation.Admin    => "admin",
               MucAffiliation.Member   => "member",
               MucAffiliation.Outcast  => "outcast",
               _                       => "none"
           };

    public static string AsText(this MucRole role)

        => role switch {
               MucRole.Moderator    => "moderator",
               MucRole.Participant  => "participant",
               MucRole.Visitor      => "visitor",
               _                    => "none"
           };

}


/// <summary>
/// XEP-0045, section 15.6: the numbers a room uses to say what just happened.
/// </summary>
/// <remarks>
/// They are the whole of the protocol's vocabulary for events that otherwise
/// look identical. An <c>unavailable</c> presence is a person leaving, being
/// kicked, being banned, or changing their nickname - four different things,
/// one stanza, and the number is the only thing that tells them apart.
/// </remarks>
public static class MucStatus
{

    /// <summary>The room shows real addresses to everybody in it.</summary>
    public const int NonAnonymous = 100;

    /// <summary>Your own affiliation changed while you were in the room.</summary>
    public const int AffiliationChanged = 101;

    /// <summary>The room is logged in public.</summary>
    public const int Logged = 170;

    /// <summary>
    /// <b>This presence is yours.</b> The one every join waits for: a room sends
    /// the occupants first and one's own last, and without the number they are
    /// indistinguishable - a nickname is not proof, since the room may have
    /// assigned a different one (see <see cref="NickAssigned"/>).
    /// </summary>
    public const int Self = 110;

    /// <summary>A newly created room, still locked until it is configured.</summary>
    public const int Created = 201;

    /// <summary>The service gave you a different nickname from the one asked for.</summary>
    public const int NickAssigned = 210;

    /// <summary>Banned. Arrives as an unavailable presence.</summary>
    public const int Banned = 301;

    /// <summary>A nickname change. The new one stands in the item.</summary>
    public const int NickChanged = 303;

    /// <summary>Kicked.</summary>
    public const int Kicked = 307;

    /// <summary>Removed because the room became members-only.</summary>
    public const int MembersOnly = 321;

    /// <summary>Removed because the room became semi-anonymous.</summary>
    public const int SemiAnonymous = 322;

    /// <summary>Removed because the service is shutting down.</summary>
    public const int Shutdown = 332;

}


/// <summary>
/// Somebody in a room.
/// </summary>
/// <param name="Nick">
/// Their name in this room - the resource of the address the room writes them
/// under, and the only name everybody present shares.
/// </param>
/// <param name="Affiliation">What they are to the room, beyond this visit.</param>
/// <param name="Role">What they may do while they are in it.</param>
/// <param name="RealJid">
/// Their actual address, or null.
/// <b>Null is the normal case</b>, and not a gap in the parsing: a room is
/// semi-anonymous by default and tells nobody but its moderators who anybody
/// really is. A client that needs the real address for something has to cope
/// with not having it, rather than treating its absence as an error.
/// </param>
/// <param name="Show">The presence show value, if they set one.</param>
/// <param name="Status">Their presence text, if they set one.</param>
public sealed record MucOccupant(string          Nick,
                                 MucAffiliation  Affiliation,
                                 MucRole         Role,
                                 JID?            RealJid  = null,
                                 string?         Show     = null,
                                 string?         Status   = null)
{

    /// <summary>
    /// May this occupant speak in a moderated room?
    /// </summary>
    public bool MaySpeak
        => Role is MucRole.Participant or MucRole.Moderator;

}
