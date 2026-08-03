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
/// XEP-0060: The kind of a PubSub event.
/// </summary>
public enum PubSubEventType
{
    Items,      // new/updated items
    Retract,    // items deleted
    Purge,      // node emptied
    Delete,     // node deleted
    Configuration, // node config changed

    /// <summary>
    /// A subscription was ended without this client having asked for it
    /// (XEP-0060, section 8.8.4).
    /// </summary>
    /// <remarks>
    /// <b>Ended and not "changed".</b> The other direction - a consent by
    /// notification - this client does not enter: a consent comes in answer to
    /// a request. Whoever accepted it unasked would let themselves be signed up
    /// by a service, and that is exactly what the server of this project
    /// refuses on the other side.
    /// </remarks>
    SubscriptionEnded,

    /// <summary>
    /// A subscription applied for was granted (XEP-0060, section 8.6).
    /// </summary>
    /// <remarks>
    /// <b>The answer to a question of one's own, and only that.</b> It comes
    /// later than the question - between them lies a human being who answers it
    /// - and that is why it comes as a notification and not as an answer to the
    /// IQ. Whoever has no pending application for it does not get this event: an
    /// unrequested consent would remain a signing-up by somebody else.
    /// </remarks>
    SubscriptionApproved
}
