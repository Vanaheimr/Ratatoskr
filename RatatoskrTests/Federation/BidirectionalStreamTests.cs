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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0288: both directions over one connection - the protocol layer,
    /// without a transport underneath.
    /// </summary>
    /// <remarks>
    /// Without the extension an S2S connection is one-sided (RFC 6120,
    /// section 4.1): whoever gets a stanza answers it over a connection of
    /// their own to the sender domain. That presupposes that they can reach the
    /// far end. If they cannot - behind NAT, behind a firewall, without a DNS
    /// record -, the answer is lost, and silently at that.
    ///
    /// As in <see cref="S2SStreamTests"/> the handshakes are built by hand. To
    /// let two instances run against each other would only check that the class
    /// fits itself.
    /// </remarks>
    [TestFixture]
    public class BidirectionalStreamTests
    {

        #region Data

        private List<String> _sent = null!;

        #endregion

        #region SetUp / helper functions

        [SetUp]
        public void Clear()
        {
            _sent = [];
        }

        private Task Send(String frame, CancellationToken _)
        {
            _sent.Add(frame);
            return Task.CompletedTask;
        }

        private static String OpenFrom(String from, String? to = "right.example", String? id = null)

            => $"<open xmlns='urn:ietf:params:xml:ns:xmpp-framing' from='{from}'" +
               (to is not null ? $" to='{to}'" : "") +
               (id is not null ? $" id='{id}'" : "") +
               " version='1.0'/>";

        /// <summary>
        /// The features of the recipient, optionally with a bidi announcement.
        /// </summary>
        private static String FeaturesWith(Boolean bidi, Boolean external = true)

            => "<stream:features xmlns:stream='http://etherx.jabber.org/streams'>" +
               (external
                    ? $"<mechanisms xmlns='{S2SStream.SaslNamespace}'><mechanism>EXTERNAL</mechanism></mechanisms>"
                    : "") +
               (bidi ? $"<bidi xmlns='{S2SStream.BidiFeatureNamespace}'/>" : "") +
               "</stream:features>";

        private Boolean WasSent(String contains)
            => _sent.Any(f => f.Contains(contains, StringComparison.Ordinal));

        private Int32 IndexOf(String contains)
            => _sent.FindIndex(f => f.Contains(contains, StringComparison.Ordinal));

        #endregion


        #region TheReceiverAnnouncesBidi()

        /// <summary>
        /// XEP-0288, section 3: whoever masters it announces it in the features
        /// - in both forms that occur in the wild.
        /// </summary>
        /// <remarks>
        /// The XEP form (<c>urn:xmpp:features:bidi</c>) is the right one, and
        /// Prosody picks up precisely that one. ejabberd 24.12 does not: it
        /// announces <c>urn:xmpp:bidi</c> itself and evidently looks for the
        /// same. Without the second form it does not take our return direction
        /// - observed in <c>ThePeerTakesTheReturnPathWeOffered</c>, not
        /// surmised.
        ///
        /// On the wire it stays unambiguous: the enabling element is called
        /// <c>urn:xmpp:bidi</c> in both readings, so only one answer comes
        /// back.
        /// </remarks>
        [Test]
        public async Task TheReceiverAnnouncesBidi()
        {

            var stream = S2SStream.Accept("right.example",
                                          Send,
                                          (_, _) => Task.FromResult(RemoteStanzaResult.Accepted),
                                          offerBidi: true);

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            Assert.Multiple(() =>
            {

                Assert.That(WasSent($"<bidi xmlns='{S2SStream.BidiFeatureNamespace}'/>"), Is.True,
                            "The form of the XEP is missing.");

                Assert.That(WasSent($"<bidi xmlns='{S2SStream.BidiNamespace}'/>"), Is.True,
                            "The form ejabberd 24.12 looks for is missing.");

            });

        }

        #endregion

        #region WithoutTheSwitch_NothingIsAnnounced()

        /// <summary>
        /// Switched off nothing is announced - and without an announcement the
        /// far end must not use it.
        /// </summary>
        [Test]
        public async Task WithoutTheSwitch_NothingIsAnnounced()
        {

            var stream = S2SStream.Accept("right.example",
                                          Send,
                                          (_, _) => Task.FromResult(RemoteStanzaResult.Accepted));

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            Assert.Multiple(() =>
            {
                Assert.That(WasSent(S2SStream.BidiFeatureNamespace), Is.False);
                Assert.That(stream.BidiEnabled, Is.False);
            });

        }

        #endregion

        #region AnUnannouncedBidi_IsRefused()

        /// <summary>
        /// A <c>&lt;bidi/&gt;</c> without a previous announcement enables
        /// nothing.
        /// </summary>
        /// <remarks>
        /// Otherwise a return direction could be forced that this server never
        /// offered - and over which it would subsequently send out stanzas that
        /// it would otherwise have sent over a connection of its own, a checked
        /// one.
        /// </remarks>
        [Test]
        public async Task AnUnannouncedBidi_IsRefused()
        {

            var stream = S2SStream.Accept("right.example",
                                          Send,
                                          (_, _) => Task.FromResult(RemoteStanzaResult.Accepted));

            await stream.ProcessFrameAsync(OpenFrom("left.example"));

            var accepted = await stream.ProcessFrameAsync(
                               $"<bidi xmlns='{S2SStream.BidiNamespace}'/>");

            Assert.Multiple(() =>
            {
                Assert.That(accepted,           Is.False);
                Assert.That(stream.BidiEnabled, Is.False);
            });

        }

        #endregion

        #region TheInitiatorAsksForBidi_OnlyWhenOffered()

        /// <summary>
        /// Section 4: the building server sends the <c>&lt;bidi/&gt;</c> - but
        /// only if the far end has offered it.
        /// </summary>
        [Test]
        public async Task TheInitiatorAsksForBidi_OnlyWhenOffered()
        {

            var withOffer = S2SStream.Initiate("left.example", "right.example", Send,
                                               canOfferExternal: true, useBidi: true);

            await withOffer.ProcessFrameAsync(OpenFrom("right.example", "left.example", "abc"));
            await withOffer.ProcessFrameAsync(FeaturesWith(bidi: true));

            Assert.That(WasSent($"<bidi xmlns='{S2SStream.BidiNamespace}'/>"), Is.True,
                        "Offered and not requested.");
            Assert.That(withOffer.BidiEnabled, Is.True);

            _sent.Clear();

            var withoutOffer = S2SStream.Initiate("left.example", "right.example", Send,
                                                  canOfferExternal: true, useBidi: true);

            await withoutOffer.ProcessFrameAsync(OpenFrom("right.example", "left.example", "abc"));
            await withoutOffer.ProcessFrameAsync(FeaturesWith(bidi: false));

            Assert.Multiple(() =>
            {
                Assert.That(WasSent(S2SStream.BidiNamespace), Is.False,
                            "Not offered and requested nevertheless.");
                Assert.That(withoutOffer.BidiEnabled, Is.False);
            });

        }

        #endregion

        #region AnOfferInTheEnableNamespace_IsUnderstood()

        /// <summary>
        /// An announcement in the namespace of the enabling element is read as
        /// an offer as well.
        /// </summary>
        /// <remarks>
        /// XEP-0288 hands out two namespaces: <c>urn:xmpp:features:bidi</c> for
        /// the announcement in the features, <c>urn:xmpp:bidi</c> for the
        /// element with which the building server takes it. Prosody keeps to
        /// that.
        ///
        /// ejabberd 24.12 does not: its accepting side puts the
        /// <i>enabling</i> element into the features, so it announces
        /// <c>&lt;bidi xmlns='urn:xmpp:bidi'/&gt;</c>. Upstream that has been
        /// fixed by now - in the shipped versions it still stands.
        ///
        /// That is not an error we go along with: we go on announcing the form
        /// of the XEP, and ejabberd's building side looks for precisely that
        /// one (its codec maps both forms onto separate types). Only in the
        /// <b>reading</b> of a foreign offer are we lenient - otherwise the
        /// return direction would be lost against every one of these servers,
        /// and what would be left is a connection that is silently one-sided.
        /// </remarks>
        [Test]
        public async Task AnOfferInTheEnableNamespace_IsUnderstood()
        {

            var stream = S2SStream.Initiate("left.example", "right.example", Send,
                                            canOfferExternal: true, useBidi: true);

            await stream.ProcessFrameAsync(OpenFrom("right.example", "left.example", "abc"));

            // Literally what ejabberd 24.12 sends.
            await stream.ProcessFrameAsync(
                      "<stream:features xmlns:stream='http://etherx.jabber.org/streams'>" +
                      $"<mechanisms xmlns='{S2SStream.SaslNamespace}'><mechanism>EXTERNAL</mechanism></mechanisms>" +
                      "<dialback xmlns='urn:xmpp:features:dialback'><errors/></dialback>" +
                      $"<bidi xmlns='{S2SStream.BidiNamespace}'/>" +
                      "</stream:features>");

            Assert.Multiple(() =>
            {

                Assert.That(WasSent($"<bidi xmlns='{S2SStream.BidiNamespace}'/>"), Is.True,
                            "The offer stood there, only in the other namespace.");

                Assert.That(stream.BidiEnabled, Is.True);

            });

        }

        #endregion

        #region BidiGoesOutBeforeTheAuthentication()

        /// <summary>
        /// Section 4: <i>"This SHOULD be done before either SASL negotiation
        /// or Server Dialback."</i>
        /// </summary>
        /// <remarks>
        /// The order is no blemish. The far end decides at the conclusion of
        /// the authentication how it answers from then on; a
        /// <c>&lt;bidi/&gt;</c> afterwards would come too late and would let it
        /// build a connection of its own.
        /// </remarks>
        [Test]
        public async Task BidiGoesOutBeforeTheAuthentication()
        {

            var stream = S2SStream.Initiate("left.example", "right.example", Send,
                                            canOfferExternal: true, useBidi: true);

            await stream.ProcessFrameAsync(OpenFrom("right.example", "left.example", "abc"));
            await stream.ProcessFrameAsync(FeaturesWith(bidi: true));

            var bidi = IndexOf(S2SStream.BidiNamespace);
            var auth = IndexOf("<auth");

            Assert.Multiple(() =>
            {
                Assert.That(bidi, Is.GreaterThanOrEqualTo(0), "No <bidi/> sent.");
                Assert.That(auth, Is.GreaterThanOrEqualTo(0), "No <auth/> sent.");
                Assert.That(bidi, Is.LessThan(auth),
                            "The <bidi/> has to go out before the authentication.");
            });

        }

        #endregion

        #region BidiAlsoGoesOutBeforeDialback()

        /// <summary>
        /// The same for the dialback path - the section names both.
        /// </summary>
        [Test]
        public async Task BidiAlsoGoesOutBeforeDialback()
        {

            var stream = S2SStream.Initiate("left.example", "right.example", Send,
                                            secret:  DialbackKey.NewSecret(),
                                            useBidi: true);

            await stream.ProcessFrameAsync(OpenFrom("right.example", "left.example", "abc"));
            await stream.ProcessFrameAsync(FeaturesWith(bidi: true, external: false));

            var bidi     = IndexOf(S2SStream.BidiNamespace);
            var dialback = IndexOf("<db:result");

            Assert.Multiple(() =>
            {
                Assert.That(bidi,     Is.GreaterThanOrEqualTo(0));
                Assert.That(dialback, Is.GreaterThanOrEqualTo(0));
                Assert.That(bidi,     Is.LessThan(dialback));
            });

        }

        #endregion

        #region TheInitiatorTakesStanzasOnlyOnceBidiIsEnabled()

        /// <summary>
        /// Without bidi an outbound stream carries only one direction; with
        /// bidi it takes in what comes back.
        /// </summary>
        [Test]
        public async Task TheInitiatorTakesStanzasOnlyOnceBidiIsEnabled()
        {

            var delivered = new List<String>();

            Task<RemoteStanzaResult> Deliver(String _, String stanza)
            {
                delivered.Add(stanza);
                return Task.FromResult(RemoteStanzaResult.Accepted);
            }

            var withoutBidi = S2SStream.Initiate("left.example", "right.example", Send,
                                                 deliverStanza: Deliver);

            await withoutBidi.ProcessFrameAsync(OpenFrom("right.example", "left.example", "abc"));

            var refused = await withoutBidi.ProcessFrameAsync(
                                     "<message from='juliet@right.example' to='romeo@left.example'/>");

            Assert.Multiple(() =>
            {
                Assert.That(refused, Is.False,
                            "Without bidi an outbound stream must take nothing in.");
                Assert.That(delivered, Is.Empty);
            });

            var withBidi = S2SStream.Initiate("left.example", "right.example", Send,
                                              canOfferExternal:  true,
                                         deliverStanza:     Deliver,
                                         useBidi:           true);

            await withBidi.ProcessFrameAsync(OpenFrom("right.example", "left.example", "abc"));
            await withBidi.ProcessFrameAsync(FeaturesWith(bidi: true));

            var accepted = await withBidi.ProcessFrameAsync(
                                    "<message from='juliet@right.example' to='romeo@left.example'/>");

            Assert.Multiple(() =>
            {
                Assert.That(accepted, Is.True);
                Assert.That(delivered, Has.Count.EqualTo(1));
            });

        }

        #endregion

        #region TheReturnPath_StaysShutBeforeAuthentication()

        /// <summary>
        /// Section 4: <i>"The receiving server MUST NOT send stanzas to the
        /// peer before it has authenticated via SASL, or the peer's identity
        /// has been verified via Server Dialback."</i>
        /// </summary>
        /// <remarks>
        /// Whoever has not yet established who they are gets nothing either.
        /// Without this line foreign mail could be fetched with a mere claim in
        /// the stream header - one builds a connection up, calls oneself
        /// <c>example.com</c>, asks for the return direction and waits.
        /// </remarks>
        [Test]
        public async Task TheReturnPath_StaysShutBeforeAuthentication()
        {

            var stream = S2SStream.Accept("right.example",
                                          Send,
                                          (_, _) => Task.FromResult(RemoteStanzaResult.Accepted),
                                          verifyKey:  (_, _, _) => Task.FromResult(true),
                                          offerBidi:  true);

            await stream.ProcessFrameAsync(OpenFrom("left.example"));
            await stream.ProcessFrameAsync($"<bidi xmlns='{S2SStream.BidiNamespace}'/>");

            Assert.That(stream.BidiEnabled,  Is.True);
            Assert.That(stream.IsAuthenticated, Is.False, "Set-up of the test: not identified yet.");

            var wentOut = await stream.SendStanzaOverBidiAsync(
                              "<message from='juliet@right.example' to='romeo@left.example'/>");

            Assert.That(wentOut, Is.False,
                        "Before the identification nothing may go out over the return direction.");

        }

        #endregion

        #region TheReturnPath_CarriesOnlyOurOwnDomain()

        /// <summary>
        /// Section 4: <i>"The receiving server MUST only send stanzas for
        /// which it has been authenticated - ... this is the value of the
        /// stream's 'to' attribute."</i>
        /// </summary>
        /// <remarks>
        /// The <c>to</c> of the inbound stream header is one's own domain. To
        /// speak for a foreign one would be just as wrong here as in the
        /// reverse direction, where we forbid it to the far end - and the
        /// section says expressly that the bidi return direction must not skip
        /// this check.
        /// </remarks>
        [Test]
        public async Task TheReturnPath_CarriesOnlyOurOwnDomain()
        {

            var stream = S2SStream.Accept("right.example",
                                          Send,
                                          (_, _) => Task.FromResult(RemoteStanzaResult.Accepted),
                                          verifyKey:  (_, _, _) => Task.FromResult(true),
                                          offerBidi:  true);

            await stream.ProcessFrameAsync(OpenFrom("left.example", id: "s1"));
            await stream.ProcessFrameAsync($"<bidi xmlns='{S2SStream.BidiNamespace}'/>");

            // Conclude the dialback, so that only the sender check can stand in
            // the way any more.
            await stream.ProcessFrameAsync(
                      $"<db:result xmlns:db='{DialbackKey.Namespace}' " +
                      "from='left.example' to='right.example'>whatever</db:result>");

            Assert.That(stream.IsAuthenticated, Is.True, "Set-up of the test: identified.");

            var ownDomain = await stream.SendStanzaOverBidiAsync(
                                "<message from='juliet@right.example' to='romeo@left.example'/>");

            var foreignDomain = await stream.SendStanzaOverBidiAsync(
                                    "<message from='eve@elsewhere.example' to='romeo@left.example'/>");

            Assert.Multiple(() =>
            {
                Assert.That(ownDomain, Is.True,  "One's own domain has to get through.");
                Assert.That(foreignDomain, Is.False, "A foreign domain does not.");
            });

        }

        #endregion

    }

}
