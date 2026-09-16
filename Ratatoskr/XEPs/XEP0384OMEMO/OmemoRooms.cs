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

using System.Xml.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;


#region (record) OmemoRoomSent

/// <summary>
/// What became of an attempt to write encrypted into a room.
/// </summary>
/// <param name="MessageId">The id it went out under, or null when it did not go.</param>
/// <param name="Recipients">Whose devices it was encrypted to.</param>
/// <param name="Skipped">
/// The individual devices no session could be built with - the same reporting
/// as one-to-one, and the same reason for sending anyway: one unreachable
/// device must not make a person unreachable.
/// </param>
/// <param name="Refusal">
/// Why nothing was sent, or null. <b>A refusal here is not a failure to
/// report and move on from</b>: it means there is somebody in the room this
/// message could not have been read by, and a caller that ignores it has a
/// conversation that looks whole and is not.
/// </param>
/// <remarks>
/// Not an exception, because a room filling and emptying is ordinary: somebody
/// joins whose presence has not arrived yet, and for a moment the room cannot
/// be written to. That is a thing to say and wait out, not a fault.
/// </remarks>
public sealed record OmemoRoomSent(String?                            MessageId,
                                   IReadOnlyList<JID>                 Recipients,
                                   IReadOnlyList<OmemoSkippedDevice>  Skipped,
                                   String?                            Refusal)
{

    /// <summary>
    /// Did it go?
    /// </summary>
    public Boolean Sent
        => MessageId is not null;

}

#endregion

#region (record) OmemoRoomRecipients

/// <summary>
/// Whose devices a message to this room would go to.
/// </summary>
/// <param name="Jids">
/// The real bare addresses of everybody present, this client's own excepted -
/// <see cref="OmemoManager.EncryptAsync"/> adds that itself.
/// </param>
/// <param name="Anonymous">
/// The nicknames the room gave no real address for. <b>Not an empty list is a
/// refusal and not a warning</b>: see <see cref="OmemoRooms"/>.
/// </param>
public sealed record OmemoRoomRecipients(IReadOnlyList<JID>     Jids,
                                         IReadOnlyList<String>  Anonymous)
{

    /// <summary>
    /// Can everybody in this room be written to?
    /// </summary>
    public Boolean Complete
        => Anonymous.Count == 0;

}

#endregion


/// <summary>
/// XEP-0384 in a room (XEP-0045): who one can encrypt to when one only has
/// nicknames.
/// </summary>
/// <remarks>
/// <b>OMEMO encrypts to the devices of a bare address, and a room hands out
/// nicknames.</b> That sentence is the whole of the problem. Everything here
/// follows from it and from the three things that make it tractable.
///
/// <b>One: a room may be made to tell.</b> A room is semi-anonymous by default
/// and gives real addresses to its moderators only, which means a participant
/// in an ordinary room cannot encrypt to anybody at all. With
/// <c>muc#roomconfig_whois = anyone</c> (section 10.2.1) the service writes the
/// real address into every occupant's presence, and only then is there anything
/// to encrypt to. <see cref="XMPPConnection.MakeRoomNonAnonymousAsync"/> asks
/// for that, and it is a change to what the room <i>is</i>: everybody in it can
/// then see who everybody else really is.
///
/// <b>Two: half a room is not a room.</b> If one occupant's real address is
/// unknown, there is a person present who will not be able to read what is
/// written. Three answers are possible and only one is usable, and it is the
/// opposite of the answer for a single missing <i>device</i> in
/// <see cref="OmemoManager"/>:
///
/// <list type="bullet">
/// <item><b>Send to the rest anyway.</b> Silently excluding somebody who is
///       standing in the room is worse than not sending: the room looks whole
///       to everybody in it, and the person who cannot read is not told, and
///       neither is the sender.</item>
/// <item><b>Send unencrypted.</b> The worst one, as always - the sender
///       believes they encrypted.</item>
/// <item><b>Refuse and say who is missing.</b> What this does.</item>
/// </list>
///
/// The difference from the device case is that a device is invisible and a
/// person is not. Skipping a contact's fourth device makes them unreachable on
/// one machine; skipping an occupant makes a conversation that visibly includes
/// them exclude them.
///
/// <b>Three: the service says who is behind a nickname, and the envelope
/// checks it.</b> This is the part worth being exact about, because it is where
/// encrypting in a room differs from encrypting to a person. The mapping from
/// <c>room@service/alice</c> to <c>alice@example.org</c> comes from the room
/// service and from nowhere else - so a service that lies could name itself
/// behind a nickname and be encrypted to. What stops that is not this file: it
/// is XEP-0420. The sender writes their own real address <i>inside</i> the
/// encryption, and <see cref="OmemoManager.DecryptAsync"/> refuses an envelope
/// whose sender is not the one the stanza claims. A room that lies about who
/// somebody is therefore produces a message that does not decrypt, rather than
/// one that decrypts as the wrong person.
///
/// What it does <b>not</b> stop is a service adding an occupant nobody notices,
/// or naming a real address that exists. That is the honest limit: in a room,
/// the service decides who is in the conversation. Encryption keeps the server
/// from reading along; it does not keep the room from inviting.
/// </remarks>
public static class OmemoRooms
{

