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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

#region (delegate) OnRoster...Delegate

/// <summary>
/// A contact entered the roster.
/// </summary>
public delegate Task OnRosterItemAddedDelegate           (DateTimeOffset     Timestamp,
                                                          Roster             Sender,
                                                          RosterItem         Item,
                                                          CancellationToken  CancellationToken);

/// <summary>
/// Something about a contact changed - name, groups, subscription or presence.
/// </summary>
public delegate Task OnRosterItemUpdatedDelegate         (DateTimeOffset     Timestamp,
                                                          Roster             Sender,
                                                          RosterItem         Item,
                                                          CancellationToken  CancellationToken);

/// <summary>
/// A contact left the roster.
/// </summary>
public delegate Task OnRosterItemRemovedDelegate         (DateTimeOffset     Timestamp,
                                                          Roster             Sender,
                                                          JID                BareJid,
                                                          CancellationToken  CancellationToken);

/// <summary>
/// Someone asks to see our presence (RFC 6121, section 3.1).
/// </summary>
public delegate Task OnRosterSubscriptionRequestDelegate (DateTimeOffset     Timestamp,
                                                          Roster             Sender,
                                                          JID                From,
                                                          String             Status,
                                                          CancellationToken  CancellationToken);

#endregion


/// <summary>
/// Roster manager with subscription handling
/// </summary>
public sealed class Roster
{

    #region Data

    private readonly Dictionary<JID, RosterItem>     _items  = new();
    private readonly HashSet<JID>                    _asked  = new();
    private readonly Lock                            _lock   = new();
    private readonly ILogger                         _logger;

    #endregion

    #region Properties

    public String? Version { get; set; }

    public IReadOnlyCollection<RosterItem> Items
    {
        get { lock (_lock) return _items.Values.ToList(); }
    }

    #endregion

    #region Events

    /// <summary>
    /// A contact entered the roster.
    /// </summary>
    public event OnRosterItemAddedDelegate?            OnItemAdded;

    /// <summary>
    /// Something about a contact changed.
    /// </summary>
    public event OnRosterItemUpdatedDelegate?          OnItemUpdated;

    /// <summary>
    /// A contact left the roster.
    /// </summary>
    public event OnRosterItemRemovedDelegate?          OnItemRemoved;

    /// <summary>
    /// Someone asks to see our presence.
    /// </summary>
    public event OnRosterSubscriptionRequestDelegate?  OnSubscriptionRequest;

    #endregion

    #region Constructor(s)

    /// <summary>
    /// Creates a new roster.
    /// </summary>
    /// <param name="LoggerFactory">
    /// Optional; used for reporting a handler that threw, and for the one
    /// subscription change this roster notes without applying it.
    /// </param>
    public Roster(ILoggerFactory? LoggerFactory = null)
    {

        _logger = LoggerFactory is not null
                      ? LoggerFactory.CreateLogger<Roster>()
                      : NullLogger<Roster>.Instance;

    }

    #endregion


    #region GetItem(Jid)

    public RosterItem? GetItem(JID Jid)
    {

        var bareJid = Jid.Bare;

        lock (_lock)
            return _items.TryGetValue(bareJid, out var item) ? item : null;

    }

    #endregion

    #region ProcessRosterItemAsync(NewItem, CancellationToken = default)

