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
/// XEP-0060: An event delivered by a PubSub service - or, under XEP-0163, by
/// an account about a node of its own.
/// </summary>
public sealed class PubSubEvent
{
    public string NodeId { get; }
    public PubSubEventType Type { get; }
    public List<PubSubItem> Items { get; } = [];
    public List<string> RetractedIds { get; } = [];

    /// <summary>
    /// Who reported it: the service, or under PEP the account whose node it is.
    /// </summary>
    /// <remarks>
    /// <b>A node name alone does not say whose node it is.</b> A service keeps
    /// its node names to itself, so for one service the name was the whole
    /// address; under PEP every account is its own service (XEP-0163, section
    /// 4.3) and <c>urn:xmpp:avatar:metadata</c> is the name of a node that
    /// every one of them has. Bob's picture and Carol's arrive under the same
    /// name and differ in this and in nothing else.
    ///
    /// <b>So a reader attributes by this and not by the node.</b> It is the
    /// address as it was received, resource and all, because dropping part of
    /// a sender is not this class's decision; whoever wants the account takes
    /// <c>From.Bare</c>. What stands here has been through
    /// <c>PubSubManager</c>'s check on who may report this node at all, which
    /// is a check on the sender and therefore not a licence to ignore it.
    /// </remarks>
    public JID From { get; }

    /// <summary>
    /// The subscription this notification belongs to - from the SHIM header
    /// <c>SubID</c> (XEP-0060, section 12.20), or null.
    /// </summary>
    /// <remarks>
    /// Null does not mean "unknown" but usually: there is only one, and then
    /// the service does not have to send the identifier along. With several it
    /// has to - otherwise two deliveries of the same thing could not be told
    /// apart.
    /// </remarks>
    public string? SubId { get; }

    public PubSubEvent(string nodeId, PubSubEventType type, JID from, string? subId = null)
    {
        NodeId = nodeId;
        Type = type;
        From = from;
        SubId = subId;
    }
}
