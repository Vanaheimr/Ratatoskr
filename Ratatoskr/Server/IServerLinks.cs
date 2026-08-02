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
    /// The way to other servers - the place where a server-to-server transport
    /// is put in (RFC 6120, section 10.4).
    /// </summary>
    /// <remarks>
    /// Deliberately only this one method. Whether behind it lies an existing
    /// connection, whether one is established first, whether the peer has
    /// identified itself by dialback (XEP-0220) or SASL-EXTERNAL - none of that
    /// is any of the routing part's business. It wants to know whether the
    /// stanza is out.
    ///
    /// The real transport is still missing, and deliberately in this form: the
    /// method asks for a <b>domain</b> and not for a connection. Which
    /// transport reaches a domain is decided by the implementation - it can use
    /// TCP for one peer and WebSocket for another without the routing learning
    /// of it.
    ///
    /// Both is the goal. RFC 6120 provides for S2S over TCP on port 5269 with
    /// <c>jabber:server</c> streams - only with that is federation with
    /// ejabberd or Prosody possible. RFC 7395 is cut for browser-based clients
    /// and says nothing about S2S; it does not forbid the transport there
    /// either, though, and a WebSocket link between two instances of this
    /// server is considerably quicker to have.
    ///
    /// The expensive part - dialback resp. SASL-EXTERNAL, the sender check,
    /// addressing, the life cycle - is common to both. That is why "both" is
    /// not twice the work.
    /// </remarks>
    public interface IServerLinks
    {

        /// <summary>
        /// Delivers a stanza to a foreign domain.
        /// </summary>
        /// <param name="remoteDomain">The domain of the recipient.</param>
        /// <param name="stanza">The complete stanza, already stamped with <c>from</c>.</param>
        /// <returns>
        /// false when the domain was not reachable. The caller then produces the
        /// stanza error - answering here would mean repeating the error path at
        /// every implementation.
        /// </returns>
        Task<Boolean> DeliverAsync(String             remoteDomain,
                                   String             stanza,
                                   CancellationToken  cancellationToken = default);

    }

}
