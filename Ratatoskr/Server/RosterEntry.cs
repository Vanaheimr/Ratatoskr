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
    /// A roster entry in the test server.
    /// </summary>
    /// <param name="Jid">Bare JID of the contact.</param>
    /// <param name="Name">Display name or null.</param>
    /// <param name="Subscription">none, to, from or both.</param>
    /// <param name="Ask">
    /// <c>subscribe</c> as long as a request posed is still unanswered,
    /// otherwise null (RFC 6121, section 3.1.2). The state does not hang on
    /// <paramref name="Subscription"/>: an open request leaves the subscription
    /// standing at <c>none</c>.
    /// </param>
    /// <param name="Approved">
    /// The contact is admitted in advance (RFC 6121, section 3.4): if they pose
    /// a request in future, the server answers it itself.
    /// </param>
    /// <remarks>
    /// The opposite direction of <paramref name="Ask"/> - that one <i>was
    /// asked</i> - deliberately does not stand here. RFC 6121 knows the state
    /// ("None + Pending In"), but section 3.1.3 forbids in the same breath a
    /// roster entry for an applicant who has not been agreed to yet. The open
    /// request therefore lies beside the roster, in
    /// <see cref="XMPPAccount.PendingSubscriptionRequests"/> - and there
    /// completely, together with its extended content, instead of as a mere
    /// yes/no.
    /// </remarks>
    /// <param name="Groups">
    /// The groups the owner has put this contact into (RFC 6121,
    /// section 2.1.2.4).
    /// </param>
    /// <remarks>
    /// <b>The groups were missing here until D91</b>, and the comment in the
    /// roster handling claimed all along that a set changes "the name and the
    /// groups". They were never read: a client sent a group, got a
    /// <c>result</c> and the same entry back in the push without them - with
    /// which they disappeared at its end too, because a push replaces the
    /// groups of an entry completely.
    /// </remarks>
    public sealed record RosterEntry(String                 Jid,
                                     String?                Name          = null,
                                     String                 Subscription  = "both",
                                     String?                Ask           = null,
                                     Boolean                Approved      = false,
                                     IReadOnlyList<String>? Groups        = null)
    {

        /// <summary>
        /// The groups, never null - "no group" is an empty list and not
        /// something missing.
        /// </summary>
        public IReadOnlyList<String> Groups { get; init; } = Groups ?? [];

    }

}
