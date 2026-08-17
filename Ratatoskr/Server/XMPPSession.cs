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

using System.Text.RegularExpressions;

using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    /// <summary>
    /// A single client connection on the test server - after the resource
    /// binding it corresponds to exactly one resource of an account.
    /// </summary>
    public sealed class XMPPSession
    {

        #region Data

        private readonly AWebSocketServer _server;
        private readonly WebSocketServerConnection _connection;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly List<String> _received = [];
        private readonly List<String> _sent = [];
        private readonly HashSet<String> _directedPresence = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _lock = new();

        /// <summary>
        /// XEP-0352, section 3: what is held back as long as the client has
        /// declared itself inactive - together with the key under which a later
        /// stanza supersedes it.
        /// </summary>
        private readonly List<(String? Key, String Xml)> _held = [];

        /// <summary>
        /// XEP-0198, section 5: what went to the client and is not acknowledged
        /// yet. After a resumption exactly that is sent afterwards.
        /// </summary>
        private readonly Queue<(UInt32 Seq, String Stanza)> _unackedToClient = new();

        private UInt32? _lastAckFromClient;

        #endregion

        #region Properties

        /// <summary>
        /// The running number of the connection, in the order in which connections were established.
        /// </summary>
        public Int32 ConnectionNumber { get; }

        /// <summary>
        /// The account, as soon as the authentication succeeded.
        /// </summary>
        public XMPPAccount? Account { get; internal set; }

        /// <summary>
        /// The SCRAM exchange in progress between <c>&lt;auth/&gt;</c> and
        /// <c>&lt;response/&gt;</c>, otherwise null.
        /// </summary>
        internal SCRAMExchange? Scram { get; set; }

        /// <summary>
        /// How many authentication attempts on this stream have failed.
        /// </summary>
        /// <remarks>
        /// Counted per stream and not per account, and that difference is the
        /// whole of it. A counter on the account is a lock somebody else can
        /// turn: whoever wants Alice shut out fails at her name often enough
        /// and the server does the rest. A counter on the stream costs the
        /// guesser a fresh connection for every handful of tries and costs
        /// nobody else anything - which is the measure RFC 6120, section 13.12
        /// names as limiting the number of authentication attempts per
        /// connection.
        ///
        /// <b>What it does not do</b> is bound the attempts per unit of time:
        /// whoever reconnects begins at zero. Bounding that means counting per
        /// remote address, and no address reaches this far - the sessions are
        /// built by the WebSocket and the TCP links and neither hands one down.
        /// That is a further step and not this one.
        /// </remarks>
        internal Int32 FailedAuthentications { get; set; }

        /// <summary>
        /// The resource assigned, as soon as the binding has taken place.
        /// </summary>
        public String? Resource { get; internal set; }

        /// <summary>
        /// The bare JID, or null before the authentication.
        /// </summary>
        public String? BareJid => Account?.BareJid;

        /// <summary>
        /// The full JID, or null before the binding.
        /// </summary>
        public String? FullJid => Account is not null && Resource is not null
                                      ? $"{Account.BareJid}/{Resource}"
                                      : null;

        /// <summary>
        /// XEP-0280: Has the client enabled carbons for this resource?
        /// </summary>
        public Boolean CarbonsEnabled { get; internal set; }

        /// <summary>
        /// XEP-0352: Is a human being looking right now?
        /// </summary>
        /// <remarks>
        /// Section 5: "The server MUST assume all clients to be in the 'active'
        /// state until the client indicates otherwise." Hence true and not, say,
        /// "unknown": a client that does not know XEP-0352 gets exactly what it
        /// would get without this extension.
        /// </remarks>
        public Boolean ClientIsActive { get; private set; } = true;

        /// <summary>
        /// XEP-0352: How many stanzas are being held back right now.
        /// </summary>
        public Int32 HeldWhileInactive
        {
            get { lock (_lock) return _held.Count; }
        }

        /// <summary>
        /// The stanzas held back, in the order in which they are to be sent.
        /// </summary>
        public IReadOnlyList<String> HeldStanzas
        {
            get { lock (_lock) return [.. _held.Select(e => e.Xml)]; }
        }

        /// <summary>
        /// XEP-0352, section 3: How many stanzas were dropped because they would
        /// no longer have been true when delivered afterwards.
        /// </summary>
        public Int32 DiscardedWhileInactive { get; private set; }

        /// <summary>
        /// XEP-0352: How many stanzas are held back at most before the buffer
        /// goes out of its own accord.
        /// </summary>
        /// <remarks>
        /// A client that declares itself inactive and then never comes back
        /// would otherwise leave behind a buffer that grows until the end of the
        /// connection - and a server one can wring unbounded memory out of with
        /// a single <c>&lt;inactive/&gt;</c> has turned a saving measure against
        /// itself.
        ///
        /// On overflow the whole buffer goes out and nothing is thrown away: the
        /// client then gets traffic it did not want just now - that is the
        /// friendlier of the two possibilities.
        /// </remarks>
        public Int32 MaxHeldWhileInactive { get; set; } = 100;

        /// <summary>
        /// The last undirected presence sent by this resource, already stamped
        /// with the full JID - or null as long as the client has not sent one
        /// yet.
        /// </summary>
        /// <remarks>
        /// Per RFC 6121, section 4.2.1 a bound resource without a sent presence
        /// is not "available" yet. Hence null and not, say, an assumed
        /// <c>&lt;presence/&gt;</c>: to a probe of this resource there is then
        /// simply nothing to answer.
        /// </remarks>
        public String? LastPresence { get; private set; }

        /// <summary>
        /// Has this resource sent its first undirected presence? On exactly that
        /// hangs when the server delivers it the state of the contacts
        /// afterwards (RFC 6121, section 4.3.1).
        /// </summary>
        public Boolean HasSentInitialPresence { get; private set; }

        /// <summary>
        /// Does this resource currently count as available towards the contacts?
        /// </summary>
        /// <remarks>
        /// Kept apart from <see cref="HasSentInitialPresence"/>, because the two
        /// answer different things: whether a presence ever came, and whether
        /// the last one was an available one. On the second hangs whether the
        /// teardown still has to make up the sign-off - if the client sent it
        /// itself, it would otherwise come a second time.
        /// </remarks>
        public Boolean IsAvailable { get; private set; }

        /// <summary>
        /// The priority from the last undirected presence (RFC 6121,
        /// section 4.7.2.3) - default 0.
        /// </summary>
        /// <remarks>
        /// It is no ornament: per section 8.5.2.1.1 the server must not deliver
        /// any message at all to a resource with a <b>negative</b> priority.
        /// That is precisely what a client sets it for - the device stays
        /// reachable for directed messages to its full JID but gets nothing more
        /// of what went only to the bare JID.
        /// </remarks>
        public Int32 PresencePriority { get; private set; }

        /// <summary>
        /// The entities this resource has sent directed presence to (RFC 6121,
        /// section 4.6) - by bare JID.
        /// </summary>
        /// <remarks>
        /// Section 4.6.1 describes exactly this list: "keeping a list of the
        /// entities (bare JIDs or full JIDs) to which a user has sent directed
        /// presence during the user's current session for a given resource (full
        /// JID), then clearing the list when the user goes offline".
        ///
        /// It therefore hangs on the session and not on the account: directed
        /// presence is a promise of this one resource and ends with it.
        ///
        /// What is kept is the bare JID of the recipient, even when a full JID
        /// was written to. Whoever shows a resource their presence shows it to a
        /// human being, and that person's other device knows it the next moment
        /// anyway. Distinguishing more finely would mean treating the same
        /// person differently depending on the device - and the roster, which
        /// the same question otherwise hangs on, likewise knows only bare JIDs.
        /// </remarks>
        public IReadOnlyCollection<String> DirectedPresenceTargets
        {
            get { lock (_lock) return _directedPresence.ToList(); }
        }

        /// <summary>
        /// May this entity see the presence of this resource because directed
        /// presence was sent to it?
        /// </summary>
        public Boolean HasDirectedPresenceTo(String bareJid)
        {
            lock (_lock)
                return _directedPresence.Contains(bareJid);
        }

        /// <summary>
        /// Notes a directed presence down or takes it back (RFC 6121,
        /// section 4.6.1).
        /// </summary>
        /// <param name="bareJid">The recipient, without a resource.</param>
        /// <param name="available">
        /// true for an available presence, false for <c>unavailable</c>.
        /// </param>
        /// <remarks>
        /// The taking back is a MUST of the section: "The server MUST remove
        /// from the directed presence list (or its functional equivalent) any
        /// entity to which the user sends directed unavailable presence."
        /// Without it the promise would stay standing after the user has
        /// explicitly revoked it - and on that hangs, per section 8.5.3.1, who
        /// may ask them anything at all.
        /// </remarks>
        internal void RecordDirectedPresence(String bareJid, Boolean available)
        {

            lock (_lock)
            {

                if (available)
                    _directedPresence.Add(bareJid);

                else
                    _directedPresence.Remove(bareJid);

            }

        }

        /// <summary>
        /// Hands out the recipients of directed presence and empties the list -
        /// to be called when this resource becomes unavailable.
        /// </summary>
        /// <remarks>
        /// Two jobs in one step, and that is intentional. Section 4.6.1 demands
        /// the emptying on signing off, section 4.6.3, rule 2 demands sending
        /// the sign-off to exactly these recipients beforehand. Were they two
        /// calls, the second could be forgotten - and then either the list would
        /// stay standing beyond the end of the presence or the sign-off would
        /// stay on the way. This way nobody gets at the recipients without
        /// emptying the list, and nobody empties it without holding it in hand.
        ///
        /// What is handed out are bare JIDs; whether a recipient still needs a
        /// sign-off is decided by the server - whoever stands in the roster with
        /// <c>from</c> or <c>both</c> gets it through the ordinary distribution
        /// already.
        /// </remarks>
        internal IReadOnlyCollection<String> TakeDirectedPresenceTargets()
        {

            lock (_lock)
            {

                if (_directedPresence.Count == 0)
                    return [];

                var taken = _directedPresence.ToList();
                _directedPresence.Clear();

                return taken;

            }

        }

        /// <summary>
        /// Takes over an undirected presence of the client.
        /// </summary>
        /// <returns>Was it the first of this session?</returns>
        /// <summary>
        /// Reads the priority out of a presence stanza.
        /// </summary>
        /// <remarks>
        /// RFC 6121, section 4.7.2.3: a whole number from -128 to +127. If it is
        /// missing or unusable, 0 holds - and not, say, an error: the number is
        /// a wish of the client, not a contract, and an unreadable one must not
        /// prevent a delivery.
        /// </remarks>
        internal static Int32 ReadPriority(String stanza)
        {

            var m = Regex.Match(stanza, @"<priority[^>]*>\s*(-?\d+)\s*</priority>");

            return m.Success && Int32.TryParse(m.Groups[1].Value, out var value)
                       ? Math.Clamp(value, -128, 127)
                       : 0;

        }

        internal Boolean RecordPresence(String stanza, Boolean available)
        {

            lock (_lock)
            {

                var first = !HasSentInitialPresence;

                // A signed-off resource has no state to report (RFC 6121,
                // section 4.2.1). If the sign-off itself stood here, the server
                // would deliver it afterwards to every contact that signed on
                // after it - and to the contact just signed off a second time,
                // if their first presence was processed only after the sign-off.
                LastPresence            = available ? stanza : null;
                HasSentInitialPresence  = true;
                IsAvailable             = available;
                PresencePriority        = available ? ReadPriority(stanza) : 0;

                // The emptying of the directed presence list does not stand here
                // but in TakeDirectedPresenceTargets: section 4.6.3, rule 2
                // demands sending the sign-off to exactly these recipients
                // beforehand, and whoever emptied it here would take away from
                // the caller the list they need for it.
                return first;

            }

        }

        /// <summary>
        /// Switches the session to signed off and reports whether <b>this</b>
        /// call performed the switch.
        /// </summary>
        /// <remarks>
        /// The sign-off at the end of the connection may go out exactly once.
        /// Previously a check-then-act without a lock stood here: if the
        /// connection dropped while the client was sending its own sign-off,
        /// both routes got past the guard and the contacts got the same sign-off
        /// twice. In the full test run that struck in roughly every second pass.
        /// </remarks>
        internal Boolean TryMarkUnavailable()
        {

            lock (_lock)
            {

                if (!IsAvailable)
                    return false;

                IsAvailable   = false;
                LastPresence  = null;

                return true;

            }

        }

        /// <summary>
        /// XEP-0198: Has stream management been negotiated for this session?
        /// </summary>
        public Boolean StreamManagementEnabled { get; private set; }

        /// <summary>
        /// XEP-0198: The number of countable stanzas the server has sent to the
        /// client since <c>&lt;enabled/&gt;</c>. Exactly this value the client
        /// has to report in its <c>&lt;a h='...'/&gt;</c>.
        /// </summary>
        public UInt32 StanzasSentToClient { get; private set; }

        /// <summary>
        /// XEP-0198: The number of countable stanzas the server has received
        /// from the client since <c>&lt;enabled/&gt;</c>. Exactly this value the
        /// client has to carry as its own outgoing counter.
        /// </summary>
        public UInt32 StanzasReceivedFromClient { get; private set; }

        /// <summary>
        /// XEP-0198: the <c>h</c> last reported by the client, or null as long
        /// as the client has not sent an <c>&lt;a/&gt;</c> yet.
        /// </summary>
        public UInt32? LastAckFromClient
        {
            get { lock (_lock) return _lastAckFromClient; }
            internal set => AcknowledgeToClient(value);
        }

        /// <summary>
        /// XEP-0198, section 5: the identifier this stream can be resumed with -
        /// or null when the client has not asked for it.
        /// </summary>
        /// <remarks>
        /// It is the only secret that identifies a returner: whoever knows it
        /// takes over the stream together with its full JID. That is why it
        /// comes from
        /// <see cref="System.Security.Cryptography.RandomNumberGenerator"/> and
        /// not from the connection number, as the earlier version did - there it
        /// was without consequence, here it would be a way in.
        /// </remarks>
        public String? ResumptionId { get; private set; }

        /// <summary>
        /// XEP-0198, section 5: the stanzas to the client not yet acknowledged,
        /// which would have to be sent afterwards following a resumption.
        /// </summary>
        public Int32 UnacknowledgedToClient
        {
            get { lock (_lock) return _unackedToClient.Count; }
        }

        /// <summary>
        /// The stanzas not yet acknowledged, with their sequence number, in the
        /// order in which they were sent.
        /// </summary>
        internal IReadOnlyList<(UInt32 Seq, String Stanza)> PendingToClient
        {
            get { lock (_lock) return [.. _unackedToClient]; }
        }

        /// <summary>
        /// The underlying WebSocket connection.
        /// </summary>
        public WebSocketServerConnection Connection => _connection;

        /// <summary>
        /// Is the connection still open?
        /// </summary>
        public Boolean IsOpen => !_connection.IsClosed;

        /// <summary>
        /// How often this client has already sent <c>&lt;open/&gt;</c>.
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 6.4.6: after a successful authentication the client
        /// begins the stream anew. On the counter hangs which features the
        /// server offers - SASL before the login, binding afterwards.
        /// </remarks>
        internal Int32 OpenCount { get; set; }

        /// <summary>
        /// Whether the SASL exchange on this stream was begun in the SASL2
        /// profile (XEP-0388) rather than the one from RFC 6120.
        /// </summary>
        /// <remarks>
        /// Per session and not per server, because the choice is the client's:
        /// both profiles are announced and one stream may take either. Every
        /// frame the server sends back - challenge, success, failure - has to
        /// go out in the namespace the exchange was opened in, or it reaches a
        /// client that is not listening for it.
        /// </remarks>
        internal Boolean UsesSasl2 { get; set; }

        /// <summary>
        /// The upgrade tasks the client asked for in its
        /// <c>&lt;authenticate/&gt;</c> (XEP-0480).
        /// </summary>
        /// <remarks>
        /// Asked for, not granted. Whether any of them actually runs is decided
        /// after the login, when there is an account to ask whether it lacks
        /// the material - before that the server does not know whose account it
        /// is, and answering the question earlier would be answering it about a
        /// name a stranger typed.
        /// </remarks>
        internal List<String> RequestedUpgrades { get; } = [];

        /// <summary>
        /// The mechanism an upgrade is running for, between the
        /// <c>&lt;next/&gt;</c> and the client's hash.
        /// </summary>
        internal SCRAMMechanism? PendingUpgrade { get; set; }

        /// <summary>
        /// Whether the client asked for an inline resource binding in its
        /// <c>&lt;authenticate/&gt;</c> (XEP-0386).
        /// </summary>
        internal Boolean WantsInlineBind { get; set; }

        /// <summary>
        /// The <c>&lt;tag/&gt;</c> the client offered for its resource, or null
        /// for none.
        /// </summary>
        internal String? InlineBindTag { get; set; }

        /// <summary>
        /// All frames received from the client, in the order they arrived.
        /// </summary>
        public IReadOnlyList<String> Received
        {
            get { lock (_lock) return _received.ToList(); }
        }

        /// <summary>
        /// All frames sent to the client, in the order they were sent.
        /// </summary>
        public IReadOnlyList<String> Sent
        {
            get { lock (_lock) return _sent.ToList(); }
        }

        #endregion

        #region Constructor(s)

        internal XMPPSession(AWebSocketServer           server,
                             WebSocketServerConnection  connection,
                             Int32                      connectionNumber)
        {
            _server           = server;
            _connection       = connection;
            ConnectionNumber  = connectionNumber;
        }

        #endregion


        internal void RecordReceived(String frame)
        {

            lock (_lock)
            {

                _received.Add(frame);

                if (StreamManagementEnabled && IsStanza(frame))
                    StanzasReceivedFromClient++;

            }

        }

        /// <summary>
        /// XEP-0198: Counts only message, presence and iq - not nonzas such as
        /// <c>&lt;r/&gt;</c> or <c>&lt;a/&gt;</c>.
        ///
        /// Deliberately implemented independently of the client: if the test
        /// server used the same helper, the tests would check both sides with
        /// the same logic and a shared mistake in thinking would stay
        /// undetected.
        ///
        /// That is why <see cref="StanzaElement"/> does not stand here even
        /// after D26, although it answers the same question. The prefix
        /// comparison was wrong all the same — <c>&lt;iqbogus/&gt;</c> counted
        /// here and not at the client, and of all things the counters that have
        /// to run alike would have drifted apart. The way there is a different
        /// one than over there, the answer the same.
        /// </summary>
        internal static Boolean IsStanza(String xml)
            => Regex.IsMatch(xml, @"^\s*<(?:[A-Za-z][\w.-]*:)?(?:message|presence|iq)(?=[\s/>])");

        /// <summary>
        /// XEP-0198: Negotiates stream management and sets both counters to
        /// zero, as section 4 demands for <c>&lt;enabled/&gt;</c>.
        /// </summary>
        /// <param name="resumable">
        /// Has the client demanded <c>resume='true'</c>? Then the session gets
        /// an identifier it can be resumed under.
        /// </param>
        internal void EnableStreamManagement(Boolean resumable = false)
        {
            lock (_lock)
            {

                StreamManagementEnabled    = true;
                StanzasSentToClient        = 0;
                StanzasReceivedFromClient  = 0;
                _lastAckFromClient         = null;

                _unackedToClient.Clear();

                // 128 bits from the random generator, base64 without padding -
                // 22 characters. Shorter would be guessable, and what is to be
                // guessed here is somebody else's session.
                ResumptionId = resumable
                                   ? Convert.ToBase64String(
                                         System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
                                            .TrimEnd('=')
                                   : null;

            }
        }

        /// <summary>
        /// XEP-0198, section 5: Takes over a preserved stream.
        /// </summary>
        /// <remarks>
        /// Taken over is everything the stream hangs on for its surroundings:
        /// the resource - and with it the full JID contacts address it under -,
        /// both counters, the identifier, the presence state and the carbons
        /// setting. What was forgotten here would show up not to the returner
        /// but to their conversation partners.
        ///
        /// The old session object stays behind; its connection is dead and
        /// <see cref="IsOpen"/> filters it out of everything.
        /// </remarks>
        /// <returns>
        /// The stanzas of the old stream still open, so that the caller can send
        /// them afterwards.
        /// </returns>
        internal IReadOnlyList<(UInt32 Seq, String Stanza)> AdoptResumed(XMPPSession previous)
        {

            var pending = previous.PendingToClient;

            lock (_lock)
            {

                Resource                   = previous.Resource;
                CarbonsEnabled             = previous.CarbonsEnabled;

                StreamManagementEnabled    = true;
                ResumptionId               = previous.ResumptionId;
                StanzasSentToClient        = previous.StanzasSentToClient;
                StanzasReceivedFromClient  = previous.StanzasReceivedFromClient;
                _lastAckFromClient         = previous.LastAckFromClient;

                _unackedToClient.Clear();
                foreach (var e in pending)
                    _unackedToClient.Enqueue(e);

                // Without these two the resource would count as freshly bound
                // and thereby as not available (RFC 6121, 4.2.1) - the server
                // would never have signed it off towards the contacts and would
                // nevertheless no longer report it to them as present.
                HasSentInitialPresence     = previous.HasSentInitialPresence;
                IsAvailable                = previous.IsAvailable;
                LastPresence               = previous.LastPresence;

                // What deliberately does *not* stand here: ClientIsActive.
                // XEP-0352, section 5.2 says it explicitly - "stream resumption
                // does not affect the current CSI state, which always defaults
                // to 'active' for new and resumed streams". The client declares
                // itself inactive anew after the resumption if it still is. Were
                // this line to take over the old state, the server would take a
                // returned client for sleeping, and that client would wait for
                // traffic it never requested.

            }

            previous.EndResumption();

            return pending;

        }

        /// <summary>
        /// XEP-0198, section 5: Takes back the promise of resumption.
        /// </summary>
        /// <remarks>
        /// After the deadline expires the stream is finally over. Without this
        /// step the sign-off that now has to be made up would see a resumable
        /// stream in front of it again and would put itself off once more.
        /// </remarks>
        internal void EndResumption()
        {
            lock (_lock)
            {
                ResumptionId = null;
                _unackedToClient.Clear();
            }
        }

        /// <summary>
        /// XEP-0198: The client has reported how much it has received -
        /// everything up to there may leave the buffer.
        /// </summary>
        internal void AcknowledgeToClient(UInt32? h)
        {

            lock (_lock)
            {

                _lastAckFromClient = h;

                if (h is null)
                    return;

                // Modulo arithmetic as on the client side: the counter wraps to
                // 0 after 2^32-1 (section 4), and a plain Seq <= h would leave
                // the open stanzas lying there forever afterwards.
                while (_unackedToClient.Count > 0 &&
                       unchecked(h.Value - _unackedToClient.Peek().Seq) < 0x8000_0000u)
                    _unackedToClient.Dequeue();

            }

        }

        /// <summary>
        /// XEP-0198, section 4: Switches stream management on and confirms it in
        /// one go.
        /// </summary>
        /// <remarks>
        /// Both under the lock that also holds the sending. Otherwise a stanza
        /// can go out between the zeroing of the counters and the
        /// <c>&lt;enabled/&gt;</c>: the server counts it, the client does not -
        /// because the client only resets its counter at the
        /// <c>&lt;enabled/&gt;</c> - and the two states stay exactly this one
        /// apart for the rest of the session. After that every
        /// <c>&lt;a h='…'/&gt;</c> acknowledges one stanza too few, and the
        /// buffer of the unacknowledged never runs empty again.
        ///
        /// The window is narrow and only hits whoever does not send
        /// <c>&lt;enable/&gt;</c> during the setup phase - in the full test run
        /// it was enough.
        /// </remarks>
        /// <param name="resumable">Has the client demanded <c>resume='true'</c>?</param>
        /// <param name="answer">Builds the <c>&lt;enabled/&gt;</c> from the freshly set identifier.</param>
        internal async Task EnableStreamManagementAsync(Boolean                    resumable,
                                                        Func<XMPPSession, String>  answer)
        {

            await _sendLock.WaitAsync();

            try
            {

                EnableStreamManagement(resumable);

                if (_connection.IsClosed)
                    return;

                var xml = answer(this);

                if (await _server.SendTextMessage(_connection, xml) == SentStatus.Success)
                    lock (_lock)
                        _sent.Add(xml);

            }
            catch (Exception)
            {
                // The connection was dropped in the meantime
            }
            finally
            {
                _sendLock.Release();
            }

        }

        /// <summary>
        /// XEP-0198: Asks the client to report its receive counter. The answer
        /// lands in <see cref="LastAckFromClient"/>.
        /// </summary>
        public Task RequestAckAsync()
            => SendAsync("<r xmlns='urn:xmpp:sm:3'/>");

        /// <summary>
        /// RFC 6120, section 4.9: Ends the stream with an error - send the
        /// error, close the stream, lay the connection down.
        /// </summary>
        /// <param name="condition">A condition from section 4.9.3, such as <c>conflict</c>.</param>
        /// <param name="text">Optional explanatory text.</param>
        /// <remarks>
        /// Both in one call, because section 4.9.1.1 leaves no choice:
        /// "Stream-level errors are unrecoverable. Therefore, if an error occurs
        /// at the level of the stream, the entity that detects the error MUST
        /// send an &lt;error/&gt; element ... and then <b>immediately close the
        /// stream</b>." A stream that carries on after a stream error is none
        /// any more: both sides have different notions of what still holds.
        ///
        /// Until D23 it was two methods - this one without the closing and a
        /// <c>FailStreamAsync</c> with it. The separation had no caller that
        /// needed it: both users were tests, and both made up the closing by
        /// hand immediately afterwards. A choice that does not exist should not
        /// be offered by the interface either.
        ///
        /// Three steps, and the middle one is the one easily forgotten over
        /// WebSocket: per RFC 7395, section 3.6 <c>&lt;close/&gt;</c> stands for
        /// the <c>&lt;/stream:stream&gt;</c> - without it the client sees a
        /// socket falling shut without a farewell, and that is a network outage
        /// and not a stream error.
        ///
        /// <see cref="S2SStream.SendStreamErrorAsync"/> does the same for a
        /// server-to-server stream and was called that from the beginning; that
        /// the method of the same name here did something else was the real
        /// trap.
        /// </remarks>
        public async Task SendStreamErrorAsync(String condition, String? text = null)
        {

            await SendAsync("<stream:error xmlns:stream='http://etherx.jabber.org/streams'>" +
                            $"<{condition} xmlns='urn:ietf:params:xml:ns:xmpp-streams'/>" +
                            (text is not null
                                 ? $"<text xmlns='urn:ietf:params:xml:ns:xmpp-streams'>{text}</text>"
                                 : "") +
                            "</stream:error>");

            await SendAsync(WebSocketFraming.Instance.StreamClose());

            Kill();

        }

        /// <summary>
        /// Sends a stanza to this client.
        /// </summary>
        public async Task SendAsync(String xml)
        {

            // RFC 6120, section 4.8.1 and RFC 7395, section 3.3.3: on the client
            // connection every stanza stands in jabber:client, and over
            // WebSocket it has to carry that itself - there is no enclosing
            // <stream:stream> it could inherit it from.
            //
            // Two cases come together here. What the server produces itself
            // carried no namespace at all until now; what came in from a foreign
            // server carried jabber:server and was thereby passed on unchanged.
            // Both are wrong on this stream, and both never showed up, because
            // our own client recognises stanzas by the local name and does not
            // look at the namespace at all - the same leniency that covered up
            // the reverse mistake on the client side for years.
            //
            // Here and not at the callers, for the same reason that counting
            // happens here too: this is the only place every frame to a client
            // runs through.
            xml = StanzaNamespace.Apply(xml, StanzaNamespace.Client);

            await _sendLock.WaitAsync();

            try
            {

                // XEP-0352: First decide whether this goes out now at all -
                // under the same lock that also writes. Otherwise an
                // <active/> could empty the buffer between the decision and the
                // writing, and this stanza would fall behind it into a buffer
                // nobody reads from any more.
                if (!ClientIsActive && !_connection.IsClosed && IsStanza(xml))
                {

                    switch (ClientStateIndication.HandlingOf(xml))
                    {

                        case ClientStateHandling.Discarded:
                            lock (_lock)
                                DiscardedWhileInactive++;
                            return;

                        case ClientStateHandling.Queued:

                            Boolean full;

                            lock (_lock)
                            {

                                var key = ClientStateIndication.SupersedeKey(xml);

                                if (key is not null)
                                    _held.RemoveAll(e => e.Key == key);

                                _held.Add((key, xml));

                                full = _held.Count > MaxHeldWhileInactive;

                            }

                            if (full)
                                await FlushHeldLockedAsync();

                            return;

                    }

                }

                // What was held back goes before anything that goes out now -
                // otherwise an important message would overtake the presence of
                // the same sender, and RFC 6120, section 10.1 explicitly demands
                // the order ("in-order delivery") between two entities.
                //
                // Only before stanzas: a nonza carries no order, and an <r/> of
                // the server must not empty the buffer - that would be a wake-up
                // call through the back door.
                if (IsStanza(xml))
                    await FlushHeldLockedAsync();

                await WriteLockedAsync(xml);

            }
            catch (Exception)
            {
                // The connection was dropped in the meantime
            }
            finally
            {
                _sendLock.Release();
            }

        }

        /// <summary>
        /// XEP-0352: Takes over the state reported by the client and, on the
        /// <c>&lt;active/&gt;</c>, delivers afterwards what was held back.
        /// </summary>
        /// <remarks>
        /// Section 5.1 demands that everything a CSI nonza triggers happens
        /// before the next request of the same client. Hence here and not on the
        /// side: the caller waits for this task, and the next frame of the
        /// client is only handled afterwards.
        /// </remarks>
        internal async Task SetClientStateAsync(Boolean active)
        {

            await _sendLock.WaitAsync();

            try
            {

                lock (_lock)
                    ClientIsActive = active;

                if (active)
                    await FlushHeldLockedAsync();

            }
            catch (Exception)
            {
                // The connection was dropped in the meantime
            }
            finally
            {
                _sendLock.Release();
            }

        }

        /// <summary>
        /// XEP-0352: Hands the buffer out without changing the state.
        /// </summary>
        /// <remarks>
        /// For the end of the connection: what still lies here never reached the
        /// client and did not get into the buffer of unacknowledged stanzas
        /// either - a resumption would not find it. Without this call the saving
        /// measure would be a loss at every drop.
        /// </remarks>
        internal async Task FlushHeldAsync()
        {

            await _sendLock.WaitAsync();

            try
            {
                await FlushHeldLockedAsync();
            }
            catch (Exception)
            {
                // The connection was dropped in the meantime
            }
            finally
            {
                _sendLock.Release();
            }

        }

        /// <summary>
        /// Writes the buffer empty. Only to be called with <c>_sendLock</c>
        /// held.
        /// </summary>
        private async Task FlushHeldLockedAsync()
        {

            List<String> pending;

            lock (_lock)
            {

                if (_held.Count == 0)
                    return;

                pending = [.. _held.Select(e => e.Xml)];
                _held.Clear();

            }

            foreach (var stanza in pending)
                await WriteLockedAsync(stanza);

        }

        /// <summary>
        /// Writes a frame onto the wire, counts it and keeps it as long as it is
        /// unacknowledged. Only to be called with <c>_sendLock</c> held.
        /// </summary>
        private async Task WriteLockedAsync(String xml)
        {

            if (_connection.IsClosed)
            {

                // XEP-0198, section 5: something reaches a preserved stream all
                // the same - it is, after all, waiting for its returner just
                // now, and then it gets it delivered afterwards.
                //
                // Without that the resumption would be almost worthless: saved
                // would be only what was on the way in the last tenth of a
                // second before the drop. Everything that arrives during the
                // disturbance - and that is the case it is about - would be
                // discarded without either sender or recipient learning of it.
                if (ResumptionId is not null && IsStanza(xml))
                    lock (_lock)
                    {
                        StanzasSentToClient++;
                        _unackedToClient.Enqueue((StanzasSentToClient, xml));
                    }

                return;

            }

            var status = await _server.SendTextMessage(_connection, xml);

            // Only a frame actually sent off counts - otherwise the server would
            // report an h to the client that the client can never reach.
            if (status != SentStatus.Success)
                return;

            lock (_lock)
            {

                _sent.Add(xml);

                // XEP-0198: only count after the successful send.
                if (StreamManagementEnabled && IsStanza(xml))
                {

                    StanzasSentToClient++;

                    // And keep it as long as the client has not acknowledged it
                    // - only with a promised resumption, otherwise it would be a
                    // buffer nobody ever reads from.
                    if (ResumptionId is not null)
                        _unackedToClient.Enqueue((StanzasSentToClient, xml));

                }

            }

        }

        /// <summary>
        /// Tears the connection down without a close handshake - simulates a
        /// network outage and triggers a reconnect at the client.
        /// </summary>
        /// <remarks>
        /// <c>Close</c> without a status code deliberately sends no close frame
        /// but only lays the TCP connection down - that is exactly what
        /// distinguishes a network outage from a proper sign-off.
        /// </remarks>
        public void Kill()
        {
            try { _connection.Close().GetAwaiter().GetResult(); }
            catch { /* never mind */ }
        }

        /// <summary>
        /// Counts received frames that contain the given text.
        /// </summary>
        public Int32 CountReceived(String contains)
            => Received.Count(f => f.Contains(contains, StringComparison.Ordinal));

        public override String ToString()
            => FullJid ?? BareJid ?? $"(connection {ConnectionNumber}, not logged in)";

    }

}
