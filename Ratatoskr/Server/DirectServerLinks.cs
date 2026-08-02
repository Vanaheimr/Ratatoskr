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
    /// Connects <see cref="XMPPServer"/> instances in the same process directly
    /// with one another, without a network in between.
    /// </summary>
    /// <remarks>
    /// <b>No substitute for a real S2S connection.</b> There is no stream, no
    /// TLS, no dialback and no authentication: the domain a peer may speak for
    /// is simply asserted here. For operation this is nothing.
    ///
    /// What it is good for: checking routing, addressing and delivery across a
    /// domain boundary without having committed to a transport beforehand. The
    /// sender check at the entrance of
    /// <see cref="XMPPServer.ReceiveFromRemoteAsync"/> is therefore armed all
    /// the same - it is exactly what a real transport builds on after the
    /// dialback.
    /// </remarks>
    public sealed class DirectServerLinks : IServerLinks
    {

        #region Data

        private readonly XMPPServer _localServer;
        private readonly Dictionary<String, XMPPServer> _peers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _lock = new();

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Creates the peer list for a server.
        /// </summary>
        public DirectServerLinks(XMPPServer localServer)
        {
            _localServer = localServer;
        }

        #endregion


        #region AddPeer(peer)

        /// <summary>
        /// Makes a further server reachable - in this direction.
        /// </summary>
        public void AddPeer(XMPPServer peer)
        {
            lock (_lock)
                _peers[peer.Domain] = peer;
        }

        #endregion

        #region (static) Connect(a, b)

        /// <summary>
        /// Connects two servers in both directions and hangs the links onto
        /// their <see cref="XMPPServer.ServerLinks"/>.
        /// </summary>
        /// <remarks>
        /// Both directions, because a one-sided connection would be a trap: the
        /// message would arrive, the answer would not, and the fault would look
        /// like a delivery problem instead of like half a wiring job.
        /// </remarks>
        public static void Connect(XMPPServer a, XMPPServer b)
        {

            if (String.Equals(a.Domain, b.Domain, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                          $"Both servers serve '{a.Domain}' - a federation with itself amounts to nothing.",
                          nameof(b));

            LinksOf(a).AddPeer(b);
            LinksOf(b).AddPeer(a);

        }

        /// <summary>
        /// The peer list of a server, created if necessary.
        /// </summary>
        private static DirectServerLinks LinksOf(XMPPServer server)
        {

            if (server.ServerLinks is DirectServerLinks existing)
                return existing;

            var links = new DirectServerLinks(server);
            server.ServerLinks = links;

            return links;

        }

        #endregion

        #region DeliverAsync(remoteDomain, stanza, cancellationToken)

        public Task<Boolean> DeliverAsync(String             remoteDomain,
                                          String             stanza,
                                          CancellationToken  cancellationToken = default)
        {

            XMPPServer? peer;

            lock (_lock)
                _peers.TryGetValue(remoteDomain, out peer);

            return peer is null
                       ? Task.FromResult(false)
                       : peer.ReceiveFromRemoteAsync(_localServer.Domain, stanza);

        }

        #endregion

    }

}
