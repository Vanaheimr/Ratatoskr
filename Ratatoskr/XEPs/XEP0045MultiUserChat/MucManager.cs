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

using System.Threading;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

#region Delegates

/// <summary>XEP-0045: this client is now in a room.</summary>
public delegate Task OnRoomJoinedDelegate        (DateTimeOffset     Timestamp,
                                                  MucManager         Sender,
                                                  MucRoom            Room,
                                                  CancellationToken  CancellationToken);

/// <summary>XEP-0045: this client is out of a room - by choice or not.</summary>
public delegate Task OnRoomLeftDelegate          (DateTimeOffset     Timestamp,
                                                  MucManager         Sender,
                                                  MucRoom            Room,
                                                  MucUserInfo?       Why,
                                                  CancellationToken  CancellationToken);

/// <summary>XEP-0045: somebody entered, changed, or left a room.</summary>
public delegate Task OnOccupantDelegate          (DateTimeOffset     Timestamp,
                                                  MucManager         Sender,
                                                  MucRoom            Room,
                                                  MucOccupant        Occupant,
                                                  MucUserInfo?       Why,
                                                  CancellationToken  CancellationToken);

/// <summary>XEP-0045: somebody in a room is called something else from now on.</summary>
public delegate Task OnOccupantRenamedDelegate   (DateTimeOffset     Timestamp,
                                                  MucManager         Sender,
                                                  MucRoom            Room,
                                                  String             OldNick,
                                                  String             NewNick,
                                                  Boolean            IsSelf,
                                                  CancellationToken  CancellationToken);

/// <summary>XEP-0045: somebody wants us in a room we are not in.</summary>
public delegate Task OnRoomInvitationDelegate    (DateTimeOffset     Timestamp,
                                                  MucManager         Sender,
                                                  MucInvitation      Invitation,
                                                  CancellationToken  CancellationToken);

/// <summary>XEP-0045: somebody we invited is not coming.</summary>
public delegate Task OnInvitationDeclinedDelegate(DateTimeOffset     Timestamp,
                                                  MucManager         Sender,
                                                  MucDecline         Decline,
                                                  CancellationToken  CancellationToken);

/// <summary>XEP-0045: the subject of a room.</summary>
public delegate Task OnRoomSubjectDelegate       (DateTimeOffset     Timestamp,
                                                  MucManager         Sender,
                                                  MucRoom            Room,
                                                  String             Subject,
                                                  JID                By,
                                                  CancellationToken  CancellationToken);

#endregion


/// <summary>
/// What became of an attempt to enter a room.
/// </summary>
/// <param name="Room">The room, when the attempt succeeded.</param>
/// <param name="Refusal">
/// Why the service said no: <c>conflict</c> for a nickname already taken,
/// <c>not-authorized</c> for a missing password, <c>registration-required</c>
/// for a members-only room, <c>forbidden</c> for a ban.
/// </param>
/// <remarks>
/// Both null means the room never answered at all. Three outcomes rather than
/// an exception, because none of the three is exceptional: being refused from a
/// room is an ordinary thing for a room to do, and a caller has to handle it
/// either way.
/// </remarks>
public sealed record MucJoinOutcome(MucRoom? Room, StanzaError? Refusal)
{

    /// <summary>Are we in?</summary>
    public bool Joined => Room is not null;

    /// <summary>Did the room say nothing at all?</summary>
    public bool TimedOut => Room is null && Refusal is null;

}


/// <summary>
/// XEP-0045: the rooms this client is in.
/// </summary>
/// <remarks>
/// <b>The second presence model.</b> Everything else in this library reads a
/// presence as news about a contact and files it in the roster. A room sends
/// presences that look exactly the same and mean something else entirely:
/// somebody is in a room, usually under a name that is theirs only here, and
/// usually without their real address being given at all. The one question that
/// separates the two is whether the sender's bare address is a room this client
/// has entered - which is why the rooms are held here and asked before the
/// roster ever sees the stanza.
///
/// What is implemented is the visiting half of XEP-0045: entering, being there,
/// following who else is, and leaving. Configuring rooms, moderating them and
/// inviting people are not - see the README for what that leaves out.
/// </remarks>
public sealed class MucManager
{

    #region Data

    private readonly Func<string, Task>                                  _send;

