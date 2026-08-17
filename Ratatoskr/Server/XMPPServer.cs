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

using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

// Hermod brings along a type IPAddress of its own that would hide the one of
// the same name from System.Net. An alias beats every using directive on the
// same level, so it suffices up here - as long as Ratatoskr does not itself lie
// beneath Hermod. That is exactly what it was not until the move: as a
// namespace member of a surrounding namespace, Hermod's IPAddress won against
// the alias, and the alias had to go into the body of the namespace
// declaration.
using IPAddress = System.Net.IPAddress;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    #region (delegate) OnXMPPServer...Delegate

    /// <summary>
    /// A stanza was received from a client.
    /// </summary>
    public delegate Task OnXMPPServerStanzaReceivedDelegate      (DateTimeOffset     Timestamp,
                                                                  XMPPServer         Sender,
                                                                  XMPPSession        Session,
                                                                  String             Frame,
                                                                  CancellationToken  CancellationToken);

    /// <summary>
    /// A session was bound successfully.
    /// </summary>
    public delegate Task OnXMPPServerSessionBoundDelegate        (DateTimeOffset     Timestamp,
                                                                  XMPPServer         Sender,
                                                                  XMPPSession        Session,
                                                                  CancellationToken  CancellationToken);

    /// <summary>
    /// A stanza from another server was refused - with the peer domain and
    /// the reason.
    /// </summary>
    public delegate Task OnXMPPServerRemoteStanzaRejectedDelegate(DateTimeOffset     Timestamp,
                                                                  XMPPServer         Sender,
                                                                  String             PeerDomain,
                                                                  String             Reason,
                                                                  CancellationToken  CancellationToken);

    /// <summary>
    /// Processing a frame ended in an exception - with the session, the frame
    /// and the exception.
    /// </summary>
    public delegate Task OnXMPPServerInternalErrorDelegate       (DateTimeOffset     Timestamp,
                                                                  XMPPServer         Sender,
                                                                  XMPPSession        Session,
                                                                  String             Frame,
                                                                  Exception          Exception,
                                                                  CancellationToken  CancellationToken);

    #endregion


    /// <summary>
    /// A minimal XMPP-over-WebSocket server (RFC 7395).
    ///
    /// Intended as a peer for tests and for development, not for production
    /// use: a persistent account management is missing.
    ///
    /// The transport - WebSocket frames, connection management and TLS - is
    /// delivered by Hermod's <c>AWebSocketServer</c>; here stands only the
    /// protocol.
    ///
    /// It masters enough of the protocol that several real
    /// <c>XMPPClient</c> instances can log in at the same time and speak with
    /// one another:
    ///
    /// <list type="bullet">
    ///   <item>SASL PLAIN against accounts on record</item>
    ///   <item>Resource binding with a unique resource per connection</item>
    ///   <item>Routing of message, presence and iq between the sessions</item>
    ///   <item>Presence only to subscribers, probe included (RFC 6121, section 4)</item>
    ///   <item>The subscription handshake with roster pushes to both sides (section 3)</item>
    ///   <item>XEP-0280 message carbons between the resources of an account</item>
    ///   <item>A server-side roster including the roster push</item>
    ///   <item>XEP-0199 ping, to the server and between clients</item>
    ///   <item>XEP-0198 stream management with its own, independent counting</item>
    /// </list>
    ///
    /// It produces error cases only where a switch demands it.
    /// </summary>
    public sealed class XMPPServer : IAsyncDisposable
    {

        #region Data

        private readonly XMPPWebSocketServer _webSocketServer;
        private readonly IXMPPAccountStore _accountStore;
        private readonly CancellationTokenSource _cts = new();
        private readonly Dictionary<String, XMPPAccount> _accounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<XMPPSession> _sessions = [];

        /// <summary>
        /// XEP-0198, section 5: dropped streams waiting for their returner - by
        /// their identifier.
        /// </summary>
        private readonly Dictionary<String, ParkedStream> _resumable = new(StringComparer.Ordinal);

        private Timer? _resumptionSweeper;
        private readonly Lock _lock = new();

        /// <summary>
        /// The key the invented credentials of unknown accounts arise from
        /// (RFC 6120, section 13.11).
        /// </summary>
        /// <remarks>
        /// One per server, from the random generator. It must not be guessable:
        /// whoever knows it can recompute every invented salt and tell again
        /// which account exists.
        /// </remarks>
        /// <remarks>
        /// Kept in the account store now and no longer drawn afresh at every
        /// start. A key that changes with the process makes the invented salts
        /// change across a restart while the real ones stand - so whoever asks
        /// for the same name before and after the restart sees which of the two
        /// it was, and that is the very question the decoy exists to leave
        /// unanswered. A store that keeps nothing, such as the in-memory one,
        /// still gets a fresh key: it has no restart to survive.
        /// </remarks>
        private readonly Byte[] _decoySecret;

        private Int32 _connectionCounter;

        #endregion

        #region Properties

        /// <summary>
        /// The port served.
        /// </summary>
        /// <remarks>
        /// The port that was actually bound. Constructed with 0 - "whichever is
        /// free" - it stays 0 until <see cref="Start"/> has run, because until
        /// then nobody has chosen. <see cref="Uri"/> is therefore only
        /// meaningful after the start, which is the order everything here uses
        /// anyway.
        /// </remarks>
        public Int32 Port { get; private set; }

        /// <summary>
        /// The domain the server is responsible for.
        /// </summary>
        public String Domain { get; }

        /// <summary>
        /// The self-signed certificate of this server, or null when it speaks
        /// in the clear.
        /// </summary>
        public X509Certificate2? Certificate { get; }

        /// <summary>
        /// The WebSocket URI for the client.
        /// </summary>
        public URL Uri => URL.Parse($"{(Certificate is not null ? "wss" : "ws")}://localhost:{Port}/ws/");

        /// <summary>
        /// The number of all connections ever accepted.
        /// </summary>
        public Int32 ConnectionCount => Volatile.Read(ref _connectionCounter);

        /// <summary>
        /// All sessions currently open.
        /// </summary>
        public IReadOnlyList<XMPPSession> Sessions
        {
            get { lock (_lock) return _sessions.Where(s => s.IsOpen).ToList(); }
        }

        /// <summary>
        /// All frames of all sessions, regardless of the sender.
        /// </summary>
        public IReadOnlyList<String> AllReceived
        {
            get { lock (_lock) return _sessions.SelectMany(s => s.Received).ToList(); }
        }

        /// <summary>
        /// All sessions of this server, ended ones included - in the order in
        /// which they were established.
        /// </summary>
        /// <remarks>
        /// <see cref="Sessions"/> shows only the open ones, and those are
        /// precisely what no longer exists when a setup failed at the login.
        /// Whoever wants to check what the server answered a refused client
        /// finds the session only here.
        /// </remarks>
        public IReadOnlyList<XMPPSession> AllSessions
        {
            get { lock (_lock) return [.. _sessions]; }
        }

        #endregion

        #region Behaviour switches

        /// <summary>
        /// Does the server answer the close frame of the client? On false a
        /// server can be simulated that leaves the handshake open: it holds its
        /// answer back for <c>SilentCloseDelay</c> while the connection stays
        /// open.
        /// </summary>
        public Boolean CompleteCloseHandshake { get; set; } = true;

        /// <summary>
        /// Does the server support subscription pre-approval (RFC 6121,
        /// section 3.4)?
        /// </summary>
        /// <remarks>
        /// Optional for servers <b>and</b> clients. The section demands that a
        /// server that masters it also announces it - and that a client does
        /// not even attempt it without an announcement. The switch steers both
        /// together: without it the announcement is missing, and a
        /// <c>&lt;presence type='subscribed'/&gt;</c> without an open request
        /// stays without consequence instead of admitting in advance.
        /// </remarks>
        public Boolean OfferSubscriptionPreApproval { get; set; } = true;

        /// <summary>
        /// Does the server support roster versioning (RFC 6121, section 2.6)?
        /// </summary>
        /// <remarks>
        /// As with pre-approval the switch steers both sides of the bargain:
        /// without it the announcement is missing, a <c>ver</c> on the request
        /// is not heeded, and neither the result nor the push carries one. That
        /// is more important than it sounds - a server that silently passes
        /// over a <c>ver</c> and nevertheless sends an empty result would bring
        /// the client to take an empty roster for the current state.
        /// </remarks>
        public Boolean OfferRosterVersioning { get; set; } = true;

        /// <summary>
        /// How many unanswered subscription requests are kept per account
        /// (RFC 6121, section 3.1.3).
        /// </summary>
        /// <remarks>
        /// The section demands the keeping and warns against it in the same
        /// breath: what is kept is what strangers send, and a request may carry
        /// arbitrary extended content. The security warning explicitly advises
        /// an upper bound ("limits on the number or size of inbound presence
        /// subscription requests that the server will store in aggregate or for
        /// any given contact").
        ///
        /// Once the bound is reached, the new request is discarded instead of
        /// displacing one already kept. The other way round an attacker could
        /// deliberately push out the real request of an acquaintance - the
        /// contact would then get rubbish to see and not what was expected.
        /// </remarks>
        public Int32 MaxStoredSubscriptionRequests { get; set; } = 100;

        /// <summary>
        /// Does the server keep messages for an account without a reachable
        /// resource (XEP-0160)?
        /// </summary>
        /// <remarks>
        /// RFC 6121, section 8.5.2.2.1 puts two routes side by side: store the
        /// message or answer the sender with
        /// <c>&lt;service-unavailable/&gt;</c>. Both are right, and this switch
        /// chooses between them - switched off the server is therefore not less
        /// conformant, only less convenient.
        ///
        /// What it must not do is the third possibility: discard silently. That
        /// is exactly what this server did up to here, and it is the most
        /// unpleasant of the three routes - the sender takes their message for
        /// delivered.
        ///
        /// The switch also steers the announcement in disco#info
        /// (<c>msgoffline</c>): a client shall not have to notice from the
        /// absent error what the server does with messages to the absent.
        /// </remarks>
        public Boolean StoreOfflineMessages { get; set; } = true;

        /// <summary>
        /// How many messages are kept per account.
        /// </summary>
        /// <remarks>
        /// What is kept is what strangers send - the same situation as with
        /// <see cref="MaxStoredSubscriptionRequests"/>, and without a bound the
        /// storage would itself be the weak point. Once the bound is reached,
        /// the new message is refused and none of those kept is displaced: a
        /// refused message is reported to the sender, a displaced one
        /// disappears unnoticed.
        /// </remarks>
        public Int32 MaxStoredOfflineMessages { get; set; } = 100;

        /// <summary>
        /// Which SASL mechanisms the server offers, in the order of the
        /// announcement.
        /// </summary>
        /// <remarks>
        /// The client chooses itself, and takes the strongest one it knows. The
        /// default corresponds to what widespread servers offer. PLAIN is among
        /// them because it is defensible behind TLS and older clients can do
        /// nothing else - for the counter-check the list can be narrowed.
        ///
        /// A mechanism missing here is refused even when a client attempts it
        /// anyway.
        /// </remarks>
        public IList<String> OfferedSaslMechanisms { get; } =
            ["SCRAM-SHA-256", "SCRAM-SHA-1", "PLAIN"];

        /// <summary>
        /// The <c>tls-server-end-point</c> data this server binds to (RFC
        /// 5929), or null when it has no certificate or one this binding is not
        /// defined for.
        /// </summary>
        /// <remarks>
        /// Computed once from the certificate rather than per login: the
        /// certificate does not change while the server runs, and hashing it on
        /// every handshake would be work done to reach the same answer.
        /// </remarks>
        public Byte[]? ChannelBindingData => OfferChannelBinding ? _channelBindingData : null;

        private readonly Byte[]? _channelBindingData;

        /// <summary>
        /// Whether this server offers channel binding at all. Default true.
        /// </summary>
        /// <remarks>
        /// A switch and not a constant, for the tests that measure how a client
        /// ranks mechanisms rather than whether it binds. With binding on, every
        /// SCRAM login over TLS becomes a <c>-PLUS</c> one, and a fixture asking
        /// "does it prefer SHA-256 to SHA-1" would be answered in terms of
        /// mechanisms it never named. Turning it off there keeps each fixture
        /// measuring one thing.
        ///
        /// It also stands in for the deployment that cannot bind - TLS
        /// terminated by something in front of the server, which is the common
        /// case behind a reverse proxy.
        /// </remarks>
        public Boolean OfferChannelBinding { get; set; } = true;

        /// <summary>
        /// Whether this server offers the SASL2 profile (XEP-0388) beside the
        /// one from RFC 6120. Default true.
        /// </summary>
        /// <remarks>
        /// Both are announced, which the XEP provides for: it is a replacement
        /// profile and expects a transition in which a client speaks whichever
        /// it knows. A switch rather than a constant, so that the RFC 6120 path
        /// stays measurable - with SASL2 on, a client that prefers it would
        /// never take the old route again, and the older half of the
        /// negotiation would quietly stop being tested.
        /// </remarks>
        public Boolean OfferSasl2 { get; set; } = true;

        /// <summary>
        /// Whether this server offers SASL upgrade tasks (XEP-0480). Default
        /// true; needs <see cref="OfferSasl2"/>, which carries them.
        /// </summary>
        public Boolean OfferScramUpgrades { get; set; } = true;

        /// <summary>
        /// Whether this server binds a resource inline during SASL2
        /// (XEP-0386). Default true; needs <see cref="OfferSasl2"/>.
        /// </summary>
        /// <remarks>
        /// A switch for the same reason the others are: with it on, no client
        /// that speaks SASL2 ever takes the <c>&lt;iq/&gt;</c> route again, and
        /// the RFC 6120 binding - which every server in the world still speaks
        /// - would stop being tested here.
        /// </remarks>
        public Boolean OfferBind2 { get; set; } = true;

        /// <summary>
        /// The upgrade tasks this server can run.
        /// </summary>
        /// <remarks>
        /// Every SCRAM mechanism this server implements - deliberately not just
        /// the ones it currently offers.
        ///
        /// Tying it to <see cref="OfferedSaslMechanisms"/> was the first
        /// instinct and it is wrong, because it forbids the case the extension
        /// is for. An operator moving from SCRAM-SHA-1 to SCRAM-SHA-256 has
        /// accounts with no SHA-256 material: announce SHA-256 before the
        /// material exists and every login with it fails, which is the outage
        /// the upgrade is meant to avoid. What they want is to collect the
        /// material first, while still offering only what works, and to switch
        /// the offer once enough accounts have been through. That is only
        /// possible if an upgrade may name a mechanism that is not on offer
        /// yet.
        /// </remarks>
        public IEnumerable<String> SupportedUpgradeTasks

            => Enum.GetValues<SCRAMMechanism>().
                    Select(ScramUpgrade.TaskNameOf).
                    Distinct();

        /// <summary>
        /// What actually goes into <c>&lt;mechanisms/&gt;</c>: the offered list,
        /// with the <c>-PLUS</c> variants added when there is a channel binding
        /// to back them.
        /// </summary>
        /// <remarks>
        /// Derived rather than stored, and that is the point. Announcing
        /// <c>SCRAM-SHA-256-PLUS</c> without a binding invites a client to bind
        /// to nothing: the exchange then fails at the proof with no reason a
        /// human could read. Whether the server has a certificate is decided at
        /// construction and cannot change afterwards, so the two can never drift
        /// apart.
        ///
        /// The <c>-PLUS</c> entries come first only for readability; a client
        /// choosing by announcement order rather than by its own ranking is
        /// doing something the RFC warns against, and this server is not the
        /// place to reward it.
        /// </remarks>
        public IEnumerable<String> AnnouncedSaslMechanisms

            => ChannelBindingData is null
                   ? OfferedSaslMechanisms
                   : OfferedSaslMechanisms.
                         Where (m => m.StartsWith("SCRAM-", StringComparison.Ordinal)).
                         Select(m => m + "-PLUS").
                         Concat(OfferedSaslMechanisms);

        /// <summary>
        /// Does the server send a wrong server signature in the
        /// <c>&lt;success/&gt;</c>?
        /// </summary>
        /// <remarks>
        /// For the counter-check on the second half of SCRAM: a server that
        /// does not know the password cannot produce it. The client then has to
        /// refuse the login (RFC 5802, section 5).
        /// </remarks>
        public Boolean CorruptScramSignature { get; set; } = false;

        /// <summary>
        /// Does the server hash a different list of mechanisms into the
        /// server-first-message than the one it announced (XEP-0474)?
        /// </summary>
        /// <remarks>
        /// For the counter-check on the downgrade protection, and it stands in
        /// for the attacker rather than imitating him exactly. What a man in
        /// the middle really does is take a mechanism out of
        /// <c>&lt;features/&gt;</c> on the way to the client; he cannot be
        /// arranged inside a test that owns both ends, and he does not need to
        /// be - either way the client sees an <c>h</c> that describes an
        /// announcement other than the one that reached it, which is the whole
        /// of what it can detect.
        ///
        /// Worth knowing why he gains nothing by recomputing <c>h</c> himself,
        /// which he can, since the hash is unkeyed: the attribute sits inside
        /// the server-first-message, and RFC 5802 puts that into the
        /// AuthMessage verbatim. Change it and the client's proof no longer
        /// matches what the server computes. He may have the hash or the proof.
        /// </remarks>
        public Boolean SignAnotherSaslAnnouncement { get; set; } = false;

        /// <summary>
        /// Does the server leave the server signature out of the
        /// <c>&lt;success/&gt;</c> entirely?
        /// </summary>
        /// <remarks>
        /// The second way past the mutual authentication - and the more
        /// dangerous one, because a client easily tends not to check a missing
        /// signature at all.
        /// </remarks>
        public Boolean OmitScramSignature { get; set; } = false;

        /// <summary>
        /// The way to other servers, or null - then no foreign domain is
        /// reachable and every stanza there is answered with
        /// <c>&lt;remote-server-not-found/&gt;</c>.
        /// </summary>
        public IServerLinks? ServerLinks { get; set; }

        /// <summary>
        /// Are message/presence/iq delivered between sessions?
        /// </summary>
        public Boolean RouteStanzas { get; set; } = true;

        /// <summary>
        /// Is presence without a 'to' distributed at all? Who gets it is
        /// decided by the subscription state; this switch suspends the
        /// distribution entirely.
        /// </summary>
        public Boolean BroadcastPresence { get; set; } = true;

        /// <summary>
        /// Are XEP-0280 carbons distributed to further resources?
        /// </summary>
        public Boolean DeliverCarbons { get; set; } = true;

        /// <summary>
        /// Does the server answer XEP-0199 pings directed at it?
        /// </summary>
        public Boolean AnswerPings { get; set; } = true;

        /// <summary>
        /// Does the server answer PEP requests, or does it stay silent about
        /// them?
        /// </summary>
        /// <remarks>
        /// Like <see cref="AnswerPings"/> for XEP-0199, and for the same
        /// reason: <b>a client that waits for an answer can only be checked
        /// against a server that sometimes gives none.</b> A failure and
        /// silence are two different cases, and silence is the one most easily
        /// handled wrongly - it does not report itself.
        /// </remarks>
        public Boolean AnswerPepRequests { get; set; } = true;

        /// <summary>
        /// XEP-0198: Does the server negotiate stream management? On false it
        /// answers an <c>&lt;enable/&gt;</c> with <c>&lt;failed/&gt;</c>.
        /// </summary>
        public Boolean OfferStreamManagement { get; set; } = true;

        /// <summary>
        /// XEP-0198: Does the server answer an <c>&lt;r/&gt;</c> of the client?
        /// </summary>
        public Boolean AnswerAckRequests { get; set; } = true;

        /// <summary>
        /// XEP-0352: Does the server announce client state indication?
        /// </summary>
        /// <remarks>
        /// On false not only the announcement disappears but the handling too:
        /// an <c>&lt;inactive/&gt;</c> then counts like every other
        /// unannounced element. A server that keeps quiet about the extension
        /// and nevertheless acts on it would be the worse case - the client
        /// would take its contacts for silent while the server holds them back.
        /// </remarks>
        public Boolean OfferClientStateIndication { get; set; } = true;

        /// <summary>
        /// XEP-0352: How many stanzas a session holds back at most before the
        /// buffer goes out of its own accord.
        /// </summary>
        public Int32 MaxHeldWhileInactive { get; set; } = 100;

        /// <summary>
        /// XEP-0163: Does the server answer PEP requests for its accounts?
        /// </summary>
        /// <remarks>
        /// On false it behaves like a server without personal eventing: a
        /// request to a foreign bare JID then goes the ordinary way and lands
        /// at that person's client - which does not know it and answers with
        /// <c>&lt;service-unavailable/&gt;</c>. It is exactly by that that an
        /// OMEMO client has to recognise that there is nothing to fetch here.
        /// </remarks>
        public Boolean OfferPersonalEventing { get; set; } = true;

        /// <summary>
        /// Discards incoming stanzas of the client without counting them or
        /// passing them on.
        /// </summary>
        /// <remarks>
        /// Produces the one case the buffer of unacknowledged stanzas on the
        /// client side exists for in the first place: the stanza leaves the
        /// wire successfully and nevertheless does not arrive. In the same
        /// process it does not otherwise exist - a dropped socket makes the
        /// sending fail immediately, and a stanza not sent is not counted in
        /// the first place.
        ///
        /// Nonzas stay untouched: without them neither an <c>&lt;r/&gt;</c> nor
        /// a <c>&lt;resume/&gt;</c> would be possible in this state.
        /// </remarks>
        public Boolean SwallowClientStanzas { get; set; }

        /// <summary>
        /// A test switch: the server stays silent on the stream opening.
        /// </summary>
        /// <remarks>
        /// Produces the one case a failure does not produce: a peer that
        /// accepts the connection and then says <b>nothing</b>. An error
        /// arrives, a closed socket arrives — silence does not arrive, and that
        /// is exactly what the negotiation of the client waited for
        /// indefinitely.
        ///
        /// No made-up case: a server behind a state table that has forgotten
        /// the return path behaves exactly like this, and it is the most
        /// unpleasant outcome of all — the caller never learns that something
        /// is wrong.
        /// </remarks>
        public Boolean AnswerStreamOpen { get; set; } = true;

        /// <summary>
        /// XEP-0198, section 5: Does the server promise the resumption of a
        /// dropped stream?
        /// </summary>
        public Boolean OfferStreamResumption { get; set; } = true;

        /// <summary>
        /// How long a dropped stream waits for its returner.
        /// </summary>
        /// <remarks>
        /// After that the session counts as ended, and the sign-off the drop
        /// put off is made up. Without this deadline every dropped resource
        /// would stay online for its contacts forever.
        /// </remarks>
        public TimeSpan ResumptionTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// A test switch: does the sweep clear expired streams away?
        /// </summary>
        /// <remarks>
        /// Produces the one state that can otherwise only be hit in a race: a
        /// deadline that has expired while the stream is still lying there. It
        /// is exactly there - and only there - that the server knows the state
        /// of a stream it nevertheless no longer hands out, and can name it in
        /// its refusal (XEP-0198, section 5).
        ///
        /// In operation the window is at most a second wide; a test that wanted
        /// to hit it would in truth be checking the sweeper.
        /// </remarks>
        public Boolean SweepResumableStreams { get; set; } = true;

        /// <summary>
        /// How many dropped streams are waiting for their returner right now.
        /// </summary>
        public Int32 ResumableStreamCount
        {
            get { lock (_lock) return _resumable.Count; }
        }

        /// <summary>
        /// Makes the processing of a frame fail with an exception - the only
        /// way to reach the reporting route of
        /// <see cref="OnInternalError"/>.
        /// </summary>
        /// <remarks>
        /// A switch whose whole job is a failure looks strange and is necessary
        /// here: a guard that nothing triggers is itself unguarded. That was
        /// exactly the fault this step fixes - the old <c>catch</c> without a
        /// filter was reached by no test, and that is why what it swallowed did
        /// not show up for years.
        ///
        /// The same reasoning as with
        /// <see cref="SwallowClientStanzas"/>: a state that can occur in
        /// operation but cannot be produced from the outside is otherwise never
        /// checked.
        /// </remarks>
        public Boolean FailFrameHandling { get; set; } = false;

        /// <summary>
        /// Does the server answer XEP-0199 pings with a stanza error instead of
        /// with a result? For tests of the error handling.
        /// </summary>
        public Boolean FailPings { get; set; } = false;

        /// <summary>
        /// Does the server answer disco#info queries with a stanza error?
        /// </summary>
        public Boolean FailDiscoInfo { get; set; } = false;

        /// <summary>
        /// Does the server refuse the resource binding? A real server does that
        /// with <c>&lt;conflict/&gt;</c> or
        /// <c>&lt;resource-constraint/&gt;</c>, for instance.
        /// </summary>
        public Boolean FailBind { get; set; } = false;

        /// <summary>
        /// Does the server announce the legacy session (RFC 3921) as mandatory,
        /// that is, without <c>&lt;optional/&gt;</c>?
        /// </summary>
        public Boolean SessionRequired { get; set; } = false;

        /// <summary>
        /// Does the server answer an already occupied resource with
        /// <c>&lt;conflict/&gt;</c> instead of handing out a free one itself?
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 7.7.2.2 leaves the server both. The default stays
        /// the handing out of a differing resource - that is how the widespread
        /// servers behave, and the multi-client tests in the same process hang
        /// on it. For the counter-check there is this switch.
        /// </remarks>
        public Boolean ConflictOnUsedResource { get; set; } = false;

        /// <summary>
        /// Frames the server sends to the session immediately after the bind
        /// answer - before the client has enabled carbons and fetched the
        /// roster.
        ///
        /// That is how real servers behave: messages delivered late, roster
        /// pushes and presence arrive as soon as the resource is bound, and not
        /// only when the client is done with its setup phase.
        /// </summary>
        public List<String> DeliverAfterBind { get; } = [];

        #endregion

        #region Events

        /// <summary>
        /// Where a subscriber that threw gets reported. Null - the default -
        /// means nowhere.
        /// </summary>
        /// <remarks>
        /// The same reasoning as on <see cref="S2SStream.Logger"/>: this class
        /// does no logging of its own, and the one place that wants a logger is
        /// the report of a handler that failed. Whoever builds the server can
        /// set it; whoever does not gets what there was before, which is
        /// silence.
        /// </remarks>
        public ILogger? Logger { get; set; }

        /// <summary>
        /// Is raised for every stanza received from the client.
        /// </summary>
        public event OnXMPPServerStanzaReceivedDelegate? OnStanzaReceived;

        /// <summary>
        /// Is raised as soon as a session has been bound successfully.
        /// </summary>
        public event OnXMPPServerSessionBoundDelegate? OnSessionBound;

        /// <summary>
        /// Is raised when a stanza was refused by another server - with the
        /// domain of the peer and the reason.
        /// </summary>
        public event OnXMPPServerRemoteStanzaRejectedDelegate? OnRemoteStanzaRejected;

        /// <summary>
        /// Is raised when the processing of a frame ends with an exception -
        /// with the session, the frame and the exception.
        /// </summary>
        /// <remarks>
        /// The only purpose is visibility. Previously a <c>catch</c> without a
        /// filter stood at this place, with the note "the connection dropped -
        /// in the test the normal case". The note was no longer true: a
        /// measurement over the entire collection caught <b>not a single</b>
        /// exception. What the catch actually still achieved was the silent
        /// swallowing of programming errors - in D15 a mutation survived only
        /// because its <c>NullReferenceException</c> disappeared here.
        ///
        /// That is why nothing is filtered. A list of exceptions a drop
        /// "really" produces would be guessed - the measurement says that none
        /// of them occurs - and a branch no test reaches is exactly the sort of
        /// precaution that covered up the fault back then. Everything is
        /// reported; the test collection treats every report as a defect until
        /// the opposite is shown.
        ///
        /// The exception is not rethrown after the report: Hermod catches every
        /// one above anyway and writes it into a log no test looks at. Nothing
        /// about the behaviour of the server changes with that - only whether
        /// anybody learns of it.
        /// </remarks>
        public event OnXMPPServerInternalErrorDelegate? OnInternalError;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Creates a test server on a free port.
        /// </summary>
        /// <param name="domain">The domain served; has to match the JID of the clients.</param>
        /// <param name="port">A fixed port or 0 for a free one.</param>
        /// <param name="useTLS">
        /// TLS with a self-produced certificate, as RFC 6120, section 5 demands
        /// it. On false the server speaks <c>ws://</c> - usable for
        /// fault-finding with a recording, otherwise nothing.
        /// </param>
        /// <param name="accountStore">
        /// Where the accounts lie; null takes an
        /// <see cref="InMemoryAccountStore"/> that disappears when the process
        /// ends. Existing accounts are read in right away.
        /// </param>
        /// <param name="certificate">
        /// A server certificate set from the outside; null produces a
        /// self-signed one for <paramref name="domain"/>.
        /// </param>
        public XMPPServer(String              domain         = "localhost",
                          Int32               port           = 0,
                          Boolean             useTLS         = true,
                          IXMPPAccountStore?  accountStore   = null,
                          X509Certificate2?   certificate    = null)
        {

            Domain       = domain;
            // 0 is passed straight through to the transport, which binds it and
            // reports back what the operating system gave it - see Start().
            //
            // It used to be resolved here, by binding a throwaway listener on
            // 0, reading the number and closing it again. That leaves the port
            // free for anybody between the closing and the real bind, and
            // whoever loses that race fails to start with AddressAlreadyInUse.
            // It happened rarely and on Linux, where the ephemeral range cycles
            // faster - which made it a test that went red for reasons that had
            // nothing to do with the change under test.
            Port         = port;

            // A self-signed certificate cannot be checked by a foreign peer -
            // it would have to know this one certificate, and it comes into
            // being anew at every start. For a run against ejabberd or Prosody
            // the certificate has to come from the outside, from a chain both
            // sides trust. That holds just as much for any operation that is
            // not a test.
            Certificate  = useTLS
                               ? certificate ?? CreateSelfSignedCertificate(domain)
                               : null;

            // Over plaintext this stays null and no -PLUS is announced, which
            // is not a limitation but the definition: there is no channel to
            // bind to.
            _channelBindingData = TlsServerEndPoint.For(Certificate);

            _accountStore = accountStore ?? new InMemoryAccountStore();

            // Read before anything else touches the store, and written back
            // only when there was none: the first start of a server settles
            // this key, every later one inherits it.
            _decoySecret  = _accountStore.LoadDecoySecret() ?? NewDecoySecret();

            foreach (var account in _accountStore.Load())
            {
                account.OnChanged        = _accountStore.Save;
                _accounts[account.BareJid] = account;
            }

            _webSocketServer = new XMPPWebSocketServer(this, IPPort.Parse(Port), Certificate);

            _webSocketServer.OnNewWebSocketConnection  += OnConnectionOpenedAsync;
            _webSocketServer.OnCloseMessageReceived    += OnCloseFrameReceivedAsync;
            _webSocketServer.OnTCPConnectionClosed     += OnConnectionClosedAsync;

            OnInstanceCreated?.Invoke(this);

        }

        #endregion

        #region (internal, static) OnInstanceCreated

        /// <summary>
        /// Reports every instance created - only for the test collection.
        /// </summary>
        /// <remarks>
        /// The guard against swallowed programming errors
        /// (<c>OnInternalError</c>) hung until now on every fixture attaching
        /// it by hand. That is a mechanical property no test holds: whoever
        /// creates a server without the guard gets no failure but
        /// <b>silence</b> - and that was exactly the state the guard was
        /// supposed to abolish.
        ///
        /// Through this event the collection finds every server without anybody
        /// having to think of it. It is <c>internal</c> and thereby no promise
        /// to the outside; it becomes visible solely through
        /// <c>InternalsVisibleTo</c>.
        ///
        /// Raised at the end of the constructor, not at the beginning: a
        /// subscriber gets a fully built instance and not half of one.
        /// </remarks>
        /// <remarks>
        /// <b>The one event here that stayed an <c>Action</c></b>, while every
        /// other one in this library became a Task-returning delegate. Not an
        /// oversight, and not laziness: a constructor cannot await.
        ///
        /// The two ways round that are both worse than leaving it. Awaiting is
        /// impossible; firing and forgetting would let the constructor return
        /// before the subscriber had run - and the subscriber is the guard that
        /// is supposed to be watching from the first frame onwards, so a server
        /// could produce an internal error while nobody was yet listening for
        /// it. Blocking on <c>GetAwaiter().GetResult()</c> in a constructor
        /// buys the ordering back at the price of a deadlock hazard.
        ///
        /// So this one is what it always was: a synchronous construction hook,
        /// which is what its purpose actually asks for. It is <c>internal</c>,
        /// visible only through <c>InternalsVisibleTo</c>, and its handlers do
        /// nothing but attach further handlers.
        /// </remarks>
        internal static event Action<XMPPServer>? OnInstanceCreated;

        #endregion


        #region Accounts

        /// <summary>
        /// Creates an account a client may log in at.
        /// </summary>
        public XMPPAccount AddAccount(String localPart, String password = "pw")

            => AddAccount(localPart, XMPPCredentials.FromPassword(password));

        /// <summary>
        /// Adds an account from credentials that already exist.
        /// </summary>
        /// <remarks>
        /// The way an account arrives from somewhere else, and the only way to
        /// produce one that holds key material for some mechanisms and not
        /// others: <see cref="XMPPCredentials.FromPassword"/> derives every
        /// mechanism at once, so an account made from a password never needs an
        /// upgrade. A server that stored only SHA-1 material - which is what
        /// ejabberd and Prosody do by default - looks like this instead, and it
        /// is the situation XEP-0480 exists for.
        /// </remarks>
        public XMPPAccount AddAccount(String localPart, XMPPCredentials credentials)
        {

            var account = new XMPPAccount($"{localPart}@{Domain}", credentials) {
                              OnChanged = _accountStore.Save
                          };

            lock (_lock)
                _accounts[account.BareJid] = account;

            _accountStore.Save(account);

            return account;

        }

        /// <summary>
        /// Delivers an account or null.
        /// </summary>
        public XMPPAccount? GetAccount(String bareJid)
        {
            lock (_lock)
                return _accounts.TryGetValue(bareJid, out var a) ? a : null;
        }

        /// <summary>
        /// All accounts of this server.
        /// </summary>
        public IReadOnlyList<XMPPAccount> Accounts
        {
            get { lock (_lock) return _accounts.Values.ToList(); }
        }

        /// <summary>
        /// Removes an account, from the account store too. Existing sessions
        /// stay untouched by it.
        /// </summary>
        public void RemoveAccount(String bareJid)
        {

            lock (_lock)
            {
                if (_accounts.Remove(bareJid, out var account))
                    account.OnChanged = null;
            }

            _accountStore.Delete(bareJid);

        }

        #endregion

        #region Sessions

        /// <summary>
        /// All deliverable sessions of an account, oldest first.
        /// </summary>
        /// <remarks>
        /// Deliverable does not mean open: a preserved stream (XEP-0198,
        /// section 5) has no connection any more but waits for its returner and
        /// takes in what arrives for it in the meantime. If it stayed out here,
        /// nothing would arrive any more during a disturbance, and the
        /// resumption would save only the last stanzas before the drop.
        /// </remarks>
        public IReadOnlyList<XMPPSession> SessionsOf(String bareJid)
        {
            lock (_lock)
                return _sessions
                       .Where(s => (s.IsOpen || s.ResumptionId is not null) &&
                                   String.Equals(s.BareJid, BareOf(bareJid), StringComparison.OrdinalIgnoreCase))
                       .ToList();
        }

        /// <summary>
        /// The deliverable session for a full JID or null - open or preserved,
        /// as with <see cref="SessionsOf"/>.
        /// </summary>
        /// <remarks>
        /// The open one first: after a resumption the old and the new session
        /// carry the same full JID, and the old one stays standing in the list
        /// as a dead object.
        /// </remarks>
        public XMPPSession? SessionOf(String fullJid)
        {
            // RFC 7622, section 3.4: the resourcepart depends on the spelling,
            // the localpart and the domainpart do not. An OrdinalIgnoreCase
            // over the whole full JID threw both into one pot - and thereby
            // delivered, for 'alice@example.com/handy', the session of
            // 'alice@example.com/Handy' too. The resource assignment
            // distinguished the two from the start (see Occupied); only the
            // lookup did not.
            lock (_lock)
                return _sessions.Where(s => JID.AreEqual(s.FullJid, fullJid))
                                .OrderByDescending(s => s.IsOpen)
                                .FirstOrDefault(s => s.IsOpen || s.ResumptionId is not null);
        }

        /// <summary>
        /// Tears all open sessions down.
        /// </summary>
        public void KillAllSessions()
        {
            foreach (var s in Sessions)
                s.Kill();
        }

        /// <summary>
        /// Tears all sessions of an account down.
        /// </summary>
        public void KillSessionsOf(String bareJid)
        {
            foreach (var s in SessionsOf(bareJid))
                s.Kill();
        }

        #endregion

        #region Sending and waiting

        /// <summary>
        /// Sends a stanza to all sessions of the given JID; with a full JID
        /// only to the resource in question.
        /// </summary>
        public async Task PushAsync(String jid, String xml)
        {

            var targets = jid.Contains('/')
                              ? [SessionOf(jid)]
                              : SessionsOf(jid).Cast<XMPPSession?>().ToArray();

            foreach (var t in targets)
                if (t is not null)
                    await t.SendAsync(xml);

        }

        /// <summary>
        /// Sends a stanza to all open sessions.
        /// </summary>
        public async Task BroadcastAsync(String xml)
        {
            foreach (var s in Sessions)
                await s.SendAsync(xml);
        }

        /// <summary>
        /// Waits until the condition holds, or until the timeout expires.
        /// </summary>
        public static async Task<Boolean> WaitUntilAsync(Func<Boolean> condition,
                                                         TimeSpan?     timeout = null,
                                                         TimeSpan?     poll    = null)
        {

            var deadline  = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            var interval  = poll ?? TimeSpan.FromMilliseconds(25);

            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                    return true;

                await Task.Delay(interval);
            }

            return condition();

        }

        /// <summary>
        /// Waits until at least this many sessions are bound.
        /// </summary>
        public Task<Boolean> WaitForBoundSessionsAsync(Int32 count, TimeSpan? timeout = null)
            => WaitUntilAsync(() => Sessions.Count(s => s.FullJid is not null) >= count, timeout);

        #endregion

        #region Start and accepting connections

        public void Start()
        {

            _webSocketServer.Start().GetAwaiter().GetResult();

            // The port is only settled by the bind, and only now: constructed
            // with 0, this is where the operating system's choice becomes
            // knowable. Read back rather than guessed at, so that Uri names the
            // socket that is actually listening.
            Port = _webSocketServer.TCPPort.ToUInt16();

            // XEP-0198, section 5: the deadline of the preserved streams
            // expires in real time, not at the next access - otherwise a
            // postponed sign-off would hang on somebody else happening to do
            // something. A second suffices: the deadline is in the order of
            // minutes.
            _resumptionSweeper = new Timer(
                                     _ => SweepResumableStreamsAsync().GetAwaiter().GetResult(),
                                     null,
                                     TimeSpan.FromSeconds(1),
                                     TimeSpan.FromSeconds(1));

        }

        /// <summary>
        /// The WebSocket transport. The protocol sits entirely in
        /// <see cref="XMPPServer"/>; Hermod delivers frames, TLS and the
        /// connection management.
        /// </summary>
        /// <remarks>
        /// Composition instead of inheritance: <see cref="XMPPServer"/> shall
        /// keep its own small surface towards the outside and not inherit the
        /// entire one of <c>AWebSocketServer</c>.
        /// </remarks>
        private sealed class XMPPWebSocketServer : AWebSocketServer
        {

            private readonly XMPPServer _xmpp;

            public XMPPWebSocketServer(XMPPServer         xmpp,
                                       IPPort             port,
                                       X509Certificate2?  certificate)

                : base(TCPPort:                port,

                       // RFC 6120, section 5: XMPP belongs over TLS. Without a
                       // selector the listener stays in the clear.
                       ServerCertificateSelector:  certificate is not null
                                                       ? (_, _) => certificate
                                                       : null,

                       // Otherwise Hermod would demand an HTTP basic
                       // authentication at the handshake. Who may log in is
                       // decided in XMPP by the SASL afterwards.
                       RequireAuthentication:  false,

                       // RFC 7395, section 3.3: the subprotocol is called "xmpp".
                       SecWebSocketProtocols:  ["xmpp"],

                       AutoStart:              false)

            {
                _xmpp = xmpp;
            }

            public override Task ProcessTextMessage(DateTimeOffset             Timestamp,
                                                    AWebSocketServer           Server,
                                                    WebSocketServerConnection  Connection,
                                                    EventTracking_Id           EventTrackingId,
                                                    WebSocketFrame             TextFrame,
                                                    String                     TextMessage,
                                                    CancellationToken          CancellationToken)

                => _xmpp.HandleTextMessageAsync(Connection, TextMessage);

        }

        /// <summary>
        /// A new connection stands - from here on there is a session for it.
        /// </summary>
        private Task OnConnectionOpenedAsync(DateTimeOffset             timestamp,
                                             AWebSocketServer           server,
                                             WebSocketServerConnection  connection,
                                             IEnumerable<String>        sharedSubprotocols,
                                             String?                    selectedSubprotocol,
                                             EventTracking_Id           eventTrackingId,
                                             CancellationToken          ct)
        {

            SessionOf(connection);

            return Task.CompletedTask;

        }

        /// <summary>
        /// Delivers the session for a connection and creates it if there is
        /// none yet.
        /// </summary>
        /// <remarks>
        /// The creating stands here and not only in the connection event,
        /// because the order between that event and the first text frame is
        /// nothing the protocol should rely on.
        /// </remarks>
        private XMPPSession SessionOf(WebSocketServerConnection connection)
        {

            lock (_lock)
            {

                var existing = _sessions.FirstOrDefault(s => ReferenceEquals(s.Connection, connection));

                if (existing is not null)
                    return existing;

                var session = new XMPPSession(_webSocketServer,
                                              connection,
                                              Interlocked.Increment(ref _connectionCounter))
                {
                    MaxHeldWhileInactive = MaxHeldWhileInactive
                };

                _sessions.Add(session);

                return session;

            }

        }

        /// <summary>
        /// A text frame of the client - the entry into the protocol.
        /// </summary>
        private async Task HandleTextMessageAsync(WebSocketServerConnection  connection,
                                                  String                     frame)
        {

            var session = SessionOf(connection);

            // A switch for the error case: the stanza has left the wire and
            // nevertheless does not arrive. Before the recording and before the
            // counting, so that for the server it looks as though nothing had
            // ever come - exactly the picture a connection leaves behind that
            // falls apart between sending off and processing.
            //
            // Only stanzas: nonzas have to keep getting through, otherwise
            // neither an <r/> nor a <resume/> could be sent in this state, and
            // the case would again be unreachable.
            if (SwallowClientStanzas && XMPPSession.IsStanza(frame))
                return;

            session.RecordReceived(frame);
            await OnStanzaReceived.InvokeAllAsync(handler => handler(Timestamp.Now, this, session, frame, CancellationToken.None), Logger);

            if (StanzaElement.Is(frame, "open"))
                session.OpenCount++;

            try
            {
                await HandleFrameAsync(session, frame, session.OpenCount);
            }
            catch (Exception e)
            {

                // Reported instead of swallowed - see OnInternalError. Before
                // the closing, so that a subscriber sees the exception even when
                // the closing itself goes wrong.
                await OnInternalError.InvokeAllAsync(handler => handler(Timestamp.Now, this, session, frame, e, CancellationToken.None), Logger);

                // RFC 6120, section 4.9.3.8: "The server has experienced a
                // misconfiguration or other internal error that prevents it from
                // servicing the stream." That is exactly what has happened here
                // - and section 4.9.1.1 leaves no choice afterwards: stream
                // errors are unrecoverable, the stream is closed.
                //
                // Until D21 the stream carried on. That was convenient and
                // wrong: what the frame was supposed to change is half changed,
                // and nobody knows how far. The client reckons with a state the
                // server no longer has - and of all things the error most likely
                // to leave state behind remained the only one without
                // consequences.
                //
                // The client comes back: <internal-server-error/> counts as
                // recoverable (RFC 6120, section 4.9.3.8 names no reason to take
                // it for final), and a new stream begins with a state both sides
                // agree on. That is precisely the point of an unrecoverable
                // error.
                try
                {
                    await session.SendStreamErrorAsync("internal-server-error");
                }
                catch (Exception whileClosing)
                {
                    await OnInternalError.InvokeAllAsync(handler => handler(Timestamp.Now, this, session, frame, whileClosing, CancellationToken.None), Logger);
                }

            }

        }

        /// <summary>
        /// The client has closed the stream.
        /// </summary>
        /// <remarks>
        /// Hermod answers a close frame of its own accord with one of its own,
        /// as RFC 6455, section 5.5.1 demands it, and lays the TCP connection
        /// down afterwards. If <see cref="CompleteCloseHandshake"/> is switched
        /// off, this event handler holds the answer up - Hermod waits for it
        /// before it closes.
        ///
        /// Postponing and not suppressing: the client shall see silence, and
        /// that on an open connection. A dropped socket ends its waiting right
        /// away and would let the test pass without the time limit ever having
        /// taken hold - the first version here almost ran past exactly that.
        /// </remarks>
        private async Task OnCloseFrameReceivedAsync(DateTimeOffset                    timestamp,
                                                     AWebSocketServer                  server,
                                                     WebSocketServerConnection         connection,
                                                     WebSocketFrame                    frame,
                                                     EventTracking_Id                  eventTrackingId,
                                                     WebSocketFrame.ClosingStatusCode  statusCode,
                                                     String?                           reason,
                                                     CancellationToken                 ct)
        {

            if (CompleteCloseHandshake)
                return;

            try
            {
                await Task.Delay(SilentCloseDelay, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                // The server is shutting down - then the delay is done with.
            }

        }

        /// <summary>
        /// How long a server with <see cref="CompleteCloseHandshake"/> switched
        /// off stays silent. Has to lie above the time limit the client gives
        /// its close handshake (three seconds), otherwise the test checks not
        /// the time limit but only a slow answer.
        /// </summary>
        private static readonly TimeSpan SilentCloseDelay = TimeSpan.FromSeconds(6);

        /// <summary>
        /// The connection is gone - whether properly, dropped or at an
        /// exception: the contacts have to learn of it.
        /// </summary>
        private async Task OnConnectionClosedAsync(DateTimeOffset             timestamp,
                                                   AWebSocketServer           server,
                                                   WebSocketServerConnection  connection,
                                                   EventTracking_Id           eventTrackingId,
                                                   String?                    reason,
                                                   CancellationToken          ct)
        {

            XMPPSession? session;

            lock (_lock)
                session = _sessions.FirstOrDefault(s => ReferenceEquals(s.Connection, connection));

            if (session is not null)
                await AnnounceUnavailableAsync(session);

        }

        /// <summary>
        /// Signs an ended session off at its contacts.
        /// </summary>
        /// <remarks>
        /// RFC 6121, section 4.5.2 (Server Processing of Outbound Unavailable
        /// Presence): a client can no longer send its sign-off when the
        /// connection breaks away under its feet - so the server produces it in
        /// its name. Without that the contacts carry the resource as online
        /// forever.
        ///
        /// The recipients are the same as with every other presence: the
        /// sign-off is a piece of information about one's own state and must
        /// reach strangers just as little as the sign-on.
        /// </remarks>
        private async Task AnnounceUnavailableAsync(XMPPSession session)
        {

            // XEP-0352: what was held back now goes its accustomed way - with a
            // preserved stream into the buffer of unacknowledged stanzas,
            // otherwise into the void.
            //
            // Before everything else, and that is the reason: from here on
            // nothing gets past this session any more. If the buffer stayed
            // standing, the saving measure would have made a loss out of every
            // drop - the returner would get everything delivered afterwards
            // except what the server had set aside for them.
            await session.FlushHeldAsync();

            // XEP-0198, section 5: a stream that has been promised the
            // resumption is spared the sign-off for now. Otherwise the server
            // would perform a disappearance to its contacts that would have to
            // be taken back right afterwards - and between the two presences
            // would lie everything that was directed in the meantime at a
            // supposedly signed-off resource.
            //
            // Before the guard below, not behind it: TryMarkUnavailable
            // switches the state over, and afterwards the session would already
            // be used up for the sign-off made up after the deadline.
            if (session.ResumptionId is not null && Park(session))
                return;

            // If the client signed off itself, the matter is settled. The
            // switching over has to be atomic: otherwise an aborting socket and
            // the client's own sign-off both get past the guard, and the
            // contacts get it twice.
            if (session.FullJid is null || !session.TryMarkUnavailable())
                return;

            // Fetched before the guard below, not behind it: the presence of
            // this resource is over, and with it every promise it gave through
            // directed presence (section 4.6.1). If that stood only after the
            // guard, the list would stay standing as soon as there is once no
            // distribution - and a stranger would be allowed to keep querying a
            // signed-off resource (section 8.5.3.1).
            var directed = session.TakeDirectedPresenceTargets();

            // While the server is shutting down it goes to nobody any more.
            if (!RouteStanzas || !BroadcastPresence || _cts.IsCancellationRequested)
                return;

            var stanza = $"<presence type='unavailable' from='{session.FullJid}'/>";

            // Here too, not only in RouteToAsync: the distribution to local
            // contacts goes directly to the session, without taking the switch.
            //
            // That is not a filling-in for completeness' sake but necessary -
            // the two roster halves are easy to mix up here. Whoever gets the
            // sign-off over this route stands in *Alice's* roster with 'from'
            // (Bob may see Alice); about their right to ask, however, *Bob's*
            // roster decides. If that one is empty, it hangs solely on the list
            // of directed presence - and without this line it would outlive
            // Alice's sign-off.
            foreach (var target in PresenceTargetsOf(session))
            {
                ForgetDirectedPresenceFrom(target, stanza);
                await target.SendAsync(stanza);
            }

            foreach (var remote in RemotePresenceTargetsOf(session))
                await RouteToAsync(remote, StampTo(stanza, remote));

            await SendUnavailableToDirectedTargetsAsync(session, directed, stanza);

        }

        /// <summary>
        /// Delivers the sign-off afterwards to the recipients of directed
        /// presence (RFC 6121, section 4.6.3, rule 2).
        /// </summary>
        /// <param name="targets">
        /// The recipients as
        /// <see cref="XMPPSession.TakeDirectedPresenceTargets"/> handed them
        /// out.
        /// </param>
        /// <param name="unavailable">
        /// The sign-off - either the client's own or the one the server
        /// produced in its name.
        /// </param>
        /// <remarks>
        /// The rule closes a gap nobody else notices: whoever has shown a
        /// stranger their presence does not thereby stand in that stranger's
        /// roster - and without this route would never get an end. The stranger
        /// would carry the resource as present forever, and because a
        /// conversation with a non-contact begins exactly like this
        /// (section 5.1), that is the rule and not the exception.
        ///
        /// Skipped is whoever stands in the roster with <c>from</c> or
        /// <c>both</c>: they have got the sign-off through the ordinary
        /// distribution already. Without this restriction it would come twice -
        /// and a client that counts presence instead of replacing it would get
        /// confused. The RFC narrows rule 2 for the same reason to entities
        /// that do <b>not</b> stand in the roster with <c>from</c> or
        /// <c>both</c>.
        ///
        /// Whoever has already got a directed sign-off does not stand in the
        /// list any more - that is done by
        /// <see cref="XMPPSession.RecordDirectedPresence"/>, and that is
        /// exactly what the parenthesis of the rule aims at ("if the user has
        /// not yet sent directed unavailable presence to that entity").
        /// </remarks>
        private async Task SendUnavailableToDirectedTargetsAsync(XMPPSession                 session,
                                                                IReadOnlyCollection<String>  targets,
                                                                String                       unavailable)
        {

            foreach (var target in targets)
            {

                if (session.Account?.IsPresenceSubscriber(target) == true)
                    continue;

                await RouteToAsync(target, StampTo(unavailable, target));

            }

        }

        /// <summary>
        /// Preserves a dropped stream for its returner.
        /// </summary>
        /// <returns>
        /// false when there was nothing to preserve - then the caller takes the
        /// accustomed way and signs off.
        /// </returns>
        private Boolean Park(XMPPSession session)
        {

            // Bound the session has to be - without a resource there is nothing
            // a returner could return to.
            //
            // Available it does *not* have to be. Here an additional
            // !session.IsAvailable once stood, and that confused two things: the
            // resumption is a property of the stream and was promised with
            // <enabled resume='true'/>; the presence tells the contacts
            // something about the person in front of it. A client that made
            // itself invisible or has not sent its first presence yet thereby
            // lost the promise silently: its <resume/> got a <failed/>, and
            // everything unacknowledged was gone.
            //
            // For the sign-off, in whose sequence this function sits, the
            // distinction is already made anyway - TryMarkUnavailable further
            // below refuses a never-available session of its own accord.
            if (session.FullJid is null)
                return false;

            lock (_lock)
            {

                // Two drops of the same session must not yield two entries: the
                // second would get a new deadline and would hold the sign-off up
                // arbitrarily long.
                if (_resumable.ContainsKey(session.ResumptionId!))
                    return true;

                _resumable[session.ResumptionId!] = new ParkedStream(
                                                        session,
                                                        DateTimeOffset.UtcNow + ResumptionTimeout);

            }

            return true;

        }

        /// <summary>
        /// Clears expired streams away and makes up their sign-off.
        /// </summary>
        /// <remarks>
        /// Without this sweep the postponement from
        /// <see cref="AnnounceUnavailableAsync"/> would be no postponement but
        /// a swallowing: the contacts would carry every dropped resource as
        /// online forever, and nobody would notice a thing.
        /// </remarks>
        internal async Task SweepResumableStreamsAsync()
        {

            if (!SweepResumableStreams)
                return;

            List<ParkedStream> expired;

            lock (_lock)
            {

                expired = [.. _resumable.Values.Where(p => p.Deadline <= DateTimeOffset.UtcNow)];

                foreach (var p in expired)
                    _resumable.Remove(p.Session.ResumptionId!);

            }

            foreach (var p in expired)
            {

                // First take the promise back, then sign off: otherwise
                // AnnounceUnavailableAsync would again see a resumable stream in
                // front of it and would park it anew. The sign-off would then
                // never come.
                p.Session.EndResumption();

                await AnnounceUnavailableAsync(p.Session);

            }

        }

        #endregion

        #region Protocol handling

        private async Task HandleFrameAsync(XMPPSession session, String frame, Int32 openCount)
        {

            if (FailFrameHandling)
                throw new InvalidOperationException(
                          "FailFrameHandling: a deliberate failure while processing a frame.");

            // Decided by the element name and not by a prefix. A
            // StartsWith("<iq") also hits <iqbogus/>, StartsWith("<presence")
            // also <presence-probe/> - and that was no imagined case: a
            // <presence-probe/> ran into the presence handling and counted
            // there as presence. A human being was reported to their contacts
            // as online because their element begins with the same eight
            // characters.
            var elementName = StanzaElement.NameOf(frame);

            // RFC 6120, section 8.3.3.8: if there is no JID in the 'to', the
            // stanza is not deliverable - and that regardless of what else it
            // is. Hence before the switch and for all three kinds in one place:
            // every branch behind it asks its own questions, and this question
            // belongs to none of them.
            if (elementName is "iq" or "message" or "presence" &&
                await RefuseMalformedToAsync(session, frame, elementName))
                return;

            switch (elementName)
            {

                case "open":
                    await HandleStreamOpenAsync(session, openCount);
                    return;

                case "auth":
                    await HandleAuthAsync(session, frame);
                    return;

                // XEP-0388's opening element. A different name from <auth/>,
                // which is what lets the two profiles share one stream without
                // anything having to guess which is meant.
                case "authenticate":
                    await HandleSasl2AuthenticateAsync(session, frame);
                    return;

                // <response/> and <abort/> carry the same names in both
                // profiles and are told apart by their namespace. The session
                // remembers which profile the exchange began in, so the answer
                // goes back in the namespace it was asked in - a <success/> in
                // the wrong one is a frame the client will not recognise.
                case "response":
                    await HandleSaslResponseAsync(session, frame);
                    return;

                // XEP-0388's task flow, which XEP-0480 rides on.
                case "next":
                    await HandleSasl2NextAsync(session, frame);
                    return;

                case "task-data":
                    await HandleSasl2TaskDataAsync(session, frame);
                    return;

                case "abort":
                    await HandleSaslAbortAsync(session);
                    return;

                case "iq":
                    await HandleIqAsync(session, frame);
                    return;

                case "message":
                    await HandleMessageAsync(session, frame);
                    return;

                case "presence":
                    await HandlePresenceAsync(session, frame);
                    return;

            }

            // The namespace alone does not decide: what it does not know falls
            // further down and gets the same answer as every other unknown
            // element. Until D29 the branch ended here - it was the last place
            // where a frame fell out silently at the back.
            if (frame.Contains("urn:xmpp:sm:3", StringComparison.Ordinal) &&
                await HandleStreamManagementAsync(session, frame))
            {
                return;
            }

            // XEP-0352: <active/> and <inactive/>.
            if (frame.Contains(ClientStateIndication.Namespace, StringComparison.Ordinal) &&
                await HandleClientStateAsync(session, frame))
            {
                return;
            }

            // RFC 7395, section 3.6: the client says goodbye.
            //
            // With that the stream is over, and not dropped - a resumption is
            // out of the question any more (XEP-0198, section 5.3). Without
            // this distinction the server would take every proper sign-off for
            // a disturbance for a minute: the contacts would see the signed-off
            // party as present for that long, and a renewed login would tie in
            // with a stream the user themselves ended.
            if (StanzaElement.Is(frame, "close"))
            {
                session.EndResumption();
                return;
            }

            // A frame without an element is not an unknown element but none at
            // all. The section below speaks of a "first-level child"; an empty
            // frame is not a child that is unsupported but no child. In D26 it
            // still fell under the error - one line too far.
            if (StanzaElement.NameOf(frame) is null)
                return;

            // RFC 6120, section 4.9.3.24: "The initiating entity has sent a
            // first-level child of the stream that is not supported by the
            // server, either because the receiving entity does not understand
            // the namespace or because the receiving entity does not understand
            // the element name."
            //
            // Up to here such a frame fell out silently at the back. That was
            // the convenient answer and the worse one: whoever sends something
            // this server does not know otherwise waits for an answer that
            // never comes, and never learns why. A stream error ends the stream
            // (section 4.9.1.1) - and that is the statement here: about this
            // stream we no longer agree.
            //
            // It also hits what another server would answer and this one does
            // not - an <abort/> from the SASL negotiation (section 6.4.4), for
            // instance. There too the condition is met literally: "not
            // supported by the server". It stands under "Later".
            await session.SendStreamErrorAsync("unsupported-stanza-type");

        }

        /// <summary>
        /// XEP-0198: <c>&lt;enable/&gt;</c>, <c>&lt;r/&gt;</c> and <c>&lt;a/&gt;</c>.
        /// </summary>
        /// <returns>
        /// false when the element is not provided for in this namespace - then
        /// the caller handles it like every other unknown one.
        /// </returns>
        private async Task<Boolean> HandleStreamManagementAsync(XMPPSession session, String frame)
        {

            if (StanzaElement.Is(frame, "enable"))
            {

                if (!OfferStreamManagement)
                {
                    await session.SendAsync(
                        "<failed xmlns='urn:xmpp:sm:3'>" +
                        "<feature-not-implemented xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/></failed>");
                    return true;
                }

                // XEP-0198, section 5: only promise what was asked for. An
                // unasked-for resume='true' would oblige the server to preserve
                // every dropped session, and no client would ever come back to
                // fetch it.
                var resume = OfferStreamResumption &&
                             Regex.IsMatch(frame, @"resume=['""](true|1)['""]");

                // Reset the counters and confirm in one go - the <enabled/>
                // itself is a nonza and does not count, but a stanza in between
                // would count at only one of the two sides. See
                // EnableStreamManagementAsync.
                await session.EnableStreamManagementAsync(
                          resume,
                          s => resume
                                   ? $"<enabled xmlns='urn:xmpp:sm:3' id='{s.ResumptionId}' " +
                                     $"resume='true' max='{(Int32) ResumptionTimeout.TotalSeconds}'/>"
                                   : $"<enabled xmlns='urn:xmpp:sm:3' id='sm-{s.ConnectionNumber}'/>");

                return true;

            }

            // XEP-0198, section 5: the client wants to tie in with an earlier
            // stream. That comes before the resource binding - a bound resource
            // does not exist here yet, it is being taken over just now.
            if (StanzaElement.Is(frame, "resume"))
            {
                await HandleResumeAsync(session, frame);
                return true;
            }

            // The client queries our receive counter.
            //
            // By the complete name and not by the initial letter: a
            // StartsWith("<r") hit every element beginning with r, and a
            // StartsWith("<a") every one with a. The order of the branches held
            // that together until now - <resume/> before <r/>, <auth/> far up
            // in the switch before it. An order that carries as long as nobody
            // rearranges it is not a check but an agreement.
            if (StanzaElement.Is(frame, "r"))
            {

                if (AnswerAckRequests)
                    await session.SendAsync(
                        $"<a xmlns='urn:xmpp:sm:3' h='{session.StanzasReceivedFromClient}'/>");

                return true;

            }

            // The client reports its receive counter.
            if (StanzaElement.Is(frame, "a"))
            {

                var h = Regex.Match(frame, @"h=['""](\d+)['""]");

                if (h.Success && UInt32.TryParse(h.Groups[1].Value, out var value))
                    session.AcknowledgeToClient(value);

                return true;

            }

            // Everything else in this namespace this server does not know -
            // <enabled/>, <resumed/> and <failed/> included, which do exist but
            // which the server sends to the client and not the other way round.
            // Known does not mean "known in this direction".
            return false;

        }

        /// <summary>
        /// XEP-0352: <c>&lt;active/&gt;</c> and <c>&lt;inactive/&gt;</c> - the
        /// client says whether a human being is looking.
        /// </summary>
        /// <returns>
        /// false when the element is not provided for in this namespace or the
        /// server has not offered the extension at all - then the caller
        /// handles it like every other unknown one.
        /// </returns>
        /// <remarks>
        /// Not without a login: the announcement stands in the features after
        /// the SASL exchange (section 4.1), and what was not announced yet does
        /// not hold yet either. Otherwise someone not logged in would have a
        /// state on a session that belongs to nobody yet.
        ///
        /// There is no answer - section 4.2: "There is no reply from the server
        /// to either of these elements." An <c>&lt;active/&gt;</c> that drew a
        /// confirmation after it would wake the device at exactly the moment it
        /// is going to sleep.
        /// </remarks>
        private async Task<Boolean> HandleClientStateAsync(XMPPSession session, String frame)
        {

            if (!OfferClientStateIndication || session.Account is null)
                return false;

            if (StanzaElement.Is(frame, "active"))
            {
                await session.SetClientStateAsync(true);
                return true;
            }

            if (StanzaElement.Is(frame, "inactive"))
            {
                await session.SetClientStateAsync(false);
                return true;
            }

            return false;

        }

        /// <summary>
        /// XEP-0198, section 5: <c>&lt;resume/&gt;</c> - somebody ties in with
        /// a preserved stream.
        /// </summary>
        /// <remarks>
        /// The identifier alone does not suffice. It travels over the wire, and
        /// whoever gets hold of it would otherwise have somebody else's session
        /// together with its full JID, roster and conversations in progress -
        /// without ever having seen the password. That is why the stream the
        /// <c>&lt;resume/&gt;</c> arrives on has to be logged in to the
        /// <b>same account</b> already; the identifier then only selects which
        /// of the streams of this account is meant.
        ///
        /// If it fails, that is no error case but the normal case after a
        /// longer disturbance: the client gets <c>&lt;failed/&gt;</c> and binds
        /// a new resource.
        /// </remarks>
        private async Task HandleResumeAsync(XMPPSession session, String frame)
        {

            var previd = Regex.Match(frame, @"previd=['""]([^'""]+)['""]");

            ParkedStream? parked = null;

            // How far the old stream had got - known only as long as it is
            // still lying there, and nameable only to its own account.
            UInt32? processed = null;

            if (previd.Success)
                lock (_lock)
                    if (_resumable.TryGetValue(previd.Groups[1].Value, out var found) &&
                        session.Account is not null &&
                        String.Equals(found.Session.BareJid, session.BareJid,
                                      StringComparison.OrdinalIgnoreCase))
                    {

                        processed = found.Session.StanzasReceivedFromClient;

                        if (found.Deadline > DateTimeOffset.UtcNow)
                        {
                            parked = found;
                            _resumable.Remove(previd.Groups[1].Value);
                        }

                    }

            if (parked is null)
            {

                // XEP-0198, section 5: the h is voluntary ("MAY also include")
                // and means a measurement - how much of the old stream the
                // server had processed. Here a fixed h='0' once stood, and that
                // was not information but an assertion: "of everything you
                // sent, nothing arrived". Whoever believes it and sends after
                // it delivers everything a second time.
                //
                // It is left out in both cases where the server has nothing to
                // say: it does not know the identifier - the normal case after
                // a restart or after the sweeper - or it belongs to another
                // account. In the second case the number would betray that this
                // stream exists and how much has run over it; out of a guessed
                // attempt would come a probe.
                await session.SendAsync(
                    $"<failed xmlns='urn:xmpp:sm:3'{(processed is null ? "" : $" h='{processed}'")}>" +
                    "<item-not-found xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/></failed>");

                return;

            }

            // The new stream takes the old one over. Only afterwards the
            // <resumed/>: it reports the receive counter, and that belongs to
            // the state taken over.
            var pending = session.AdoptResumed(parked.Session);

            await session.SendAsync(
                $"<resumed xmlns='urn:xmpp:sm:3' h='{session.StanzasReceivedFromClient}' " +
                $"previd='{XmlEscaping.Escape(previd.Groups[1].Value)}'/>");

            // What the old stream could not get rid of now goes after it. The
            // counter runs on in the process - the client has not seen these
            // stanzas yet, they count like every other one.
            var h = Regex.Match(frame, @"h=['""](\d+)['""]");
            var acknowledged = h.Success && UInt32.TryParse(h.Groups[1].Value, out var value)
                                   ? value
                                   : 0u;

            foreach (var (seq, stanza) in pending)
                if (unchecked(acknowledged - seq) >= 0x8000_0000u)
                    await session.SendAsync(stanza);

        }

        private async Task HandleStreamOpenAsync(XMPPSession session, Int32 openCount)
        {

            if (!AnswerStreamOpen)
                return;

            await session.SendAsync(
                $"<open xmlns='urn:ietf:params:xml:ns:xmpp-framing' from='{Domain}' id='stream-{session.ConnectionNumber}' version='1.0'/>");

            if (openCount == 1)
                await session.SendAsync(BeforeLoginFeatures());
            else
                await session.SendAsync(AfterLoginFeatures());

        }

        /// <summary>
        /// The features before the login: which SASL mechanisms, in both
        /// profiles.
        /// </summary>
        /// <remarks>
        /// RFC 6120's <c>&lt;mechanisms/&gt;</c> and XEP-0388's
        /// <c>&lt;authentication/&gt;</c> stand side by side, which the XEP
        /// provides for: it is a replacement profile, and during the transition
        /// a server advertises both so that a client which knows only one of
        /// them still gets in. Both list the same mechanisms - the profile
        /// decides how they are spoken, not which exist.
        /// </remarks>
        private String BeforeLoginFeatures()

            => "<stream:features xmlns:stream='http://etherx.jabber.org/streams'>" +

               "<mechanisms xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>" +
               String.Concat(AnnouncedSaslMechanisms.Select(m => $"<mechanism>{m}</mechanism>")) +
               "</mechanisms>" +

               // XEP-0388. No <inline/>: this server negotiates nothing along
               // with the authentication yet, and an empty <inline/> would
               // announce a capability with nothing in it. Bind 2 (XEP-0386) is
               // what belongs there, and it is not written.
               (OfferSasl2
                    ? "<authentication xmlns='urn:xmpp:sasl:2'>" +
                      String.Concat(AnnouncedSaslMechanisms.Select(m => $"<mechanism>{m}</mechanism>")) +

                      // XEP-0480. What this server can teach itself, not what
                      // any particular account needs - there is no account yet.
                      // A client that wants one asks for it in <authenticate/>,
                      // and only after the login is there anything to ask
                      // whether the material is missing.
                      (OfferScramUpgrades
                           ? String.Concat(
                                 SupportedUpgradeTasks.Select(
                                     t => $"<upgrade xmlns='{ScramUpgrade.Namespace}'>{t}</upgrade>"))
                           : "") +

                      // XEP-0386. Inside <inline/>, which is what that element
                      // is for: features negotiable as part of the
                      // authentication rather than after it.
                      //
                      // The <bind/> carries no nested <inline/> of its own,
                      // because this server enables nothing along with the
                      // binding - carbons and stream management are still
                      // asked for afterwards. An empty one would advertise a
                      // list of features that is empty, which is a slower way
                      // of saying nothing.
                      (OfferBind2
                           ? "<inline><bind xmlns='urn:xmpp:bind:0'/></inline>"
                           : "") +

                      "</authentication>"
                    : "") +

               // XEP-0440. Announced only when there is a binding to
               // announce, which means only over TLS and only for a
               // certificate RFC 5929 defines a hash for. An empty
               // <sasl-channel-binding/> would be a claim that the server
               // supports the extension and offers nothing - true, and
               // useless to a client deciding whether to bind.
               (ChannelBindingData is not null
                    ? "<sasl-channel-binding xmlns='urn:xmpp:sasl-cb:0'>" +
                      $"<channel-binding type='{TlsServerEndPoint.Name}'/>" +
                      "</sasl-channel-binding>"
                    : "") +

               "</stream:features>";

        /// <summary>
        /// The features after the login: binding, session, stream management
        /// and what else this server offers an authenticated stream.
        /// </summary>
        /// <remarks>
        /// Its own method because two paths now send it. RFC 6120, section
        /// 6.4.6 has the client open a new stream after SASL and the server
        /// answers that with these; XEP-0388, section 3.6 has no restart at all
        /// and the server sends them straight after <c>&lt;success/&gt;</c>.
        /// The content is the same either way, and it must stay so - a client
        /// that chose the newer profile is not owed a smaller stream.
        /// </remarks>
        private String AfterLoginFeatures()

            => "<stream:features xmlns:stream='http://etherx.jabber.org/streams'>" +
                    "<bind xmlns='urn:ietf:params:xml:ns:xmpp-bind'/>" +
                    (SessionRequired
                         ? "<session xmlns='urn:ietf:params:xml:ns:xmpp-session'/>"
                         : "<session xmlns='urn:ietf:params:xml:ns:xmpp-session'><optional/></session>") +
                    // XEP-0198, section 3 shows the feature exactly like this:
                    // the <optional/> belongs to the <sm/> and says nothing
                    // about the legacy session.
                    "<sm xmlns='urn:xmpp:sm:3'><optional/></sm>" +

                    // RFC 6121, section 3.4: purely informative, never to be
                    // negotiated - but without the announcement a client must
                    // not use pre-approval.
                    (OfferSubscriptionPreApproval
                         ? "<sub xmlns='urn:xmpp:features:pre-approval'/>"
                         : "") +

                    // RFC 6121, section 2.6.1: without this announcement a
                    // client must not append a 'ver' to its roster request - it
                    // would otherwise not know whether an empty result means
                    // "unchanged" or "empty roster".
                    (OfferRosterVersioning
                         ? "<ver xmlns='urn:xmpp:features:rosterver'/>"
                         : "") +

                    // XEP-0352, section 4.1: "If the server supports CSI, it
                    // advertises it in the stream features after the client has
                    // authenticated." Hence only here and not in the first
                    // features - before the login there is nobody whose state
                    // would be worth sparing.
               (OfferClientStateIndication
                    ? ClientStateIndication.FeatureXml
                    : "") +
               "</stream:features>";

        /// <summary>
        /// How many authentication attempts may fail on one stream before it is
        /// ended. Zero means: as many as the peer likes.
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 13.12 lists this among the measures against denial
        /// of service, and there was none at all: a stream could carry
        /// <c>&lt;auth/&gt;</c> after <c>&lt;auth/&gt;</c> without limit, so a
        /// password could be guessed at the speed of the network on a single
        /// connection.
        ///
        /// <b>Per stream, deliberately, and not per account.</b> A counter on
        /// the account is a lock that a stranger can turn - fail often enough
        /// at Alice's name and the server shuts Alice out. This one costs the
        /// guesser a new connection for every handful of tries and costs nobody
        /// else anything.
        ///
        /// Five, because nobody mistypes a password five times inside one
        /// connection: a client that got it wrong sends one <c>&lt;auth/&gt;</c>
        /// and asks the human being again.
        /// </remarks>
        public Int32 MaxAuthenticationFailuresPerStream { get; set; } = 5;

        /// <summary>
        /// Refuses an authentication attempt - and ends the stream once there
        /// have been too many.
        /// </summary>
        /// <remarks>
        /// One door for every refusal, so that none of them can be counted
        /// past. Every <c>&lt;failure/&gt;</c> of the SASL negotiation goes
        /// through here; a new one that did not would be a way of guessing that
        /// costs nothing.
        /// </remarks>
        private async Task RefuseAuthenticationAsync(XMPPSession session, String condition)
        {

            // XEP-0388, section 3.5: the failure element moves to the SASL2
            // namespace, but the condition inside it stays an RFC 6120 one -
            // the profile changed, not the vocabulary of what can go wrong.
            await session.SendAsync(
                session.UsesSasl2
                    ? $"<failure xmlns='urn:xmpp:sasl:2'>" +
                      $"<{condition} xmlns='urn:ietf:params:xml:ns:xmpp-sasl'/>" +
                      $"</failure>"
                    : $"<failure xmlns='urn:ietf:params:xml:ns:xmpp-sasl'><{condition}/></failure>");

            session.FailedAuthentications++;

            if (MaxAuthenticationFailuresPerStream > 0 &&
                session.FailedAuthentications >= MaxAuthenticationFailuresPerStream)
            {
                await session.SendStreamErrorAsync(
                          "policy-violation",
                          $"More than {MaxAuthenticationFailuresPerStream} failed authentication " +
                          "attempts on one stream.");
            }

        }

        /// <summary>
        /// Draws a decoy key and hands it to the store to keep.
        /// </summary>
        /// <remarks>
        /// It must not be guessable: whoever knows it can recompute every
        /// invented salt and tell again which account exists.
        /// </remarks>
        private Byte[] NewDecoySecret()
        {

            var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

            // A store that keeps nothing ignores this - and is right to. What
            // must not happen is that the key is drawn here and the storing
            // forgotten, because then every start has a new one and nothing has
            // changed.
            _accountStore.SaveDecoySecret(secret);

            return secret;

        }

        /// <summary>
        /// XEP-0388, section 3.2: <c>&lt;authenticate/&gt;</c> opens the newer
        /// profile.
        /// </summary>
        /// <remarks>
        /// Only the wrapping differs from <c>&lt;auth/&gt;</c>: the mechanism
        /// is an attribute in both, and the initial response moves from the
        /// element's own text into a child <c>&lt;initial-response/&gt;</c>.
        /// Once those two are read, the exchange from here on is the same
        /// SCRAM or PLAIN as ever - which is the point of the profile split and
        /// the reason this method ends by calling the same code.
        ///
        /// The <c>&lt;user-agent/&gt;</c> a client may send is read and
        /// discarded. It exists so a server can show somebody the list of their
        /// own logins, and this server keeps no such list; parsing it to throw
        /// it away would only look like a feature.
        /// </remarks>
        /// <summary>
        /// Which upgrade this session should run now, or null for none.
        /// </summary>
        /// <remarks>
        /// Three conditions, and the middle one is the whole point. The client
        /// has to have asked - key material is not something to collect
        /// uninvited. The account has to actually lack the mechanism, or the
        /// exchange would cost a round trip to overwrite what is already there.
        /// And the server has to be willing, which is the switch above.
        ///
        /// Only one at a time: XEP-0388 allows a sequence of tasks, and running
        /// them one per login is enough for something that happens once in an
        /// account's life.
        /// </remarks>
        private SCRAMMechanism? UpgradeWantedBy(XMPPSession session)
        {

            if (!OfferScramUpgrades || session.Account is null)
                return null;

            foreach (var task in session.RequestedUpgrades)
            {

                if (ScramUpgrade.MechanismOf(task) is not SCRAMMechanism mechanism)
                    continue;

                if (!SupportedUpgradeTasks.Contains(task, StringComparer.Ordinal))
                    continue;

                if (!session.Account.Credentials.Has(mechanism))
                    return mechanism;

            }

            return null;

        }

        /// <summary>
        /// XEP-0388, section 3.4: the client picks a task out of the
        /// <c>&lt;continue/&gt;</c>.
        /// </summary>
        private async Task HandleSasl2NextAsync(XMPPSession session, String frame)
        {

            XElement next;

            try
            {
                next = XElement.Parse(frame);
            }
            catch (System.Xml.XmlException)
            {
                await RefuseAuthenticationAsync(session, "malformed-request");
                return;
            }

            var task = next.Attribute("task")?.Value;

            // Against what this session was actually offered, not against what
            // the server can do in general: a client that names a task it was
            // not given is not choosing, it is guessing.
            if (session.PendingUpgrade is not SCRAMMechanism pending ||
                !String.Equals(task, ScramUpgrade.TaskNameOf(pending), StringComparison.Ordinal))
            {
                await RefuseAuthenticationAsync(session, "malformed-request");
                return;
            }

            var credentials = session.Account!.Credentials;

            await session.SendAsync(
                "<task-data xmlns='urn:xmpp:sasl:2'>" +
                $"<salt xmlns='{ScramUpgrade.DataNamespace}' iterations='{credentials.IterationCount}'>" +
                Convert.ToBase64String(credentials.Salt) +
                "</salt>" +
                "</task-data>");

        }

        /// <summary>
        /// The client's answer to the salt: the SaltedPassword for the new
        /// mechanism (XEP-0480).
        /// </summary>
        /// <remarks>
        /// From it the server derives the two keys RFC 5802 stores and keeps
        /// them beside the ones it already had. The account gains a mechanism
        /// and loses none - the login that is running used one of the old ones,
        /// and taking it away underneath would end the session that just
        /// upgraded it.
        /// </remarks>
        private async Task HandleSasl2TaskDataAsync(XMPPSession session, String frame)
        {

            if (session.PendingUpgrade is not SCRAMMechanism pending || session.Account is null)
            {
                await RefuseAuthenticationAsync(session, "not-authorized");
                return;
            }

            XElement taskData;

            try
            {
                taskData = XElement.Parse(frame);
            }
            catch (System.Xml.XmlException)
            {
                await RefuseAuthenticationAsync(session, "malformed-request");
                return;
            }

            var hash = taskData.Child(ScramUpgrade.DataNamespace, "hash")?.Value.Trim();

            Byte[] saltedPassword;

            try
            {
                saltedPassword = Convert.FromBase64String(hash ?? "");
            }
            catch (FormatException)
            {
                await RefuseAuthenticationAsync(session, "malformed-request");
                return;
            }

            // A SaltedPassword has the length of the mechanism's hash. Anything
            // else is not short key material, it is a client that computed
            // something other than what was asked for, and storing it would
            // produce an account nobody can log into.
            var expected = pending == SCRAMMechanism.ScramSha256 ? 32 : 20;

            if (saltedPassword.Length != expected)
            {
                await RefuseAuthenticationAsync(session, "malformed-request");
                return;
            }

            session.Account.UpgradeCredentials(pending, saltedPassword);
            session.PendingUpgrade = null;

            await SendSaslSuccessAsync(session, null);

        }

        private async Task HandleSasl2AuthenticateAsync(XMPPSession session, String frame)
        {

            if (!OfferSasl2)
            {
                await RefuseAuthenticationAsync(session, "invalid-mechanism");
                return;
            }

            XElement authenticate;

            try
            {
                authenticate = XElement.Parse(frame);
            }
            catch (System.Xml.XmlException)
            {
                await RefuseAuthenticationAsync(session, "malformed-request");
                return;
            }

            // Set before anything can fail: from here every answer to this
            // session belongs in the SASL2 namespace, including the refusals
            // below. A failure sent in the older one would reach a client that
            // is not listening for it.
            session.UsesSasl2 = true;

            var mechanism  = authenticate.Attribute("mechanism")?.Value ?? "";
            var payload    = authenticate.Child("initial-response")?.Value.Trim() ?? "";

            // XEP-0386: an inline binding, if the client asked for one and this
            // server offers it. Recorded rather than acted on - XEP-0386 is
            // explicit that a bind request MUST NOT be processed when the
            // authentication fails, and at this point it has not even started.
            var bind = authenticate.Child("urn:xmpp:bind:0", "bind");

            session.WantsInlineBind  = OfferBind2 && bind is not null;
            session.InlineBindTag    = bind?.Child("tag")?.Value.Trim() is String tag && tag.Length > 0
                                           ? tag
                                           : null;

            // XEP-0480: which upgrades the client is willing to perform. Only
            // recorded here - whether any of them is needed cannot be known
            // until there is an account, which is after the exchange.
            session.RequestedUpgrades.Clear();
            session.RequestedUpgrades.AddRange(
                authenticate.Elements().
                             Where (e => e.Name.LocalName     == "upgrade" &&
                                         e.Name.NamespaceName == ScramUpgrade.Namespace).
                             Select(e => e.Value.Trim()).
                             Where (t => t.Length > 0));

            if (!AnnouncedSaslMechanisms.Contains(mechanism, StringComparer.Ordinal))
            {
                await RefuseAuthenticationAsync(session, "invalid-mechanism");
                return;
            }

            if (ScramMechanismOf(mechanism) is SCRAMMechanism scram)
            {
                await BeginScramAsync(session, payload, scram);
                return;
            }

            await HandlePlainAsync(session, payload);

        }

        private async Task HandleAuthAsync(XMPPSession session, String frame)
        {

            // Read with the XML parser and no longer with a pattern. The
            // pattern was <auth[^>]*>([^<]*)</auth>, and the [^>]* is where it
            // goes wrong: an attribute value may contain a '>' - XML only
            // requires '<' and '&' to be escaped - so a frame carrying one ends
            // the match in the middle of the attribute list and the rest of it
            // is read as the payload. That the base64 itself contains nothing
            // interesting is not the point; the point is that the frame decides
            // where the payload begins.
            //
            // The <auth/> element declares its own namespace (RFC 6120,
            // section 6.4.2), so unlike the dialback frames of S2SStream it is
            // well-formed on its own and there is nothing here that would force
            // a pattern.
            XElement auth;

            try
            {
                auth = XElement.Parse(frame);
            }
            catch (System.Xml.XmlException)
            {
                await RefuseAuthenticationAsync(session, "malformed-request");
                return;
            }

            var payload    = auth.Value;
            var mechanism  = auth.Attribute("mechanism")?.Value ?? "PLAIN";

            // A mechanism the server has not offered at all is to be refused -
            // otherwise the negotiation could be circumvented. Against the
            // *announced* list, which is the one the client saw: the -PLUS
            // variants exist only there, and checking against the bare list
            // would refuse exactly the channel-bound logins this server just
            // invited.
            if (!AnnouncedSaslMechanisms.Contains(mechanism, StringComparer.Ordinal))
            {
                await RefuseAuthenticationAsync(session, "invalid-mechanism");
                return;
            }

            if (ScramMechanismOf(mechanism) is SCRAMMechanism scram)
            {
                await BeginScramAsync(session, payload, scram);
                return;
            }

            await HandlePlainAsync(session, payload);

        }

        /// <summary>
        /// SASL PLAIN (RFC 4616): base64( \0 user \0 password ).
        /// </summary>
        private async Task HandlePlainAsync(XMPPSession session, String payload)
        {

            String user = "", password = "";

            try
            {
                var parts = Encoding.UTF8.GetString(Convert.FromBase64String(payload)).Split('\0');
                if (parts.Length >= 3)
                {
                    user      = parts[1];
                    password  = parts[2];
                }
            }
            catch { /* unreadable -> fails below */ }

            var account = GetAccount($"{user}@{Domain}");

            if (account is null || !account.Credentials.Verify(password))
            {
                await RefuseAuthenticationAsync(session, "not-authorized");
                return;
            }

            session.Account = account;
            await SendSaslSuccessAsync(session, null);

        }

        /// <summary>
        /// The successful end of a SASL exchange, in whichever profile it was
        /// begun.
        /// </summary>
        /// <param name="additionalData">
        /// The mechanism's final data - SCRAM's server-final-message - or null
        /// where the mechanism has none, as PLAIN does not.
        /// </param>
        /// <remarks>
        /// One method for both profiles, because the two differ in more than
        /// the namespace and the difference is easy to get half right.
        ///
        /// <list type="bullet">
        ///   <item>
        ///     The mechanism's data is the element's text in RFC 6120 and a
        ///     child <c>&lt;additional-data/&gt;</c> in XEP-0388.
        ///   </item>
        ///   <item>
        ///     SASL2 names the identity it just settled in
        ///     <c>&lt;authorization-identifier/&gt;</c>. The bare JID here: this
        ///     server binds no resource inline, so there is no full JID to name
        ///     yet, and claiming one would be a promise about a resource nobody
        ///     has asked for.
        ///   </item>
        ///   <item>
        ///     <b>And no stream restart.</b> RFC 6120, section 6.4.6 has the
        ///     client open a new stream and the server answer with fresh
        ///     features; XEP-0388, section 3.6 drops that round trip and has the
        ///     server send the features immediately. Forgetting this leaves both
        ///     ends waiting for the other.
        ///   </item>
        /// </list>
        /// </remarks>
        private async Task SendSaslSuccessAsync(XMPPSession session, String? additionalData)
        {

            if (!session.UsesSasl2)
            {
                await session.SendAsync(
                    additionalData is null
                        ? "<success xmlns='urn:ietf:params:xml:ns:xmpp-sasl'/>"
                        : $"<success xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>{additionalData}</success>");
                return;
            }

            // XEP-0480: the login worked, and before it is confirmed there may
            // be a task to run. Decided here rather than earlier because it
            // needs the account, and there was no account until a moment ago.
            //
            // The mechanism's final data goes into the <continue/> instead of
            // the <success/>, and that is not cosmetic: for SCRAM it is the
            // server-final-message, and a client that never receives it cannot
            // check the server signature. Withholding it until after the
            // upgrade would mean computing new key material for a peer that has
            // not yet proved it knows the old.
            if (UpgradeWantedBy(session) is SCRAMMechanism wanted)
            {

                session.PendingUpgrade = wanted;

                await session.SendAsync(
                    "<continue xmlns='urn:xmpp:sasl:2'>" +
                    (additionalData is not null
                         ? $"<additional-data>{additionalData}</additional-data>"
                         : "") +
                    $"<tasks><task>{ScramUpgrade.TaskNameOf(wanted)}</task></tasks>" +
                    "</continue>");

                return;

            }

            // XEP-0386, and only now: the binding happens after the exchange
            // has succeeded, never before. The identity in the success is then
            // the *full* JID rather than the bare one, which is the whole
            // saving - the client is bound and knows its resource without a
            // further round trip.
            var bound = session.WantsInlineBind;

            if (bound)
                BindResourceInline(session, session.InlineBindTag);

            await session.SendAsync(
                "<success xmlns='urn:xmpp:sasl:2'>" +
                (additionalData is not null
                     ? $"<additional-data>{additionalData}</additional-data>"
                     : "") +
                "<authorization-identifier>" +
                (bound ? session.FullJid : session.Account?.BareJid) +
                "</authorization-identifier>" +

                // Empty, and it has to be present all the same: XEP-0386 makes
                // <bound/> the signal that the binding happened. Its optional
                // content is the archive metadata of XEP-0313, which this
                // server does not keep.
                (bound ? "<bound xmlns='urn:xmpp:bind:0'/>" : "") +

                "</success>");

            await session.SendAsync(AfterLoginFeatures());

            // The same two things the <iq/> route does once a resource exists.
            // Skipping them would leave an inline-bound session invisible to
            // everything that waits for a binding.
            if (bound)
            {

                await OnSessionBound.InvokeAllAsync(handler => handler(Timestamp.Now, this, session, CancellationToken.None), Logger);

                foreach (var frameToDeliver in DeliverAfterBind.ToArray())
                    await session.SendAsync(frameToDeliver.Replace("{jid}", session.FullJid));

            }

        }

        /// <summary>
        /// SCRAM, first half: the client-first-message in, the
        /// server-first-message out (RFC 5802, section 5).
        /// </summary>
        private async Task BeginScramAsync(XMPPSession     session,
                                           String          payload,
                                           SCRAMMechanism  mechanism)
        {

            // The announced list travels into the exchange, where it becomes
            // the attribute h of the server-first-message (XEP-0474). It is the
            // announcement and not the mechanism just chosen: what the client
            // checks is the offer it was given, not the one it took - and it
            // has to be the same list the client saw, -PLUS entries and all, or
            // the two hash different strings and every channel-bound login
            // fails as a forged announcement.
            var announced = SignAnotherSaslAnnouncement
                                ? AnnouncedSaslMechanisms.Concat(["SCRAM-SHA-512"])
                                : AnnouncedSaslMechanisms;

            var exchange = SCRAMExchange.Begin(payload,
                                               mechanism,
                                               user => GetAccount($"{user}@{Domain}"),
                                               user => XMPPCredentials.Decoy(user, _decoySecret),
                                               announced,
                                               ChannelBindingData);

            if (exchange is null)
            {
                session.Scram = null;
                await RefuseAuthenticationAsync(session, "not-authorized");
                return;
            }

            session.Scram = exchange;

            await session.SendAsync(
                session.UsesSasl2
                    ? $"<challenge xmlns='urn:xmpp:sasl:2'>{exchange.Challenge}</challenge>"
                    : $"<challenge xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>{exchange.Challenge}</challenge>");

        }

        /// <summary>
        /// RFC 6120, section 6.4.4: The client breaks the SASL negotiation off.
        /// </summary>
        /// <remarks>
        /// The abort is an <b>intended</b> step and no protocol violation -
        /// hence a SASL failure and not a stream error, and hence the stream
        /// stays standing. Since D26 it ended here with
        /// <c>&lt;unsupported-stanza-type/&gt;</c>: not wrong literally,
        /// because the server did not support the element, but the worse of two
        /// answers. It forced the client into a new connection for something
        /// the RFC provides for within the existing one.
        ///
        /// The half exchange is discarded, and that is the actual content of an
        /// abort. If it stayed lying there, it could still be carried to its
        /// end with a <c>&lt;response/&gt;</c> pushed in later - the abort
        /// would then be a polite phrase and not a statement.
        ///
        /// It is answered in every state, after a completed login too.
        /// Section 6.4.4 ties the answer to no condition, and an abort without
        /// an exchange in progress simply aborts nothing.
        /// </remarks>
        private static async Task HandleSaslAbortAsync(XMPPSession session)
        {

            session.Scram = null;

            // Not counted against the stream's failures, and that is not an
            // oversight. An abort tests no password - whoever wants to guess
            // one has to send a proof, and that goes through the refusal above.
            // Counting it would only end streams of clients that changed their
            // minds.
            await session.SendAsync(
                "<failure xmlns='urn:ietf:params:xml:ns:xmpp-sasl'><aborted/></failure>");

        }

        /// <summary>
        /// SCRAM, second half: the client-final-message in, on success the
        /// <c>&lt;success/&gt;</c> together with the server signature out.
        /// </summary>
        private async Task HandleSaslResponseAsync(XMPPSession session, String frame)
        {

            var exchange = session.Scram;

            // A <response/> without a preceding <auth/> belongs to no exchange.
            if (exchange is null)
            {
                await RefuseAuthenticationAsync(session, "not-authorized");
                return;
            }

            session.Scram = null;

            // Read with the parser, for the same reason as the <auth/> above:
            // an attribute value may carry a '>', and [^>]* ends the match
            // there.
            String payload;

            try
            {
                payload = XElement.Parse(frame).Value;
            }
            catch (System.Xml.XmlException)
            {
                await RefuseAuthenticationAsync(session, "malformed-request");
                return;
            }

            var serverFinal = exchange.Complete(payload);

            if (serverFinal is null)
            {
                await RefuseAuthenticationAsync(session, "not-authorized");
                return;
            }

            session.Account = exchange.Account;

            if (OmitScramSignature)
                serverFinal = "";

            else if (CorruptScramSignature)
                serverFinal = Convert.ToBase64String(
                                  Encoding.UTF8.GetBytes(
                                      $"v={Convert.ToBase64String(new Byte[32])}"));

            // RFC 5802, section 3: the server signature belongs with it.
            // Without it the client cannot check that the peer knows the
            // password as well.
            await SendSaslSuccessAsync(session,
                                       // An empty string is what OmitScramSignature
                                       // produces, and it has to stay an empty
                                       // element rather than become no element:
                                       // the test that asks what a client does
                                       // with a missing signature depends on the
                                       // difference.
                                       serverFinal);

        }

        /// <summary>
        /// The SCRAM mechanism behind a name, or null for PLAIN and everything
        /// unknown.
        /// </summary>
        internal static SCRAMMechanism? ScramMechanismOf(String mechanism)
            => mechanism switch {
                   "SCRAM-SHA-1"         => SCRAMMechanism.ScramSha1,
                   "SCRAM-SHA-256"       => SCRAMMechanism.ScramSha256,

                   // The suffix says how the exchange is bound, not which hash
                   // it uses - SCRAM-SHA-256-PLUS is SHA-256 throughout. Whether
                   // a binding is actually required follows from the GS2 header
                   // the client sends, which SCRAMExchange checks; mapping it
                   // here would only duplicate that decision in a second place.
                   "SCRAM-SHA-1-PLUS"    => SCRAMMechanism.ScramSha1,
                   "SCRAM-SHA-256-PLUS"  => SCRAMMechanism.ScramSha256,

                   _                     => null
               };

        private async Task HandleIqAsync(XMPPSession session, String frame)
        {

            var id    = Attr(frame, "id");
            var type  = Attr(frame, "type");
            var to    = Attr(frame, "to");

            // RFC 6120, section 8.2.3, rule 2: without one of the four intended
            // values this stanza is neither a question nor an answer.
            //
            // Before the switch and not behind it: what goes to the server
            // itself never passes the delivery route and would otherwise fall
            // out at the back.
            if (!IqTypes.IsKnown(type))
            {
                await session.SendAsync(BadRequestIq(id));
                return;
            }

            // XEP-0163: PEP is answered by the server for the account - and
            // that BEFORE the forwarding.
            //
            // That is the core of the matter and easy to get wrong: a request
            // to bob@domain looks like a request to Bob and would go to his
            // session below. Then a bundle would only be retrievable as long as
            // Bob is online - and that is precisely not what PEP exists for.
            // The server answers on behalf of a human being who is not there
            // just now.
            if (OfferPersonalEventing &&
                frame.Contains(OmemoPep.PubSubNamespace, StringComparison.Ordinal) &&
                await HandlePepAsync(session, frame, id, type, to))
            {
                return;
            }

            // Directed at another entity? Then forward it.
            if (RouteStanzas &&
                to is not null &&
                !String.Equals(to, Domain, StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(BareOf(to), session.BareJid, StringComparison.OrdinalIgnoreCase))
            {

                var stamped = StampFrom(frame, session.FullJid);

                // A foreign domain: out with it.
                if (!IsLocal(to))
                {

                    if (!await RouteToAsync(to, stamped) &&
                        type != "error")
                    {
                        await SendRemoteServerNotFoundAsync(session, "iq", id, to);
                    }

                    return;

                }

                await DeliverIqLocallyAsync(session, to, stamped);

                return;

            }

            // Resource binding
            if (frame.Contains("urn:ietf:params:xml:ns:xmpp-bind", StringComparison.Ordinal) && type == "set")
            {
                await HandleBindAsync(session, frame, id);
                return;
            }

            // The legacy session
            if (frame.Contains("urn:ietf:params:xml:ns:xmpp-session", StringComparison.Ordinal))
            {
                await session.SendAsync($"<iq type='result' id='{id}'/>");
                return;
            }

            // XEP-0280 carbons on/off
            if (frame.Contains("urn:xmpp:carbons:2", StringComparison.Ordinal))
            {
                session.CarbonsEnabled = frame.Contains("<enable", StringComparison.Ordinal);
                await session.SendAsync($"<iq type='result' id='{id}'/>");
                return;
            }

            // The roster
            if (frame.Contains("jabber:iq:roster", StringComparison.Ordinal))
            {
                await HandleRosterAsync(session, frame, id, type);
                return;
            }

            // From here on the server answers for itself - and for that this
            // session is no longer needed, only a way back.
            if (AnswerAboutSelf(frame, id, type) is { } answer)
                await session.SendAsync(answer);

        }

        /// <summary>
        /// The namespace of the PubSub-specific error conditions (XEP-0060,
        /// section 6.1.3).
        /// </summary>
        private const String PubSubErrorNamespace = "http://jabber.org/protocol/pubsub#errors";

        /// <summary>
        /// The namespace of the owner requests (XEP-0060, section 8).
        /// </summary>
        /// <remarks>
        /// A namespace of its own and not an element of its own: whoever
        /// configures a node does something other than whoever uses it, and the
        /// XEP separates the two at the address already.
        /// </remarks>
        private const String PubSubOwnerNamespace = "http://jabber.org/protocol/pubsub#owner";

        private const String DataFormNamespace = DataForm.Namespace;

        /// <summary>
        /// XEP-0163 Personal Eventing: publishing, retrieving, subscribing to
        /// and unsubscribing from the PEP nodes of an account.
        /// </summary>
        /// <returns>
        /// false when this IQ has nothing to do with PEP - then the caller
        /// takes it like every other one.
        /// </returns>
        /// <remarks>
        /// <b>A subset, and that belongs said.</b> There is no node
        /// configuration, there are no access models and no filtered
        /// notifications through XEP-0115. The node is open, whoever asks gets
        /// - for a test server that is the right amount, for a real one it
        /// would be too little: there the access model decides who may see a
        /// bundle, and the feature announcement who wants a notification at
        /// all.
        ///
        /// Notified are those who also get presence - contacts with
        /// <c>from</c> or <c>both</c> and one's own further resources - and in
        /// addition the explicit subscribers. One subscription per node and
        /// JID; several at the same time, which the <c>subid</c> actually
        /// exists for, this server does not know.
        /// </remarks>
        private async Task<Boolean> HandlePepAsync(XMPPSession  session,
                                                   String       frame,
                                                   String?      id,
                                                   String?      type,
                                                   String?      to)
        {

            XElement iq;

            try
            {
                iq = XElement.Parse(frame);
            }
            catch (System.Xml.XmlException)
            {
                return false;
            }

            var pubsub = iq.Child(OmemoPep.PubSubNamespace, "pubsub");
            var owner  = iq.Child(PubSubOwnerNamespace,     "pubsub");

            if (session.Account is null || (pubsub is null && owner is null))
                return false;

            // The test switch: accepted and not answered. Deliberately here and
            // not before the detection - what is not PEP shall go its
            // accustomed way then too.
            if (!AnswerPepRequests)
                return true;

            #region Owner instructions (XEP-0060, section 8)

            if (owner is not null)
            {

                if (type is not ("get" or "set"))
                    return false;

                // Three instructions and one common preamble: who does the node
                // belong to, and does it exist at all.
                //
                // It once stood at each of them separately - the same decision
                // in several places, and every one of them could have quietly
                // overtaken the others. Whoever loosens one of them loosens it
                // here visibly for all, or not at all.
                var instruction = owner.Child(PubSubOwnerNamespace, "affiliations")  ??
                                  owner.Child(PubSubOwnerNamespace, "subscriptions") ??
                                  owner.Child(PubSubOwnerNamespace, "delete")        ??
                                  owner.Child(PubSubOwnerNamespace, "purge")         ??
                                  owner.Child(PubSubOwnerNamespace, "configure");

                if (instruction is null)
                    return false;

                // A PEP node belongs to a human being, and only they decide
                // about it. Foreign nodes are not merely inaccessible here -
                // whoever could configure them could switch the storage off,
                // for instance, and thereby make foreign bundles unreachable
                // without anything looking like an error.
                if (to is not null &&
                    !String.Equals(BareOf(to), session.BareJid, StringComparison.OrdinalIgnoreCase))
                {
                    await session.SendAsync(StanzaErrorIq(id, "forbidden", "auth"));
                    return true;
                }

                var node = instruction.Attr("node");

                if (String.IsNullOrEmpty(node))
                {
                    await session.SendAsync(BadRequestIq(id));
                    return true;
                }

                var existing = session.Account.PepNodeConfiguration(node);

                if (existing is null)
                {
                    await session.SendAsync(StanzaErrorIq(id, "item-not-found"));
                    return true;
                }

                #region Managing roles (XEP-0060, section 8.9)

                if (instruction.Name.LocalName == "affiliations")
                {

                    if (type == "get")
                    {

                        await session.SendAsync(
                            $"<iq type='result' id='{id}'>" +
                            $"<pubsub xmlns='{PubSubOwnerNamespace}'>" +
                            $"<affiliations node='{XmlEscaping.Escape(node)}'>" +
                            String.Concat(session.Account.PepAffiliations(node).Select(r =>
                                $"<affiliation jid='{XmlEscaping.Escape(r.Jid)}'" +
                                $" affiliation='{PubSubAffiliations.NameOf(r.Affiliation)}'/>")) +
                            "</affiliations></pubsub></iq>");

                        return true;

                    }

                    foreach (var entry in instruction.Children(PubSubOwnerNamespace, "affiliation"))
                    {

                        // First check everything, then carry everything out: a
                        // request that holds by half would be worse than one
                        // refused entirely - the sender would not know which
                        // half.
                        if (entry.Attr("jid") is not String who ||
                            !PubSubAffiliations.TryRead(entry.Attr("affiliation"), out var role))
                        {
                            await session.SendAsync(BadRequestIq(id));
                            return true;
                        }

                        // XEP-0060, section 8.9.2: the owner is the account.
                        // Whoever could transfer them could take someone else's
                        // own account away from them.
                        if (String.Equals(BareOf(who), session.BareJid, StringComparison.OrdinalIgnoreCase) ||
                            role == PubSubAffiliation.Owner)
                        {
                            await session.SendAsync(StanzaErrorIq(id, "not-allowed", "cancel"));
                            return true;
                        }

                    }

                    var ended = new List<PepSubscription>();

                    foreach (var entry in instruction.Children(PubSubOwnerNamespace, "affiliation"))
                    {

                        PubSubAffiliations.TryRead(entry.Attr("affiliation"), out var role);

                        session.Account.SetPepAffiliation(node,
                                                          BareOf(entry.Attr("jid")!),
                                                          role,
                                                          out var alsoEnded);

                        ended.AddRange(alsoEnded);

                    }

                    await session.SendAsync($"<iq type='result' id='{id}'/>");

                    // An exclusion ends subscriptions (section 8.9.4), and of
                    // that too the person concerned learns. The exclusion
                    // itself stays hidden from them: what they are at this node
                    // is none of their business - that they no longer get it
                    // is.
                    foreach (var one in ended)
                        await NotifySubscriptionStateAsync(session.Account, node, one,
                                                           PubSubSubscriptionState.None);

                    return true;

                }

                #endregion

                #region Managing subscribers (XEP-0060, section 8.8)

                if (instruction.Name.LocalName == "subscriptions")
                {

                    var subscribers = session.Account.PepSubscriptions(node);

                    // XEP-0060, section 8.8.1: who hangs on this node.
                    //
                    // <b>The opposite of section 5.6, and deliberately so.</b>
                    // There foreign subscriptions are kept quiet about, because
                    // they would be information about people - who is
                    // interested in what, across all nodes. Here the question
                    // is a different one: not "where is this person
                    // everywhere", but "who hangs on my node". This list is
                    // information about the node, and the owner is the one the
                    // recipients get their data from. Withholding the
                    // recipients from them would mean making them responsible
                    // for a distribution they are not allowed to see.
                    if (type == "get")
                    {

                        // <b>Here the state stood fixed in the text</b>, with
                        // the note that this would be one of the places that
                        // needed a real one as soon as there is `authorize`.
                        // There is - and this list is the place where the owner
                        // sees who is still waiting for their promise.
                        await session.SendAsync(
                            $"<iq type='result' id='{id}'>" +
                            $"<pubsub xmlns='{PubSubOwnerNamespace}'>" +
                            $"<subscriptions node='{XmlEscaping.Escape(node)}'>" +
                            String.Concat(subscribers.Select(a =>
                                $"<subscription jid='{XmlEscaping.Escape(a.Jid)}'" +
                                $" subid='{a.SubId}'" +
                                $" subscription='{PubSubSubscription.NameOf(a.State)}'/>")) +
                            "</subscriptions></pubsub></iq>");

                        return true;

                    }

                    // First check everything, then carry everything out - as
                    // with the roles and for the same reason.
                    foreach (var entry in instruction.Children(PubSubOwnerNamespace, "subscription"))
                    {

                        if (entry.Attr("jid") is not String who ||
                            !PubSubSubscription.TryReadState(entry.Attr("subscription"), out var state))
                        {
                            await session.SendAsync(BadRequestIq(id));
                            return true;
                        }

                        var meant = entry.Attr("subid");

                        var known = subscribers.Any(
                                        a => String.Equals(a.Jid, BareOf(who), StringComparison.OrdinalIgnoreCase) &&
                                             (meant is null || String.Equals(a.SubId, meant, StringComparison.Ordinal)));

                        // XEP-0060, section 8.8.2 lets the owner sign people up
                        // too. <b>This server only sign them off.</b> Entering
                        // somebody who has not asked is exactly what
                        // section 6.1.3.1 prevents on the other side; that it
                        // is one's own node changes nothing for the one whose
                        // mailbox fills up. Without an approval procedure there
                        // would also be nothing there that had been a question
                        // beforehand.
                        if (state != PubSubSubscriptionState.None)
                        {

                            // Naming the existing state once more is not an
                            // instruction but a confirmation. A list that
                            // cannot be sent back unchanged would not be a
                            // state but a form.
                            if (state == PubSubSubscriptionState.Subscribed && known)
                                continue;

                            await session.SendAsync(StanzaErrorIq(id, "not-allowed", "cancel"));
                            return true;

                        }

                        // What nobody finds is not ended either. Agreeing
                        // silently would mean reporting the success of an
                        // instruction that went nowhere - one typo in the JID,
                        // and the owner would take somebody for removed who
                        // keeps getting everything.
                        if (!known)
                        {
                            await session.SendAsync(StanzaErrorIq(id, "item-not-found"));
                            return true;
                        }

                    }

                    var changed = new List<(PepSubscription Subscription, PubSubSubscriptionState State)>();

                    foreach (var entry in instruction.Children(PubSubOwnerNamespace, "subscription"))
                    {

                        PubSubSubscription.TryReadState(entry.Attr("subscription"), out var state);

                        var who   = BareOf(entry.Attr("jid")!);
                        var meant = entry.Attr("subid");

                        if (state == PubSubSubscriptionState.None)
                            changed.AddRange(
                                session.Account.RemovePepSubscriptions(node, who, meant)
                                       .Select(a => (a, PubSubSubscriptionState.None)));

                        // XEP-0060, section 8.6: the promise on an application.
                        //
                        // In D84 it stood here that a `subscribed` was "not an
                        // instruction but a confirmation" - right, as long as
                        // there was nothing to approve. Now there is something:
                        // an applied-for subscription is promised, a promised
                        // one stays the confirmation from before.
                        else if (session.Account.ApprovePepSubscription(node, who, meant) is { } approved)
                            changed.Add((approved, PubSubSubscriptionState.Subscribed));

                    }

                    await session.SendAsync($"<iq type='result' id='{id}'/>");

                    // First the answer, then the reports - and only about what
                    // has really changed. A report about a subscription the
                    // server did not even find would be the same assertion into
                    // the blue as a `result` on it.
                    foreach (var (one, state) in changed)
                        await NotifySubscriptionStateAsync(session.Account, node, one, state);

                    return true;

                }

                #endregion

                #region Deleting and purging nodes (XEP-0060, sections 8.4 and 8.5)

                if (instruction.Name.LocalName is "delete" or "purge")
                {

                    // Both change something. A `get` on it is not a question
                    // that could be answered - and must under no circumstances
                    // land at the configuring further below, where it would get
                    // the node configuration back.
                    if (type != "set")
                    {
                        await session.SendAsync(BadRequestIq(id));
                        return true;
                    }

                    if (instruction.Name.LocalName == "delete")
                    {

                        var ended = session.Account.DeletePepNode(node)!;

                        await session.SendAsync($"<iq type='result' id='{id}'/>");

                        // XEP-0060, section 8.4.2. <b>One report per subscriber
                        // and not per subscription</b>, and without an
                        // identifier: it is not a subscription that ends but
                        // the node. Naming an identifier would mean the others
                        // continued to exist.
                        //
                        // A second report per section 8.8.4 does not go with
                        // it - that a subscription to a node that no longer
                        // exists has expired is said by this report already.
                        await NotifyPepNodeAsync(session.Account, session,
                                                 $"<delete node='{XmlEscaping.Escape(node)}'/>",
                                                 ended.Select(a => a.Jid));

                        return true;

                    }

                    // XEP-0060, section 8.5.3.2: what keeps nothing can give
                    // nothing. A `result` on it would be the information that
                    // something had been purged, and the report to the
                    // subscribers the request to throw away something this node
                    // never delivered.
                    if (!existing.PersistItems)
                    {
                        await session.SendAsync(
                            StanzaErrorIq(id, "feature-not-implemented", "cancel",
                                          applicationError: $"<unsupported xmlns='{PubSubErrorNamespace}'" +
                                                            " feature='persistent-items'/>"));
                        return true;
                    }

                    var subscribers = session.Account.PepSubscriptions(node).Select(a => a.Jid);

                    session.Account.PurgePepNode(node);

                    await session.SendAsync($"<iq type='result' id='{id}'/>");

                    // XEP-0060, section 8.5.2. The node stays, the
                    // subscriptions stay - the report only says that there is
                    // nothing left to fetch.
                    await NotifyPepNodeAsync(session.Account, session,
                                             $"<purge node='{XmlEscaping.Escape(node)}'/>",
                                             subscribers);

                    return true;

                }

                #endregion

                #region Configuring nodes (XEP-0060, section 8.2)

                if (type == "get")
                {

                    await session.SendAsync(
                        $"<iq type='result' id='{id}'>" +
                        $"<pubsub xmlns='{PubSubOwnerNamespace}'>" +
                        $"<configure node='{XmlEscaping.Escape(node)}'>" +
                        existing.ToForm().ToString(SaveOptions.DisableFormatting) +
                        "</configure></pubsub></iq>");

                    return true;

                }

                var form = instruction.Child(DataFormNamespace, "x");

                if (form is null ||
                    !PubSubNodeConfiguration.TryRead(form, existing, out var configured))
                {
                    await session.SendAsync(BadRequestIq(id));
                    return true;
                }

                session.Account.ConfigurePepNode(node, configured!);

                await session.SendAsync($"<iq type='result' id='{id}'/>");

                return true;

                #endregion

            }

            #endregion

            if (pubsub is null)
                return false;

            #region Creating

            if (type == "set" && pubsub.Child(OmemoPep.PubSubNamespace, "create") is { } create)
            {

                if (to is not null &&
                    !String.Equals(BareOf(to), session.BareJid, StringComparison.OrdinalIgnoreCase))
                {
                    await session.SendAsync(StanzaErrorIq(id, "forbidden", "auth"));
                    return true;
                }

                var node = create.Attr("node");

                // XEP-0060, section 8.1.2 knows nodes without a name that the
                // service names. Not here: a PEP node is found through its name,
                // and an invented one nobody knows except whoever just got it.
                if (String.IsNullOrEmpty(node))
                {
                    await session.SendAsync(BadRequestIq(id));
                    return true;
                }

                var wish = PubSubNodeConfiguration.Default;

                if (pubsub.Child(OmemoPep.PubSubNamespace, "configure")?.Child(DataFormNamespace, "x") is { } supplied &&
                    !PubSubNodeConfiguration.TryRead(supplied, wish, out wish))
                {
                    await session.SendAsync(BadRequestIq(id));
                    return true;
                }

                // XEP-0060, section 8.1.3: what exists is not created a second
                // time. Letting it stand silently would mean replacing an
                // existing setting with a new one without anyone having asked
                // for it.
                if (!session.Account.CreatePepNode(node, wish))
                {
                    await session.SendAsync(StanzaErrorIq(id, "conflict"));
                    return true;
                }

                await session.SendAsync(
                    $"<iq type='result' id='{id}'>" +
                    $"<pubsub xmlns='{OmemoPep.PubSubNamespace}'>" +
                    $"<create node='{XmlEscaping.Escape(node)}'/>" +
                    "</pubsub></iq>");

                return true;

            }

            #endregion

            #region Publishing

            if (type == "set" && pubsub.Child(OmemoPep.PubSubNamespace, "publish") is { } publish)
            {

                var node = publish.Attr("node");
                var item = publish.Elements().FirstOrDefault(e => e.Name.LocalName == "item");

                if (String.IsNullOrEmpty(node) || item is null)
                {
                    await session.SendAsync(BadRequestIq(id));
                    return true;
                }

                // The node belongs to the account and not to whoever writes.
                var nodeOwner = to is null ||
                            String.Equals(BareOf(to), session.BareJid, StringComparison.OrdinalIgnoreCase)
                                ? session.Account
                                : GetAccount(BareOf(to));

                // Write may whoever owns the node or whoever the owner has
                // permitted it - one rule for both cases.
                //
                // Without it anybody could exchange foreign bundles; that would
                // be the attack the signature over the signed prekey stands
                // against. With it, it is a role the owner has handed out and
                // takes back at any time.
                //
                // That a publisher cannot create a node in a foreign account
                // follows from it by itself: a role belongs to a node, and at
                // one that does not exist nobody has one.
                if (nodeOwner?.PepAffiliationOf(node, session.BareJid!)
                        is not (PubSubAffiliation.Owner or PubSubAffiliation.Publisher))
                {
                    await session.SendAsync(StanzaErrorIq(id, "forbidden", "auth"));
                    return true;
                }

                // XEP-0060, section 7.1.5: conditions on the node.
                //
                // OMEMO has been sending them along since D66 - and until K8
                // nobody read them. That was the quietest way of giving a
                // promise: the client demanded an open node, got a 'result' and
                // was allowed to assume its bundle was retrievable.
                if (pubsub.Child(OmemoPep.PubSubNamespace, "publish-options")?.Child(DataFormNamespace, "x") is { } conditions)
                {

                    if (!PubSubPublishOptions.TryRead(conditions, out var demanded))
                    {
                        await session.SendAsync(BadRequestIq(id));
                        return true;
                    }

                    var existing = nodeOwner.PepNodeConfiguration(node);

                    if (existing is null)
                        nodeOwner.CreatePepNode(node, demanded!.ApplyTo(PubSubNodeConfiguration.Default));

                    else if (!demanded!.AreMetBy(existing))
                    {
                        await session.SendAsync(
                            StanzaErrorIq(id, "conflict", "cancel",
                                          applicationError: $"<precondition-not-met xmlns='{PubSubErrorNamespace}'/>"));
                        return true;
                    }

                }

                var itemId   = item.Attr("id") ?? Guid.NewGuid().ToString("N")[..8];
                var content  = item.Elements().FirstOrDefault()?.ToString(SaveOptions.DisableFormatting) ?? "";

                nodeOwner.PublishPepItem(node, itemId, content);

                await session.SendAsync(
                    $"<iq type='result' id='{id}'>" +
                    $"<pubsub xmlns='{OmemoPep.PubSubNamespace}'>" +
                    $"<publish node='{XmlEscaping.Escape(node)}'>" +
                    $"<item id='{XmlEscaping.Escape(itemId)}'/>" +
                    "</publish></pubsub></iq>");

                await NotifyPepAsync(nodeOwner, session, node,
                                     $"<item id='{XmlEscaping.Escape(itemId)}'>{content}</item>");

                return true;

            }

            #endregion

            #region Retracting (XEP-0060, section 7.2)

            if (type == "set" && pubsub.Child(OmemoPep.PubSubNamespace, "retract") is { } retract)
            {

                var node   = retract.Attr("node");
                var entry  = retract.Child(OmemoPep.PubSubNamespace, "item")?.Attr("id");

                // Without an identifier there is no saying what is to be
                // retracted. XEP-0060 knows no "retract just anything" - for
                // that there is the purging, and that is another instruction
                // with another report.
                if (String.IsNullOrEmpty(node) || String.IsNullOrEmpty(entry))
                {
                    await session.SendAsync(BadRequestIq(id));
                    return true;
                }

                var nodeOwner = to is null ||
                            String.Equals(BareOf(to), session.BareJid, StringComparison.OrdinalIgnoreCase)
                                ? session.Account
                                : GetAccount(BareOf(to));

                // The same rule as when publishing, and that is the decision:
                // <b>whoever may write may also retract.</b>
                //
                // A publisher would thereby get at foreign items in the same
                // node. Telling them apart would mean remembering who wrote
                // which - a store that does not exist here, and without which
                // every finer rule would merely be asserted.
                if (nodeOwner?.PepAffiliationOf(node, session.BareJid!)
                        is not (PubSubAffiliation.Owner or PubSubAffiliation.Publisher))
                {
                    await session.SendAsync(StanzaErrorIq(id, "forbidden", "auth"));
                    return true;
                }

                // XEP-0060, section 7.2.3.3, as with the purging: what keeps
                // nothing can retract nothing.
                if (nodeOwner.PepNodeConfiguration(node) is { PersistItems: false })
                {
                    await session.SendAsync(
                        StanzaErrorIq(id, "feature-not-implemented", "cancel",
                                      applicationError: $"<unsupported xmlns='{PubSubErrorNamespace}'" +
                                                        " feature='persistent-items'/>"));
                    return true;
                }

                // XEP-0060, section 7.2.3.2. A `result` on an item that does
                // not exist would be the information that it is gone now - and
                // the report to the subscribers the request to throw away
                // something they never got.
                if (!nodeOwner.RetractPepItem(node, entry))
                {
                    await session.SendAsync(StanzaErrorIq(id, "item-not-found"));
                    return true;
                }

                await session.SendAsync($"<iq type='result' id='{id}'/>");

                // XEP-0060, section 7.2.2.1: the same delivery as a
                // publication, only with other content. Whoever got the item
                // otherwise keeps taking it for valid.
                await NotifyPepAsync(nodeOwner, session, node,
                                     $"<retract id='{XmlEscaping.Escape(entry)}'/>");

                return true;

            }

            #endregion

            #region Retrieving

            if (type == "get" && pubsub.Child(OmemoPep.PubSubNamespace, "items") is { } items)
            {

                var node = items.Attr("node");

                if (String.IsNullOrEmpty(node))
                {
                    await session.SendAsync(BadRequestIq(id));
                    return true;
                }

                var account = to is null
                                  ? session.Account
                                  : GetAccount(BareOf(to));

                // An account that does not exist is not distinguished from one
                // that has published nothing: otherwise it could be found out
                // through PEP which accounts exist on this server - the same
                // consideration as with the login (RFC 6120, section 13.11, see
                // D50).
                if (account is not null &&
                    account.PepNodeExists(node) &&
                    PepAccessErrorIq(id, account, node, session.BareJid!) is { } refused)
                {
                    await session.SendAsync(refused);
                    return true;
                }

                var sought   = items.Elements().FirstOrDefault(e => e.Name.LocalName == "item")?.Attr("id");
                var entries  = account?.GetPepItems(node, sought) ?? [];

                if (entries.Count == 0)
                {
                    await session.SendAsync(StanzaErrorIq(id, "item-not-found", "cancel"));
                    return true;
                }

                await session.SendAsync(
                    $"<iq type='result' id='{id}'" +
                    (to is not null ? $" from='{XmlEscaping.Escape(BareOf(to)!)}'" : "") + ">" +
                    $"<pubsub xmlns='{OmemoPep.PubSubNamespace}'>" +
                    $"<items node='{XmlEscaping.Escape(node)}'>" +
                    String.Concat(entries.Select(e =>
                        $"<item id='{XmlEscaping.Escape(e.ItemId)}'>{e.Payload}</item>")) +
                    "</items></pubsub></iq>");

                return true;

            }

            #endregion

            #region Enumerating one's own roles

            if (type == "get" && pubsub.Child(OmemoPep.PubSubNamespace, "affiliations") is not null)
            {

                var account = to is null
                                  ? session.Account
                                  : GetAccount(BareOf(to));

                // As with the subscriptions: the roles *of the asker*. Whoever
                // were allowed to enumerate foreign ones would learn who may do
                // what where.
                var mine = account?.PepAffiliationsOf(session.BareJid!) ?? [];

                await session.SendAsync(
                    $"<iq type='result' id='{id}'" +
                    (to is not null ? $" from='{XmlEscaping.Escape(BareOf(to)!)}'" : "") + ">" +
                    $"<pubsub xmlns='{OmemoPep.PubSubNamespace}'><affiliations>" +
                    String.Concat(mine.Select(r =>
                        $"<affiliation node='{XmlEscaping.Escape(r.Node)}'" +
                        $" affiliation='{PubSubAffiliations.NameOf(r.Affiliation)}'/>")) +
                    "</affiliations></pubsub></iq>");

                return true;

            }

            #endregion

            #region Enumerating subscriptions

            if (type == "get" && pubsub.Child(OmemoPep.PubSubNamespace, "subscriptions") is { } enumeration)
            {

                var account = to is null
                                  ? session.Account
                                  : GetAccount(BareOf(to));

                var node = enumeration.Attr("node");

                // XEP-0060, section 5.6: the subscriptions *of the asker*.
                //
                // Never those of another, and that is no matter of
                // interpretation: whoever were allowed to enumerate foreign
                // ones would learn who is interested in what - information
                // about people, not about nodes.
                var theirs = account?.PepSubscriptionsOf(session.BareJid!) ?? [];

                if (!String.IsNullOrEmpty(node))
                    theirs = [.. theirs.Where(e => String.Equals(e.Node, node, StringComparison.Ordinal))];

                // No subscriptions is an empty list and not an error: the
                // question was answerable, the answer is "none".
                await session.SendAsync(
                    $"<iq type='result' id='{id}'" +
                    (to is not null ? $" from='{XmlEscaping.Escape(BareOf(to)!)}'" : "") + ">" +
                    $"<pubsub xmlns='{OmemoPep.PubSubNamespace}'>" +
                    "<subscriptions" + (String.IsNullOrEmpty(node) ? "" : $" node='{XmlEscaping.Escape(node)}'") + ">" +
                    String.Concat(theirs.Select(e =>
                        $"<subscription node='{XmlEscaping.Escape(e.Node)}'" +
                        $" jid='{XmlEscaping.Escape(e.Subscription.Jid)}'" +
                        $" subid='{e.Subscription.SubId}'" +
                        $" subscription='{PubSubSubscription.NameOf(e.Subscription.State)}'/>")) +
                    "</subscriptions></pubsub></iq>");

                return true;

            }

            #endregion

            #region Subscribing

            if (type == "set" && pubsub.Child(OmemoPep.PubSubNamespace, "subscribe") is { } subscribe)
            {

                var node = subscribe.Attr("node");

                if (String.IsNullOrEmpty(node))
                {
                    await session.SendAsync(BadRequestIq(id));
                    return true;
                }

                // XEP-0060, section 6.1.3.1: the JID given has to be that of
                // the sender.
                //
                // Without this check anybody could sign anybody up, and the
                // person signed up would get publications from then on that
                // they never demanded - from a node whose name they do not
                // know. They could only unsubscribe from it if they hit on what
                // they have to search for.
                if (subscribe.Attr("jid") is not String who ||
                    !String.Equals(BareOf(who), session.BareJid, StringComparison.OrdinalIgnoreCase))
                {
                    await session.SendAsync(
                        StanzaErrorIq(id, "bad-request", "modify",
                                      applicationError: $"<invalid-jid xmlns='{PubSubErrorNamespace}'/>"));
                    return true;
                }

                var account = to is null
                                  ? session.Account
                                  : GetAccount(BareOf(to));

                // XEP-0060, section 6.1.3.12. An account that does not exist is
                // here too not to be distinguished from one that has published
                // nothing - the same consideration as when retrieving. It
                // exists as soon as it is created - not only as soon as
                // something stands in it. Otherwise a created node could not be
                // subscribed to and the creating would be without consequence.
                if (account is null || !account.PepNodeExists(node))
                {
                    await session.SendAsync(StanzaErrorIq(id, "item-not-found"));
                    return true;
                }

                // XEP-0060, sections 6.1.3.4 and 6.1.3.8
                if (PepAccessErrorIq(id, account, node, session.BareJid!, forSubscribing: true) is { } refused)
                {
                    await session.SendAsync(refused);
                    return true;
                }

                var subscription = account.AddPepSubscription(node, session.BareJid!);

                // The state comes from the subscription created and no longer
                // stands fixed in the text: on a node with an approval
                // procedure it is `pending`, and whoever read that as a promise
                // would wait for reports somebody has to release first.
                await session.SendAsync(
                    $"<iq type='result' id='{id}'" +
                    (to is not null ? $" from='{XmlEscaping.Escape(BareOf(to)!)}'" : "") + ">" +
                    $"<pubsub xmlns='{OmemoPep.PubSubNamespace}'>" +
                    $"<subscription node='{XmlEscaping.Escape(node)}'" +
                    $" jid='{XmlEscaping.Escape(session.BareJid!)}'" +
                    $" subid='{subscription.SubId}'" +
                    $" subscription='{PubSubSubscription.NameOf(subscription.State)}'/>" +
                    "</pubsub></iq>");

                // XEP-0060, section 8.6.1: the owner learns of the application
                // without having to look.
                if (subscription.State == PubSubSubscriptionState.Pending)
                    await RequestSubscriptionApprovalAsync(account, node, subscription);

                return true;

            }

            #endregion

            #region Unsubscribing

            if (type == "set" && pubsub.Child(OmemoPep.PubSubNamespace, "unsubscribe") is { } unsubscribe)
            {

                var node = unsubscribe.Attr("node");

                if (String.IsNullOrEmpty(node))
                {
                    await session.SendAsync(BadRequestIq(id));
                    return true;
                }

                if (unsubscribe.Attr("jid") is not String who ||
                    !String.Equals(BareOf(who), session.BareJid, StringComparison.OrdinalIgnoreCase))
                {
                    await session.SendAsync(
                        StanzaErrorIq(id, "bad-request", "modify",
                                      applicationError: $"<invalid-jid xmlns='{PubSubErrorNamespace}'/>"));
                    return true;
                }

                var account = to is null
                                  ? session.Account
                                  : GetAccount(BareOf(to));

                var result = account?.RemovePepSubscription(node,
                                                            session.BareJid!,
                                                            unsubscribe.Attr("subid"))
                                 ?? PepSubscriptionResult.NotSubscribed;

                await session.SendAsync(result switch {

                    // XEP-0060, section 6.2.3.1: several, and none named. Here a
                    // bad-request, when configuring a not-acceptable - see
                    // PepSubscriptionResult.
                    PepSubscriptionResult.SubIdRequired
                        => StanzaErrorIq(id, "bad-request", "modify",
                                         applicationError: $"<subid-required xmlns='{PubSubErrorNamespace}'/>"),

                    PepSubscriptionResult.Ok
                        => $"<iq type='result' id='{id}'" +
                           (to is not null ? $" from='{XmlEscaping.Escape(BareOf(to)!)}'" : "") + "/>",

                    _   => SubscriptionErrorIq(id, result)

                });

                return true;

            }

            #endregion

            #region Configuring

            if (pubsub.Child(OmemoPep.PubSubNamespace, "options") is { } options &&
                type is "get" or "set")
            {

                var node = options.Attr("node");

                if (String.IsNullOrEmpty(node))
                {
                    await session.SendAsync(BadRequestIq(id));
                    return true;
                }

                // The third place with the same check, and the quietest:
                // whoever were allowed to configure foreign subscriptions could
                // switch them off silently. The subscription would stay
                // standing - only nothing would arrive any more, and the person
                // concerned would find nothing conspicuous in their own list.
                if (options.Attr("jid") is not String whoConfigures ||
                    !String.Equals(BareOf(whoConfigures), session.BareJid, StringComparison.OrdinalIgnoreCase))
                {
                    await session.SendAsync(
                        StanzaErrorIq(id, "bad-request", "modify",
                                      applicationError: $"<invalid-jid xmlns='{PubSubErrorNamespace}'/>"));
                    return true;
                }

                var account = to is null
                                  ? session.Account
                                  : GetAccount(BareOf(to));

                var subId = options.Attr("subid");

                // No `account?.Find(...)`: a conditional call leaves the out
                // parameter unwritten in case of doubt, and the compiler says so
                // rightly.
                PepSubscription? subscription = null;

                var finding = account is null
                                  ? PepSubscriptionResult.NotSubscribed
                                  : account.FindPepSubscription(node, session.BareJid!, subId, out subscription);

                if (finding != PepSubscriptionResult.Ok)
                {

                    // XEP-0060, section 6.3.3: here not-acceptable, when
                    // unsubscribing bad-request. The request is in order, it
                    // merely cannot be answered in this situation.
                    await session.SendAsync(
                        finding == PepSubscriptionResult.SubIdRequired
                            ? StanzaErrorIq(id, "not-acceptable", "modify",
                                            applicationError: $"<subid-required xmlns='{PubSubErrorNamespace}'/>")
                            : SubscriptionErrorIq(id, finding));

                    return true;

                }

                // The offer: what can be configured and what holds just now.
                if (type == "get")
                {

                    await session.SendAsync(
                        $"<iq type='result' id='{id}'" +
                        (to is not null ? $" from='{XmlEscaping.Escape(BareOf(to)!)}'" : "") + ">" +
                        $"<pubsub xmlns='{OmemoPep.PubSubNamespace}'>" +
                        $"<options node='{XmlEscaping.Escape(node)}'" +
                        $" jid='{XmlEscaping.Escape(session.BareJid!)}'" +
                        $" subid='{subscription!.SubId}'>" +
                        subscription.Options.ToForm().ToString(SaveOptions.DisableFormatting) +
                        "</options></pubsub></iq>");

                    return true;

                }

                var form = options.Child(DataFormNamespace, "x");

                if (form is null ||
                    !PubSubSubscriptionOptions.TryRead(form, out var configured))
                {
                    await session.SendAsync(
                        StanzaErrorIq(id, "bad-request", "modify",
                                      applicationError: $"<invalid-options xmlns='{PubSubErrorNamespace}'/>"));
                    return true;
                }

                account!.SetPepSubscriptionOptions(node, session.BareJid!, subscription!.SubId, configured!);

                await session.SendAsync(
                    $"<iq type='result' id='{id}'" +
                    (to is not null ? $" from='{XmlEscaping.Escape(BareOf(to)!)}'" : "") + "/>");

                return true;

            }

            #endregion

            return false;

        }

        /// <summary>
        /// May this JID get at the items of the node (XEP-0060, section 4.5)?
        /// </summary>
        /// <remarks>
        /// <b>The owner always.</b> They are not a presence subscriber at
        /// themselves, and a model that locked them out of their own node would
        /// not deserve the name.
        ///
        /// The model betrays in passing that the node exists: whoever has no
        /// access gets <c>&lt;not-authorized/&gt;</c> and not
        /// <c>&lt;item-not-found/&gt;</c>. That is how it is provided for
        /// (section 6.5.3) and is nevertheless a piece of information - for a
        /// node whose mere existence would be a secret, <c>presence</c> is the
        /// wrong means.
        /// </remarks>
        /// <remarks>
        /// <b>Only half of the question</b>, namely the one about the access
        /// model: who <i>may come in</i>. Who stays out is said by the role,
        /// and that stands in <see cref="PepAccessErrorIq"/> - checking both
        /// here would mean making the same decision in two places, and one of
        /// them would be forgotten the next time.
        /// </remarks>
        private static Boolean MayAccessPepNode(XMPPAccount account, String node, String requesterBareJid)
        {

            // The owner gets at their node, whichever model holds. At
            // themselves they are neither a presence subscriber nor on a list.
            if (String.Equals(account.BareJid, requesterBareJid, StringComparison.OrdinalIgnoreCase))
                return true;

            var configuration = account.PepNodeConfiguration(node);

            return configuration?.AccessModel switch {

                       PubSubAccessModel.Presence
                           => account.IsPresenceSubscriber(requesterBareJid),

                       // On the list stands whoever the owner has explicitly
                       // put on it - a presence authorisation comes about in
                       // passing, a role does not.
                       PubSubAccessModel.Whitelist
                           => account.PepAffiliationOf(node, requesterBareJid)
                                  is PubSubAffiliation.Publisher or PubSubAffiliation.Member,

                       // And here the list decides that the owner keeps anyway.
                       // Without groups named that is the whole roster; with
                       // them only whoever stands in one of them.
                       PubSubAccessModel.Roster
                           => account.IsInRosterGroups(requesterBareJid, configuration.RosterGroups),

                       // Let in is whoever the owner has promised. An
                       // applied-for subscription does not count - otherwise
                       // the approval would be a formality, and whoever asks
                       // would get everything in the same second.
                       PubSubAccessModel.Authorize
                           => account.PepSubscriptions(node).Any(
                                  a => String.Equals(a.Jid, requesterBareJid, StringComparison.OrdinalIgnoreCase) &&
                                       a.State == PubSubSubscriptionState.Subscribed),

                       _   => true

                   };

        }

        /// <summary>
        /// XEP-0060, sections 6.1.3.4 and 6.5.3: the node stands open only to
        /// those who may see the presence of its owner.
        /// </summary>
        private String NotAuthorizedForPepNodeIq(String? id)

            => StanzaErrorIq(id, "not-authorized", "auth",
                             applicationError: $"<presence-subscription-required xmlns='{PubSubErrorNamespace}'/>");

        /// <summary>
        /// The refusal of an access, or null when it is permitted.
        /// </summary>
        /// <remarks>
        /// <b>Two refusals and not one</b>, because they say different things:
        /// <c>&lt;not-authorized/&gt;</c> means "this node does not stand open
        /// to you" and names, with
        /// <c>&lt;presence-subscription-required/&gt;</c>, the way in right
        /// away. <c>&lt;forbidden/&gt;</c> for somebody excluded
        /// (section 6.1.3.8) says "not you" - and there is no way they could go
        /// themselves. Answering both alike would mean sending somebody
        /// excluded on a presence request that will change nothing.
        /// </remarks>
        /// <param name="forSubscribing">
        /// Is the question about <i>applying for</i> a subscription and not
        /// about the access?
        ///
        /// <b>With <c>authorize</c> those are two questions</b>, and with all
        /// other models the same one: whoever may not come in may not subscribe
        /// there either. Here everybody may ask - the asking is the procedure.
        /// Whoever threw that together would make the approval procedure
        /// unreachable: to be allowed, one would have to be allowed already.
        ///
        /// The exclusion holds all the same. It is not a question of the model.
        /// </param>
        private String? PepAccessErrorIq(String?      id,
                                         XMPPAccount  account,
                                         String       node,
                                         String       requesterBareJid,
                                         Boolean      forSubscribing = false)

            => account.PepAffiliationOf(node, requesterBareJid) == PubSubAffiliation.Outcast
                   ? StanzaErrorIq(id, "forbidden", "auth")
                   : (forSubscribing &&
                      account.PepNodeConfiguration(node)?.AccessModel == PubSubAccessModel.Authorize) ||
                     MayAccessPepNode(account, node, requesterBareJid)
                         ? null
                         : NotAuthorizedForPepNodeIq(id);

        /// <summary>
        /// The refusals that are the same for unsubscribing and configuring
        /// (XEP-0060, sections 6.2.3 and 6.3.3).
        /// </summary>
        /// <remarks>
        /// <c>SubIdRequired</c> deliberately does not stand in it: for that the
        /// XEP demands different errors at the two places, and a common helper
        /// deciding on one of them would answer one of the two places silently
        /// wrongly.
        /// </remarks>
        private String SubscriptionErrorIq(String? id, PepSubscriptionResult result)

            => result switch {

                   PepSubscriptionResult.WrongSubId
                       => StanzaErrorIq(id, "not-acceptable", "modify",
                                        applicationError: $"<invalid-subid xmlns='{PubSubErrorNamespace}'/>"),

                   _   => StanzaErrorIq(id, "unexpected-request", "cancel",
                                        applicationError: $"<not-subscribed xmlns='{PubSubErrorNamespace}'/>")

               };

        /// <summary>
        /// Sends a PEP notification to everyone who may see the state of this
        /// account.
        /// </summary>
        /// <remarks>
        /// <b>One's own further resources explicitly belong to that.</b> With
        /// OMEMO more than convenience hangs on it: section 5.2 demands of a
        /// client that it <i>enter itself again</i> when it has disappeared
        /// from its own device list. If it learns nothing of the change, it
        /// cannot do that - and is from then on unreachable for everyone
        /// without anything looking like an error.
        ///
        /// <b>In addition the explicit subscribers (XEP-0060, section 6.1).</b>
        /// Without them "subscribing" would mean nothing other than "standing
        /// in the roster", and the promise from
        /// <see cref="HandlePepAsync"/> would be one without cover.
        ///
        /// <b>Explicit beats incidental.</b> Whoever has subscribed to the node
        /// gets the report <i>per subscription</i> and not additionally through
        /// the presence - otherwise the number of deliveries would hang on
        /// whether somebody happens to stand in the roster too. Whoever has no
        /// subscription gets it once through the presence, as before.
        ///
        /// The identifier stands only where there is one: in the SHIM header of
        /// the subscribed delivery (section 12.20). An invented one would be
        /// worse than none - the recipient could afterwards want to unsubscribe
        /// from what was never subscribed to.
        /// </remarks>
        /// <param name="owner">
        /// The account the node belongs to - not necessarily that of the
        /// sender: a <c>publisher</c> writes into a foreign node, and the
        /// report nevertheless comes from that node's owner. Anything else
        /// would be a false statement about the origin, and the spoofing
        /// protection of the recipient would be right to discard it.
        /// </param>
        /// <param name="sender">
        /// The session that published - it does not get its own report.
        /// </param>
        /// <param name="content">
        /// What stands in <c>&lt;items/&gt;</c>: an <c>&lt;item/&gt;</c> with
        /// its payload or a <c>&lt;retract/&gt;</c> with the identifier of the
        /// item retracted.
        ///
        /// <b>Both are a delivery and therefore go through here</b> - per
        /// subscription, with an identifier, and dormant ones passed over. A
        /// retraction delivered to a dormant subscription after all would
        /// undermine the setting, and one without an identifier could not be
        /// assigned to any delivery where there are several subscriptions. That
        /// distinguishes them from deleting and purging: those concern the node
        /// and therefore go out once per subscriber (see
        /// <see cref="NotifyPepNodeAsync"/>).
        /// </param>
        private async Task NotifyPepAsync(XMPPAccount   owner,
                                          XMPPSession   sender,
                                          String        node,
                                          String        content)
        {

            if (!RouteStanzas || sender.FullJid is null)
                return;

            String Event(String? subId)
                => $"<message from='{owner.BareJid}' type='headline'>" +
                   "<event xmlns='http://jabber.org/protocol/pubsub#event'>" +
                   $"<items node='{XmlEscaping.Escape(node)}'>" +
                   content +
                   "</items></event>" +
                   (subId is not null
                        ? "<headers xmlns='http://jabber.org/protocol/shim'>" +
                          $"<header name='SubID'>{XmlEscaping.Escape(subId)}</header>" +
                          "</headers>"
                        : "") +
                   "</message>";

            var subscriptions = owner.PepSubscriptions(node);

            // The dormant ones stand in here too, and that is the point:
            // whoever has said that they want nothing shall not get it through
            // the presence either. Otherwise a second route would undermine an
            // explicit setting.
            var explicitly = new HashSet<String>(subscriptions.Select(a => a.Jid),
                                                 StringComparer.OrdinalIgnoreCase);

            // <b>The incidental delivery asks the access model too.</b> It did
            // not until D93: on a node with `whitelist` every publication went
            // to all presence recipients all the same - the model barred the
            // retrieval and let through the report in which the item stands
            // complete. With `authorize` that would have turned the approval
            // into a mere formality.
            foreach (var target in PresenceTargetsOf(owner, sender))
                if (!explicitly.Contains(target.BareJid ?? "") &&
                    MayAccessPepNode(owner, node, target.BareJid ?? ""))
                {
                    await target.SendAsync(StampTo(Event(null), target.FullJid!));
                }

            // An applied-for subscription gets nothing: it is the question and
            // not the promise.
            foreach (var subscription in subscriptions.Where(a => a.Options.Deliver &&
                                                                  a.State == PubSubSubscriptionState.Subscribed))
                foreach (var target in SessionsOf(subscription.Jid))
                    if (target != sender && target.FullJid is not null)
                        await target.SendAsync(StampTo(Event(subscription.SubId), target.FullJid));

        }

        /// <summary>
        /// Reports to everyone who would have got something from a node what
        /// has happened to it (XEP-0060, sections 8.4.2 and 8.5.2).
        /// </summary>
        /// <param name="content">
        /// The content of the report - <c>&lt;delete/&gt;</c> or
        /// <c>&lt;purge/&gt;</c> together with the node name.
        /// </param>
        /// <param name="subscribers">
        /// The explicit subscribers. When deleting they are already gone at
        /// this point and therefore have to be handed in - a report to those
        /// one still finds afterwards would reach nobody.
        /// </param>
        /// <remarks>
        /// <b>Everyone once, without an identifier.</b> Unlike a publication,
        /// this report belongs to no delivery: it is about the node. Whoever
        /// holds two subscriptions gets it only once all the same - naming an
        /// identifier would mean the others continued to exist.
        ///
        /// The recipients are the same as with a publication: presence
        /// recipients and explicit subscribers. Whoever would have got the
        /// items shall learn that they no longer exist.
        /// </remarks>
        private async Task NotifyPepNodeAsync(XMPPAccount          owner,
                                              XMPPSession          sender,
                                              String               content,
                                              IEnumerable<String>  subscribers)
        {

            if (!RouteStanzas || sender.FullJid is null)
                return;

            var eventXml = $"<message from='{owner.BareJid}' type='headline'>" +
                           $"<event xmlns='{PubSubManager.EventNamespace}'>{content}</event>" +
                           "</message>";

            var explicitly = new HashSet<String>(subscribers, StringComparer.OrdinalIgnoreCase);

            foreach (var target in PresenceTargetsOf(owner, sender))
                if (!explicitly.Contains(target.BareJid ?? ""))
                    await target.SendAsync(StampTo(eventXml, target.FullJid!));

            foreach (var who in explicitly)
                foreach (var target in SessionsOf(who))
                    if (target != sender && target.FullJid is not null)
                        await target.SendAsync(StampTo(eventXml, target.FullJid));

        }

        /// <summary>
        /// XEP-0060, section 8.6.1: Presents an application to the owner.
        /// </summary>
        /// <remarks>
        /// <b>A convenience and no carrier of the state.</b> The application
        /// itself stands in the subscription; this message only says that it
        /// exists. Hence a <c>headline</c>: whoever is offline just now misses
        /// the message and not the application - that one waits in the
        /// subscriber list until somebody looks.
        ///
        /// Conversely a message that was kept would be the worse information:
        /// it would describe a state from back then, and the application could
        /// long since have been decided.
        /// </remarks>
        private async Task RequestSubscriptionApprovalAsync(XMPPAccount      owner,
                                                            String           node,
                                                            PepSubscription  subscription)
        {

            if (!RouteStanzas)
                return;

            var application = new PubSubSubscribeAuthorization(node, subscription.Jid, subscription.SubId);

            var message = $"<message from='{owner.BareJid}' type='headline'>" +
                          application.ToForm().ToString(SaveOptions.DisableFormatting) +
                          "</message>";

            foreach (var target in SessionsOf(owner.BareJid))
                if (target.FullJid is not null)
                    await target.SendAsync(StampTo(message, target.FullJid));

        }

        /// <summary>
        /// XEP-0060, section 8.6.2: Takes the owner's answer to an application
        /// in.
        /// </summary>
        /// <returns>
        /// true when the message was such an answer - then it is answered and
        /// goes no further.
        /// </returns>
        /// <remarks>
        /// <b>What is not understood here is not swallowed either.</b> A form
        /// of this purpose that cannot be read goes its ordinary way on - it
        /// could be anything at all, and making a message disappear without a
        /// trace is the most expensive way of being polite.
        ///
        /// <b>And both doors lead into the same room:</b> promising and
        /// refusing do here exactly what the subscriber list from D84 does. Two
        /// ways to one decision are fine, as long as it stays one decision.
        /// </remarks>
        private async Task<Boolean> TryAnswerSubscribeAuthorizationAsync(XMPPSession session, String frame)
        {

            if (!frame.Contains(PubSubSubscribeAuthorization.FormType, StringComparison.Ordinal))
                return false;

            XElement message;

            try
            {
                message = XElement.Parse(frame);
            }
            catch (System.Xml.XmlException)
            {
                return false;
            }

            if (message.Child(DataFormNamespace, "x") is not { } form ||
                !PubSubSubscribeAuthorization.TryRead(form, out var answer))
            {
                return false;
            }

            var account = session.Account!;

            // Decided is only what hangs on one's own nodes. A form about a
            // foreign one is not an answer but a presumption - and goes on as
            // what it is: a message.
            if (!account.PepNodeExists(answer!.NodeId))
                return false;

            var who = BareOf(answer.SubscriberJid);

            if (answer.Allow)
            {

                if (account.ApprovePepSubscription(answer.NodeId, who, answer.SubId) is { } approved)
                    await NotifySubscriptionStateAsync(account, answer.NodeId, approved,
                                                       PubSubSubscriptionState.Subscribed);

            }

            else
                foreach (var ended in account.RemovePepSubscriptions(answer.NodeId, who, answer.SubId,
                                                                     PubSubSubscriptionState.Pending))
                {
                    await NotifySubscriptionStateAsync(account, answer.NodeId, ended,
                                                       PubSubSubscriptionState.None);
                }

            return true;

        }
        private async Task NotifySubscriptionStateAsync(XMPPAccount              owner,
                                                        String                   node,
                                                        PepSubscription          subscription,
                                                        PubSubSubscriptionState  state)
        {

            if (!RouteStanzas)
                return;

            var eventXml = $"<message from='{owner.BareJid}' type='headline'>" +
                           $"<event xmlns='{PubSubManager.EventNamespace}'>" +
                           $"<subscription node='{XmlEscaping.Escape(node)}'" +
                           $" jid='{XmlEscaping.Escape(subscription.Jid)}'" +
                           $" subid='{XmlEscaping.Escape(subscription.SubId)}'" +
                           $" subscription='{PubSubSubscription.NameOf(state)}'/>" +
                           "</event></message>";

            foreach (var target in SessionsOf(subscription.Jid))
                if (target.FullJid is not null)
                    await target.SendAsync(StampTo(eventXml, target.FullJid));

        }

        /// <summary>
        /// What the server answers itself at <b>its own address</b>: XEP-0199
        /// ping, XEP-0030 disco#info, and otherwise
        /// <c>&lt;service-unavailable/&gt;</c>.
        /// </summary>
        /// <returns>
        /// The answer - or <c>null</c> when there is none to give: a
        /// <c>result</c> or <c>error</c> is never answered (RFC 6120,
        /// section 8.2.3, rule 4), and the test switches can force the silence.
        /// </returns>
        /// <remarks>
        /// Built instead of sent, and that is what this is about: these answers
        /// stood in the middle of <see cref="HandleIqAsync"/> until D36 and
        /// wrote directly into a client session. With that they were
        /// unreachable for a peer — a request across the server boundary to our
        /// own address stayed unanswered although rule 3 demands an answer.
        ///
        /// <b>The answer does not hang on who asks.</b> What this server can do
        /// is the same for a local client and for a foreign server; only the
        /// way back differs, and the caller knows that. That is why this place
        /// builds the stanza and does not send it — otherwise the information
        /// would exist twice, and two pieces of information about the same
        /// thing can drift apart.
        ///
        /// What does <b>not</b> belong here is just as important: binding, the
        /// legacy session, carbons and the roster change the state of <i>one
        /// session</i> or belong to an account. They stay in
        /// <see cref="HandleIqAsync"/> and thereby unreachable for a peer — a
        /// foreign server asking about our roster gets
        /// <c>&lt;service-unavailable/&gt;</c> here like for every other
        /// unknown request.
        /// </remarks>
        private String? AnswerAboutSelf(String frame, String? id, String? type)
        {

            // Rule 4: an answer is never answered.
            if (type is not ("get" or "set"))
                return null;

            // XEP-0199 ping to the server
            if (frame.Contains("urn:xmpp:ping", StringComparison.Ordinal) && type == "get")
                return FailPings
                           ? StanzaErrorIq(id, "service-unavailable")
                           : AnswerPings
                                 ? $"<iq type='result' id='{id}' from='{Domain}'/>"
                                 : null;

            // XEP-0030 disco#info about the server
            if (frame.Contains("http://jabber.org/protocol/disco#info", StringComparison.Ordinal) && type == "get")
            {

                if (FailDiscoInfo)
                    return StanzaErrorIq(id, "item-not-found", "modify",
                                         "This information is not given here.");

                // This server announces no capabilities and carries no nodes. A
                // question about a node therefore asks about something that
                // does not exist here - and until then got the full feature
                // list, as though every made-up node existed.
                //
                // The error takes the question back with it (RFC 6120,
                // section 8.3.1); that is at the same time the mirroring
                // XEP-0030, section 3.2 demands for the 'node'.
                var node = Regex.Match(frame, @"<query[^>]*?\snode=['""]([^'""]*)['""]");

                if (node.Success)
                    return StanzaErrorIq(id, "item-not-found", "cancel",
                                         "This node does not exist here.",
                                         "<query xmlns='http://jabber.org/protocol/disco#info' " +
                                        $"node='{node.Groups[1].Value}'/>");

                return $"<iq type='result' id='{id}' from='{Domain}'>" +
                        "<query xmlns='http://jabber.org/protocol/disco#info'>" +
                        "<identity category='server' type='im' name='XMPPServer'/>" +
                        "<feature var='urn:xmpp:carbons:2'/>" +
                        "<feature var='urn:xmpp:ping'/>" +
                        "<feature var='urn:xmpp:sm:3'/>" +
                        // XEP-0160, section 4: only when the storage really
                        // exists. An announcement that always stands promises a
                        // client that its message to an absent party will lie
                        // ready - and lets it overlook the error with which the
                        // server is telling it the opposite just now.
                        (StoreOfflineMessages ? "<feature var='msgoffline'/>" : "") +
                        "</query></iq>";

            }

            // Unknown requests get an error (section 8.4), and that even when
            // nobody is listening: rule 3 knows no third possibility beside
            // result and error.
            return StanzaErrorIq(id, "service-unavailable");

        }

        /// <summary>
        /// The delivery of an IQ stanza to a local account (RFC 6121,
        /// sections 8.5.1, 8.5.2.1.3, 8.5.2.2.3 and 8.5.3.2.3).
        /// </summary>
        /// <param name="origin">
        /// The session of the sender - or <c>null</c> when the request came in
        /// across the server boundary.
        /// </param>
        /// <remarks>
        /// The difference to a message is fundamental and not one of degree: a
        /// request to a <b>bare JID</b> is not delivered but answered by the
        /// server itself - section 8.5.2.1.3 says it twice ("MUST reply on
        /// behalf of the user" and "MUST NOT deliver the IQ stanza to any of
        /// the user's available resources").
        ///
        /// The reason lies in the nature of IQ. It is a question-answer pair,
        /// held together by the <c>id</c> (RFC 6120, section 8.2.3), and every
        /// request received <b>has to</b> be answered. Distribute it to all
        /// resources and all of them answer: the asker gets three answers to
        /// one <c>id</c> and cannot decide which one holds - exactly what this
        /// server did. With a message a multiple delivery would be a nuisance
        /// at most; here it breaks the semantics.
        ///
        /// Two cases, one result: section 8.5.2.1.3 (resources there) and
        /// 8.5.2.2.3 (none there) demand literally the same thing. That is why
        /// this route does not even ask whether anybody is logged in - the
        /// answer would be the same in both cases, and a branch that
        /// distinguishes nothing asserts a difference.
        /// </remarks>
        private async Task DeliverIqLocallyAsync(XMPPSession?  origin,
                                                 String        to,
                                                 String        stanza)
        {

            // As with the message: without a sender there is no address for an
            // answer, and an answer is mandatory here. The early return is
            // never reached - both callers stamp or check the 'from' - but it
            // makes everything below it free of null.
            if (Attr(stanza, "from") is not { } sender)
                return;

            var type  = Attr(stanza, "type");
            var id    = Attr(stanza, "id");

            // An answer is never answered (RFC 6120, section 8.2.3, rule 4). It
            // belongs to exactly the resource that asked, and to nobody else;
            // if it does not find that one, it is an answer to a question
            // nobody is asking any more, and best forgotten.
            //
            // Section 8.5.3.2.3 demands an error for "an IQ stanza" without a
            // matching resource and does not distinguish the kind. Rule 4 holds
            // all the same here: whoever answers an answer with an error sends
            // it to somebody who asked nothing, under the 'id' of a question
            // they answered themselves.
            if (type is "result" or "error")
            {

                if (SessionOf(to) is { } waiting)
                    await waiting.SendAsync(stanza);

                return;

            }

            // From here on: a request, that is, get or set. Another value no
            // longer arrives here - both entrances refuse it per RFC 6120,
            // section 8.2.3, rule 2 before they deliver.
            //
            // One branch where the RFC has two sections: section 8.5.3.1 lets a
            // matching resource have it, 8.5.3.2.3 (no matching resource) and
            // 8.5.2.1.3/8.5.2.2.3 (bare JID) all three demand the same thing -
            // <service-unavailable/> from the server. Where the behaviour is
            // the same, no test can distinguish the cases, and a branch that
            // does it anyway asserts a difference that does not exist.
            //
            // The bare JID falls into the error branch by itself, because
            // SessionOf compares full JIDs exclusively (RFC 7622,
            // section 3.4) - and that is exactly what 8.5.2.1.3 demands with
            // "MUST NOT deliver the IQ stanza to any of the user's available
            // resources". This promise is kept not by a check here but by a
            // test: it logs two resources in and passes only when neither sees
            // the request.
            //
            // The error also goes to an account that does not exist here:
            // section 8.5.1 permits the silent passing over with a message, not
            // with a request. Nothing is given away by that - the answer is the
            // same as for an existing account without a reachable resource.
            //
            // And it is always <service-unavailable/>, which is the complete
            // implementation and not half of one: section 8.5.2.1.3 demands an
            // answer of the server's own, "if the semantics of the qualifying
            // namespace define a reply that the server can provide on behalf of
            // the user" - and otherwise explicitly this error. This server
            // knows no such namespace; should one be added, this is the place.
            if (SessionOf(to) is { } match && SharesPresenceWith(match, sender))
                await match.SendAsync(stanza);

            else
                await SendServiceUnavailableAsync("iq", id, to, sender);

        }

        /// <summary>
        /// May the asker see the presence of this resource (RFC 6121,
        /// section 8.5.3.1)?
        /// </summary>
        /// <remarks>
        /// The check section 8.5.3.1 puts before the delivery of a request: "if
        /// the intended recipient does not share presence with the requesting
        /// entity either by means of a presence subscription of type 'both' or
        /// 'from' or by means of directed presence, then the server SHOULD NOT
        /// deliver the IQ stanza".
        ///
        /// The reason stands in section 11 and is finer than it looks at first:
        /// <b>the answer is already a piece of information.</b> Whoever queries
        /// a full JID and gets a result knows that exactly this resource is
        /// logged in at this moment - and whoever gets
        /// <c>&lt;service-unavailable/&gt;</c> does not. Without this check the
        /// presence of a human being could be queried without ever having asked
        /// them for permission, and resource names could be tried out one after
        /// another.
        ///
        /// Two ways in, and with both the direction is easy to mix up:
        /// <list type="bullet">
        ///   <item>
        ///     The roster of the <b>recipient</b> carries the asker with
        ///     <c>from</c> or <c>both</c> - "that one may see me". A <c>to</c>
        ///     would mean the opposite and would give the information to
        ///     exactly the wrong half of the roster.
        ///   </item>
        ///   <item>
        ///     Or the resource has sent the asker directed presence
        ///     (section 4.6) - then it has shown its presence of its own
        ///     accord, and the answer betrays nothing the asker does not know
        ///     already.
        ///   </item>
        /// </list>
        ///
        /// The directed presence hangs on the <b>session</b> and not on the
        /// account: it is the promise of one resource and ends with it. A
        /// roster entry holds for all resources, a directed presence only for
        /// the one that sent it.
        /// </remarks>
        private static Boolean SharesPresenceWith(XMPPSession recipient, String requester)

            => recipient.Account?.IsPresenceSubscriber(BareOf(requester)) == true ||
               recipient.HasDirectedPresenceTo(BareOf(requester));

        /// <summary>
        /// XEP-0386: binds a resource without an <c>&lt;iq/&gt;</c>, during the
        /// SASL2 exchange.
        /// </summary>
        /// <param name="tag">
        /// The client's <c>&lt;tag/&gt;</c>, or null when it sent none.
        /// </param>
        /// <remarks>
        /// The client cannot choose its resource here, and that is the
        /// substance of the extension rather than an omission: XEP-0386 gives
        /// it no way to propose one. It may offer a tag, which the server
        /// SHOULD carry into the result as a prefix - the recommended form is
        /// <c>tag/server-generated</c>, and yes, that puts a '/' inside the
        /// resourcepart. RFC 7622 permits it: a JID is split at the *first*
        /// slash, so everything after it, slashes included, is the resource.
        ///
        /// The random tail is what makes two clients with the same tag
        /// distinguishable, so it is drawn rather than counted up: a counter
        /// would tell every client how many others of its kind are connected.
        ///
        /// The SASL2 user-agent id is deliberately not used here. XEP-0386
        /// forbids exposing it, and for a good reason - it is meant to be
        /// stable across logins, so putting it in the resource would publish a
        /// device identifier to everybody the account talks to.
        /// </remarks>
        private void BindResourceInline(XMPPSession session, String? tag)
        {

            lock (_lock)
            {

                Boolean Occupied(String candidate)
                    => _sessions.Any(s => s.IsOpen &&
                                          String.Equals(s.BareJid, session.BareJid, StringComparison.OrdinalIgnoreCase) &&
                                          String.Equals(s.Resource, candidate, StringComparison.Ordinal));

                String Generated()
                    => Convert.ToBase64String(RandomNumberGenerator.GetBytes(9)).
                               Replace('+', '-').
                               Replace('/', '_');

                var resource = tag is not null
                                   ? $"{tag}/{Generated()}"
                                   : Generated();

                while (Occupied(resource))
                    resource = tag is not null
                                   ? $"{tag}/{Generated()}"
                                   : Generated();

                session.Resource = resource;

            }

        }

        private async Task HandleBindAsync(XMPPSession session, String frame, String? id)
        {

            if (FailBind)
            {
                await session.SendAsync(StanzaErrorIq(id, "not-allowed", "cancel",
                                                      "This resource must not be bound."));
                return;
            }

            var requested  = Regex.Match(frame, @"<resource>([^<]*)</resource>").Groups[1].Value;
            var wished     = !String.IsNullOrEmpty(requested);
            var conflict   = false;

            // The client uses console-{ProcessId} as its resource. If several
            // clients run in the same process, they collide - the server then
            // hands out a differing, unique resource, like a real server.
            lock (_lock)
            {

                Boolean Occupied(String candidate)
                    => _sessions.Any(s => s.IsOpen &&
                                          String.Equals(s.BareJid, session.BareJid, StringComparison.OrdinalIgnoreCase) &&
                                          String.Equals(s.Resource, candidate, StringComparison.Ordinal));

                // RFC 6120, section 7.7.2.2: on an occupied resource the server
                // may also simply answer with <conflict/>.
                if (wished && ConflictOnUsedResource && Occupied(requested))
                    conflict = true;

                else
                {

                    var baseName  = wished ? requested : "auto";
                    var resource  = baseName;
                    var n         = 2;

                    while (Occupied(resource))
                        resource = $"{baseName}-{n++}";

                    session.Resource = resource;

                }

            }

            if (conflict)
            {
                await session.SendAsync(StanzaErrorIq(id, "conflict", "cancel",
                                                      "This resource is already bound."));
                return;
            }

            await session.SendAsync(
                $"<iq type='result' id='{id}'>" +
                "<bind xmlns='urn:ietf:params:xml:ns:xmpp-bind'>" +
                $"<jid>{session.FullJid}</jid>" +
                "</bind></iq>");

            await OnSessionBound.InvokeAllAsync(handler => handler(Timestamp.Now, this, session, CancellationToken.None), Logger);

            // Everything a real server delivers right after the binding.
            foreach (var frameToDeliver in DeliverAfterBind.ToArray())
                await session.SendAsync(frameToDeliver.Replace("{jid}", session.FullJid));

        }

        private async Task HandleRosterAsync(XMPPSession session, String frame, String? id, String? type)
        {

            var account = session.Account;

            if (account is null)
                return;

            if (type == "get")
            {

                var version = account.RosterVersion;

                // RFC 6121, section 2.6.2: if the client knows this version
                // already, an empty result comes entirely without a <query/>.
                // Its cache is correct, there is nothing to send.
                //
                // The leaving out of the <query/> is the whole statement here:
                // a <query/> without children would mean "your roster is empty"
                // and would delete everything at the client.
                if (OfferRosterVersioning &&
                    QueryAttr(frame, "ver") is String known &&
                    String.Equals(known, version, StringComparison.Ordinal))
                {
                    await session.SendAsync($"<iq type='result' id='{id}'/>");
                    return;
                }

                var items = new StringBuilder();

                foreach (var e in account.Roster)
                    items.Append(RosterItemXml(e));

                var verAttribute = OfferRosterVersioning ? $" ver='{version}'" : "";

                await session.SendAsync(
                    $"<iq type='result' id='{id}'>" +
                    $"<query xmlns='jabber:iq:roster'{verAttribute}>{items}</query></iq>");

                return;

            }

            if (type == "set")
            {

                // The body belongs with it: the groups stand in it. The pattern
                // takes both spellings - the empty element and the one with a
                // closing tag - because both occur.
                var m = Regex.Match(frame, @"<item\s+([^>]+?)(?:/>|>(.*?)</item>)", RegexOptions.Singleline);

                if (!m.Success)
                {
                    await session.SendAsync($"<iq type='result' id='{id}'/>");
                    return;
                }

                var attrs         = m.Groups[1].Value;
                var jid           = AttrIn(attrs, "jid");
                var name          = AttrIn(attrs, "name");
                var subscription  = AttrIn(attrs, "subscription");

                // RFC 6121, section 2.3.2: the groups of the set replace the
                // previous ones completely. A set without a <group/> therefore
                // takes them away - that is not an omission but the instruction
                // that the contact no longer stands in any group.
                var groups        = Regex.Matches(m.Groups[2].Value, @"<group[^>]*>([^<]*)</group>")
                                         .Select(g => XmlEscaping.Unescape(g.Groups[1].Value))
                                         .Where (g => g.Length > 0)
                                         .Distinct(StringComparer.Ordinal)
                                         .ToArray();

                if (jid is null)
                {
                    await session.SendAsync($"<iq type='result' id='{id}'/>");
                    return;
                }

                if (subscription == "remove")
                {
                    account.RemoveRosterEntry(jid);
                    await session.SendAsync($"<iq type='result' id='{id}'/>");

                    var removed = $"<item jid='{jid}' subscription='remove'/>";

                    foreach (var s in SessionsOf(account.BareJid))
                        await s.SendAsync(
                            $"<iq type='set' id='push-{Guid.NewGuid():N}' to='{s.FullJid}'>" +
                            $"<query xmlns='jabber:iq:roster'>{removed}</query></iq>");

                    return;
                }

                // RFC 6121, section 2.3.2: a roster set changes the name and
                // the groups. It does not touch the subscription state - that
                // belongs to the handshake from section 3. Taking the missing
                // attribute over as 'none' would have deleted a permission just
                // granted on a mere renaming.
                var existing = account.Roster.FirstOrDefault(
                                   e => String.Equals(e.Jid, jid, StringComparison.OrdinalIgnoreCase));

                account.SetRosterEntry(new RosterEntry(jid,
                                                       name,
                                                       existing?.Subscription ?? "none",
                                                       existing?.Ask,
                                                       existing?.Approved ?? false,
                                                       groups));

                await session.SendAsync($"<iq type='result' id='{id}'/>");

                // The push is built anew from the stored entry and not assembled
                // from the text of the client. An <item/> with a separate
                // closing tag - which RosterStanzaBuilder.SetItem produces -
                // would otherwise yield an open element in the push and thereby
                // ill-formed XML.
                await PushRosterEntryAsync(account, jid);

            }

        }
        private async Task HandleMessageAsync(XMPPSession session, String frame)
        {

            if (!RouteStanzas)
                return;

            // XEP-0060, section 8.6.2: the owner's answer to an application. It
            // is directed at the service, and with PEP the service is the
            // account itself - which is why it is caught here, before it would
            // go to one's own devices as an ordinary message.
            if (session.Account is not null &&
                await TryAnswerSubscribeAuthorizationAsync(session, frame))
            {
                return;
            }

            var to = Attr(frame, "to");

            if (to is null || session.FullJid is null)
                return;

            var stamped = StampFrom(frame, session.FullJid);

            // A foreign domain: out with it, and if that does not work, tell
            // the sender. The <sent> carbons below hold all the same - they
            // concern the account of the sender and not the target.
            if (!IsLocal(to))
            {

                if (!await RouteToAsync(to, stamped) &&
                    Attr(frame, "type") != "error")
                {
                    await SendRemoteServerNotFoundAsync(session, "message", Attr(frame, "id"), to);
                }

                await SendSentCarbonsAsync(session, stamped);

                return;

            }

            await DeliverMessageLocallyAsync(session, to, stamped);

        }

        /// <summary>
        /// The delivery of a message to a local address (RFC 6121,
        /// section 8.5).
        /// </summary>
        /// <param name="origin">
        /// The session of the sender - or <c>null</c> when the message came in
        /// across the server boundary.
        /// </param>
        /// <param name="to">The address as it stands in the stanza.</param>
        /// <param name="stanza">The stanza with the <c>from</c> set.</param>
        /// <remarks>
        /// One place for both origins, and that is the core of this step:
        /// section 8.5 speaks throughout of an "inbound stanza" and does not
        /// distinguish whether it came from a client or from another server.
        /// The recipient does not notice the difference anyway - for them it is
        /// a message to their account.
        ///
        /// Up to here only the route from the client took these rules. What
        /// came across the boundary went unexamined into the routing: without
        /// offline storage, without regard for negative priorities, without
        /// distinguishing by kind. That hit precisely the most frequent case -
        /// the acquaintance on another server is the rule and not the
        /// exception.
        ///
        /// The only difference that remains are the <c>&lt;sent&gt;</c>
        /// carbons: they belong to the other devices of the sender, and those
        /// of a foreign account are not our business. The way back of an error
        /// answer, by contrast, is <b>no</b> difference - it goes through
        /// <see cref="RouteToAsync"/> in both cases, and that one knows itself
        /// whether an address lies here or elsewhere. A branch of its own for
        /// it would be a second answer to a question that is already answered.
        /// </remarks>
        private async Task DeliverMessageLocallyAsync(XMPPSession?  origin,
                                                     String        to,
                                                     String        stanza)
        {

            // Without a sender neither of the two halves can be decided:
            // neither where the message goes nor where a refusal goes back to.
            //
            // No test holds this line, and none is needed either: it cannot be
            // removed without the compiler refusing to compile, because
            // everything below it reckons with a string and not with a maybe.
            // The early return is never reached anyway - the one caller stamps
            // the 'from' itself, the other has checked it before coming here.
            if (Attr(stanza, "from") is not { } sender)
                return;

            // RFC 6121, section 8.5: where a message goes hangs on its kind
            // *and* on the form of the address. Up to here everything went the
            // same way.
            var messageType = MessageTypeExtensions.Parse(Attr(stanza, "type"));

            if (to.Contains('/'))
            {

                // Section 8.5.3.1: if the resource matches, it is delivered -
                // and that regardless of the kind. That is how a room delivers
                // its groupchat messages, and how an error answer reaches
                // exactly the resource that caused the error.
                //
                // The priority does not stand in the way here either: whoever
                // sets it negative wants nothing more of what went merely to
                // their account - addressable directly they remain.
                if (SessionOf(to) is { } match)
                {

                    await match.SendAsync(stanza);

                    if (DeliverCarbons && origin is not null)
                        await SendSentCarbonsAsync(origin, stanza);

                    return;

                }

                // Section 8.5.3.2.1: no matching resource. For normal,
                // groupchat and headline the stanza may be discarded silently -
                // the sender meant this resource, and it does not exist.
                if (messageType != MessageType.Chat)
                    return;

                // A chat, by contrast, is treated as though it had gone to the
                // account. The exception looks quirky and hits everyday life: a
                // client answers to the full JID it last saw, and if the
                // conversation partner has changed device in the meantime, that
                // one is gone. The sender did not mean this resource but their
                // counterpart.
                //
                // The 'to' stays as it arrived in the process - not rewritten
                // to the resource that now gets it.

            }

            await DeliverToAccountAsync(origin, to, stanza, Attr(stanza, "id"), sender, messageType);

        }

        /// <summary>
        /// The delivery to an account (RFC 6121, section 8.5.2) - there lead
        /// the bare JID and, for <c>chat</c>, the non-matching resource too.
        /// </summary>
        /// <param name="sender">
        /// The checked <c>from</c> of the stanza - where a refusal goes back
        /// to.
        /// </param>
        private async Task DeliverToAccountAsync(XMPPSession?  origin,
                                                 String        to,
                                                 String        stamped,
                                                 String?       id,
                                                 String        sender,
                                                 MessageType   messageType)
        {

            // An error stanza is passed over silently. Answering it would mean
            // answering an error with an error.
            if (messageType == MessageType.Error)
                return;

            // A groupchat belongs in a room. Directed at an account it is never
            // deliverable, neither to one nor to all resources, and the sender
            // gets told.
            if (messageType == MessageType.GroupChat)
            {
                await SendServiceUnavailableAsync("message", id, to, sender);
                return;
            }

            // A resource with a negative priority gets nothing that was
            // directed merely at the account - for every kind of message.
            var recipients = SessionsOf(to).Where(r => r.PresencePriority >= 0).ToArray();

            // A headline goes to *all* non-negative resources: it is a report
            // to the human being and not to a device, and which one they are
            // looking at just now nobody knows. If none is there, it is
            // discarded silently - it is transient and not worth keeping.
            if (messageType == MessageType.Headline)
            {

                foreach (var target in recipients)
                    await target.SendAsync(stamped);

                return;

            }

            // What remain are normal and chat. If nobody is reachable,
            // section 8.5.2.2.1 demands the storage or an error - discard it
            // silently the server must not.
            //
            // "Nobody reachable" here also means: only negative priorities.
            // Section 8.5.2.1.1 says that explicitly at the end - then the
            // server shall proceed as though there were no resource at all. The
            // alternative would be to give the message to the device that has
            // just said it does not want it after all.
            if (recipients.Length == 0)
            {

                // XEP-0160, section 3: a chat that carries *only* a typing
                // state is not stored. It is a statement about now, and
                // delivered late at the next login it would simply be wrong.
                //
                // The sender gets no error for it either, although the silent
                // discarding is otherwise ruled out: whoever sends a message
                // wants to know whether it arrived; whoever sends a typing
                // state has lost nothing when it lapses.
                if (messageType != MessageType.Chat || !IsChatStateOnly(stamped))
                    await StoreOfflineOrRefuseAsync(to, stamped, id, sender);

                // Honestly noted: a mutation that drops the question about the
                // origin here survives - although it throws a
                // NullReferenceException for a message from outside. The reason
                // does not lie in this line but in the `catch` when processing
                // a frame (see above): it is meant for dropped connections and
                // swallows every programming error with them. Because the
                // storage is written beforehand and nothing follows afterwards,
                // the throw stays without a visible consequence. It stands
                // under "Later".
                if (origin is not null)
                    await SendSentCarbonsAsync(origin, stamped);

                return;

            }

            // Like a real server: deliver to the resource bound last.
            var primary = recipients[^1];

            await primary.SendAsync(stamped);

            if (!DeliverCarbons)
                return;

            // XEP-0280 <received>: the remaining resources of the recipient
            foreach (var other in recipients.Where(r => r != primary && r.CarbonsEnabled))
                await other.SendAsync(CarbonEnvelope("received", other.BareJid!, other.FullJid!, stamped));

            if (origin is not null)
                await SendSentCarbonsAsync(origin, stamped);

        }

        /// <summary>
        /// Does this message carry <b>only</b> a typing state (XEP-0085)?
        /// </summary>
        /// <remarks>
        /// <b>Here, as the only place in the server, a tree is read</b>, and
        /// deliberately so: the question is "are <i>all</i> children typing
        /// state elements", and that cannot be asked of a string. A
        /// <c>Contains</c> answers "occurs", not "occurs only" - and the
        /// difference is exactly the rule.
        ///
        /// A <c>thread</c> does not count as content: XEP-0085, section 5.3
        /// explicitly shows it beside the typing state. It is an identifier,
        /// not text.
        ///
        /// If the stanza cannot be read, the answer is <c>false</c> - then it
        /// is stored as before. What cannot be proven to be a typing state is
        /// treated like a message; the reverse error would lose one.
        /// </remarks>
        internal static Boolean IsChatStateOnly(String stanza)
        {

            try
            {

                var children = XElement.Parse(stanza).Elements().ToArray();

                // The Any answers the empty case at the same time: a message
                // without children carries no typing state.
                return children.Any(k => k.Name.NamespaceName == ChatStatesNamespace) &&
                       children.All(k => k.Name.NamespaceName == ChatStatesNamespace ||
                                         k.Name.LocalName     == "thread");

            }

            catch (System.Xml.XmlException)
            {
                return false;
            }

        }

        /// <summary>
        /// The namespace of the typing state elements (XEP-0085).
        /// </summary>
        private const String ChatStatesNamespace = "http://jabber.org/protocol/chatstates";

        /// <summary>
        /// Stores a message for an account without a reachable resource - or
        /// tells the sender that nothing will come of it (RFC 6121,
        /// section 8.5.2.2.1, XEP-0160).
        /// </summary>
        /// <remarks>
        /// The section puts two routes side by side and forbids the third.
        /// Storing and refusing are both right; discarding silently is not,
        /// because then the sender takes their message for delivered and nobody
        /// can notice the loss.
        ///
        /// An account that does not exist here stays exempt from that:
        /// section 8.5.1 permits the silent passing over for that case too, and
        /// that is how it stays. Whoever made an error out of every message to
        /// an unknown name would thereby give information about which accounts
        /// exist on this server.
        /// </remarks>
        private async Task StoreOfflineOrRefuseAsync(String   to,
                                                     String   stamped,
                                                     String?  id,
                                                     String   sender)
        {

            var account = GetAccount(BareOf(to));

            // An account that does not exist is treated like one that is there
            // and is not looking just now - with an empty storage.
            //
            // RFC 6121, section 8.5.1 leaves the choice between
            // <service-unavailable/> and silence for an unknown recipient. Free
            // it is not all the same: it has to be the same one as for an
            // existing, absent account, otherwise it answers the question "does
            // this account exist?" - and by the most convenient route there is
            // (RFC 6120, section 13.11; see D50 for the same question at the
            // login).
            //
            // A bare `return` stood here, and with it the handling fell apart
            // as soon as the storage was off or full: the existing account got
            // an error, the unknown one silence.
            //
            // What is therefore asked is not "does an account exist" but "would
            // the storage take it". For an unknown one the storage is empty, and
            // an empty one takes, as long as anything fits into it at all - with
            // MaxStoredOfflineMessages = 0 therefore nothing.
            var stored = StoreOfflineMessages &&
                         (account?.StoreOfflineMessage(stamped,
                                                       DateTimeOffset.UtcNow,
                                                       MaxStoredOfflineMessages)
                              ?? MaxStoredOfflineMessages > 0);

            if (stored)
                return;

            await SendServiceUnavailableAsync("message", id, to, sender);

        }

        /// <summary>
        /// Delivers the stored messages afterwards to a newly available
        /// resource (XEP-0160).
        /// </summary>
        /// <remarks>
        /// Only to an available resource with a non-negative priority. XEP-0160
        /// says it that way ("when the recipient next sends non-negative
        /// available presence"), and it is the same consideration section 8.5
        /// demands during operation: a device that keeps out of the traffic to
        /// the account is the wrong place for a storage that came into being
        /// precisely because nobody was looking.
        ///
        /// Both conditions are necessary, not only the second: a sign-off
        /// resets the priority to 0
        /// (<see cref="XMPPSession.RecordPresence"/>), and without the question
        /// about availability the storage would go to exactly the resource that
        /// has just signed off.
        ///
        /// Unlike the subscription requests kept, the storage is emptied in the
        /// process - see
        /// <see cref="XMPPAccount.TakeOfflineMessages"/>.
        /// </remarks>
        private async Task SendOfflineMessagesToAsync(XMPPSession session)
        {

            if (session.Account is not { } account ||
                !session.IsAvailable ||
                session.PresencePriority < 0)
            {
                return;
            }

            foreach (var message in account.TakeOfflineMessages())
                await session.SendAsync(WithDelay(message, Domain));

        }

        /// <summary>
        /// Appends to a message delivered late the moment it came in
        /// (XEP-0203).
        /// </summary>
        /// <remarks>
        /// Without the stamp a message from yesterday claims to be from now:
        /// the recipient does not see the difference and answers something that
        /// has long since settled itself. The stamp is the only way to
        /// communicate the delay at all - the stanza itself carries no time.
        ///
        /// Appended and not inserted: the <c>&lt;delay/&gt;</c> is a further
        /// child element of the message, and the order of the child elements is
        /// free.
        ///
        /// The second branch is no precautionary branch: a message without
        /// child elements (<c>&lt;message .../&gt;</c>) may be sent by a
        /// client, it is a <c>chat</c> like every other and is therefore
        /// stored. Without the expanding of the empty element the stamp would
        /// either be lost or end up behind the end of the stanza.
        /// </remarks>
        internal static String WithDelay(OfflineMessage message, String from)
        {

            var stamp = message.StoredAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'",
                                                              CultureInfo.InvariantCulture);

            var delay = $"<delay xmlns='urn:xmpp:delay' from='{from}' stamp='{stamp}'>Offline Storage</delay>";

            var stanza = message.Stanza;
            var end    = stanza.LastIndexOf("</message>", StringComparison.Ordinal);

            if (end >= 0)
                return stanza[..end] + delay + stanza[end..];

            // An empty element: <message .../> becomes <message ...>…</message>.
            var close = stanza.LastIndexOf("/>", StringComparison.Ordinal);

            return close >= 0
                       ? stanza[..close] + ">" + delay + "</message>"
                       : stanza;

        }

        /// <summary>
        /// XEP-0280 <c>&lt;sent&gt;</c>: the remaining resources of the sender
        /// learn what they have written.
        /// </summary>
        private async Task SendSentCarbonsAsync(XMPPSession sender, String stamped)
        {

            if (!DeliverCarbons || sender.BareJid is null)
                return;

            foreach (var other in SessionsOf(sender.BareJid).Where(r => r != sender && r.CarbonsEnabled))
                await other.SendAsync(CarbonEnvelope("sent", other.BareJid!, other.FullJid!, stamped));

        }

        private async Task HandlePresenceAsync(XMPPSession session, String frame)
        {

            if (!RouteStanzas || session.FullJid is null)
                return;

            var type     = Attr(frame, "type");
            var to       = Attr(frame, "to");
            var stamped  = StampFrom(frame, session.FullJid);

            // A presence probe: the question about the state of a contact
            // (RFC 6121, section 4.3).
            //
            // Only for a local account does the server answer it itself
            // (section 4.3.2). If it goes across the boundary, the server is
            // not the one being asked but the conveyor: section 4.3.1 lets the
            // server of the user send the probe to the server of the contact,
            // and there it is answered.
            //
            // This distinction was missing. The branch took hold for *every*
            // target, found no account for a foreign address and returned - so
            // a probe to a contact on another server never left this server. It
            // only showed up when a test was supposed to check the opposite
            // direction and the probe never arrived.
            if (type == "probe" && to is not null && session.BareJid is not null)
            {

                if (IsLocal(to))
                    await AnswerPresenceProbeAsync(session.BareJid, session.FullJid, to);

                else
                    await RouteToAsync(to, stamped);

                return;

            }

            // The subscription handshake (RFC 6121, section 3).
            if (to is not null &&
                type is "subscribe" or "subscribed" or "unsubscribe" or "unsubscribed")
            {
                await HandleSubscriptionAsync(session, type, BareOf(to), frame);
                return;
            }

            // Other directed presence goes exactly there - and is noted down.
            if (to is not null)
            {

                // RFC 6121, section 4.6: whoever shows a stranger their
                // presence thereby also lets them ask (section 8.5.3.1).
                // Without this note directed presence would be a one-way
                // street: the recipient would see that the resource is there
                // but would not be allowed to ask it anything - and that is
                // exactly what a conversation with a non-contact builds on.
                session.RecordDirectedPresence(BareOf(to), type is null);

                await RouteToAsync(to, stamped);
                return;

            }

            // Asked before the recording: afterwards the session is available,
            // and the difference between "was already" and "has just become"
            // would no longer be visible.
            var becameAvailable  = type is null && !session.IsAvailable;

            // RFC 6121, section 4.6.3, rule 2: if the resource signs off, the
            // recipients of directed presence get the sign-off too - and the
            // list is done with by that (section 4.6.1). One call fetches both,
            // see TakeDirectedPresenceTargets.
            var directed         = type is null
                                       ? []
                                       : session.TakeDirectedPresenceTargets();

            var initial          = session.RecordPresence(stamped, available: type is null);

            // RFC 6121, section 3.1.3, rule 4: "deliver the request when the
            // contact next has an available resource". Before the broadcast
            // switch, because the late delivery of requests that were kept has
            // nothing to do with the distribution of presence - whoever
            // switches the distribution off does not want to lose requests.
            if (becameAvailable)
                await SendStoredSubscriptionRequestsToAsync(session);

            // XEP-0160: "When the recipient next sends non-negative available
            // presence to the server, the server delivers the message to the
            // resource that has sent that presence."
            //
            // At *every* such presence and not only when becoming available -
            // unlike with the kept request above. The difference lies in the
            // storage being emptied when delivering: a second pass finds
            // nothing any more and can therefore present nothing twice. And it
            // has a case of its own that becoming available does not cover: a
            // resource that is logged in with a negative priority and raises it
            // to 0 was available already - but is only just now becoming a
            // recipient.
            await SendOfflineMessagesToAsync(session);

            if (!BroadcastPresence)
                return;

            foreach (var target in PresenceTargetsOf(session))
            {
                ForgetDirectedPresenceFrom(target, stamped);
                await target.SendAsync(stamped);
            }

            // Contacts on foreign domains get the same presence - an
            // unreachable peer stays without consequence here, presence is not
            // answered with errors.
            foreach (var remote in RemotePresenceTargetsOf(session))
                await RouteToAsync(remote, StampTo(stamped, remote));

            await SendUnavailableToDirectedTargetsAsync(session, directed, stamped);

            // RFC 6121, section 4.3.1: after the first presence the server
            // queries the state of the client's contacts for it. Because all
            // accounts lie on the same instance here, we deliver what we know
            // right away - the result of a probe would be the same.
            if (initial && type is null)
                await SendKnownPresencesToAsync(session);

        }

        /// <summary>
        /// The subscription handshake per RFC 6121, section 3.
        /// </summary>
        /// <remarks>
        /// A real server only ever sees one half of it: the sections separate
        /// the outbound processing at the sender from the inbound one at the
        /// recipient, because the S2S connection lies between them. Here both
        /// accounts lie in the same instance, so the halves coincide - which
        /// changes the rosters of both sides in one step.
        ///
        /// Both roster entries have to match each other in the process:
        /// <c>from</c> at the one means <c>to</c> at the other. Every direction
        /// therefore changes only its own half.
        /// </remarks>
        /// <param name="sender">The session sending the handshake step.</param>
        /// <param name="type">subscribe, subscribed, unsubscribe or unsubscribed.</param>
        /// <param name="peerBareJid">The bare JID of the other side.</param>
        /// <param name="frame">The stanza as the client sent it.</param>
        private async Task HandleSubscriptionAsync(XMPPSession  sender,
                                                   String       type,
                                                   String       peerBareJid,
                                                   String       frame)
        {

            var senderAccount  = sender.Account;
            var peerAccount    = GetAccount(peerBareJid);

            if (senderAccount is null)
                return;

            // Per RFC 6121, section 3.1.1 the handshake always carries the bare
            // JID - the request concerns the account, not a resource. That is
            // why both addresses are replaced and not merely added to.
            //
            // Stamped and not built anew: a request may carry extended content,
            // and the <status/> in it is the reason with which a human being
            // decides about the consent. A newly built <presence .../> throws it
            // away - and section 3.1.3 demands keeping the *complete* stanza.
            var stanza = StampTo(StampFrom(frame, senderAccount.BareJid), peerBareJid);

            switch (type)
            {

                // Section 3.1.2: the entry comes into being with
                // subscription='none' - nothing is permitted yet -, and
                // ask='subscribe' holds fast that the request is pending.
                case "subscribe":
                    UpdateRosterEntry(senderAccount, peerBareJid, subscription: null, ask: AskChange.Set);
                    await PushRosterEntryAsync(senderAccount, peerBareJid);
                    break;

                // Sections 3.1.5 and 3.1.6: the consenting one permits the other
                // side to see them; at the other side the request is thereby
                // settled and the opposite direction set.
                //
                // Section 3.4.2 distinguishes four cases here, and the
                // difference hangs solely on whether a request is pending.
                case "subscribed":
                {

                    var previous = senderAccount.SubscriptionOf(peerBareJid) ?? "none";

                    // Case 1: the contact may see us anyway already - pass over
                    // silently.
                    if (previous is "from" or "both")
                        return;

                    // Cases 3 and 4: no pending request. Then this is a
                    // pre-approval, and the stanza expressly does *not* go out -
                    // the contact asked nothing and shall get no answer.
                    //
                    // Asking and settling in one step: the kept request *is* the
                    // pending request, and whoever queries it first and deletes
                    // it afterwards can let the two run apart.
                    if (!senderAccount.ForgetSubscriptionRequest(peerBareJid))
                    {

                        if (!OfferSubscriptionPreApproval)
                            return;

                        UpdateRosterEntry(senderAccount, peerBareJid, approved: true);
                        await PushRosterEntryAsync(senderAccount, peerBareJid);

                        return;

                    }

                    // Case 2: a request was there - the ordinary consent.
                    UpdateRosterEntry(senderAccount, peerBareJid,
                                      GrantFrom(previous));
                    await PushRosterEntryAsync(senderAccount, peerBareJid);

                    if (peerAccount is not null)
                    {
                        UpdateRosterEntry(peerAccount, senderAccount.BareJid,
                                          GrantTo(peerAccount.SubscriptionOf(senderAccount.BareJid)),
                                          ask: AskChange.Clear);
                        await PushRosterEntryAsync(peerAccount, senderAccount.BareJid);
                    }

                    break;

                }

                // Sections 3.2.2 and 3.2.3: the revocation, mirror-imaged.
                // Section 3.4.2, note: an 'unsubscribed' also takes a
                // pre-approval back.
                case "unsubscribed":
                    senderAccount.ForgetSubscriptionRequest(peerBareJid);
                    UpdateRosterEntry(senderAccount, peerBareJid,
                                      RevokeFrom(senderAccount.SubscriptionOf(peerBareJid)),
                                      approved: false);
                    await PushRosterEntryAsync(senderAccount, peerBareJid);

                    if (peerAccount is not null)
                    {
                        UpdateRosterEntry(peerAccount, senderAccount.BareJid,
                                          RevokeTo(peerAccount.SubscriptionOf(senderAccount.BareJid)),
                                          ask: AskChange.Clear);
                        await PushRosterEntryAsync(peerAccount, senderAccount.BareJid);
                    }
                    break;

                // Sections 3.3.2 and 3.3.3: the sender cancels their own
                // subscription - here their 'to' half therefore changes.
                case "unsubscribe":
                    UpdateRosterEntry(senderAccount, peerBareJid,
                                      RevokeTo(senderAccount.SubscriptionOf(peerBareJid)),
                                      ask: AskChange.Clear);
                    await PushRosterEntryAsync(senderAccount, peerBareJid);

                    if (peerAccount is not null)
                    {
                        UpdateRosterEntry(peerAccount, senderAccount.BareJid,
                                          RevokeFrom(peerAccount.SubscriptionOf(senderAccount.BareJid)));
                        await PushRosterEntryAsync(peerAccount, senderAccount.BareJid);
                    }
                    break;

            }

            // The stanza itself goes to the other side: the contact shall see
            // the request, the applicant the answer.
            //
            // A request to a local account takes the same route in the process
            // as one from outside: there it is decided whether it is delivered
            // or answered by the server itself. Across the boundary the server
            // of the other side makes this decision.
            if (type == "subscribe" && IsLocal(peerBareJid))
                await DeliverSubscribeAsync(senderAccount.BareJid, peerBareJid, stanza);
            else
                await RouteToAsync(peerBareJid, stanza);

            // Section 3.1.5: "The contact's server MUST then also send current
            // presence to the user from each of the contact's available
            // resources." Without that the applicant waits until the contact
            // sends something of their own accord the next time.
            if (type == "subscribed")
                await SendOwnPresenceToAsync(sender, peerBareJid);

            // Section 3.2.2: "the contact's server MUST send a presence stanza
            // of type 'unavailable' from all of the contact's online
            // resources". Otherwise the other side would keep the last known
            // state although it is no longer allowed to see it.
            if (type == "unsubscribed")
                await SendOwnUnavailableToAsync(senderAccount, peerBareJid);

            // Mirror-imaged to the revocation: whoever cancels themselves shall
            // likewise no longer carry the contact as present.
            if (type == "unsubscribe" && peerAccount is not null)
                await SendOwnUnavailableToAsync(peerAccount, senderAccount.BareJid);

        }

        /// <summary>
        /// What is to happen to the ask note of a roster entry.
        /// </summary>
        /// <remarks>
        /// Three cases, and null is good for at most two of them: note a
        /// request, delete an answered one, or do not touch the note at all.
        /// </remarks>
        private enum AskChange
        {
            Keep,
            Set,
            Clear
        }

        /// <summary>
        /// Sets the subscription and/or the ask of a roster entry and creates it
        /// if it does not exist yet. A subscription of null leaves the previous
        /// value standing.
        /// </summary>
        private static void UpdateRosterEntry(XMPPAccount  account,
                                              String       contactBareJid,
                                              String?      subscription  = null,
                                              AskChange    ask           = AskChange.Keep,
                                              Boolean?     approved      = null)
        {

            // The existing entry is changed and not rebuilt.
            //
            // <b>Rebuilt meant: taken over field by field</b> - and what came
            // along later fell out. That is exactly how the groups vanished in
            // D91: the client set them, right afterwards its presence request
            // went through this place, and that one wrote an entry without them
            // back. A `with` knows the new fields without anybody having to
            // think of them.
            var previous = RosterEntryOf(account, contactBareJid)
                             ?? new RosterEntry(contactBareJid, Subscription: "none");

            account.SetRosterEntry(previous with {

                                       Subscription  = subscription ?? previous.Subscription,

                                       Ask           = ask switch {
                                                           AskChange.Set    => "subscribe",
                                                           AskChange.Clear  => null,
                                                           _                => previous.Ask
                                                       },

                                       Approved      = approved ?? previous.Approved

                                   });

        }

        /// <summary>
        /// The roster entry of a contact, or null.
        /// </summary>
        private static RosterEntry? RosterEntryOf(XMPPAccount account, String contactBareJid)
            => account.Roster.FirstOrDefault(
                   e => String.Equals(e.Jid, contactBareJid, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Sends a roster push for exactly one entry to all resources of the
        /// account (RFC 6121, section 2.1.6).
        /// </summary>
        private async Task PushRosterEntryAsync(XMPPAccount account, String contactBareJid)
        {

            var entry = account.Roster.FirstOrDefault(
                            e => String.Equals(e.Jid, contactBareJid, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
                return;

            var item = RosterItemXml(entry);

            // RFC 6121, section 2.6.3: the push too carries the new version.
            // Without it the client would have to fetch the whole roster anew
            // after every change in order to know again where it stands - and
            // that is exactly what the versioning is meant to spare it.
            var verAttribute = OfferRosterVersioning ? $" ver='{account.RosterVersion}'" : "";

            foreach (var s in SessionsOf(account.BareJid))
                await s.SendAsync($"<iq type='set' id='push-{Guid.NewGuid():N}' to='{s.FullJid}'>" +
                                  $"<query xmlns='jabber:iq:roster'{verAttribute}>{item}</query></iq>");

        }

        /// <summary>
        /// Sends the current presence of a session to a single JID.
        /// </summary>
        private async Task SendOwnPresenceToAsync(XMPPSession sender, String peerBareJid)
        {

            if (sender.LastPresence is null)
                return;

            await RouteToAsync(peerBareJid, sender.LastPresence);

        }

        /// <summary>
        /// Signs all resources of an account off at a single JID.
        /// </summary>
        private async Task SendOwnUnavailableToAsync(XMPPAccount account, String peerBareJid)
        {

            foreach (var s in SessionsOf(account.BareJid).Where(s => s.IsAvailable && s.FullJid is not null))
                await RouteToAsync(peerBareJid, $"<presence type='unavailable' from='{s.FullJid}'/>");

        }

        /// <summary>
        /// Who gets the undirected presence of this session?
        /// </summary>
        /// <remarks>
        /// RFC 6121, section 4.2.2: the contacts with <c>from</c> or
        /// <c>both</c>. Beside them, per section 4.4.2, the further resources of
        /// one's own account, for which no roster entry is needed.
        /// </remarks>
        /// <summary>
        /// The contacts on foreign domains that may see the presence of this
        /// session - as bare JIDs, because nobody here knows their resources.
        /// </summary>
        /// <remarks>
        /// RFC 6121, section 4.2.2 makes no difference between near and far:
        /// whoever has <c>from</c> or <c>both</c> gets the presence. Separate
        /// from <see cref="PresenceTargetsOf"/>, because the one delivers
        /// sessions and the other addresses - a common list would have to bear
        /// both and would have to be split apart again at every place it is
        /// used.
        /// </remarks>
        private IEnumerable<String> RemotePresenceTargetsOf(XMPPSession session)
        {

            var account = session.Account;

            if (account is null)
                yield break;

            foreach (var entry in account.Roster)
            {

                if (!IsLocal(entry.Jid) &&
                    entry.Subscription is "from" or "both")
                {
                    yield return entry.Jid;
                }

            }

        }

        private IEnumerable<XMPPSession> PresenceTargetsOf(XMPPSession session)
            => session.Account is null
                   ? []
                   : PresenceTargetsOf(session.Account, session);

        /// <summary>
        /// Who gets what goes out from this account?
        /// </summary>
        /// <param name="except">
        /// A session that does not get it - that of the sender. It does not have
        /// to belong to this account: a <c>publisher</c> writes into a foreign
        /// node and does not need their own notification.
        /// </param>
        private IEnumerable<XMPPSession> PresenceTargetsOf(XMPPAccount account, XMPPSession? except)
        {

            foreach (var other in Sessions.Where(s => s != except && s.FullJid is not null))
            {

                if (String.Equals(other.BareJid, account.BareJid, StringComparison.OrdinalIgnoreCase) ||
                    account.IsPresenceSubscriber(other.BareJid!))
                {
                    yield return other;
                }

            }

        }

        /// <summary>
        /// Delivers the known state of their contacts afterwards to a freshly
        /// signed-on session.
        /// </summary>
        private async Task SendKnownPresencesToAsync(XMPPSession session)
        {

            var account = session.Account;

            if (account is null)
                return;

            foreach (var other in Sessions.Where(s => s != session &&
                                                      s.FullJid     is not null &&
                                                      s.LastPresence is not null))
            {

                // Whether a contact gives their state away is decided by their
                // roster, not by ours - which is why the other side is asked
                // here.
                var ownResource = String.Equals(other.BareJid, account.BareJid,
                                                   StringComparison.OrdinalIgnoreCase);

                if (ownResource ||
                    other.Account?.IsPresenceSubscriber(account.BareJid) == true)
                {
                    await session.SendAsync(other.LastPresence!);
                }

            }

        }

        /// <summary>
        /// Answers a presence probe (RFC 6121, section 4.3.2).
        /// </summary>
        /// <param name="proberBareJid">Who asks - without a resource.</param>
        /// <param name="replyTo">
        /// Where the answer goes: the full JID of a local session, otherwise the
        /// bare JID of the asker on the foreign domain.
        /// </param>
        /// <param name="to">Whose state is being asked about.</param>
        /// <remarks>
        /// If the permission is missing, the probe stays unanswered. Section
        /// 8.5.1 leaves the server the choice between
        /// <c>&lt;unsubscribed/&gt;</c> and silence for an unknown account -
        /// silence does not even betray whether the account exists at all, and
        /// that is why it stays with it.
        ///
        /// What is asked is the roster of the <b>asked-about one</b> for
        /// <c>from</c> or <c>both</c>: "that one may see me". The same half as
        /// with the IQ check from section 8.5.3.1, and the same danger of
        /// mixing it up.
        ///
        /// One route for both origins, through <see cref="RouteToAsync"/>. A
        /// branch of its own for the local asker would be a second answer to the
        /// question "here or elsewhere" that the switch answers already.
        /// </remarks>
        private async Task AnswerPresenceProbeAsync(String proberBareJid,
                                                    String replyTo,
                                                    String to)
        {

            var account = GetAccount(BareOf(to));

            if (account is null ||
                !account.IsPresenceSubscriber(proberBareJid))
            {
                return;
            }

            foreach (var s in SessionsOf(account.BareJid).Where(s => s.LastPresence is not null))
                await RouteToAsync(replyTo, StampTo(s.LastPresence!, replyTo));

        }

        /// <summary>
        /// The only switch between "here" and "elsewhere" (RFC 6120,
        /// section 10.4).
        /// </summary>
        /// <returns>
        /// false only when the stanza went to a foreign domain and did not
        /// arrive there. An unknown account on one's own domain counts as
        /// handled - what the server should do with it is another question
        /// (RFC 6121, section 8.1) and does not hang on the routing.
        /// </returns>
        private async Task<Boolean> RouteToAsync(String to, String stanza)
        {

            if (!IsLocal(to))
            {

                // The address has to go out with it. Within one server it knows
                // itself to whom it distributes; across the boundary the 'to'
                // is all the other side has - a stanza without one is discarded
                // there. Central here and not at the callers, because otherwise
                // every new caller would have to think of it.
                //
                // Honestly noted: no test holds this line fast. The only caller
                // today that arrives without a 'to' is the presence delivered
                // afterwards from section 3.1.5, and there the behaviour of the
                // client covers the difference. It stays as a provision for the
                // next caller.
                // And the namespace has to change along with it. What came in
                // from a client stands in jabber:client; out it goes on a
                // stream that speaks jabber:server (RFC 6120,
                // section 4.8.1). Prosody answers a jabber:client IQ on the S2S
                // stream with an error - between two instances of this server
                // it would never show, because both look only at the local name.
                return ServerLinks is not null &&
                       await ServerLinks.DeliverAsync(DomainOf(to),
                                                      StanzaNamespace.Apply(StampTo(stanza, to),
                                                                            StanzaNamespace.Server),
                                                      _cts.Token);

            }

            var targets = to.Contains('/')
                              ? (SessionOf(to) is { } one ? [one] : Array.Empty<XMPPSession>())
                              : SessionsOf(to).ToArray();

            foreach (var t in targets)
            {
                ForgetDirectedPresenceFrom(t, stanza);
                await t.SendAsync(stanza);
            }

            return true;

        }

        /// <summary>
        /// An incoming sign-off takes its sender out of the list of directed
        /// presence of the recipient (RFC 6121, section 4.6.1).
        /// </summary>
        /// <remarks>
        /// The SHOULD part of the section: "The server MUST remove from the
        /// directed presence list ... any entity to which the user sends
        /// directed unavailable presence and SHOULD remove any entity that sends
        /// unavailable presence to the user."
        ///
        /// The two halves look similar and mean the opposite. The MUST concerns
        /// the <b>own</b> revocation and stands in
        /// <see cref="XMPPSession.RecordDirectedPresence"/>; this SHOULD concerns
        /// the opposite direction: the other one goes, and with that the
        /// temporary relationship is likewise at an end. Since D17 it hangs on
        /// that who may ask this resource anything (section 8.5.3.1) - without
        /// this route a returning one would keep their right to ask although
        /// nobody has shown them anything any more.
        ///
        /// What is looked at is the <b>receipt</b> and not the sending, for that
        /// is exactly how the rule is worded: "any entity that sends unavailable
        /// presence <i>to the user</i>". That is why the call stands here, in the
        /// one switch through which every stanza to a local address runs - and
        /// not at the senders, of which there are several.
        /// </remarks>
        private static void ForgetDirectedPresenceFrom(XMPPSession recipient, String stanza)
        {

            if (!StanzaElement.Is(stanza, "presence") ||
                Attr(stanza, "type") != "unavailable")
            {
                return;
            }

            if (Attr(stanza, "from") is { } from)
                recipient.RecordDirectedPresence(BareOf(from), available: false);

        }

        /// <summary>
        /// Takes a stanza from another server - the counterpart to
        /// <see cref="IServerLinks"/>.
        /// </summary>
        /// <param name="peerDomain">
        /// The domain the other side is allowed to speak for. A real transport
        /// sets that after dialback (XEP-0220) or SASL-EXTERNAL; here it is the
        /// promise of the link.
        /// </param>
        /// <param name="stanza">The incoming stanza.</param>
        /// <returns>false when it was refused.</returns>
        /// <remarks>
        /// The sender check is the core and not an accessory: another side may
        /// speak exclusively for its own domain. Without this check every server
        /// one ever speaks with could smuggle in messages in the name of any
        /// other one - the whole effort of dialback would then be for nothing.
        ///
        /// RFC 6120, section 8.1.1.1 lets a server end the stream with
        /// <c>&lt;invalid-from/&gt;</c> on a wrong <c>from</c>. Whether it comes
        /// to that is not decided by this method but by the stream over which the
        /// stanza came - here there is only the verdict. That is why
        /// <see cref="AcceptFromRemoteAsync"/> delivers a
        /// <see cref="RemoteStanzaResult"/>; this overload passes it on as a
        /// yes/no, for callers to whom the reason is all the same.
        /// </remarks>
        public async Task<Boolean> ReceiveFromRemoteAsync(String peerDomain, String stanza)

            => await AcceptFromRemoteAsync(peerDomain, stanza) == RemoteStanzaResult.Accepted;

        /// <summary>
        /// Like <see cref="ReceiveFromRemoteAsync"/>, but with the reason of a
        /// refusal.
        /// </summary>
        public async Task<RemoteStanzaResult> AcceptFromRemoteAsync(String peerDomain, String stanza)
        {

            var from  = Attr(stanza, "from");
            var to    = Attr(stanza, "to");

            if (from is null || to is null)
            {
                await OnRemoteStanzaRejected.InvokeAllAsync(handler => handler(Timestamp.Now, this, peerDomain, "from or to is missing", CancellationToken.None), Logger);
                return RemoteStanzaResult.MissingAddress;
            }

            // RFC 6120, section 8.3.3.8, here for the route across the boundary.
            // The check of the sender stands before the question of
            // responsibility: a DomainOf on a string that is no JID compares
            // fragments and then calls the result "foreign domain".
            if (!JID.TryParse(from, out _))
            {
                await OnRemoteStanzaRejected.InvokeAllAsync(handler => handler(Timestamp.Now, this, peerDomain, $"'{from}' is no JID", CancellationToken.None), Logger);
                return RemoteStanzaResult.MalformedSender;
            }

            if (!String.Equals(DomainOf(from), peerDomain, StringComparison.OrdinalIgnoreCase))
            {
                await OnRemoteStanzaRejected.InvokeAllAsync(handler => handler(
                    Timestamp.Now, this,
                    peerDomain,
                    $"'{from}' does not belong to '{peerDomain}'",
                    CancellationToken.None), Logger);
                return RemoteStanzaResult.ForeignSender;
            }

            // And the recipient before the question whether they belong here:
            // IsLocal looks only at the domain, and 'b ob@this.server' belongs
            // here without being an address. Up to here such a stanza ran into
            // the delivery and looked there like one to an absent party.
            if (!JID.TryParse(to, out _))
            {

                await OnRemoteStanzaRejected.InvokeAllAsync(handler => handler(Timestamp.Now, this, peerDomain, $"'{to}' is no JID", CancellationToken.None), Logger);

                // Section 8.3.1: an error is not followed by an error. Across the
                // boundary that weighs more heavily than in one's own house - two
                // servers answering each other do not stop of their own accord.
                if (Attr(stanza, "type") != "error")
                    await RouteToAsync(from,
                                       JidMalformedError(StanzaElement.NameOf(stanza) ?? "message",
                                                         Attr(stanza, "id"),
                                                         from));

                return RemoteStanzaResult.MalformedRecipient;

            }

            if (!IsLocal(to))
            {
                // Forwarding for third parties would be an open relay.
                await OnRemoteStanzaRejected.InvokeAllAsync(handler => handler(Timestamp.Now, this, peerDomain, $"'{to}' does not lie on '{Domain}'", CancellationToken.None), Logger);
                return RemoteStanzaResult.ForeignRecipient;
            }

            if (!RouteStanzas)
                return RemoteStanzaResult.RoutingDisabled;

            // RFC 6120, section 8.2.3, rule 2, here in the role of the
            // recipient. A client of this server never gets that far - its own
            // server already refuses it as a router -, a foreign implementation
            // that does not know the rule very much so.
            //
            // Before all delivery branches: the route for requests distinguishes
            // only answer and request and would take everything unknown for a
            // request.
            if (StanzaElement.Is(stanza, "iq") &&
                !IqTypes.IsKnown(Attr(stanza, "type")))
            {

                await RouteToAsync(from, BadRequestIq(Attr(stanza, "id")));

                return RemoteStanzaResult.Accepted;

            }

            // RFC 6121, section 3: a subscription presence is not a message that
            // is merely passed on - it changes the roster of the local side.
            // Without this step the request would indeed arrive at the client,
            // but the server would forget it, and the answer would find no entry
            // in front of it that it could change.
            var kind = SubscriptionTypeOf(stanza);

            if (kind is not null)
            {
                await ApplyRemoteSubscriptionAsync(BareOf(from), BareOf(to), kind, stanza);
                return RemoteStanzaResult.Accepted;
            }

            // RFC 6121, section 8.5 holds for every incoming stanza and does not
            // ask where it came from. A message therefore takes the same route as
            // that of a local client - with offline storage, priorities and
            // distinction by kind. Up to here it went unexamined into the
            // routing, and that hit precisely the most frequent case: the
            // acquaintance on another server is the rule.
            if (StanzaElement.Is(stanza, "message"))
            {
                await DeliverMessageLocallyAsync(null, to, stanza);
                return RemoteStanzaResult.Accepted;
            }

            // And the same for the request to an account: it must not be
            // distributed to all resources but belongs answered.
            //
            // Only with a local part. Section 8.5.2 deals with an address "of
            // the form <localpart@domainpart>"; a request to the domain itself
            // is directed at the server and not at a user, and for that the
            // section does not hold.
            if (StanzaElement.Is(stanza, "iq") &&
                to.Contains('@'))
            {
                await DeliverIqLocallyAsync(null, to, stanza);
                return RemoteStanzaResult.Accepted;
            }

            // And a request to the server address itself the server answers for
            // itself - the same information a local client gets.
            //
            // Until D36 it went into the routing, found no session there for the
            // domain and vanished. The other side waited for an answer that rule
            // 3 demands and that never came - it learned nothing of it. The way
            // back is the only difference to the local client; the information
            // itself does not hang on who asks.
            if (StanzaElement.Is(stanza, "iq"))
            {

                if (AnswerAboutSelf(stanza, Attr(stanza, "id"), Attr(stanza, "type")) is { } answer)
                    await RouteToAsync(from, answer);

                return RemoteStanzaResult.Accepted;

            }

            // A presence probe the server answers itself and does not deliver
            // it (RFC 6121, sections 8.5.2.1.2, 8.5.2.2.2, 8.5.3.1 and
            // 8.5.3.2.2 - all four refer to section 4.3 for it).
            //
            // Up to here it went into the routing and ended up at the client.
            // That was wrong in both directions: the client got to see a stanza
            // that is not meant for it and to which it can answer nothing, and
            // the other side never got an answer - it asks about the state of a
            // contact and receives silence although the server has the
            // information. Exactly the same asymmetry as with message and IQ:
            // for a local client the probe has always been answered.
            if (kind is null &&
                StanzaElement.Is(stanza, "presence") &&
                Attr(stanza, "type") == "probe")
            {

                await AnswerPresenceProbeAsync(BareOf(from), BareOf(from), to);

                return RemoteStanzaResult.Accepted;

            }

            // Available and unavailable presence takes the straight route, and
            // here that is also the right one: to a bare JID it goes to all
            // resources (section 8.5.2.1.2), to a full JID to the matching one
            // (8.5.3.1), and without an account or without a matching resource
            // silently into the void (8.5.1 and 8.5.3.2.2). That is exactly what
            // RouteToAsync does.
            //
            // A request to the domain itself takes it likewise - what the server
            // would have to answer for itself it does not answer yet (see
            // "Later").
            await RouteToAsync(to, stanza);

            return RemoteStanzaResult.Accepted;

        }

        /// <summary>
        /// Delivers a request to a local account - or answers it itself.
        /// </summary>
        /// <remarks>
        /// One place for both origins, local as well as across the boundary. The
        /// decision does not hang on where the request came from but solely on
        /// the roster of the recipient; to make it twice would mean creating two
        /// opportunities to make it differently.
        ///
        /// Two reasons to answer oneself:
        /// <list type="bullet">
        ///   <item>
        ///     The applicant may see us anyway already (section 3.1.4) - the
        ///     question is answered before it was asked.
        ///   </item>
        ///   <item>
        ///     They are pre-approved (section 3.4.2) - then the request
        ///     <b>must</b> not be delivered to the user in the first place.
        ///   </item>
        /// </list>
        /// </remarks>
        private async Task DeliverSubscribeAsync(String fromBareJid,
                                                 String toBareJid,
                                                 String stanza)
        {

            var account = GetAccount(toBareJid);

            // RFC 6121, section 8.1: for an account that does not exist here
            // there is nothing to do.
            if (account is null)
                return;

            var entry = RosterEntryOf(account, fromBareJid);

            if (entry?.Approved == true ||
                account.SubscriptionOf(fromBareJid) is "from" or "both")
            {
                await AutoApproveAsync(account, fromBareJid);
                return;
            }

            // Section 3.1.3, rule 4: the complete stanza is kept until the
            // contact consents or declines, and delivered anew at every newly
            // available resource.
            //
            // It is always kept, not only when nobody is connected just now. The
            // rule demands the delivery to *every* resource the contact creates
            // afterwards; to hold a request only when by chance nobody was there
            // just then would miss precisely the case the rule exists for - the
            // contact is signed on but is not looking just now and signs off.
            //
            // Beside that, the same keeping holds fast that a request is
            // pending. Per section 3.4.2 it hangs on that whether a later
            // 'subscribed' is a consent or a pre-approval.
            //
            // Appendix A, table 6: if a request of this sender is already there,
            // it shall not be delivered a second time.
            if (!account.RememberSubscriptionRequest(fromBareJid, stanza,
                                                     MaxStoredSubscriptionRequests))
            {
                return;
            }

            // No roster entry: the security warning of the same section forbids
            // it expressly as long as no consent has been given.
            await RouteToAsync(toBareJid, stanza);

        }

        /// <summary>
        /// Delivers the kept subscription requests to a newly available resource
        /// (RFC 6121, section 3.1.3, rule 4).
        /// </summary>
        /// <remarks>
        /// The requests stay standing in the process. The rule demands the
        /// delivery "until the contact either approves or denies the request" -
        /// a request overlooked at the first sign-on would otherwise be lost for
        /// ever, and the applicant would wait for an answer that nobody can give
        /// any more.
        /// </remarks>
        private async Task SendStoredSubscriptionRequestsToAsync(XMPPSession session)
        {

            if (session.Account is not { } account)
                return;

            foreach (var request in account.PendingSubscriptionRequests)
                await session.SendAsync(request.Value);

        }

        /// <summary>
        /// Answers a request in the name of the user.
        /// </summary>
        /// <remarks>
        /// The answer takes the same route as one given by hand: the applicant
        /// shall not be able to distinguish whether a human being or the server
        /// consented. If they lie on this domain, their roster half is tended to
        /// as well - across the boundary their own server does that as soon as
        /// the <c>subscribed</c> arrives there.
        /// </remarks>
        private async Task AutoApproveAsync(XMPPAccount account, String requesterBareJid)
        {

            // A provision, no living path: the one caller decides on the
            // automatic consent *before* it keeps anything, and both routes on
            // which a subscription becomes 'from' clear the request away
            // already. There is therefore no state today in which anything
            // would still lie here - no test holds the line fast, and a mutation
            // survives it. It stands because that is a statement about the order
            // in DeliverSubscribeAsync and not about this method: whoever
            // rearranges things there would otherwise leave the request lying.
            account.ForgetSubscriptionRequest(requesterBareJid);

            UpdateRosterEntry(account, requesterBareJid,
                              GrantFrom(account.SubscriptionOf(requesterBareJid)),
                              approved: false);

            await PushRosterEntryAsync(account, requesterBareJid);

            if (GetAccount(requesterBareJid) is { } requester)
            {
                UpdateRosterEntry(requester, account.BareJid,
                                  GrantTo(requester.SubscriptionOf(account.BareJid)),
                                  ask: AskChange.Clear);
                await PushRosterEntryAsync(requester, account.BareJid);
            }

            await RouteToAsync(requesterBareJid,
                               $"<presence from='{account.BareJid}' to='{requesterBareJid}' type='subscribed'/>");

        }

        /// <summary>
        /// The type of a subscription presence, or null when it is none.
        /// </summary>
        private static String? SubscriptionTypeOf(String stanza)
        {

            if (!StanzaElement.Is(stanza, "presence"))
                return null;

            return Attr(stanza, "type") is "subscribe" or "subscribed" or
                                           "unsubscribe" or "unsubscribed"
                       ? Attr(stanza, "type")
                       : null;

        }

        /// <summary>
        /// Applies a subscription presence that came in from outside to the
        /// roster of the local account (RFC 6121, section 3).
        /// </summary>
        /// <param name="remoteBareJid">The sender on the foreign domain.</param>
        /// <param name="localBareJid">The local account.</param>
        /// <param name="type">subscribe, subscribed, unsubscribe or unsubscribed.</param>
        /// <param name="stanza">The stanza that came in, for delivery to the resources.</param>
        /// <remarks>
        /// Here exactly <b>one</b> half is tended to: that of the local account.
        /// The other belongs to the foreign domain, and to guess it would be
        /// wrong - every side keeps its own roster, and across the boundary one
        /// learns of the other only what is expressly sent. Therein lies exactly
        /// the difference to the handshake between two local accounts, where the
        /// same server has both halves in hand.
        /// </remarks>
        private async Task ApplyRemoteSubscriptionAsync(String  remoteBareJid,
                                                        String  localBareJid,
                                                        String  type,
                                                        String  stanza)
        {

            var account = GetAccount(localBareJid);

            // RFC 6121, section 8.1: for an account that does not exist here
            // there is nothing to do.
            if (account is null)
                return;

            switch (type)
            {

                // Deliver or answer oneself - the same decision as with a
                // request from next door.
                case "subscribe":
                    await DeliverSubscribeAsync(remoteBareJid, localBareJid, stanza);
                    return;

                // Section 3.1.6: the consent of the other side sets our 'to'
                // half and settles the pending request.
                case "subscribed":
                    UpdateRosterEntry(account, remoteBareJid,
                                      GrantTo(account.SubscriptionOf(remoteBareJid)),
                                      ask: AskChange.Clear);
                    await PushRosterEntryAsync(account, remoteBareJid);
                    break;

                // Section 3.2.3: the revocation takes the 'to' half from us.
                case "unsubscribed":
                    UpdateRosterEntry(account, remoteBareJid,
                                      RevokeTo(account.SubscriptionOf(remoteBareJid)),
                                      ask: AskChange.Clear);
                    await PushRosterEntryAsync(account, remoteBareJid);
                    break;

                // Section 3.3.3: the other side cancels what it was allowed to
                // see at us - our 'from' half, that is. And because it may no
                // longer see us, the sign-off follows afterwards.
                case "unsubscribe":
                    UpdateRosterEntry(account, remoteBareJid,
                                      RevokeFrom(account.SubscriptionOf(remoteBareJid)));
                    await PushRosterEntryAsync(account, remoteBareJid);
                    await SendOwnUnavailableToAsync(account, remoteBareJid);
                    break;

            }

            // The stanza itself belongs to the client: about 'subscribe' it
            // wants to decide, about the rest to know.
            await RouteToAsync(localBareJid, stanza);

        }

        /// <summary>
        /// RFC 6121, section 8.5: the stanza was not deliverable at this
        /// address.
        /// </summary>
        /// <param name="intendedRecipient">
        /// The address it did not go to - it becomes the sender of the answer.
        /// For the client the question is "what became of my message to bob",
        /// and that is exactly what it answers; this server as the sender would
        /// be an answer to a different question.
        /// </param>
        /// <param name="replyTo">The checked <c>from</c> of the stanza.</param>
        /// <remarks>
        /// One route back, not two. Whether the sender sits here or on another
        /// server is decided by <see cref="RouteToAsync"/> - that is its only
        /// task, and it also does the namespace change in the process. A branch
        /// of its own for the local case would be a second answer to an already
        /// answered question, and the two could run apart.
        ///
        /// If the answer does not arrive, it stays with that. An error that drew
        /// an error after it would be the beginning of a loop (RFC 6120,
        /// section 8.3.1) - which is why the result of the delivery is
        /// deliberately not looked at here.
        /// </remarks>
        private async Task SendServiceUnavailableAsync(String   kind,
                                                       String?  id,
                                                       String   intendedRecipient,
                                                       String   replyTo)
        {

            await RouteToAsync(
                replyTo,
                $"<{kind} type='error'" +
                (id is not null ? $" id='{id}'" : "") +
                $" from='{intendedRecipient}' to='{replyTo}'>" +
                "<error type='cancel'>" +
                "<service-unavailable xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                "</error>" +
                $"</{kind}>");

        }

        /// <summary>
        /// Tells the sender that the domain of the recipient is not reachable.
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 10.4.3 demands a stanza error but does not lay down
        /// the condition; <c>&lt;remote-server-not-found/&gt;</c> stands in
        /// section 8.3.3.
        ///
        /// The error carries the original recipient as the sender, not this
        /// server: for the client the question is "what became of my message to
        /// bob@elsewhere.example" - and that is exactly what it answers.
        ///
        /// An error stanza is never followed by an error (section 8.3.1).
        /// Otherwise two servers could push notifications back and forth at each
        /// other until one gives up. This check stands at the callers, because
        /// only there is the type of the incoming stanza known.
        /// </remarks>
        private async Task SendRemoteServerNotFoundAsync(XMPPSession  session,
                                                         String       kind,
                                                         String?      id,
                                                         String       intendedRecipient)
        {

            await session.SendAsync(
                $"<{kind} type='error'" +
                (id is not null ? $" id='{id}'" : "") +
                $" from='{intendedRecipient}' to='{session.FullJid}'>" +
                "<error type='cancel'>" +
                "<remote-server-not-found xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                "</error>" +
                $"</{kind}>");

        }

        #endregion

        #region Subscription states

        // The four transitions from RFC 6121, section 3. The subscription value
        // always stands from the point of view of the roster owner: 'from' means
        // "the contact sees me", 'to' means "I see the contact". That is why
        // every direction changes only its own half and leaves the other
        // standing - it is exactly on that that an implementation founders which
        // treats the four states as one scale from none to both.

        /// <summary>
        /// The contact may now see us: none→from, to→both.
        /// </summary>
        internal static String GrantFrom(String? subscription)
            => subscription is "to" or "both" ? "both" : "from";

        /// <summary>
        /// The contact may no longer see us: from→none, both→to.
        /// </summary>
        internal static String RevokeFrom(String? subscription)
            => subscription is "to" or "both" ? "to" : "none";

        /// <summary>
        /// We may now see the contact: none→to, from→both.
        /// </summary>
        internal static String GrantTo(String? subscription)
            => subscription is "from" or "both" ? "both" : "to";

        /// <summary>
        /// We may no longer see the contact: to→none, both→from.
        /// </summary>
        internal static String RevokeTo(String? subscription)
            => subscription is "from" or "both" ? "from" : "none";

        #endregion

        #region Helper functions

        /// <summary>
        /// The refusal per RFC 6120, section 8.2.3, rule 2.
        /// </summary>
        /// <remarks>
        /// The <c>id</c> goes along when there is one, and is missing otherwise -
        /// an empty attribute belongs to no question and is worse than none.
        ///
        /// The refusal is sent all the same, even without an <c>id</c>. Rule 2
        /// puts it under no proviso, and the reason carries: where an unanswered
        /// request merely lets the sender wait, this answer says something about
        /// the stanza itself - that its form is not right. They can use that even
        /// when they cannot assign it to any pending question.
        ///
        /// The sender is this server. <c>&lt;service-unavailable/&gt;</c>
        /// answers in the name of the intended recipient, because the server
        /// there answered for them; here it did not even accept the stanza, and
        /// a recipient as the sender would claim that somebody had looked into
        /// it.
        /// </remarks>
        /// <summary>
        /// RFC 6120, section 8.3.3.8: refuses a stanza whose <c>to</c> is no
        /// JID.
        /// </summary>
        /// <remarks>
        /// The check is the one from RFC 7622 - the same one the client applies
        /// to its own addresses. It stood there complete up to here and was not
        /// asked by the server a single time: what arrived went into the
        /// delivery, and an impossible recipient looked there like an absent
        /// one. The sender got silence or a storage nobody ever fetches.
        ///
        /// <b>The sender of the refusal is this server</b>, not the intended
        /// recipient - unlike with <c>&lt;service-unavailable/&gt;</c>, which
        /// answers in the name of a recipient because the server answered for
        /// them there. Here there is none: the address is not one, so nobody
        /// looked into it.
        ///
        /// <b>No <c>to</c> is not a wrong one.</b> A stanza without an address
        /// is directed at the server (section 8.1.1.1), and undirected presence
        /// never carries one.
        ///
        /// An error stanza is not followed by an error (section 8.3.1) -
        /// discarded it is all the same, deliverable it is not after all.
        /// </remarks>
        /// <returns>true when the stanza ends here.</returns>
        private async Task<Boolean> RefuseMalformedToAsync(XMPPSession  session,
                                                           String       frame,
                                                           String       kind)
        {

            var to = Attr(frame, "to");

            if (to is null || JID.TryParse(to, out _))
                return false;

            if (Attr(frame, "type") != "error")
                await session.SendAsync(
                    JidMalformedError(kind, Attr(frame, "id"), session.FullJid));

            return true;

        }

        /// <summary>
        /// The error frame for a <c>to</c> that is no JID (RFC 6120,
        /// section 8.3.3.8).
        /// </summary>
        /// <remarks>
        /// One version for both origins. The second would have differed only in
        /// small things - and precisely those would have been the difference
        /// nobody notices: a client that gets a different kind of error across
        /// the boundary than in its own house has two cases to handle where
        /// there is one.
        ///
        /// <paramref name="replyTo"/> may be missing: before the binding the
        /// sender has no address yet, and an empty <c>to</c> would be worse than
        /// none.
        /// </remarks>
        private String JidMalformedError(String kind, String? id, String? replyTo)

            => $"<{kind} type='error'" +
               (id is not null ? $" id='{id}'" : "") +
               $" from='{Domain}'" +
               (replyTo is not null ? $" to='{replyTo}'" : "") +
               ">" +
               "<error type='modify'>" +
               "<jid-malformed xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
               "</error>" +
               $"</{kind}>";

        private String BadRequestIq(String? id)

            => "<iq type='error'" +
               (id is not null ? $" id='{id}'" : "") +
               $" from='{Domain}'>" +
               "<error type='modify'>" +
               "<bad-request xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
               "</error></iq>";

        /// <summary>
        /// Builds an <c>iq type='error'</c> per RFC 6120, section 8.3.
        /// </summary>
        /// <param name="payload">
        /// The original request that the error takes back with it (RFC 6120,
        /// section 8.3.1). Without it an asker who has several requests of the
        /// same kind pending knows only <i>that</i> one has failed.
        /// </param>
        /// <param name="applicationError">
        /// The application-specific error condition as finished XML, or null (RFC
        /// 6120, section 8.3.2). The conditions of the RFC are coarse: two
        /// refusals for entirely different reasons carry the same one, and only
        /// this second element says which it was.
        /// </param>
        internal String StanzaErrorIq(String?  id,
                                      String   condition,
                                      String   errorType         = "cancel",
                                      String?  text              = null,
                                      String?  payload           = null,
                                      String?  applicationError  = null)

            => $"<iq type='error' id='{id}' from='{Domain}'>" +
               (payload ?? "") +
               $"<error type='{errorType}'>" +
               $"<{condition} xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
               (text is not null
                    ? $"<text xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'>{text}</text>"
                    : "") +
               (applicationError ?? "") +
               "</error></iq>";

        private static String CarbonEnvelope(String kind, String ownBareJid, String targetFullJid, String inner)
            => $"<message xmlns='jabber:client' from='{ownBareJid}' to='{targetFullJid}'>" +
               $"<{kind} xmlns='urn:xmpp:carbons:2'>" +
               $"<forwarded xmlns='urn:xmpp:forward:0'>{inner}</forwarded>" +
               $"</{kind}></message>";

        /// <summary>
        /// Sets or replaces the from attribute in the outermost element.
        /// </summary>
        internal static String StampFrom(String stanza, String? fullJid)
        {

            if (fullJid is null)
                return stanza;

            var m = Regex.Match(stanza, @"^<(\w+)([^>]*?)(/?)>");

            if (!m.Success)
                return stanza;

            var attrs = Regex.Replace(m.Groups[2].Value, @"\s+from=['""][^'""]*['""]", "");

            return $"<{m.Groups[1].Value}{attrs} from='{fullJid}'{m.Groups[3].Value}>" +
                   stanza[m.Length..];

        }

        /// <summary>
        /// Sets or replaces the to attribute in the outermost element.
        /// </summary>
        /// <remarks>
        /// Undirected presence carries no <c>to</c> - within one server it needs
        /// none either, because the server itself knows to whom it distributes
        /// it. Across a domain boundary that does not work: there the address is
        /// all the other side has, and without it it refuses the stanza.
        /// </remarks>
        internal static String StampTo(String stanza, String jid)
        {

            var m = Regex.Match(stanza, @"^<(\w+)([^>]*?)(/?)>");

            if (!m.Success)
                return stanza;

            var attrs = Regex.Replace(m.Groups[2].Value, @"\s+to=['""][^'""]*['""]", "");

            return $"<{m.Groups[1].Value}{attrs} to='{jid}'{m.Groups[3].Value}>" +
                   stanza[m.Length..];

        }

        private static String? Attr(String xml, String name)
        {
            var m = Regex.Match(xml, @"^<\w+[^>]*?\s" + name + @"=['""]([^'""]*)['""]");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// An attribute of the <c>&lt;query/&gt;</c> child element.
        /// </summary>
        /// <remarks>
        /// <see cref="Attr"/> is anchored to the root element and delivers null
        /// silently for an attribute at the child element. The <c>ver</c> of the
        /// roster request, however, sits at the <c>&lt;query/&gt;</c>, not at the
        /// <c>&lt;iq/&gt;</c> - a check with <c>Attr</c> would look right and
        /// would never read anything.
        /// </remarks>
        private static String? QueryAttr(String xml, String name)
        {

            var m = Regex.Match(xml, @"<query\b([^>]*)>");

            if (!m.Success)
                return null;

            var a = Regex.Match(m.Groups[1].Value, @"\b" + name + @"\s*=\s*['""]([^'""]*)['""]");

            return a.Success ? a.Groups[1].Value : null;

        }

        private static String? AttrIn(String attrs, String name)
        {
            var m = Regex.Match(attrs, name + @"\s*=\s*['""]([^'""]*)['""]");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// A roster entry as an <c>&lt;item/&gt;</c> (RFC 6121,
        /// section 2.1.2).
        /// </summary>
        /// <remarks>
        /// <b>One place for the fetch and for the push.</b> They stood
        /// separately, and the groups were then missing separately too - two
        /// pieces of information about the same entry run apart at some point,
        /// and the versioning makes a lasting one out of that: the client takes
        /// the state from the push for the whole one and does not ask again.
        /// </remarks>
        private static String RosterItemXml(RosterEntry entry)

            => $"<item jid='{entry.Jid}'" +
               (entry.Name is not null ? $" name='{XmlEscaping.Escape(entry.Name)}'" : "") +
               (entry.Ask  is not null ? $" ask='{entry.Ask}'"   : "") +
               (entry.Approved         ? " approved='true'"      : "") +
               $" subscription='{entry.Subscription}'" +
               (entry.Groups.Count == 0
                    ? "/>"
                    : ">" +
                      String.Concat(entry.Groups.Select(g => $"<group>{XmlEscaping.Escape(g)}</group>")) +
                      "</item>");

        private static String BareOf(String jid)
        {
            var slash = jid.IndexOf('/');
            return slash > 0 ? jid[..slash] : jid;
        }

        /// <summary>
        /// The domain part of a JID - out of <c>alice@example.com/mobile</c>
        /// becomes <c>example.com</c>.
        /// </summary>
        /// <remarks>
        /// A JID without an <c>@</c> is a bare domain, as it stands in <c>to</c>
        /// when a stanza goes to the server itself.
        /// </remarks>
        internal static String DomainOf(String jid)
        {

            var bare  = BareOf(jid);
            var at    = bare.IndexOf('@');

            return at >= 0 ? bare[(at + 1)..] : bare;

        }

        /// <summary>
        /// Does this JID belong to the domain this server serves?
        /// </summary>
        internal Boolean IsLocal(String jid)
            => String.Equals(DomainOf(jid), Domain, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Creates a self-signed server certificate for the domain.
        /// </summary>
        /// <remarks>
        /// Deliberately via the BCL and not via Hermod's <c>PKIFactory</c>: that
        /// saves the dependency on BouncyCastle and a three-stage CA chain, of
        /// which nothing is needed here.
        ///
        /// The detour via PFX at the end is necessary on Windows. A certificate
        /// from <c>CreateSelfSigned</c> carries its key in a form that
        /// <c>SslStream</c> does not accept during the handshake; only after
        /// exporting and loading it anew is it usable.
        /// </remarks>
        private static X509Certificate2 CreateSelfSignedCertificate(String domain)
        {

            using var key = RSA.Create(2048);

            var request = new CertificateRequest($"CN={domain}",
                                                 key,
                                                 HashAlgorithmName.SHA256,
                                                 RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, true));

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature |
                                          X509KeyUsageFlags.KeyEncipherment,
                                          true));

            // Without Server Authentication the check of the operating system
            // refuses the certificate even when one would otherwise trust it.
            // Client Authentication comes along for SASL-EXTERNAL: there the
            // establishing server presents its certificate as a client, and a
            // certificate without this usage would be rejected in the process.
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1"),
                                                   new Oid("1.3.6.1.5.5.7.3.2")], true));

            var alternativeNames = new SubjectAlternativeNameBuilder();
            alternativeNames.AddDnsName(domain);
            alternativeNames.AddDnsName("localhost");
            alternativeNames.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(alternativeNames.Build());

            var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                                                       DateTimeOffset.UtcNow.AddYears(1));

            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx),
                                                     null);

        }

        /// <summary>
        /// A certificate check for the client that accepts exactly the
        /// certificate of this server and nothing else.
        /// </summary>
        /// <remarks>
        /// Stands here because only the test server knows its own fingerprint.
        /// What is compared is the fingerprint and not the name: two servers of
        /// this class are both called "localhost" but carry different keys.
        ///
        /// Deliberately no check that waves everything through. Such a one would
        /// be shorter but would have decoupled the connections of the tests from
        /// TLS: they would then come about against any other server as well.
        /// </remarks>
        public Boolean IsOwnCertificate(Object            sender,
                                        X509Certificate?  certificate,
                                        X509Chain?        chain,
                                        SslPolicyErrors   errors)

            => Certificate is not null &&
               certificate is not null &&
               String.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256),
                             Certificate.GetCertHashString(HashAlgorithmName.SHA256),
                             StringComparison.OrdinalIgnoreCase);


        #endregion

        public async ValueTask DisposeAsync()
        {

            _cts.Cancel();

            if (_resumptionSweeper is not null)
            {
                await _resumptionSweeper.DisposeAsync();
                _resumptionSweeper = null;
            }

            KillAllSessions();

            lock (_lock)
                _resumable.Clear();

            try { await _webSocketServer.Shutdown(Wait: true); }
            catch { }

            _cts.Dispose();

        }

    }

}
