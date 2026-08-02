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

#region Usings

using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

// See XMPPServer.cs: Hermod's IPAddress hides the one from System.Net, the
// alias clears that up.
using IPAddress = System.Net.IPAddress;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    /// <summary>
    /// Connects <see cref="XMPPServer"/> instances with one another over real
    /// WebSocket S2S - the counterpart to <see cref="DirectServerLinks"/>, only
    /// with a network in between.
    /// </summary>
    /// <remarks>
    /// The namespace of the framing is the same as for clients (RFC 7395); the
    /// distinction is made through the WebSocket subprotocol. RFC 7395 is cut
    /// for browser-based clients and says nothing about S2S - "xmpp-server" is
    /// therefore not a standard but this implementation. That is deliberate per
    /// the work plan: WebSocket S2S shall only connect instances of this server
    /// with one another, not speak to ejabberd or Prosody. Whoever needs that
    /// takes the TCP framing.
    ///
    /// What this class delivers: establishing the connection, TLS, splitting
    /// the WebSocket frames into <see cref="S2SStream"/> frames, a connection
    /// cache per domain. What it does <b>not</b> deliver - not yet -: dialback.
    /// The domain of the peer is entered by hand through
    /// <see cref="AddPeer"/>, as with <see cref="DirectServerLinks"/>; the
    /// sender check in <see cref="XMPPServer.AcceptFromRemoteAsync"/> is armed
    /// all the same.
    /// </remarks>
    public sealed class WebSocketServerLinks : IServerLinks, IAsyncDisposable
    {

        #region Data

        /// <summary>RFC 7395, section 3.1 - the same framing as for clients.</summary>
        private const String FramingNamespace = S2SStream.FramingNamespace;

        /// <summary>The WebSocket subprotocol by which S2S differs from the client access.</summary>
        internal const String S2SSubprotocol = "xmpp-server";

        private readonly XMPPServer                          _localServer;
        private readonly S2SWebSocketListener                _listener;
        private readonly Dictionary<String, PeerConfig>      _peers      = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<String, OutboundSlot>    _outbound   = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock                                _lock       = new();

        private Int32 _bidiDeliveries;

        private sealed record PeerConfig(String Uri, RemoteCertificateValidationCallback? Validator);

        private sealed record OutboundLink(ClientWebSocket Socket, S2SStream Stream);

        /// <summary>
        /// A slot in the connection cache. Not the <c>Task</c> itself, because
        /// clearing up has to be possible <b>while</b> the setup is still
        /// running.
        /// </summary>
        /// <remarks>
        /// Previously the task stood here, and it was only removed when it had
        /// already completed successfully. If the stream dies while still being
        /// set up, though - which became the normal case with dialback, because
        /// the setup now takes several round trips - the entry stayed there
        /// forever and every further delivery to this domain got the dead
        /// connection back. Through the identity of the slot it can be cleared
        /// up safely, without accidentally hitting an entry created anew in the
        /// meantime.
        /// </remarks>
        private sealed class OutboundSlot
        {
            public Task<OutboundLink?>? Connecting;
        }

        #endregion

        #region Properties

        /// <summary>The port on which incoming S2S connections are expected.</summary>
        public Int32 Port { get; }

        /// <summary>
        /// The dialback secret of this server (XEP-0220). It comes into being
        /// when the object is created and never leaves the process.
        /// </summary>
        public String DialbackSecret { get; } = DialbackKey.NewSecret();

        /// <summary>The certificate the incoming branch speaks TLS with, or null.</summary>
        public X509Certificate2? Certificate { get; }

        /// <summary>Our own S2S address, meant for <see cref="AddPeer"/> on the other side.</summary>
        public String Uri => $"{(Certificate is not null ? "wss" : "ws")}://localhost:{Port}/s2s/";

        /// <summary>
        /// The number of S2S connections ever received - independently of the
        /// client connections of the server, which
        /// <see cref="XMPPServer.ConnectionCount"/> counts.
        /// </summary>
        public Int32 InboundConnectionCount => _listener.ConnectionCounter;

        /// <summary>
        /// XEP-0288: offer the return direction on <b>incoming</b> connections.
        /// </summary>
        /// <remarks>
        /// The same extension as with <see cref="TcpServerLinks"/> and for the
        /// same reason - the protocol layer underneath is the same anyway. Here
        /// it weighs less in operation, because at both ends of this WebSocket
        /// transport hang instances of this server that have entered one
        /// another.
        ///
        /// Kept apart from <see cref="RequestBidirectionalStreams"/>, because
        /// they are two different things: here we tell a peer that dials us
        /// that it may answer us over its own connection; there we ask the same
        /// of a peer that we dial. Wired together they were not merely
        /// imprecise - it was thereby impossible to observe our announcement at
        /// all: as long as our outgoing connection uses the return direction,
        /// the peer does not dial us in the first place.
        /// </remarks>
        public Boolean OfferBidirectionalStreams { get; init; }

        /// <summary>
        /// XEP-0288: ask for the return direction on <b>outgoing</b>
        /// connections.
        /// </summary>
        /// <remarks>
        /// Sensible when the peer cannot reach us. See
        /// <see cref="OfferBidirectionalStreams"/> for the opposite direction.
        /// </remarks>
        public Boolean RequestBidirectionalStreams { get; init; }

        /// <summary>
        /// How many stanzas went over the return direction of an incoming
        /// stream instead of over a connection of our own.
        /// </summary>
        public Int32 BidirectionalDeliveryCount => Volatile.Read(ref _bidiDeliveries);

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Creates the incoming branch and starts it right away - without a
        /// reachable entrance the federation would only be half of one anyway.
        /// </summary>
        /// <param name="localServer">The server whose S2S counterpart this is.</param>
        /// <param name="port">A fixed port or 0 for a free one.</param>
        public WebSocketServerLinks(XMPPServer localServer, Int32 port = 0)
        {

            _localServer  = localServer;
            Port          = port > 0 ? port : FreeTcpPort();
            Certificate   = localServer.Certificate;

            _listener = new S2SWebSocketListener(this, IPPort.Parse(Port), Certificate);
            _listener.Start().GetAwaiter().GetResult();

            localServer.ServerLinks = this;

        }

        #endregion


        #region AddPeer(domain, uri, validator)

        /// <summary>
        /// Makes a foreign domain reachable through its S2S address.
        /// </summary>
        /// <param name="domain">The domain of the peer.</param>
        /// <param name="uri">Its S2S WebSocket address.</param>
        /// <param name="validator">
        /// The certificate validation for the outgoing connection; null leaves
        /// it to the operating system.
        /// </param>
        public void AddPeer(String                                domain,
                            String                                uri,
                            RemoteCertificateValidationCallback?  validator = null)
        {
            lock (_lock)
                _peers[domain] = new PeerConfig(uri, validator);
        }

        #endregion

        #region (static) Connect(a, b)

        /// <summary>
        /// Connects two servers in both directions - each receives the S2S
        /// address and the pinned certificate of the other.
        /// </summary>
        /// <remarks>
        /// For a server that does not have a <see cref="WebSocketServerLinks"/>
        /// yet, it silently creates one - the same convenience
        /// <see cref="DirectServerLinks.Connect"/> already offers.
        /// </remarks>
        public static void Connect(XMPPServer a, XMPPServer b)
        {

            if (String.Equals(a.Domain, b.Domain, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                          $"Both servers serve '{a.Domain}' - a federation with itself amounts to nothing.",
                          nameof(b));

            var linksA = LinksOf(a);
            var linksB = LinksOf(b);

            linksA.AddPeer(b.Domain, linksB.Uri, b.IsOwnCertificate);
            linksB.AddPeer(a.Domain, linksA.Uri, a.IsOwnCertificate);

        }

        private static WebSocketServerLinks LinksOf(XMPPServer server)

            => server.ServerLinks as WebSocketServerLinks
               ?? new WebSocketServerLinks(server);

        #endregion

        #region DeliverAsync(remoteDomain, stanza, cancellationToken)

        /// <remarks>
        /// Unlike with <see cref="DirectServerLinks"/>, true is no assurance
        /// here that the peer has accepted the stanza - S2S knows no ack per
        /// stanza. It only means: the stream stood and the frame was written. A
        /// sender check that fails afterwards ends the stream (see
        /// <see cref="S2SStream"/>) but no longer reports back to this call -
        /// by that time it has long since completed.
        /// </remarks>
        public async Task<Boolean> DeliverAsync(String             remoteDomain,
                                                String             stanza,
                                                CancellationToken  cancellationToken = default)
        {

            // XEP-0288: if an incoming connection of this domain carries the
            // return direction, the stanza goes out there - taking precedence
            // over dialling, because that is precisely why the peer asked for
            // the return direction.
            // No switch in front of it - see TcpServerLinks: BidiEnabled
            // already presupposes both.
            if (await S2SStream.TryDeliverOverBidiAsync(_listener.InboundStreams(), remoteDomain,
                                                        stanza, cancellationToken))
            {
                Interlocked.Increment(ref _bidiDeliveries);
                return true;
            }

            var link = await GetOrCreateOutboundAsync(remoteDomain, cancellationToken);

            return link is not null &&
                   await link.Stream.SendStanzaAsync(stanza, cancellationToken);

        }

        #endregion


        #region (internal) VerifyDialbackKeyAsync(senderDomain, streamId, key)

        /// <summary>
        /// XEP-0220, steps 2 and 3: asks the authoritative server of the sender
        /// domain whether it issued this key.
        /// </summary>
        /// <remarks>
        /// <b>Here sits the whole value of dialback.</b> The address that is
        /// asked comes from the peer list of this server - that is, from the
        /// operator's configuration - and <b>not</b> from whoever is currently
        /// trying to identify itself. Whoever falsely claims to be a domain is
        /// therefore never asked themselves: the question goes to the real
        /// server of that domain, which does not recognise the key and refuses
        /// it.
        ///
        /// That is at the same time the difference to the dialback of the XEP:
        /// there a DNS resolution (SRV on the sender domain) replaces this
        /// list. DNS is still missing here - the list is the substitute, and
        /// for the purpose a stricter one, because it is signed by the hand
        /// that maintained it rather than by an unauthenticated protocol. What
        /// it does not achieve: filling itself. An unknown domain cannot be
        /// checked and therefore cannot be accepted.
        ///
        /// The connection for it is its own and short-lived. It must not be the
        /// cached stanza connection - that one is itself just trying to
        /// identify itself, and letting both wait on each other would be a
        /// deadlock.
        /// </remarks>
        internal async Task<Boolean> VerifyDialbackKeyAsync(String senderDomain,
                                                            String streamId,
                                                            String key)
        {

            PeerConfig? peer;

            lock (_lock)
                _peers.TryGetValue(senderDomain, out peer);

            // No address on record - then there is nobody one could ask, and
            // believing is not checking.
            if (peer is null)
                return false;

            ClientWebSocket? socket = null;

            try
            {

                socket = new ClientWebSocket();
                socket.Options.AddSubProtocol(S2SSubprotocol);

                if (peer.Validator is not null)
                    socket.Options.RemoteCertificateValidationCallback = peer.Validator;

                using var cts = new CancellationTokenSource(VerificationTimeout);

                await socket.ConnectAsync(new Uri(peer.Uri), cts.Token);

                var stream = S2SStream.InitiateVerification(
                                 _localServer.Domain,
                                 senderDomain,
                                 (frame, ct) => SendFrameAsync(socket, frame, ct));

                var pumping = PumpVerificationFramesAsync(socket, stream);

                await stream.OpenAsync(cts.Token);

                if (!await stream.WaitUntilOpenAsync(VerificationTimeout, cts.Token))
                    return false;

                return await stream.RequestVerificationAsync(
                           targetDomain:  _localServer.Domain,
                           streamId:      streamId,
                           key:           key,
                           Timeout:       VerificationTimeout,
                           cancellationToken: cts.Token);

            }
            catch (Exception)
            {
                // XEP-0220, section 2.4 knows <remote-server-timeout/> for
                // this; for the caller the result is the same: not proven.
                return false;
            }
            finally
            {
                try { socket?.Dispose(); }
                catch { /* never mind */ }
            }

        }

        /// <summary>How long the query at the authoritative server may take.</summary>
        private static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Reads the answer of the authoritative server until the verification
        /// stream ends.
        /// </summary>
        private static async Task PumpVerificationFramesAsync(ClientWebSocket socket, S2SStream stream)
        {

            var buffer = new Byte[8192];

            try
            {

                while (socket.State == WebSocketState.Open && !stream.IsClosed)
                {

                    var sb = new StringBuilder();
                    WebSocketReceiveResult result;

                    do
                    {

                        result = await socket.ReceiveAsync(buffer, CancellationToken.None);

                        if (result.MessageType == WebSocketMessageType.Close)
                            break;

                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    if (sb.Length > 0)
                        await stream.ProcessFrameAsync(sb.ToString());

                }

            }
            catch (Exception)
            {
                // Connection gone - the abort below wakes any waiting party.
            }

            stream.Abort("The verification connection has ended");

        }

        #endregion

        #region (private) GetOrCreateOutboundAsync(remoteDomain, cancellationToken)

        /// <summary>
        /// Delivers the existing outgoing connection to a domain or establishes
        /// a new one.
        /// </summary>
        /// <remarks>
        /// The setup stands in the cache as a <c>Task</c>, not only its result -
        /// otherwise two simultaneous deliveries to the same domain could
        /// establish two connections.
        /// </remarks>
        private Task<OutboundLink?> GetOrCreateOutboundAsync(String              remoteDomain,
                                                             CancellationToken   cancellationToken)
        {

            lock (_lock)
            {

                if (_outbound.TryGetValue(remoteDomain, out var existing))
                    return existing.Connecting!;

                if (!_peers.TryGetValue(remoteDomain, out var peer))
                    return Task.FromResult<OutboundLink?>(null);

                var slot = new OutboundSlot();
                _outbound[remoteDomain] = slot;

                slot.Connecting = ConnectOutboundAsync(remoteDomain, peer, slot, cancellationToken);

                return slot.Connecting;

            }

        }

        #endregion

        #region (private) ConnectOutboundAsync(remoteDomain, peer, cancellationToken)

        private async Task<OutboundLink?> ConnectOutboundAsync(String              remoteDomain,
                                                               PeerConfig          peer,
                                                               OutboundSlot        slot,
                                                               CancellationToken   cancellationToken)
        {

            try
            {

                var socket = new ClientWebSocket();
                socket.Options.AddSubProtocol(S2SSubprotocol);

                if (peer.Validator is not null)
                    socket.Options.RemoteCertificateValidationCallback = peer.Validator;

                await socket.ConnectAsync(new Uri(peer.Uri), cancellationToken);

                var stream = S2SStream.Initiate(
                                 _localServer.Domain,
                                 remoteDomain,
                                 (frame, ct) => SendFrameAsync(socket, frame, ct),
                                 secret:         DialbackSecret,

                                 // XEP-0288: what comes in over the return
                                 // direction takes the same way as on an
                                 // incoming connection, sender check included.
                                 deliverStanza:  (peerDomain, stanza)
                                                     => _localServer.AcceptFromRemoteAsync(peerDomain, stanza),
                                 useBidi:        RequestBidirectionalStreams);

                stream.OnClosed += _ => DropOutbound(remoteDomain, slot);

                _ = PumpIncomingFramesAsync(socket, stream, remoteDomain, slot);

                await stream.OpenAsync(cancellationToken);

                if (!await stream.WaitUntilOpenAsync(OutboundHandshakeTimeout, cancellationToken))
                {
                    DropOutbound(remoteDomain, slot);
                    return null;
                }

                // XEP-0220: only once the peer has confirmed our domain is the
                // stream usable. Stanzas delivered before that it would discard
                // anyway.
                if (!await stream.WaitUntilAuthenticatedAsync(OutboundHandshakeTimeout, cancellationToken))
                {
                    stream.Abort("The dialback was not completed");
                    DropOutbound(remoteDomain, slot);
                    return null;
                }

                return new OutboundLink(socket, stream);

            }
            catch (Exception)
            {

                DropOutbound(remoteDomain, slot);

                return null;

            }

        }

        /// <summary>How long the <c>&lt;open/&gt;</c> of the peer is waited for.</summary>
        private static readonly TimeSpan OutboundHandshakeTimeout = TimeSpan.FromSeconds(10);

        #endregion

        #region (private) PumpIncomingFramesAsync / SendFrameAsync / RemoveOutbound

        /// <summary>
        /// Reads WebSocket frames from the outgoing socket and passes them on
        /// to the stream, until the connection ends.
        /// </summary>
        private async Task PumpIncomingFramesAsync(ClientWebSocket  socket,
                                                   S2SStream        stream,
                                                   String           remoteDomain,
                                                   OutboundSlot     slot)
        {

            var buffer = new Byte[8192];

            try
            {

                while (socket.State == WebSocketState.Open)
                {

                    var sb = new StringBuilder();
                    WebSocketReceiveResult result;

                    do
                    {

                        result = await socket.ReceiveAsync(buffer, CancellationToken.None);

                        if (result.MessageType == WebSocketMessageType.Close)
                            break;

                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    var frame = sb.ToString();

                    if (frame.Length > 0)
                        await stream.ProcessFrameAsync(frame);

                    // A stream error of the peer closes the stream without
                    // ending the WebSocket connection - RFC 6120, section 4.9
                    // demands exactly that, though. Without this way out the
                    // loop would carry on into a ReceiveAsync that never gets
                    // anything again.
                    if (stream.IsClosed)
                        break;

                }

            }
            catch (Exception)
            {
                // Socket gone - the stream learns of it below through the abort.
            }

            stream.Abort("The outgoing WebSocket connection has ended");
            DropOutbound(remoteDomain, slot);

            try { socket.Dispose(); }
            catch { /* never mind */ }

        }

        private async Task SendFrameAsync(ClientWebSocket socket, String frame, CancellationToken ct)
        {

            if (socket.State != WebSocketState.Open)
                return;

            var bytes = Encoding.UTF8.GetBytes(frame);

            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

        }

        /// <summary>
        /// Clears a slot out of the connection cache when it is still the same
        /// one - regardless of whether the setup had already finished.
        /// </summary>
        private void DropOutbound(String remoteDomain, OutboundSlot slot)
        {

            lock (_lock)
            {

                if (_outbound.TryGetValue(remoteDomain, out var current) &&
                    ReferenceEquals(current, slot))
                {
                    _outbound.Remove(remoteDomain);
                }

            }

        }

        #endregion


        #region (private class) S2SWebSocketListener

        /// <summary>
        /// The incoming branch - accepts WebSocket connections with the S2S
        /// subprotocol and holds one receiving <see cref="S2SStream"/> per
        /// connection.
        /// </summary>
        private sealed class S2SWebSocketListener : AWebSocketServer
        {

            #region Data

            private readonly WebSocketServerLinks  _links;
            private readonly Lock                  _lock = new();

            /// <summary>
            /// The streams per connection - <b>explicitly</b> by reference
            /// equality.
            /// </summary>
            /// <remarks>
            /// Hermod's <c>WebSocketServerConnection</c> compares itself
            /// through <c>LocalSocket</c>, and with a listener that one is the
            /// same for every accepted connection: from the point of view of an
            /// ordinary dictionary <b>all</b> incoming connections are thus one
            /// and the same. Without this comparer the second incoming
            /// connection got the stream of the first back - together with its
            /// send function, which wrote to a long since closed socket. The
            /// answer then went nowhere and the peer waited into its time
            /// limit.
            ///
            /// <see cref="XMPPServer"/> has been avoiding the same problem all
            /// along with a <c>ReferenceEquals</c> over a list.
            /// </remarks>
            private readonly Dictionary<WebSocketServerConnection, S2SStream> _streams = new(ByReference.Instance);

            private sealed class ByReference : IEqualityComparer<WebSocketServerConnection>
            {

                public static readonly ByReference Instance = new();

                public Boolean Equals(WebSocketServerConnection? a, WebSocketServerConnection? b)
                    => ReferenceEquals(a, b);

                public Int32 GetHashCode(WebSocketServerConnection connection)
                    => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(connection);

            }

            private Int32 _connectionCounter;

            #endregion

            /// <summary>The number of S2S connections ever accepted.</summary>
            public Int32 ConnectionCounter => Volatile.Read(ref _connectionCounter);

            #region Constructor(s)

            public S2SWebSocketListener(WebSocketServerLinks  links,
                                        IPPort                port,
                                        X509Certificate2?     certificate)

                : base(TCPPort:                     port,
                       ServerCertificateSelector:    certificate is not null
                                                          ? (_, _) => certificate
                                                          : null,
                       RequireAuthentication:        false,
                       SecWebSocketProtocols:        [S2SSubprotocol],
                       AutoStart:                    false)

            {

                _links = links;

                // Without this a stream would stay standing in the table per
                // ended connection - inconspicuous, but unbounded.
                OnTCPConnectionClosed += (timestamp, server, connection, eventTrackingId, reason, ct) =>
                {

                    S2SStream? stream;

                    lock (_lock)
                    {
                        _streams.Remove(connection, out stream);
                    }

                    stream?.Abort("The incoming WebSocket connection has ended");

                    return Task.CompletedTask;

                };

            }

            #endregion

            /// <summary>
            /// A snapshot of the open incoming streams - for XEP-0288 the only
            /// place where one can be found for the return direction.
            /// </summary>
            /// <remarks>
            /// A copy, so that the lock is not held across the sending: a slow
            /// peer would otherwise hold up every further delivery, including
            /// those to entirely different domains.
            /// </remarks>
            internal IReadOnlyList<S2SStream> InboundStreams()
            {
                lock (_lock)
                    return [.. _streams.Values];
            }

            private S2SStream StreamOf(WebSocketServerConnection connection)
            {

                lock (_lock)
                {

                    if (_streams.TryGetValue(connection, out var existing))
                        return existing;

                    Interlocked.Increment(ref _connectionCounter);

                    var stream = S2SStream.Accept(
                                     _links._localServer.Domain,
                                     (frame, ct) => SendTextMessage(connection, frame),
                                     (peerDomain, stanza) => _links._localServer.AcceptFromRemoteAsync(peerDomain, stanza),
                                     secret:     _links.DialbackSecret,
                                     verifyKey:  _links.VerifyDialbackKeyAsync,
                                     offerBidi:  _links.OfferBidirectionalStreams);

                    stream.OnClosed += reason =>
                    {

                        lock (_lock)
                            _streams.Remove(connection);

                        // RFC 6120, section 4.9: an ended stream takes the
                        // connection with it. Without this the WebSocket
                        // connection would stay open although nothing happens
                        // on it protocol-wise any more - a leak, not a fault
                        // that would show up at some point.
                        _ = Task.Run(async () =>
                        {
                            try { await connection.Close(); }
                            catch { /* never mind */ }
                        });

                    };

                    _streams[connection] = stream;

                    return stream;

                }

            }

            public override async Task ProcessTextMessage(DateTimeOffset             Timestamp,
                                                          AWebSocketServer           Server,
                                                          WebSocketServerConnection  Connection,
                                                          EventTracking_Id           EventTrackingId,
                                                          WebSocketFrame             TextFrame,
                                                          String                     TextMessage,
                                                          CancellationToken          CancellationToken)
            {

                var stream = StreamOf(Connection);

                try
                {
                    await stream.ProcessFrameAsync(TextMessage, CancellationToken);
                }
                catch (Exception)
                {
                    // Connection dropped - as with the client access the normal case.
                }

            }

        }

        #endregion

        #region (private) FreeTcpPort()

        private static Int32 FreeTcpPort()
        {

            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((System.Net.IPEndPoint) l.LocalEndpoint).Port;
            l.Stop();

            return port;

        }

        #endregion


        #region DisposeAsync()

        public async ValueTask DisposeAsync()
        {

            List<Task<OutboundLink?>> outbound;

            lock (_lock)
                outbound = [.. _outbound.Values
                                        .Select(slot => slot.Connecting)
                                        .Where(task => task is not null)
                                        .Cast<Task<OutboundLink?>>()];

            foreach (var task in outbound)
            {

                try
                {

                    var link = await task;

                    if (link is not null)
                    {
                        link.Stream.Abort("The server is shutting down");
                        try { link.Socket.Dispose(); }
                        catch { /* never mind */ }
                    }

                }
                catch (Exception)
                {
                    // Establishing the connection had already failed anyway.
                }

            }

            try { await _listener.Shutdown(Wait: true); }
            catch { /* never mind */ }

        }

        #endregion

    }

}
