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
    /// What happened to a stanza that came from another server.
    /// </summary>
    /// <remarks>
    /// A mere "refused" was no longer enough once there is a stream: the
    /// refusals differ in weight. A wrong <c>from</c> is an attack on the
    /// addressing and ends the whole stream per RFC 6120, section 8.1.1.1; a
    /// recipient on a third domain, by contrast, is merely a stanza that has no
    /// business being here.
    /// </remarks>
    public enum RemoteStanzaResult
    {

        /// <summary>
        /// Accepted and delivered locally.
        /// </summary>
        Accepted,

        /// <summary>
        /// <c>from</c> or <c>to</c> is missing - without both it cannot be delivered.
        /// </summary>
        MissingAddress,

        /// <summary>
        /// The <c>from</c> is not a JID per RFC 7622.
        /// </summary>
        /// <remarks>
        /// For the stream the same case as <see cref="ForeignSender"/>:
        /// RFC 6120, section 8.1.1.1 calls both an invalid <c>from</c> and lets
        /// the stream end with <c>&lt;invalid-from/&gt;</c>. It is a value of
        /// its own nonetheless, because the reason is a different one - here
        /// nobody speaks for a foreign domain, here there is no address there
        /// at all.
        /// </remarks>
        MalformedSender,

        /// <summary>
        /// The <c>to</c> is not a JID per RFC 7622.
        /// </summary>
        /// <remarks>
        /// Unlike with the sender this only costs the one stanza: it is a typo
        /// in an address and not a statement about who is speaking there. The
        /// sender gets <c>&lt;jid-malformed/&gt;</c> back.
        /// </remarks>
        MalformedRecipient,

        /// <summary>
        /// The peer speaks for a domain that does not belong to it.
        /// </summary>
        ForeignSender,

        /// <summary>
        /// The recipient does not lie on this domain - forwarding for third
        /// parties would be an open relay.
        /// </summary>
        ForeignRecipient,

        /// <summary>
        /// The routing is switched off (a test switch), so the stanza was not
        /// delivered.
        /// </summary>
        RoutingDisabled

    }

}
