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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using org.GraphDefined.Vanaheimr.Illias;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

#region (delegate) OnXMPPClient...Delegate

/// <summary>A chat message was received.</summary>
public delegate Task OnXMPPClientMessageDelegate                    (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     XMPPMessage        Message,
                                                                     CancellationToken  CancellationToken);

/// <summary>
/// XEP-0384: a message that arrived encrypted, already decrypted - together
/// with the rating of the sending device.
/// </summary>
public delegate Task OnXMPPClientEncryptedMessageDelegate           (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     XMPPMessage        Message,
                                                                     OmemoDecrypted     Omemo,
                                                                     CancellationToken  CancellationToken);

/// <summary>XEP-0280: a message was mirrored from or to another device of our own.</summary>
public delegate Task OnXMPPClientCarbonMessageDelegate              (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     CarbonMessage      Carbon,
                                                                     CancellationToken  CancellationToken);

/// <summary>XEP-0085: a contact changed their typing state.</summary>
public delegate Task OnXMPPClientChatStateDelegate                  (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     JID                From,
                                                                     ChatState          State,
                                                                     CancellationToken  CancellationToken);

/// <summary>XEP-0333: a chat marker was received.</summary>
public delegate Task OnXMPPClientChatMarkerDelegate                 (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     ChatMarker         Marker,
                                                                     CancellationToken  CancellationToken);

/// <summary>XEP-0184: a sent message was delivered.</summary>
public delegate Task OnXMPPClientReceiptReceivedDelegate            (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     JID                From,
                                                                     String             MessageId,
                                                                     CancellationToken  CancellationToken);

/// <summary>Presence change of a contact.</summary>
public delegate Task OnXMPPClientPresenceChangedDelegate            (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     JID                From,
                                                                     String             Type,
                                                                     CancellationToken  CancellationToken);

/// <summary>XEP-0060: PubSub event from the service.</summary>
public delegate Task OnXMPPClientPubSubEventDelegate                (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     PubSubEvent        Event,
                                                                     CancellationToken  CancellationToken);

/// <summary>XEP-0060, section 8.6.1: someone applies for a subscription to a node of our own.</summary>
public delegate Task OnXMPPClientPubSubSubscriptionRequestDelegate  (DateTimeOffset                Timestamp,
                                                                     XMPPClient                    Sender,
                                                                     PubSubSubscribeAuthorization  Request,
                                                                     CancellationToken             CancellationToken);

/// <summary>A new contact request.</summary>
public delegate Task OnXMPPClientSubscriptionRequestDelegate        (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     JID                From,
                                                                     String             Status,
                                                                     CancellationToken  CancellationToken);

/// <summary>A contact was added to the roster.</summary>
public delegate Task OnXMPPClientRosterItemAddedDelegate            (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     RosterItem         Item,
                                                                     CancellationToken  CancellationToken);

/// <summary>A contact was removed from the roster.</summary>
public delegate Task OnXMPPClientRosterItemRemovedDelegate          (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     JID                BareJid,
                                                                     CancellationToken  CancellationToken);

/// <summary>XEP-0115: the capabilities of a peer were determined.</summary>
public delegate Task OnXMPPClientCapsDiscoveredDelegate             (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     JID                From,
                                                                     DiscoInfo          Info,
                                                                     CancellationToken  CancellationToken);

/// <summary>The connection state has changed.</summary>
public delegate Task OnXMPPClientStateChangedDelegate               (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     ConnectionState    OldState,
                                                                     ConnectionState    NewState,
                                                                     CancellationToken  CancellationToken);

/// <summary>An error occurred (already logged).</summary>
public delegate Task OnXMPPClientErrorDelegate                      (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     String             Message,
                                                                     CancellationToken  CancellationToken);

/// <summary>A spoofing attempt was fended off (already logged).</summary>
public delegate Task OnXMPPClientSpoofingAttemptDelegate            (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     String             Details,
                                                                     CancellationToken  CancellationToken);

/// <summary>
/// RFC 6120, section 8.3: a stanza was refused. <paramref name="From"/> is the
/// sender of the error and null on an error from one's own server.
/// </summary>
public delegate Task OnXMPPClientStanzaErrorDelegate                (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     JID?               From,
                                                                     StanzaError        Error,
                                                                     CancellationToken  CancellationToken);

/// <summary>RFC 6120, section 4.9: the server ended the stream with an error.</summary>
public delegate Task OnXMPPClientStreamErrorDelegate                (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     StreamError        Error,
                                                                     CancellationToken  CancellationToken);

/// <summary>Raw XML, inbound and outbound - for debug displays.</summary>
public delegate Task OnXMPPClientRawXmlDelegate                     (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     String             XML,
                                                                     CancellationToken  CancellationToken);

/// <summary>The current chat partner was switched or reset.</summary>
public delegate Task OnXMPPClientChatPartnerChangedDelegate         (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     JID?               ChatPartner,
                                                                     CancellationToken  CancellationToken);

#endregion