    /// <summary>
    /// Takes over a contact from a roster push or result.
    /// </summary>
    /// <remarks>
    /// Deciding and announcing are two steps, and they used to be one: the
    /// event was raised while the lock was held. A handler that asks the roster
    /// something then waits for a lock its own caller holds - and on a
    /// re-entrant path that is a deadlock rather than a delay. Awaiting inside
    /// a <c>lock</c> is not even expressible in C#, so the compiler now
    /// enforces what the comment in <see cref="ReplaceAllAsync"/> always
    /// claimed was the case.
    /// </remarks>
    public async Task ProcessRosterItemAsync(RosterItem         NewItem,
                                             CancellationToken  CancellationToken   = default)
    {

        var         bareJid = NewItem.Jid.Bare;
        RosterItem  item;
        Boolean     isNew;

        lock (_lock)
        {

            if (_items.TryGetValue(bareJid, out var existing))
            {

                existing.Name          = NewItem.Name;
                existing.Subscription  = NewItem.Subscription;
                existing.Groups.Clear();
                existing.Groups.AddRange(NewItem.Groups);

                item   = existing;
                isNew  = false;

            }
            else
            {
                _items[bareJid] = NewItem;
                item   = NewItem;
                isNew  = true;
            }

        }

        if (isNew)
            await OnItemAdded.  InvokeAllAsync(handler => handler(Timestamp.Now, this, item, CancellationToken), _logger);
        else
            await OnItemUpdated.InvokeAllAsync(handler => handler(Timestamp.Now, this, item, CancellationToken), _logger);

    }

    #endregion

    #region ReplaceAllAsync(Items, CancellationToken = default)

    /// <summary>
    /// RFC 6121, section 2.1.4: Takes the result of a roster request as the
    /// complete roster.
    /// </summary>
    /// <remarks>
    /// The difference to <see cref="ProcessRosterItemAsync"/> is the removal. A
    /// roster result is not an addition but the state of things: whatever is
    /// not in it does not exist any more.
    ///
    /// Previously it was merged in, and the consequence was a contact one
    /// cannot get rid of. Whoever deletes it on another device while this one
    /// is logged off gets it back at the next login - the server no longer
    /// sends it, but nobody takes it out. When deleting during operation this
    /// never shows, because then a push with <c>subscription='remove'</c>
    /// arrives.
    ///
    /// This is called exclusively for the result, never for a push. A push
    /// carries exactly the changed entries; treating it this way would delete
    /// the whole rest of the roster on every change.
    /// </remarks>
    public async Task ReplaceAllAsync(IEnumerable<RosterItem>  Items,
                                      CancellationToken        CancellationToken   = default)
    {

        var fresh  = Items.ToList();

        // No comparer any more: the JID knows how it compares - local and
        // domain part without regard to spelling, the resourcepart with it -
        // and a set that has to be told how is a set that can be told wrong.
        var kept   = new HashSet<JID>(fresh.Select(item => item.Jid.Bare));

        List<JID> dropped;

        lock (_lock)
            dropped = _items.Keys.Where(key => !kept.Contains(key)).ToList();

        // Outside the lock: both calls take it themselves, and the events are
        // not meant to run under it.
        foreach (var item in fresh)
            await ProcessRosterItemAsync(item, CancellationToken);

        foreach (var jid in dropped)
            await RemoveItemAsync(jid, CancellationToken);

    }

    #endregion

    #region NoteOwnSubscriptionRequest(Jid)

    /// <summary>
    /// Notes that we have asked this contact to let us see their presence
    /// (RFC 6121, section 3.1.1): a <c>subscribe</c> of ours has gone out.
    /// </summary>
    /// <remarks>
    /// What the RFC keeps as <c>ask='subscribe'</c> on the roster entry, as far
    /// as this side knows it - and it is kept beside the entries rather than on
    /// one of them, because the case it exists for is the case where there is
    /// no entry: the push that would carry it is exactly what may be missing.
    ///
    /// It is no promise about what the server did with the request. The server
    /// may refuse it, and nothing noted here counts as a subscription; the note
    /// says only that an inbound <c>subscribed</c> from this address would be
    /// an answer and not an announcement out of nowhere.
    /// </remarks>
    /// <param name="Jid">The address the request went to.</param>
    public void NoteOwnSubscriptionRequest(JID Jid)
    {
        lock (_lock)
            _asked.Add(Jid.Bare);
    }

    #endregion

    #region HasOwnSubscriptionRequest(Jid)

