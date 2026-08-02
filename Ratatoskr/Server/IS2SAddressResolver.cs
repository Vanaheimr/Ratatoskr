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
    /// Works out where a foreign domain offers its S2S service (RFC 6120,
    /// section 3.2).
    /// </summary>
    /// <remarks>
    /// An interface of its own and not the DNS client directly, for two
    /// reasons. First, the hand-maintained peer list is an equally valid answer
    /// to the same question - in the test setup even the only usable one.
    /// Second, on this answer hangs a network access, and a test that asks real
    /// DNS checks the world instead of the code.
    ///
    /// <b>The answer says where the connection goes - not with whom.</b>
    /// Without DNSSEC the information is not authenticated. Whoever can forge
    /// it redirects the connection; that is why the identity stays bound to
    /// what the peer presents - a certificate or dialback - and the check is
    /// always against the <i>domain sought</i>, never against the host name
    /// delivered.
    /// </remarks>
    public interface IS2SAddressResolver
    {

        /// <summary>
        /// The targets for a domain, in the order in which they are to be
        /// tried.
        /// </summary>
        /// <returns>
        /// Empty when the domain is not reachable or explicitly does not offer
        /// the service.
        /// </returns>
        Task<IReadOnlyList<SrvTarget>> ResolveAsync(String             domain,
                                                    CancellationToken  cancellationToken = default);

    }

}
