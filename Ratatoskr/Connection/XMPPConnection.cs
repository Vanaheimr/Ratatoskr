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
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// XMPP over WebSocket (RFC 7395) with auto-reconnect.
///
/// This class is the transport and protocol layer: WebSocket I/O, SASL,
/// resource binding and stanza routing. The application-facing session logic
/// (current chat partner, open contact requests, composite operations) lives in
/// <see cref="XMPPClient"/>.
///
/// Features:
/// - SCRAM-SHA-1/256 and SASL PLAIN authentication
/// - XEP-0030 Service Discovery
/// - XEP-0060 Publish-Subscribe
/// - XEP-0085 Chat State Notifications
/// - XEP-0115 Entity Capabilities
/// - XEP-0184 Message Delivery Receipts
/// - XEP-0198 Stream Management (disabled by default)
/// - XEP-0199 Ping
/// - XEP-0280 Message Carbons
/// - XEP-0333 Chat Markers
/// </summary>
public sealed class XMPPConnection : IAsyncDisposable
{

    #region Data

    private string? _wsUri;
    private readonly string _defaultWsUri;
    private bool _endpointDiscovered;
    private readonly string _jid;
    private readonly string _password;
    private readonly string _username;
    private readonly string _domain;

    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger _logger;

    /// <summary>
    /// Serialises outgoing stanzas. Sending happens from several directions at
    /// once: the keepalive loop, auto-receipts and chat markers from the
    /// receive loop, as well as user actions.
    /// </summary>
    /// <remarks>
    /// The WebSocket contract allows only one outstanding send; whether a
    /// violation is noticed depends on the implementation. On .NET 10
    /// ClientWebSocket serialises internally, and there 200 parallel sends of
    /// 40 kB each stayed error-free and undamaged. Other implementations (older
    /// runtimes, browser WebSockets under WASM) throw
    /// InvalidOperationException instead. The lock makes the assurance
    /// explicit rather than relying on an undocumented implementation detail -
    /// cost: around 150 ms for the 200 sends mentioned.
    /// </remarks>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// How long the teardown waits for the background loops to end before
    /// giving up on them.
    /// </summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the WebSocket close handshake of the other side is waited for.
    /// Without a bound, CloseAsync blocks indefinitely when the server does not
    /// answer the close frame.
    /// </summary>
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Namespace of the stream layer (RFC 6120, section 4.8.2).</summary>
    private const string StreamNamespace = StreamNegotiation.StreamNamespace;

    /// <summary>Namespace of XEP-0198 stream management.</summary>
    private const string StreamManagementNamespace = StreamManagementManager.Namespace;

    /// <summary>
    /// How long the setup phase waits for the answer to one of its IQs.
    /// </summary>
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// IQs whose answer someone is waiting for right now, by their id.
    ///
    /// Replaces the earlier approach of the setup phase, which read up to ten
    /// frames from the socket itself and discarded everything that did not look
    /// like the expected answer. Discarded that way were messages, presence and
    /// roster pushes too; and because "looks like" was a
    /// <c>Contains("id='roster1'")</c> on the raw text, a message with that
    /// character sequence in it could also replace the answer.
    /// </summary>
    private readonly Dictionary<string, TaskCompletionSource<XElement>> _pendingIqs = new();

    /// <summary>
    /// The last error from <see cref="ConnectInternalAsync"/> - so that
    /// <see cref="ConnectAsync"/> can pass it on to the caller instead of
    /// merely reporting it.
    /// </summary>
    private Exception? _lastConnectError;
    private readonly object _iqLock = new();

    /// <summary>
    /// The lower bound for the SASL negotiation. Belongs to the connection and
    /// not to the individual connection attempt: its value arises precisely
    /// from the fact that it survives the reconnect.
    /// </summary>
    private readonly SaslMechanismPolicy _saslPolicy = new();

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _keepaliveTask;

    private int _messageIdCounter;
    private int _pepCounter;
    private int _reconnectAttempts;
    private bool _intentionalDisconnect;

    /// <summary>
    /// Set when the server ended the stream with a non-recoverable condition
    /// (RFC 6120, section 4.9). Suppresses the automatic reconnect, which would
    /// otherwise trigger the same error again. Reset on every deliberate
    /// connection attempt.
    /// </summary>
    private bool _fatalStreamError;

    #endregion

    #region Properties

    // Reconnect settings
    public int MaxReconnectAttempts { get; set; } = 5;
    public TimeSpan InitialReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

    // Keepalive - prevents the inactivity timeout from the server
    public TimeSpan KeepaliveInterval { get; set; } = TimeSpan.FromSeconds(25);
    public bool KeepaliveEnabled { get; set; } = true;

    /// <summary>
    /// The priority that every presence of this client carries (RFC 6121,
    /// section 4.7.2.3); <c>null</c> leaves the element out.
    /// </summary>
    /// <remarks>
    /// It is the only way for a client to say how much it is meant when a
    /// message goes to the account and not to it. Negative means: not at all -
    /// the device stays addressable directly and keeps out of the rest. The
    /// server goes by it (RFC 6121, section 8.5.2.1.1), and offline storage,
    /// too, is only delivered late to a resource with a non-negative priority
    /// (XEP-0160).
    ///
    /// The range is bounded to -128 up to +127; a value beside it gets clamped
    /// by the server rather than refused.
    /// </remarks>
    public int? PresencePriority { get; set; }

    // XEP-0198: Stream management. The earlier switching-off because of
    // "ejabberd compatibility problems" went back to the faulty counting. That
    // is fixed, tested against XMPPServer and by now evidenced against
    // Prosody 13: after a complete session setup both sides report the same
    // state, down to the counter.
    //
    // With that the reason for the switched-off default has fallen away.
    // Whoever does not want it switches it off - at runtime with /sm off. It is
    // requested anyway only when the server announces it; a server without
    // XEP-0198 notices nothing of this line.
    public bool StreamManagementEnabled { get; set; } = true;

    /// <summary>
    /// The weakest SASL mechanism that may still be used - null demands nothing
    /// and leaves the choice to the server's announcement alone.
    /// </summary>
    /// <remarks>
    /// Permitted are PLAIN, SCRAM-SHA-1 and SCRAM-SHA-256; another name is
    /// refused instead of silently demanding nothing at all. Whoever knows that
    /// their server can do SCRAM sets it here: then the lower bound takes hold
    /// already on the very first connection attempt, which
    /// <see cref="PinnedSaslMechanism"/> naturally cannot protect yet.
    /// </remarks>
    public string? MinimumSaslMechanism
    {
        get => _saslPolicy.Minimum;
        set => _saslPolicy.Minimum = value;
    }

    /// <summary>
    /// The mechanism the last login succeeded with - and thereby the lower
    /// bound for the next one. Null before the first.
    /// </summary>
    /// <remarks>
    /// If the server offers less afterwards, no connection comes about any
    /// more. That is intended: a server that could do SCRAM and suddenly offers
    /// only PLAIN has either been reconfigured or is not the same one at all
    /// any more.
    /// </remarks>
    public string? PinnedSaslMechanism => _saslPolicy.Pinned;

    /// <summary>
    /// The resource wished for during resource binding; null leaves the choice
    /// to the server (RFC 6120, section 7.6).
    /// </summary>
    /// <remarks>
    /// The default value comes from the console application and is really too
    /// narrow for a library - two users in the same process thereby wish for
    /// the same resource. It stays out of consideration for existing callers,
    /// but can now be set.
    /// </remarks>
    public string? Resource { get; set; } = $"console-{Environment.ProcessId}";

    /// <summary>
    /// Validation of the server certificate with <c>wss://</c>. Null leaves it
    /// to the operating system - the server then needs a certificate the
    /// machine trusts anyway.
    /// </summary>
    /// <remarks>
    /// Intended for certificates that no known CA has signed: a test server, a
    /// company's own CA, a pinned fingerprint. Whoever puts a validation in
    /// here that always returns true has reduced TLS to encryption without
    /// authentication - that helps against a recording, not against a man in
    /// the middle.
    /// </remarks>
    public RemoteCertificateValidationCallback? ServerCertificateValidator { get; set; }

    // State
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string FullJid { get; private set; } = string.Empty;
    public string BareJid => JidUtilities.Bare(FullJid);
    public string Domain => _domain;

    /// <summary>
    /// The endpoint that is connected to: the one given, the one found through
    /// XEP-0156 or the default value - in that order of precedence.
    /// </summary>
    public string WebSocketUri => _wsUri ?? _defaultWsUri;

    /// <summary>
    /// XEP-0156: What the endpoint is searched with when the caller has named
    /// none. Without one the <c>host-meta</c> of the domain is loaded over
    /// HTTPS.
    /// </summary>
    public AltConnectionsResolver? EndpointDiscovery { get; set; }
    public List<string> ServerFeatures { get; } = [];

    /// <summary>
    /// XEP-0352: Has the server announced client state indication?
    /// </summary>
    public bool SupportsClientStateIndication { get; private set; }

    /// <summary>
    /// XEP-0352: Is a human being looking right now? Default true - a stream
    /// always begins active (section 4.2).
    /// </summary>
    /// <remarks>
    /// The value outlasts a connection drop, the state on the server does not:
    /// per section 5.2 a resumed stream, too, begins active again. That is why
    /// the client declares itself inactive anew after every setup, as long as
    /// it is - the phone is, after all, still lying in the same pocket.
    /// </remarks>
    public bool ClientIsActive { get; private set; } = true;

    // Core Managers
    public Roster Roster { get; } = new();
    public ReceiptTracker Receipts { get; }
    public CarbonManager? Carbons { get; private set; }
    public PubSubManager? PubSub { get; private set; }

    // Advanced Managers (XEP-0030, 0115, 0198, 0199)
    public PingManager? Ping { get; private set; }
    public DiscoManager? Disco { get; private set; }
    public EntityCapsManager? EntityCaps { get; private set; }
    public StreamManagementManager? StreamManagement { get; private set; }

    #endregion

    #region Events

    // Events - Core
    /// <summary>
    /// A received message - fully assembled.
    /// </summary>
    /// <remarks>
    /// Here stood a list of individual values that grew longer with every
    /// extension: first five, with the delay stamp eight, with the correction
    /// nine. A row of alike strings whose meaning hangs on their position alone
    /// is a mix-up waiting for its opportunity.
    ///
    /// It is assembled here and not at the caller: <b>only here is the stanza
    /// still available.</b> That is exactly what the delay stamp went past -
    /// the caller set the time of day itself and could not possibly know that a
    /// different one stood in the stanza (see D59).
    /// </remarks>
    public event Action<XMPPMessage>? OnMessage;
    public event Action<string, string>? OnPresence;
    public event Action<string, ChatState>? OnChatState;
    public event Action<string, string>? OnReceiptReceived;
    public event Action<CarbonMessage>? OnCarbonMessage;
    public event Action<PubSubEvent>? OnPubSubEvent;

    /// <summary>
    /// Someone applies for a subscription to a node of our own (XEP-0060,
    /// section 8.6.1).
    /// </summary>
    /// <remarks>
    /// <b>An event and not a callback.</b> The application is answered with
    /// <see cref="PubSubAnswerSubscriptionRequestAsync"/>, and by whoever sees
    /// the report - a client that agreed of its own accord would decide about
    /// someone else's access by a rule nobody has seen.
    /// </remarks>
    public event Action<PubSubSubscribeAuthorization>? OnPubSubSubscriptionRequest;
    public event Action<string>? OnRawXml;
    public event Action<string>? OnError;
    public event Action<string>? OnSpoofingAttempt;
    public event Action<ConnectionState, ConnectionState>? OnStateChanged;

    // Events - Advanced
    public event Action<ChatMarker>? OnChatMarker;
    public event Action<string, DiscoInfo>? OnCapsDiscovered;

    /// <summary>
    /// RFC 6120, section 8.3: A stanza was refused by the peer. The first
    /// parameter is the sender of the error; it is null when the error came
    /// from one's own server.
    /// </summary>
    public event Action<string?, StanzaError>? OnStanzaError;

    /// <summary>
    /// RFC 6120, section 4.9: The server ended the stream with an error.
    /// Whether a reconnect follows is said by
    /// <see cref="StreamError.IsRecoverable"/>.
    /// </summary>
    public event Action<StreamError>? OnStreamError;

    #endregion

    #region Constructor(s)

    /// <summary>
    /// Creates a new WebSocket-based XMPP connection
    /// </summary>
    /// <param name="jid">Bare JID in the format user@domain</param>
    /// <param name="password">Password for the SASL authentication</param>
    /// <param name="wsUri">
    /// WebSocket endpoint. Without one the <c>host-meta</c> of the domain is
    /// asked before the first connect (XEP-0156); if none is found there, it
    /// stays at wss://{domain}:5443/ws (the ejabberd default).
    /// </param>
    /// <param name="LoggerFactory">Optional logger factory; without one nothing is logged</param>
    public XMPPConnection(string             jid,
                          string             password,
                          string?            wsUri           = null,
                          ILoggerFactory?    LoggerFactory   = null)
    {

        _jid       = jid;
        _password  = password;

        var parts  = jid.Split('@');
        if (parts.Length != 2)
            throw new ArgumentException("The JID has to be in the format 'user@domain'", nameof(jid));

        _username  = parts[0];
        _domain    = parts[1];

        // Kept apart: without one, the host-meta of the domain is asked before
        // the first connect (XEP-0156). Whoever names an endpoint is not asked
        // - the XEP is explicitly the fallback route, not the first address.
        _wsUri         = wsUri;
        _defaultWsUri  = $"wss://{_domain}:5443/ws";

        _loggerFactory  = LoggerFactory;
        _logger         = CreateLogger<XMPPConnection>();

        Receipts        = new ReceiptTracker(CreateLogger<ReceiptTracker>());
        Receipts.OnReceiptReceived += (msgId, from) => OnReceiptReceived?.Invoke(from, msgId);

    }

    #endregion


    private ILogger CreateLogger<T>()
    {

        if (_loggerFactory is null)
            return NullLogger<T>.Instance;

        return _loggerFactory.CreateLogger<T>();

    }


    /// <summary>
    /// Establishes the connection and logs in.
    /// </summary>
    /// <exception cref="AuthenticationException">
    /// The login was refused.
    /// </exception>
    /// <exception cref="XMPPProtocolException">
    /// The negotiation failed - through a timeout, for instance.
    /// </exception>
    /// <remarks>
    /// A failed setup <b>throws</b> and does not return silently. Until D31 it
    /// did exactly that: the error went to <c>OnError</c> and to the state, and
    /// whoever had subscribed to nothing saw no difference between succeeded
    /// and failed - and carried on working on a connection that does not exist.
    ///
    /// Thrown is the original error and not a shell around it: a wrong password
    /// is something other than a timeout, and the caller shall be able to tell
    /// them apart without reading a message.
    ///
    /// Only this route throws. The reconnect attempt in the background runs
    /// through the same <see cref="ConnectInternalAsync"/>, but has no caller
    /// it could owe anything to - it keeps reporting through events. That is
    /// why the decision stands here and not there.
    /// </remarks>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _intentionalDisconnect = false;
        _reconnectAttempts = 0;

        // A deliberate connection attempt lifts the lock from an earlier stream
        // error: the caller knows what they are doing.
        _fatalStreamError = false;

        _lastConnectError = null;

        await ConnectInternalAsync(ct);

        if (State == ConnectionState.Connected)
            return;

        // Rethrow the original error with its own stack - do not repackage it.
        // For the caller the interesting place is the one where it went wrong,
        // and not this one here.
        if (_lastConnectError is not null)
            ExceptionDispatchInfo.Capture(_lastConnectError).Throw();