/// <summary>
/// Application-facing XMPP client.
///
/// Encapsulates an <see cref="XMPPConnection"/> and the session logic that
/// otherwise ends up in the user interface: current chat partner, open contact
/// requests, the last received message ID as well as composite operations
/// (such as "accept contact request" = send subscribed, pose a counter-request
/// and remove it from the waiting list).
///
/// The class produces no output whatsoever; everything runs through the events
/// and the <see cref="ILoggerFactory"/> handed in.
/// </summary>
public sealed class XMPPClient : IAsyncDisposable
{

    #region Data

    private readonly XMPPConnection _connection;
    private readonly ILogger _logger;
    private readonly List<JID> _pendingSubscriptions = [];
    private readonly Lock _pendingLock = new();

    /// <summary>
    /// Valid values for the &lt;show/&gt; element (RFC 6121, section 4.7.2.1).
    /// "available" is the absence of &lt;show/&gt; and therefore permitted as well.
    /// </summary>
    private static readonly string[] ValidShowValues = ["available", "away", "chat", "dnd", "xa"];

    #endregion

    #region Properties

    /// <summary>
    /// The underlying connection - for status queries and special cases.
    /// </summary>
    public XMPPConnection Connection => _connection;

    public Roster Roster => _connection.Roster;
    public ConnectionState State => _connection.State;
    public JID FullJid => _connection.FullJid;
    public JID BareJid => _connection.BareJid;
    public string Domain => _connection.Domain;
    public string WebSocketUri => _connection.WebSocketUri;
    public IReadOnlyList<string> ServerFeatures => _connection.ServerFeatures;
    public IReadOnlyList<string> LocalFeatures => _connection.Disco?.LocalFeatures ?? [];

    public bool IsConnected => _connection.State == ConnectionState.Connected;
    public bool CarbonsEnabled => _connection.Carbons?.IsEnabled == true;
    public StreamManagementManager? StreamManagement => _connection.StreamManagement;

    /// <summary>
    /// JID of the current chat partner; null when no chat is active.
    /// </summary>
    public JID? CurrentChatPartner { get; private set; }

    /// <summary>
    /// ID of the last received message - the point of reference for chat
    /// markers without an explicit ID.
    /// </summary>
    public string? LastReceivedMessageId { get; private set; }

    /// <summary>
    /// The message last sent to a recipient - the point of reference for a
    /// correction per XEP-0308.
    /// </summary>
    /// <remarks>
    /// Per recipient and not overall: section 5 only allows the respectively
    /// last message <b>to the same recipient</b> to be corrected. A single
    /// note would be wrong after every change of subject - and wrong in such a
    /// way that the correction ends up with the previous conversation partner.
    /// </remarks>
    private readonly Dictionary<JID, string> _lastSentTo     = new();
    private readonly Lock                       _lastSentToLock = new();

    /// <summary>
    /// Contact requests not answered yet, in order of arrival.
    /// </summary>
    public IReadOnlyList<JID> PendingSubscriptions
    {
        get { lock (_pendingLock) return _pendingSubscriptions.ToList(); }
    }

    // Configuration - takes effect when the connection is established resp. on reconnect
    public bool KeepaliveEnabled
    {
        get => _connection.KeepaliveEnabled;
        set => _connection.KeepaliveEnabled = value;
    }

    public TimeSpan KeepaliveInterval
    {
        get => _connection.KeepaliveInterval;
        set => _connection.KeepaliveInterval = value;
    }

    public bool StreamManagementEnabled
    {
        get => _connection.StreamManagementEnabled;
        set => _connection.StreamManagementEnabled = value;
    }

    #endregion

    #region Events

    /// <summary>A chat message was received.</summary>
    public event OnXMPPClientMessageDelegate? OnMessage;

    /// <summary>
    /// XEP-0384: a message that arrived encrypted, already decrypted - together
    /// with the rating of the sending device.
    /// </summary>
    public event OnXMPPClientEncryptedMessageDelegate? OnEncryptedMessage;

    /// <summary>XEP-0280: A message was mirrored from/to another device of our own.</summary>
    public event OnXMPPClientCarbonMessageDelegate? OnCarbonMessage;

    /// <summary>XEP-0085: A contact changed their typing state.</summary>
    public event OnXMPPClientChatStateDelegate? OnChatState;

    /// <summary>XEP-0333: A chat marker was received.</summary>
    public event OnXMPPClientChatMarkerDelegate? OnChatMarker;

    /// <summary>XEP-0184: A sent message was delivered.</summary>
    public event OnXMPPClientReceiptReceivedDelegate? OnReceiptReceived;

    /// <summary>Presence change of a contact.</summary>
    public event OnXMPPClientPresenceChangedDelegate? OnPresenceChanged;

    /// <summary>XEP-0060: PubSub event from the service.</summary>
    public event OnXMPPClientPubSubEventDelegate? OnPubSubEvent;

    /// <summary>
    /// XEP-0060, section 8.6.1: Someone applies for a subscription to a node of
    /// our own - answered with
    /// <see cref="PubSubAnswerSubscriptionRequestAsync"/>.
    /// </summary>
    public event OnXMPPClientPubSubSubscriptionRequestDelegate? OnPubSubSubscriptionRequest;

