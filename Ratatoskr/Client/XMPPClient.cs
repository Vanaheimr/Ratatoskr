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

using System.Xml.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

#region (delegate) OnXMPPClient...Delegate

/// <summary>
/// A chat message was received.
/// </summary>
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

/// <summary>
/// XEP-0384: a known device reports with a different identity key - and its
/// message was refused.
/// </summary>
public delegate Task OnXMPPClientOmemoIdentityChangedDelegate       (DateTimeOffset        Timestamp,
                                                                     XMPPClient            Sender,
                                                                     OmemoIdentityChanged  Change,
                                                                     CancellationToken     CancellationToken);

/// <summary>
/// XEP-0280: a message was mirrored from or to another device of our own.
/// </summary>
public delegate Task OnXMPPClientCarbonMessageDelegate              (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     CarbonMessage      Carbon,
                                                                     CancellationToken  CancellationToken);

/// <summary>
/// XEP-0085: a contact changed their typing state.
/// </summary>
public delegate Task OnXMPPClientChatStateDelegate                  (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     JID                From,
                                                                     ChatState          State,
                                                                     CancellationToken  CancellationToken);

/// <summary>
/// XEP-0333: a chat marker was received.
/// </summary>
public delegate Task OnXMPPClientChatMarkerDelegate                 (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     ChatMarker         Marker,
                                                                     CancellationToken  CancellationToken);

/// <summary>
/// XEP-0184: a sent message was delivered.
/// </summary>
public delegate Task OnXMPPClientReceiptReceivedDelegate            (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     JID                From,
                                                                     String             MessageId,
                                                                     CancellationToken  CancellationToken);

/// <summary>
/// Presence change of a contact.
/// </summary>
public delegate Task OnXMPPClientPresenceChangedDelegate            (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     JID                From,
                                                                     String             Type,
                                                                     CancellationToken  CancellationToken);

/// <summary>
/// XEP-0060: PubSub event from the service.
/// </summary>
public delegate Task OnXMPPClientPubSubEventDelegate                (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     PubSubEvent        Event,
                                                                     CancellationToken  CancellationToken);

/// <summary>
/// XEP-0060, section 8.6.1: someone applies for a subscription to a node of our own.
/// </summary>
public delegate Task OnXMPPClientPubSubSubscriptionRequestDelegate  (DateTimeOffset                Timestamp,
                                                                     XMPPClient                    Sender,
                                                                     PubSubSubscribeAuthorization  Request,
                                                                     CancellationToken             CancellationToken);

/// <summary>
/// A new contact request.
/// </summary>
public delegate Task OnXMPPClientSubscriptionRequestDelegate        (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     JID                From,
                                                                     String             Status,
                                                                     CancellationToken  CancellationToken);

/// <summary>
/// A contact was added to the roster.
/// </summary>
public delegate Task OnXMPPClientRosterItemAddedDelegate            (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     RosterItem         Item,
                                                                     CancellationToken  CancellationToken);

/// <summary>
/// A contact was removed from the roster.
/// </summary>
public delegate Task OnXMPPClientRosterItemRemovedDelegate          (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     JID                BareJid,
                                                                     CancellationToken  CancellationToken);

/// <summary>
/// XEP-0115: the capabilities of a peer were determined.
/// </summary>
public delegate Task OnXMPPClientCapsDiscoveredDelegate             (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     JID                From,
                                                                     DiscoInfo          Info,
                                                                     CancellationToken  CancellationToken);

/// <summary>
/// The connection state has changed.
/// </summary>
public delegate Task OnXMPPClientStateChangedDelegate               (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     ConnectionState    OldState,
                                                                     ConnectionState    NewState,
                                                                     CancellationToken  CancellationToken);

/// <summary>
/// An error occurred (already logged).
/// </summary>
public delegate Task OnXMPPClientErrorDelegate                      (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     String             Message,
                                                                     CancellationToken  CancellationToken);

/// <summary>
/// A spoofing attempt was fended off (already logged).
/// </summary>
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

/// <summary>
/// RFC 6120, section 4.9: the server ended the stream with an error.
/// </summary>
public delegate Task OnXMPPClientStreamErrorDelegate                (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     StreamError        Error,
                                                                     CancellationToken  CancellationToken);

/// <summary>
/// Raw XML, inbound and outbound - for debug displays.
/// </summary>
public delegate Task OnXMPPClientRawXmlDelegate                     (DateTimeOffset     Timestamp,
                                                                     XMPPClient         Sender,
                                                                     String             XML,
                                                                     CancellationToken  CancellationToken);