        // Without a recorded error only the finding itself remains: that
        // happens when the reconnect attempts are used up without the last
        // attempt having even begun.
        throw new XMPPProtocolException(
                  $"Establishing the connection to {WebSocketUri} failed, state: {State}.");

    }

    /// <summary>
    /// Searches for the endpoint through XEP-0156, in case the caller has named
    /// none.
    /// </summary>
    /// <remarks>
    /// <b>At most once per connection, across reconnects too.</b> The reconnect
    /// attempt runs in a loop; one query per pass would mean, with a server
    /// that is currently away, waiting twenty times for an HTTPS answer that
    /// does not exist.
    ///
    /// If the search stays without a result, it is not repeated and the default
    /// value stays. That is the order of precedence of the XEP: the discovery
    /// is the fallback route, and a fallback route that fails itself must not
    /// hold up the connection setup.
    /// </remarks>
    private async Task DiscoverEndpointAsync(CancellationToken ct)
    {

        if (_wsUri is not null || _endpointDiscovered)
            return;

        _endpointDiscovered = true;

        var found = await (EndpointDiscovery ?? new AltConnectionsResolver()).
                              DiscoverWebSocketAsync(_domain, ct);

        if (found is not null)
        {
            _logger.LogInformation("XEP-0156: {WebSocketUri} from the host-meta of {Domain}",
                                   found, _domain);
            _wsUri = found;
        }

        else
            _logger.LogDebug("XEP-0156: no WebSocket endpoint for {Domain}, it stays at {WebSocketUri}",
                             _domain, _defaultWsUri);

    }

    /// <summary>
    /// Ends the receive and keepalive loop of the current connection, waits for
    /// their end and releases the CancellationTokenSource and the socket.
    /// </summary>
    /// <remarks>
    /// Without this teardown a reconnect overwrites the old
    /// CancellationTokenSource without cancelling it: the loops of the previous
    /// connection then keep running, reach the new socket through the fields
    /// and accumulate with every reconnect.
    /// </remarks>
    private async Task ShutdownConnectionAsync()
    {

        var cts           = _cts;
        var receiveTask   = _receiveTask;
        var keepaliveTask = _keepaliveTask;
        var webSocket     = _webSocket;

        _cts           = null;
        _receiveTask   = null;
        _keepaliveTask = null;
        _webSocket     = null;

        CancelPendingIqs();

        if (cts is null && webSocket is null)
            return;

        if (cts is not null)
        {
            try
            {
                await cts.CancelAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Cancelling the background tasks failed (ignored)");
            }
        }

        var pending = new List<Task>(2);
        if (receiveTask   is not null) pending.Add(receiveTask);
        if (keepaliveTask is not null) pending.Add(keepaliveTask);

        if (pending.Count > 0)
        {
            try
            {
                // Wait, so that the old loops no longer touch the new socket.
                await Task.WhenAll(pending).WaitAsync(ShutdownTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Background tasks not ended within {Timeout}s",
                                 ShutdownTimeout.TotalSeconds);
            }
        }

        cts?.Dispose();
        webSocket?.Dispose();

    }

    private async Task ConnectInternalAsync(CancellationToken ct)
    {

        // Clear away the remains of a previous connection before a new one comes into being.
        await ShutdownConnectionAsync();

        SetState(ConnectionState.Connecting);

        await DiscoverEndpointAsync(ct);

        try
        {
            // Connect the WebSocket
            var webSocket = new ClientWebSocket();
            webSocket.Options.AddSubProtocol("xmpp");  // RFC 7395

            if (ServerCertificateValidator is not null)
                webSocket.Options.RemoteCertificateValidationCallback = ServerCertificateValidator;

            _webSocket = webSocket;

            _logger.LogInformation("Connecting to {WebSocketUri} ...", WebSocketUri);

            // The endpoint belongs in the exception, and only here. What the
            // transport throws reads "Unable to connect to the remote server"
            // and does not say where to - since XEP-0156 (D41) the address does
            // not even have to come from the caller any more, and then it
            // stands in no source text they could read.
            //
            // This is not a retreat from D31: there it is about the *stack* of
            // the original error, and that one is without value here (it ends
            // in ClientWebSocket.ConnectAsync). The exception is preserved as
            // the InnerException; the negotiation and login errors after it are
            // not touched.
            try
            {
                await webSocket.ConnectAsync(new Uri(WebSocketUri), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new XMPPProtocolException(
                          $"Establishing the connection to {WebSocketUri} failed: {ex.Message}",
                          ex);
            }

            _logger.LogInformation("WebSocket connected");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // ===== Negotiation =====
            //
            // Up to the resource binding this section reads from the socket
            // itself. That is right here: the server has no resource yet that
            // it could route anything to, so nothing else can arrive than the
            // negotiation itself.

            await SendAsync(OpenStream());

            var features   = await ReceiveFeaturesAsync(ct);
            var mechanisms = StreamNegotiation.SaslMechanisms(features);

            if (mechanisms.Count > 0)
            {
                _logger.LogDebug("Available SASL mechanisms: {Mechanisms}", string.Join(", ", mechanisms));
            }

            // SASL auth - preference: SCRAM-SHA-256 > SCRAM-SHA-1 > PLAIN
            var chosen = SaslMechanismPolicy.Strongest(mechanisms);

            if (chosen is null)
                throw new AuthenticationException(
                          mechanisms.Count > 0
                              ? $"No supported SASL mechanisms. Available: {string.Join(", ", mechanisms)}"
                              : "The server offers no SASL mechanisms. Features: " +
                                Shorten(features.ToString(), 200));

            // The lower bound is checked before the first frame goes out, not
            // afterwards: with PLAIN the password stands in exactly this
            // <auth/>. Whoever notices the downgrade only from the answer has
            // already given it to the man in the middle.
            _saslPolicy.EnsureAcceptable(chosen);

            _logger.LogInformation("{Mechanism} authentication ...", chosen);

            switch (chosen)
            {

                case SaslMechanismPolicy.ScramSha256:
                    await PerformScramAsync(SCRAMMechanism.ScramSha256, ct);
                    break;

                case SaslMechanismPolicy.ScramSha1:
                    await PerformScramAsync(SCRAMMechanism.ScramSha1, ct);
                    break;

                case SaslMechanismPolicy.Plain:
                    // PLAIN transmits the password in the clear (protected only by TLS)
                    // and is the weakest mechanism supported here.
                    _logger.LogWarning("SASL PLAIN authentication - the server offers no SCRAM");
                    await PerformSaslPlainAsync(ct);
                    break;

                // A mechanism that stands in the ranking but has no procedure
                // here is a mistake in this file - and not one that may fall
                // back to PLAIN.
                default:
                    throw new AuthenticationException(
                              $"For the chosen mechanism {chosen} no procedure is on file.");

            }

            // Only now, after the successful login.
            _saslPolicy.Remember(chosen);

            // Open a new stream after auth (RFC 6120, section 6.4.6)
            await SendAsync(OpenStream());
            features = await ReceiveFeaturesAsync(ct);

            ServerFeatures.Clear();
            ServerFeatures.AddRange(StreamNegotiation.FeatureNamespaces(features));

            SupportsClientStateIndication = StreamNegotiation.OffersClientStateIndication(features);

            // XEP-0198, section 5: the attempt to tie in with the earlier
            // stream belongs exactly here - after the login, before the
            // binding. If it succeeds, there is no new resource: the old full
            // JID still holds, and everything addressed to it since the drop
            // comes after.
            var resumed = await TryResumeAsync(features, ct);

            if (!resumed && StreamNegotiation.OffersBind(features))
            {
                _logger.LogDebug("Resource binding ...");
                FullJid = await PerformBindAsync(ct);
                _logger.LogInformation("Connected as {FullJid}", FullJid);
            }

            // ===== From here on the session is usable =====
            //
            // The managers come into being before the receive loop: as soon as
            // the resource is bound, the server may deliver, and the first
            // stanza can arrive before the next line runs.

            InitialiseManagers();

            // The receive loop gets its socket handed to it explicitly, so that
            // after a reconnect it does not hang on the new socket.
            _receiveTask = ReceiveLoopAsync(webSocket, _cts.Token);

            // A resumed stream is not a new session: session, stream
            // management, carbons, roster and presence are all in place
            // already. Going through them once more would not merely be
            // superfluous - a second presence would announce the resource anew,
            // and to the contacts it would look like the return that the
            // resumption is meant to avoid in the first place.
            if (!resumed)
            {

                // Session (if necessary - dropped in RFC 6121)
                if (StreamNegotiation.RequiresSession(features))
                    await PerformSessionAsync(ct);

                // XEP-0198: stream management, on by default. The counting is
                // evidenced against Prosody 13 (ProsodyStreamManagementTests);
                // the reason for the earlier switching-off - a faulty counting
                // - no longer exists.
                //
                // With resumption: it costs nothing as long as it is not
                // needed, and without it every drop throws the unacknowledged
                // stanzas away.
                if (StreamManagementEnabled && StreamNegotiation.OffersStreamManagement(features))
                {
                    _logger.LogInformation("Enabling stream management ...");

                    if (!await StreamManagement!.NegotiateAsync(requestResume: true, SetupTimeout, ct))
                        _logger.LogWarning("Stream management refused by the server");
                }

                // Enable carbons
                _logger.LogDebug("Enabling message carbons ...");
                await EnableCarbonsAsync(ct);

                // Load the roster
                _logger.LogDebug("Loading the roster ...");
                await RequestRosterAsync(StreamNegotiation.OffersRosterVersioning(features), ct);

                // Go online
                await SendPresenceAsync();

            }

            else
                await ResendUnackedAsync();

            // XEP-0352, section 5.2: "stream resumption does not affect the
            // current CSI state, which always defaults to 'active' for new and
            // resumed streams". The server has thus forgotten the state, but
            // the device is still lying in the pocket - hence here and outside
            // the branch above: it holds for the newly bound as well as for the
            // resumed stream.
            if (!ClientIsActive && SupportsClientStateIndication)
                await SendAsync(ClientStateIndication.InactiveXml);

            SetState(ConnectionState.Connected);
            _reconnectAttempts = 0;
            _logger.LogInformation("Online");

            // Start the keepalive loop (prevents the server timeout)
            if (KeepaliveEnabled)
            {
                _logger.LogDebug("Starting keepalive (interval: {Seconds}s) ...", KeepaliveInterval.TotalSeconds);
                _keepaliveTask = KeepaliveLoopAsync(_cts.Token);
            }
        }
        catch (AuthenticationException ex)
        {
            // Auth errors are permanent - no reconnect makes sense
            _lastConnectError = ex;
            SetState(ConnectionState.Disconnected);
            _logger.LogError(ex, "Authentication error");
            OnError?.Invoke($"Authentication error: {ex.Message}");
            // NO reconnect on auth errors!
        }
        catch (Exception ex)
        {
            _lastConnectError = ex;
            SetState(ConnectionState.Disconnected);
            _logger.LogError(ex, "Connection error");
            OnError?.Invoke($"Connection error: {ex.Message}");

            if (!_intentionalDisconnect)
            {
                await TryReconnectAsync(ct);
            }
        }
    }

    /// <summary>The stream header per RFC 7395.</summary>
    private string OpenStream()
        => $"<open xmlns='{StreamNegotiation.FramingNamespace}' " +
           $"to='{XmlEscaping.Escape(_domain)}' version='1.0'/>";

    /// <summary>
    /// Reads the next frame of the negotiation and returns it parsed.
    /// </summary>
    /// <param name="expected">
    /// What is being waited for - appears in the message when the deadline
    /// expires. An expired deadline without it only shifts the search: the
    /// caller then knows that something did not come, but not what.
    /// </param>
    private async Task<XElement> ReceiveElementAsync(CancellationToken ct,
                                                     string            expected = "the negotiation")
    {

        var xml = await ReceiveStanzaAsync(ct, expected);

        try
        {
            return XElement.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new XMPPProtocolException(
                      $"A frame of the negotiation is not well-formed XML: {ex.Message}", ex);
        }

    }

    /// <summary>
    /// Waits for the stream features. Whether the server sends
    /// <c>&lt;open/&gt;</c> and <c>&lt;features/&gt;</c> in one frame or in two
    /// is left to it.
    /// </summary>
    private async Task<XElement> ReceiveFeaturesAsync(CancellationToken ct)
    {

        var element = await ReceiveElementAsync(ct, "the stream header");

        if (StreamNegotiation.IsStreamOpen(element))
            element = await ReceiveElementAsync(ct, "the stream features");

        if (!StreamNegotiation.IsFeatures(element))
            throw new XMPPProtocolException(
                      $"Expected were the stream features, received was <{element.Name.LocalName}/>.");

        return element;

    }

    /// <summary>
    /// XEP-0198, section 5: Tries to tie in with the earlier stream.
    /// </summary>
    /// <remarks>
    /// Read is still directly from the socket here, as in the whole section of
    /// the negotiation: the receive loop only runs once the session is in
    /// place. A detour through it would also be wrong in substance - as long as
    /// it is not settled whether this stream is the old one, there is nobody a
    /// stanza could be delivered to.
    ///
    /// A <c>&lt;failed/&gt;</c> is not an error but the normal case after a
    /// longer disturbance. The caller then binds a new resource.
    /// </remarks>
    /// <returns>true when the old stream carries on.</returns>
    private async Task<bool> TryResumeAsync(XElement features, CancellationToken ct)
    {

        if (!StreamManagementEnabled ||
            StreamManagement?.CanResume != true ||
            !StreamNegotiation.OffersStreamManagement(features))
            return false;

        _logger.LogInformation("Trying to resume the stream ...");

        await StreamManagement.ResumeAsync();

        var answer = await ReceiveElementAsync(ct);
        var name   = answer.Name.LocalName;

        if (name == "resumed")
        {
            StreamManagement.ProcessResumed(answer.ToString());
            _logger.LogInformation("Stream resumed as {FullJid}", FullJid);
            return true;
        }

        // Anything other than a <resumed/> means: the old stream is gone.
        // ProcessFailed clears away the identifier and reports what was lost in
        // the process - without that the next reconnect would try it again with
        // an identifier the server has long forgotten.
        if (name != "failed")
            _logger.LogWarning("Unexpected answer to <resume/>: <{Name}/>", name);

        // Together with the frame: a <failed h='…'/> names the state of the old
        // stream, and what the server has processed is not lost.
        StreamManagement.ProcessFailed(answer.ToString());

        return false;

    }

    /// <summary>
    /// Creates the XEP managers for this connection.
    /// </summary>
    /// <remarks>
    /// Has to run before the receive loop starts: <c>ProcessStanza</c> reaches
    /// all of them, and after the resource binding the server may deliver at
    /// any time.
    /// </remarks>
    private void InitialiseManagers()
    {

        // XEP-0198, section 5: this one manager survives the reconnect. On it
        // hang the identifier of the preserved stream and the stanzas not yet
        // acknowledged - were it created anew here like the rest, both would be
        // gone after a drop, and the resumption would have nothing to tie in
        // with. It resets its session state itself as soon as an <enabled/>
        // arrives.
        if (StreamManagement is null)
        {
            StreamManagement = new StreamManagementManager(xml => SendAsync(xml), CreateLogger<StreamManagementManager>());
            StreamManagement.OnAckReceived += count =>
                _logger.LogTrace("Stream management: {Count} stanzas acknowledged", count);
        }

        Carbons = new CarbonManager(BareJid);
        Carbons.OnCarbonReceived += c => OnCarbonMessage?.Invoke(c);
        Carbons.OnParseError     += msg => OnError?.Invoke($"[Carbon] {msg}");

        PubSub = new PubSubManager($"pubsub.{_domain}", CreateLogger<PubSubManager>());
        PubSub.OnEvent += e => OnPubSubEvent?.Invoke(e);

        // XEP-0199: Ping Manager
        Ping = new PingManager(xml => SendAsync(xml));
        Ping.OnPingTimeout += target => OnError?.Invoke($"Ping timeout: {target}");

        // XEP-0030: Service Discovery
        Disco = new DiscoManager(xml => SendAsync(xml));

        // XEP-0115: Entity Capabilities
        EntityCaps = new EntityCapsManager(Disco);
        EntityCaps.OnCapsDiscovered += (from, info) => OnCapsDiscovered?.Invoke(from, info);

    }

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max];

    private async Task TryReconnectAsync(CancellationToken ct)
    {
        if (_intentionalDisconnect || _reconnectAttempts >= MaxReconnectAttempts)
        {
            _logger.LogWarning("Reconnect given up after {Attempts} attempts", _reconnectAttempts);
            return;
        }

        _reconnectAttempts++;

        // Exponential backoff
        var delay = TimeSpan.FromMilliseconds(
            Math.Min(
                InitialReconnectDelay.TotalMilliseconds * Math.Pow(2, _reconnectAttempts - 1),
                MaxReconnectDelay.TotalMilliseconds
            )
        );

        SetState(ConnectionState.Reconnecting);
        _logger.LogInformation("Reconnect attempt {Attempt}/{Max} in {Seconds:F1}s ...",
                               _reconnectAttempts, MaxReconnectAttempts, delay.TotalSeconds);

        try
        {
            await Task.Delay(delay, ct);
            await ConnectInternalAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconnect failed");
            OnError?.Invoke($"Reconnect failed: {ex.Message}");
        }
    }

    private void SetState(ConnectionState newState)
    {
        var oldState = State;
        if (oldState != newState)
        {
            State = newState;
            _logger.LogDebug("Connection state: {OldState} -> {NewState}", oldState, newState);
            OnStateChanged?.Invoke(oldState, newState);
        }
    }

    // ===== WEBSOCKET I/O =====

    /// <summary>
    /// XEP-0198, section 5: After a resumption, sends afterwards what the old
    /// stream did not get acknowledged any more.
    /// </summary>
    /// <remarks>
    /// Without counting along: these stanzas already carry their sequence
    /// number and remain in the queue until the server acknowledges them.
    /// Whoever counted them again while sending them after would shift their
    /// outgoing counter against the incoming counter of the peer - and from
    /// then on every <c>&lt;a h='…'/&gt;</c> would acknowledge the wrong
    /// stanzas.
    /// </remarks>
    private async Task ResendUnackedAsync()
    {

        var open = StreamManagement?.GetUnackedStanzas() ?? [];

        if (open.Count == 0)
            return;

        _logger.LogInformation("Sending {Count} unacknowledged stanzas afterwards", open.Count);

        foreach (var stanza in open)
            await SendAsync(stanza, track: false);

        // And afterwards ask for an acknowledgement.
        //
        // Without that the queue stays put. The <resumed h='…'/> has only
        // emptied it up to the state of the server; what was open beyond that
        // has just gone out once more and is now waiting for an <a/> that never
        // comes by itself: the server acknowledges when it is asked, and the
        // keepalive only asks when it is switched on. A disturbance thereby
        // turned into a queue that does not become empty again until the end of
        // the session - and went out completely once more at every further
        // resumption.
        await StreamManagement!.RequestAckAsync();

    }

    private async Task SendAsync(string xml, bool track = true)
    {

        // RFC 7395, section 3.3.3: over WebSocket there is no enclosing
        // <stream:stream> from which a stanza could inherit its namespace - it
        // has to carry it itself. Here and not at the roughly 25 callers, for
        // the same reason that counting happens here too: this is the only
        // place every outgoing frame runs through.
        xml = StanzaNamespace.Apply(xml, StanzaNamespace.Client);

        // Hold on to the socket locally: the field can be swapped during a
        // reconnect while we are still waiting for the lock.
        var webSocket = _webSocket;

        if (webSocket?.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket not connected");

        var bytes = Encoding.UTF8.GetBytes(xml);
        var token = _cts?.Token ?? CancellationToken.None;

        await _sendLock.WaitAsync(token);

        try
        {

            // Check again after the wait - the connection may have been closed
            // in the meantime.
            if (webSocket.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket not connected");

            await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, token);

            // XEP-0198: this is the only place every outgoing stanza runs
            // through - which is why counting happens here and not at the
            // roughly 25 callers. Only after the successful send, so that a
            // failed stanza does not shift the counter permanently, and still
            // under the send lock, so that the sequence numbers match the order
            // on the wire.
            if (track)
                StreamManagement?.TrackOutgoing(xml);

        }
        finally
        {
            _sendLock.Release();
        }

        _logger.LogTrace(">>> {Xml}", xml);
        OnRawXml?.Invoke($">>> {xml}");

    }

    /// <summary>
    /// Sends an IQ and waits for the answer with the same id.
    /// </summary>
    /// <remarks>
    /// The same procedure that <see cref="DiscoManager"/> and
    /// <see cref="PingManager"/> already use: the answer comes in through the
    /// receive loop and is assigned by its id, instead of the waiting party
    /// reading from the socket itself.
    /// </remarks>
    /// <returns>The answer, or null on a timeout.</returns>
    private async Task<XElement?> SendIqAsync(string             id,
                                              string             xml,
                                              CancellationToken  ct)
    {

        // RunContinuationsAsynchronously: the answer is delivered on the thread
        // of the receive loop; without this the waiting setup would carry on
        // there and hold up the reading of the next stanzas.
        var tcs = new TaskCompletionSource<XElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_iqLock)
            _pendingIqs[id] = tcs;

        try
        {

            await SendAsync(xml);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(SetupTimeout);

            return await tcs.Task.WaitAsync(cts.Token);

        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("No answer to IQ '{Id}' within {Seconds}s",
                               id, SetupTimeout.TotalSeconds);
            return null;
        }
        finally
        {
            lock (_iqLock)
                _pendingIqs.Remove(id);
        }

    }

    /// <summary>
    /// Delivers an IQ answer to the waiting party, if there is one.
    /// </summary>
    private bool TryCompleteIq(string id, XElement element)
    {

        TaskCompletionSource<XElement>? tcs;

        lock (_iqLock)
        {
            if (!_pendingIqs.TryGetValue(id, out tcs))
                return false;

            _pendingIqs.Remove(id);
        }

        return tcs.TrySetResult(element);

    }

    /// <summary>
    /// Cancels all open IQ requests. Without that a reconnect would first wait
    /// out their timeout, although the answer cannot possibly come over the old
    /// socket any more.
    /// </summary>
    private void CancelPendingIqs()
    {

        List<TaskCompletionSource<XElement>> pending;

        lock (_iqLock)
        {
            pending = [.. _pendingIqs.Values];
            _pendingIqs.Clear();
        }

        foreach (var tcs in pending)
            tcs.TrySetCanceled();

    }

    private async Task<string> ReceiveStanzaAsync(CancellationToken ct, string expected = "the negotiation")
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();

        // One deadline for the step, not for the individual read: a frame that
        // arrives in pieces must not take longer altogether than one in a
        // single piece.
        //
        // Without it the negotiation waited indefinitely. An error arrives, a
        // closed socket arrives - silence does not arrive, and then ConnectAsync
        // never returned. This showed up in five mutations across D25 to D29,
        // all of which made the run hang instead of letting it fail: a result
        // that is none.
        //
        // The resource binding was never affected - SendIqAsync has had its
        // deadline all along. Affected was everything before it that reads here.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SetupTimeout);

        WebSocketReceiveResult result;
        do
        {

            try
            {
                result = await _webSocket!.ReceiveAsync(buffer, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new XMPPProtocolException(
                          $"Timeout in the negotiation: to {expected} came no answer " +
                          $"within {SetupTimeout.TotalSeconds:0} seconds.");
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new IOException("WebSocket closed");
            }

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);

        var xml = sb.ToString();
        _logger.LogTrace("<<< {Xml}", xml);
        OnRawXml?.Invoke($"<<< {xml}");
        NoteInboundStanza(xml);
        return xml;
    }

    /// <summary>
    /// XEP-0198: counts a received stanza.
    ///
    /// Sits deliberately on both receive paths. On the direct path there is
    /// nothing left to count today - <see cref="ReceiveStanzaAsync"/> only
    /// reads the negotiation any more, and that ends before
    /// <c>&lt;enabled/&gt;</c> - but the assurance "every received stanza comes
    /// past here" shall not depend on where the boundary between the two paths
    /// currently runs.
    /// </summary>
    private void NoteInboundStanza(string xml)
    {
        StreamManagement?.TrackIncoming(xml);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket webSocket, CancellationToken ct)
    {
        var buffer = new byte[8192];

        try
        {
            while (!ct.IsCancellationRequested && webSocket.State == WebSocketState.Open)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;

                do
                {
                    result = await webSocket.ReceiveAsync(buffer, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogWarning("The server has closed the connection");
                        break;
                    }

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                var stanza = sb.ToString();
                if (!string.IsNullOrEmpty(stanza))
                {
                    _logger.LogTrace("<<< {Xml}", stanza);
                    OnRawXml?.Invoke($"<<< {stanza}");
                    NoteInboundStanza(stanza);
                    ProcessStanza(stanza);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal
        }
        catch (WebSocketException ex)
        {
            _logger.LogError(ex, "WebSocket error");
            OnError?.Invoke($"WebSocket error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Receive error");
            OnError?.Invoke($"Receive error: {ex.Message}");
        }

        // A loop of an already superseded connection must not trigger a
        // reconnect any more.
        if (!ReferenceEquals(webSocket, _webSocket))
        {
            _logger.LogDebug("Receive loop of a superseded connection ended");
            return;
        }

        // Connection lost - try a reconnect.
        // Deliberately decoupled through Task.Run: the reconnect clears away,
        // among other things, this very loop via ShutdownConnectionAsync and
        // would otherwise wait on itself.
        if (_fatalStreamError)
        {
            _logger.LogDebug("No reconnect after a non-recoverable stream error");
            SetState(ConnectionState.Disconnected);
            return;
        }

        if (!_intentionalDisconnect && State == ConnectionState.Connected)
        {
            SetState(ConnectionState.Disconnected);
            _ = Task.Run(() => TryReconnectAsync(CancellationToken.None));
        }
    }

    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        _logger.LogDebug("Keepalive loop started (interval: {Seconds}s)", KeepaliveInterval.TotalSeconds);

        try
        {
            while (!ct.IsCancellationRequested && State == ConnectionState.Connected)
            {
                await Task.Delay(KeepaliveInterval, ct);

                if (State != ConnectionState.Connected)
                {
                    _logger.LogDebug("Keepalive stopped - no longer connected");
                    break;
                }

                // Preferred: stream management <r/> (less overhead)
                if (StreamManagement?.IsEnabled == true)
                {
                    _logger.LogTrace("Keepalive: sending stream management <r/>");
                    await StreamManagement.RequestAckAsync();
                }
                // Fallback: XEP-0199 ping
                else if (Ping != null)
                {
                    _logger.LogTrace("Keepalive: sending ping");
                    var rtt = await Ping.PingAsync(ct: ct);
                    if (rtt.HasValue)
                        _logger.LogTrace("Keepalive: pong after {Milliseconds:F0}ms", rtt.Value.TotalMilliseconds);
                    else
                        _logger.LogWarning("Keepalive: ping timeout");
                }
                else
                {
                    _logger.LogWarning("Keepalive: neither stream management nor ping available");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Keepalive loop ended (cancelled)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Keepalive error");
            OnError?.Invoke($"Keepalive error: {ex.Message}");
        }
    }

    // ===== STANZA PROCESSING =====

    /// <summary>
    /// Takes a received frame apart and passes it on.
    ///
    /// The frame is parsed exactly once; the further processing works on the
    /// <see cref="XElement"/>. The earlier detection through
    /// <c>StartsWith</c> failed on valid spellings: a prefix bound to
    /// <c>jabber:client</c> (<c>&lt;c:message/&gt;</c>) made the stanza fall
    /// through completely, and <c>StartsWith("&lt;a")</c> also hit
    /// <c>&lt;auth/&gt;</c>.
    ///
    /// The raw text is additionally passed through, because the XEP managers
    /// still expect it - their conversion is outstanding.
    /// </summary>
    private void ProcessStanza(string stanza)
    {
        try
        {
            // XEP-0198: the counting along deliberately does not happen here
            // but in NoteInboundStanza on both receive paths.

            XElement element;

            try
            {
                element = XElement.Parse(stanza, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException ex)
            {
                // Not well-formed - in practice above all a <stream:error/>
                // whose prefix the server declared only on the stream root. For
                // that the text path remains.
                _logger.LogWarning("The stanza is not well-formed XML: {Reason}", ex.Message);

                if (StreamError.TryParse(stanza, out var rawStreamError) && rawStreamError is not null)
                    ProcessStreamError(rawStreamError);
                else
                    OnError?.Invoke($"The stanza is not well-formed XML: {ex.Message}");

                return;
            }

            var name = element.Name.LocalName;
            var ns   = element.Name.NamespaceName;

            switch (name)
            {

                case "message":
                    ProcessMessage(element);
                    return;

                case "presence":
                    ProcessPresence(element);
                    return;

                case "iq":
                    ProcessIq(element);
                    return;

                case "close":
                    _logger.LogWarning("Stream closed by the server");
                    OnError?.Invoke("Stream closed by the server");
                    return;

                // RFC 6120, section 4.9: a stream error. After it the stream is dead.
                case "error" when ns == StreamNamespace:
                    if (StreamError.TryParse(stanza, out var streamError) && streamError is not null)
                        ProcessStreamError(streamError);
                    return;

                // XEP-0198: stream management. Now checked through the
                // namespace instead of through the initial letter.
                case "enabled" when ns == StreamManagementNamespace:
                    StreamManagement?.ProcessEnabled(stanza);
                    return;

                case "a" when ns == StreamManagementNamespace:
                    StreamManagement?.ProcessAck(stanza);
                    return;

                case "r" when ns == StreamManagementNamespace:
                    _ = StreamManagement?.ProcessRequestAsync();
                    return;

                case "resumed" when ns == StreamManagementNamespace:
                    StreamManagement?.ProcessResumed(stanza);
                    return;

                case "failed" when ns == StreamManagementNamespace:
                    StreamManagement?.ProcessFailed(stanza);
                    return;

                default:
                    _logger.LogDebug("Unhandled frame <{Name}/> from {Namespace}", name, ns);
                    return;

            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stanza processing failed");
            OnError?.Invoke($"Stanza processing failed: {ex.Message}");
        }
    }

    /// <summary>
    /// RFC 6120, section 4.9: After a stream error the server closes the stream
    /// immediately. A reconnect is only worthwhile with temporary conditions -
    /// with everything else it would run into the same refusal and produce a
    /// loop.
    /// </summary>
    private void ProcessStreamError(StreamError streamError)
    {

        if (streamError.IsRecoverable)
            _logger.LogWarning("Stream error from the server: {Error} - a reconnect is attempted", streamError);

        else
        {
            _logger.LogError("Stream error from the server: {Error} - no reconnect", streamError);

            // Prevents the receive loop from starting a reconnect right away;
            // the error would only repeat itself.
            _fatalStreamError = true;
        }

        OnStreamError?.Invoke(streamError);
        OnError?.Invoke($"Stream error: {streamError}");

    }

    private void ProcessMessage(XElement element)
    {
        var from = element.Attr("from") ?? "unknown";
        var to = element.Attr("to") ?? FullJid;
        var msgId = element.Attr("id");

        // RFC 6120, section 8.3: An error stanza carries no payload but the
        // reason. Previously it ran through as an ordinary message.
        if (element.Attr("type") == "error")
        {

            var parsed = StanzaError.TryParse(element.ToString(), out var stanzaError) && stanzaError is not null
                             ? stanzaError
                             : new StanzaError(StanzaErrorType.Cancel, "undefined-condition");

            _logger.LogDebug("Message to {From} refused: {Error}", from, parsed);

            OnStanzaError?.Invoke(from, parsed);
            return;

        }

        // XEP-0384: arrived encrypted.
        //
        // Before everything else, because what stands here cannot be seen from
        // the outside: the stanza has no <body/>, and every evaluation after
        // this would take it for empty.
        if (element.Attr("from") is String sender && TryProcessEncrypted(element, sender))
            return;

        // XEP-0060/XEP-0163: a PubSub notification.
        //
        // In practice it almost always comes as a <message type='headline'/> -
        // and up to here was only handled in ProcessIq, where it as good as
        // never arrives. The comment there claimed all along "can come as a
        // message or an iq"; the message half did not exist. It only showed up
        // when, with OMEMO, someone depended on a notification for the first
        // time - the same half-wired corner as in D38.
        if (element.Child(PubSubManager.EventNamespace, "event") is not null &&
            element.Attr("from") is not null)
        {

            PubSub?.ProcessEvent(element, from, PubSub.PubSubService);

            _ = ProcessPepEventAsync(element, from);

            return;

        }

        // XEP-0060, section 8.6.1: An application for a subscription to a node
        // of our own.
        //
        // <b>Only passed on and not answered.</b> Whoever agrees is a human
        // being; this client shows them the question and waits. A client that
        // answered of its own accord would decide about someone else's access
        // by a rule nobody has seen.
        if (element.Child(DataForm.Namespace, "x") is { } form &&
            PubSubSubscribeAuthorization.TryReadRequest(form, out var application))
        {

            _logger.LogInformation("PubSub: {Who} applies for {Node}", application!.SubscriberJid, application.NodeId);

            OnPubSubSubscriptionRequest?.Invoke(application);

            return;

        }

        // XEP-0280 and XEP-0384 together: A carbon brings along the message of
        // another device of our own, and that one can be encrypted.
        //
        // Everything that would hold for the outer message holds for the
        // wrapped one - except the sender: if one's own address stands on the
        // outside, the message comes from one's own account, and the real
        // sender stands on the inside.
        //
        // Without this branch one's own second device does not see what the
        // first one wrote: the key entry is there, the message arrives - and
        // nobody looks at it, because it sits in the <forwarded/>.
        if (Omemo is not null &&
            element.HasNamespace(CarbonManager.Namespace) &&
            element.Descendants()
                   .FirstOrDefault(e => e.Name.LocalName     == "forwarded" &&
                                        e.Name.NamespaceName == "urn:xmpp:forward:0")
                  ?.Elements()
                   .FirstOrDefault(e => e.Name.LocalName == "message") is XElement wrapped &&
            (wrapped.Attr("from") ?? wrapped.Attr("to")) is String innerSender &&
            TryProcessEncrypted(wrapped, innerSender))
        {
            return;
        }

        // XEP-0280: Carbon check
        if (element.HasNamespace(CarbonManager.Namespace))
        {
            if (Carbons != null)
            {
                var result = Carbons.ProcessCarbon(element, from);

                switch (result)
                {
                    case CarbonResult.Success:
                        return; // the carbon was processed

                    case CarbonResult.SpoofingDetected:
                        _logger.LogWarning("Carbon spoofing from {From}", from);
                        OnSpoofingAttempt?.Invoke($"Carbon spoofing from {from}");
                        return;

                    case CarbonResult.ParseError:
                        _logger.LogError("Carbon parse error from {From}", from);
                        OnError?.Invoke($"Carbon parse error from {from}");
                        return;

                    case CarbonResult.NotACarbon:
                        // Not a carbon, carry on processing it as an ordinary message
                        break;
                }
            }
        }

        // XEP-0333: Chat Markers
        var chatMarker = ChatMarkers.Parse(element, from);
        if (chatMarker != null)
        {
            OnChatMarker?.Invoke(chatMarker);
            return;
        }

        // XEP-0184: Receipt
        var receiptId = ReceiptBuilder.ExtractReceiptId(element);
        if (receiptId != null)
        {
            if (!Receipts.ProcessReceipt(receiptId, from))
                OnSpoofingAttempt?.Invoke($"Receipt spoofing: {receiptId} from {from}");
            return;
        }

        // XEP-0085: Chat State
        var chatState = ChatStateExtensions.ParseChatState(element);
        if (chatState.HasValue)
        {
            OnChatState?.Invoke(from, chatState.Value);
        }

        // An ordinary message. Only direct children: a forwarded message in
        // <forwarded/> brings along its own <body/>.
        var body = element.ChildValue("body");
        if (!string.IsNullOrEmpty(body))
        {

            var messageType = MessageTypeExtensions.Parse(element.Attr("type"));

            // XEP-0203: If it was held, its own time holds and not the one of
            // the reception. The stamp only stands on the outer stanza - hence
            // here and not in the carbon branch, which brings along its own
            // inner message.
            var received = DateTime.Now;
            var written  = DelayedDelivery.TryRead(element, out var stamp, out var heldBy)
                               ? stamp.ToLocalTime().DateTime
                               : received;

            OnMessage?.Invoke(new XMPPMessage(from,
                                              to,
                                              body,
                                              msgId,
                                              written,
                                              messageType,
                                              received,
                                              heldBy,
                                              MessageCorrection.ReplacedId(element)));

            // Answered of its own accord is only where an answer belongs. A
            // shout is not to be acknowledged, and into a room least of all -
            // there everyone present would get to see the acknowledgement.
            if (!messageType.ExpectsAReply())
                return;

            // Auto-receipt (XEP-0184)
            if (ReceiptBuilder.HasReceiptRequest(element) && msgId != null)
            {
                _ = SendReceiptAsync(from, msgId);
            }

            // Auto-received marker (XEP-0333)
            if (ChatMarkers.IsMarkable(element) && msgId != null)
            {
                _ = SendChatMarkerAsync(from, msgId, ChatMarkerType.Received);
            }
        }
    }

    private void ProcessPresence(XElement element)
    {
        var from = element.Attr("from") ?? "unknown";
        var type = element.Attr("type") ?? "available";

        // RFC 6120, section 8.3: 'error' is not a presence state. Previously it
        // wandered through UpdatePresence into the roster, where the contact was
        // then carried as being in the state "error".
        if (type == "error")
        {

            var parsed = StanzaError.TryParse(element.ToString(), out var stanzaError) && stanzaError is not null
                             ? stanzaError
                             : new StanzaError(StanzaErrorType.Cancel, "undefined-condition");

            _logger.LogDebug("Presence from/to {From} refused: {Error}", from, parsed);

            OnStanzaError?.Invoke(from, parsed);
            return;

        }

        if (type == "subscribe")
        {
            Roster.RaiseSubscriptionRequest(from, element.ChildValue("status") ?? "");
        }

        // RFC 6121, section 3: state changes, not presence. They previously ran
        // through UpdatePresence, and because everything there without
        // 'unavailable' counts as present, it was of all things a
        // <presence type='unsubscribed'/> that made the contact online.
        else if (type is "subscribed" or "unsubscribed" or "unsubscribe")
        {
            Roster.ProcessSubscriptionChange(from, type);
        }

        else
        {
            var show = element.ChildValue("show");
            var status = element.ChildValue("status");
            Roster.UpdatePresence(from, type, show, status);

            // XEP-0115: Entity Capabilities
            // IMPORTANT: skip our own presences - we know our caps already!
            // Otherwise a query loop to ourselves → server error → disconnect
            var fromBareJid = JidUtilities.Bare(from);
            var isOwnPresence = fromBareJid.Equals(BareJid, StringComparison.OrdinalIgnoreCase);

            if (!isOwnPresence && (type == "available" || string.IsNullOrEmpty(type)))
            {
                var caps = EntityCapsManager.ParseCaps(element);
                if (caps.HasValue && EntityCaps != null)
                {
                    // Query to the full JID (for the correct resource), not the bare JID
                    // The server routes the answer correctly
                    //
                    // The hash attribute goes along: without it the ver value
                    // cannot be recomputed, and what cannot be recomputed is not
                    // stored.
                    _ = EntityCaps.ProcessCapsAsync(from,
                                                    caps.Value.Node,
                                                    caps.Value.Ver,
                                                    caps.Value.Hash);
                }
            }
        }

        OnPresence?.Invoke(from, type);
    }

    private void ProcessIq(XElement element)
    {
        var type = element.Attr("type");
        var id = element.Attr("id");
        var from = element.Attr("from");

        // RFC 6120, section 8.2.3, rule 2: Without one of the four intended
        // values this stanza is neither a question nor an answer - here the rule
        // meets the client in the role of "the recipient".
        //
        // Right at the front, because every line below already presupposes the
        // type: the assignment to an open question only takes result and error,
        // and the fallback at the end asks for get or set. A fifth value thereby
        // fell out silently at the back.
        if (!IqTypes.IsKnown(type))
        {
            RefuseMalformedIq(id, from);
            return;
        }

        // Is somebody waiting for exactly this answer? The assignment through
        // the id goes before everything else - before the error path too,
        // because for the waiting party an 'error' is just as much an answer as
        // a 'result'.
        if (id is not null && type is "result" or "error" && TryCompleteIq(id, element))
            return;

        // RFC 6120, section 8.3: An iq 'error' is not an answer with content but
        // a refusal. Previously it ran through the same handlers as a 'result' -
        // a refused ping was thereby taken for a measured round-trip time and a
        // refused disco query for an empty result.
        if (type == "error")
        {

            var parsed = StanzaError.TryParse(element.ToString(), out var stanzaError) && stanzaError is not null
                             ? stanzaError
                             : new StanzaError(StanzaErrorType.Cancel, "undefined-condition");

            _logger.LogDebug("Stanza error on IQ '{Id}' from {From}: {Error}",
                             id ?? "(without id)", from ?? "(server)", parsed);

            if (id != null)
            {
                if (id.StartsWith("ping-"))
                    Ping?.ProcessError(id, parsed);

                else if (id.StartsWith("disco-info-") || id.StartsWith("disco-items-"))
                    Disco?.ProcessError(id, parsed);
            }

            OnStanzaError?.Invoke(from, parsed);
            return;

        }

        // IQ result for pending queries
        if (type == "result")
        {
            if (id != null)
            {
                // XEP-0199: ping answer
                if (id.StartsWith("ping-"))
                {
                    Ping?.ProcessPong(id);
                    return;
                }

                // XEP-0030: disco info answer
                if (id.StartsWith("disco-info-") && from != null)
                {
                    Disco?.ProcessInfoResult(id, element, from);
                    return;
                }

                // XEP-0030: disco items answer
                if (id.StartsWith("disco-items-") && from != null)
                {
                    Disco?.ProcessItemsResult(id, element, from);
                    return;
                }
            }
        }

        // IQ get - requests
        //
        // No 'from' does not mean "cannot be answered": per RFC 6120,
        // section 8.1.1.1 the request then comes from one's own server.
        // Previously such requests were discarded silently; now the managers
        // answer without a 'to'. If the manager in charge is not initialised
        // yet, the request deliberately falls through to the
        // <service-unavailable/> below - that is the more honest answer than
        // silence.
        if (type == "get" && id != null)
        {
            // XEP-0199: ping request
            if (PingManager.IsPing(element) && Ping is not null)
            {
                _ = Ping.RespondAsync(id, from);
                return;
            }

            // XEP-0030: disco info request
            if (element.Child(DiscoManager.InfoNamespace, "query") is XElement discoQuery && Disco is not null)
            {

                var node = discoQuery.Attr("node");

                // XEP-0030, section 3.2: The 'node' of the question belongs in
                // the answer - and answered is only what this entity actually
                // denotes. Without this distinction every made-up node would get
                // the full feature list, and this side would thereby claim to
                // carry every one of them.
                if (node is not null && EntityCaps?.IsOwnNode(node) != true)
                    RefuseUnknownNode(id, from, node);

                else
                    _ = Disco.RespondInfoAsync(id, from, node);

                return;

            }

            // XEP-0030, section 4: disco items request
            //
            // A 'node' is here a branch in the tree of sub-entities, not the
            // caps node from XEP-0115. This client has not a single one, and an
            // empty list would be the wrong answer: it would mean "this branch
            // exists, it is empty" instead of "this branch does not exist".
            if (element.Child(DiscoManager.ItemsNamespace, "query") is XElement itemsQuery && Disco is not null)
            {

                var node = itemsQuery.Attr("node");

                if (node is not null)
                    RefuseUnknownNode(id, from, node, DiscoManager.ItemsNamespace);

                else
                    _ = Disco.RespondItemsAsync(id, from);

                return;

            }
        }

        // IQ set
        if (type == "set")
        {
            // Roster push
            if (element.Child(RosterStanzaBuilder.Namespace, "query") is not null)
            {
                // RFC 6121, section 2.1.6: A roster push may only be accepted
                // when it carries no 'from' (then it comes implicitly from one's
                // own account) or the 'from' corresponds to one's own bare JID.
                // Without this check any sender could manipulate the local
                // roster.
                if (!IsAuthorizedRosterPush(from))
                {
                    _logger.LogWarning("Roster push from the unauthorised sender {From} discarded", from);
                    OnSpoofingAttempt?.Invoke($"Roster push spoofing from {from}");

                    // Deliberately without an answer. RFC 6121, section 2.1.6
                    // permits that explicitly: the client may "refuse to return
                    // a stanza error at all (the latter behavior overrides a
                    // MUST-level requirement from [XMPP-CORE] for the purpose of
                    // preventing a presence leak)". An answer would confirm to
                    // the sender that this account is online.
                    return;
                }

                ProcessRosterPush(element);
                _ = SendAsync($"<iq type='result' id='{id}'/>");
                return;
            }
        }

        // PubSub event (can come as a message or an iq)
        if (element.Child(PubSubManager.EventNamespace, "event") is not null && from != null)
        {
            PubSub?.ProcessEvent(element, from, PubSub.PubSubService);

            // XEP-0384, section 5.2: The device list comes over the same route
            // but demands an answer - if one's own device is missing from it, it
            // has to enter itself again.
            _ = ProcessPepEventAsync(element, from);

            // If the event comes as an iq set instead of as a message, it is a
            // request and needs a result per section 8.2.3.
            if (type is "get" or "set" && id != null)
                _ = SendAsync($"<iq type='result' id='{XmlEscaping.Escape(id)}' to='{XmlEscaping.Escape(from)}'/>");

            return;
        }

        // RFC 6120, section 8.2.3: An iq 'get' or 'set' MUST be followed by an
        // answer. Everything that nobody above has claimed is answered
        // conclusively here.
        if (type is "get" or "set")
            RespondUnhandledIq(id, from);
    }

    /// <summary>
    /// Refuses an IQ stanza whose <c>type</c> is missing or is none of the four
    /// intended values (RFC 6120, section 8.2.3, rule 2).
    /// </summary>
    /// <remarks>
    /// <c>modify</c> and not <c>cancel</c>: section 8.3.3.1 provides for it that
    /// way for <c>&lt;bad-request/&gt;</c>, and the kind is a piece of
    /// information - put right, the sender can try again.
    ///
    /// Unlike <see cref="RespondUnhandledIq"/>, this answer goes out without an
    /// <c>id</c> too. There it would be an answer to a question that cannot be
    /// assigned without an <c>id</c> and therefore is of no use to anyone; here
    /// it says something about the stanza itself - that its form is not right.
    /// All the more since the missing <c>id</c> belongs to that per rule 1. What
    /// it does not get is an empty <c>id=''</c>: that belongs to no question and
    /// would look as though it belonged to one.
    /// </remarks>
    private void RefuseMalformedIq(string? id, string? from)
    {

        _logger.LogDebug("IQ with an unusable 'type' from {From} answered with <bad-request/>",
                         from ?? "(server)");

        // Without a 'from' the stanza came from one's own server; the answer
        // then goes back there implicitly without a 'to' (section 8.1.1.1).
        var idAttr  = id   != null ? $" id='{XmlEscaping.Escape(id)}'"   : "";
        var toAttr  = from != null ? $" to='{XmlEscaping.Escape(from)}'" : "";

        _ = SendAsync($"<iq type='error'{idAttr}{toAttr}>" +
                       "<error type='modify'>" +
                       "<bad-request xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                       "</error></iq>");

    }

    /// <summary>
    /// Answers a disco query for a node that does not exist here with
    /// <c>&lt;item-not-found/&gt;</c>.
    /// </summary>
    /// <param name="ns">
    /// The namespace of the request - disco#info or disco#items. The request
    /// carried back has to be the one that was posed; an error naming the wrong
    /// question is worse than one without.
    /// </param>
    /// <remarks>
    /// XEP-0030, section 7 demands "an appropriate error" and leaves the choice;
    /// <c>item-not-found</c> is the piece of information it is about: the
    /// address is right, the node is not.
    ///
    /// The error carries the original request including the <c>node</c> back
    /// (RFC 6120, section 8.3.1). That is more than form here: an asker who
    /// queries several nodes of the same entity otherwise only learns that
    /// <i>one of them</i> is missing.
    /// </remarks>
    private void RefuseUnknownNode(string   id,
                                   string?  from,
                                   string   node,
                                   string   ns    = DiscoManager.InfoNamespace)
    {

        _logger.LogDebug("disco query for the unknown node '{Node}' from {From} answered with <item-not-found/>",
                         node, from ?? "(server)");

        // Without a 'from' the request came from one's own server; the answer
        // then goes back there implicitly without a 'to' (section 8.1.1.1).
        var toAttr = from != null ? $" to='{XmlEscaping.Escape(from)}'" : "";

        _ = SendAsync($"<iq type='error' id='{XmlEscaping.Escape(id)}'{toAttr}>" +
                      $"<query xmlns='{ns}' node='{XmlEscaping.Escape(node)}'/>" +
                       "<error type='cancel'>" +
                       "<item-not-found xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                       "</error></iq>");

    }

    /// <summary>
    /// Answers an IQ for which there is no handler.
    ///
    /// RFC 6120, section 8.2.3 demands an answer to every <c>iq</c> of the type
    /// <c>get</c> or <c>set</c> - <c>result</c> or <c>error</c>. If it fails to
    /// come, the peer waits into its timeout; with a server that can cost the
    /// session. For unsupported requests the right answer per section 8.4 is
    /// <c>&lt;service-unavailable/&gt;</c>.
    /// </summary>
    private void RespondUnhandledIq(string? id, string? from)
    {

        // Without an 'id' the answer could not be assigned - there the attribute
        // is mandatory per section 8.2.3, so the request is itself faulty.
        if (id is null)
        {
            _logger.LogWarning("IQ without an 'id' from {From} - cannot be answered", from ?? "(server)");
            return;
        }

        // Without a 'from' the request came from one's own server; the answer
        // then goes back there implicitly without a 'to' (section 8.1.1.1).
        var toAttr = from != null ? $" to='{XmlEscaping.Escape(from)}'" : "";

        _logger.LogDebug("Unknown IQ '{Id}' from {From} answered with <service-unavailable/>",
                         id, from ?? "(server)");

        _ = SendAsync($"<iq type='error' id='{XmlEscaping.Escape(id)}'{toAttr}>" +
                       "<error type='cancel'>" +
                       "<service-unavailable xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                       "</error></iq>");

    }

    /// <summary>
    /// RFC 6121, section 2.1.6: Checks whether a roster push stems from one's
    /// own account and may therefore be applied.
    /// </summary>
    /// <param name="from">The 'from' attribute of the IQ; null when not set.</param>
    internal bool IsAuthorizedRosterPush(string? from)
    {

        // No 'from' means: implicitly from the bare JID of one's own account.
        if (from is null)
            return true;

        // Before the resource binding there is no own JID yet that could be
        // checked against - then refuse when in doubt.
        if (string.IsNullOrEmpty(FullJid))
            return false;

        return JidUtilities.Bare(from).Equals(BareJid, StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// Applies a roster push.
    ///
    /// The earlier pattern demanded the attributes in the order <c>jid</c>,
    /// <c>name</c>, <c>subscription</c>. An
    /// <c>&lt;item subscription='both' jid='…'/&gt;</c> - valid and, depending
    /// on the server, customary - did not fit that and the push was discarded
    /// silently. Groups it did not read at all.
    /// </summary>
    private void ProcessRosterPush(XElement element)
    {

        var query = element.Child("query");

        if (query is null)
            return;

        foreach (var itemElement in query.Elements().Where(e => e.Name.LocalName == "item"))
        {

            var jid = itemElement.Attr("jid");

            if (string.IsNullOrEmpty(jid))
                continue;

            if (itemElement.Attr("subscription") == "remove")
                Roster.RemoveItem(jid);
            else
                Roster.ProcessRosterItem(ToRosterItem(itemElement, jid));

        }

        // RFC 6121, section 2.6.3: The push carries the version the roster
        // stands at after this change. Taking it over is the whole point of the
        // exercise - without that the client asks with an outdated version at
        // the next login and gets everything all over again.
        if (query.Attr("ver") is string version)
            Roster.Version = version;

    }

    /// <summary>
    /// Builds a <see cref="RosterItem"/> out of an <c>&lt;item/&gt;</c> of the
    /// roster - including the groups, which previously got lost.
    /// </summary>
    private static RosterItem ToRosterItem(XElement itemElement, string jid)
    {

        var item = new RosterItem(jid)
        {
            Name          = itemElement.Attr("name"),
            Subscription  = ParseSubscription(itemElement.Attr("subscription") ?? "")
        };

        foreach (var group in itemElement.Elements().Where(e => e.Name.LocalName == "group"))
            item.Groups.Add(group.Value);

        return item;

    }

    // ===== AUTH & SETUP =====

    private async Task PerformSaslPlainAsync(CancellationToken ct)
    {
        // RFC 4616, section 2: PLAIN, too, sends user name and password in the
        // SASLprep form. Otherwise it would hang on the mechanism whether the
        // same password fits - prepared over SCRAM, not over PLAIN.
        var authData = $"\0{SaslPrep.Prepare(_username)}\0{SaslPrep.Prepare(_password)}";
        var authBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(authData));

        await SendAsync($"<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='PLAIN'>{authBase64}</auth>");

        var response = await ReceiveElementAsync(ct, "the answer to SASL PLAIN");

        if (StreamNegotiation.IsSasl(response, "success"))
            _logger.LogInformation("Authentication successful (PLAIN)");

        else if (StreamNegotiation.IsSasl(response, "failure"))
            throw new AuthenticationException(
                      $"SASL PLAIN refused: {StreamNegotiation.SaslFailureCondition(response) ?? "without a reason given"}");

        else
            throw new AuthenticationException(
                      $"Unexpected answer to SASL PLAIN: <{response.Name.LocalName}/>");

    }

    private async Task PerformScramAsync(SCRAMMechanism mechanism, CancellationToken ct)
    {
        var scram = new SCRAMAuthenticator(_username, _password, mechanism);

        // Step 1: client-first-message
        var clientFirst = scram.CreateClientFirstMessage();
        await SendAsync($"<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='{scram.MechanismName}'>{clientFirst}</auth>");

        // Step 2: server-first-message (challenge)
        var challenge = await ReceiveElementAsync(ct, "the SCRAM challenge");

        if (!StreamNegotiation.IsSasl(challenge, "challenge"))
        {

            if (StreamNegotiation.IsSasl(challenge, "failure"))
                throw new AuthenticationException(
                          $"SCRAM refused: {StreamNegotiation.SaslFailureCondition(challenge) ?? "without a reason given"}");

            throw new AuthenticationException(
                      $"Unexpected answer to the client-first-message: <{challenge.Name.LocalName}/>");

        }

        var serverFirst = StreamNegotiation.SaslPayload(challenge);

        if (serverFirst.Length == 0)
            throw new AuthenticationException("The SASL challenge of the server is empty.");

        // Step 3: client-final-message
        var clientFinal = scram.ProcessServerFirstMessage(serverFirst);
        await SendAsync($"<response xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>{clientFinal}</response>");

        // Step 4: server-final-message (success or failure)
        var final = await ReceiveElementAsync(ct, "the SCRAM server signature");

        if (StreamNegotiation.IsSasl(final, "success"))
        {

            var serverFinal = StreamNegotiation.SaslPayload(final);

            // RFC 5802, section 5: Checking the server signature is the second
            // half of SCRAM - it proves that the peer knows the password as
            // well. Previously that was optional: if a <success/> came without a
            // payload, the check was silently omitted and the mutual
            // authentication was thereby worthless.
            if (serverFinal.Length == 0)
                throw new AuthenticationException(
                          "The server confirmed SCRAM without a server-final-message - " +
                          "its signature is thereby not checkable.");

            if (!scram.VerifyServerFinalMessage(serverFinal))
                throw new AuthenticationException("Server signature invalid - possible MITM attack!");

            _logger.LogInformation("Authentication successful ({Mechanism})", scram.MechanismName);

        }

        else if (StreamNegotiation.IsSasl(final, "failure"))
            throw new AuthenticationException(
                      $"SCRAM failed: {StreamNegotiation.SaslFailureCondition(final) ?? "without a reason given"}");

        else
            throw new AuthenticationException(
                      $"Unexpected answer to the client-final-message: <{final.Name.LocalName}/>");

    }

    private async Task<string> PerformBindAsync(CancellationToken ct)
    {

        var response = await RequestBindAsync("bind1", Resource, ct);
        var jid      = StreamNegotiation.ReadBoundJid(response);

        if (jid is not null)
            return jid;

        // RFC 6120, section 7.7.2.2: If the wished-for resource is already
        // bound, the server may refuse it with <conflict/> - other servers hand
        // out a differing one themselves instead. The refusal calls for the
        // second attempt without a wish; only that way does a second client of
        // the same account get in at all.
        //
        // Only on <conflict/>: every other condition would come back just the
        // same on the second attempt.
        if (Resource is not null && IsConflict(response))
        {

            _logger.LogInformation("The resource '{Resource}' is taken - the server shall hand one out", Resource);

            response = await RequestBindAsync("bind2", null, ct);
            jid      = StreamNegotiation.ReadBoundJid(response);

            if (jid is not null)
                return jid;

        }

        throw new XMPPProtocolException($"Resource binding refused: {DescribeRejection(response)}");

    }

    /// <summary>
    /// Was the request refused with <c>&lt;conflict/&gt;</c>?
    /// </summary>
    private static bool IsConflict(XElement response)
        => StanzaError.TryParse(response.ToString(), out var error) &&
           error?.Condition == "conflict";

    /// <summary>
    /// Sends a bind request and reads the answer.
    /// </summary>
    /// <param name="resource">The wished-for resource, or null for "you hand one out".</param>
    private async Task<XElement> RequestBindAsync(string id, string? resource, CancellationToken ct)
    {

        var wish = resource is not null
                       ? $"<resource>{XmlEscaping.Escape(resource)}</resource>"
                       : "";

        await SendAsync($"<iq type='set' id='{id}'>" +
                        $"<bind xmlns='{StreamNegotiation.BindNamespace}'>{wish}</bind>" +
                        $"</iq>");

        return await ReceiveElementAsync(ct);

    }

    /// <summary>
    /// Describes a refused request for the error message.
    /// </summary>
    private static string DescribeRejection(XElement response)
        => StanzaError.TryParse(response.ToString(), out var error) && error is not null
               ? error.ToString()
               : Shorten(response.ToString(), 200);

    private async Task PerformSessionAsync(CancellationToken ct)
    {

        var response = await SendIqAsync(
                           "sess1",
                           "<iq type='set' id='sess1'>" +
                           $"<session xmlns='{StreamNegotiation.SessionNamespace}'/>" +
                           "</iq>",
                           ct);

        if (response is null)
            _logger.LogWarning("No answer to the session request");

        else if (response.Attr("type") != "result")
            _logger.LogWarning("Session request refused: {Reason}", DescribeRejection(response));

    }

    private async Task EnableCarbonsAsync(CancellationToken ct)
    {

        var response = await SendIqAsync("carbons-enable", CarbonManager.EnableIq(), ct);

        if (response is null)
        {
            _logger.LogWarning("Message carbons: no answer from the server");
            return;
        }

        if (response.Attr("type") == "result")
        {
            Carbons!.SetEnabled(true);
            _logger.LogInformation("Message carbons enabled");
        }
        else
            _logger.LogWarning("Message carbons not available: {Reason}", DescribeRejection(response));

    }
    #region OMEMO (XEP-0384), PEP distribution

    /// <summary>
    /// XEP-0384: Publishes one's own device list.
    /// </summary>
    /// <returns>false when the server has refused it.</returns>
    /// <remarks>
    /// <b>The return value is the point.</b> Up to here this house has sent
    /// PubSub requests off and not looked at what came back (see D38) - for a
    /// subscription that was venial. Here it is not: whoever publishes their
    /// device list and does not learn that it went wrong is unreachable for all
    /// their contacts and notices nothing of it. Everything looks as it always
    /// does, only nobody writes to them encrypted any more.
    /// </remarks>
    public async Task<bool> PublishOmemoDeviceListAsync(OmemoDeviceList   list,
                                                        CancellationToken ct = default)
        => await PublishPepAsync(OmemoDeviceList.Node, OmemoDeviceList.ItemId, list.ToXml(), ct);

    /// <summary>
    /// XEP-0384: Publishes one's own bundle under the device identifier.
    /// </summary>
    public async Task<bool> PublishOmemoBundleAsync(UInt32             deviceId,
                                                    OmemoBundle        bundle,
                                                    CancellationToken  ct = default)
        => await PublishPepAsync(OmemoPep.BundlesNode, deviceId.ToString(), bundle.ToXml(), ct);

    private async Task<bool> PublishPepAsync(string             node,
                                             string             itemId,
                                             XElement           payload,
                                             CancellationToken  ct)
    {

        var id       = $"pep-{Interlocked.Increment(ref _pepCounter)}";
        var response = await SendIqAsync(id, OmemoPep.PublishIq(id, node, itemId, payload), ct);

        if (response is null)
        {
            _logger.LogWarning("PEP: no answer to the publishing in {Node}", node);
            return false;
        }

        if (response.Attr("type") != "result")
        {
            _logger.LogWarning("PEP: {Node} refused: {Reason}", node, DescribeRejection(response));
            return false;
        }

        return true;

    }

    /// <summary>
    /// XEP-0384: Fetches the device list of an account.
    /// </summary>
    /// <returns>
    /// null when there is none - this person does not use OMEMO, or their
    /// server keeps nothing ready. Both are the same thing for whoever wants to
    /// write.
    /// </returns>
    public async Task<OmemoDeviceList?> FetchOmemoDeviceListAsync(string             bareJid,
                                                                  CancellationToken  ct = default)
    {

        var content = await FetchPepAsync(bareJid, OmemoDeviceList.Node, OmemoDeviceList.ItemId, ct);

        return content is not null && OmemoDeviceList.TryRead(content, out var list)
                   ? list
                   : null;

    }

    /// <summary>
    /// XEP-0384: Fetches the bundle of a particular device.
    /// </summary>
    /// <remarks>
    /// <b>The signature is checked here and not only at the caller.</b> A bundle
    /// comes from the server of the peer - that is, from the party OMEMO
    /// protects against. Passing an unchecked bundle on would mean leaving the
    /// check to whoever is most likely to forget it.
    /// </remarks>
    public async Task<OmemoBundle?> FetchOmemoBundleAsync(string             bareJid,
                                                          UInt32             deviceId,
                                                          CancellationToken  ct = default)
    {

        var content = await FetchPepAsync(bareJid, OmemoPep.BundlesNode, deviceId.ToString(), ct);

        if (content is null || !OmemoPep.TryReadBundle(content, out var bundle))
            return null;

        if (!bundle!.SignatureIsValid())
        {
            _logger.LogWarning("OMEMO: The bundle of {Jid}/{Device} is not validly signed",
                               bareJid, deviceId);
            return null;
        }

        return bundle;

    }

    private async Task<XElement?> FetchPepAsync(string             bareJid,
                                                string             node,
                                                string             itemId,
                                                CancellationToken  ct)
    {

        var id       = $"pep-{Interlocked.Increment(ref _pepCounter)}";
        var response = await SendIqAsync(id, OmemoPep.FetchIq(id, bareJid, node, itemId), ct);

        if (response is null || response.Attr("type") != "result")
            return null;

        return response.Child(OmemoPep.PubSubNamespace, "pubsub")
                      ?.Child(OmemoPep.PubSubNamespace, "items")
                      ?.Elements().FirstOrDefault(e => e.Name.LocalName == "item")
                      ?.Elements().FirstOrDefault();

    }

    /// <summary>
    /// Someone else's device list has arrived - through PEP, without anyone
    /// having asked.
    /// </summary>
    public event Action<string, OmemoDeviceList>? OnOmemoDeviceListChanged;

    /// <summary>
    /// One's own device identifier, as soon as it is settled - on it hangs the
    /// re-entry per section 5.2.
    /// </summary>
    public UInt32? OmemoDeviceId { get; set; }

    /// <summary>
    /// Processes a PEP notification (XEP-0163).
    /// </summary>
    /// <remarks>
    /// <b>The re-entry is a MUST of the specification</b>, and the reason is
    /// unpleasant: another device of the same person - or a tidying server - can
    /// rewrite the list and forget this device while doing so. From then on
    /// nobody writes to this device encrypted any more, and it notices nothing
    /// of it, because nothing is missing for it: it keeps getting everything
    /// that comes unencrypted.
    ///
    /// Added to, not replaced: whoever published a list here with only their own
    /// device turned the re-entry into a displacement of all other devices.
    /// </remarks>
    internal async Task ProcessPepEventAsync(XElement stanza, string from)
    {

        var items = stanza.Child("http://jabber.org/protocol/pubsub#event", "event")
                         ?.Child("http://jabber.org/protocol/pubsub#event", "items");

        if (items?.Attr("node") != OmemoDeviceList.Node)
            return;

        var payload = items.Elements().FirstOrDefault(e => e.Name.LocalName == "item")
                          ?.Elements().FirstOrDefault();

        if (payload is null || !OmemoDeviceList.TryRead(payload, out var list) || list is null)
            return;

        OnOmemoDeviceListChanged?.Invoke(JidUtilities.Bare(from), list);

        if (OmemoDeviceId is not UInt32 own ||
            !string.Equals(JidUtilities.Bare(from), BareJid, StringComparison.OrdinalIgnoreCase) ||
            list.Contains(own))
            return;

        _logger.LogWarning("OMEMO: One's own device {Device} is missing from the device list - entering it again",
                           own);

        await PublishOmemoDeviceListAsync(list.With(new OmemoDevice(own)));

    }

    /// <summary>
    /// XEP-0384: the OMEMO manager, as soon as it is switched on.
    /// </summary>
    public OmemoManager? Omemo { get; private set; }

    /// <summary>
    /// A message that arrived encrypted - already decrypted.
    /// </summary>
    public event Action<XMPPMessage, OmemoDecrypted>? OnEncryptedMessage;

    /// <summary>
    /// XEP-0384: Switches OMEMO on - key material from the store, device list
    /// and bundle published.
    /// </summary>
    /// <remarks>
    /// <b>The device list is added to and not replaced.</b> Whoever rewrote it
    /// would thereby displace every other device of the same person - and those
    /// would get nothing any more from then on, without anyone noticing.
    /// </remarks>
    public async Task<bool> EnableOmemoAsync(IOmemoStore store, CancellationToken ct = default)
    {

        Omemo = new OmemoManager(store,
                                 BareJid,
                                 jid => FetchOmemoDeviceListAsync(jid, ct),
                                 (jid, device) => FetchOmemoBundleAsync(jid, device, ct),
                                 _logger);

        OmemoDeviceId = Omemo.Identity.DeviceId;

        var existing = await FetchOmemoDeviceListAsync(BareJid, ct)
                           ?? new OmemoDeviceList([]);

        var extended = existing.With(new OmemoDevice(Omemo.Identity.DeviceId));

        if (!await PublishOmemoDeviceListAsync(extended, ct))
            return false;

        return await PublishOmemoBundleAsync(Omemo.Identity.DeviceId, Omemo.Identity.Bundle(), ct);

    }

    /// <summary>
    /// XEP-0384: Sends an encrypted message.
    /// </summary>
    /// <returns>
    /// The devices skipped - <b>empty means: everyone reads along</b>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// When OMEMO is not switched on. <b>Here it throws and does not send
    /// unencrypted:</b> whoever wanted to write encrypted and sends unencrypted
    /// has made the worst of all mistakes - and silently at that.
    /// </exception>
    public async Task<IReadOnlyList<OmemoSkippedDevice>> SendEncryptedMessageAsync(
        string to, string body, CancellationToken ct = default)
    {

        if (Omemo is null)
            throw new InvalidOperationException(
                      "OMEMO is not switched on. This message will not be sent unencrypted - " +
                      "that would be the worst of all mistakes, and a silent one.");

        XNamespace client = "jabber:client";

        var result = await Omemo.EncryptAsync([to], [new XElement(client + "body", body)]);

        // A <store/> per XEP-0334, so that the storage keeps it: from the
        // outside this message looks like one without content, and a server
        // deciding by the <body/> would throw it away.
        var stanza = new XElement(client + "message",
                                  new XAttribute("to",   JidUtilities.Bare(to)),
                                  new XAttribute("type", "chat"),
                                  new XAttribute("id",   GenerateMessageId()),
                                  result.Element.ToXml(),
                                  new XElement(XNamespace.Get("urn:xmpp:hints") + "store"));

        await SendAsync(stanza.ToString(SaveOptions.DisableFormatting));

        return result.Skipped;

    }

    /// <summary>
    /// Takes an encrypted message in.
    /// </summary>
    /// <returns>true when it was processed - then it no longer goes the ordinary way.</returns>
    private bool TryProcessEncrypted(XElement element, string from)
    {

        if (Omemo is null || !OmemoEncryptedElement.TryRead(element, out var encrypted))
            return false;

        _ = Task.Run(async () =>
        {

            var decrypted = await Omemo.DecryptAsync(encrypted!, from);

            if (decrypted is null)
                return;

            var body = decrypted.Content
                                .FirstOrDefault(e => e.Name.LocalName == "body")
                               ?.Value;

            if (body is null)
                return;

            OnEncryptedMessage?.Invoke(
                new XMPPMessage(from,
                                element.Attr("to") ?? FullJid,
                                body,
                                element.Attr("id"),
                                DateTime.Now,
                                MessageType.Chat),
                decrypted);

        });

        return true;

    }

    #endregion

    /// <summary>
    /// XEP-0352: Tells the server whether a human being is looking right now.
    /// </summary>
    /// <param name="active">
    /// false when the device is lying in the pocket - the server then holds back
    /// what can wait.
    /// </param>
    /// <returns>
    /// false when the server has not announced the extension. Then it stays at
    /// the active state, and on both sides at that: a client that noted its wish
    /// down anyway would take the server for thrifty while it keeps sending
    /// everything.
    /// </returns>
    public async Task<bool> SetClientStateAsync(bool active)
    {

        if (!SupportsClientStateIndication)
        {
            _logger.LogWarning("XEP-0352: The server offers no client state indication.");
            return false;
        }

        await SendAsync(active
                            ? ClientStateIndication.ActiveXml
                            : ClientStateIndication.InactiveXml);

        // Only after the successful send. If the send throws, the state on the
        // server is unchanged, and the two sides would otherwise disagree about
        // what is being held back right now.
        ClientIsActive = active;

        return true;

    }

    /// <summary>
    /// Fetches the roster (RFC 6121, section 2.1) - versioned when the server
    /// offers it.
    /// </summary>
    /// <remarks>
    /// The first time an empty <c>ver=''</c> goes out. That is not a placeholder
    /// but the statement "I can do versioning, but I have nothing yet"
    /// (RFC 6121, section 2.6.1): the server sends the full roster and this time
    /// with a version along with it.
    /// </remarks>
    private async Task RequestRosterAsync(Boolean versioned, CancellationToken ct)
    {

        var response = await SendIqAsync(
                           "roster1",
                           RosterStanzaBuilder.GetRoster(versioned ? Roster.Version ?? "" : null),
                           ct);

        if (response is null)
        {
            _logger.LogWarning("No answer to the roster request");
            return;
        }

        if (response.Attr("type") != "result")
        {
            _logger.LogWarning("Roster request refused: {Reason}", DescribeRejection(response));
            return;
        }

        var query = response.Child(RosterStanzaBuilder.Namespace, "query");

        // RFC 6121, section 2.6.2: A result entirely without a <query/> means
        // "unchanged" - the cache stays as it is. That only holds, though, when
        // we asked in a versioned manner at all; otherwise it would simply be a
        // server that has sent nothing.
        if (query is null)
        {

            if (versioned)
                _logger.LogDebug("Roster unchanged (version {Version}), {Count} contacts from the cache",
                                 Roster.Version, Roster.Items.Count);
            else
                _logger.LogWarning("Roster answer without a <query/>");

            return;

        }

        var state = new List<RosterItem>();

        foreach (var itemElement in query.Children(RosterStanzaBuilder.Namespace, "item"))
        {

            var jid = itemElement.Attr("jid");

            if (!string.IsNullOrEmpty(jid))
                state.Add(ToRosterItem(itemElement, jid));

        }

        // Replace and do not add to: the result is the complete roster
        // (RFC 6121, section 2.1.4). Whoever only merges here keeps a contact
        // the server has long stopped carrying.
        Roster.ReplaceAll(state);

        // The version belongs to exactly this state and is therefore only taken
        // over after that state has been merged in.
        if (query.Attr("ver") is string version)
            Roster.Version = version;

        _logger.LogInformation("Roster loaded: {Count} contacts (version {Version})",
                               Roster.Items.Count, Roster.Version ?? "none");
    }

    // ===== PUBLIC API =====

    public async Task SendPresenceAsync(string? show = null, string? status = null)
    {
        var sb = new StringBuilder("<presence>");
        if (!string.IsNullOrEmpty(show))
            sb.Append($"<show>{XmlEscaping.Escape(show)}</show>");
        if (!string.IsNullOrEmpty(status))
            sb.Append($"<status>{XmlEscaping.Escape(status)}</status>");

        // RFC 6121, section 4.7.2.3: The priority stands behind show and status,
        // the way the section enumerates them.
        if (PresencePriority.HasValue)
            sb.Append($"<priority>{PresencePriority.Value}</priority>");

        // XEP-0115: Entity Capabilities
        if (EntityCaps != null)
        {
            sb.Append(EntityCaps.GetCapsElement());
        }

        sb.Append("</presence>");

        await SendAsync(sb.ToString());
    }

    /// <summary>
    /// Sends a message.
    /// </summary>
    /// <param name="type">
    /// The kind of the message (RFC 6121, section 5.2.2). The default is
    /// <see cref="MessageType.Chat"/> - this client is one for one-on-one
    /// conversations.
    /// </param>
    /// <param name="requestReceipt">
    /// Request a delivery receipt (XEP-0184). Is passed over for messages where
    /// no answer is to be expected: in a room everyone present would get to see
    /// the acknowledgements, and a shout wants none.
    /// </param>
    public async Task<string> SendMessageAsync(string       to,
                                               string       body,
                                               bool         requestReceipt  = true,
                                               bool         markable        = true,
                                               MessageType  type            = MessageType.Chat,
                                               string?      corrects        = null)
    {
        var messageId = GenerateMessageId();

        var typeAttr = type.AsAttribute() is string t ? $" type='{t}'" : "";

        var sb = new StringBuilder();
        sb.Append($"<message to='{XmlEscaping.Escape(to)}'{typeAttr} id='{messageId}'>");
        sb.Append($"<body>{XmlEscaping.Escape(body)}</body>");

        // XEP-0308: An id of its own and the full new text - the <replace/> only
        // names which message is being superseded. A recipient without this
        // extension displays it as a second message, and that is intended: ugly,
        // but complete.
        if (corrects is not null)
            sb.Append(MessageCorrection.ReplaceXml(corrects));

        // What expects no answer gets none requested either.
        if (!type.ExpectsAReply())
        {
            requestReceipt  = false;
            markable        = false;
        }

        // XEP-0184: receipt request
        if (requestReceipt)
        {
            sb.Append(ReceiptBuilder.RequestXml);
            Receipts.TrackMessage(messageId, to);
        }

        // XEP-0333: Chat Markers - markable
        if (markable)
        {
            sb.Append(ChatMarkers.Markable);
        }

        // XEP-0085: Chat State
        sb.Append(ChatState.Active.ToXml());
        sb.Append("</message>");

        var xml = sb.ToString();

        // XEP-0198: the counting along happens centrally in SendAsync.
        await SendAsync(xml);
        return messageId;
    }

    public async Task SendChatStateAsync(string to, ChatState state)
    {
        await SendAsync($"<message to='{XmlEscaping.Escape(to)}' type='chat'>{state.ToXml()}</message>");
    }

    public async Task SendReceiptAsync(string to, string messageId)
    {
        await SendAsync(ReceiptBuilder.CreateReceipt(to, messageId));
    }

    /// <summary>
    /// XEP-0333: Sends a chat marker
    /// </summary>
    public async Task SendChatMarkerAsync(string to, string refMessageId, ChatMarkerType type)
    {
        await SendAsync(ChatMarkers.CreateMarker(to, refMessageId, type));
    }

    /// <summary>
    /// XEP-0199: Sends a ping
    /// </summary>
    public Task<TimeSpan?> PingAsync(string? to = null, CancellationToken ct = default)
    {
        return Ping?.PingAsync(to, ct) ?? Task.FromResult<TimeSpan?>(null);
    }

    /// <summary>
    /// XEP-0030: Queries service discovery info
    /// </summary>
    public Task<DiscoInfo?> DiscoverInfoAsync(string jid, CancellationToken ct = default)
    {
        return Disco?.QueryInfoAsync(jid, ct: ct) ?? Task.FromResult<DiscoInfo?>(null);
    }

    /// <summary>
    /// XEP-0030: Queries service discovery items
    /// </summary>
    public Task<DiscoItems?> DiscoverItemsAsync(string jid, CancellationToken ct = default)
    {
        return Disco?.QueryItemsAsync(jid, ct: ct) ?? Task.FromResult<DiscoItems?>(null);
    }

    /// <summary>
    /// XEP-0198: Requests an ack from the server
    /// </summary>
    public Task RequestAckAsync()
    {
        return StreamManagement?.RequestAckAsync() ?? Task.CompletedTask;
    }

    public async Task SendRawAsync(string xml) => await SendAsync(xml);

    // Roster operations
    public async Task AddContactAsync(string jid, string? name = null, IEnumerable<string>? groups = null)
    {
        await SendAsync(RosterStanzaBuilder.SetItem(jid, name, groups));
        await SendAsync(RosterStanzaBuilder.Subscribe(jid));
    }

    public async Task RemoveContactAsync(string jid) => await SendAsync(RosterStanzaBuilder.RemoveItem(jid));
    public async Task AcceptSubscriptionAsync(string jid) => await SendAsync(RosterStanzaBuilder.Subscribed(jid));
    public async Task DenySubscriptionAsync(string jid) => await SendAsync(RosterStanzaBuilder.Unsubscribed(jid));

    /// <summary>
    /// Cancels one's own subscription to the presence of a contact (RFC 6121,
    /// section 3.3).
    /// </summary>
    /// <remarks>
    /// The fourth of the four transitions from section 3 - and until D57 the
    /// only one this client could not offer, although the building block for it
    /// stood there and the server has mastered it since S3b. Whoever wants to
    /// get rid of the contact entirely takes
    /// <see cref="RemoveContactAsync"/>; here they stay in the roster, only
    /// their presence no longer comes.
    ///
    /// The difference to <see cref="DenySubscriptionAsync"/> is the direction:
    /// there it is about what the contact sees of me, here about what I see of
    /// them.
    /// </remarks>
    public async Task CancelSubscriptionAsync(string jid) => await SendAsync(RosterStanzaBuilder.Unsubscribe(jid));
    #region PubSub (XEP-0060) - outgoing

    /// <summary>
    /// The identifier of the next PubSub request.
    /// </summary>
    /// <remarks>
    /// One per request, and therefore a counter. Until D71 every
    /// <c>subscribe</c> carried the same fixed identifier <c>pubsub-sub</c> - as
    /// long as nobody assigned the answers, that did not show; as soon as
    /// somebody does, the second request would get the answer to the first.
    /// </remarks>
    private Int32 _pubSubCounter;

    private String NextPubSubId()
        => $"pubsub-{Interlocked.Increment(ref _pubSubCounter)}";

    /// <summary>
    /// XEP-0060, section 6.1: Subscribes to a node and <b>waits for the
    /// answer</b>.
    /// </summary>
    /// <param name="service">
    /// The service or the account subscribed at; without one the PubSub service
    /// of one's own domain.
    /// </param>
    /// <returns>
    /// What the service has said - <b>together with its state</b> - or null on a
    /// rejection, on an answer without a promise and on silence.
    /// </returns>
    /// <remarks>
    /// <b>A <c>pending</c> is not a promise, but it is information.</b> Until
    /// D95 it was discarded and the caller got <c>null</c> - the same answer as
    /// to a rejection. That was right for the question "am I subscribed" and
    /// wrong for the question "what have I applied for": the identifier of the
    /// application comes from the service, and without it this client cannot
    /// assign the later promise to any question of its own.
    ///
    /// It is therefore entered, but as what it is.
    /// <see cref="PubSubManager.IsSubscribed"/> counts only what was promised -
    /// whoever booked a <c>pending</c> as a subscription waited for reports that
    /// have not even been decided on yet.
    /// </remarks>
    public async Task<PubSubSubscription?> PubSubSubscribeAsync(String             nodeId,
                                                                String?            service  = null,
                                                                CancellationToken  ct       = default)
    {

        var target   = service ?? PubSub!.PubSubService;
        var id       = NextPubSubId();
        var answer   = await SendIqAsync(id, PubSubBuilder.Subscribe(target, nodeId, BareJid, id), ct);

        if (answer is null)
        {
            _logger.LogWarning("PubSub: no answer to the subscribing to {Node} at {Service}", nodeId, target);
            return null;
        }

        if (answer.Attr("type") != "result")
        {
            _logger.LogWarning("PubSub: {Node} at {Service} refused: {Reason}",
                               nodeId, target, DescribeRejection(answer));
            return null;
        }

        if (!PubSubSubscription.TryRead(answer, target, out var subscription))
        {
            _logger.LogWarning("PubSub: the answer to the subscribing to {Node} contains no promise", nodeId);
            return null;
        }

        // Only what the service knows: a state this client could not read has
        // become None at PubSubSubscription.StateOf - and a subscription that is
        // none does not belong in the bookkeeping.
        if (subscription!.State is not (PubSubSubscriptionState.Subscribed or PubSubSubscriptionState.Pending))
        {
            _logger.LogInformation("PubSub: {Node} at {Service} stands at {State} - not a subscription",
                                   nodeId, target, subscription.State);
            return null;
        }

        if (subscription.State == PubSubSubscriptionState.Pending)
            _logger.LogInformation("PubSub: {Node} at {Service} is applied for and not promised yet",
                                   nodeId, target);

        PubSub!.AddSubscription(subscription);

        return subscription;

    }

    /// <summary>
    /// XEP-0060, section 6.2: Ends a subscription.
    /// </summary>
    /// <param name="subId">
    /// Which subscription is meant. Without one this only works as long as there
    /// is exactly one.
    /// </param>
    /// <remarks>
    /// The <c>subid</c> from the promise goes along when there is one. It is
    /// prescribed as soon as a JID holds several subscriptions to the same node
    /// (section 6.2.3.1), and names the single one unambiguously too.
    ///
    /// <b>With several and without an identifier it is not even asked.</b> The
    /// service would refuse it with <c>&lt;subid-required/&gt;</c>; this client
    /// knows that itself. What it does not do is more important: pick one. That
    /// might end the wrong one, and the caller would take it for the one meant.
    ///
    /// The entry only falls after the <c>result</c>. Deleting it beforehand
    /// would be the same mistake as entering it beforehand, only the other way
    /// round: one would lose the reports of a subscription that still exists.
    /// </remarks>
    public async Task<Boolean> PubSubUnsubscribeAsync(String             nodeId,
                                                      String?            service  = null,
                                                      String?            subId    = null,
                                                      CancellationToken  ct       = default)
    {

        if (!TryPickSubscription(nodeId, subId, service, out var target, out var used))
            return false;

        var id       = NextPubSubId();
        var answer   = await SendIqAsync(id,
                                         PubSubBuilder.Unsubscribe(target, nodeId, BareJid, id, used),
                                         ct);

        if (answer is null || answer.Attr("type") != "result")
        {
            _logger.LogWarning("PubSub: {Node} at {Service} not unsubscribed: {Reason}",
                               nodeId, target,
                               answer is null ? "no answer" : DescribeRejection(answer));
            return false;
        }

        PubSub!.RemoveSubscription(nodeId, used);

        return true;

    }

    /// <summary>
    /// XEP-0060, section 5.6: Fetches one's own subscriptions from the service
    /// and takes them over into the bookkeeping.
    /// </summary>
    /// <returns>
    /// What the service says, or null on a rejection and on silence. <b>An empty
    /// list is something other than null</b>: it means "none", and the
    /// bookkeeping is emptied accordingly.
    /// </returns>
    /// <remarks>
    /// <b>The way out of the fix after a connection drop.</b> The bookkeeping of
    /// this client lives in memory and is created anew at every connection
    /// setup; the subscriptions continue to exist at the service. Without this
    /// request the client afterwards knows not a single identifier any more -
    /// and with several subscriptions to the same node cannot end any of them.
    ///
    /// It does <b>not</b> happen by itself: a client that spoke to a PubSub
    /// service unasked at every connection setup would send a request for a
    /// feature most never use - and against an address that possibly does not
    /// exist at all.
    /// </remarks>
    public async Task<IReadOnlyList<PubSubSubscription>?> PubSubGetSubscriptionsAsync(String?            service  = null,
                                                                                      String?            nodeId   = null,
                                                                                      CancellationToken  ct       = default)
    {

        var target   = service ?? PubSub!.PubSubService;
        var id       = NextPubSubId();
        var answer   = await SendIqAsync(id, PubSubBuilder.GetSubscriptions(target, id, nodeId), ct);

        if (answer is null || answer.Attr("type") != "result")
        {
            _logger.LogWarning("PubSub: the subscriptions at {Service} not read: {Reason}",
                               target,
                               answer is null ? "no answer" : DescribeRejection(answer));
            return null;
        }

        var list = answer.Child(PubSubSubscription.Namespace, "pubsub")
                        ?.Child(PubSubSubscription.Namespace, "subscriptions");

        if (list is null)
        {
            _logger.LogWarning("PubSub: the answer from {Service} contains no enumeration", target);
            return null;
        }

        var entries = list.Children(PubSubSubscription.Namespace, "subscription")
                          .Where (e => e.Attr("node") is not null)
                          .Select(e => new PubSubSubscription(e.Attr("node")!,
                                                              target,
                                                              e.Attr("subid"),
                                                              PubSubSubscription.StateOf(e.Attr("subscription"))))
                          .Where (a => a.State == PubSubSubscriptionState.Subscribed)
                          .ToList();

        // A restriction to one node says nothing about the rest: what the
        // service was not supposed to enumerate must not count as ended here.
        if (nodeId is null)
            PubSub!.ReplaceSubscriptionsOf(target, entries);

        else
            foreach (var subscription in entries)
                PubSub!.AddSubscription(subscription);

        return entries;

    }

    /// <summary>
    /// XEP-0060, section 5.7: Fetches one's own roles - what am I where?
    /// </summary>
    /// <returns>
    /// Per node the role, or null on a rejection and on silence.
    /// </returns>
    public async Task<IReadOnlyList<(String NodeId, PubSubAffiliation Affiliation)>?>
        PubSubGetAffiliationsAsync(String? service = null, CancellationToken ct = default)

        => await ReadAffiliationsAsync(PubSubBuilder.GetAffiliations(service ?? PubSub!.PubSubService,
                                                                     NextPubSubId()),
                                       PubSubSubscription.Namespace, "node", ct);

    /// <summary>
    /// XEP-0060, section 8.9.1: Fetches, as the owner, the roles at a node.
    /// </summary>
    /// <returns>
    /// Per entry the JID and its role, or null - <b>because the node belongs to
    /// someone else, for instance.</b> That is not an empty list: "I do not
    /// know" and "there is nobody" are two answers.
    /// </returns>
    public async Task<IReadOnlyList<(String Jid, PubSubAffiliation Affiliation)>?>
        PubSubGetNodeAffiliationsAsync(String             nodeId,
                                       String?            service  = null,
                                       CancellationToken  ct       = default)

        => await ReadAffiliationsAsync(PubSubBuilder.GetNodeAffiliations(service ?? PubSub!.PubSubService,
                                                                         nodeId, NextPubSubId()),
                                       PubSubBuilder.OwnerNamespace, "jid", ct);

    /// <summary>
    /// XEP-0060, section 8.9.2: Sets, as the owner, a role.
    /// </summary>
    public async Task<Boolean> PubSubSetAffiliationAsync(String             nodeId,
                                                         String             jid,
                                                         PubSubAffiliation  affiliation,
                                                         String?            service  = null,
                                                         CancellationToken  ct       = default)

        => await PubSubRequestAsync(PubSubBuilder.SetAffiliation(service ?? PubSub!.PubSubService,
                                                                 nodeId, NextPubSubId(), jid,
                                                                 PubSubAffiliations.NameOf(affiliation)),
                                    "setting a role", nodeId, ct);

    /// <summary>
    /// XEP-0060, section 8.8.1: Fetches, as the owner, the subscribers of a
    /// node.
    /// </summary>
    /// <returns>
    /// Per entry the JID, the identifier and the state, or null on a rejection
    /// and on silence - <b>because the node belongs to someone else, for
    /// instance</b>. That is not an empty list: "I do not know" and "there is
    /// nobody" are two answers.
    /// </returns>
    /// <remarks>
    /// <b>The state is read strictly here, unlike in one's own promise.</b>
    /// There an unknown name as "not subscribed" is the cautious assumption:
    /// whoever wrongly considers themselves not subscribed asks again. Here the
    /// same leniency would be the opposite of cautious - the owner would take a
    /// subscriber the service carries for absent, and might remove another one
    /// in their place. An unreadable entry therefore makes the whole list fail,
    /// as with the roles.
    /// </remarks>
    public async Task<IReadOnlyList<(String Jid, String? SubId, PubSubSubscriptionState State)>?>
        PubSubGetNodeSubscribersAsync(String             nodeId,
                                      String?            service  = null,
                                      CancellationToken  ct       = default)
    {

        var target   = service ?? PubSub!.PubSubService;
        var id       = NextPubSubId();
        var answer   = await SendIqAsync(id, PubSubBuilder.GetNodeSubscriptions(target, nodeId, id), ct);

        if (answer is null || answer.Attr("type") != "result")
        {
            _logger.LogWarning("PubSub: the subscribers of {Node} not read: {Reason}",
                               nodeId,
                               answer is null ? "no answer" : DescribeRejection(answer));
            return null;
        }

        var list = answer.Child(PubSubBuilder.OwnerNamespace, "pubsub")
                        ?.Child(PubSubBuilder.OwnerNamespace, "subscriptions");

        if (list is null)
        {
            _logger.LogWarning("PubSub: the answer about {Node} contains no subscriber list", nodeId);
            return null;
        }

        var entries = new List<(String, String?, PubSubSubscriptionState)>();

        foreach (var entry in list.Children(PubSubBuilder.OwnerNamespace, "subscription"))
        {

            if (entry.Attr("jid") is not String who ||
                !PubSubSubscription.TryReadState(entry.Attr("subscription"), out var state))
            {
                _logger.LogWarning("PubSub: unreadable entry in the subscriber list: {Entry}", entry);
                return null;
            }

            // The identifier may be missing - a service does not have to hand
            // one out as long as there is only one (section 12.19).
            entries.Add((who, entry.Attr("subid"), state));

        }

        return entries;

    }

    /// <summary>
    /// XEP-0060, section 8.8.2: Ends, as the owner, a subscription to a node of
    /// one's own.
    /// </summary>
    /// <param name="subId">
    /// A particular subscription, or null for all of this JID at this node.
    /// </param>
    public async Task<Boolean> PubSubRemoveSubscriberAsync(String             nodeId,
                                                           String             jid,
                                                           String?            subId    = null,
                                                           String?            service  = null,
                                                           CancellationToken  ct       = default)

        => await PubSubRequestAsync(PubSubBuilder.RemoveSubscriber(service ?? PubSub!.PubSubService,
                                                                    nodeId, NextPubSubId(), jid, subId),
                                    "removing a subscriber", nodeId, ct);

    /// <summary>
    /// Reads a role list - both look the same, only the namespace and the
    /// identifying attribute differ.
    /// </summary>
    /// <remarks>
    /// <b>An entry with an unknown role makes the whole list fail</b>, instead
    /// of being silently missing. A list from which individual lines disappear
    /// is worse than none: whoever looks at it takes someone for without rights
    /// who is not.
    /// </remarks>
    private async Task<IReadOnlyList<(String, PubSubAffiliation)>?> ReadAffiliationsAsync(String             iq,
                                                                                          String             ns,
                                                                                          String             key,
                                                                                          CancellationToken  ct)
    {

        var id       = XElement.Parse(iq).Attr("id")!;
        var answer   = await SendIqAsync(id, iq, ct);

        if (answer is null || answer.Attr("type") != "result")
        {
            _logger.LogWarning("PubSub: the roles not read: {Reason}",
                               answer is null ? "no answer" : DescribeRejection(answer));
            return null;
        }

        var list = answer.Child(ns, "pubsub")?.Child(ns, "affiliations");

        if (list is null)
        {
            _logger.LogWarning("PubSub: the answer contains no role list");
            return null;
        }

        var entries = new List<(String, PubSubAffiliation)>();

        foreach (var entry in list.Children(ns, "affiliation"))
        {

            if (entry.Attr(key) is not String who ||
                !PubSubAffiliations.TryRead(entry.Attr("affiliation"), out var role))
            {
                _logger.LogWarning("PubSub: unreadable entry in the role list: {Entry}", entry);
                return null;
            }

            entries.Add((who, role));

        }

        return entries;

    }

    /// <summary>
    /// XEP-0060, section 6.3.1: Fetches the settings of a subscription.
    /// </summary>
    /// <returns>
    /// What the service says, or null on a rejection and on silence.
    /// </returns>
    /// <remarks>
    /// <b>It is asked even when the settings already stand in one's own
    /// bookkeeping.</b> There stands what this client has set - another device
    /// of the same account may have reconfigured the same subscription in the
    /// meantime, and then one's own entry would be a memory and not a piece of
    /// information.
    /// </remarks>
    public async Task<PubSubSubscriptionOptions?> PubSubGetOptionsAsync(String             nodeId,
                                                                        String?            service  = null,
                                                                        String?            subId    = null,
                                                                        CancellationToken  ct       = default)
    {

        if (!TryPickSubscription(nodeId, subId, service, out var target, out var used))
            return null;

        var id       = NextPubSubId();
        var answer   = await SendIqAsync(id, PubSubBuilder.GetOptions(target, nodeId, BareJid, id, used), ct);

        if (answer is null || answer.Attr("type") != "result")
        {
            _logger.LogWarning("PubSub: settings of {Node} at {Service} not read: {Reason}",
                               nodeId, target,
                               answer is null ? "no answer" : DescribeRejection(answer));
            return null;
        }

        var form = answer.Child(PubSubSubscription.Namespace, "pubsub")
                        ?.Child(PubSubSubscription.Namespace, "options")
                        ?.Child(PubSubSubscriptionOptions.DataFormNamespace, "x");

        if (form is null || !PubSubSubscriptionOptions.TryReadForm(form, out var options))
        {
            _logger.LogWarning("PubSub: the answer about the settings of {Node} contains no readable form",
                               nodeId);
            return null;
        }

        PubSub!.SetOptions(nodeId, used, options!);

        return options;

    }

    /// <summary>
    /// XEP-0060, section 6.3.5: Configures a subscription.
    /// </summary>
    /// <remarks>
    /// It is noted down only after the <c>result</c>. A refused wish as the
    /// state in force would be the same mistake as a subscription entered before
    /// the promise - only one level down.
    /// </remarks>
    public async Task<Boolean> PubSubSetOptionsAsync(String                     nodeId,
                                                     PubSubSubscriptionOptions  options,
                                                     String?                    service  = null,
                                                     String?                    subId    = null,
                                                     CancellationToken          ct       = default)
    {

        if (!TryPickSubscription(nodeId, subId, service, out var target, out var used))
            return false;

        var id       = NextPubSubId();
        var answer   = await SendIqAsync(id,
                                         PubSubBuilder.SetOptions(target, nodeId, BareJid, id, used,
                                                                  options.ToSubmit()
                                                                         .ToString(SaveOptions.DisableFormatting)),
                                         ct);

        if (answer is null || answer.Attr("type") != "result")
        {
            _logger.LogWarning("PubSub: settings of {Node} at {Service} not set: {Reason}",
                               nodeId, target,
                               answer is null ? "no answer" : DescribeRejection(answer));
            return false;
        }

        PubSub!.SetOptions(nodeId, used, options);

        return true;

    }

    /// <summary>
    /// Works out which subscription is meant and where the request goes.
    /// </summary>
    /// <returns>
    /// false when there are several and no identifier says which - then it is
    /// not even asked. The same rule as when unsubscribing, and for the same
    /// reason: the client does not pick one.
    /// </returns>
    private Boolean TryPickSubscription(String       nodeId,
                                        String?      subId,
                                        String?      service,
                                        out String   target,
                                        out String?  usedSubId)
    {

        var subscriptions = PubSub!.SubscriptionsOf(nodeId);

        target     = service ?? PubSub!.PubSubService;
        usedSubId  = subId;

        if (subId is null && subscriptions.Count > 1)
        {
            _logger.LogWarning("PubSub: {Count} subscriptions to {Node} - without a subid there is no saying which one is meant",
                               subscriptions.Count, nodeId);
            return false;
        }

        var meant = subId is not null
                        ? subscriptions.FirstOrDefault(a => String.Equals(a.SubId, subId, StringComparison.Ordinal))
                        : subscriptions.FirstOrDefault();

        target     = service ?? meant?.ServiceJid ?? PubSub!.PubSubService;
        usedSubId  = subId ?? meant?.SubId;

        return true;

    }

    /// <summary>
    /// XEP-0060, section 7.1: Publishes an item.
    /// </summary>
    public async Task<Boolean> PubSubPublishAsync(String             nodeId,
                                                  String             itemId,
                                                  String             payload,
                                                  String?            service  = null,
                                                  CancellationToken  ct       = default)

        => await PubSubRequestAsync(PubSubBuilder.Publish(service ?? PubSub!.PubSubService,
                                                          nodeId, itemId, payload, NextPubSubId()),
                                    "publishing", nodeId, ct);

    /// <summary>
    /// XEP-0060, section 8.1: Creates a node, optionally right away with its
    /// settings.
    /// </summary>
    /// <remarks>
    /// Creating and configuring in one go, because two steps would have a gap:
    /// between the creating and the configuring the node would stand open, and
    /// whoever asks during that time gets in.
    /// </remarks>
    public async Task<Boolean> PubSubCreateNodeAsync(String                    nodeId,
                                                     PubSubNodeConfiguration?  configuration  = null,
                                                     String?                   service        = null,
                                                     CancellationToken         ct             = default)

        => await PubSubRequestAsync(PubSubBuilder.CreateNode(service ?? PubSub!.PubSubService,
                                                             nodeId, NextPubSubId(),
                                                             configuration?.ToSubmit()
                                                                           .ToString(SaveOptions.DisableFormatting)),
                                    "creating", nodeId, ct);

    /// <summary>
    /// XEP-0060, section 8.2.1: Fetches the settings of a node.
    /// </summary>
    /// <returns>
    /// What the service says, or null on a rejection and on silence - and also
    /// when there is nothing in the offer this client understands.
    /// </returns>
    public async Task<PubSubNodeConfiguration?> PubSubGetNodeConfigAsync(String             nodeId,
                                                                         String?            service  = null,
                                                                         CancellationToken  ct       = default)
    {

        var target   = service ?? PubSub!.PubSubService;
        var id       = NextPubSubId();
        var answer   = await SendIqAsync(id, PubSubBuilder.GetNodeConfig(target, nodeId, id), ct);

        if (answer is null || answer.Attr("type") != "result")
        {
            _logger.LogWarning("PubSub: settings of the node {Node} at {Service} not read: {Reason}",
                               nodeId, target,
                               answer is null ? "no answer" : DescribeRejection(answer));
            return null;
        }

        var form = answer.Child(PubSubBuilder.OwnerNamespace, "pubsub")
                        ?.Child(PubSubBuilder.OwnerNamespace, "configure")
                        ?.Child(DataForm.Namespace, "x");

        if (form is null || !PubSubNodeConfiguration.TryReadForm(form, out var configuration))
        {
            _logger.LogWarning("PubSub: the answer about the node {Node} contains no readable form", nodeId);
            return null;
        }

        return configuration;

    }

    /// <summary>
    /// XEP-0060, section 8.2.4: Configures a node.
    /// </summary>
    public async Task<Boolean> PubSubConfigureNodeAsync(String                   nodeId,
                                                        PubSubNodeConfiguration  configuration,
                                                        String?                  service  = null,
                                                        CancellationToken        ct       = default)

        => await PubSubRequestAsync(PubSubBuilder.SetNodeConfig(service ?? PubSub!.PubSubService,
                                                                nodeId, NextPubSubId(),
                                                                configuration.ToSubmit()
                                                                             .ToString(SaveOptions.DisableFormatting)),
                                    "configuring", nodeId, ct);

    /// <summary>
    /// XEP-0060, section 8.6.2: Answers an application for a subscription.
    /// </summary>
    /// <param name="request">The application as it was presented.</param>
    /// <param name="allow">Agree or refuse.</param>
    /// <remarks>
    /// <b>The application goes back the way it came</b> - with the node, the
    /// applicant and the identifier. Getting them made up or left out would be
    /// indistinguishable, for the service, from the answer to a different
    /// application; the same JID may ask several times.
    ///
    /// Without an answer from the service: a message is not answered. Whether it
    /// took effect is said by the subscriber list - or by the report that
    /// arrives at the applicant.
    /// </remarks>
    public async Task PubSubAnswerSubscriptionRequestAsync(PubSubSubscribeAuthorization  request,
                                                           Boolean                       allow,
                                                           String?                       service  = null)
    {

        var target = service ?? PubSub!.PubSubService;

        await SendAsync($"<message to='{XmlEscaping.Escape(target)}'>" +
                        (request with { Allow = allow }).ToSubmit()
                                                        .ToString(SaveOptions.DisableFormatting) +
                        "</message>");

    }

    /// <summary>
    /// XEP-0060, section 7.2: Retracts a single item.
    /// </summary>
    /// <remarks>
    /// <b>The bookkeeping stays untouched</b>, and that from two directions: the
    /// node continues to exist, and so does every subscription to it - and what
    /// this client has of the item, it does not carry. What it carries are
    /// subscriptions; the items lie at the service.
    /// </remarks>
    public async Task<Boolean> PubSubRetractAsync(String             nodeId,
                                                  String             itemId,
                                                  String?            service  = null,
                                                  CancellationToken  ct       = default)

        => await PubSubRequestAsync(PubSubBuilder.Retract(service ?? PubSub!.PubSubService,
                                                          nodeId, itemId, NextPubSubId()),
                                    "retracting", nodeId, ct);

    /// <summary>
    /// XEP-0060, section 8.4: Deletes a node.
    /// </summary>
    /// <remarks>
    /// <b>Afterwards one's own subscription to it is gone too</b>, and this
    /// client has to strike it out itself: the report per section 8.4.2 goes to
    /// everyone except the one who deleted. Whoever relied on it would be the
    /// only one left holding an entry about a node they removed themselves.
    /// </remarks>
    public async Task<Boolean> PubSubDeleteNodeAsync(String             nodeId,
                                                     String?            service  = null,
                                                     CancellationToken  ct       = default)
    {

        var target = service ?? PubSub!.PubSubService;

        if (!await PubSubRequestAsync(PubSubBuilder.DeleteNode(target, nodeId, NextPubSubId()),
                                      "deleting", nodeId, ct))
        {
            return false;
        }

        PubSub!.RemoveSubscriptionsOf(nodeId, target);

        return true;

    }

    /// <summary>
    /// XEP-0060, section 8.5: Purges a node.
    /// </summary>
    /// <remarks>
    /// And leaves the bookkeeping alone: the node continues to exist, the
    /// subscription to it as well - the next publication comes to the same
    /// address.
    /// </remarks>
    public async Task<Boolean> PubSubPurgeNodeAsync(String             nodeId,
                                                    String?            service  = null,
                                                    CancellationToken  ct       = default)

        => await PubSubRequestAsync(PubSubBuilder.PurgeNode(service ?? PubSub!.PubSubService,
                                                             nodeId, NextPubSubId()),
                                    "purging", nodeId, ct);

    /// <summary>
    /// Sends a PubSub request and reports whether the service agreed.
    /// </summary>
    /// <remarks>
    /// The identifier already stands in the finished XML - which is why it is
    /// read back out here instead of handed out anew. Two places that make up an
    /// identifier will at some point make up two different ones.
    /// </remarks>
    private async Task<Boolean> PubSubRequestAsync(String             iq,
                                                   String             what,
                                                   String             nodeId,
                                                   CancellationToken  ct)
    {

        var id       = XElement.Parse(iq).Attr("id")!;
        var answer   = await SendIqAsync(id, iq, ct);

        if (answer is null || answer.Attr("type") != "result")
        {
            _logger.LogWarning("PubSub: {What} in {Node} failed: {Reason}",
                               what, nodeId,
                               answer is null ? "no answer" : DescribeRejection(answer));
            return false;
        }

        return true;

    }

    #endregion

    /// <summary>
    /// XEP-0060, section 6.5: Fetches the items of a node.
    /// </summary>
    /// <returns>
    /// The items, or null on a rejection and on silence. An empty list is
    /// something else: the node was reachable and had nothing.
    /// </returns>
    /// <remarks>
    /// Until D71 this method sent the request off and was done. The answer
    /// arrived, was assigned to no waiting party and fell out of the reception -
    /// the items it was about, nobody ever saw.
    /// </remarks>
    public async Task<IReadOnlyList<PubSubItem>?> PubSubGetItemsAsync(String             nodeId,
                                                                      Int32?             maxItems  = null,
                                                                      String?            service   = null,
                                                                      CancellationToken  ct        = default)
    {

        var target   = service ?? PubSub!.PubSubService;
        var id       = NextPubSubId();
        var answer   = await SendIqAsync(id, PubSubBuilder.GetItems(target, nodeId, maxItems, id), ct);

        if (answer is null || answer.Attr("type") != "result")
        {
            _logger.LogWarning("PubSub: {Node} at {Service} not retrieved: {Reason}",
                               nodeId, target,
                               answer is null ? "no answer" : DescribeRejection(answer));
            return null;
        }

        var items = answer.Child(PubSubSubscription.Namespace, "pubsub")
                         ?.Child(PubSubSubscription.Namespace, "items");

        if (items is null)
            return null;

        return [.. items.Children(PubSubSubscription.Namespace, "item")
                        .Where (item => item.Attr("id") is not null)
                        .Select(item => new PubSubItem(item.Attr("id")!,
                                                       items.Attr("node") ?? nodeId,
                                                       String.Concat(item.Nodes())))];

    }

    // ===== HELPERS =====

    private string GenerateMessageId() => $"msg-{Interlocked.Increment(ref _messageIdCounter)}-{Guid.NewGuid():N}";

    // ExtractAttribute, ExtractAttributeValue and ExtractElement have been
    // dropped. They found attributes and elements somewhere in the text instead
    // of at the element meant, demanded a <body> without attributes and returned
    // entities raw. The replacement are the extension methods in
    // StanzaExtensions, which work on the parsed XElement.

    // ExtractSaslMechanisms has been dropped; the negotiation now reads through
    // StreamNegotiation from the parsed <features/>.

    private static SubscriptionState ParseSubscription(string? sub) => sub switch
    {
        "to" => SubscriptionState.To,
        "from" => SubscriptionState.From,
        "both" => SubscriptionState.Both,
        "remove" => SubscriptionState.Remove,
        _ => SubscriptionState.None
    };

    /// <summary>
    /// Tears the connection down without a close handshake - simulates a network
    /// outage and triggers the reconnect.
    /// </summary>
    /// <remarks>
    /// The counterpart to <c>XMPPSession.Kill()</c> on the server side. For a
    /// run against a <b>foreign</b> peer there is no other way: there the
    /// session cannot be cut from the other side, and a proper
    /// <see cref="DisconnectAsync"/> is precisely not what is to be checked - a
    /// stream that said goodbye is not resumed.
    ///
    /// <c>Abort</c> and not <c>CloseAsync</c>: only that lays the socket down
    /// without sending a close frame.
    /// </remarks>
    public void KillConnection()
        => _webSocket?.Abort();

    public async Task DisconnectAsync()
    {

        _intentionalDisconnect = true;

        // Close the stream cleanly first, then cancel: SendAsync uses the token
        // of the connection, an earlier cancel would prevent the <close/>.
        try
        {
            var webSocket = _webSocket;

            if (webSocket?.State == WebSocketState.Open)
            {
                await SendAsync("<close xmlns='urn:ietf:params:xml:ns:xmpp-framing'/>");

                try
                {
                    using var closeCts = new CancellationTokenSource(CloseHandshakeTimeout);
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Goodbye", closeCts.Token);
                }
                catch (Exception ex)
                {
                    // The peer does not answer the close frame - end the socket hard,
                    // otherwise the teardown blocks indefinitely.
                    _logger.LogDebug(ex, "Close handshake not completed, aborting the socket");
                    webSocket.Abort();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while closing the connection (ignored)");
        }

        await ShutdownConnectionAsync();

        SetState(ConnectionState.Disconnected);

    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();

        _sendLock.Dispose();
    }

}