    #region RecipientsOf(Room)

    /// <summary>
    /// Whose devices a message to this room would go to.
    /// </summary>
    /// <remarks>
    /// Our own occupant is left out and not because it would be wrong to
    /// include it: <see cref="OmemoManager.EncryptAsync"/> appends our own bare
    /// address anyway, so that our other devices see what this one wrote. What
    /// it would be is a second entry for the same person, and a duplicate in
    /// the list would make the count say something untrue.
    /// </remarks>
    public static OmemoRoomRecipients RecipientsOf(MucRoom Room)
    {

        var jids       = new List<JID>();
        var anonymous  = new List<String>();

        foreach (var (nick, occupant) in Room.Occupants)
        {

            if (nick == Room.Nick)
                continue;

            if (occupant.RealJid is JID real)
            {
                if (!jids.Contains(real.Bare))
                    jids.Add(real.Bare);
            }

            else
                anonymous.Add(nick);

        }

        return new OmemoRoomRecipients(jids, anonymous);

    }

    #endregion

    #region WhyNot(Room)

    /// <summary>
    /// Why a message to this room cannot be encrypted, or null when it can.
    /// </summary>
    /// <remarks>
    /// Meant to be shown. A client that offers a lock in a room has to be able
    /// to say why it is greyed out, and "because the room is semi-anonymous" is
    /// something a person can act on - by making it non-anonymous, or by
    /// deciding that a room is not the place for this.
    /// </remarks>
    public static String? WhyNot(MucRoom Room)
    {

        if (Room.State != MucRoomState.Joined)
            return "this room has not been entered";

        var recipients = RecipientsOf(Room);

        if (Room.Occupants.Count <= 1)
            return "nobody else is in this room";

        if (!recipients.Complete)
            return Room.IsNonAnonymous

                       // Non-anonymous and still missing somebody: their
                       // presence has not arrived yet, or the service did not
                       // write the address in. Either way it is a wait and not
                       // a setting.
                       ? $"the room gave no real address for {String.Join(", ", recipients.Anonymous)}"

                       : "this room is semi-anonymous: it tells only its moderators who anybody " +
                         "really is, and one cannot encrypt to a nickname";

        return null;

    }

    #endregion

    #region IsOwnReflection(Room, From) / SenderOf(Room, From)

    /// <summary>
    /// Is this the room handing us back what we just wrote?
    /// </summary>
    /// <remarks>
    /// <b>It has to be recognised before decryption is even attempted</b>, and
    /// not because decrypting it would be expensive: it cannot succeed.
    /// <see cref="OmemoManager.EncryptAsync"/> puts in a key for every device
    /// except the one doing the encrypting, which is correct - a device cannot
    /// keep a ratchet with itself - and a room sends every message to everybody
    /// including the sender. So our own message comes back as one there is
    /// nothing in for us.
    ///
    /// Without this it is reported as a message that could not be read, and in
    /// a room one writes in that is every single line one sends.
    /// </remarks>
    public static Boolean IsOwnReflection(MucRoom Room, JID From)

        => From.Resourcepart is String nick &&
           nick == Room.Nick;

    /// <summary>
    /// Whose message is this, really?
    /// </summary>
    /// <remarks>
    /// <b>Null is an answer and the common one</b>: in a semi-anonymous room
    /// nobody but a moderator can name the sender, so nothing can be decrypted
    /// there. What must not happen is the obvious repair - taking the room's
    /// bare address, or the nickname, for the sender. Both would send the
    /// session lookup somewhere that cannot have a session, and the first would
    /// file every occupant of the room under one identity.
    /// </remarks>
    public static JID? SenderOf(MucRoom Room, JID From)
    {

        if (From.Resourcepart is not String nick)
            return null;

        return Room.Occupants.TryGetValue(nick, out var occupant)
                   ? occupant.RealJid?.Bare
                   : null;

    }

    #endregion

    #region IsEncryptedGroupChat(Message)

    /// <summary>
    /// A message from a room that is carrying an OMEMO element.
    /// </summary>
    /// <remarks>
    /// Asked before the ordinary encrypted branch, which takes the address on
    /// the stanza for the sender. In a room that address is the occupant
    /// address, and using it would look up a session under the room.
    /// </remarks>
    public static Boolean IsEncryptedGroupChat(XElement Message)

        => Message.Attr("type") == "groupchat" &&
           OmemoEncryptedElement.TryRead(Message, out _);

    #endregion

}
