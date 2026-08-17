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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    /// <summary>
    /// A server-to-server stream - the protocol layer between two servers,
    /// without a transport underneath.
    /// </summary>
    /// <remarks>
    /// This class knows neither sockets nor WebSocket frames: it gets incoming
    /// frames handed to it as strings and sends outgoing ones out through a
    /// function. That is precisely why it comes first - TCP and WebSocket are
    /// only two framings of the same layer beneath it, and what they have in
    /// common (handshake, sender check, stream errors, life cycle) shall not
    /// come into being twice.
    ///
    /// <b>The stream is directed</b>, as RFC 6120, section 4.1 describes it:
    /// over it stanzas flow only from the initiator to the receiver. Whoever
    /// wants to answer establishes their own stream in the opposite direction.
    /// That is the reason why a stream without a <c>deliverStanza</c> function
    /// does not deliver incoming stanzas but reports them through
    /// <see cref="OnStanzaRefused"/> and discards them. Conducting both over
    /// one connection would be XEP-0288 (Bidirectional Server-to-Server
    /// Connections) and would have to be negotiated.
    ///
    /// <b>What this layer does not achieve:</b> it <i>believes</i> the peer its
    /// domain. The <c>from</c> in the <c>&lt;open/&gt;</c> is a claim; it is
    /// only proven by dialback (XEP-0220) or SASL-EXTERNAL. Until then a real
    /// transport is worth exactly as much here as
    /// <see cref="DirectServerLinks"/> - only over a network.
    /// </remarks>
    public sealed class S2SStream
    {

        #region Data

        private readonly Func<String, CancellationToken, Task>            sendFrame;
        private readonly Func<String, String, Task<RemoteStanzaResult>>?  deliverStanza;
        private readonly Lock                                             dataLock  = new();

        /// <summary>
        /// The dialback secret of this server, or null. Needed in two roles:
        /// the establishing server produces its key with it, the authoritative
        /// one recomputes it with it.
        /// </summary>
        private readonly String? secret;

        /// <summary>
        /// How this stream is wrapped.
        /// </summary>
        private readonly IS2SFraming framing;

        /// <summary>
        /// Checks whether the certificate the peer presented in the TLS
        /// handshake may speak for the domain named. Null when SASL-EXTERNAL is
        /// out of the question for this stream - because there is no
        /// certificate at all, for instance.
        /// </summary>
        private readonly Func<String, Boolean>? externalIdentity;

        /// <summary>
        /// May this stream identify itself through SASL-EXTERNAL? Only when a
        /// certificate of our own was presented.
        /// </summary>
        private readonly Boolean canOfferExternal;

        /// <summary>
        /// Shall XEP-0288 be attempted (initiator) resp. offered (receiver)?
        /// </summary>
        private readonly Boolean bidi;

        /// <summary>
        /// Has a dialback key that was presented checked at the authoritative
        /// server of the sender domain - the parameters are the sender domain,
        /// the stream ID and the key.
        /// </summary>
        /// <remarks>
        /// Stands here as a function and not as an implementation, because the
        /// check needs a <b>second connection</b> and this layer cannot
        /// establish one. It is at exactly this point that it is decided
        /// whether dialback is worth anything: the address that is asked must
        /// not come from whoever is currently to be checked.
        /// </remarks>
        private readonly Func<String, String, String, Task<Boolean>>? verifyKey;

        /// <summary>
        /// Is fulfilled as soon as dialback is through - and cancelled when the
        /// stream ends beforehand.
        /// </summary>
        private readonly TaskCompletionSource dialbackDone =
            new (TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Is fulfilled as soon as the stream is open <b>and</b> identified.
        /// </summary>
        /// <remarks>
        /// Waiting for both separately does not suffice. After a successful
        /// SASL the stream starts over (RFC 6120, section 6.4.6): for a moment
        /// it is identified and nevertheless not open. Whoever sends then loses
        /// the stanza - and silently at that, because the stream is neither
        /// closed nor faulty.
        /// </remarks>
        private readonly TaskCompletionSource ready =
            new (TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Is fulfilled as soon as the handshake stands - and cancelled when
        /// the stream ends beforehand, so that nobody waits for an
        /// <c>&lt;open/&gt;</c> that cannot come any more.
        /// </summary>
        private readonly TaskCompletionSource openHandshake =
            new (TaskCreationOptions.RunContinuationsAsynchronously);

        #endregion

        #region Properties

        /// <summary>
        /// The namespace of the WebSocket framing (RFC 7395, section 3.1).
        /// </summary>
        public const String FramingNamespace = WebSocketFraming.Namespace;

        /// <summary>
        /// The namespace of the stream layer (RFC 6120, section 4.8.2).
        /// </summary>
        public const String StreamNamespace = "http://etherx.jabber.org/streams";

        /// <summary>
        /// The namespace of the stream error conditions (RFC 6120, section 4.9.2).
        /// </summary>
        public const String StreamErrorNamespace = "urn:ietf:params:xml:ns:xmpp-streams";

        /// <summary>
        /// XEP-0288: the namespace of the <c>&lt;bidi/&gt;</c> element.
        /// </summary>
        public const String BidiNamespace = "urn:xmpp:bidi";

        /// <summary>
        /// XEP-0288: the namespace of the announcement in the features.
        /// </summary>
        public const String BidiFeatureNamespace = "urn:xmpp:features:bidi";

        /// <summary>
        /// XEP-0288: does this stream carry both directions?
        /// </summary>
        /// <remarks>
        /// Without the extension an S2S connection is one-sided (RFC 6120,
        /// section 4.1): whoever gets a stanza answers it over a connection of
        /// their <b>own</b> to the sender domain. That presupposes that they
        /// can reach the peer - behind NAT, behind a firewall or without a DNS
        /// entry they cannot, and the answer is lost. That is exactly what the
        /// return path failed on in the run against Prosody.
        ///
        /// If bidi is negotiated, the same connection carries both directions.
        /// </remarks>
        public Boolean BidiEnabled { get; private set; }

        /// <summary>
        /// Our own domain.
        /// </summary>
        public String LocalDomain { get; }

        /// <summary>
        /// The domain of the peer. Known from the start at the initiator, at
        /// the receiver only after their <c>&lt;open/&gt;</c> - and then as a
        /// claim, not as proof.
        /// </summary>
        public String? RemoteDomain { get; private set; }

        /// <summary>
        /// Did this server establish the stream?
        /// </summary>
        public Boolean IsInitiator { get; }

        /// <summary>
        /// The identifier of the stream. The receiver hands it out, the
        /// initiator reads it from the <c>&lt;open/&gt;</c> of the other side
        /// (RFC 7395, section 3.4). Dialback hangs on it.
        /// </summary>
        public String? StreamId { get; private set; }

        /// <summary>
        /// Does the handshake stand?
        /// </summary>
        public Boolean IsOpen { get; private set; }

        /// <summary>
        /// Has the stream ended?
        /// </summary>
        public Boolean IsClosed { get; private set; }

        /// <summary>
        /// Does this stream demand dialback before stanzas may flow?
        /// </summary>
        /// <remarks>
        /// XEP-0220, section 1: the accepting server "does not process XMPP
        /// stanzas over the connection until it has verified the initiating
        /// server's identity". Without dialback it stays at the state of S4b-2:
        /// the domain of the peer is claimed, not proven. A transport that
        /// switches this off without a substitute opens exactly the hole the
        /// sender check exists against - it is permissible only where the
        /// identity is settled otherwise (SASL-EXTERNAL) or where there is no
        /// network in between at all.
        /// </remarks>
        public Boolean RequiresDialback { get; }

        /// <summary>
        /// Is the domain of the peer proven? With a stream without dialback
        /// this stays false permanently - then
        /// <see cref="RequiresDialback"/> is false too and nobody asks.
        /// </summary>
        public Boolean IsAuthenticated { get; private set; }

        /// <summary>
        /// What the domain of the peer was proven with, or null as long as it
        /// is not.
        /// </summary>
        public String? AuthenticatedBy { get; private set; }

        #endregion

        #region Events

        /// <summary>
        /// An incoming stanza was not delivered - with the reason.
        /// </summary>
        public event Action<String>? OnStanzaRefused;

        /// <summary>
        /// The stream has ended, with the reason or null on a proper
        /// <c>&lt;close/&gt;</c>.
        /// </summary>
        public event Action<String?>? OnClosed;

        /// <summary>
        /// The stream starts over (RFC 6120, section 6.4.6).
        /// </summary>
        /// <remarks>
        /// The transport has to react to it: whatever takes the stream apart
        /// into elements has seen the stream header so far and would otherwise
        /// take the new one for a child element.
        /// </remarks>
        public event Action? OnRestart;

        #endregion

        #region Constructor(s)

        private S2SStream(String                                           localDomain,
                          String?                                          remoteDomain,
                          Boolean                                          isInitiator,
                          Func<String, CancellationToken, Task>            sendFrame,
                          Func<String, String, Task<RemoteStanzaResult>>?  deliverStanza,
                          String?                                          secret,
                          Func<String, String, String, Task<Boolean>>?     verifyKey,
                          Boolean                                          requiresDialback,
                          IS2SFraming?                                     framing,
                          Func<String, Boolean>?                           externalIdentity,
                          Boolean                                          canOfferExternal,
                          Boolean                                          bidi)
        {

            LocalDomain         = localDomain;
            RemoteDomain        = remoteDomain;
            IsInitiator         = isInitiator;
            RequiresDialback    = requiresDialback;

            this.sendFrame      = sendFrame;
            this.deliverStanza  = deliverStanza;
            this.secret         = secret;
            this.verifyKey      = verifyKey;
            this.framing            = framing ?? WebSocketFraming.Instance;
            this.externalIdentity   = externalIdentity;
            this.canOfferExternal   = canOfferExternal;
            this.bidi               = bidi;

        }

        #endregion

        #region (static) Initiate(localDomain, remoteDomain, sendFrame, secret)

        /// <summary>
        /// The outgoing stream: it carries stanzas out and takes none in.
        /// </summary>
        /// <param name="localDomain">Our own domain.</param>
        /// <param name="remoteDomain">The domain that is being established to.</param>
        /// <param name="sendFrame">Sends a frame over the transport.</param>
        /// <param name="secret">
        /// Our own dialback secret. If it is set, this stream identifies itself
        /// of its own accord after the handshake with
        /// <c>&lt;db:result/&gt;</c> and only carries stanzas afterwards.
        /// </param>
        /// <param name="deliverStanza">
        /// Only for XEP-0288: where incoming stanzas go as soon as bidi is
        /// negotiated. Without bidi an outgoing stream takes none in, and this
        /// function is never called.
        /// </param>
        /// <param name="useBidi">
        /// Attempt XEP-0288 when the peer announces it.
        /// </param>
        public static S2SStream Initiate(String                                           localDomain,
                                         String                                           remoteDomain,
                                         Func<String, CancellationToken, Task>            sendFrame,
                                         String?                                          secret            = null,
                                         IS2SFraming?                                     framing           = null,
                                         Boolean                                          canOfferExternal  = false,
                                         Func<String, String, Task<RemoteStanzaResult>>?  deliverStanza     = null,
                                         Boolean                                          useBidi           = false)

            => new (localDomain,
                    remoteDomain,
                    isInitiator:        true,
                    sendFrame:          sendFrame,
                    deliverStanza:      deliverStanza,
                    secret:             secret,
                    verifyKey:          null,
                    requiresDialback:   secret is not null,
                    framing:            framing,
                    externalIdentity:   null,
                    canOfferExternal:   canOfferExternal,
                    bidi:               useBidi);

        #endregion

        #region (static) Accept(localDomain, sendFrame, deliverStanza, secret, verifyKey)

        /// <summary>
        /// The incoming stream: it takes stanzas in and itself sends only the
        /// stream layer.
        /// </summary>
        /// <param name="localDomain">Our own domain.</param>
        /// <param name="sendFrame">Sends a frame over the transport.</param>
        /// <param name="deliverStanza">
        /// Hands an incoming stanza, together with the domain the peer may
        /// speak for, to the routing.
        /// </param>
        /// <param name="secret">
        /// Our own dialback secret - needed in the role of the authoritative
        /// server, to recompute a foreign <c>&lt;db:verify/&gt;</c>.
        /// </param>
        /// <param name="verifyKey">
        /// Has a key that was presented checked at the authoritative server of
        /// the sender domain. If it is set, this stream demands dialback before
        /// it accepts stanzas.
        /// </param>
        /// <param name="offerBidi">
        /// Announce XEP-0288 in the features and accept a
        /// <c>&lt;bidi/&gt;</c> of the peer.
        /// </param>
        public static S2SStream Accept(String                                          localDomain,
                                       Func<String, CancellationToken, Task>           sendFrame,
                                       Func<String, String, Task<RemoteStanzaResult>>  deliverStanza,
                                       String?                                         secret      = null,
                                       Func<String, String, String, Task<Boolean>>?    verifyKey          = null,
                                       IS2SFraming?                                    framing            = null,
                                       Func<String, Boolean>?                          externalIdentity   = null,
                                       Boolean                                         offerBidi          = false)

            => new (localDomain,
                    remoteDomain:       null,
                    isInitiator:        false,
                    sendFrame:          sendFrame,
                    deliverStanza:      deliverStanza,
                    secret:             secret,
                    verifyKey:          verifyKey,
                    requiresDialback:   verifyKey is not null,
                    framing:            framing,
                    externalIdentity:   externalIdentity,
                    canOfferExternal:   false,
                    bidi:               offerBidi);

        #endregion

        #region (static) InitiateVerification(localDomain, remoteDomain, sendFrame)

        /// <summary>
        /// The short-lived stream over which an accepting server queries a
        /// dialback key at the authoritative server (XEP-0220, steps 2 and 3).
        /// </summary>
        /// <remarks>
        /// A role of its own and not merely an <see cref="Initiate"/> without a
        /// secret: no stanza ever goes over it, it does not identify itself and
        /// it belongs in no connection cache. It is established, asks a
        /// question, gets an answer and is gone again.
        /// </remarks>
        public static S2SStream InitiateVerification(String                                localDomain,
                                                     String                                remoteDomain,
                                                     Func<String, CancellationToken, Task> sendFrame,
                                                     IS2SFraming?                          framing = null)

            => new (localDomain,
                    remoteDomain,
                    isInitiator:       true,
                    sendFrame:         sendFrame,
                    deliverStanza:     null,
                    secret:            null,
                    verifyKey:         null,
                    requiresDialback:   false,
                    framing:            framing,
                    externalIdentity:   null,
                    canOfferExternal:   false,
                    bidi:               false);

        #endregion


        #region OpenAsync(CancellationToken)

        /// <summary>
        /// Sends the stream header. Only the initiator begins.
        /// </summary>
        public Task OpenAsync(CancellationToken cancellationToken = default)
        {

            if (!IsInitiator)
                throw new InvalidOperationException(
                          "Only the initiator opens the stream; the receiver answers the <open/>.");

            return sendFrame(framing.StreamOpen(LocalDomain, RemoteDomain, null),
                             cancellationToken);

        }

        #endregion

        #region WaitUntilOpenAsync(Timeout, CancellationToken)

        /// <summary>
        /// Waits for the <c>&lt;open/&gt;</c> of the peer.
        /// </summary>
        /// <returns>false on a timeout or when the stream ended beforehand.</returns>
        public async Task<Boolean> WaitUntilOpenAsync(TimeSpan           Timeout,
                                                      CancellationToken  cancellationToken = default)
        {

            try
            {
                await openHandshake.Task.WaitAsync(Timeout, cancellationToken);
                return true;
            }
            catch (Exception)
            {
                return false;
            }

        }

        #endregion

        #region WaitUntilReadyAsync(Timeout, CancellationToken)

        /// <summary>
        /// Waits until sending over the stream is actually permitted - open
        /// and, if demanded, identified.
        /// </summary>
        public async Task<Boolean> WaitUntilReadyAsync(TimeSpan           Timeout,
                                                       CancellationToken  cancellationToken = default)
        {

            try
            {
                await ready.Task.WaitAsync(Timeout, cancellationToken);
                return true;
            }
            catch (Exception)
            {
                return false;
            }

        }

        #endregion

        #region ProcessFrameAsync(frame, CancellationToken)

        /// <summary>
        /// Processes an incoming frame.
        /// </summary>
        /// <returns>false when the frame was not understood.</returns>
        public async Task<Boolean> ProcessFrameAsync(String             frame,
                                                     CancellationToken  cancellationToken = default)
        {


            if (framing.IsStreamOpen(frame))
                return await ProcessOpenAsync(frame, cancellationToken);

            if (framing.IsStreamClose(frame))
            {
                MarkClosed(null);
                return true;
            }

            // RFC 6120, section 4.9: after a stream error the stream is dead;
            // there is no answer to it.
            if (StanzaElement.Is(frame, "error") ||
                frame.Contains(StreamErrorNamespace, StringComparison.Ordinal))
            {
                MarkClosed($"Stream error of the peer: {frame}");
                return true;
            }

            // The features of the receiver. That dialback is offered stands in
            // there; it is demanded here independently of that, though, because
            // an attacker could simply leave the announcement out.
            // The element name takes the prefix along here too: a server may
            // send its features as <stream:features/> or as <features/>,
            // depending on what it bound the streams namespace to (RFC 6120,
            // section 4.8.1). Both are the same element.
            if (StanzaElement.Is(frame, "features"))
                return await ProcessFeaturesAsync(frame, cancellationToken);

            if (StanzaElement.Is(frame, "bidi"))
                return ProcessBidi(frame);

            if (StanzaElement.Is(frame, "auth"))
                return await ProcessSaslAuthAsync(frame, cancellationToken);

            if (StanzaElement.Is(frame, "abort"))
                return await ProcessSaslAbortAsync(cancellationToken);

            if (StanzaElement.Is(frame, "success"))
                return await ProcessSaslSuccessAsync(cancellationToken);

            if (StanzaElement.Is(frame, "failure") &&
                frame.Contains(SaslNamespace, StringComparison.Ordinal))
            {
                return await ProcessSaslFailureAsync(cancellationToken);
            }

            if (IsDialback(frame, "result"))
                return await ProcessDialbackResultAsync(frame, cancellationToken);

            if (IsDialback(frame, "verify"))
                return await ProcessDialbackVerifyAsync(frame, cancellationToken);

            if (StanzaElement.IsStanza(frame))
                return await ProcessStanzaAsync(frame, cancellationToken);

            // A frame without an element is not an unknown element but none at
            // all - section 4.9.3.24 speaks of "a first-level child of the
            // stream that is not supported", and an empty frame is no child.
            // Over TCP such a thing does not even arrive: SkipProlog in the
            // splitter swallows whitespace, XML declarations and comments, and
            // whitespace as a keepalive is explicitly permitted (section
            // 4.6.1). Over WebSocket every frame is passed through.
            if (StanzaElement.NameOf(frame) is null)
                return false;

            // RFC 6120, section 4.9.3.24, as on the client connection since
            // D26.
            //
            // Until now an unknown element stayed lying here, and that was an
            // openly noted gap and no carelessness: on the client stream both
            // sides speak the same thing, here a foreign implementation stands
            // opposite. Breaking a stream off because one does not know an
            // element would have been a bet against Prosody or ejabberd.
            //
            // It was therefore measured first: over the full run against both
            // peers, outgoing as well as incoming, not a single frame fell
            // through to here - and the feeler for it demonstrably struck,
            // otherwise "measured nothing" would only mean "did not look".
            await SendStreamErrorAsync("unsupported-stanza-type",
                                       cancellationToken: cancellationToken);

            return true;

        }

        #endregion

        #region SendStanzaAsync(stanza, CancellationToken)

        /// <summary>
        /// Sends a stanza over the stream.
        /// </summary>
        /// <returns>false when the stream is not (any longer) open.</returns>
        public async Task<Boolean> SendStanzaAsync(String             stanza,
                                                   CancellationToken  cancellationToken = default)
        {

            lock (dataLock)
            {

                if (!IsOpen || IsClosed)
                    return false;

                // A stream that has not identified itself yet carries nothing.
                // The peer would discard it anyway.
                if (RequiresDialback && !IsAuthenticated)
                    return false;

            }

            await sendFrame(stanza, cancellationToken);

            return true;

        }

        #endregion

        #region WaitUntilAuthenticatedAsync(Timeout, CancellationToken)

        /// <summary>
        /// Waits until dialback is through.
        /// </summary>
        /// <returns>
        /// true right away also when this stream demands no dialback at all -
        /// then there is nothing to wait for.
        /// </returns>
        public async Task<Boolean> WaitUntilAuthenticatedAsync(TimeSpan           Timeout,
                                                               CancellationToken  cancellationToken = default)
        {

            if (!RequiresDialback)
                return true;

            try
            {
                await dialbackDone.Task.WaitAsync(Timeout, cancellationToken);
                return true;
            }
            catch (Exception)
            {
                return false;
            }

        }

        #endregion

        #region CloseAsync(CancellationToken)

        /// <summary>
        /// Ends the stream properly (RFC 7395, section 3.6).
        /// </summary>
        public async Task CloseAsync(CancellationToken cancellationToken = default)
        {

            lock (dataLock)
            {
                if (IsClosed)
                    return;
            }

            try
            {
                await sendFrame(framing.StreamClose(), cancellationToken);
            }
            catch (Exception)
            {
                // The connection is already gone - the result is the same.
            }

            MarkClosed(null);

        }

        #endregion

        #region SendStreamErrorAsync(condition, text, CancellationToken)

        /// <summary>
        /// Ends the stream with an error (RFC 6120, section 4.9).
        /// </summary>
        /// <param name="condition">A condition from section 4.9.3, such as <c>invalid-from</c>.</param>
        /// <param name="text">Optional explanatory text.</param>
        public async Task SendStreamErrorAsync(String             condition,
                                               String?            text                = null,
                                               CancellationToken  cancellationToken   = default)
        {

            try
            {
                await sendFrame(
                          $"<stream:error xmlns:stream='{StreamNamespace}'>" +
                          $"<{condition} xmlns='{StreamErrorNamespace}'/>" +
                          (text is not null
                               ? $"<text xmlns='{StreamErrorNamespace}'>{XmlEscaping.Escape(text)}</text>"
                               : "") +
                          "</stream:error>",
                          cancellationToken);
            }
            catch (Exception)
            {
                // An unheard error ends the stream too.
            }

            MarkClosed(condition);

        }

        #endregion


        #region (private) ProcessOpenAsync(frame, CancellationToken)

        /// <summary>
        /// The stream header of the peer (RFC 7395, section 3.4 resp.
        /// RFC 6120, section 4.7).
        /// </summary>
        /// <remarks>
        /// The attributes are read, not parsed. Over TCP the stream header is
        /// an <b>open</b> tag and thereby, taken by itself, not well-formed XML
        /// - <see cref="XElement.Parse(String)"/> stood here first and would
        /// have refused every TCP connection with
        /// <c>&lt;bad-format/&gt;</c>.
        /// </remarks>
        private async Task<Boolean> ProcessOpenAsync(String             frame,
                                                     CancellationToken  cancellationToken)
        {

            var from  = Attr(frame, "from");
            var to    = Attr(frame, "to");
            var id    = Attr(frame, "id");

            if (IsInitiator)
            {

                // The peer has to give itself out as the domain we established
                // to. If it names another one, either the address is wrong or
                // somebody is sitting in between - in both cases the stream is
                // worth nothing.
                if (from is not null &&
                    !String.Equals(from, RemoteDomain, StringComparison.OrdinalIgnoreCase))
                {
                    await SendStreamErrorAsync("invalid-from",
                                               $"Expected was '{RemoteDomain}', the answer came from '{from}'.",
                                               cancellationToken);
                    return false;
                }

                MarkOpen(id);

                // After a SASL restart the stream is already identified; then
                // the second stream header only stands for the new beginning
                // and there is nothing left to negotiate.
                if (IsAuthenticated)
                    return true;

                // If SASL-EXTERNAL is a possibility, the offer of the peer is
                // waited for - it stands in the features, which follow in a
                // moment.
                //
                // The same holds for XEP-0288, and that even when only dialback
                // is a possibility: whether bidi is offered likewise only
                // stands in the features, and the <bidi/> has to go out
                // *before* the <db:result/> (XEP-0288, section 4). The
                // unsolicited dialback from XEP-0220 therefore moves to
                // ProcessFeaturesAsync.
                if (canOfferExternal || bidi)
                    return true;

                // XEP-0220, step 1: identify oneself unsolicited. The key binds
                // to the stream ID the peer has just handed out.
                if (RequiresDialback && secret is not null && StreamId is not null)
                    await sendFrame(
                              $"<db:result xmlns:db='{DialbackKey.Namespace}' " +
                              $"from='{XmlEscaping.Escape(LocalDomain)}' " +
                              $"to='{XmlEscaping.Escape(RemoteDomain!)}'>" +
                              DialbackKey.Generate(secret, RemoteDomain!, LocalDomain, StreamId) +
                              "</db:result>",
                              cancellationToken);

                return true;

            }

            // Receiver: without a 'from' we do not know who the peer wants to
            // speak for, and the sender check would have nothing to hold on to.
            if (String.IsNullOrEmpty(from))
            {
                await SendStreamErrorAsync("improper-addressing",
                                           "The <open/> is missing its 'from'.",
                                           cancellationToken);
                return false;
            }

            // RFC 6120, section 4.9.3.6: a 'to' this server does not serve is
            // <host-unknown/>.
            if (to is not null &&
                !String.Equals(to, LocalDomain, StringComparison.OrdinalIgnoreCase))
            {
                await SendStreamErrorAsync("host-unknown",
                                           $"This server serves '{LocalDomain}', not '{to}'.",
                                           cancellationToken);
                return false;
            }

            RemoteDomain  = from;

            var streamId  = Guid.NewGuid().ToString("N");

            await sendFrame(framing.StreamOpen(LocalDomain, from, streamId),
                            cancellationToken);

            // RFC 6120, section 4.3.2 demands the features. Dialback is
            // demanded independently of whether it stands announced here,
            // though - an announcement one relies on could simply be left out
            // by an attacker.
            // Only offer SASL-EXTERNAL when a certificate of the peer is on
            // hand that could be checked at all - otherwise the offer would be
            // an invitation into a dead end.
            var offersExternal = externalIdentity is not null && !IsAuthenticated;

            await sendFrame(
                      $"<stream:features xmlns:stream='{StreamNamespace}'>" +
                      (offersExternal
                           ? $"<mechanisms xmlns='{SaslNamespace}'><mechanism>EXTERNAL</mechanism></mechanisms>"
                           : "") +
                      (RequiresDialback && !IsAuthenticated
                           ? "<dialback xmlns='urn:xmpp:features:dialback'><required/></dialback>"
                           : "") +
                      // XEP-0288, section 3: it is announced before *and* after
                      // TLS. If bidi is already negotiated, the announcement is
                      // omitted - a second <bidi/> would have nothing left to
                      // say.
                      //
                      // Two forms, and the second is an imposition with
                      // evidence: ejabberd 24.12 does not pick up the form of
                      // the XEP. Its accepting side itself announces
                      // urn:xmpp:bidi (see AnnouncesBidi), and its establishing
                      // side apparently looks for the same. If we announce only
                      // the XEP form, it does not take our return direction -
                      // observed, not assumed: with both forms it takes it.
                      //
                      // In P6 the counter-thesis stood here, concluded from
                      // ejabberd's *master*, where it is fixed. The version
                      // shipped behaves differently, and that is what matters.
                      //
                      // On the wire this is unambiguous: the enabling element
                      // is called urn:xmpp:bidi in both readings, so there is
                      // only one answer. Whoever knows only the XEP form passes
                      // over the second element as an unknown feature.
                      (bidi && !BidiEnabled
                           ? $"<bidi xmlns='{BidiFeatureNamespace}'/>" +
                             $"<bidi xmlns='{BidiNamespace}'/>"
                           : "") +
                      "</stream:features>",
                      cancellationToken);

            MarkOpen(streamId);

            return true;

        }

        #endregion

        #region SASL-EXTERNAL (RFC 6120, section 6; XEP-0178)

        /// <summary>
        /// The namespace of the SASL negotiation.
        /// </summary>
        public const String SaslNamespace = "urn:ietf:params:xml:ns:xmpp-sasl";

        /// <summary>
        /// The features of the peer - here the establishing server decides
        /// whether it attempts SASL-EXTERNAL or falls back on dialback.
        /// </summary>
        private async Task<Boolean> ProcessFeaturesAsync(String             frame,
                                                         CancellationToken  cancellationToken)
        {

            if (!IsInitiator || IsAuthenticated)
                return true;

            // XEP-0288, section 4: the <bidi/> goes out *before* SASL or
            // dialback. Afterwards it would be too late - the peer has decided
            // by then how it answers.
            //
            // After TLS it is here anyway: this stream only exists once the
            // transport has the encryption behind it (XEP-0288 demands exactly
            // this order).
            if (bidi && !BidiEnabled && AnnouncesBidi(frame))
            {
                await sendFrame($"<bidi xmlns='{BidiNamespace}'/>", cancellationToken);
                BidiEnabled = true;
            }

            var offersExternal = frame.Contains(SaslNamespace, StringComparison.Ordinal) &&
                                 frame.Contains("EXTERNAL",    StringComparison.Ordinal);

            if (canOfferExternal && offersExternal)
            {

                // RFC 6120, section 6.4.2: the authzid is the identity that is
                // to be spoken for - base64, like every SASL payload.
                var authzid = Convert.ToBase64String(
                                  System.Text.Encoding.UTF8.GetBytes(LocalDomain));

                await sendFrame(
                          $"<auth xmlns='{SaslNamespace}' mechanism='EXTERNAL'>{authzid}</auth>",
                          cancellationToken);

                return true;

            }

            // No EXTERNAL - then the other way, if it is intended.
            if (RequiresDialback && secret is not null && StreamId is not null)
                await sendFrame(
                          $"<db:result xmlns:db='{DialbackKey.Namespace}' " +
                          $"from='{XmlEscaping.Escape(LocalDomain)}' " +
                          $"to='{XmlEscaping.Escape(RemoteDomain!)}'>" +
                          DialbackKey.Generate(secret, RemoteDomain!, LocalDomain, StreamId) +
                          "</db:result>",
                          cancellationToken);

            return true;

        }

        /// <summary>
        /// Does a bidi offer stand in these features?
        /// </summary>
        /// <remarks>
        /// XEP-0288 hands out two namespaces and means two different things by
        /// them: <see cref="BidiFeatureNamespace"/> for the announcement,
        /// <see cref="BidiNamespace"/> for the element the establishing server
        /// accepts it with. Announced is the first - Prosody keeps to that, and
        /// so do we.
        ///
        /// ejabberd 24.12 does not: its accepting side puts the
        /// <i>enabling</i> element into the features. Upstream that is fixed by
        /// now, in the versions shipped it still stands, and they are
        /// numerous.
        ///
        /// Hence both forms here - but only when reading. What we announce
        /// ourselves stays the form of the XEP; ejabberd's establishing side
        /// looks for exactly that one and understands us. Whoever stayed strict
        /// when reading would get no error but a connection that is silently
        /// one-sided - and whose answers then hang at a firewall, for no reason
        /// visible in the protocol.
        /// </remarks>
        private static Boolean AnnouncesBidi(String features)

            => features.Contains(BidiFeatureNamespace, StringComparison.Ordinal) ||
               features.Contains(BidiNamespace,        StringComparison.Ordinal);

        /// <summary>
        /// <c>&lt;auth mechanism='EXTERNAL'/&gt;</c> on the accepting side: the
        /// certificate has to cover the domain claimed.
        /// </summary>
        /// <remarks>
        /// Here lies the whole difference to dialback. There the domain is
        /// proven by asking back at an address on record; here by reading the
        /// certificate presented in the TLS handshake. No second connection -
        /// but then everything hangs on
        /// <see cref="CertificateIdentity"/>.
        ///
        /// An empty authzid (<c>=</c>) is permissible and means: take the
        /// identity from the certificate. Because a certificate can hold for
        /// several domains, it is related here to the <c>from</c> of the stream
        /// header - there would be no other choice without guessing.
        /// </remarks>
        /// <summary>
        /// RFC 6120, section 6.4.4: The peer breaks the SASL negotiation off.
        /// </summary>
        /// <remarks>
        /// An intended step and no protocol violation - hence a SASL failure
        /// and not a stream error, and hence the stream stays standing. There
        /// is nothing to discard here: SASL-EXTERNAL is a single move, there is
        /// no half exchange as with SCRAM.
        ///
        /// This gap came into being in D27: before the strictness an
        /// <c>&lt;abort/&gt;</c> stayed lying here, afterwards it ended the
        /// stream. Whoever makes a switch strict inherits every answer it does
        /// not know yet.
        /// </remarks>
        private async Task<Boolean> ProcessSaslAbortAsync(CancellationToken cancellationToken)
        {

            // Whoever dialled themselves gets no abort sent to them - they
            // would be the one sending it.
            if (IsInitiator)
                return false;

            await sendFrame($"<failure xmlns='{SaslNamespace}'><aborted/></failure>",
                            cancellationToken);

            return true;

        }

        private async Task<Boolean> ProcessSaslAuthAsync(String             frame,
                                                         CancellationToken  cancellationToken)
        {

            if (IsInitiator)
                return false;

            var mechanism = Attr(frame, "mechanism");

            if (externalIdentity is null || mechanism != "EXTERNAL")
            {

                await sendFrame(
                          $"<failure xmlns='{SaslNamespace}'><invalid-mechanism/></failure>",
                          cancellationToken);

                return true;

            }

            var claimed = RemoteDomain;
            var payload = Body(frame);

            if (payload is not null && payload != "=")
            {

                try
                {
                    claimed = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                }
                catch (FormatException)
                {

                    await sendFrame(
                              $"<failure xmlns='{SaslNamespace}'><incorrect-encoding/></failure>",
                              cancellationToken);

                    return true;

                }

            }

            // Whoever identifies themselves as a domain other than the one the
            // stream header names gets nothing - otherwise the stream could be
            // rewritten onto a second identity after the fact.
            if (claimed is null ||
                !String.Equals(claimed, RemoteDomain, StringComparison.OrdinalIgnoreCase) ||
                !externalIdentity(claimed))
            {

                await sendFrame(
                          $"<failure xmlns='{SaslNamespace}'><not-authorized/></failure>",
                          cancellationToken);

                OnStanzaRefused?.Invoke($"SASL-EXTERNAL for '{claimed ?? "(none)"}' refused");

                return true;

            }

            // The order counts: first note the restart down, then the
            // identification. The other way round the stream would report
            // itself usable for a moment although its new header is still
            // outstanding.
            ReopenForRestart();
            MarkAuthenticated("SASL-EXTERNAL");

            await sendFrame($"<success xmlns='{SaslNamespace}'/>", cancellationToken);

            return true;

        }

        /// <summary>
        /// <c>&lt;success/&gt;</c> on the establishing side: open the stream
        /// anew (RFC 6120, section 6.4.6).
        /// </summary>
        private async Task<Boolean> ProcessSaslSuccessAsync(CancellationToken cancellationToken)
        {

            if (!IsInitiator)
                return false;

            ReopenForRestart();
            MarkAuthenticated("SASL-EXTERNAL");

            await sendFrame(framing.StreamOpen(LocalDomain, RemoteDomain, null), cancellationToken);

            return true;

        }

        /// <summary>
        /// <c>&lt;failure/&gt;</c>: SASL has failed. A fallback to dialback
        /// does <b>not</b> take place.
        /// </summary>
        /// <remarks>
        /// That is a decision and no omission. Whoever wanted to identify
        /// themselves by certificate and was refused has a problem that a
        /// second attempt with a weaker procedure does not solve - it only
        /// covers it up. RFC 6120, section 6.4.5 does permit further attempts;
        /// here the stream ends.
        /// </remarks>
        private async Task<Boolean> ProcessSaslFailureAsync(CancellationToken cancellationToken)
        {

            await SendStreamErrorAsync("not-authorized",
                                       "SASL-EXTERNAL was refused.",
                                       cancellationToken);

            return true;

        }

        #endregion

        #region (private) ProcessDialbackResultAsync(frame, CancellationToken)

        /// <summary>
        /// <c>&lt;db:result/&gt;</c> - XEP-0220, step 1 at the receiver and
        /// step 4 at the establishing side.
        /// </summary>
        private async Task<Boolean> ProcessDialbackResultAsync(String             frame,
                                                               CancellationToken  cancellationToken)
        {

            var type = Attr(frame, "type");

            // Step 4: the answer to one's own key.
            if (IsInitiator)
            {

                if (type == "valid")
                {
                    MarkAuthenticated();
                    return true;
                }

                // XEP-0220, section 2.1.3: without a valid dialback nothing may
                // run over this stream.
                await SendStreamErrorAsync(
                          "not-authorized",
                          $"The peer refused the dialback key with '{type ?? "(without a type)"}'.",
                          cancellationToken);

                return true;

            }

            // Step 1: the peer presents its key.
            if (verifyKey is null)
            {
                // This stream demands no dialback - then it cannot check it
                // either and does not act as though it had.
                return true;
            }

            var senderDomain  = Attr(frame, "from");
            var targetDomain  = Attr(frame, "to");
            var key           = Body(frame);

            if (senderDomain is null || key is null)
            {
                await SendStreamErrorAsync("improper-addressing",
                                           "The <db:result/> is missing its 'from' or the key.",
                                           cancellationToken);
                return false;
            }

            // The peer must not identify itself as a domain other than the one
            // it named in the <open/> - otherwise a second identity could be
            // pushed in after the fact over a stream once established.
            if (!String.Equals(senderDomain, RemoteDomain, StringComparison.OrdinalIgnoreCase))
            {
                await SendStreamErrorAsync("invalid-from",
                                           $"The stream belongs to '{RemoteDomain}', not to '{senderDomain}'.",
                                           cancellationToken);
                return false;
            }

            if (targetDomain is not null &&
                !String.Equals(targetDomain, LocalDomain, StringComparison.OrdinalIgnoreCase))
            {
                await SendStreamErrorAsync("host-unknown",
                                           $"This server serves '{LocalDomain}', not '{targetDomain}'.",
                                           cancellationToken);
                return false;
            }

            var valid = false;

            try
            {
                valid = await verifyKey(senderDomain, StreamId ?? "", key);
            }
            catch (Exception)
            {
                // The authoritative server was not reachable. XEP-0220,
                // section 2.4 names <remote-server-timeout/> for that; here
                // "not valid" suffices, the answer below says it.
            }

            await sendFrame(
                      $"<db:result xmlns:db='{DialbackKey.Namespace}' " +
                      $"from='{XmlEscaping.Escape(LocalDomain)}' " +
                      $"to='{XmlEscaping.Escape(senderDomain)}' " +
                      $"type='{(valid ? "valid" : "invalid")}'/>",
                      cancellationToken);

            if (valid)
                MarkAuthenticated();

            else
                OnStanzaRefused?.Invoke($"Dialback for '{senderDomain}' failed");

            return true;

        }

        #endregion

        #region (private) ProcessDialbackVerifyAsync(frame, CancellationToken)

        /// <summary>
        /// <c>&lt;db:verify/&gt;</c> - XEP-0220, steps 2 and 3 in the role of
        /// the authoritative server.
        /// </summary>
        /// <remarks>
        /// Here the server recomputes whether <b>it itself</b> could have
        /// issued this key. It remembers nothing for that: from the target
        /// domain, its own domain and the stream ID the key follows anew every
        /// time. An attacker who gives themselves out as this domain fails
        /// because the question never reaches them - it goes to the address the
        /// checking server has on record for this domain.
        /// </remarks>
        private async Task<Boolean> ProcessDialbackVerifyAsync(String             frame,
                                                               CancellationToken  cancellationToken)
        {

            var type = Attr(frame, "type");

            // Step 3: the answer to one's own query.
            if (type is not null)
            {

                verificationAnswer?.TrySetResult(type == "valid");

                return true;

            }

            // Step 2: somebody asks about a key we are supposed to have issued.
            var targetDomain  = Attr(frame, "from");
            var ownDomain     = Attr(frame, "to");
            var streamId      = Attr(frame, "id");
            var key           = Body(frame);

            if (targetDomain is null || streamId is null || key is null)
            {
                await SendStreamErrorAsync("improper-addressing",
                                           "The <db:verify/> is missing its 'from', 'id' or the key.",
                                           cancellationToken);
                return false;
            }

            if (ownDomain is not null &&
                !String.Equals(ownDomain, LocalDomain, StringComparison.OrdinalIgnoreCase))
            {
                await SendStreamErrorAsync("host-unknown",
                                           $"This server serves '{LocalDomain}', not '{ownDomain}'.",
                                           cancellationToken);
                return false;
            }

            var valid = secret is not null &&
                        DialbackKey.Verify(secret, targetDomain, LocalDomain, streamId, key);

            await sendFrame(
                      $"<db:verify xmlns:db='{DialbackKey.Namespace}' " +
                      $"from='{XmlEscaping.Escape(LocalDomain)}' " +
                      $"to='{XmlEscaping.Escape(targetDomain)}' " +
                      $"id='{XmlEscaping.Escape(streamId)}' " +
                      $"type='{(valid ? "valid" : "invalid")}'/>",
                      cancellationToken);

            return true;

        }

        #endregion

        #region RequestVerificationAsync(targetDomain, streamId, key, Timeout, CancellationToken)

        /// <summary>
        /// Asks the authoritative server whether it issued this key (XEP-0220,
        /// step 2).
        /// </summary>
        /// <param name="targetDomain">
        /// The domain of the accepting server - that is, our own. It goes out
        /// as the <c>from</c>, as the normative text on step 2 demands.
        /// </param>
        /// <param name="streamId">The stream ID the key is bound to.</param>
        /// <param name="key">The key that was presented.</param>
        public async Task<Boolean> RequestVerificationAsync(String             targetDomain,
                                                            String             streamId,
                                                            String             key,
                                                            TimeSpan           Timeout,
                                                            CancellationToken  cancellationToken = default)
        {

            verificationAnswer = new TaskCompletionSource<Boolean>(
                                     TaskCreationOptions.RunContinuationsAsynchronously);

            await sendFrame(
                      $"<db:verify xmlns:db='{DialbackKey.Namespace}' " +
                      $"from='{XmlEscaping.Escape(targetDomain)}' " +
                      $"to='{XmlEscaping.Escape(RemoteDomain!)}' " +
                      $"id='{XmlEscaping.Escape(streamId)}'>" +
                      key +
                      "</db:verify>",
                      cancellationToken);

            try
            {
                return await verificationAnswer.Task.WaitAsync(Timeout, cancellationToken);
            }
            catch (Exception)
            {
                return false;
            }

        }

        private TaskCompletionSource<Boolean>? verificationAnswer;

        #endregion

        #region (private static) IsDialback(frame, name) / Attr(xml, name) / Body(xml)

        /// <summary>
        /// Is the frame a dialback element of the given name?
        /// </summary>
        /// <remarks>
        /// XEP-0220 writes the prefix <c>db:</c> throughout; the variant with a
        /// default namespace is recognised all the same, because it is just as
        /// valid.
        /// </remarks>
        private static Boolean IsDialback(String frame, String name)

            => frame.StartsWith($"<db:{name}", StringComparison.Ordinal) ||
               (frame.StartsWith($"<{name}", StringComparison.Ordinal) &&
                frame.Contains(DialbackKey.Namespace, StringComparison.Ordinal));

        /// <summary>
        /// Reads an attribute out of a frame.
        /// </summary>
        /// <remarks>
        /// Through a regular expression and not through
        /// <see cref="XElement.Parse(String)"/>: the dialback elements carry a
        /// prefix, and whether the peer declares it on the element itself is up
        /// to them. Over TCP the declaration hangs on the stream root and the
        /// frame would not be well-formed on its own at all - this layer has to
        /// bear that, it is meant to carry both framings after all.
        /// </remarks>
        internal static String? Attr(String xml, String name)
        {

            var m = Regex.Match(xml, $@"\b{name}\s*=\s*['""]([^'""]*)['""]");

            return m.Success && m.Groups[1].Value.Length > 0
                       ? m.Groups[1].Value
                       : null;

        }

        /// <summary>
        /// The text content of a frame, without surrounding whitespace.
        /// </summary>
        private static String? Body(String xml)
        {

            var m = Regex.Match(xml, @">([^<>]*)<\s*/");

            return m.Success && m.Groups[1].Value.Trim().Length > 0
                       ? m.Groups[1].Value.Trim()
                       : null;

        }

        #endregion

        #region (private) ProcessBidi(frame)

        /// <summary>
        /// XEP-0288, section 4: the peer enables the return direction.
        /// </summary>
        /// <remarks>
        /// Only the receiver takes a <c>&lt;bidi/&gt;</c> in. At the initiator
        /// it would be the wrong way round - it sent one itself, and one coming
        /// back would mean the peer wanted to enable something over <i>our</i>
        /// outgoing stream in its turn, which the section does not provide for.
        ///
        /// It is taken in even when the announcement was not asked for at all
        /// (<c>bidi</c> off): then it is <b>not</b> enabled. An attacker could
        /// otherwise force a return direction this server never offered.
        /// </remarks>
        private Boolean ProcessBidi(String frame)
        {

            if (IsInitiator || !frame.Contains(BidiNamespace, StringComparison.Ordinal))
                return false;

            if (!bidi)
            {
                OnStanzaRefused?.Invoke("<bidi/> without an announcement");
                return false;
            }

            BidiEnabled = true;

            return true;

        }

        #endregion

        #region SendStanzaOverBidiAsync(stanza, CancellationToken)

        /// <summary>
        /// Sends a stanza over the return direction of an incoming stream
        /// (XEP-0288).
        /// </summary>
        /// <returns>
        /// false when this stream may not carry the return direction - then
        /// only the ordinary way over a connection of our own remains.
        /// </returns>
        /// <remarks>
        /// Two conditions from section 4, and both are safeguards, not
        /// formalities:
        /// <list type="bullet">
        ///   <item>
        ///     <i>"The receiving server MUST NOT send stanzas to the peer
        ///     before it has authenticated via SASL, or the peer's identity has
        ///     been verified via Server Dialback."</i> Whoever has not proven
        ///     who they are gets nothing either - otherwise someone else's post
        ///     could be collected with a mere claim.
        ///   </item>
        ///   <item>
        ///     <i>"The receiving server MUST only send stanzas for which it has
        ///     been authenticated - in the case of TLS/SASL based
        ///     authentication, this is the value of the stream's 'to'
        ///     attribute."</i> The <c>to</c> of the incoming stream header is
        ///     our own domain; speaking for another one would be just as wrong
        ///     here as the other way round.
        ///   </item>
        /// </list>
        /// </remarks>
        public async Task<Boolean> SendStanzaOverBidiAsync(String             stanza,
                                                           CancellationToken  cancellationToken = default)
        {

            lock (dataLock)
            {

                if (IsInitiator || !BidiEnabled || !IsOpen || IsClosed || !IsAuthenticated)
                    return false;

            }

            var from = Attr(stanza, "from");

            if (from is not null && !BelongsToLocalDomain(from))
            {
                OnStanzaRefused?.Invoke(
                    $"'{from}' does not belong to '{LocalDomain}' - not over the return direction");
                return false;
            }

            await sendFrame(stanza, cancellationToken);

            return true;

        }

        /// <summary>
        /// Searches among incoming streams for one that carries the return
        /// direction to this domain, and sends the stanza out there.
        /// </summary>
        /// <returns>true when one of them took it.</returns>
        /// <remarks>
        /// Here and not in the transports, although both need the same thing:
        /// the matching of the domain is the place where a stanza can end up at
        /// the wrong peer, and two versions of it would be two opportunities
        /// for that. In the first mutation run exactly this rule slipped
        /// through - it had no test, because only one peer hung on every setup.
        /// </remarks>
        internal static async Task<Boolean> TryDeliverOverBidiAsync(IEnumerable<S2SStream>  inboundStreams,
                                                                    String                  remoteDomain,
                                                                    String                  stanza,
                                                                    CancellationToken       cancellationToken = default)
        {

            foreach (var stream in inboundStreams)
            {

                if (!String.Equals(stream.RemoteDomain, remoteDomain, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Whether the stream may carry the return direction at all it
                // decides itself - there stand the conditions from XEP-0288,
                // section 4.
                if (await stream.SendStanzaOverBidiAsync(stanza, cancellationToken))
                    return true;

            }

            return false;

        }

        private Boolean BelongsToLocalDomain(String jid)
        {

            var at      = jid.IndexOf('@');
            var domain  = at >= 0 ? jid[(at + 1)..] : jid;
            var slash   = domain.IndexOf('/');

            if (slash >= 0)
                domain = domain[..slash];

            return String.Equals(domain, LocalDomain, StringComparison.OrdinalIgnoreCase);

        }

        #endregion

        #region (private) ProcessStanzaAsync(stanza, CancellationToken)

        private async Task<Boolean> ProcessStanzaAsync(String             stanza,
                                                       CancellationToken  cancellationToken)
        {

            if (!IsOpen)
            {
                // RFC 6120, section 4.9.3.12: stanzas before the stream header
                // do not exist.
                await SendStreamErrorAsync("not-well-formed",
                                           "A stanza before the <open/>.",
                                           cancellationToken);
                return false;
            }

            // An outgoing stream carries in one direction only (RFC 6120,
            // section 4.1) - unless XEP-0288 is negotiated. Then *we* asked for
            // the return direction, and what comes over it belongs here.
            if (IsInitiator && !BidiEnabled)
            {
                OnStanzaRefused?.Invoke("A stanza on an outgoing stream");
                return false;
            }

            if (deliverStanza is null)
            {
                OnStanzaRefused?.Invoke("No recipient for incoming stanzas");
                return false;
            }

            // XEP-0220, section 1: until the identity is proven, no stanza is
            // processed over the connection. That is the line that makes
            // dialback a safeguard in the first place - without it the exchange
            // would run along without deciding anything.
            if (RequiresDialback && !IsAuthenticated)
            {
                OnStanzaRefused?.Invoke("A stanza before dialback was completed");
                return false;
            }

            var result = await deliverStanza(RemoteDomain!, stanza);

            if (result == RemoteStanzaResult.Accepted)
                return true;

            OnStanzaRefused?.Invoke(result.ToString());

            // RFC 6120, section 8.1.1.1: with a 'from' the peer may not speak
            // for, the stream ends. The reason is not strictness for its own
            // sake - whoever writes once in the name of a foreign domain does
            // it again on the next attempt, and a single discarded stanza would
            // not stop them. The other refusals concern only the one stanza.
            // A 'from' that is not a JID at all belongs in the same line:
            // section 8.1.1.1 calls both invalid, and the reason carries just
            // as well - whoever sends something without an address once does it
            // again.
            if (result is RemoteStanzaResult.ForeignSender
                       or RemoteStanzaResult.MalformedSender)
                await SendStreamErrorAsync("invalid-from",
                                           result == RemoteStanzaResult.ForeignSender
                                               ? $"'{RemoteDomain}' must not speak for a foreign domain."
                                               : "The 'from' of the stanza is not a JID.",
                                           cancellationToken);

            return false;

        }

        #endregion

        #region Abort(reason)

        /// <summary>
        /// Ends the stream without sending a frame - for the case that the
        /// transport itself is already gone and a <c>&lt;close/&gt;</c> would
        /// reach nobody anyway.
        /// </summary>
        internal void Abort(String? reason)
            => MarkClosed(reason);

        #endregion

        #region (private) MarkOpen(streamId) / MarkClosed(reason)

        private void MarkOpen(String? streamId)
        {

            lock (dataLock)
            {

                if (IsOpen || IsClosed)
                    return;


                StreamId  = streamId;
                IsOpen    = true;

            }

            openHandshake.TrySetResult();

            if (IsAuthenticated || !RequiresDialback)
                ready.TrySetResult();

        }

        private void MarkAuthenticated(String by = "Dialback")
        {

            lock (dataLock)
            {

                if (IsAuthenticated || IsClosed)
                    return;

                IsAuthenticated  = true;
                AuthenticatedBy  = by;

            }

            dialbackDone.TrySetResult();

            // Only when the stream is not starting over just now - otherwise it
            // reports itself usable while its header is still outstanding.
            if (IsOpen)
                ready.TrySetResult();

        }

        /// <summary>
        /// Resets the stream to "not opened yet" without giving up what has
        /// been reached.
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 6.4.6: after a successful SASL the stream begins
        /// over - a new stream header, a new stream ID. What is <b>not</b>
        /// reset is the finding of who the peer is: that comes from the
        /// certificate and not from the stream, and asking for it once more
        /// would mean letting it be guessed once more.
        /// </remarks>
        private void ReopenForRestart()
        {

            lock (dataLock)
            {

                if (IsClosed)
                    return;

                IsOpen    = false;
                StreamId  = null;

            }

            OnRestart?.Invoke();

        }

        private void MarkClosed(String? reason)
        {

            lock (dataLock)
            {

                if (IsClosed)
                    return;

                IsClosed  = true;
                IsOpen    = false;

            }

            // Whoever waits for the handshake shall not run into the time limit
            // when it is already settled that it is not coming any more. The
            // same holds for dialback and for an open verification query.
            openHandshake.TrySetCanceled();
            dialbackDone.TrySetCanceled();
            ready.TrySetCanceled();
            verificationAnswer?.TrySetResult(false);

            OnClosed?.Invoke(reason);

        }

        #endregion


        public override String ToString()

            => $"{(IsInitiator ? "→" : "←")} {LocalDomain} / {RemoteDomain ?? "(unknown)"}" +
               (IsClosed ? " (ended)" : IsOpen ? " (open)" : " (being established)");

    }

}
