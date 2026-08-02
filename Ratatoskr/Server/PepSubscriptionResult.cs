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
    /// Whether the subscription meant could be found for a request.
    /// </summary>
    /// <remarks>
    /// The same question arises when unsubscribing (XEP-0060, section 6.2) and
    /// when configuring (section 6.3), and it is answered the same way both
    /// times. <b>The error belonging to it is not:</b> if the identifier is
    /// missing where there are several, the XEP demands a
    /// <c>&lt;bad-request/&gt;</c> when unsubscribing and a
    /// <c>&lt;not-acceptable/&gt;</c> when configuring. That is not
    /// arbitrariness - there the request is incomplete, here it is in order and
    /// merely cannot be answered in this situation.
    ///
    /// That is why the finding stands here and not the answer. Whoever let both
    /// places build the same error message would not have read one of the two.
    /// </remarks>
    public enum PepSubscriptionResult
    {

        /// <summary>
        /// Found - and, with a change, carried out as well.
        /// </summary>
        Ok,

        /// <summary>
        /// This JID has no subscription to this node -
        /// <c>&lt;unexpected-request/&gt;</c> with
        /// <c>&lt;not-subscribed/&gt;</c>.
        /// </summary>
        NotSubscribed,

        /// <summary>
        /// The <c>subid</c> sent along belongs to none of its subscriptions -
        /// <c>&lt;not-acceptable/&gt;</c> with <c>&lt;invalid-subid/&gt;</c>.
        /// </summary>
        WrongSubId,

        /// <summary>
        /// There are several, and no identifier says which one is meant.
        /// </summary>
        /// <remarks>
        /// Picking one would be the comfortable answer and the wrong one: the
        /// service might hit the other one and confirm to the sender that it
        /// had been theirs.
        /// </remarks>
        SubIdRequired

    }

}
