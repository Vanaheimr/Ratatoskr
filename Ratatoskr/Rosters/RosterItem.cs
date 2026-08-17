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
/// Represents a contact in the roster
/// </summary>
public sealed class RosterItem
{
    public JID Jid { get; }
    public string? Name { get; set; }
    public SubscriptionState Subscription { get; set; } = SubscriptionState.None;
    public List<string> Groups { get; } = [];

    public PresenceState Presence { get; set; } = PresenceState.Offline;
    public string? PresenceStatus { get; set; }
    public DateTime LastSeen { get; set; }

    public RosterItem(JID jid)
    {
        Jid = jid;
    }

    /// <summary>
    /// A roster entry for the given address.
    /// </summary>
    /// <exception cref="JidFormatException">If it is not an address.</exception>
    public RosterItem(String jid)

        : this(JID.Parse(jid))

    { }

    public string DisplayName => Name ?? Jid.ToString();

    /// <summary>
    /// The account, without the device.
    /// </summary>
    /// <remarks>
    /// This used to cut the string at the first slash by hand, and a roster
    /// entry never carries a resourcepart, so the cut had nothing to do. Which
    /// is why nobody noticed that the constructor beside it prepared nothing
    /// either: <c>ToLowerInvariant</c> over the whole address is not what
    /// RFC 7622 asks for, and it flattens a resourcepart that is meant to keep
    /// its spelling.
    /// </remarks>
    public JID BareJid => Jid.Bare;

    public override string ToString()
    {
        var sub = Subscription switch
        {
            SubscriptionState.Both => "↔",
            SubscriptionState.To => "→",
            SubscriptionState.From => "←",
            _ => "○"
        };

        var pres = Presence switch
        {
            PresenceState.Available => "●",
            PresenceState.Away => "◐",
            PresenceState.Dnd => "⊘",
            PresenceState.Xa => "◑",
            PresenceState.Chat => "◉",
            _ => "○"
        };

        var groups = Groups.Count > 0 ? $" [{string.Join(", ", Groups)}]" : "";
        return $"{pres} {sub} {DisplayName}{groups}";
    }
}