/// <summary>
/// The current chat partner was switched or reset.
/// </summary>
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

    private readonly XMPPConnection           _connection;
    private readonly ILogger                  _logger;
    private readonly List<JID>                _pendingSubscriptions  = [];
    private readonly Lock                     _pendingLock           = new();

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
    private readonly Dictionary<JID, string>  _lastSentTo            = [];
    private readonly Lock                     _lastSentToLock        = new();

    /// <summary>
    /// The key <see cref="_lastSentTo"/> is kept under.
    /// </summary>
    /// <remarks>
    /// <b>The bare address everywhere except in a room.</b> A correction has to
    /// name a message this client sent to this recipient (XEP-0308, section 5),
    /// and for an ordinary conversation the resource is not part of who that is:
    /// somebody who answers from their telephone is the same person.
    ///
    /// <b>In a room they are not.</b> A private message (XEP-0045, section 7.5)
    /// is addressed to <c>room@service/nick</c>, whose bare address is the room -
    /// so keying by it puts every occupant of one room under a single entry, and
    /// a correction sent after two private messages would carry the id of the one
    /// that went to somebody else. Section 5 of XEP-0308 has a correction replace
    /// a message from the same sender to the same recipient; that one is for
    /// neither, and the recipient who gets it never saw what it claims to
    /// replace.
    ///
    /// Found in D134, when there was finally something addressed that way.
    /// </remarks>
    private JID LastSentKey(JID to)

        => _connection.Muc?.IsRoom(to.Bare) == true
               ? to
               : to.Bare;

    /// <summary>
    /// Valid values for the &lt;show/&gt; element (RFC 6121, section 4.7.2.1).
    /// "available" is the absence of &lt;show/&gt; and therefore permitted as well.
    /// </summary>
    private static readonly String[]          ValidShowValues        = ["available", "away", "chat", "dnd", "xa"];

    #endregion

    #region Properties

    /// <summary>
    /// The underlying connection - for status queries and special cases.
    /// </summary>
    public XMPPConnection            Connection
        => _connection;

    public Roster                    Roster
        => _connection.Roster;

    public ConnectionState           State
        => _connection.State;

    public JID                       FullJid
        => _connection.FullJid;

    public JID                       BareJid
        => _connection.BareJid;

    public string                    Domain
        => _connection.Domain;

    public URL                       WebSocketUri
        => _connection.WebSocketUri;

    public IReadOnlyList<string>     ServerFeatures
        => _connection.ServerFeatures;

    public IReadOnlyList<string>     LocalFeatures
        => _connection.Disco?.LocalFeatures ?? [];

    public Boolean                   IsConnected
        => _connection.State == ConnectionState.Connected;

    public Boolean                   CarbonsEnabled
        => _connection.Carbons?.IsEnabled == true;

    public StreamManagementManager?  StreamManagement
        => _connection.StreamManagement;

    /// <summary>
    /// JID of the current chat partner; null when no chat is active.
    /// </summary>
    public JID?                       CurrentChatPartner       { get; private set; }

    /// <summary>
    /// ID of the last received message - the point of reference for chat
    /// markers without an explicit ID.
    /// </summary>
    public string?                    LastReceivedMessageId    { get; private set; }

    /// <summary>
    /// Contact requests not answered yet, in order of arrival.
    /// </summary>
    public IReadOnlyList<JID>         PendingSubscriptions
    {
        get { lock (_pendingLock) return _pendingSubscriptions.ToList(); }
    }

    // Configuration - takes effect when the connection is established resp. on reconnect
    public Boolean                    KeepaliveEnabled
    {
        get => _connection.KeepaliveEnabled;
        set => _connection.KeepaliveEnabled = value;
    }

    public TimeSpan                   KeepaliveInterval
    {
        get => _connection.KeepaliveInterval;
        set => _connection.KeepaliveInterval = value;
    }

    public Boolean                    StreamManagementEnabled
    {
        get => _connection.StreamManagementEnabled;
        set => _connection.StreamManagementEnabled = value;
    }

    #endregion

    #region Events

    /// <summary>
    /// A chat message was received.
    /// </summary>
    public event OnXMPPClientMessageDelegate? OnMessage;

    /// <summary>
    /// XEP-0384: a message that arrived encrypted, already decrypted - together
    /// with the rating of the sending device.
    /// </summary>
    public event OnXMPPClientEncryptedMessageDelegate? OnEncryptedMessage;

    /// <summary>
    /// XEP-0384: a device that has written before reports with a different
    /// identity key. <b>Its message was refused</b>, so nothing arrives on
    /// <see cref="OnEncryptedMessage"/> for it.
    /// </summary>
    /// <remarks>
    /// Whoever trusts new devices blindly - the default, and the only trust
    /// model that gets used - is trading the first message for the promise that
    /// a change afterwards is noticed. This is that promise. A user interface
    /// that ignores it has taken the trade without paying for it: the device
    /// simply stops arriving, and nothing says why.
    /// </remarks>
    public event OnXMPPClientOmemoIdentityChangedDelegate? OnOmemoIdentityChanged;

    /// <summary>
    /// XEP-0280: A message was mirrored from/to another device of our own.
    /// </summary>
    public event OnXMPPClientCarbonMessageDelegate? OnCarbonMessage;

    #region XEP-0045: room events

    /// <summary>XEP-0045: this client is now in a room.</summary>
    public event OnRoomJoinedDelegate?       OnRoomJoined;

    /// <summary>
    /// XEP-0045: this client is out of a room.
    /// </summary>
    /// <remarks>
    /// By choice or not - the <c>Why</c> carries the status codes that tell a
    /// departure from a kick, a ban and a room shutting down. Without them all
    /// four look the same.
    /// </remarks>
    public event OnRoomLeftDelegate?         OnRoomLeft;

    /// <summary>XEP-0045: somebody entered a room.</summary>
    public event OnOccupantDelegate?         OnOccupantJoined;

    /// <summary>XEP-0045: somebody in a room changed.</summary>
    public event OnOccupantDelegate?         OnOccupantChanged;

    /// <summary>XEP-0045: somebody left a room, or was removed from it.</summary>
    public event OnOccupantDelegate?         OnOccupantLeft;

    /// <summary>XEP-0045: somebody in a room is called something else now.</summary>
    public event OnOccupantRenamedDelegate?  OnOccupantRenamed;

    /// <summary>XEP-0045: the subject of a room.</summary>
    public event OnRoomSubjectDelegate?      OnRoomSubject;

    /// <summary>
    /// XEP-0045: somebody wants us in a room we are not in.
    /// </summary>
    /// <remarks>
    /// The one thing a room says about a room this client has not entered. An
    /// application that ignores it cannot be invited anywhere.
    /// </remarks>
    public event OnRoomInvitationDelegate?      OnRoomInvitation;

    /// <summary>XEP-0045: somebody we invited is not coming.</summary>
    public event OnInvitationDeclinedDelegate?  OnInvitationDeclined;

    /// <summary>XEP-0045: a room would not pass an invitation of ours on.</summary>
    public event OnInvitationRefusedDelegate?   OnInvitationRefused;

    /// <summary>XEP-0045, section 10.9: a room we were in has been taken down.</summary>
    public event OnRoomDestroyedDelegate?       OnRoomDestroyed;

    /// <summary>XEP-0045, section 8.6: somebody is asking to be allowed to speak.</summary>
    public event OnVoiceRequestedDelegate?      OnVoiceRequested;

    /// <summary>
    /// XEP-0313: one message out of an archive, as it arrives.
    /// </summary>
    /// <remarks>
    /// For showing a long answer while it comes in. Ignoring it loses nothing -
    /// the whole page comes back from the query as well.
    /// </remarks>
    public event OnArchivedMessageDelegate?     OnArchivedMessage;

    #endregion

    /// <summary>
    /// XEP-0085: A contact changed their typing state.
    /// </summary>
    public event OnXMPPClientChatStateDelegate? OnChatState;

    /// <summary>
    /// XEP-0333: A chat marker was received.
    /// </summary>
    public event OnXMPPClientChatMarkerDelegate? OnChatMarker;

    /// <summary>
    /// XEP-0184: A sent message was delivered.
    /// </summary>
    public event OnXMPPClientReceiptReceivedDelegate? OnReceiptReceived;

    /// <summary>
    /// Presence change of a contact.
    /// </summary>
    public event OnXMPPClientPresenceChangedDelegate? OnPresenceChanged;

    /// <summary>
    /// XEP-0060: PubSub event from the service.
    /// </summary>
    public event OnXMPPClientPubSubEventDelegate? OnPubSubEvent;

    /// <summary>
    /// XEP-0060, section 8.6.1: Someone applies for a subscription to a node of
    /// our own - answered with
    /// <see cref="PubSubAnswerSubscriptionRequestAsync"/>.
    /// </summary>
    public event OnXMPPClientPubSubSubscriptionRequestDelegate? OnPubSubSubscriptionRequest;

    /// <summary>
    /// A new contact request; afterwards it lies in <see cref="PendingSubscriptions"/>.
    /// </summary>
    public event OnXMPPClientSubscriptionRequestDelegate? OnSubscriptionRequest;

    /// <summary>
    /// A contact was added to the roster.
    /// </summary>
    public event OnXMPPClientRosterItemAddedDelegate? OnRosterItemAdded;

    /// <summary>
    /// A contact was removed from the roster.
    /// </summary>
    public event OnXMPPClientRosterItemRemovedDelegate? OnRosterItemRemoved;

    /// <summary>
    /// XEP-0115: The capabilities of a peer were determined.
    /// </summary>
    public event OnXMPPClientCapsDiscoveredDelegate? OnCapsDiscovered;

    /// <summary>
    /// The connection state has changed.
    /// </summary>
    public event OnXMPPClientStateChangedDelegate? OnStateChanged;

    /// <summary>
    /// An error occurred (already logged).
    /// </summary>
    public event OnXMPPClientErrorDelegate? OnError;

    /// <summary>
    /// A spoofing attempt was fended off (already logged).
    /// </summary>
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

    /// <summary>
    /// Raw XML, inbound and outbound - for debug displays.
    /// </summary>
    public event OnXMPPClientRawXmlDelegate? OnRawXml;

    /// <summary>
    /// The current chat partner was switched or reset.
    /// </summary>
    public event OnXMPPClientChatPartnerChangedDelegate? OnChatPartnerChanged;

    #endregion

    #region Constructor(s)

    /// <summary>
    /// Creates a new XMPP client.
    /// </summary>
    /// <param name="jid">The account to log in as, in the form user@domain</param>
    /// <param name="password">Password for the SASL authentication</param>
    /// <param name="wsUri">
    /// WebSocket endpoint. Without one the <c>host-meta</c> of the domain is
    /// asked (XEP-0156); if none is found there, it stays at
    /// wss://{domain}:5443/ws (the ejabberd default).
    /// </param>
    /// <param name="LoggerFactory">Optional logger factory; without one nothing is logged</param>
    /// <exception cref="ArgumentException">If the address names a domain and no account.</exception>
    /// <remarks>
    /// <c>JID.Parse("alice@example.com")</c> at the call site - see the
    /// constructor of <see cref="XMPPConnection"/> for why the address is a
    /// type here and not a String.
    /// </remarks>
    public XMPPClient(JID             jid,
                      string          password,
                      URL?            wsUri           = null,
                      ILoggerFactory? LoggerFactory   = null)
    {

        _logger      = LoggerFactory is not null
                           ? LoggerFactory.CreateLogger<XMPPClient>()
                           : NullLogger<XMPPClient>.Instance;

        _connection  = new XMPPConnection(
                           jid,
                           password,
                           wsUri,
                           LoggerFactory
                       );

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

        _connection.OnOmemoIdentityChanged += async (timestamp, sender, change, ct)
            => await OnOmemoIdentityChanged.InvokeAllAsync(handler => handler(timestamp, this, change, ct), _logger);

        _connection.OnCarbonMessage += async (timestamp, sender, carbon, ct)
            => await OnCarbonMessage.InvokeAllAsync(handler => handler(timestamp, this, carbon, ct), _logger);

        // XEP-0045: forwarded from the connection and not from the manager. The
        // manager is replaced on every reconnect; the connection is not.
        _connection.OnRoomJoined       += async (timestamp, sender, room, ct)
            => await OnRoomJoined.     InvokeAllAsync(handler => handler(timestamp, sender, room, ct), _logger);

        _connection.OnRoomLeft         += async (timestamp, sender, room, why, ct)
            => await OnRoomLeft.       InvokeAllAsync(handler => handler(timestamp, sender, room, why, ct), _logger);

        _connection.OnOccupantJoined   += async (timestamp, sender, room, occupant, why, ct)
            => await OnOccupantJoined. InvokeAllAsync(handler => handler(timestamp, sender, room, occupant, why, ct), _logger);

        _connection.OnOccupantChanged  += async (timestamp, sender, room, occupant, why, ct)
            => await OnOccupantChanged.InvokeAllAsync(handler => handler(timestamp, sender, room, occupant, why, ct), _logger);

        _connection.OnOccupantLeft     += async (timestamp, sender, room, occupant, why, ct)
            => await OnOccupantLeft.   InvokeAllAsync(handler => handler(timestamp, sender, room, occupant, why, ct), _logger);

        _connection.OnOccupantRenamed  += async (timestamp, sender, room, oldNick, newNick, isSelf, ct)
            => await OnOccupantRenamed.InvokeAllAsync(handler => handler(timestamp, sender, room, oldNick, newNick, isSelf, ct), _logger);

        _connection.OnRoomSubject      += async (timestamp, sender, room, subject, by, ct)
            => await OnRoomSubject.    InvokeAllAsync(handler => handler(timestamp, sender, room, subject, by, ct), _logger);

        _connection.OnRoomInvitation      += async (timestamp, sender, invitation, ct)
            => await OnRoomInvitation.    InvokeAllAsync(handler => handler(timestamp, sender, invitation, ct), _logger);

        _connection.OnInvitationDeclined  += async (timestamp, sender, declined, ct)
            => await OnInvitationDeclined.InvokeAllAsync(handler => handler(timestamp, sender, declined, ct), _logger);

        _connection.OnInvitationRefused   += async (timestamp, sender, refusal, ct)
            => await OnInvitationRefused. InvokeAllAsync(handler => handler(timestamp, sender, refusal, ct), _logger);

        _connection.OnRoomDestroyed       += async (timestamp, sender, destroyed, ct)
            => await OnRoomDestroyed.     InvokeAllAsync(handler => handler(timestamp, sender, destroyed, ct), _logger);

        _connection.OnVoiceRequested      += async (timestamp, sender, request, ct)
            => await OnVoiceRequested.    InvokeAllAsync(handler => handler(timestamp, sender, request, ct), _logger);

        _connection.OnArchivedMessage     += async (timestamp, sender, queryId, archived, ct)
            => await OnArchivedMessage.   InvokeAllAsync(handler => handler(timestamp, sender, queryId, archived, ct), _logger);

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
            _lastSentTo[LastSentKey(to)] = id;

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

        var key = LastSentKey(recipient.Value);

        string? previous;

        lock (_lastSentToLock)
            if (!_lastSentTo.TryGetValue(key, out previous))
                return null;

        var id = await _connection.SendMessageAsync(recipient.Value, body, corrects: previous);

        lock (_lastSentToLock)
            _lastSentTo[key] = id;

        return id;

    }

    /// <summary>
    /// XEP-0461: Answers a message that arrived here.
    /// </summary>
    /// <param name="message">What is being answered.</param>
    /// <param name="body">The answer.</param>
    /// <param name="quote">
    /// Carry the answered text along as quoted lines, so that a client which
    /// does not know XEP-0461 still shows what this is about. On by default:
    /// leaving it out saves a few hundred bytes and costs the other side the
    /// message.
    /// </param>
    /// <returns>
    /// The ID of the answer, or null - then there is nothing here that can be
    /// answered. In a room that is a real case and not an error: without a name
    /// from the room itself there is no reference everybody present would read
    /// the same way.
    /// </returns>
    /// <remarks>
    /// <b>What is quoted is <see cref="XMPPMessage.Text"/>, not
    /// <see cref="XMPPMessage.Body"/></b>, and the difference is the whole
    /// comfort of the thing. When the answered message was itself an answer,
    /// its body still holds the quotation it came with - quoting that as well
    /// would carry the entire conversation forward one <c>&gt;</c> deeper each
    /// time. An answer quotes what was said, not what was quoted while saying
    /// it.
    /// </remarks>
    public async Task<string?> ReplyToAsync(XMPPMessage  message,
                                            string       body,
                                            bool         quote = true)
    {

        var id = message.ReplyableId;

        if (id is null)
            return null;

        var room = message.Type == MessageType.GroupChat;

        // In a room the answer goes to the room, not to the occupant who is
        // being answered - the reference is only worth anything to those who
        // can see the message it names.
        var to   = room ? message.From.Bare : message.From;

        var sent = await _connection.SendReplyAsync(
                             to,
                             body,
                             id,
                             replyToAuthor:  message.From,
                             quotedText:     quote ? message.Text : null,
                             quotedAuthor:   room  ? message.From.Resourcepart : null,
                             type:           room  ? MessageType.GroupChat : MessageType.Chat
                         );

        // For a later correction (XEP-0308): an answer is a message like any
        // other, and whoever mistypes in one wants to correct it too.
        lock (_lastSentToLock)
            _lastSentTo[LastSentKey(to)] = sent;

        return sent;

    }

    /// <summary>
    /// XEP-0461: Answers a message by its name, for a caller that has one but
    /// not the message.
    /// </summary>
    /// <remarks>
    /// The way in for anything that keeps its own history - a stored
    /// conversation, an archive. <see cref="ReplyToAsync"/> is the everyday
    /// one; this is the same thing without the convenience of having the
    /// message to hand.
    /// </remarks>
    public async Task<string> SendReplyAsync(JID      to,
                                             string   body,
                                             string   replyToId,
                                             JID?     replyToAuthor  = null,
                                             string?  quotedText     = null,
                                             string?  quotedAuthor   = null)
    {

        var id = await _connection.SendReplyAsync(to,
                                                  body,
                                                  replyToId,
                                                  replyToAuthor,
                                                  quotedText,
                                                  quotedAuthor);

        lock (_lastSentToLock)
            _lastSentTo[LastSentKey(to)] = id;

        return id;

    }

    #region XEP-0363: files

    /// <summary>
    /// XEP-0363: the service this server hands out upload slots at, or null.
    /// </summary>
    /// <remarks>
    /// Worth asking before offering somebody a paperclip: a server that has no
    /// upload service cannot send a file at all, and finding that out after the
    /// file has been read from disk is a worse way to learn it.
    /// </remarks>
    public Task<UploadService?> DiscoverUploadServiceAsync(CancellationToken ct = default)
        => _connection.Upload?.DiscoverAsync(false, ct) ?? Task.FromResult<UploadService?>(null);

    /// <summary>
    /// XEP-0363 and XEP-0066: sends a file from disk.
    /// </summary>
    /// <remarks>
    /// The size comes from the file itself, which is the only place it can
    /// honestly come from: the number in the slot request is a promise the
    /// service checks the PUT against.
    /// </remarks>
    public async Task<FileSent> SendFileAsync(JID                to,
                                              String             path,
                                              MessageType        type  = MessageType.Chat,
                                              CancellationToken  ct    = default)
    {

        var info = new FileInfo(path);

        if (!info.Exists)
            return new FileSent(new UploadOutcome(null), null);

        await using var content = info.OpenRead();

        return await SendFileAsync(to,
                                   content,
                                   info.Length,
                                   info.Name,
                                   HttpFileUpload.GuessContentType(info.Name),
                                   type,
                                   ct);

    }

    /// <summary>
    /// XEP-0363 and XEP-0066: sends what is in a stream as a file.
    /// </summary>
    public async Task<FileSent> SendFileAsync(JID                to,
                                              Stream             content,
                                              Int64              size,
                                              String             filename,
                                              String?            contentType  = null,
                                              MessageType        type         = MessageType.Chat,
                                              CancellationToken  ct           = default)
    {

        var sent = await _connection.SendFileAsync(to, content, size, filename, contentType, type, ct);

        // The same bookkeeping an ordinary message gets: a file message is a
        // message, and the last thing sent to somebody is what a correction
        // reaches for.
        if (sent.MessageId is not null)
        {
            lock (_lastSentToLock)
                _lastSentTo[LastSentKey(to)] = sent.MessageId;
        }

        return sent;

    }

    #region XEP-0084: avatars

    /// <summary>
    /// XEP-0084: publishes a picture - the data first, then what it is.
    /// </summary>
    /// <remarks>
    /// The order is not a detail: the metadata is what subscribers are told,
    /// and the first thing they do on being told is fetch the data by id.
    /// </remarks>
    public Task<AvatarInfo?> PublishAvatarAsync(Byte[]             image,
                                                String             type,
                                                Int32?             width   = null,
                                                Int32?             height  = null,
                                                CancellationToken  ct      = default)
        => _connection.PublishAvatarAsync(image, type, width, height, ct);

    /// <summary>
    /// XEP-0084, section 4: takes the picture down.
    /// </summary>
    public Task<Boolean> RemoveAvatarAsync(CancellationToken ct = default)
        => _connection.RemoveAvatarAsync(ct);

    /// <summary>
    /// XEP-0084: what somebody says their picture is - without the picture.
    /// </summary>
    /// <remarks>
    /// An empty list is an answer (no avatar); null is not (the question could
    /// not be asked). A caller choosing between a placeholder and a retry needs
    /// the difference.
    /// </remarks>
    public Task<IReadOnlyList<AvatarInfo>?> FetchAvatarInfoAsync(JID                bareJid,
                                                                 CancellationToken  ct = default)
        => _connection.FetchAvatarInfoAsync(bareJid, ct);

    /// <summary>
    /// XEP-0084: fetches the picture itself, checked against the id it was
    /// fetched under.
    /// </summary>
    public Task<Avatar?> FetchAvatarAsync(JID                bareJid,
                                          AvatarInfo         info,
                                          CancellationToken  ct = default)
        => _connection.FetchAvatarAsync(bareJid, info, ct);

    #endregion

    /// <summary>
    /// XEP-0454 and XEP-0363: sends a file the storage service cannot read.
    /// </summary>
    /// <remarks>
    /// <b>The encryption is against the storage, not against the
    /// conversation.</b> The key travels in the URL fragment to whoever is being
    /// sent the file, so anybody who can read the message can read the file -
    /// and if that message went in the clear, so did the key. Worth saying
    /// plainly, because <c>aesgcm://</c> in a body looks like more than it is:
    /// what it buys is that the host holding the bytes is not among the readers.
    /// </remarks>
    public async Task<FileSent> SendEncryptedFileAsync(JID                to,
                                                       Stream             content,
                                                       String             filename,
                                                       MessageType        type  = MessageType.Chat,
                                                       CancellationToken  ct    = default)
    {

        if (_connection.Upload is null)
            return new FileSent(new UploadOutcome(null), null);

        var encrypted = await _connection.Upload.UploadEncryptedAsync(content, filename,
                                                                      CancellationToken: ct);

        if (encrypted.Url is null)
            return new FileSent(encrypted.Upload, null);

        var messageId = await _connection.SendFileMessageAsync(to, encrypted.Url, type, ct);

        lock (_lastSentToLock)
            _lastSentTo[LastSentKey(to)] = messageId;

        return new FileSent(encrypted.Upload with { Url = encrypted.Url }, messageId);

    }

    /// <summary>
    /// XEP-0454: fetches an encrypted file and decrypts it.
    /// </summary>
    public Task<Byte[]?> DownloadEncryptedFileAsync(Uri url, CancellationToken ct = default)
        => _connection.Upload?.DownloadEncryptedAsync(url, ct) ?? Task.FromResult<Byte[]?>(null);

    /// <summary>
    /// Fetches a file somebody was told about.
    /// </summary>
    /// <remarks>
    /// Without authentication, because there is none to give: see
    /// <see cref="UploadManager.DownloadAsync"/>.
    /// </remarks>
    public Task<Byte[]?> DownloadFileAsync(Uri url, CancellationToken ct = default)
        => _connection.Upload?.DownloadAsync(url, ct) ?? Task.FromResult<Byte[]?>(null);

    #endregion

    #region XEP-0313: the archive

    /// <summary>
    /// XEP-0313: asks an archive for a page of what it kept.
    /// </summary>
    /// <param name="archive">
    /// Whose archive. Null is one's own server; a room's is asked by naming the
    /// room.
    /// </param>
    /// <remarks>
    /// The general form. <see cref="LastFromArchiveAsync"/> is the one an
    /// interface usually wants.
    /// </remarks>
    public Task<ArchivePage?> QueryArchiveAsync(JID?               archive            = null,
                                                JID?               with               = null,
                                                DateTimeOffset?    start              = null,
                                                DateTimeOffset?    end                = null,
                                                Int32?             max                = null,
                                                String?            before             = null,
                                                String?            after              = null,
                                                CancellationToken  cancellationToken  = default)

        => _connection.Mam?.QueryAsync(archive, with, start, end, max, before, after, cancellationToken)
               ?? Task.FromResult<ArchivePage?>(null);

    /// <summary>
    /// The end of a conversation, out of one's own archive.
    /// </summary>
    /// <remarks>
    /// <b>The last page and not the first</b>, which is the one thing about
    /// paging that is easy to get backwards: an archive counts from the
    /// beginning, and what somebody opening a conversation wants to see is the
    /// end of it. An empty <c>&lt;before/&gt;</c> is how XEP-0059 says that, and
    /// it is different from leaving it out - which asks for the oldest messages
    /// there are.
    /// </remarks>
    public Task<ArchivePage?> LastFromArchiveAsync(JID                with,
                                                   Int32              howMany            = 20,
                                                   CancellationToken  cancellationToken  = default)

        => QueryArchiveAsync(with:              with,
                             max:               howMany,
                             before:            "",
                             cancellationToken: cancellationToken);

    /// <summary>
    /// What was said in a room before we walked in.
    /// </summary>
    /// <remarks>
    /// The room's own archive, which is a different one from ours: we were not
    /// there, so our server never saw any of it. This is the reason a room
    /// without an archive is a room one enters blind - and, since D116, the
    /// reason archiving is switched on in both test set-ups.
    /// </remarks>
    public Task<ArchivePage?> RoomHistoryAsync(JID                room,
                                               Int32              howMany            = 20,
                                               CancellationToken  cancellationToken  = default)

        => QueryArchiveAsync(archive:           room.Bare,
                             max:               howMany,
                             before:            "",
                             cancellationToken: cancellationToken);

    #endregion

    #region XEP-0045: rooms

    /// <summary>
    /// The rooms this client is in, or is entering.
    /// </summary>
    public IReadOnlyDictionary<JID, MucRoom> Rooms
        => _connection.Muc?.Rooms ?? new Dictionary<JID, MucRoom>();

    /// <summary>
    /// The room with this address, or null.
    /// </summary>
    public MucRoom? Room(JID address)
        => _connection.Muc?.Room(address);

    /// <summary>
    /// XEP-0045, section 7.2: enters a room.
    /// </summary>
    /// <param name="room">The bare address of the room.</param>
    /// <param name="nick">The name to be known by in there.</param>
    /// <param name="password">For a password-protected room.</param>
    /// <param name="historyMaxStanzas">
    /// How much of what was said before to send along; zero asks for none.
    /// </param>
    /// <returns>
    /// The room, the refusal, or neither - see <see cref="MucJoinOutcome"/>.
    /// Being refused from a room is an ordinary thing for a room to do, so it
    /// comes back as an answer and not as an exception.
    /// </returns>
    public Task<MucJoinOutcome> JoinRoomAsync(JID                room,
                                              string             nick,
                                              string?            password           = null,
                                              int?               historyMaxStanzas  = null,
                                              CancellationToken  cancellationToken  = default)

        => _connection.Muc?.JoinAsync(room, nick, password, historyMaxStanzas, cancellationToken)
               ?? Task.FromResult(new MucJoinOutcome(null, null));

    /// <summary>
    /// XEP-0045, section 10.1.2: accepts the default configuration of a room
    /// that has just come into being.
    /// </summary>
    /// <remarks>
    /// To be called after a join that reported <see cref="MucRoom.WasCreated"/>.
    /// Until it is, the room is locked and nobody else can enter - a room only
    /// its creator can see, with nothing anywhere saying so.
    /// </remarks>
    public Task<bool> CreateInstantRoomAsync(JID                room,
                                             CancellationToken  cancellationToken = default)
        => _connection.CreateInstantRoomAsync(room, cancellationToken);

    /// <summary>
    /// XEP-0045, section 7.14: leaves a room.
    /// </summary>
    /// <returns>false when this client was not in that room.</returns>
    public Task<bool> LeaveRoomAsync(JID room, string? status = null)
        => _connection.Muc?.LeaveAsync(room, status) ?? Task.FromResult(false);

    /// <summary>
    /// XEP-0045, section 7.6: takes a different name in a room.
    /// </summary>
    /// <remarks>
    /// Whether the room allows it comes back as a presence and not as a result -
    /// the name may be taken, or reserved for somebody else.
    /// </remarks>
    public Task<bool> ChangeRoomNickAsync(JID room, string newNick)
        => _connection.Muc?.ChangeNickAsync(room, newNick) ?? Task.FromResult(false);

    /// <summary>
    /// XEP-0045, section 7.2.16: sets the subject of a room.
    /// </summary>
    public Task<bool> SetRoomSubjectAsync(JID room, string subject)
        => _connection.Muc?.SetSubjectAsync(room, subject) ?? Task.FromResult(false);

    /// <summary>
    /// XEP-0045, section 9.2: throws somebody out of a room for this visit.
    /// </summary>
    /// <remarks>
    /// They may come back: a kick takes away a role, and a role lasts only as
    /// long as somebody is in the room. That is also why a nickname is enough
    /// here and not enough for <see cref="BanFromRoomAsync"/>.
    /// </remarks>
    public Task<bool> KickFromRoomAsync(JID                room,
                                        string             nick,
                                        string?            reason             = null,
                                        CancellationToken  cancellationToken  = default)
        => _connection.Muc?.KickAsync(room, nick, reason, cancellationToken) ?? Task.FromResult(false);

    /// <summary>
    /// XEP-0045, section 9.1: keeps somebody out of a room for good.
    /// </summary>
    /// <remarks>
    /// <b>Needs their real address</b>, which an ordinary room gives only to its
    /// moderators - so this can fail for a reason that has nothing to do with
    /// permissions. <see cref="MucOccupant.RealJid"/> being null is where that
    /// shows, and there is nothing this library can do about it: an affiliation
    /// outlives the visit, so it has to name somebody who exists outside it.
    /// </remarks>
    public Task<bool> BanFromRoomAsync(JID                room,
                                       JID                jid,
                                       string?            reason             = null,
                                       CancellationToken  cancellationToken  = default)
        => _connection.Muc?.BanAsync(room, jid, reason, cancellationToken) ?? Task.FromResult(false);

    /// <summary>
    /// XEP-0045, section 8: what somebody may do in a room while they are in it.
    /// </summary>
    public Task<bool> SetRoomRoleAsync(JID                room,
                                       string             nick,
                                       MucRole            role,
                                       string?            reason             = null,
                                       CancellationToken  cancellationToken  = default)
        => _connection.Muc?.SetRoleAsync(room, nick, role, reason, cancellationToken) ?? Task.FromResult(false);

    /// <summary>
    /// XEP-0045, section 9: what somebody is to a room, beyond this visit.
    /// </summary>
    public Task<bool> SetRoomAffiliationAsync(JID                room,
                                              JID                jid,
                                              MucAffiliation     affiliation,
                                              string?            reason             = null,
                                              CancellationToken  cancellationToken  = default)
        => _connection.Muc?.SetAffiliationAsync(room, jid, affiliation, reason, cancellationToken) ?? Task.FromResult(false);

    /// <summary>
    /// XEP-0045, section 7.8.1: asks somebody into a room, through the room.
    /// </summary>
    /// <returns>
    /// false when this client is not in that room. <b>true means sent, not
    /// delivered</b> - see <see cref="OnInvitationRefused"/>.
    /// </returns>
    /// <remarks>
    /// Through the room and not straight to the person: an invitation the room
    /// forwarded is one the room will honour, and it can put the invitee on the
    /// member list on the way. One sent directly is a stranger's word about a
    /// room they have never heard of.
    ///
    /// <b>Whether one may ask at all is the room's to decide</b>
    /// (<c>muc#roomconfig_allowinvites</c>), and the default differs: ejabberd
    /// lets nobody but the owner invite, Prosody lets anybody who is in the
    /// room. A refusal comes back afterwards and separately, because a message
    /// has no answer to wait for.
    /// </remarks>
    public Task<bool> InviteToRoomAsync(JID room, JID who, string? reason = null)
        => _connection.Muc?.InviteAsync(room, who, reason) ?? Task.FromResult(false);

    /// <summary>
    /// XEP-0045, section 7.5: says something to one occupant of a room and to
    /// nobody else in it.
    /// </summary>
    /// <param name="nick">
    /// Whom - by what they are called <b>in this room</b>. Their real address
    /// is not used and usually not known: a semi-anonymous room gives it to
    /// nobody, and the room is what routes this.
    /// </param>
    /// <returns>The message id, or null when this client is not in that room.</returns>
    /// <remarks>
    /// <b>type=chat and never groupchat.</b> The section is explicit, and the
    /// mistake it is guarding against is the one that cannot be taken back: a
    /// groupchat to <c>room@service/nick</c> is either refused or shown to
    /// everybody, and which of the two depends on the service.
    ///
    /// The id is recorded against the <b>occupant</b> address, not the room -
    /// see <see cref="LastSentKey"/> for what goes wrong otherwise.
    /// </remarks>
    public async Task<string?> SendRoomPrivateMessageAsync(JID     room,
                                                           string  nick,
                                                           string  body)
    {

        if (_connection.Muc?.IsRoom(room.Bare) != true)
            return null;

        var to = JID.Parse($"{room.Bare}/{nick}");

        var id = await _connection.SendRoomPrivateMessageAsync(to, body);

        lock (_lastSentToLock)
            _lastSentTo[LastSentKey(to)] = id;

        return id;

    }

    /// <summary>
    /// XEP-0045, section 8.6: asks a moderated room to be allowed to speak.
    /// </summary>
    /// <returns>
    /// false when this client is not in that room. <b>true means asked</b>, not
    /// granted - nobody answers a voice request, and what comes back if a
    /// moderator agrees is a presence carrying a new role.
    /// </returns>
    public Task<bool> RequestVoiceAsync(JID                room,
                                        CancellationToken  cancellationToken = default)
        => _connection.Muc?.RequestVoiceAsync(room, cancellationToken) ?? Task.FromResult(false);

    /// <summary>
    /// XEP-0045, section 8.6: a moderator's yes or no to a voice request.
    /// </summary>
    /// <remarks>
    /// A refusal is worth sending: a room that hears nothing goes on showing
    /// the request, and the person waiting to speak is told neither way.
    /// </remarks>
    public Task<bool> AnswerVoiceRequestAsync(MucVoiceRequest    request,
                                              Boolean            allow,
                                              CancellationToken  cancellationToken = default)
        => _connection.Muc?.AnswerVoiceRequestAsync(request, allow, cancellationToken) ?? Task.FromResult(false);

    /// <summary>
    /// XEP-0045, section 7.10: claims a nickname in a room, so that nobody
    /// else may enter under it.
    /// </summary>
    /// <remarks>
    /// Reserving a name keeps others out; it does not put anybody in.
    /// </remarks>
    public Task<bool> ReserveRoomNicknameAsync(JID                room,
                                               String             nick,
                                               CancellationToken  cancellationToken = default)
        => _connection.Muc?.ReserveNicknameAsync(room, nick, cancellationToken) ?? Task.FromResult(false);

    /// <summary>
    /// XEP-0045, section 7.10: what nickname this account holds in a room.
    /// </summary>
    /// <returns>
    /// null when the room would not say at all - which a service without
    /// reservations answers - and a record otherwise. Not registered is an
    /// answer, and a different one from no answer.
    /// </returns>
    public Task<MucNicknameRegistration?> RoomNicknameAsync(JID                room,
                                                            CancellationToken  cancellationToken = default)
        => _connection.Muc?.RegisteredNicknameAsync(room, cancellationToken)
               ?? Task.FromResult<MucNicknameRegistration?>(null);

    /// <summary>
    /// XEP-0045, section 10.9: takes the room down, which only its owner may.
    /// </summary>
    /// <remarks>
    /// <b>Name the alternative if there is one.</b> The service hands it to
    /// every occupant, and it is the difference between a room that moved and
    /// a room that vanished.
    /// </remarks>
    public Task<bool> DestroyRoomAsync(JID                room,
                                       string?            reason             = null,
                                       JID?               alternate          = null,
                                       string?            password           = null,
                                       CancellationToken  cancellationToken  = default)
        => _connection.Muc?.DestroyRoomAsync(room, reason, alternate, password, cancellationToken)
               ?? Task.FromResult(false);

    /// <summary>
    /// XEP-0045, section 9.5: who is on one of the room's lists.
    /// </summary>
    /// <returns>
    /// null when the room would not say, an empty list when it said nobody.
    /// The two are different answers.
    /// </returns>
    public Task<IReadOnlyList<MucAffiliated>?> RoomAffiliationsAsync(JID                room,
                                                                     MucAffiliation     affiliation,
                                                                     CancellationToken  cancellationToken  = default)
        => _connection.Muc?.AffiliationsAsync(room, affiliation, cancellationToken)
               ?? Task.FromResult<IReadOnlyList<MucAffiliated>?>(null);

    /// <summary>
    /// XEP-0045, section 7.8.2: says no to an invitation.
    /// </summary>
    /// <remarks>
    /// Addressed to whoever asked, sent through the room. A refusal sent to the
    /// room itself is delivered to nobody.
    /// </remarks>
    public Task<bool> DeclineInvitationAsync(JID room, JID inviter, string? reason = null)
        => _connection.Muc?.DeclineAsync(room, inviter, reason) ?? Task.FromResult(false);

    /// <summary>
    /// Says something in a room.
    /// </summary>
    /// <remarks>
    /// To the bare address and as <c>groupchat</c>, which is what makes the room
    /// hand it to everybody rather than to one occupant. A delivery receipt and
    /// a chat marker are not requested and could not be: in a room everybody
    /// present would see the acknowledgements.
    /// </remarks>
    public Task<string> SendRoomMessageAsync(JID room, string body)
        => _connection.SendMessageAsync(room.Bare, body, type: MessageType.GroupChat);

    /// <summary>
    /// XEP-0384 in a room: says something only the people in it can read.
    /// </summary>
    /// <remarks>
    /// <b>Possible only in a non-anonymous room</b>, and the result says so
    /// rather than this throwing: one encrypts to real addresses and a
    /// semi-anonymous room hands out nicknames. <see cref="CannotEncryptInRoom"/>
    /// asks the same question beforehand, which is what a client draws its lock
    /// from; <see cref="MakeRoomNonAnonymousAsync"/> is what changes it.
    /// </remarks>
    public Task<OmemoRoomSent> SendEncryptedRoomMessageAsync(JID                room,
                                                             string             body,
                                                             CancellationToken  ct = default)
        => _connection.SendEncryptedRoomMessageAsync(room, body, ct);

    /// <summary>
    /// Why this room cannot carry an encrypted message, or null when it can.
    /// </summary>
    /// <remarks>
    /// Phrased as the reason and not as a Boolean on purpose: a lock that is
    /// greyed out and says nothing is a client telling somebody that encryption
    /// is impossible without telling them it is one setting away.
    /// </remarks>
    public string? CannotEncryptInRoom(JID room)

        => _connection.Muc?.Room(room) is MucRoom joined
               ? OmemoRooms.WhyNot(joined)
               : $"{room.Bare} has not been entered";

    /// <summary>
    /// XEP-0045, section 10.2: changes some settings of a room.
    /// </summary>
    /// <remarks>
    /// The whole form is fetched, changed and sent back - see
    /// <see cref="XMPPConnection.ConfigureRoomAsync"/> for why the shorter way
    /// resets a room.
    /// </remarks>
    public Task<bool> ConfigureRoomAsync(JID                                  room,
                                         IReadOnlyDictionary<string, string>  values,
                                         CancellationToken                    ct = default)
        => _connection.ConfigureRoomAsync(room, values, ct);

    /// <summary>
    /// XEP-0045, section 10.2.1: makes a room show everybody's real address,
    /// which is what end-to-end encryption in it needs.
    /// </summary>
    /// <remarks>
    /// <b>It changes the room for everybody in it.</b> From then on every
    /// occupant can see who every other occupant really is - which is the price
    /// of being able to encrypt to them.
    /// </remarks>
    public Task<bool> MakeRoomNonAnonymousAsync(JID                room,
                                                CancellationToken  ct = default)
        => _connection.MakeRoomNonAnonymousAsync(room, ct);

    /// <summary>
    /// XEP-0045, section 10.2: what a room is set to, as the service reports
    /// it - or null when it will not say, which it does to anybody but the
    /// owner.
    /// </summary>
    public Task<XElement?> FetchRoomConfigAsync(JID                room,
                                                CancellationToken  ct = default)
        => _connection.FetchRoomConfigAsync(room, ct);

    #endregion

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

    /// <summary>
    /// XEP-0384: the OMEMO manager, as soon as it is switched on.
    /// </summary>
    public OmemoManager? Omemo => _connection.Omemo;

    /// <summary>
    /// XEP-0384: Is OMEMO switched on?
    /// </summary>
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

    #region IQs of one's own

    /// <summary>
    /// Registers a handler for IQ requests this library does not implement
    /// itself - a protocol of one's own over the transport XMPP already has.
    /// </summary>
    /// <remarks>
    /// <b>Register before connecting where the extensions are fixed.</b> The
    /// feature list this adds to is what the caps hash of XEP-0115 is computed
    /// from, and that hash travels in presence: a handler registered afterwards
    /// is invisible to every peer that has already cached the old one, until the
    /// next presence goes out. <see cref="XMPPConnection.RegisterIqHandler"/>
    /// says why this does not send that presence itself.
    /// </remarks>
    public Boolean RegisterIqHandler(String                    Namespace,
                                     String                    Element,
                                     IqRequestHandlerDelegate  Handler,
                                     Boolean                   AnnounceInDisco = true)

        => _connection.RegisterIqHandler(Namespace, Element, Handler, AnnounceInDisco);

    /// <summary>
    /// Takes a handler back, and its feature announcement with it.
    /// </summary>
    public Boolean UnregisterIqHandler(String Namespace, String Element)
        => _connection.UnregisterIqHandler(Namespace, Element);

    /// <summary>
    /// What this client tells the world it speaks (XEP-0030), including the
    /// namespaces of handlers registered here.
    /// </summary>
    /// <remarks>
    /// Read-only on purpose. A feature belongs to whatever answers it, so it is
    /// added and withdrawn by <see cref="RegisterIqHandler"/> and
    /// <see cref="UnregisterIqHandler"/> - a list anybody could append to would
    /// let this client promise something nothing here answers, and the promise
    /// is what a peer acts on.
    /// </remarks>
    public IReadOnlyList<String> AnnouncedFeatures
        => _connection.Disco?.LocalFeatures ?? [];

    /// <summary>
    /// Sends an IQ request of one's own and waits for the answer.
    /// </summary>
    /// <returns>
    /// The whole answer stanza, or null on a timeout. An <c>&lt;iq
    /// type='error'/&gt;</c> comes back as an answer, because only the caller
    /// knows whether a refusal is a failure for their protocol.
    /// </returns>
    public Task<XElement?> SendIqAsync(JID?               To,
                                       String             Type,
                                       XElement           Payload,
                                       CancellationToken  CancellationToken = default)

        => _connection.SendIqAsync(To, Type, Payload, CancellationToken);

    #endregion

    /// <summary>
    /// XEP-0384: Sends an encrypted message.
    /// </summary>
    /// <returns>
    /// The id of the stanza, the devices that cannot read along - empty means
    /// all can - and whether anybody on the far side can read it at all.
    /// </returns>
    public async Task<OmemoSent> SendEncryptedMessageAsync(JID                to,
                                                           string             body,
                                                           CancellationToken  ct = default)
    {

        var sent = await _connection.SendEncryptedMessageAsync(to, body, ct);

        // The same bookkeeping a plain message gets: what was last written to
        // this address is what a later correction (XEP-0308) names. Missing
        // here until now, which made every encrypted message the one message
        // that could not be corrected.
        lock (_lastSentToLock)
            _lastSentTo[LastSentKey(to)] = sent.MessageId;

        return sent;

    }

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

    /// <summary>
    /// What am I where? (XEP-0060, section 5.7)
    /// </summary>
    public Task<IReadOnlyList<(String NodeId, PubSubAffiliation Affiliation)>?> PubSubGetAffiliationsAsync(JID? service = null)
        => _connection.PubSubGetAffiliationsAsync(service);

    /// <summary>
    /// Who is what at my node? (XEP-0060, section 8.9.1)
    /// </summary>
    public Task<IReadOnlyList<(String Jid, PubSubAffiliation Affiliation)>?> PubSubGetNodeAffiliationsAsync(String nodeId, JID? service = null)
        => _connection.PubSubGetNodeAffiliationsAsync(nodeId, service);

    /// <summary>
    /// Grants or takes a role (XEP-0060, section 8.9.2).
    /// </summary>
    public Task<Boolean> PubSubSetAffiliationAsync(String nodeId, JID jid, PubSubAffiliation affiliation, JID? service = null)
        => _connection.PubSubSetAffiliationAsync(nodeId, jid, affiliation, service);

    /// <summary>
    /// Answers an application for a subscription (XEP-0060, section 8.6.2).
    /// </summary>
    public Task PubSubAnswerSubscriptionRequestAsync(PubSubSubscribeAuthorization request, Boolean allow, JID? service = null)
        => _connection.PubSubAnswerSubscriptionRequestAsync(request, allow, service);

    /// <summary>
    /// Who hangs on my node? (XEP-0060, section 8.8.1)
    /// </summary>
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

    /// <summary>
    /// Reads the settings of a node.
    /// </summary>
    public Task<PubSubNodeConfiguration?> PubSubGetNodeConfigAsync(String nodeId, JID? service = null)
        => _connection.PubSubGetNodeConfigAsync(nodeId, service);

    /// <summary>
    /// Configures a node - only the owner may do that.
    /// </summary>
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
