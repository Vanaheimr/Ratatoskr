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

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    /// <summary>
    /// A subscription on a PEP node (XEP-0060, section 6.1).
    /// </summary>
    /// <param name="Jid">The bare JID of the subscriber.</param>
    /// <param name="SubId">
    /// The identifier this server has handed out. It distinguishes two
    /// subscriptions of the same JID to the same node - and, since the
    /// configuration per subscription, also names which setting is meant.
    /// </param>
    /// <param name="Options">The settings of this subscription.</param>
    /// <param name="State">
    /// Promised or merely applied for (XEP-0060, section 12.19).
    /// </param>
    /// <remarks>
    /// <b>The state stood only on paper until D93.</b> Without
    /// <c>authorize</c> every subscription entered was a promised one, and the
    /// server wrote <c>subscription='subscribed'</c> down as a fixed string -
    /// right, as long as there was nothing else. With the approval procedure
    /// there is something else, and a subscription that cannot be told apart
    /// from an applied-for one is exactly the promise without cover this series
    /// writes against.
    /// </remarks>
    public sealed record PepSubscription(String                     Jid,
                                         String                     SubId,
                                         PubSubSubscriptionOptions  Options,
                                         PubSubSubscriptionState    State = PubSubSubscriptionState.Subscribed);

}
