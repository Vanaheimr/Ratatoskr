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
/// XEP-0060: An event delivered by the PubSub service.
/// </summary>
public sealed class PubSubEvent
{
    public string NodeId { get; }
    public PubSubEventType Type { get; }
    public List<PubSubItem> Items { get; } = [];
    public List<string> RetractedIds { get; } = [];

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

    public PubSubEvent(string nodeId, PubSubEventType type, string? subId = null)
    {
        NodeId = nodeId;
        Type = type;
        SubId = subId;
    }
}
