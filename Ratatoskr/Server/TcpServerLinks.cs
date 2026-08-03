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

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    // See XMPPServer.cs: Hermod brings along a type IPAddress of its own that
    // hides the one of the same name from System.Net. The alias has to stand
    // inside the namespace declaration, otherwise the namespace member wins.
    using IPAddress = System.Net.IPAddress;

    /// <summary>
    /// Server-to-server over the classic framing: TCP, port 5269,
    /// <c>jabber:server</c> streams (RFC 6120).
    /// </summary>
    /// <remarks>
    /// The same protocol layer as <see cref="WebSocketServerLinks"/> - only
    /// what lies beneath changes: <see cref="TcpStreamFraming"/> instead of
    /// <see cref="WebSocketFraming"/> and <see cref="XmlStreamSplitter"/>
    /// instead of ready-made WebSocket frames. Dialback, the sender check, the
    /// connection management and the error handling stand unchanged in
    /// <see cref="S2SStream"/>.
    ///
    /// <b>This is the way to foreign servers.</b> ejabberd and Prosody speak
    /// exactly this; the WebSocket link only connects instances of this server
    /// with one another.
    ///
    /// <b>How TLS comes about is decided by <see cref="TcpTlsMode"/>.</b> The
    /// default is STARTTLS (RFC 6120, section 5.4): the stream begins in
    /// plaintext, negotiates encryption and starts over afterwards.
    /// <see cref="TcpTlsMode.Direct"/> saves the negotiation and is the simpler
    /// one between two instances of this server.
    ///
    /// The negotiation itself stands here in the transport and not in
    /// <see cref="S2SStream"/>. That is no accident: the stream before TLS is a
    /// throwaway stream whose state is discarded after the encryption
    /// (section 5.4.3.3). The protocol layer only gets the stream once it is
    /// encrypted and does not have to know anything about the negotiation - and
    /// thereby gets no opportunity to take something over from the plaintext
    /// phase by accident either.
    /// </remarks>
    public sealed class TcpServerLinks : IServerLinks, IAsyncDisposable
    {

        #region Data

        private readonly XMPPServer                       _localServer;
        private readonly TcpListener                      _listener;
        private readonly CancellationTokenSource          _cts        = new();
        private readonly Dictionary<String, PeerConfig>   _peers      = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<String, OutboundSlot> _outbound   = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock                             _lock       = new();

        /// <summary>
        /// The open incoming streams - for XEP-0288 the only place where one
        /// can be found for the return direction.
        /// </summary>
        /// <remarks>
        /// A list and not a dictionary by domain: the domain is not settled
        /// when the entry is created (it only comes with the
        /// <c>&lt;open/&gt;</c> of the peer), and several connections of the
        /// same domain are permitted. At the numbers this is about, looking
        /// through costs nothing.
        /// </remarks>
        private readonly List<InboundLink>                _inbound    = [];

        /// <summary>
        /// An accepted stream together with the connection it lies on.
        /// </summary>
        /// <remarks>
        /// The connection belongs with it, because the shutdown has to close
        /// it. An <see cref="S2SStream"/> alone can be aborted, but the socket
        /// would stay open - and the peer would take it for usable.
        /// </remarks>
        private sealed record InboundLink(S2SStream Stream, TcpClient Client);

        private Int32 _inboundCounter;
        private Int32 _dialbackVerifications;
        private Int32 _bidiDeliveries;

        private sealed record PeerConfig(String                                Host,
                                         Int32                                 Port,
                                         TcpTlsMode                            Mode,
                                         RemoteCertificateValidationCallback?  Validator);

        private sealed class OutboundSlot
        {
            public Task<S2SStream?>? Connecting;
        }

        #endregion

        #region Properties

        /// <summary>The port on which incoming S2S connections are expected.</summary>
        public Int32 Port { get; }

        /// <summary>The certificate for incoming connections, or null for plaintext.</summary>
        public X509Certificate2? Certificate { get; }

        /// <summary>How incoming connections come to TLS.</summary>
        public TcpTlsMode Mode { get; }

        /// <summary>
        /// Shall the domain of the peer be proven through its TLS certificate
        /// (SASL-EXTERNAL, XEP-0178) instead of through dialback?
        /// </summary>
        /// <remarks>
        /// Presupposes mutual TLS - without a client certificate there is
        /// nothing to check. If it is switched on and the peer presents none,
        /// dialback remains the way; the offer is then simply omitted.
        /// </remarks>
        public Boolean UseSaslExternal { get; init; }

        /// <summary>
        /// XEP-0288: offer the return direction on <b>incoming</b> connections.
        /// </summary>
        /// <remarks>
        /// Without the extension each side answers over a connection of its
        /// <b>own</b> (RFC 6120, section 4.1). That presupposes that the peer
        /// can reach us - behind NAT, behind a firewall or without a DNS entry
        /// it cannot, and the answer is lost without anyone noticing.
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
        /// <remarks>
        /// The only difference visible from the outside. Whether a message
        /// arrives says nothing about it - it would arrive over both routes,
        /// and precisely for that reason a test without this number would be
        /// blind to which one it took.
        /// </remarks>
        public Int32 BidirectionalDeliveryCount => Volatile.Read(ref _bidiDeliveries);

        /// <summary>The dialback secret of this server (XEP-0220).</summary>
        public String DialbackSecret { get; } = DialbackKey.NewSecret();

        /// <summary>
        /// Where the address of a domain comes from that is not entered by
        /// hand. Null leaves it at the peer list.
        /// </summary>
        /// <remarks>
        /// The list goes first. That is intentional and no convenience: an
        /// entry by hand is a decision of the operator, a DNS answer only a
        /// piece of information from the network - and without DNSSEC an
        /// uncertified one. Whoever has both shall keep the decision.
        ///
        /// <b>For the dialback query this shifts the root of trust.</b> Until
        /// now only the operator's list stood there, and it was exactly from
        /// that that the check drew its sharpness. If the authoritative address
        /// is searched for through DNS, dialback is only as reliable as the
        /// resolution - that is how XEP-0220 means it, but it is less than the
        /// list offered. Whoever does not want that leaves this property null
        /// and enters their peers.
        /// </remarks>
        public IS2SAddressResolver? AddressResolver { get; init; }

        /// <summary>The number of incoming connections ever accepted.</summary>
        public Int32 InboundConnectionCount => Volatile.Read(ref _inboundCounter);

        /// <summary>
        /// How often this server has queried a dialback key at the
        /// authoritative server.
        /// </summary>
        /// <remarks>
        /// The only difference visible from the outside between dialback and
        /// SASL-EXTERNAL: the one calls back, the other reads the certificate.
        /// The number of connections is no good for that - other things run
        /// across the boundary too, such as the automatic delivery receipt of
        /// the client.
        /// </remarks>
        public Int32 DialbackVerificationCount => Volatile.Read(ref _dialbackVerifications);

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Creates the incoming branch and accepts connections right away.
        /// </summary>
        /// <param name="localServer">The server whose S2S counterpart this is.</param>
        /// <param name="port">A fixed port, or 0 for a free one. Intended is 5269.</param>
        /// <param name="mode">
        /// How TLS comes about. The default is STARTTLS, because that is the
        /// way from RFC 6120, section 5.4 and because foreign servers expect
        /// it.
        /// </param>
        public TcpServerLinks(XMPPServer  localServer,
                              Int32       port   = 0,
                              TcpTlsMode  mode   = TcpTlsMode.StartTls)
        {

            _localServer  = localServer;
            Mode          = mode;
            Certificate   = mode == TcpTlsMode.None ? null : localServer.Certificate;

            _listener     = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();

            Port          = ((IPEndPoint) _listener.LocalEndpoint).Port;

            _ = AcceptLoopAsync();

            localServer.ServerLinks = this;

        }

        #endregion


        #region AddPeer(domain, host, port, useTLS, validator)

        /// <summary>
        /// Makes a foreign domain reachable through a host name and a port.
        /// </summary>
        /// <remarks>
        /// By hand, because the resolution through SRV records
        /// (<c>_xmpp-server._tcp</c>, RFC 6120 section 3.2.1) is still missing.
        /// This list is at the same time what takes the place of DNS in the
        /// dialback check - see <see cref="VerifyDialbackKeyAsync"/>.
        ///
        /// <b>The host name should resolve to an address family the peer
        /// actually listens on.</b> This listener binds IPv4 loopback; a name
        /// like <c>localhost</c> that resolves to IPv6 first then costs around
        /// two seconds per connection until the fallback takes hold - the
        /// connection comes about, only late. That is exactly what lengthened
        /// the first delivery from 82 to 4167 milliseconds, and
        /// inconspicuously at that, because in the end everything worked.
        /// </remarks>
        public void AddPeer(String                                domain,
                            String                                host,
                            Int32                                 port,
                            TcpTlsMode                            mode        = TcpTlsMode.StartTls,
                            RemoteCertificateValidationCallback?  validator   = null)
        {
            lock (_lock)
                _peers[domain] = new PeerConfig(host, port, mode, validator);
        }

        #endregion

        #region (static) Connect(a, b)

        /// <summary>
        /// Connects two servers over TCP in both directions.
        /// </summary>
        public static void Connect(XMPPServer  a,
                                   XMPPServer  b,
                                   TcpTlsMode  mode              = TcpTlsMode.StartTls,
                                   Boolean     useSaslExternal   = false)
        {

            if (String.Equals(a.Domain, b.Domain, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                          $"Both servers serve '{a.Domain}' - a federation with itself amounts to nothing.",
                          nameof(b));

            var linksA = LinksOf(a, mode, useSaslExternal);
            var linksB = LinksOf(b, mode, useSaslExternal);

            // Explicitly the address and not "localhost": the listener binds
            // IPv4 loopback, and a name that resolves to IPv6 first costs the
            // fallback on every connection.
            var loopback = IPAddress.Loopback.ToString();

            linksA.AddPeer(b.Domain, loopback, linksB.Port, linksB.Mode, b.IsOwnCertificate);
            linksB.AddPeer(a.Domain, loopback, linksA.Port, linksA.Mode, a.IsOwnCertificate);

        }

        private static TcpServerLinks LinksOf(XMPPServer server, TcpTlsMode mode, Boolean useSaslExternal)

            => server.ServerLinks as TcpServerLinks
               ?? new TcpServerLinks(server, mode: mode) { UseSaslExternal = useSaslExternal };

        #endregion

        #region DeliverAsync(remoteDomain, stanza, cancellationToken)

        public async Task<Boolean> DeliverAsync(String             remoteDomain,
                                                String             stanza,
                                                CancellationToken  cancellationToken = default)
        {

            // XEP-0288: if an incoming connection with an enabled return
            // direction is on hand for this domain, the stanza goes out there.
            //
            // Taking precedence over dialling, and that is the whole purpose:
            // the peer asked for the return direction because it reckons we
            // cannot reach it. Dialling first and treating the existing
            // connection only as a last resort would mean failing exactly where
            // the extension helps.
            // No switch in front of it: BidiEnabled can only be true when we
            // offered the return direction *and* the peer asked for it. An
            // additional query would check the same statement a second time -
            // and hung on the wrong switch until the very end, the one for the
            // outgoing side.
            if (await S2SStream.TryDeliverOverBidiAsync(InboundStreams(), remoteDomain,
                                                        stanza, cancellationToken))
            {
                Interlocked.Increment(ref _bidiDeliveries);
                return true;
            }

            var stream = await GetOrCreateOutboundAsync(remoteDomain, cancellationToken);

            return stream is not null &&
                   await stream.SendStanzaAsync(stanza, cancellationToken);

        }

        /// <summary>
        /// A snapshot of the open incoming streams.
        /// </summary>
        /// <remarks>
        /// A copy, so that the lock is not held across the sending - a slow
        /// peer would otherwise hold up every further delivery. The switch in
        /// front of it is a shortcut and not a safeguard:
        /// <c>BidiEnabled</c> only sets itself on a stream created with
        /// <c>offerBidi</c>, and that comes from the same switch. A mutation
        /// therefore survives it rightly.
        /// </remarks>
        private IReadOnlyList<S2SStream> InboundStreams()
        {
            lock (_lock)
                return [.. _inbound.Select(l => l.Stream)];
        }

        #endregion


        #region (internal) VerifyDialbackKeyAsync(senderDomain, streamId, key)

        /// <summary>
        /// XEP-0220, steps 2 and 3 - as with
        /// <see cref="WebSocketServerLinks"/>, only over TCP.
        /// </summary>
        /// <remarks>
        /// Here too it holds: what is asked is the address on record of the
        /// sender domain, not whoever is currently trying to identify itself.
        /// </remarks>
        internal async Task<Boolean> VerifyDialbackKeyAsync(String senderDomain,
                                                            String streamId,
                                                            String key)
        {

            Interlocked.Increment(ref _dialbackVerifications);

            foreach (var peer in await CandidatesForAsync(senderDomain))
            {

                if (await VerifyAtAsync(peer, senderDomain, streamId, key))
                    return true;

            }

            return false;

        }

        /// <summary>
        /// The addresses a domain might be reachable at for the dialback query.
        /// </summary>
        /// <remarks>
        /// The entry by hand goes first; only after it the resolution. Without
        /// both there is nobody to ask - and believing is not checking.
        ///
        /// That the query has to be resolved at all is the normal case from
        /// XEP-0220: the checking server searches for the authoritative one
        /// itself. It does, however, shift the root of trust from the operator
        /// into DNS - see <see cref="AddressResolver"/>.
        /// </remarks>
        private async Task<IReadOnlyList<PeerConfig>> CandidatesForAsync(String domain)
        {

            PeerConfig? configured;

            lock (_lock)
                _peers.TryGetValue(domain, out configured);

            if (configured is not null)
                return [configured];

            if (AddressResolver is null)
                return [];

            try
            {

                var targets = await AddressResolver.ResolveAsync(domain);

                return [.. targets.Select(z => new PeerConfig(z.Host,
                                                              z.Port,
                                                              Mode,
                                                              DefaultPeerValidator))];

            }
            catch (Exception)
            {
                return [];
            }

        }

        /// <summary>
        /// Asks a single address about the dialback key.
        /// </summary>
        private async Task<Boolean> VerifyAtAsync(PeerConfig  peer,
                                                  String      senderDomain,
                                                  String      streamId,
                                                  String      key)
        {

            TcpClient? client = null;

            try
            {

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(VerificationTimeout);

                client = new TcpClient();
                await client.ConnectAsync(peer.Host, peer.Port, cts.Token);

                var net = await WrapAsync(client, peer, senderDomain, cts.Token);

                if (net is null)
                    return false;

                var stream = S2SStream.InitiateVerification(
                                 _localServer.Domain,
                                 senderDomain,
                                 (frame, ct) => SendAsync(net, frame, ct),
                                 framing: TcpStreamFraming.Instance);

                _ = PumpAsync(net, stream, null);

                await stream.OpenAsync(cts.Token);

                if (!await stream.WaitUntilOpenAsync(VerificationTimeout, cts.Token))
                    return false;

                return await stream.RequestVerificationAsync(
                           targetDomain:       _localServer.Domain,
                           streamId:           streamId,
                           key:                key,
                           Timeout:            VerificationTimeout,
                           cancellationToken:  cts.Token);

            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                try { client?.Dispose(); }
                catch { /* never mind */ }
            }

        }

        private static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan HandshakeTimeout    = TimeSpan.FromSeconds(10);

        #endregion

        #region (private) AcceptLoopAsync()

        private async Task AcceptLoopAsync()
        {

            while (!_cts.IsCancellationRequested)
            {

                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    // The listener has ended - that is that.
                    return;
                }

                Interlocked.Increment(ref _inboundCounter);

                _ = HandleInboundAsync(client);

            }

        }

        #endregion

        #region (private) HandleInboundAsync(client)

        private async Task HandleInboundAsync(TcpClient client)
        {

            Stream?             net              = null;
            X509Certificate?    peerCertificate  = null;

            try
            {

                net = client.GetStream();

                if (Mode == TcpTlsMode.Direct)
                {

                    var tls = new SslStream(net, leaveInnerStreamOpen: false);

                    await tls.AuthenticateAsServerAsync(
                              ServerOptions(),
                              _cts.Token);

                    net = tls;
                    peerCertificate = tls.RemoteCertificate;

                }

                else if (Mode == TcpTlsMode.StartTls)
                {

                    // With a time limit: a peer that begins the handshake and
                    // then falls silent would otherwise hold this connection
                    // forever - and the whole server at shutdown.
                    using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    handshakeCts.CancelAfter(HandshakeTimeout);

                    var tls = await StartTlsAsServerAsync(net, handshakeCts.Token);

                    // Without TLS there is no stream. The caller learns of it
                    // from the connection ending.
                    if (tls is null)
                        return;

                    net              = tls;
                    peerCertificate  = tls.RemoteCertificate;

                }

                var stream = S2SStream.Accept(
                                 _localServer.Domain,
                                 (frame, ct) => SendAsync(net, frame, ct),
                                 (peerDomain, stanza) => _localServer.AcceptFromRemoteAsync(peerDomain, stanza),
                                 secret:            DialbackSecret,
                                 verifyKey:         VerifyDialbackKeyAsync,
                                 framing:           TcpStreamFraming.Instance,
                                 externalIdentity:  IdentityCheckFor(peerCertificate),
                                 offerBidi:         OfferBidirectionalStreams);

                var link = new InboundLink(stream, client);

                lock (_lock)
                    _inbound.Add(link);

                try
                {
                    await PumpAsync(net, stream, null);
                }
                finally
                {
                    lock (_lock)
                        _inbound.Remove(link);
                }

            }
            catch (Exception)
            {
                // The connection dropped - in operation the normal case.
            }
            finally
            {
                try { net?.Dispose(); }   catch { /* never mind */ }
                try { client.Dispose(); } catch { /* never mind */ }
            }

        }

        #endregion

        #region (private) GetOrCreateOutboundAsync / ConnectOutboundAsync

        private Task<S2SStream?> GetOrCreateOutboundAsync(String             remoteDomain,
                                                          CancellationToken  cancellationToken)
        {

            lock (_lock)
            {

                if (_outbound.TryGetValue(remoteDomain, out var existing))
                    return existing.Connecting!;

                // No entry by hand and no resolver - then the domain does not
                // exist for this server.
                if (!_peers.TryGetValue(remoteDomain, out var peer) &&
                    AddressResolver is null)
                {
                    return Task.FromResult<S2SStream?>(null);
                }

                var slot = new OutboundSlot();
                _outbound[remoteDomain] = slot;

                slot.Connecting = peer is not null
                                      ? ConnectOutboundAsync(remoteDomain, peer, slot, cancellationToken)
                                      : ResolveAndConnectAsync(remoteDomain, slot, cancellationToken);

                return slot.Connecting;

            }

        }

        /// <summary>
        /// Searches for the addresses of a domain and tries them in turn.
        /// </summary>
        /// <remarks>
        /// In turn and not only the first target: SRV records name fallback
        /// hosts, and listing them without using them would be half an
        /// implementation. The order comes from <see cref="SrvSelection"/> and
        /// is not touched again here.
        ///
        /// The mode and the certificate validation come from the default values
        /// of this server - in particular the check is against the <b>domain
        /// sought</b> and not against the host name from the SRV record. The
        /// other way round a forged record would suffice to pass the check.
        /// </remarks>
        private async Task<S2SStream?> ResolveAndConnectAsync(String             remoteDomain,
                                                              OutboundSlot       slot,
                                                              CancellationToken  cancellationToken)
        {

            IReadOnlyList<SrvTarget> targets;

            try
            {
                targets = await AddressResolver!.ResolveAsync(remoteDomain, cancellationToken);
            }
            catch (Exception)
            {
                targets = [];
            }

            foreach (var target in targets)
            {

                var peer = new PeerConfig(target.Host,
                                          target.Port,
                                          Mode,
                                          DefaultPeerValidator);

                var stream = await ConnectOutboundAsync(remoteDomain, peer, slot, cancellationToken);

                if (stream is not null)
                {

                    // The slot in the cache was cleared by ConnectOutboundAsync
                    // on every failed attempt - for the success it has to stand
                    // again, otherwise the next delivery establishes a new one.
                    lock (_lock)
                        _outbound[remoteDomain] = slot;

                    return stream;

                }

            }

            DropOutbound(remoteDomain, slot);

            return null;

        }

        /// <summary>
        /// The certificate validation for resolved peers.
        /// </summary>
        /// <remarks>
        /// Null leaves it to the operating system - for operation the right
        /// default, because a foreign server shall present a certificate of a
        /// known CA. In the test setup it is set, because self-signed
        /// certificates would otherwise get through nowhere.
        /// </remarks>
        public RemoteCertificateValidationCallback? DefaultPeerValidator { get; init; }

        private async Task<S2SStream?> ConnectOutboundAsync(String             remoteDomain,
                                                            PeerConfig         peer,
                                                            OutboundSlot       slot,
                                                            CancellationToken  cancellationToken)
        {

            try
            {

                var client = new TcpClient();
                await client.ConnectAsync(peer.Host, peer.Port, cancellationToken);

                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshakeCts.CancelAfter(HandshakeTimeout);

                var net = await WrapAsync(client, peer, remoteDomain, handshakeCts.Token);

                if (net is null)
                {
                    DropOutbound(remoteDomain, slot);
                    return null;
                }

                var stream = S2SStream.Initiate(
                                 _localServer.Domain,
                                 remoteDomain,
                                 (frame, ct) => SendAsync(net, frame, ct),
                                 secret:            DialbackSecret,
                                 framing:           TcpStreamFraming.Instance,
                                 canOfferExternal:  UseSaslExternal && Certificate is not null,

                                 // XEP-0288: what comes in over the return
                                 // direction goes the same way as on an
                                 // incoming stream - sender check included.
                                 // That the peer is who it claims to be is
                                 // already settled here: we dialled it and
                                 // checked its certificate.
                                 deliverStanza:     (peerDomain, stanza)
                                                        => _localServer.AcceptFromRemoteAsync(peerDomain, stanza),
                                 useBidi:           RequestBidirectionalStreams);

                stream.OnClosed += _ => DropOutbound(remoteDomain, slot);

                _ = PumpAsync(net, stream, () =>
                    {
                        DropOutbound(remoteDomain, slot);
                        try { client.Dispose(); } catch { /* never mind */ }
                    });

                await stream.OpenAsync(cancellationToken);

                if (!await stream.WaitUntilReadyAsync(HandshakeTimeout, cancellationToken))
                {
                    stream.Abort("The setup was not completed");
                    DropOutbound(remoteDomain, slot);
                    return null;
                }

                return stream;

            }
            catch (Exception)
            {
                DropOutbound(remoteDomain, slot);
                return null;
            }

        }

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

        #region (private class) FrameReader

        /// <summary>
        /// Reads individual frames out of a stream - for the negotiation,
        /// before an <see cref="S2SStream"/> takes over.
        /// </summary>
        private sealed class FrameReader
        {

            private readonly Stream             _stream;
            private readonly XmlStreamSplitter  _splitter  = new();
            private readonly Queue<String>      _pending   = new();
            private readonly Byte[]             _buffer    = new Byte[8192];

            public FrameReader(Stream stream)
            {
                _stream = stream;
            }

            /// <summary>
            /// Is there still something in the buffer that the peer sent ahead?
            /// </summary>
            public Boolean HasPending => _pending.Count > 0;

            /// <summary>The next frame, or null when the stream ends.</summary>
            public async Task<String?> NextAsync(CancellationToken cancellationToken)
            {

                while (_pending.Count == 0)
                {

                    var read = await _stream.ReadAsync(_buffer, cancellationToken);

                    if (read <= 0)
                        return null;

                    foreach (var frame in _splitter.Push(Encoding.UTF8.GetString(_buffer, 0, read)))
                        _pending.Enqueue(frame);

                }

                return _pending.Dequeue();

            }

        }

        #endregion

        #region (private) STARTTLS (RFC 6120, section 5.4)

        /// <summary>The namespace of the TLS negotiation.</summary>
        private const String TlsNamespace = "urn:ietf:params:xml:ns:xmpp-tls";

        /// <summary>
        /// STARTTLS on the accepting side.
        /// </summary>
        /// <returns>The encrypted stream, or null when nothing came of it.</returns>
        private async Task<SslStream?> StartTlsAsServerAsync(Stream             net,
                                                             CancellationToken  cancellationToken)
        {

            var reader = new FrameReader(net);

            var header = await reader.NextAsync(cancellationToken);

            if (header is null || !TcpStreamFraming.Instance.IsStreamOpen(header))
                return null;

            await SendAsync(net,
                            TcpStreamFraming.Instance.StreamOpen(_localServer.Domain,
                                                                 S2SStream.Attr(header, "from"),
                                                                 Guid.NewGuid().ToString("N")),
                            cancellationToken);

            // <required/>, because RFC 6120, section 13.7 demands encryption
            // for S2S. Whoever declines it gets no stream - not an unencrypted
            // one.
            await SendAsync(net,
                            $"<stream:features xmlns:stream='{S2SStream.StreamNamespace}'>" +
                            $"<starttls xmlns='{TlsNamespace}'><required/></starttls>" +
                            "</stream:features>",
                            cancellationToken);

            var request = await reader.NextAsync(cancellationToken);

            if (request is null ||
                !request.StartsWith("<starttls", StringComparison.Ordinal) ||
                !request.Contains(TlsNamespace, StringComparison.Ordinal))
            {

                await SendAsync(net, $"<failure xmlns='{TlsNamespace}'/>", cancellationToken);

                return null;

            }

            // RFC 6120, section 5.4.3.3: after the <starttls/> nothing more may
            // follow in the clear. If something does stand in the buffer, the
            // peer has sent ahead - either it is broken, or somebody is trying
            // to smuggle plaintext into the stream that is about to be
            // encrypted. Both are a reason to stop and none to carry on.
            if (reader.HasPending)
                return null;

            await SendAsync(net, $"<proceed xmlns='{TlsNamespace}'/>", cancellationToken);

            var tls = new SslStream(net, leaveInnerStreamOpen: false);

            await tls.AuthenticateAsServerAsync(ServerOptions(), cancellationToken);

            return tls;

        }

        /// <summary>
        /// STARTTLS on the establishing side.
        /// </summary>
        /// <remarks>
        /// The stream conducted here is a throwaway stream: after the
        /// encryption everything starts over, with a new stream header and a
        /// new stream ID (RFC 6120, section 5.4.3.3). That is why this stands
        /// here in the transport and not in <see cref="S2SStream"/> - that
        /// layer only gets the stream once it is encrypted and does not have to
        /// know anything about the negotiation.
        /// </remarks>
        private async Task<SslStream?> StartTlsAsClientAsync(Stream             net,
                                                             PeerConfig         peer,
                                                             String             remoteDomain,
                                                             CancellationToken  cancellationToken)
        {

            var reader = new FrameReader(net);

            await SendAsync(net,
                            TcpStreamFraming.Instance.StreamOpen(_localServer.Domain, remoteDomain, null),
                            cancellationToken);

            var offersTls = false;

            while (await reader.NextAsync(cancellationToken) is { } frame)
            {

                if (TcpStreamFraming.Instance.IsStreamOpen(frame))
                    continue;

                offersTls = frame.Contains(TlsNamespace, StringComparison.Ordinal);
                break;

            }

            // No STARTTLS in the offer - then there is no connection. Carrying
            // on in the clear would be exactly the fallback the negotiation
            // exists against.
            if (!offersTls)
                return null;

            await SendAsync(net, $"<starttls xmlns='{TlsNamespace}'/>", cancellationToken);

            var answer = await reader.NextAsync(cancellationToken);

            if (answer is null || !answer.StartsWith("<proceed", StringComparison.Ordinal))
                return null;

            if (reader.HasPending)
                return null;

            var tls = new SslStream(net,
                                    leaveInnerStreamOpen: false,
                                    userCertificateValidationCallback: peer.Validator);

            await tls.AuthenticateAsClientAsync(ClientOptions(remoteDomain), cancellationToken);

            return tls;

        }

        #endregion

        #region (private) SASL-EXTERNAL

        /// <summary>
        /// The TLS settings of the accepting server.
        /// </summary>
        /// <remarks>
        /// For SASL-EXTERNAL the client certificate has to be <b>requested</b> -
        /// without this line there is none, and the check would have nothing to
        /// read. Requested does not mean demanded: if it fails to come, the
        /// connection comes about all the same and the peer identifies itself
        /// by dialback.
        /// </remarks>
        private SslServerAuthenticationOptions ServerOptions()

            => new () {
                   ServerCertificate                   = Certificate,
                   ClientCertificateRequired           = UseSaslExternal,
                   RemoteCertificateValidationCallback = UseSaslExternal
                                                             ? (_, _, _, _) => true
                                                             : null
               };

        /// <summary>
        /// The TLS settings of the establishing server.
        /// </summary>
        /// <remarks>
        /// Our own certificate only goes along when SASL-EXTERNAL is intended.
        /// The peer checks it; whether it suffices for <i>them</i> is their
        /// decision.
        /// </remarks>
        private SslClientAuthenticationOptions ClientOptions(String remoteDomain)

            => new () {
                   TargetHost              = remoteDomain,
                   ClientCertificates      = UseSaslExternal && Certificate is not null
                                                 ? [Certificate]
                                                 : null
               };

        /// <summary>
        /// Turns the certificate presented into the check
        /// <see cref="S2SStream"/> needs - or null when there is none.
        /// </summary>
        /// <remarks>
        /// Null is the right answer here and no makeshift: without a
        /// certificate SASL-EXTERNAL must not be offered in the first place.
        ///
        /// <b>What this check does not achieve:</b> it says which domains the
        /// certificate is issued for - not whether it is to be trusted.
        /// Checking the chain against a known CA is the business of the TLS
        /// handshake and thereby of the validation on record; in the test setup
        /// that is a pinned fingerprint. Whoever puts a validation in here that
        /// lets everything through has reduced SASL-EXTERNAL to a statement
        /// about oneself.
        /// </remarks>
        private Func<String, Boolean>? IdentityCheckFor(X509Certificate? peerCertificate)
        {

            if (!UseSaslExternal || peerCertificate is null)
                return null;

            var certificate = peerCertificate as X509Certificate2
                                  ?? X509CertificateLoader.LoadCertificate(peerCertificate.GetRawCertData());

            return domain => CertificateIdentity.Authorises(certificate, domain);

        }

        #endregion

        #region (private) WrapAsync / SendAsync / PumpAsync

        /// <summary>
        /// Brings the connection into the state in which the protocol layer may
        /// take it over - depending on the mode in the clear, encrypted right
        /// away or after STARTTLS.
        /// </summary>
        /// <returns>null when TLS was intended and did not come about.</returns>
        private async Task<Stream?> WrapAsync(TcpClient          client,
                                              PeerConfig         peer,
                                              String             remoteDomain,
                                              CancellationToken  cancellationToken)
        {

            var net = (Stream) client.GetStream();

            if (peer.Mode == TcpTlsMode.None)
                return net;

            if (peer.Mode == TcpTlsMode.StartTls)
                return await StartTlsAsClientAsync(net, peer, remoteDomain, cancellationToken);

            var tls = new SslStream(
                          net,
                          leaveInnerStreamOpen: false,
                          userCertificateValidationCallback: peer.Validator);

            await tls.AuthenticateAsClientAsync(ClientOptions(remoteDomain), cancellationToken);

            return tls;

        }

        private static async Task SendAsync(Stream stream, String frame, CancellationToken cancellationToken)
        {

            var bytes = Encoding.UTF8.GetBytes(frame);

            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);

        }

        /// <summary>
        /// Reads the stream and passes every complete frame to the protocol
        /// layer.
        /// </summary>
        /// <remarks>
        /// Here sits the whole difference to WebSocket: what is read has
        /// nothing to do with element boundaries.
        /// <see cref="XmlStreamSplitter"/> makes frames out of it.
        /// </remarks>
        private static async Task PumpAsync(Stream stream, S2SStream s2s, Action? onEnd)
        {

            var buffer    = new Byte[8192];
            var splitter  = new XmlStreamSplitter();

            // After a SASL restart the stream begins as a new document.
            s2s.OnRestart += splitter.Reset;

            try
            {

                while (!s2s.IsClosed)
                {

                    var read = await stream.ReadAsync(buffer);

                    if (read <= 0)
                        break;

                    foreach (var frame in splitter.Push(Encoding.UTF8.GetString(buffer, 0, read)))
                    {

                        await s2s.ProcessFrameAsync(frame);

                        if (s2s.IsClosed)
                            break;

                    }

                }

            }
            catch (Exception)
            {
                // Connection gone.
            }

            s2s.Abort("The TCP connection has ended");

            onEnd?.Invoke();

        }

        #endregion


        #region DisposeAsync()

        public async ValueTask DisposeAsync()
        {

            await _cts.CancelAsync();

            try { _listener.Stop(); }
            catch { /* never mind */ }

            // Close accepted connections explicitly. Cancelling the token alone
            // does not suffice: the read on a socket does not break off
            // reliably with it, the loop stays standing until the peer hangs up
            // - and until then *it* takes the connection for usable and keeps
            // sending over it.
            //
            // Found in the run against Prosody: after the end of a test server,
            // Prosody kept answering the next request for another thirty
            // seconds over the long since dead socket. Between two instances of
            // this server it never showed, because there both sides disappear
            // at the same time.
            List<InboundLink> inbound;

            lock (_lock)
            {
                inbound = [.. _inbound];
                _inbound.Clear();
            }

            foreach (var link in inbound)
            {
                link.Stream.Abort("The server is shutting down");
                try { link.Client.Dispose(); } catch { /* never mind */ }
            }

            List<Task<S2SStream?>> outbound;

            lock (_lock)
                outbound = [.. _outbound.Values
                                        .Select(slot => slot.Connecting)
                                        .Where(task => task is not null)
                                        .Cast<Task<S2SStream?>>()];

            foreach (var task in outbound)
            {
                // With a time limit: a hanging connection setup must not block
                // the shutdown. Without it a failed test turned into a test run
                // that stood still.
                try { (await task.WaitAsync(HandshakeTimeout))?.Abort("The server is shutting down"); }
                catch { /* the setup failed or was too slow */ }
            }

            _cts.Dispose();

        }

        #endregion

    }

}
