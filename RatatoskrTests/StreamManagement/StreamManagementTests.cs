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
    /// XEP-0198 stream management: the counters of both sides have to agree
    /// exactly.
    ///
    /// The test server counts along independently (see
    /// <see cref="XMPPSession.IsStanza"/>) and answers <c>&lt;r/&gt;</c>. That
    /// makes it possible to check whether the client reports to the server
    /// exactly what the server actually sent - the point at which a real server
    /// breaks the connection off as a protocol violation if they differ.
    /// </summary>
    [TestFixture]
    public class StreamManagementTests : AXMPPTests
    {

        #region Helper functions

        /// <summary>
        /// Connects a client with stream management negotiated and delivers the
        /// client and the server session belonging to it.
        /// </summary>
        private async Task<(XMPPClient Client, XMPPSession Session)> ConnectWithSmAsync()
        {

            var client = await ConnectClientAsync(streamManagement: true);

            await WaitFor(() => Server.Sessions.Count(s => s.StreamManagementEnabled) == 1,
                          "the negotiated stream management");

            var session = Server.Sessions.Single(s => s.StreamManagementEnabled);

            Assert.That(client.StreamManagement?.IsEnabled, Is.True,
                        "The client does not hold stream management to be active.");

            return (client, session);

        }

        #endregion


        #region ClientAck_ReportsEveryStanzaTheServerSent()

        /// <summary>
        /// The heart of the fault: on an <c>&lt;r/&gt;</c> the client has to
        /// answer with exactly the number of stanzas the server has sent since
        /// the <c>&lt;enabled/&gt;</c>.
        ///
        /// Only ProcessStanza used to count along. The results of the carbons
        /// enable and the roster fetch are read straight through
        /// ReceiveStanzaAsync in the setup phase, though, and never arrived
        /// there, so that the client permanently acknowledged too few.
        /// </summary>
        [Test]
        public async Task ClientAck_ReportsEveryStanzaTheServerSent()
        {

            var (_, session) = await ConnectWithSmAsync();

            // The server has to have sent something at all in the setup phase
            // after the <enabled/>, otherwise the test checks nothing.
            await WaitFor(() => session.StanzasSentToClient > 0,
                          "stanzas from the server after the <enabled/>");

            var sent = session.StanzasSentToClient;

            await session.RequestAckAsync();

            await WaitFor(() => session.LastAckFromClient is not null,
                          "an <a h='...'/> from the client");

            Assert.That(session.LastAckFromClient, Is.EqualTo(sent),
                        $"The client acknowledges {session.LastAckFromClient} of {sent} stanzas.");

        }

        #endregion

        #region ClientAck_CountsStanzasArrivingAfterConnect()

        /// <summary>
        /// After the connection setup too, that is over the receive loop, the
        /// counting has to carry on - and in addition to the stanzas received
        /// in the setup phase.
        /// </summary>
        [Test]
        public async Task ClientAck_CountsStanzasArrivingAfterConnect()
        {

            var (_, session) = await ConnectWithSmAsync();

            await WaitFor(() => session.StanzasSentToClient > 0,
                          "stanzas from the server after the <enabled/>");

            var before = session.StanzasSentToClient;

            await session.SendAsync(
                $"<message from='bob@{Server.Domain}/x' to='{session.FullJid}' type='chat'>" +
                "<body>Hello</body></message>");

            await WaitFor(() => session.StanzasSentToClient == before + 1,
                          "the counted message on the server side");

            await session.RequestAckAsync();

            await WaitFor(() => session.LastAckFromClient == before + 1,
                          $"an <a h='{before + 1}'/> from the client");

            Assert.That(session.LastAckFromClient, Is.EqualTo(before + 1));

        }

        #endregion

        #region OutboundCount_CoversEveryStanzaNotJustMessages()

        /// <summary>
        /// The outgoing counter has to take in every stanza, not just the ones
        /// from SendMessageAsync. The connection setup alone sends several IQs
        /// and a presence after the <c>&lt;enabled/&gt;</c>; none of that used
        /// to be counted, because TrackOutgoing stood at a single one of some
        /// 25 sending places.
        /// </summary>
        [Test]
        public async Task OutboundCount_CoversEveryStanzaNotJustMessages()
        {

            var (client, session) = await ConnectWithSmAsync();

            await WaitFor(() => session.StanzasReceivedFromClient > 1,
                          "several stanzas from the client");

            await WaitFor(() => client.StreamManagement!.OutboundCount == session.StanzasReceivedFromClient,
                          "agreeing outgoing counters");

            Assert.That(client.StreamManagement!.OutboundCount,
                        Is.EqualTo(session.StanzasReceivedFromClient),
                        "Client and server count different numbers of outgoing stanzas.");

        }

        #endregion

        #region OutboundCount_IgnoresNonzas()

        /// <summary>
        /// <c>&lt;r/&gt;</c> and <c>&lt;a/&gt;</c> are nonzas and do not count
        /// under XEP-0198 section 2. Were they counted in, the counter would
        /// drift further apart at every keepalive.
        /// </summary>
        [Test]
        public async Task OutboundCount_IgnoresNonzas()
        {

            var (client, session) = await ConnectWithSmAsync();

            await WaitFor(() => session.StanzasReceivedFromClient > 0,
                          "stanzas from the client");

            var before = client.StreamManagement!.OutboundCount;

            // An <r/> from the client to the server ...
            await client.RequestAckAsync();

            // ... and an <a/> from the client in answer to an <r/> of the server.
            await session.RequestAckAsync();

            await WaitFor(() => session.LastAckFromClient is not null,
                          "an <a h='...'/> from the client");

            Assert.That(client.StreamManagement!.OutboundCount, Is.EqualTo(before),
                        "Nonzas must not raise the outgoing counter.");

        }

        #endregion

        #region SentMessage_IsCountedAndAcknowledged()

        /// <summary>
        /// A message that has been sent has to be counted, laid into the
        /// unacked queue and taken out of it again by the <c>&lt;a/&gt;</c> of
        /// the server.
        /// </summary>
        [Test]
        public async Task SentMessage_IsCountedAndAcknowledged()
        {

            var (client, session) = await ConnectWithSmAsync();

            await WaitFor(() => session.StanzasReceivedFromClient > 0,
                          "stanzas from the client");

            var before = client.StreamManagement!.OutboundCount;

            await client.SendMessageAsync($"bob@{Server.Domain}", "Hello");

            await WaitFor(() => client.StreamManagement!.OutboundCount == before + 1,
                          "the counted message");

            // The server acknowledges everything it has received so far.
            await client.RequestAckAsync();

            await WaitFor(() => client.StreamManagement!.UnackedCount == 0,
                          "the emptied unacked queue");

            Assert.That(client.StreamManagement!.UnackedCount, Is.Zero);

        }

        #endregion

        #region CountersStayEqual_UnderConcurrentSends()

        /// <summary>
        /// Simultaneous send calls must not throw the counting into disorder.
        /// That is why the counting happens under the send lock and not ahead
        /// of it.
        /// </summary>
        [Test]
        public async Task CountersStayEqual_UnderConcurrentSends()
        {

            var (client, session) = await ConnectWithSmAsync();

            await WaitFor(() => session.StanzasReceivedFromClient > 0,
                          "stanzas from the client");

            var before = client.StreamManagement!.OutboundCount;

            await Task.WhenAll(Enumerable.Range(0, 50)
                                         .Select(i => client.SendMessageAsync($"bob@{Server.Domain}", $"Message {i}")));

            await WaitFor(() => client.StreamManagement!.OutboundCount == before + 50,
                          "50 counted messages");

            await WaitFor(() => session.StanzasReceivedFromClient == client.StreamManagement!.OutboundCount,
                          "agreeing counters after sending in parallel");

            Assert.That(client.StreamManagement!.OutboundCount,
                        Is.EqualTo(session.StanzasReceivedFromClient));

        }

        #endregion

        #region DisabledStreamManagement_DoesNotCount()

        /// <summary>
        /// Without stream management negotiated nothing may be counted -
        /// otherwise a value would stand in the counter at the later
        /// <c>&lt;enable/&gt;</c> that was never reported to the server.
        /// </summary>
        [Test]
        public async Task DisabledStreamManagement_DoesNotCount()
        {

            var client = await ConnectClientAsync(streamManagement: false);

            await client.SendMessageAsync($"bob@{Server.Domain}", "Hello");

            Assert.Multiple(() =>
            {
                Assert.That(client.StreamManagement?.IsEnabled,      Is.False);
                Assert.That(client.StreamManagement?.OutboundCount,  Is.Zero);
                Assert.That(client.StreamManagement?.InboundCount,   Is.Zero);
            });

        }

        #endregion

        #region StreamManagement_IsNegotiatedByDefault()

        /// <summary>
        /// A client that sets nothing negotiates stream management.
        /// </summary>
        /// <remarks>
        /// The default stood at <c>false</c> for years, because the counting
        /// was wrong once. It is not any more and is vouched for against
        /// Prosody 13 (<c>ProsodyStreamManagementTests</c>) - which is why it
        /// now stands at <c>true</c>.
        ///
        /// Both are checked: the value itself and that it carries through onto
        /// the wire. A test on the property alone would pass even if the setup
        /// ignored it afterwards; a test on the negotiation alone would leave
        /// open whether it hangs on the default or on something else.
        ///
        /// That the rest of the collection goes this way at all hangs on
        /// <c>CreateClient</c> <i>not</i> setting the switch as long as nobody
        /// asks for it - see <see cref="AXMPPTests"/>.
        /// </remarks>
        [Test]
        public async Task StreamManagement_IsNegotiatedByDefault()
        {

            Assert.That(new XMPPConnection("alice@example.com", "pw").StreamManagementEnabled,
                        Is.True,
                        "The default of XMPPConnection.StreamManagementEnabled.");

            var client = await ConnectClientAsync();

            await WaitFor(() => Server.Sessions.Count(s => s.StreamManagementEnabled) == 1,
                          "stream management negotiated without the caller doing anything");

            Assert.That(client.StreamManagement?.IsEnabled, Is.True,
                        "The client does not hold stream management to be active.");

        }

        #endregion

    }

}