    /// <summary>
    /// The way to ask a room something and hear an answer.
    /// </summary>
    /// <remarks>
    /// Separate from <c>_send</c> because moderating is the one half of this
    /// extension that is a request rather than an announcement: a kick either
    /// happened or was refused, and the difference arrives as an IQ result or an
    /// IQ error. Everything else a client does with a room is a presence or a
    /// message, which nobody answers.
    /// </remarks>
    private readonly Func<JID, string, XElement, CancellationToken, Task<XElement?>>?  _ask;

    private readonly ILogger                                             _logger;
    private readonly Dictionary<JID, MucRoom>                            _rooms    = [];
    private readonly Dictionary<JID, TaskCompletionSource<MucJoinOutcome>>  _joining = [];
    private readonly Lock                                                _lock     = new();

    /// <summary>
    /// How long a join may take before it is given up on.
    /// </summary>
    public TimeSpan JoinTimeout { get; set; } = TimeSpan.FromSeconds(15);

    #endregion

    #region Events

    public event OnRoomJoinedDelegate?       OnRoomJoined;
    public event OnRoomLeftDelegate?         OnRoomLeft;
    public event OnOccupantDelegate?         OnOccupantJoined;
    public event OnOccupantDelegate?         OnOccupantChanged;
    public event OnOccupantDelegate?         OnOccupantLeft;
    public event OnOccupantRenamedDelegate?  OnOccupantRenamed;
    public event OnRoomSubjectDelegate?      OnRoomSubject;
    public event OnRoomInvitationDelegate?      OnRoomInvitation;
    public event OnInvitationDeclinedDelegate?  OnInvitationDeclined;

    #endregion

    #region Constructor

