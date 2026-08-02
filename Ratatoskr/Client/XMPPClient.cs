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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

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
    private readonly List<string> _pendingSubscriptions = [];
    private readonly object _pendingLock = new();

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
    public string FullJid => _connection.FullJid;
    public string BareJid => _connection.BareJid;
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
    public string? CurrentChatPartner { get; private set; }

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
    private readonly Dictionary<string, string> _lastSentTo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Contact requests not answered yet, in order of arrival.
    /// </summary>
    public IReadOnlyList<string> PendingSubscriptions
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
    public event Action<XMPPMessage>? OnMessage;

    /// <summary>
    /// XEP-0384: a message that arrived encrypted, already decrypted - together
    /// with the rating of the sending device.
    /// </summary>
    public event Action<XMPPMessage, OmemoDecrypted>? OnEncryptedMessage;

    /// <summary>XEP-0280: A message was mirrored from/to another device of our own.</summary>
    public event Action<CarbonMessage>? OnCarbonMessage;

    /// <summary>XEP-0085: A contact changed their typing state.</summary>
    public event Action<string, ChatState>? OnChatState;

    /// <summary>XEP-0333: A chat marker was received.</summary>
    public event Action<ChatMarker>? OnChatMarker;

    /// <summary>XEP-0184: A sent message was delivered.</summary>
    public event Action<string, string>? OnReceiptReceived; // from, messageId

    /// <summary>Presence change of a contact.</summary>
    public event Action<string, string>? OnPresenceChanged; // from, type

    /// <summary>XEP-0060: PubSub event from the service.</summary>
    public event Action<PubSubEvent>? OnPubSubEvent;

    /// <summary>
    /// XEP-0060, section 8.6.1: Someone applies for a subscription to a node of
    /// our own - answered with
    /// <see cref="PubSubAnswerSubscriptionRequestAsync"/>.
    /// </summary>
    public event Action<PubSubSubscribeAuthorization>? OnPubSubSubscriptionRequest;

    /// <summary>A new contact request; afterwards it lies in <see cref="PendingSubscriptions"/>.</summary>
    public event Action<string, string>? OnSubscriptionRequest; // from, status

    /// <summary>A contact was added to the roster.</summary>
    public event Action<RosterItem>? OnRosterItemAdded;

    /// <summary>A contact was removed from the roster.</summary>
    public event Action<string>? OnRosterItemRemoved;

    /// <summary>XEP-0115: The capabilities of a peer were determined.</summary>
    public event Action<string, DiscoInfo>? OnCapsDiscovered;

    /// <summary>The connection state has changed.</summary>
    public event Action<ConnectionState, ConnectionState>? OnStateChanged;

    /// <summary>An error occurred (already logged).</summary>
    public event Action<string>? OnError;

    /// <summary>A spoofing attempt was fended off (already logged).</summary>
    public event Action<string>? OnSpoofingAttempt;

    /// <summary>
    /// RFC 6120, section 8.3: A stanza was refused. The first parameter is the
    /// sender of the error, null on an error from one's own server.
    /// </summary>
    public event Action<string?, StanzaError>? OnStanzaError;

    /// <summary>
    /// RFC 6120, section 4.9: The server ended the stream with an error. If it
    /// is not recoverable, the reconnect is omitted.
    /// </summary>
    public event Action<StreamError>? OnStreamError;

    /// <summary>Raw XML, inbound and outbound - for debug displays.</summary>
    public event Action<string>? OnRawXml;

    /// <summary>The current chat partner was switched or reset.</summary>
    public event Action<string?>? OnChatPartnerChanged;

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

    private void WireUpConnection()
    {

        _connection.OnMessage += message =>
        {
            if (!string.IsNullOrEmpty(message.MessageId))
                LastReceivedMessageId = message.MessageId;

            OnMessage?.Invoke(message);
        };

        // XEP-0384: A decrypted message goes the same way as every other one -
        // and additionally through its own event, which brings along the rating
        // of the sending device.
        //
        // Both, because both are needed: a user interface that does not know
        // OMEMO shows the message anyway; one that knows it can add which
        // device it came from and whether that device is confirmed.
        _connection.OnEncryptedMessage += (message, omemo) =>
        {

            if (!string.IsNullOrEmpty(message.MessageId))
                LastReceivedMessageId = message.MessageId;

            OnEncryptedMessage?.Invoke(message, omemo);
            OnMessage?.Invoke(message);

        };

        _connection.OnCarbonMessage   += c            => OnCarbonMessage?.Invoke(c);
        _connection.OnChatState       += (from, s)    => OnChatState?.Invoke(from, s);
        _connection.OnChatMarker      += m            => OnChatMarker?.Invoke(m);
        _connection.OnReceiptReceived += (from, id)   => OnReceiptReceived?.Invoke(from, id);
        _connection.OnPresence        += (from, type) => OnPresenceChanged?.Invoke(from, type);
        _connection.OnPubSubEvent     += e            => OnPubSubEvent?.Invoke(e);
        _connection.OnPubSubSubscriptionRequest
                                      += a            => OnPubSubSubscriptionRequest?.Invoke(a);
        _connection.OnCapsDiscovered  += (from, info) => OnCapsDiscovered?.Invoke(from, info);
        _connection.OnStateChanged    += (o, n)       => OnStateChanged?.Invoke(o, n);
        _connection.OnRawXml          += xml          => OnRawXml?.Invoke(xml);

        _connection.OnError += msg =>
        {
            OnError?.Invoke(msg);
        };

        _connection.OnSpoofingAttempt += msg =>
        {
            _logger.LogWarning("Spoofing attempt fended off: {Details}", msg);
            OnSpoofingAttempt?.Invoke(msg);
        };

        _connection.OnStanzaError += (from, error) =>
        {
            _logger.LogInformation("Stanza refused by {From}: {Error}", from ?? "(server)", error);
            OnStanzaError?.Invoke(from, error);
        };

        _connection.OnStreamError += error =>
        {
            _logger.LogWarning("Stream error: {Error} (recoverable: {Recoverable})",
                               error, error.IsRecoverable);
            OnStreamError?.Invoke(error);
        };

        _connection.Roster.OnItemAdded   += item => OnRosterItemAdded?.Invoke(item);
        _connection.Roster.OnItemRemoved += jid  => OnRosterItemRemoved?.Invoke(jid);

        _connection.Roster.OnSubscriptionRequest += (from, status) =>
        {
            var bare = JidUtilities.Bare(from);

            lock (_pendingLock)
            {
                if (!_pendingSubscriptions.Contains(bare, StringComparer.OrdinalIgnoreCase))
                    _pendingSubscriptions.Add(bare);
            }

            _logger.LogInformation("Contact request from {From}", bare);
            OnSubscriptionRequest?.Invoke(bare, status);
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
    public Task<TimeSpan?> PingAsync(string? to = null, CancellationToken ct = default)
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
    public void SetChatPartner(string? jid)
    {
        var normalized = string.IsNullOrWhiteSpace(jid) ? null : jid.Trim();

        if (string.Equals(CurrentChatPartner, normalized, StringComparison.OrdinalIgnoreCase))
            return;

        CurrentChatPartner = normalized;
        _logger.LogDebug("Chat partner: {Partner}", normalized ?? "(none)");
        OnChatPartnerChanged?.Invoke(normalized);
    }

    /// <summary>
    /// XEP-0085: Sends &lt;gone/&gt; to the current chat partner and ends the
    /// chat.
    /// </summary>
    /// <returns>The chat partner left, or null when none was active.</returns>
    public async Task<string?> LeaveChatAsync()
    {
        var partner = CurrentChatPartner;
        if (partner == null)
            return null;

        await _connection.SendChatStateAsync(partner, ChatState.Gone);
        SetChatPartner(null);

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

        return await SendMessageAsync(partner, body);
    }

    /// <summary>
    /// Sends a message to an arbitrary JID without changing the current chat
    /// partner.
    /// </summary>
    public async Task<string> SendMessageAsync(string to, string body,
                                               MessageType type = MessageType.Chat)
    {

        var id = await _connection.SendMessageAsync(to, body, type: type);

        // For a later correction (XEP-0308). What is never corrected gets
        // remembered too - the price is one entry per conversation partner.
        lock (_lastSentTo)
            _lastSentTo[JidUtilities.Bare(to)] = id;

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
    public async Task<string?> CorrectLastMessageAsync(string body, string? to = null)
    {

        var recipient = to ?? CurrentChatPartner;

        if (recipient is null)
            return null;

        var bare = JidUtilities.Bare(recipient);

        string? previous;

        lock (_lastSentTo)
            if (!_lastSentTo.TryGetValue(bare, out previous))
                return null;

        var id = await _connection.SendMessageAsync(recipient, body, corrects: previous);

        lock (_lastSentTo)
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

        await _connection.SendChatStateAsync(partner, state);
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

        await _connection.SendChatMarkerAsync(partner, id, type);
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

    public Task AddContactAsync(string jid, string? name = null, IEnumerable<string>? groups = null)
        => _connection.AddContactAsync(jid.Trim(), name, groups);

    public Task RemoveContactAsync(string jid)
        => _connection.RemoveContactAsync(jid.Trim());

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
    public Task CancelSubscriptionAsync(string jid)
        => _connection.CancelSubscriptionAsync(jid.Trim());

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
    public Task<IReadOnlyList<OmemoSkippedDevice>> SendEncryptedMessageAsync(string             to,
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
    public async Task<string?> AcceptSubscriptionAsync(string? jid = null)
    {
        var target = ResolvePendingSubscription(jid);
        if (target == null)
            return null;

        await _connection.AcceptSubscriptionAsync(target);

        // Counter-request, so that the subscription becomes mutual
        await _connection.AddContactAsync(target);

        RemovePendingSubscription(target);
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
    public async Task<bool> PreApproveContactAsync(string jid)
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
    public async Task<string?> DenySubscriptionAsync(string? jid = null)
    {
        var target = ResolvePendingSubscription(jid);
        if (target == null)
            return null;

        await _connection.DenySubscriptionAsync(target);

        RemovePendingSubscription(target);
        _logger.LogInformation("Contact request from {Jid} refused", target);

        return target;
    }

    private string? ResolvePendingSubscription(string? jid)
    {
        if (!string.IsNullOrWhiteSpace(jid))
            return jid.Trim();

        lock (_pendingLock)
            return _pendingSubscriptions.Count > 0 ? _pendingSubscriptions[0] : null;
    }

    private void RemovePendingSubscription(string jid)
    {
        lock (_pendingLock)
            _pendingSubscriptions.RemoveAll(p => string.Equals(p, jid, StringComparison.OrdinalIgnoreCase));
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
            i.Jid.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            (i.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
            i.Groups.Any(g => g.Contains(filter, StringComparison.OrdinalIgnoreCase))
        ).ToList();
    }

    public IEnumerable<RosterItem> GetOnlineContacts() => _connection.Roster.GetOnlineContacts();
    public IEnumerable<string> GetGroups() => _connection.Roster.GetGroups();
    public IEnumerable<RosterItem> GetContactsByGroup(string group) => _connection.Roster.GetByGroup(group);
    public RosterItem? GetContact(string jid) => _connection.Roster.GetItem(jid.Trim());

    #endregion

    #region Service Discovery

    /// <summary>
    /// XEP-0030: Queries the features of a peer.
    /// </summary>
    public Task<DiscoInfo?> DiscoverInfoAsync(string jid, CancellationToken ct = default)
        => _connection.DiscoverInfoAsync(jid, ct);

    /// <summary>
    /// XEP-0030: Queries the items/services of a peer.
    /// </summary>
    public Task<DiscoItems?> DiscoverItemsAsync(string jid, CancellationToken ct = default)
        => _connection.DiscoverItemsAsync(jid, ct);

    /// <summary>
    /// XEP-0030: Queries the features of one's own server.
    /// </summary>
    public Task<DiscoInfo?> DiscoverServerInfoAsync(CancellationToken ct = default)
        => _connection.DiscoverInfoAsync(_connection.Domain, ct);

    #endregion

    #region PubSub (XEP-0060)

    /// <summary>
    /// Subscribes to a node. The result is what the service has promised - or
    /// null when it has not done so.
    /// </summary>
    public Task<PubSubSubscription?> PubSubSubscribeAsync(String nodeId, String? service = null)
        => _connection.PubSubSubscribeAsync(nodeId, service);

    /// <summary>
    /// Ends a subscription. <paramref name="subId"/> says which one - without
    /// it, this only works as long as there is exactly one.
    /// </summary>
    public Task<Boolean> PubSubUnsubscribeAsync(String nodeId, String? service = null, String? subId = null)
        => _connection.PubSubUnsubscribeAsync(nodeId, service, subId);

    /// <summary>What am I where? (XEP-0060, section 5.7)</summary>
    public Task<IReadOnlyList<(String NodeId, PubSubAffiliation Affiliation)>?> PubSubGetAffiliationsAsync(String? service = null)
        => _connection.PubSubGetAffiliationsAsync(service);

    /// <summary>Who is what at my node? (XEP-0060, section 8.9.1)</summary>
    public Task<IReadOnlyList<(String Jid, PubSubAffiliation Affiliation)>?> PubSubGetNodeAffiliationsAsync(String nodeId, String? service = null)
        => _connection.PubSubGetNodeAffiliationsAsync(nodeId, service);

    /// <summary>Grants or takes a role (XEP-0060, section 8.9.2).</summary>
    public Task<Boolean> PubSubSetAffiliationAsync(String nodeId, String jid, PubSubAffiliation affiliation, String? service = null)
        => _connection.PubSubSetAffiliationAsync(nodeId, jid, affiliation, service);

    /// <summary>
    /// Answers an application for a subscription (XEP-0060, section 8.6.2).
    /// </summary>
    public Task PubSubAnswerSubscriptionRequestAsync(PubSubSubscribeAuthorization request, Boolean allow, String? service = null)
        => _connection.PubSubAnswerSubscriptionRequestAsync(request, allow, service);

    /// <summary>Who hangs on my node? (XEP-0060, section 8.8.1)</summary>
    public Task<IReadOnlyList<(String Jid, String? SubId, PubSubSubscriptionState State)>?> PubSubGetNodeSubscribersAsync(String nodeId, String? service = null)
        => _connection.PubSubGetNodeSubscribersAsync(nodeId, service);

    /// <summary>
    /// Ends someone else's subscription at one's own node (XEP-0060, section
    /// 8.8.2) - without <paramref name="subId"/> all of this JID.
    /// </summary>
    public Task<Boolean> PubSubRemoveSubscriberAsync(String nodeId, String jid, String? subId = null, String? service = null)
        => _connection.PubSubRemoveSubscriberAsync(nodeId, jid, subId, service);

    /// <summary>
    /// Fetches one's own subscriptions from the service and takes them over -
    /// the way back to the identifiers after a connection drop.
    /// </summary>
    public Task<IReadOnlyList<PubSubSubscription>?> PubSubGetSubscriptionsAsync(String? service = null, String? nodeId = null)
        => _connection.PubSubGetSubscriptionsAsync(service, nodeId);

    /// <summary>
    /// Reads the settings of a subscription from the service.
    /// </summary>
    public Task<PubSubSubscriptionOptions?> PubSubGetOptionsAsync(String nodeId, String? service = null, String? subId = null)
        => _connection.PubSubGetOptionsAsync(nodeId, service, subId);

    /// <summary>
    /// Configures a subscription - noted down is only what the service has
    /// confirmed.
    /// </summary>
    public Task<Boolean> PubSubSetOptionsAsync(String nodeId, PubSubSubscriptionOptions options, String? service = null, String? subId = null)
        => _connection.PubSubSetOptionsAsync(nodeId, options, service, subId);

    public Task<Boolean> PubSubPublishAsync(String nodeId, String itemId, String payload, String? service = null)
        => _connection.PubSubPublishAsync(nodeId, itemId, payload, service);

    /// <summary>
    /// Creates a node, optionally right away with its settings.
    /// </summary>
    public Task<Boolean> PubSubCreateNodeAsync(String nodeId, PubSubNodeConfiguration? configuration = null, String? service = null)
        => _connection.PubSubCreateNodeAsync(nodeId, configuration, service);

    /// <summary>Reads the settings of a node.</summary>
    public Task<PubSubNodeConfiguration?> PubSubGetNodeConfigAsync(String nodeId, String? service = null)
        => _connection.PubSubGetNodeConfigAsync(nodeId, service);

    /// <summary>Configures a node - only the owner may do that.</summary>
    public Task<Boolean> PubSubConfigureNodeAsync(String nodeId, PubSubNodeConfiguration configuration, String? service = null)
        => _connection.PubSubConfigureNodeAsync(nodeId, configuration, service);

    /// <summary>
    /// Retracts a single item (XEP-0060, section 7.2) - the node and its
    /// subscribers stay.
    /// </summary>
    public Task<Boolean> PubSubRetractAsync(String nodeId, String itemId, String? service = null)
        => _connection.PubSubRetractAsync(nodeId, itemId, service);

    /// <summary>
    /// Deletes a node - together with one's own note about a subscription to
    /// it.
    /// </summary>
    public Task<Boolean> PubSubDeleteNodeAsync(String nodeId, String? service = null)
        => _connection.PubSubDeleteNodeAsync(nodeId, service);

    /// <summary>
    /// Purges a node (XEP-0060, section 8.5) - the node stays, its content
    /// goes.
    /// </summary>
    public Task<Boolean> PubSubPurgeNodeAsync(String nodeId, String? service = null)
        => _connection.PubSubPurgeNodeAsync(nodeId, service);

    public Task<IReadOnlyList<PubSubItem>?> PubSubGetItemsAsync(String nodeId, Int32? maxItems = null, String? service = null)
        => _connection.PubSubGetItemsAsync(nodeId, maxItems, service);

    #endregion

    public ValueTask DisposeAsync()
        => _connection.DisposeAsync();

}
