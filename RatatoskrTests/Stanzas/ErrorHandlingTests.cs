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

using System.Collections.Concurrent;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// Error handling in the interplay: a refused request must not look like a
    /// success to the caller.
    /// </summary>
    [TestFixture]
    public class ErrorHandlingTests : AXMPPTests
    {

        #region Helper functions

        private async Task<XMPPSession> SessionOfAsync(XMPPClient client)
        {

            await WaitFor(() => Server.SessionOf(client.FullJid.ToString()) is not null,
                          "the server session for the client");

            return Server.SessionOf(client.FullJid.ToString())!;

        }

        #endregion


        #region RejectedPing_ReturnsNullInsteadOfARoundTripTime()

        /// <summary>
        /// The clearest case: a ping refused with an <c>iq error</c> used to run
        /// through ProcessPong and delivered a measured round-trip time. A
        /// counterpart that does not support XEP-0199 at all thereby looked like
        /// an especially fast one.
        /// </summary>
        /// <remarks>
        /// The refusal is read on <c>OnPingError</c> and no longer on the general
        /// <c>OnStanzaError</c>, and that is the second thing checked here. An
        /// error belonging to a pending request is delivered to that request; the
        /// general event is what is left for stanzas belonging to nobody. Both at
        /// once made a caught error indistinguishable from an uncaught one -
        /// which is what it looked like in the console: a line about a refusal
        /// nobody needed to act on.
        /// </remarks>
        [Test]
        public async Task RejectedPing_ReturnsNullInsteadOfARoundTripTime()
        {

            Server.FailPings = true;

            var client = await ConnectClientAsync();

            StanzaError? reported  = null;
            StanzaError? general   = null;
            client.Connection.Ping!.OnPingError += (timestamp, sender, _, error, ct) => { reported = error; return Task.CompletedTask; };
            client.OnStanzaError                += (timestamp, sender, _, error, ct) => { general  = error; return Task.CompletedTask; };

            var rtt = await client.PingAsync();

            // PingAsync comes back as soon as the request is resolved; the event
            // is triggered immediately afterwards.
            await WaitFor(() => reported is not null, "the reported stanza error");

            Assert.Multiple(() =>
            {
                Assert.That(rtt, Is.Null,
                            "A refused ping must not deliver a round-trip time.");

                Assert.That(reported,            Is.Not.Null, "The error was not reported.");
                Assert.That(reported!.Condition, Is.EqualTo("service-unavailable"));
                Assert.That(reported!.Type,      Is.EqualTo(StanzaErrorType.Cancel));

                Assert.That(general, Is.Null,
                            "Whoever asked has been told; a second report through " +
                            "the general event makes a caught error look uncaught.");
            });

        }

        #endregion

        #region AcceptedPing_StillMeasuresARoundTripTime()

        /// <summary>
        /// Counter-check: the normal case has to go on working.
        /// </summary>
        [Test]
        public async Task AcceptedPing_StillMeasuresARoundTripTime()
        {

            var client = await ConnectClientAsync();

            var rtt = await client.PingAsync();

            Assert.That(rtt, Is.Not.Null);

        }

        #endregion

        #region RejectedDiscoQuery_ReturnsNullInsteadOfAnEmptyResult()

        /// <summary>
        /// A refused disco query used to deliver an empty but successful result
        /// - not to be told apart from an entity without features.
        /// </summary>
        [Test]
        public async Task RejectedDiscoQuery_ReturnsNullInsteadOfAnEmptyResult()
        {

            Server.FailDiscoInfo = true;

            var client = await ConnectClientAsync();

            StanzaError? reported  = null;
            StanzaError? general   = null;
            client.Connection.Disco!.OnQueryError += (timestamp, sender, _, error, ct) => { reported = error; return Task.CompletedTask; };
            client.OnStanzaError                  += (timestamp, sender, _, error, ct) => { general  = error; return Task.CompletedTask; };

            var info = await client.Connection.Disco!.QueryInfoAsync(JID.Parse(Server.Domain),
                                                                     timeout: TimeSpan.FromSeconds(5));

            await WaitFor(() => reported is not null, "the reported stanza error");

            Assert.Multiple(() =>
            {
                Assert.That(info, Is.Null,
                            "A refused query must not deliver a result.");

                Assert.That(general, Is.Null,
                            "The query knows its own refusal; the general event is " +
                            "for stanzas nobody was waiting for.");

                Assert.That(reported,            Is.Not.Null);
                Assert.That(reported!.Condition, Is.EqualTo("item-not-found"));
                Assert.That(reported!.Type,      Is.EqualTo(StanzaErrorType.Modify));
                // The text says what the switch does: this query is refused.
                // Until the node attribute came along, "This node does not exist
                // here" stood here - information about something the server did
                // not look at at all, and the query does not even name a node.
                Assert.That(reported!.Text,      Is.EqualTo("This information is not given here."));
            });

        }

        #endregion

        #region AnErrorNobodyWaitsFor_IsStillReported()

        /// <summary>
        /// The counter-check to the two above, and the reason the suppression is
        /// tied to the pending request rather than to the id prefix: an
        /// <c>iq error</c> carrying a disco id that belongs to no query is
        /// reported through the general event.
        /// </summary>
        /// <remarks>
        /// Without this the improvement would be a hiding place. Whoever filtered
        /// by the prefix alone would silence exactly the stanzas worth a line: a
        /// refusal to something never sent, or an answer arriving so late that
        /// the query it belongs to is already gone. ProcessError says which of
        /// the two it is - it returns false when no pending request carried the
        /// id - and only its <c>true</c> silences the general report.
        /// </remarks>
        [Test]
        public async Task AnErrorNobodyWaitsFor_IsStillReported()
        {

            var client = await ConnectClientAsync();

            StanzaError? general = null;
            client.OnStanzaError += (timestamp, sender, _, error, ct) => { general = error; return Task.CompletedTask; };

            // An IQ without a type, which the server refuses with
            // <bad-request/> (RFC 6120, section 8.2.3) - and the refusal carries
            // the id back. That id looks like one of ours, but no query of that
            // name is outstanding: DiscoManager knows nothing of it.
            //
            // The refusal is fetched this way rather than injected because a
            // client cannot deliver an <iq type='error'> to another one here -
            // the server does not route those. What is needed is an error that
            // really arrives, and the server writes one itself on request.
            await client.SendRawAsync(
                      "<iq id='disco-info-nobody-waits'>" +
                          "<ping xmlns='urn:xmpp:ping'/>" +
                      "</iq>");

            await WaitFor(() => general is not null,
                          "the report of an error belonging to no request");

            Assert.That(general!.Condition, Is.EqualTo("bad-request"));

        }

        #endregion

        #region ErrorMessage_IsReportedAsAnErrorNotAsAMessage()

        /// <summary>
        /// A <c>message type='error'</c> is the report that one's own message
        /// was not delivered - and no new message.
        /// </summary>
        [Test]
        public async Task ErrorMessage_IsReportedAsAnErrorNotAsAMessage()
        {

            var client   = await ConnectClientAsync();
            var session  = await SessionOfAsync(client);

            StanzaError?  reported  = null;
            XMPPMessage?  asMessage = null;

            client.OnStanzaError += (timestamp, sender, _, error, ct) => { reported  = error; return Task.CompletedTask; };
            client.OnMessage     += (timestamp, sender, m, ct)          => { asMessage = m; return Task.CompletedTask; };

            await session.SendAsync(
                $"<message type='error' from='nobody@{Server.Domain}' to='{client.FullJid}'>" +
                "<error type='cancel'>" +
                "<service-unavailable xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                "</error></message>");

            await WaitFor(() => reported is not null, "the reported stanza error");

            Assert.Multiple(() =>
            {
                Assert.That(reported!.Condition, Is.EqualTo("service-unavailable"));
                Assert.That(asMessage, Is.Null,
                            "An error stanza must not be passed through as a message.");
            });

        }

        #endregion

        #region ErrorPresence_DoesNotBecomeAContactState()

        /// <summary>
        /// A <c>presence type='error'</c> used to wander into the roster by way
        /// of <c>UpdatePresence</c>. Because only the <c>show</c> element is
        /// evaluated there and an error carries none, the contact ended up in
        /// the branch for "available" - so a bounced presence made them online.
        /// </summary>
        [Test]
        public async Task ErrorPresence_DoesNotMarkTheContactAsOnline()
        {

            var client   = await ConnectClientAsync();
            var session  = await SessionOfAsync(client);
            var bob      = $"bob@{Server.Domain}";

            await client.AddContactAsync(JID.Parse(bob), "Bob");

            await WaitFor(() => client.GetContact(JID.Parse(bob)) is not null, "the contact in the roster");

            Assert.That(client.GetContact(JID.Parse(bob))!.Presence, Is.EqualTo(PresenceState.Offline),
                        "Precondition: Bob is offline.");

            StanzaError? reported = null;
            client.OnStanzaError += (timestamp, sender, _, error, ct) => { reported = error; return Task.CompletedTask; };

            await session.SendAsync(
                $"<presence type='error' from='{bob}/x' to='{client.FullJid}'>" +
                "<error type='cancel'>" +
                "<remote-server-not-found xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                "</error></presence>");

            await WaitFor(() => reported is not null, "the reported stanza error");

            Assert.Multiple(() =>
            {
                Assert.That(reported!.Condition, Is.EqualTo("remote-server-not-found"));

                Assert.That(client.GetContact(JID.Parse(bob))!.Presence, Is.EqualTo(PresenceState.Offline),
                            "A presence error must not set the contact online.");
            });

        }

        #endregion

        #region FatalStreamError_IsReportedAndStopsReconnecting()

        /// <summary>
        /// RFC 6120, section 4.9: after a <c>conflict</c> the stream is finally
        /// lost. A reconnect would run into the same refusal, so it has to stay
        /// undone.
        /// </summary>
        /// <remarks>
        /// <b>This test has failed occasionally under load since D113, and it
        /// carries its own diagnosis because of it.</b> It was reproduced in
        /// D115 - three times in twenty-two runs beside a second suite - and the
        /// signature is narrower than it looked: the report never arrives at
        /// all. The reconnect, which is what the name of the test puts first,
        /// was never the part that failed.
        ///
        /// What that still leaves open is *why* nothing arrives, and three
        /// different causes fit the bare timeout equally well: the frame never
        /// crossed the wire, it crossed and was not recognised, or the session
        /// the error went to was no longer the client's. Roughly a hundred and
        /// fifty further runs under four different kinds of load produced not
        /// one failure, so the next occurrence may be a long way off - and it
        /// has to be worth something when it comes.
        ///
        /// Hence the recording. It costs one subscription and a few strings on
        /// a path that only runs when the test fails. That is D35's answer to
        /// the flake of D34, and D55 is what it was worth: the question that
        /// entry could not settle was settled in one attempt once the run said
        /// what it had seen.
        /// </remarks>
        [Test]
        public async Task FatalStreamError_IsReportedAndStopsReconnecting()
        {

            var client   = await ConnectClientAsync(reconnectDelay: TimeSpan.FromMilliseconds(100));
            var jidAtStart = client.FullJid.ToString();
            var session  = await SessionOfAsync(client);

            var inbound = new ConcurrentQueue<String>();
            client.OnRawXml += (timestamp, sender, xml, ct) =>
            {
                if (xml.StartsWith("<<<", StringComparison.Ordinal))
                    inbound.Enqueue(xml);
                return Task.CompletedTask;
            };

            StreamError? reported = null;
            client.OnStreamError += (timestamp, sender, error, ct) => { reported = error; return Task.CompletedTask; };

            var connectionsBefore = Server.ConnectionCount;

            // Closes the stream itself (RFC 6120, section 4.9.1.1) - until D23 a
            // Kill() stood behind this that did exactly that by hand.
            await session.SendStreamErrorAsync("conflict", "Resource assigned twice.");

            var arrived = await XMPPServer.WaitUntilAsync(() => reported is not null);

            Assert.That(arrived, Is.True,
                        "The stream error was never reported. Which of the three it was:\n" +
                        $"  Did anything with 'conflict' reach the client? " +
                        inbound.Any(frame => frame.Contains("conflict", StringComparison.Ordinal)) + "\n" +
                        $"  Is the session still the server's one for this client? " +
                        ReferenceEquals(session, Server.SessionOf(client.FullJid.ToString())) + "\n" +
                        $"  JID: {jidAtStart} at the start, {client.FullJid} now\n" +
                        $"  State: {client.State}\n" +
                        $"  Connections: {connectionsBefore} before, {Server.ConnectionCount} now\n" +
                        $"  The server sent {session.Sent.Count} frames, the last three:\n" +
                        "    " + String.Join("\n    ", session.Sent.TakeLast(3)) + "\n" +
                        $"  The client received {inbound.Count} frames, the last three:\n" +
                        "    " + String.Join("\n    ", inbound.TakeLast(3)));

            // Give the client time to attempt a reconnect - it must not make one.
            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(reported!.Condition,      Is.EqualTo("conflict"));
                Assert.That(reported!.Text,           Is.EqualTo("Resource assigned twice."));
                Assert.That(reported!.IsRecoverable,  Is.False);

                Assert.That(Server.ConnectionCount, Is.EqualTo(connectionsBefore),
                            "After a final stream error no reconnect may take place.");
            });

        }

        #endregion

        #region AStreamErrorWithAnUnusualPrefixIsStillReported()

        /// <summary>
        /// A prefix with a dot in it is legal, and used to make the error
        /// disappear.
        /// </summary>
        /// <remarks>
        /// <c>stream:</c> is customary and nothing more; any prefix bound to the
        /// streams namespace does the job, and an NCName may contain a dot. The
        /// branch that read the error out of the raw text allowed letters,
        /// digits, hyphens and underscores - so <c>&lt;a.b:error&gt;</c> did not
        /// match, and the branch <b>returned without a word</b>: no report, no
        /// error, nothing in the log.
        ///
        /// Of all the stanzas there are, this is the one that may least be
        /// dropped in silence. After a stream error the stream is dead, and an
        /// application that is not told goes on waiting for a connection that no
        /// longer exists - which is exactly the shape of the failure this
        /// fixture has been chasing since D113, whether or not this is its
        /// cause.
        ///
        /// It is read out of the parsed element now. The parser knows what a
        /// prefix is; a pattern over the text has to guess.
        /// </remarks>
        [Test]
        public async Task AStreamErrorWithAnUnusualPrefixIsStillReported()
        {

            var client   = await ConnectClientAsync(reconnectDelay: TimeSpan.FromMilliseconds(100));
            var session  = await SessionOfAsync(client);

            StreamError? reported = null;
            client.OnStreamError += (timestamp, sender, error, ct) => { reported = error; return Task.CompletedTask; };

            await session.SendAsync(
                "<a.b:error xmlns:a.b='http://etherx.jabber.org/streams'>" +
                    "<conflict xmlns='urn:ietf:params:xml:ns:xmpp-streams'/>" +
                    "<text xmlns='urn:ietf:params:xml:ns:xmpp-streams'>Prefix with a dot.</text>" +
                "</a.b:error>");

            await WaitFor(() => reported is not null,
                          "the reported stream error with an unusual prefix");

            Assert.Multiple(() =>
            {
                Assert.That(reported!.Condition, Is.EqualTo("conflict"));
                Assert.That(reported!.Text,      Is.EqualTo("Prefix with a dot."));
            });

        }

        #endregion

        #region TheConditionIsFoundWhateverElseStandsBesideIt()

        /// <summary>
        /// RFC 6120, section 4.9.4: a stream error may carry an element of the
        /// application's own beside the defined condition. The namespace is what
        /// tells them apart.
        /// </summary>
        /// <remarks>
        /// <b>Found as a surviving mutant in D115</b>, when the check was
        /// dropped and every one of the other nine tests stayed green - all of
        /// them send a stream error with nothing but the defined condition in
        /// it, so taking the first child at all costs the same answer.
        ///
        /// It is not cosmetic. An application element read as the condition
        /// gives a name <see cref="StreamError.IsRecoverable"/> has never heard
        /// of, and that list answers <c>false</c> to everything it does not
        /// know - deliberately, because a reconnect into an unknown refusal is a
        /// loop. So the mistake does not show up as a wrong word in a log: a
        /// <c>system-shutdown</c> the client should sit out becomes a
        /// connection it never comes back from.
        ///
        /// The same holds for the <c>&lt;text/&gt;</c>, which is in the streams
        /// namespace itself and is therefore no help at all in telling it from
        /// the condition - only its name is.
        ///
        /// Both are put in front here for the same reason a test uses an emoji:
        /// in the order RFC 6120 prints, a reading that looks at neither the
        /// namespace nor the name gets the right answer anyway.
        /// </remarks>
        [Test]
        public async Task TheConditionIsFoundWhateverElseStandsBesideIt()
        {

            var client   = await ConnectClientAsync(reconnectDelay: TimeSpan.FromMilliseconds(100));
            var session  = await SessionOfAsync(client);

            StreamError? reported = null;
            client.OnStreamError += (timestamp, sender, error, ct) => { reported = error; return Task.CompletedTask; };

            await session.SendAsync(
                "<stream:error xmlns:stream='http://etherx.jabber.org/streams'>" +
                    "<text xmlns='urn:ietf:params:xml:ns:xmpp-streams'>Shutting down.</text>" +
                    "<too-many-frobnicators xmlns='urn:example:errors'/>" +
                    "<system-shutdown xmlns='urn:ietf:params:xml:ns:xmpp-streams'/>" +
                "</stream:error>");

            await WaitFor(() => reported is not null, "the reported stream error");

            Assert.Multiple(() =>
            {

                Assert.That(reported!.Condition, Is.EqualTo("system-shutdown"),
                            "Something standing beside the condition was taken for it - the " +
                            "application's own element, or the text.");

                Assert.That(reported!.Text, Is.EqualTo("Shutting down."),
                            "The text did not survive being put first.");

                Assert.That(reported!.IsRecoverable, Is.True,
                            "A condition nobody knows counts as final, so this client would never " +
                            "come back from a shutdown it was meant to sit out.");

            });

        }

        #endregion

        #region RecoverableStreamError_IsReportedButAllowsReconnect()

        /// <summary>
        /// With <c>system-shutdown</c>, by contrast, the reconnect is worth it -
        /// the server comes back.
        /// </summary>
        [Test]
        public async Task RecoverableStreamError_IsReportedButAllowsReconnect()
        {

            var client   = await ConnectClientAsync(reconnectDelay: TimeSpan.FromMilliseconds(100));
            var session  = await SessionOfAsync(client);

            StreamError? reported = null;
            client.OnStreamError += (timestamp, sender, error, ct) => { reported = error; return Task.CompletedTask; };

            var connectionsBefore = Server.ConnectionCount;

            // Closes the stream itself; the reconnect follows from that and not
            // from an additional cut-off by hand.
            await session.SendStreamErrorAsync("system-shutdown");

            await WaitFor(() => reported is not null, "the reported stream error");

            await WaitFor(() => Server.ConnectionCount > connectionsBefore,
                          "the reconnect after a repeatable stream error");

            Assert.Multiple(() =>
            {
                Assert.That(reported!.Condition,     Is.EqualTo("system-shutdown"));
                Assert.That(reported!.IsRecoverable, Is.True);
            });

        }

        #endregion

    }

}