    public MucManager(Func<string, Task>                                                 sendStanza,
                      Func<JID, string, XElement, CancellationToken, Task<XElement?>>?   askRoom = null,
                      ILogger?                                                           logger  = null)
    {
        _send    = sendStanza;
        _ask     = askRoom;
        _logger  = logger ?? NullLogger.Instance;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The rooms this client is in or is entering.
    /// </summary>
    public IReadOnlyDictionary<JID, MucRoom> Rooms
    {
        get
        {
            lock (_lock)
                return new Dictionary<JID, MucRoom>(_rooms);
        }
    }

    /// <summary>
    /// Is this address a room this client has anything to do with?
    /// </summary>
    /// <remarks>
    /// The question the whole separation from the roster hangs on, so it is
    /// asked of the <b>bare</b> address: a room writes under
    /// <c>room@service/nick</c>, and the nickname changes while the room does
    /// not.
    /// </remarks>
    public bool IsRoom(JID address)
    {
        lock (_lock)
            return _rooms.ContainsKey(address.Bare);
    }

    /// <summary>
    /// The room with this address, or null.
    /// </summary>
    public MucRoom? Room(JID address)
    {
        lock (_lock)
            return _rooms.GetValueOrDefault(address.Bare);
    }

    #endregion

    #region Entering and leaving

    /// <summary>
    /// XEP-0045, section 7.2: enters a room.
    /// </summary>
    /// <param name="room">The bare address of the room.</param>
    /// <param name="nick">The name to be known by in there.</param>
    /// <param name="password">For a password-protected room.</param>
    /// <param name="historyMaxStanzas">
    /// How much of what was said before the room should send along. The default
    /// leaves the decision to the room; zero asks for none.
    /// </param>
    /// <remarks>
    /// <b>Waits for one's own presence</b> and not for the subject. A room sends
    /// the occupants first, then one's own presence marked 110, then the history
    /// and the subject - so at 110 the list of who is present is complete, which
    /// is what a caller needs to go on. The subject arrives a moment later and
    /// updates the room by itself.
    ///
    /// The room is entered in the table <b>before</b> the presence goes out, and
    /// that order is not cosmetic: the answer may arrive while the send is still
    /// returning, and a room that is not in the table yet has its presences
    /// filed in the roster.
    /// </remarks>
    public async Task<MucJoinOutcome> JoinAsync(JID                room,
                                                string             nick,
                                                string?            password           = null,
                                                int?               historyMaxStanzas  = null,
                                                CancellationToken  cancellationToken  = default)
    {

        var bare = room.Bare;
        TaskCompletionSource<MucJoinOutcome> waiting;

        lock (_lock)
        {

            if (_rooms.TryGetValue(bare, out var already) && already.State == MucRoomState.Joined)
                return new MucJoinOutcome(already, null);

            _rooms[bare]    = new MucRoom(bare, nick);
            waiting         = new TaskCompletionSource<MucJoinOutcome>(
                                  TaskCreationOptions.RunContinuationsAsynchronously);
            _joining[bare]  = waiting;

        }

        await _send(MultiUserChat.JoinXml(JID.Parse($"{bare}/{nick}"),
                                          password,
                                          historyMaxStanzas));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(JoinTimeout);

        using (timeout.Token.Register(() => waiting.TrySetResult(new MucJoinOutcome(null, null))))
        {

            var outcome = await waiting.Task;

            lock (_lock)
            {

                _joining.Remove(bare);

                // A room that never answered, or refused, is not one we are in -
                // and leaving it in the table would keep every later presence
                // from that address out of the roster for good.
                if (!outcome.Joined)
                    _rooms.Remove(bare);

            }

            return outcome;

        }

    }

    /// <summary>
    /// XEP-0045, section 7.14: leaves a room.
    /// </summary>
    /// <remarks>
    /// The room is not forgotten here but when its answer arrives. The service
    /// echoes the departure with status 110, and that echo is the confirmation
    /// that one is out - dropping the room at once would file that echo in the
    /// roster.
    /// </remarks>
    public async Task<bool> LeaveAsync(JID room, string? status = null)
    {

        var current = Room(room);

        if (current is null)
            return false;

        await _send(MultiUserChat.LeaveXml(current.SelfAddress, status));
        return true;

    }

    /// <summary>
    /// XEP-0045, section 7.6: takes a different name in a room.
    /// </summary>
    /// <remarks>
    /// Whether it works is the room's decision - the name may be taken, or
    /// reserved for somebody else - and the answer comes as a presence, not as a
    /// result. What this returns is only whether there was a room to ask.
    /// </remarks>
    public async Task<bool> ChangeNickAsync(JID room, string newNick)
    {

        var current = Room(room);

        if (current is null)
            return false;

        await _send(MultiUserChat.NickChangeXml(JID.Parse($"{current.Address}/{newNick}")));
        return true;

    }

    /// <summary>
    /// XEP-0045, section 7.2.16: sets the subject of a room.
    /// </summary>
    public async Task<bool> SetSubjectAsync(JID room, string subject)
    {

        var current = Room(room);

        if (current is null)
            return false;

        await _send(MultiUserChat.SubjectXml(current.Address, subject));
        return true;

    }

    #endregion

    #region Moderating, and inviting

    /// <summary>
    /// XEP-0045, section 8: changes what somebody may do while they are here.
    /// </summary>
    /// <param name="nick">Whom - by their name in this room.</param>
    /// <param name="role">
    /// <see cref="MucRole.None"/> removes them from the room; see
    /// <see cref="KickAsync"/>, which is the same thing under the name people
    /// look for.
    /// </param>
    /// <returns>
    /// Whether the service did it. False for a refusal - not being a moderator
    /// is the usual one - and for no answer at all.
    /// </returns>
    public async Task<bool> SetRoleAsync(JID                room,
                                         string             nick,
                                         MucRole            role,
                                         string?            reason             = null,
                                         CancellationToken  cancellationToken  = default)
    {

        if (_ask is null || Room(room) is null)
            return false;

        var answer = await _ask(room.Bare, "set",
                                MultiUserChat.RoleQuery(nick, role, reason),
                                cancellationToken);

        return answer?.Attr("type") == "result";

    }

    /// <summary>
    /// XEP-0045, section 9: changes what somebody is to the room, beyond this
    /// visit.
    /// </summary>
    /// <param name="jid">
    /// Whom - by their <b>real</b> address, which an ordinary room does not
    /// give out. See <see cref="MultiUserChat.AffiliationQuery"/>.
    /// </param>
    public async Task<bool> SetAffiliationAsync(JID                room,
                                                JID                jid,
                                                MucAffiliation     affiliation,
                                                string?            reason             = null,
                                                CancellationToken  cancellationToken  = default)
    {

        if (_ask is null || Room(room) is null)
            return false;

        var answer = await _ask(room.Bare, "set",
                                MultiUserChat.AffiliationQuery(jid, affiliation, reason),
                                cancellationToken);

        return answer?.Attr("type") == "result";

    }

    /// <summary>
    /// Throws somebody out of the room for this visit (section 9.2).
    /// </summary>
    /// <remarks>
    /// They may come back. A kick is a role taken away, and a role only lasts
    /// as long as somebody is in the room - which is exactly the difference
    /// from <see cref="BanAsync"/>, and the reason a kick needs only a
    /// nickname.
    /// </remarks>
    public Task<bool> KickAsync(JID                room,
                                string             nick,
                                string?            reason             = null,
                                CancellationToken  cancellationToken  = default)

        => SetRoleAsync(room, nick, MucRole.None, reason, cancellationToken);

    /// <summary>
    /// Keeps somebody out of the room for good (section 9.1).
    /// </summary>
    /// <remarks>
    /// <b>Needs their real address</b>, and in a semi-anonymous room only a
    /// moderator is given it. So a ban can fail for a reason that has nothing to
    /// do with permissions - there was nothing to name - and
    /// <see cref="MucOccupant.RealJid"/> being null is where that shows.
    /// </remarks>
    public Task<bool> BanAsync(JID                room,
                               JID                jid,
                               string?            reason             = null,
                               CancellationToken  cancellationToken  = default)

        => SetAffiliationAsync(room, jid, MucAffiliation.Outcast, reason, cancellationToken);

    /// <summary>
    /// XEP-0045, section 7.8.1: asks somebody into a room, through the room.
    /// </summary>
    /// <returns>false when this client is not in that room.</returns>
    public async Task<bool> InviteAsync(JID room, JID who, string? reason = null)
    {

        if (Room(room) is null)
            return false;

        await _send(MultiUserChat.InviteXml(room, who, reason));
        return true;

    }

    /// <summary>
    /// XEP-0045, section 7.8.2: says no to an invitation.
    /// </summary>
    /// <remarks>
    /// The one thing here that is done for a room one is <b>not</b> in - so
    /// unlike everything else it asks the room table nothing. Declining is
    /// what happens instead of entering.
    /// </remarks>
    public async Task<bool> DeclineAsync(JID room, JID inviter, string? reason = null)
    {

        await _send(MultiUserChat.DeclineXml(room, inviter, reason));
        return true;

    }

    #endregion

    #region What comes back

    /// <summary>
    /// A presence from a room. True when it was one.
    /// </summary>
    public async Task<bool> ProcessPresenceAsync(XElement           presence,
                                                 JID                from,
                                                 string             type,
                                                 CancellationToken  cancellationToken = default)
    {

        var room = Room(from);

        if (room is null)
            return false;

        var info = MultiUserChat.UserInfo(presence);
        var nick = from.Resourcepart;

        // RFC 6120, section 8.3: a refusal is not a presence state. For a join
        // it is the answer, and for anything else it is news the caller gets
        // through the ordinary error path.
        if (type == "error")
        {

            var refusal = StanzaError.TryParse(presence.ToString(), out var parsed) && parsed is not null
                              ? parsed
                              : new StanzaError(StanzaErrorType.Cancel, "undefined-condition");

            _logger.LogDebug("The room {Room} refused: {Error}", room.Address, refusal);

            room.State = MucRoomState.Left;
            Finish(room.Address, new MucJoinOutcome(null, refusal));

            return true;

        }

        if (nick is null)
        {
            // A presence from the bare address of the room is about the room
            // itself and about nobody in it.
            _logger.LogDebug("Presence from the room {Room} without an occupant", room.Address);
            return true;
        }

        if (type == "unavailable")
        {

            // Section 7.6: a nickname change is an unavailable presence for the
            // old name carrying the new one, followed by an available presence
            // under the new name. Read as a departure it empties the room of
            // everybody who ever renamed themselves.
            if (info is not null && info.Has(MucStatus.NickChanged) && info.NewNick is not null)
            {

                room.Rename(nick, info.NewNick);

                if (info.IsSelf)
                    room.Nick = info.NewNick;

                await OnOccupantRenamed.InvokeAllAsync(handler => handler(Timestamp.Now, this, room,
                                                                          nick, info.NewNick, info.IsSelf,
                                                                          cancellationToken), _logger);
                return true;

            }

            var leaving = room.Get(nick) ?? new MucOccupant(nick, MucAffiliation.None, MucRole.None);
            room.Remove(nick);

            if (info?.IsSelf == true)
            {

                room.State = MucRoomState.Left;
                room.Clear();

                lock (_lock)
                    _rooms.Remove(room.Address);

                await OnRoomLeft.InvokeAllAsync(handler => handler(Timestamp.Now, this, room, info,
                                                                   cancellationToken), _logger);

            }

            else
                await OnOccupantLeft.InvokeAllAsync(handler => handler(Timestamp.Now, this, room, leaving,
                                                                       info, cancellationToken), _logger);

            return true;

        }

        // Everything else is somebody being present.
        var occupant = info?.Item with { Nick = nick }
                           ?? new MucOccupant(nick, MucAffiliation.None, MucRole.None,
                                              Show:    presence.ChildValue("show"),
                                              Status:  presence.ChildValue("status"));

        var known = room.Get(nick) is not null;

        room.Set(occupant);

        if (info?.Has(MucStatus.NonAnonymous) == true)
            room.IsNonAnonymous = true;

        if (info?.Has(MucStatus.Created) == true)
            room.WasCreated = true;

        if (info?.IsSelf == true)
        {

            // The room addresses us by the name it gave us, which need not be
            // the one that was asked for (status 210). What comes back holds.
            room.Nick   = nick;
            room.State  = MucRoomState.Joined;

            Finish(room.Address, new MucJoinOutcome(room, null));

            await OnRoomJoined.InvokeAllAsync(handler => handler(Timestamp.Now, this, room,
                                                                 cancellationToken), _logger);

        }

        else if (known)
            await OnOccupantChanged.InvokeAllAsync(handler => handler(Timestamp.Now, this, room, occupant,
                                                                      info, cancellationToken), _logger);

        else
            await OnOccupantJoined.InvokeAllAsync(handler => handler(Timestamp.Now, this, room, occupant,
                                                                     info, cancellationToken), _logger);

        return true;

    }

    /// <summary>
    /// A message from a room. True when this was the subject and nothing else
    /// needs to happen with it.
    /// </summary>
    /// <remarks>
    /// Only the subject is claimed here. Everything a room says with a body is
    /// an ordinary message and travels the ordinary way, so that a client which
    /// knows nothing of rooms still shows it.
    /// </remarks>
    public async Task<bool> ProcessMessageAsync(XElement           message,
                                                JID                from,
                                                CancellationToken  cancellationToken = default)
    {

        // Before the room table, and that is the whole difficulty of it: an
        // invitation is the one thing a room says about a room this client is
        // not in, so the question everything else here is recognised by -
        // "have we entered this one" - answers no for the very message that
        // matters.
        if (MultiUserChat.Invitation(message) is MucInvitation invitation)
        {

            await OnRoomInvitation.InvokeAllAsync(handler => handler(Timestamp.Now, this, invitation,
                                                                     cancellationToken), _logger);
            return true;

        }

        if (MultiUserChat.Decline(message) is MucDecline declined)
        {

            await OnInvitationDeclined.InvokeAllAsync(handler => handler(Timestamp.Now, this, declined,
                                                                         cancellationToken), _logger);
            return true;

        }

        var room = Room(from);

        if (room is null || !MultiUserChat.IsSubjectChange(message))
            return false;

        var subject = message.Child("subject")!.Value;

        room.Subject    = subject;
        room.SubjectBy  = from;

        await OnRoomSubject.InvokeAllAsync(handler => handler(Timestamp.Now, this, room, subject, from,
                                                              cancellationToken), _logger);

        return true;

    }

    #endregion

    #region (private) Finish(room, outcome)

    /// <summary>
    /// Hands the outcome to whoever is waiting on a join, if anybody is.
    /// </summary>
    private void Finish(JID room, MucJoinOutcome outcome)
    {

        TaskCompletionSource<MucJoinOutcome>? waiting;

        lock (_lock)
            _joining.TryGetValue(room.Bare, out waiting);

        waiting?.TrySetResult(outcome);

    }

    #endregion

}
