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
        [Test]
        public async Task FatalStreamError_IsReportedAndStopsReconnecting()
        {

            var client   = await ConnectClientAsync(reconnectDelay: TimeSpan.FromMilliseconds(100));
            var session  = await SessionOfAsync(client);

            StreamError? reported = null;
            client.OnStreamError += (timestamp, sender, error, ct) => { reported = error; return Task.CompletedTask; };

            var connectionsBefore = Server.ConnectionCount;

            // Closes the stream itself (RFC 6120, section 4.9.1.1) - until D23 a
            // Kill() stood behind this that did exactly that by hand.
            await session.SendStreamErrorAsync("conflict", "Resource assigned twice.");

            await WaitFor(() => reported is not null, "the reported stream error");

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
