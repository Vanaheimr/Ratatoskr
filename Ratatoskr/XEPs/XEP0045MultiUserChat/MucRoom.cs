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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// How far along a visit to a room is.
/// </summary>
public enum MucRoomState
{

    /// <summary>
    /// The presence has gone out and the room has not finished answering.
    /// </summary>
    /// <remarks>
    /// Not a formality. Everything a room says about a join - the occupants, the
    /// history, one's own presence, the subject - arrives as ordinary stanzas
    /// from that address, and until the join is over they have to be taken as
    /// part of it rather than as news.
    /// </remarks>
    Joining,

    /// <summary>Inside, and the subject has arrived.</summary>
    Joined,

    /// <summary>Out again - left, kicked, banned, or the room refused.</summary>
    Left

}


/// <summary>
/// XEP-0045: a room this client is in, or is entering.
/// </summary>
/// <remarks>
/// <b>The occupants are not contacts and this is not a roster.</b> They come and
/// go with the visit, their real addresses are usually not given at all, and
/// what identifies them is a nickname that is unique in this room and nowhere
/// else. Keeping the two apart is the whole reason this type exists rather than
/// a few more fields on the roster.
/// </remarks>
public sealed class MucRoom
{

    #region Data

    private readonly Dictionary<string, MucOccupant>  _occupants  = [];
    private readonly Lock                             _lock       = new();

    #endregion

    #region Properties

    /// <summary>
    /// The bare address of the room.
    /// </summary>
    public JID Address { get; }

    /// <summary>
    /// The name this client goes by in here.
    /// </summary>
    /// <remarks>
    /// Not necessarily the one that was asked for: a service may assign a
    /// different one and say so with status 210, and one that comes back
    /// different has to be believed - every message from here is addressed
    /// under it.
    /// </remarks>
    public string Nick { get; internal set; }

    /// <summary>
    /// The address this client is known by inside the room.
    /// </summary>
    public JID SelfAddress
        => JID.Parse($"{Address.Bare}/{Nick}");

    /// <summary>How far along the visit is.</summary>
    public MucRoomState State { get; internal set; } = MucRoomState.Joining;

    /// <summary>
    /// The subject of the room, or null when it has none.
    /// </summary>
    /// <remarks>
    /// Null and empty are different here. A room without a subject has never
    /// had one; an empty one had a subject that somebody removed, and the
    /// removal is a change like any other.
    /// </remarks>
    public string? Subject { get; internal set; }

    /// <summary>Who last set the subject, by their address in the room.</summary>
    public JID? SubjectBy { get; internal set; }

    /// <summary>
    /// The room shows everybody's real address (status 100).
    /// </summary>
    /// <remarks>
    /// Worth knowing before writing anything: in such a room the connection
    /// between the nickname and the account is public to everybody present.
    /// </remarks>
    public bool IsNonAnonymous { get; internal set; }

    /// <summary>
    /// The room did not exist and was created by this join (status 201).
    /// </summary>
    /// <remarks>
    /// A newly created room is <b>locked</b> until its owner configures it
    /// (section 10.1.2), and nobody else can enter in the meantime. This client
    /// does not configure rooms, so the flag is here to be reported rather than
    /// acted upon - a caller that gets it and does nothing has a room only they
    /// can see.
    /// </remarks>
    public bool WasCreated { get; internal set; }

    /// <summary>
    /// Everybody in the room, by nickname.
    /// </summary>
    public IReadOnlyDictionary<string, MucOccupant> Occupants
    {
        get
        {
            lock (_lock)
                return new Dictionary<string, MucOccupant>(_occupants);
        }
    }

    /// <summary>
    /// What this client is in this room - or null before its own presence has
    /// come back.
    /// </summary>
    public MucOccupant? Me
    {
        get
        {
            lock (_lock)
                return _occupants.GetValueOrDefault(Nick);
        }
    }

    #endregion

    #region Constructor

    internal MucRoom(JID address, string nick)
    {
        Address  = address.Bare;
        Nick     = nick;
    }

    #endregion

    #region Changing the occupants

    internal MucOccupant? Get(string nick)
    {
        lock (_lock)
            return _occupants.GetValueOrDefault(nick);
    }

    internal void Set(MucOccupant occupant)
    {
        lock (_lock)
            _occupants[occupant.Nick] = occupant;
    }

    internal void Remove(string nick)
    {
        lock (_lock)
            _occupants.Remove(nick);
    }

    /// <summary>
    /// Carries an occupant over to a new nickname (status 303).
    /// </summary>
    /// <remarks>
    /// Carried over and not re-created: the affiliation and the role belong to
    /// the person and do not change because the label did. Whoever removes and
    /// re-adds instead turns every moderator into a visitor for as long as it
    /// takes the next presence to arrive - and in a room that sends none,
    /// permanently.
    /// </remarks>
    internal void Rename(string from, string to)
    {
        lock (_lock)
        {

            if (!_occupants.Remove(from, out var occupant))
                return;

            _occupants[to] = occupant with { Nick = to };

        }
    }

    internal void Clear()
    {
        lock (_lock)
            _occupants.Clear();
    }

    #endregion

    public override string ToString()
        => $"{Address}/{Nick} ({State}, {Occupants.Count} occupants)";

}