    /// <summary>
    /// Whether a <c>subscribe</c> of ours to this address has gone out and
    /// nothing has used the note up since.
    /// </summary>
    /// <remarks>
    /// The question <see cref="ProcessSubscriptionChangeAsync"/> asks before it
    /// makes an entry out of a <c>subscribed</c>: would that stanza be an
    /// answer to something of ours. A roster push arriving in the meantime does
    /// not clear it - while an entry stands there is nothing for the note to
    /// decide, and clearing it would cost the one thing it is good for outside
    /// this class, which is telling a reader that this contact was asked.
    /// </remarks>
    /// <param name="Jid">The address in question.</param>
    public Boolean HasOwnSubscriptionRequest(JID Jid)
    {
        lock (_lock)
            return _asked.Contains(Jid.Bare);
    }

    #endregion

    #region ProcessSubscriptionChangeAsync(From, Type, CancellationToken = default)

    /// <summary>
    /// RFC 6121, section 3: Applies a subscription change that arrives as a
    /// presence stanza.
    /// </summary>
    /// <remarks>
    /// The authoritative state comes from the server as a roster push; these
    /// stanzas are the notification about it. Evaluating them here anyway keeps
    /// the roster right even when the push fails to arrive - above all it keeps
    /// them away from <see cref="UpdatePresenceAsync"/>, where everything
    /// without <c>type='unavailable'</c> counts as present.
    ///
    /// That net had a hole in the one place where it was needed. It was
    /// written for a lost <em>second</em> push, the one carrying
    /// <c>subscription='to'</c>, and there it holds: the entry stands and the
    /// arriving <c>subscribed</c> brings it up to date. It does not hold when
    /// the <em>first</em> push goes missing - the one that brings the entry
    /// into being after a <c>subscribe</c> of our own. Then there was nothing
    /// to update, this method returned, and the <c>subscribed</c> behind it
    /// could repair nothing, while the server had long since granted the
    /// subscription. Measured in the conformance suite on 2026-10-24, where a
    /// handshake against ejabberd waited out its ten seconds for exactly that.
    ///
    /// So an entry is created here after all - but only for a
    /// <c>subscribed</c> that answers a request of our own, as recorded by
    /// <see cref="NoteOwnSubscriptionRequest"/>. That is the whole distinction,
    /// and it is the push path's own in another form: the answer to our own
    /// question is ours to believe, an unsolicited <c>subscribed</c> is not,
    /// and an entry made from one would be the roster forgery that
    /// <c>RosterPushSecurityTests</c> keeps out of the push.
    /// </remarks>
    /// <param name="From">Sender of the stanza.</param>
    /// <param name="Type">subscribed, unsubscribed or unsubscribe.</param>
    /// <param name="CancellationToken">An optional token to cancel this request.</param>
    public async Task ProcessSubscriptionChangeAsync(JID                From,
                                                     String             Type,
                                                     CancellationToken  CancellationToken   = default)
    {

        var          bareJid = From.Bare;
        RosterItem?  item;
        Boolean      isNew   = false;

        lock (_lock)
        {

            if (_items.TryGetValue(bareJid, out item))
            {

                item.Subscription = Type switch {
                    "subscribed"    => item.Subscription.GrantTo(),
                    "unsubscribed"  => item.Subscription.RevokeTo(),
                    "unsubscribe"   => item.Subscription.RevokeFrom(),
                    _               => item.Subscription
                };

                // Without a 'to' no presence arrives any more. Whatever was
                // last known would from now on grow arbitrarily old - the
                // contact therefore counts as offline instead of standing
                // forever in the last state seen.
                if (Type == "unsubscribed")
                {
                    item.Presence        = PresenceState.Offline;
                    item.PresenceStatus  = null;
                }

            }

            // The answer to a question of ours, with the push that should have
            // brought the entry into being still missing. Only 'subscribed'
            // gets this: the other two take something away, and an entry that
            // is not there has nothing left to lose.
            //
            // Remove both asks and consumes - the note is used up in the
            // asking, so a second 'subscribed' has nothing left to lean on.
            else if (Type == "subscribed" &&
                     _asked.Remove(bareJid))
            {

                item            = new RosterItem(bareJid) {
                                      Subscription = SubscriptionState.To
                                  };

                _items[bareJid] = item;
                isNew           = true;

            }

        }

        if (item is null)
        {

            _logger.LogDebug("A <presence type='{Type}'/> from {From}, who is " +
                             "not in the roster and whom we did not ask - " +
                             "nothing to apply",
                             Type, bareJid);

            return;

        }

        if (isNew)
            await OnItemAdded.  InvokeAllAsync(handler => handler(Timestamp.Now, this, item, CancellationToken), _logger);
        else
            await OnItemUpdated.InvokeAllAsync(handler => handler(Timestamp.Now, this, item, CancellationToken), _logger);

    }