    /// <summary>A new contact request; afterwards it lies in <see cref="PendingSubscriptions"/>.</summary>
    public event OnXMPPClientSubscriptionRequestDelegate? OnSubscriptionRequest;

    /// <summary>A contact was added to the roster.</summary>
    public event OnXMPPClientRosterItemAddedDelegate? OnRosterItemAdded;

    /// <summary>A contact was removed from the roster.</summary>
    public event OnXMPPClientRosterItemRemovedDelegate? OnRosterItemRemoved;

    /// <summary>XEP-0115: The capabilities of a peer were determined.</summary>
    public event OnXMPPClientCapsDiscoveredDelegate? OnCapsDiscovered;

    /// <summary>The connection state has changed.</summary>
    public event OnXMPPClientStateChangedDelegate? OnStateChanged;

    /// <summary>An error occurred (already logged).</summary>
    public event OnXMPPClientErrorDelegate? OnError;

    /// <summary>A spoofing attempt was fended off (already logged).</summary>
    public event OnXMPPClientSpoofingAttemptDelegate? OnSpoofingAttempt;

    /// <summary>
    /// RFC 6120, section 8.3: A stanza was refused. The first parameter is the
    /// sender of the error, null on an error from one's own server.
    /// </summary>
    public event OnXMPPClientStanzaErrorDelegate? OnStanzaError;

    /// <summary>
    /// RFC 6120, section 4.9: The server ended the stream with an error. If it
    /// is not recoverable, the reconnect is omitted.
    /// </summary>
    public event OnXMPPClientStreamErrorDelegate? OnStreamError;

    /// <summary>Raw XML, inbound and outbound - for debug displays.</summary>
    public event OnXMPPClientRawXmlDelegate? OnRawXml;

    /// <summary>The current chat partner was switched or reset.</summary>
    public event OnXMPPClientChatPartnerChangedDelegate? OnChatPartnerChanged;

    #endregion

    #region Constructor(s)

    /// <summary>
    /// Creates a new XMPP client.
    /// </summary>
    /// <param name="jid">Bare JID in the format user@domain</param>
    /// <param name="password">Password for the SASL authentication</param>
    /// <param name="wsUri">
    /// WebSocket endpoint. Without one the <c>host-meta</c> of the domain is
    /// asked (XEP-0156); if none is found there, it stays at
    /// wss://{domain}:5443/ws (the ejabberd default).
    /// </param>
    /// <param name="LoggerFactory">Optional logger factory; without one nothing is logged</param>
    public XMPPClient(string          jid,
                      string          password,
                      string?         wsUri           = null,
                      ILoggerFactory? LoggerFactory   = null)
    {

        _logger      = LoggerFactory is not null
                           ? LoggerFactory.CreateLogger<XMPPClient>()
                           : NullLogger<XMPPClient>.Instance;

        _connection  = new XMPPConnection(jid, password, wsUri, LoggerFactory);

        WireUpConnection();

    }

    /// <summary>
    /// Creates a client around an already configured connection.
    /// </summary>
    public XMPPClient(XMPPConnection  connection,
                      ILoggerFactory? LoggerFactory = null)
    {

        _logger      = LoggerFactory is not null
                           ? LoggerFactory.CreateLogger<XMPPClient>()
                           : NullLogger<XMPPClient>.Instance;

        _connection  = connection ?? throw new ArgumentNullException(nameof(connection));

        WireUpConnection();

    }

