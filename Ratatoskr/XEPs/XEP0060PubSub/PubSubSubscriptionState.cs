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
/// The state of a subscription (XEP-0060, section 12.19).
/// </summary>
/// <remarks>
/// <b>Four states and not two.</b> "Subscribed or not" would be the obvious
/// shortening and exactly the one a client founders on: a
/// <see cref="Pending"/> looks like a consent - the service has accepted the
/// request - but is none. Whoever throws the two together takes themselves for
/// subscribed while somebody is still deciding about it, and wonders about the
/// notifications that do not come.
/// </remarks>
public enum PubSubSubscriptionState
{

    /// <summary>
    /// No subscription.
    /// </summary>
    None,

    /// <summary>
    /// Applied for but not yet approved - the node demands the consent of its
    /// owner.
    /// </summary>
    Pending,

    /// <summary>
    /// Accepted, but the service still expects the configuration of the
    /// subscription before it sends anything.
    /// </summary>
    Unconfigured,

    /// <summary>
    /// Subscribed - from here on notifications come.
    /// </summary>
    Subscribed

}