    #endregion

    #region RemoveItemAsync(Jid, CancellationToken = default)

    public async Task RemoveItemAsync(JID                Jid,
                                      CancellationToken  CancellationToken   = default)
    {

        var      bareJid = Jid.Bare;
        Boolean  removed;

        lock (_lock)
        {

            removed = _items.Remove(bareJid);

            // Together with the entry, so that a <presence type='subscribed'/>
            // arriving after we have thrown the contact out brings nobody back.
            _asked.Remove(bareJid);

        }

        if (removed)
            await OnItemRemoved.InvokeAllAsync(handler => handler(Timestamp.Now, this, bareJid, CancellationToken), _logger);

    }

    #endregion

    #region RaiseSubscriptionRequestAsync(From, Status, CancellationToken = default)

    public Task RaiseSubscriptionRequestAsync(JID                From,
                                              String             Status,
                                              CancellationToken  CancellationToken   = default)

        => OnSubscriptionRequest.InvokeAllAsync(handler => handler(Timestamp.Now, this, From, Status, CancellationToken), _logger);

    #endregion

    #region UpdatePresenceAsync(From, Type, Show, Status, CancellationToken = default)

    public async Task UpdatePresenceAsync(JID                From,
                                          String             Type,
                                          String?            Show,
                                          String?            Status,
                                          CancellationToken  CancellationToken   = default)
    {

        var          bareJid = From.Bare;
        RosterItem?  item;

        lock (_lock)
        {

            if (!_items.TryGetValue(bareJid, out item))
                return;

            if (Type == "unavailable")
            {
                item.Presence        = PresenceState.Offline;
                item.PresenceStatus  = null;
            }
            else
            {

                item.Presence = Show switch {
                    "away"  => PresenceState.Away,
                    "chat"  => PresenceState.Chat,
                    "dnd"   => PresenceState.Dnd,
                    "xa"    => PresenceState.Xa,
                    _       => PresenceState.Available
                };

                item.PresenceStatus = Status;

            }

            item.LastSeen = DateTime.UtcNow;

        }

        await OnItemUpdated.InvokeAllAsync(handler => handler(Timestamp.Now, this, item, CancellationToken), _logger);

    }

    #endregion


    #region GetOnlineContacts()

    public IEnumerable<RosterItem> GetOnlineContacts()
    {

        lock (_lock)
            return _items.Values.
                       Where  (item => item.Presence != PresenceState.Offline).
                       OrderBy(item => item.DisplayName).
                       ToList();

    }

    #endregion

    #region GetByGroup(Group)

    public IEnumerable<RosterItem> GetByGroup(String Group)
    {

        lock (_lock)
            return _items.Values.
                       Where  (item => item.Groups.Contains(Group, StringComparer.OrdinalIgnoreCase)).
                       OrderBy(item => item.DisplayName).
                       ToList();

    }

    #endregion

    #region GetGroups()

    public IEnumerable<String> GetGroups()
    {

        lock (_lock)
            return _items.Values.
                       SelectMany(item => item.Groups).
                       Distinct  (StringComparer.OrdinalIgnoreCase).
                       OrderBy   (group => group).
                       ToList();

    }

    #endregion

}