    /// <summary>
    /// Passes the connection's events on as the client's own.
    /// </summary>
    /// <remarks>
    /// Every one of these is awaited, and that is the whole point of the
    /// exercise. While both sides were <c>Action</c>, a handler that wanted to
    /// do something asynchronous - answer, store, forward - had only
    /// <c>async void</c>, and an exception in an <c>async void</c> lambda has
    /// no caller left to catch it by the time it is thrown: it goes to the
    /// thread pool and ends the process.
    ///
    /// Converting only this class would not have helped. The forwarding itself
    /// would then have been the <c>async void</c> - the same hole, one layer
    /// further in, and harder to see. So the chain is awaited the whole way,
    /// from the receive loop up to here.
    /// </remarks>
    private void WireUpConnection()
    {

        _connection.OnMessage += async (timestamp, sender, message, ct) =>
        {

            if (!string.IsNullOrEmpty(message.MessageId))
                LastReceivedMessageId = message.MessageId;

            await OnMessage.InvokeAllAsync(handler => handler(timestamp, this, message, ct), _logger);

        };

        // XEP-0384: A decrypted message goes the same way as every other one -
        // and additionally through its own event, which brings along the rating
        // of the sending device.
        //
        // Both, because both are needed: a user interface that does not know
        // OMEMO shows the message anyway; one that knows it can add which
        // device it came from and whether that device is confirmed.
        _connection.OnEncryptedMessage += async (timestamp, sender, message, omemo, ct) =>
        {

            if (!string.IsNullOrEmpty(message.MessageId))
                LastReceivedMessageId = message.MessageId;

            await OnEncryptedMessage.InvokeAllAsync(handler => handler(timestamp, this, message, omemo, ct), _logger);
            await OnMessage.         InvokeAllAsync(handler => handler(timestamp, this, message,        ct), _logger);

        };

        _connection.OnCarbonMessage += async (timestamp, sender, carbon, ct)
            => await OnCarbonMessage.InvokeAllAsync(handler => handler(timestamp, this, carbon, ct), _logger);

        _connection.OnChatState += async (timestamp, sender, from, state, ct)
            => await OnChatState.InvokeAllAsync(handler => handler(timestamp, this, from, state, ct), _logger);

        _connection.OnChatMarker += async (timestamp, sender, marker, ct)
            => await OnChatMarker.InvokeAllAsync(handler => handler(timestamp, this, marker, ct), _logger);

        _connection.OnReceiptReceived += async (timestamp, sender, from, messageId, ct)
            => await OnReceiptReceived.InvokeAllAsync(handler => handler(timestamp, this, from, messageId, ct), _logger);

        _connection.OnPresence += async (timestamp, sender, from, type, ct)
            => await OnPresenceChanged.InvokeAllAsync(handler => handler(timestamp, this, from, type, ct), _logger);

        _connection.OnPubSubEvent += async (timestamp, sender, pubSubEvent, ct)
            => await OnPubSubEvent.InvokeAllAsync(handler => handler(timestamp, this, pubSubEvent, ct), _logger);

        _connection.OnPubSubSubscriptionRequest += async (timestamp, sender, request, ct)
            => await OnPubSubSubscriptionRequest.InvokeAllAsync(handler => handler(timestamp, this, request, ct), _logger);

        _connection.OnCapsDiscovered += async (timestamp, sender, from, info, ct)
            => await OnCapsDiscovered.InvokeAllAsync(handler => handler(timestamp, this, from, info, ct), _logger);

        _connection.OnStateChanged += async (timestamp, sender, oldState, newState, ct)
            => await OnStateChanged.InvokeAllAsync(handler => handler(timestamp, this, oldState, newState, ct), _logger);

        _connection.OnRawXml += async (timestamp, sender, xml, ct)
            => await OnRawXml.InvokeAllAsync(handler => handler(timestamp, this, xml, ct), _logger);

        _connection.OnError += async (timestamp, sender, message, ct)
            => await OnError.InvokeAllAsync(handler => handler(timestamp, this, message, ct), _logger);

        _connection.OnSpoofingAttempt += async (timestamp, sender, details, ct) =>
        {
            _logger.LogWarning("Spoofing attempt fended off: {Details}", details);
            await OnSpoofingAttempt.InvokeAllAsync(handler => handler(timestamp, this, details, ct), _logger);
        };

        _connection.OnStanzaError += async (timestamp, sender, from, error, ct) =>
        {
            _logger.LogInformation("Stanza refused by {From}: {Error}", from?.ToString() ?? "(server)", error);
            await OnStanzaError.InvokeAllAsync(handler => handler(timestamp, this, from, error, ct), _logger);
        };

        _connection.OnStreamError += async (timestamp, sender, error, ct) =>
        {
            _logger.LogWarning("Stream error: {Error} (recoverable: {Recoverable})",
                               error, error.IsRecoverable);
            await OnStreamError.InvokeAllAsync(handler => handler(timestamp, this, error, ct), _logger);
        };

        _connection.Roster.OnItemAdded += async (timestamp, sender, item, ct)
            => await OnRosterItemAdded.InvokeAllAsync(handler => handler(timestamp, this, item, ct), _logger);

        _connection.Roster.OnItemRemoved += async (timestamp, sender, bareJid, ct)
            => await OnRosterItemRemoved.InvokeAllAsync(handler => handler(timestamp, this, bareJid, ct), _logger);

        _connection.Roster.OnSubscriptionRequest += async (timestamp, sender, from, status, ct) =>
        {

            var bare = from.Bare;

            lock (_pendingLock)
            {
                if (!_pendingSubscriptions.Contains(bare))
                    _pendingSubscriptions.Add(bare);
            }

            _logger.LogInformation("Contact request from {From}", bare);

            await OnSubscriptionRequest.InvokeAllAsync(handler => handler(timestamp, this, bare, status, ct), _logger);

        };

    }

    #endregion

    #region Connection

    public Task ConnectAsync(CancellationToken ct = default)
        => _connection.ConnectAsync(ct);

    /// <summary>
    /// Tears the connection down without a close handshake - simulates a
    /// network outage and triggers the reconnect.
    /// </summary>
    public void KillConnection()
        => _connection.KillConnection();

    public Task DisconnectAsync()
        => _connection.DisconnectAsync();

    /// <summary>
    /// Severs an existing connection and establishes it anew.
    /// </summary>
    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        if (IsConnected)
            await _connection.DisconnectAsync();

