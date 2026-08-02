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
/// Roster manager with subscription handling
/// </summary>
public sealed class Roster
{
    private readonly Dictionary<string, RosterItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public string? Version { get; set; }

    public event Action<RosterItem>? OnItemAdded;
    public event Action<RosterItem>? OnItemUpdated;
    public event Action<string>? OnItemRemoved;
    public event Action<string, string>? OnSubscriptionRequest;

    public IReadOnlyCollection<RosterItem> Items
    {
        get { lock (_lock) return _items.Values.ToList(); }
    }

    public RosterItem? GetItem(string jid)
    {
        var bareJid = JidUtilities.Bare(jid);
        lock (_lock)
        {
            return _items.TryGetValue(bareJid, out var item) ? item : null;
        }
    }

    public void ProcessRosterItem(RosterItem newItem)
    {
        var bareJid = JidUtilities.Bare(newItem.Jid);

        lock (_lock)
        {
            if (_items.TryGetValue(bareJid, out var existing))
            {
                existing.Name = newItem.Name;
                existing.Subscription = newItem.Subscription;
                existing.Groups.Clear();
                existing.Groups.AddRange(newItem.Groups);
                OnItemUpdated?.Invoke(existing);
            }
            else
            {
                _items[bareJid] = newItem;
                OnItemAdded?.Invoke(newItem);
            }
        }
    }

    /// <summary>
    /// RFC 6121, section 2.1.4: Takes the result of a roster request as the
    /// complete roster.
    /// </summary>
    /// <remarks>
    /// The difference to <see cref="ProcessRosterItem"/> is the removal. A
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
    public void ReplaceAll(IEnumerable<RosterItem> items)
    {

        var fresh    = items.ToList();
        var kept     = new HashSet<string>(fresh.Select(i => JidUtilities.Bare(i.Jid)),
                                           StringComparer.OrdinalIgnoreCase);

        List<string> dropped;

        lock (_lock)
            dropped = _items.Keys.Where(k => !kept.Contains(k)).ToList();

        // Outside the lock: both calls take it themselves, and the events are
        // not meant to run under it.
        foreach (var item in fresh)
            ProcessRosterItem(item);

        foreach (var jid in dropped)
            RemoveItem(jid);

    }

    /// <summary>
    /// RFC 6121, section 3: Applies a subscription change that arrives as a
    /// presence stanza.
    /// </summary>
    /// <remarks>
    /// The authoritative state comes from the server as a roster push; these
    /// stanzas are the notification about it. Evaluating them here anyway keeps
    /// the roster right even when the push fails to arrive - above all it keeps
    /// them away from <see cref="UpdatePresence"/>, where everything without
    /// <c>type='unavailable'</c> counts as present.
    ///
    /// An unknown contact is deliberately not created: entries come into being
    /// through the roster push, not through a presence.
    /// </remarks>
    /// <param name="from">Sender of the stanza.</param>
    /// <param name="type">subscribed, unsubscribed or unsubscribe.</param>
    public void ProcessSubscriptionChange(string from, string type)
    {
        var bareJid = JidUtilities.Bare(from);

        lock (_lock)
        {
            if (!_items.TryGetValue(bareJid, out var item))
                return;

            item.Subscription = type switch
            {
                "subscribed"    => item.Subscription.GrantTo(),
                "unsubscribed"  => item.Subscription.RevokeTo(),
                "unsubscribe"   => item.Subscription.RevokeFrom(),
                _               => item.Subscription
            };

            // Without a 'to' no presence arrives any more. Whatever was last
            // known would from now on grow arbitrarily old - the contact
            // therefore counts as offline instead of standing forever in the
            // last state seen.
            if (type == "unsubscribed")
            {
                item.Presence        = PresenceState.Offline;
                item.PresenceStatus  = null;
            }

            OnItemUpdated?.Invoke(item);
        }
    }

    public void RemoveItem(string jid)
    {
        var bareJid = JidUtilities.Bare(jid);
        lock (_lock)
        {
            if (_items.Remove(bareJid))
            {
                OnItemRemoved?.Invoke(bareJid);
            }
        }
    }

    public void RaiseSubscriptionRequest(string from, string status)
    {
        OnSubscriptionRequest?.Invoke(from, status);
    }

    public void UpdatePresence(string from, string type, string? show, string? status)
    {
        var bareJid = JidUtilities.Bare(from);

        lock (_lock)
        {
            if (!_items.TryGetValue(bareJid, out var item))
            {
                return;
            }

            if (type == "unavailable")
            {
                item.Presence = PresenceState.Offline;
                item.PresenceStatus = null;
            }
            else
            {
                item.Presence = show switch
                {
                    "away" => PresenceState.Away,
                    "chat" => PresenceState.Chat,
                    "dnd" => PresenceState.Dnd,
                    "xa" => PresenceState.Xa,
                    _ => PresenceState.Available
                };
                item.PresenceStatus = status;
            }

            item.LastSeen = DateTime.UtcNow;
            OnItemUpdated?.Invoke(item);
        }
    }

    public IEnumerable<RosterItem> GetOnlineContacts()
    {
        lock (_lock)
        {
            return _items.Values
                .Where(i => i.Presence != PresenceState.Offline)
                .OrderBy(i => i.DisplayName)
                .ToList();
        }
    }

    public IEnumerable<RosterItem> GetByGroup(string group)
    {
        lock (_lock)
        {
            return _items.Values
                .Where(i => i.Groups.Contains(group, StringComparer.OrdinalIgnoreCase))
                .OrderBy(i => i.DisplayName)
                .ToList();
        }
    }

    public IEnumerable<string> GetGroups()
    {
        lock (_lock)
        {
            return _items.Values
                .SelectMany(i => i.Groups)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g)
                .ToList();
        }
    }
}