        await _connection.ConnectAsync(ct);
    }

    /// <summary>
    /// XEP-0199: Measures the round-trip time to the server or to a JID.
    /// </summary>
    public Task<TimeSpan?> PingAsync(JID? to = null, CancellationToken ct = default)
        => _connection.PingAsync(to, ct);

    /// <summary>
    /// XEP-0198: Requests an acknowledgement from the server.
    /// </summary>
    public Task RequestAckAsync()
        => _connection.RequestAckAsync();

    #endregion

    #region Chat partner and messages

    /// <summary>
    /// Sets the current chat partner. null ends the chat without sending
    /// &lt;gone/&gt; - use <see cref="LeaveChatAsync"/> for that.
    /// </summary>
    public async Task SetChatPartnerAsync(JID?               jid,
                                          CancellationToken  CancellationToken   = default)
    {

        // No trimming and no case folding here any more: a JID arrives
        // prepared, and comparing two of them is the type's own business.
        if (CurrentChatPartner == jid)
            return;

        CurrentChatPartner = jid;
        _logger.LogDebug("Chat partner: {Partner}", jid?.ToString() ?? "(none)");

        await OnChatPartnerChanged.InvokeAllAsync(handler => handler(Timestamp.Now, this, jid, CancellationToken), _logger);

    }

    /// <summary>
    /// XEP-0085: Sends &lt;gone/&gt; to the current chat partner and ends the
    /// chat.
    /// </summary>
    /// <returns>The chat partner left, or null when none was active.</returns>
    public async Task<JID?> LeaveChatAsync()
    {
        var partner = CurrentChatPartner;
        if (partner == null)
            return null;

        await _connection.SendChatStateAsync(partner.Value, ChatState.Gone);
        await SetChatPartnerAsync(null);

        return partner;
    }

    /// <summary>
    /// Sends a message to the current chat partner.
    /// </summary>
    /// <returns>The message ID, or null when no chat partner is set.</returns>
    public async Task<string?> SendMessageAsync(string body)
    {
        var partner = CurrentChatPartner;
        if (partner == null)
            return null;

        return await SendMessageAsync(partner.Value, body);
    }

    /// <summary>
    /// Sends a message to an arbitrary JID without changing the current chat
    /// partner.
    /// </summary>
    public async Task<string> SendMessageAsync(JID to, string body,
                                               MessageType type = MessageType.Chat)
    {

        var id = await _connection.SendMessageAsync(to, body, type: type);

        // For a later correction (XEP-0308). What is never corrected gets
        // remembered too - the price is one entry per conversation partner.
        lock (_lastSentToLock)
            _lastSentTo[to.Bare] = id;

        return id;

    }

    /// <summary>
    /// XEP-0308: Corrects the message last sent to this recipient.
    /// </summary>
    /// <param name="to">The recipient; without one the current chat partner.</param>
    /// <param name="body">The complete new text.</param>
    /// <returns>
    /// The ID of the correction, or null - then there is nothing to correct:
    /// no recipient, or nothing has gone out to this one in this session yet.
    /// </returns>
    /// <remarks>
    /// Corrected is exclusively the <b>last</b> message to this recipient
    /// (section 5) - and the correction itself becomes the last one, so that a
    /// correction can in turn be corrected. That is not hairsplitting but the
    /// usual case: whoever mistypes also mistypes in the correction.
    /// </remarks>
    public async Task<string?> CorrectLastMessageAsync(string body, JID? to = null)
    {

        var recipient = to ?? CurrentChatPartner;

        if (recipient is null)
            return null;

        var bare = recipient.Value.Bare;

        string? previous;

        lock (_lastSentToLock)
            if (!_lastSentTo.TryGetValue(bare, out previous))
                return null;

        var id = await _connection.SendMessageAsync(recipient.Value, body, corrects: previous);

        lock (_lastSentToLock)
            _lastSentTo[bare] = id;

        return id;

    }

    /// <summary>
    /// XEP-0085: Sends a typing state to the current chat partner.
    /// </summary>
    /// <returns>false when no chat partner is set.</returns>
    public async Task<bool> SendChatStateAsync(ChatState state)
    {
        var partner = CurrentChatPartner;
        if (partner == null)
            return false;

        await _connection.SendChatStateAsync(partner.Value, state);
        return true;
    }

    /// <summary>
    /// XEP-0333: Sends a chat marker to the current chat partner. Without
    /// <paramref name="messageId"/>, <see cref="LastReceivedMessageId"/> is
    /// used.
    /// </summary>
    /// <returns>The marked message ID, or null when no chat partner is set or
    /// no ID is known.</returns>
    public async Task<string?> SendMarkerAsync(ChatMarkerType type, string? messageId = null)
    {
        var partner = CurrentChatPartner;
        if (partner == null)
            return null;

        var id = messageId ?? LastReceivedMessageId;
        if (string.IsNullOrEmpty(id))
            return null;

        await _connection.SendChatMarkerAsync(partner.Value, id, type);
        return id;
    }

    /// <summary>
    /// Sends raw XML - for protocol experiments.
    /// </summary>
    public Task SendRawAsync(string xml)
        => _connection.SendRawAsync(xml);

    #endregion

    #region Presence

    /// <summary>
    /// Checks whether a &lt;show/&gt; value is valid per RFC 6121.
    /// </summary>
    public static bool IsValidShow(string? show)
        => string.IsNullOrEmpty(show) ||
           ValidShowValues.Contains(show, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sets one's own presence.
    /// </summary>
    /// <exception cref="ArgumentException">On an invalid show value.</exception>
    public Task SetPresenceAsync(string? show = null, string? status = null)
    {
        if (!IsValidShow(show))
            throw new ArgumentException(
                $"Invalid show value '{show}'. Permitted: {string.Join(", ", ValidShowValues)}",
                nameof(show));

        // "available" is the absence of <show/>
        var effectiveShow = string.Equals(show, "available", StringComparison.OrdinalIgnoreCase)
                                ? null
                                : show;

        return _connection.SendPresenceAsync(effectiveShow, status);
    }

    #endregion

    #region Roster and contact requests

    public Task AddContactAsync(JID jid, string? name = null, IEnumerable<string>? groups = null)
        => _connection.AddContactAsync(jid, name, groups);

    public Task RemoveContactAsync(JID jid)
        => _connection.RemoveContactAsync(jid);

    /// <summary>
    /// Cancels one's own subscription to the presence of a contact (RFC 6121,
    /// section 3.3).
    /// </summary>
    /// <remarks>
    /// Without a waiting list and without a counter-request, unlike
    /// <see cref="AcceptSubscriptionAsync"/> and
    /// <see cref="DenySubscriptionAsync"/>: nothing is open here that would
    /// have to be worked off. The contact stays in the roster - whoever wants
    /// to get rid of them entirely takes <see cref="RemoveContactAsync"/>.
    /// </remarks>
    public Task CancelSubscriptionAsync(JID jid)
        => _connection.CancelSubscriptionAsync(jid);

    /// <summary>XEP-0384: the OMEMO manager, as soon as it is switched on.</summary>
    public OmemoManager? Omemo => _connection.Omemo;

    /// <summary>XEP-0384: Is OMEMO switched on?</summary>
    public bool OmemoEnabled => _connection.Omemo is not null;

    /// <summary>
    /// XEP-0384: Switches OMEMO on.
    /// </summary>
    /// <param name="store">
    /// Where keys and sessions go. Without one into memory - <b>then this
    /// device has a new fingerprint at every start</b>, and every comparison is
    /// worthless. For a human being an <see cref="OmemoFileStore"/> belongs
    /// here.
    /// </param>
    public Task<bool> EnableOmemoAsync(IOmemoStore? store = null, CancellationToken ct = default)
        => _connection.EnableOmemoAsync(store ?? new OmemoMemoryStore(), ct);

    /// <summary>
    /// XEP-0384: Sends an encrypted message.
    /// </summary>
    /// <returns>The devices that cannot read along - empty means: all can.</returns>
    public Task<IReadOnlyList<OmemoSkippedDevice>> SendEncryptedMessageAsync(JID                to,
                                                                            string             body,
                                                                            CancellationToken  ct = default)
        => _connection.SendEncryptedMessageAsync(to, body, ct);

    /// <summary>
    /// XEP-0352: Is a human being looking right now?
    /// </summary>
    public bool IsActive => _connection.ClientIsActive;

    /// <summary>
    /// XEP-0352: Has the server announced client state indication?
    /// </summary>
    public bool SupportsClientStateIndication => _connection.SupportsClientStateIndication;

    /// <summary>
    /// XEP-0352: Tells the server whether a human being is looking right now -
    /// inactive means it may hold back what can wait.
    /// </summary>
    /// <returns>false when the server has not announced the extension.</returns>
    /// <remarks>
    /// What is held back is the server's decision. Messages with text
    /// explicitly do not belong to it - this is a saving measure for the
    /// battery and not a do-not-disturb function for the human being in front
    /// of it.
    /// </remarks>
    public Task<bool> SetActiveAsync(bool active)
        => _connection.SetClientStateAsync(active);

    /// <summary>
    /// Accepts a contact request: confirms the subscription, poses a
    /// counter-request for mutual visibility and tidies up the waiting list.
    /// </summary>
    /// <param name="jid">The applicant; without one the oldest open request.</param>
    /// <returns>The JID processed, or null when no request was open.</returns>
    public async Task<JID?> AcceptSubscriptionAsync(JID? jid = null)
    {
        var target = ResolvePendingSubscription(jid);
        if (target == null)
            return null;

        await _connection.AcceptSubscriptionAsync(target.Value);

        // Counter-request, so that the subscription becomes mutual
        await _connection.AddContactAsync(target.Value);

        RemovePendingSubscription(target.Value);
        _logger.LogInformation("Contact request from {Jid} accepted", target);

        return target;
    }

    /// <summary>
    /// Admits a contact in advance: if they pose a request in future, the
    /// server answers it itself (RFC 6121, section 3.4).
    /// </summary>
    /// <param name="jid">The contact to be admitted.</param>
    /// <returns>
    /// false when the server has not announced pre-approval - then per section
    /// 3.4.1 it <b>must</b> not even be attempted.
    /// </returns>
    /// <remarks>
    /// Deliberately not through <see cref="AcceptSubscriptionAsync"/>: that one
    /// accepts an <i>open</i> request and poses a counter-request so that the
    /// visibility becomes mutual. An advance admission does neither - there is
    /// nothing to accept, and whoever admits in advance has not thereby said
    /// that they want to see the other one themselves as well.
    /// </remarks>
    public async Task<bool> PreApproveContactAsync(JID jid)
    {

        if (!ServerSupportsPreApproval)
        {
            _logger.LogWarning("The server announces no pre-approval - {Jid} is not admitted in advance", jid);
            return false;
        }

        await _connection.AcceptSubscriptionAsync(jid);

        _logger.LogInformation("Contact {Jid} admitted in advance", jid);

        return true;

    }

    /// <summary>
    /// Has the server announced subscription pre-approval (RFC 6121,
    /// section 3.4)?
    /// </summary>
    public bool ServerSupportsPreApproval
        => _connection.ServerFeatures.Contains("urn:xmpp:features:pre-approval");

    /// <summary>
    /// Refuses a contact request.
    /// </summary>
    /// <param name="jid">The applicant; without one the oldest open request.</param>
    /// <returns>The JID processed, or null when no request was open.</returns>
    public async Task<JID?> DenySubscriptionAsync(JID? jid = null)
    {
        var target = ResolvePendingSubscription(jid);
        if (target == null)
            return null;

        await _connection.DenySubscriptionAsync(target.Value);

        RemovePendingSubscription(target.Value);
        _logger.LogInformation("Contact request from {Jid} refused", target);

        return target;
    }

    private JID? ResolvePendingSubscription(JID? jid)
    {

        if (jid is not null)
            return jid;

        lock (_pendingLock)
            return _pendingSubscriptions.Count > 0 ? _pendingSubscriptions[0] : null;

    }

    private void RemovePendingSubscription(JID jid)
    {
        lock (_pendingLock)
            _pendingSubscriptions.RemoveAll(pending => pending == jid);
    }

    /// <summary>
    /// Contacts, optionally filtered by JID, display name or group.
    /// </summary>
    public IReadOnlyCollection<RosterItem> GetContacts(string? filter = null)
    {
        var items = _connection.Roster.Items;

        if (string.IsNullOrWhiteSpace(filter))
            return items;

        return items.Where(i =>
            i.Jid.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            (i.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
            i.Groups.Any(g => g.Contains(filter, StringComparison.OrdinalIgnoreCase))
        ).ToList();
    }

    public IEnumerable<RosterItem> GetOnlineContacts() => _connection.Roster.GetOnlineContacts();
    public IEnumerable<string> GetGroups() => _connection.Roster.GetGroups();
    public IEnumerable<RosterItem> GetContactsByGroup(string group) => _connection.Roster.GetByGroup(group);
    public RosterItem? GetContact(JID jid) => _connection.Roster.GetItem(jid);

    #endregion

    #region Service Discovery

    /// <summary>
    /// XEP-0030: Queries the features of a peer.
    /// </summary>
    public Task<DiscoInfo?> DiscoverInfoAsync(JID jid, CancellationToken ct = default)
        => _connection.DiscoverInfoAsync(jid, ct);

    /// <summary>
    /// XEP-0030: Queries the items/services of a peer.
    /// </summary>
    public Task<DiscoItems?> DiscoverItemsAsync(JID jid, CancellationToken ct = default)
        => _connection.DiscoverItemsAsync(jid, ct);

    /// <summary>
    /// XEP-0030: Queries the features of one's own server.
    /// </summary>
    public Task<DiscoInfo?> DiscoverServerInfoAsync(CancellationToken ct = default)
        => _connection.DiscoverInfoAsync(JID.Parse(_connection.Domain), ct);

    #endregion

    #region PubSub (XEP-0060)

    /// <summary>
    /// Subscribes to a node. The result is what the service has promised - or
    /// null when it has not done so.
    /// </summary>
    public Task<PubSubSubscription?> PubSubSubscribeAsync(String nodeId, JID? service = null)
        => _connection.PubSubSubscribeAsync(nodeId, service);

    /// <summary>
    /// Ends a subscription. <paramref name="subId"/> says which one - without
    /// it, this only works as long as there is exactly one.
    /// </summary>
    public Task<Boolean> PubSubUnsubscribeAsync(String nodeId, JID? service = null, String? subId = null)
        => _connection.PubSubUnsubscribeAsync(nodeId, service, subId);

    /// <summary>What am I where? (XEP-0060, section 5.7)</summary>
    public Task<IReadOnlyList<(String NodeId, PubSubAffiliation Affiliation)>?> PubSubGetAffiliationsAsync(JID? service = null)
        => _connection.PubSubGetAffiliationsAsync(service);

    /// <summary>Who is what at my node? (XEP-0060, section 8.9.1)</summary>
    public Task<IReadOnlyList<(String Jid, PubSubAffiliation Affiliation)>?> PubSubGetNodeAffiliationsAsync(String nodeId, JID? service = null)
        => _connection.PubSubGetNodeAffiliationsAsync(nodeId, service);

    /// <summary>Grants or takes a role (XEP-0060, section 8.9.2).</summary>
    public Task<Boolean> PubSubSetAffiliationAsync(String nodeId, JID jid, PubSubAffiliation affiliation, JID? service = null)
        => _connection.PubSubSetAffiliationAsync(nodeId, jid, affiliation, service);

    /// <summary>
    /// Answers an application for a subscription (XEP-0060, section 8.6.2).
    /// </summary>
    public Task PubSubAnswerSubscriptionRequestAsync(PubSubSubscribeAuthorization request, Boolean allow, JID? service = null)
        => _connection.PubSubAnswerSubscriptionRequestAsync(request, allow, service);

    /// <summary>Who hangs on my node? (XEP-0060, section 8.8.1)</summary>
    public Task<IReadOnlyList<(String Jid, String? SubId, PubSubSubscriptionState State)>?> PubSubGetNodeSubscribersAsync(String nodeId, JID? service = null)
        => _connection.PubSubGetNodeSubscribersAsync(nodeId, service);

    /// <summary>
    /// Ends someone else's subscription at one's own node (XEP-0060, section
    /// 8.8.2) - without <paramref name="subId"/> all of this JID.
    /// </summary>
    public Task<Boolean> PubSubRemoveSubscriberAsync(String nodeId, JID jid, String? subId = null, JID? service = null)
        => _connection.PubSubRemoveSubscriberAsync(nodeId, jid, subId, service);

    /// <summary>
    /// Fetches one's own subscriptions from the service and takes them over -
    /// the way back to the identifiers after a connection drop.
    /// </summary>
    public Task<IReadOnlyList<PubSubSubscription>?> PubSubGetSubscriptionsAsync(JID? service = null, String? nodeId = null)
        => _connection.PubSubGetSubscriptionsAsync(service, nodeId);

    /// <summary>
    /// Reads the settings of a subscription from the service.
    /// </summary>
    public Task<PubSubSubscriptionOptions?> PubSubGetOptionsAsync(String nodeId, JID? service = null, String? subId = null)
        => _connection.PubSubGetOptionsAsync(nodeId, service, subId);

    /// <summary>
    /// Configures a subscription - noted down is only what the service has
    /// confirmed.
    /// </summary>
    public Task<Boolean> PubSubSetOptionsAsync(String nodeId, PubSubSubscriptionOptions options, JID? service = null, String? subId = null)
        => _connection.PubSubSetOptionsAsync(nodeId, options, service, subId);

    public Task<Boolean> PubSubPublishAsync(String nodeId, String itemId, String payload, JID? service = null)
        => _connection.PubSubPublishAsync(nodeId, itemId, payload, service);

    /// <summary>
    /// Creates a node, optionally right away with its settings.
    /// </summary>
    public Task<Boolean> PubSubCreateNodeAsync(String nodeId, PubSubNodeConfiguration? configuration = null, JID? service = null)
        => _connection.PubSubCreateNodeAsync(nodeId, configuration, service);

    /// <summary>Reads the settings of a node.</summary>
    public Task<PubSubNodeConfiguration?> PubSubGetNodeConfigAsync(String nodeId, JID? service = null)
        => _connection.PubSubGetNodeConfigAsync(nodeId, service);

    /// <summary>Configures a node - only the owner may do that.</summary>
    public Task<Boolean> PubSubConfigureNodeAsync(String nodeId, PubSubNodeConfiguration configuration, JID? service = null)
        => _connection.PubSubConfigureNodeAsync(nodeId, configuration, service);

    /// <summary>
    /// Retracts a single item (XEP-0060, section 7.2) - the node and its
    /// subscribers stay.
    /// </summary>
    public Task<Boolean> PubSubRetractAsync(String nodeId, String itemId, JID? service = null)
        => _connection.PubSubRetractAsync(nodeId, itemId, service);

    /// <summary>
    /// Deletes a node - together with one's own note about a subscription to
    /// it.
    /// </summary>
    public Task<Boolean> PubSubDeleteNodeAsync(String nodeId, JID? service = null)
        => _connection.PubSubDeleteNodeAsync(nodeId, service);

    /// <summary>
    /// Purges a node (XEP-0060, section 8.5) - the node stays, its content
    /// goes.
    /// </summary>
    public Task<Boolean> PubSubPurgeNodeAsync(String nodeId, JID? service = null)
        => _connection.PubSubPurgeNodeAsync(nodeId, service);

    public Task<IReadOnlyList<PubSubItem>?> PubSubGetItemsAsync(String nodeId, Int32? maxItems = null, JID? service = null)
        => _connection.PubSubGetItemsAsync(nodeId, maxItems, service);

    #endregion

    public ValueTask DisposeAsync()
        => _connection.DisposeAsync();

}
