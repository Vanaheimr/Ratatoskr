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
    /// The XEP payloads inside a stanza in a valid but unusual spelling.
    ///
    /// The stanza frame is already read with an XML parser; these tests take on
    /// the evaluation of the child elements - chat states, chat markers,
    /// receipts, carbons and entity capabilities.
    /// </summary>
    [TestFixture]
    public class XepPayloadParsingTests : AXMPPTests
    {

        #region Helper functions

        private async Task<(XMPPClient Client, XMPPSession Session)> ConnectedPairAsync()
        {

            var client = await ConnectClientAsync();

            await WaitFor(() => Server.SessionOf(client.FullJid.ToString()) is not null,
                          "the server session to the client");

            return (client, Server.SessionOf(client.FullJid.ToString())!);

        }

        private String Bob => $"bob@{Server.Domain}";

        #endregion


        #region ChatState_InAForeignNamespace_IsIgnored()

        /// <summary>
        /// A <c>&lt;composing/&gt;</c> counts only when it belongs to XEP-0085.
        /// A recognition over <c>Contains("&lt;composing")</c> does not check
        /// the namespace at all and reports every element of the same name -
        /// out of an arbitrary extension, say - as a typing notification.
        /// </summary>
        [Test]
        public async Task ChatState_InAForeignNamespace_IsIgnored()
        {

            var (client, session) = await ConnectedPairAsync();

            ChatState? reported = null;
            client.OnChatState += (timestamp, sender, _, state, ct) => { reported = state; return Task.CompletedTask; };

            await session.SendAsync(
                $"<message from='{Bob}/x' to='{client.FullJid}' type='chat' id='cs1'>" +
                "<composing xmlns='urn:example:something-else'/>" +
                "<body>Text</body></message>");

            await WaitFor(() => client.LastReceivedMessageId == "cs1", "the delivered message");

            Assert.That(reported, Is.Null,
                        "A foreign <composing/> is no chat state notification.");

        }

        #endregion

        #region ChatState_InsideForwarded_DoesNotLeakOut()

        /// <summary>
        /// A forwarded message brings its own chat state along. That one
        /// belongs to the embedded message, not to the outer one.
        /// </summary>
        [Test]
        public async Task ChatState_InsideForwarded_DoesNotLeakOut()
        {

            var (client, session) = await ConnectedPairAsync();

            ChatState? reported = null;
            client.OnChatState += (timestamp, sender, _, state, ct) => { reported = state; return Task.CompletedTask; };

            await session.SendAsync(
                $"<message from='{Bob}/x' to='{client.FullJid}' type='chat' id='cs2'>" +
                "<forwarded xmlns='urn:xmpp:forward:0'>" +
                "<message xmlns='jabber:client'>" +
                "<composing xmlns='http://jabber.org/protocol/chatstates'/>" +
                "</message></forwarded>" +
                "<body>Text</body></message>");

            await WaitFor(() => client.LastReceivedMessageId == "cs2", "the delivered message");

            Assert.That(reported, Is.Null,
                        "The chat state of the embedded message must not take effect outside.");

        }

        #endregion

        #region ChatState_IsStillRecognised()

        /// <summary>
        /// Control group: the normal case has to go on being recognised.
        /// </summary>
        [Test]
        public async Task ChatState_IsStillRecognised()
        {

            var (client, session) = await ConnectedPairAsync();

            ChatState? reported = null;
            client.OnChatState += (timestamp, sender, _, state, ct) => { reported = state; return Task.CompletedTask; };

            await session.SendAsync(
                $"<message from='{Bob}/x' to='{client.FullJid}' type='chat'>" +
                "<composing xmlns='http://jabber.org/protocol/chatstates'/></message>");

            await WaitFor(() => reported is not null, "the reported chat state");

            Assert.That(reported, Is.EqualTo(ChatState.Composing));

        }

        #endregion

        #region ChatMarker_WithAttributesInAnyOrder_IsRecognised()

        /// <summary>
        /// The former pattern demanded <c>xmlns</c> before <c>id</c>. XML knows
        /// no attribute order - a server writing them the other way round was
        /// silently ignored.
        /// </summary>
        /// <remarks>
        /// A message goes out first, and that is not decoration. A marker is
        /// only believed from whoever the message it marks was sent to, so
        /// without one there is nothing for the marker to be about and the
        /// parsing would never be reached. The identifier therefore comes from
        /// the sending rather than being made up here; what is measured stays
        /// the attribute order.
        /// </remarks>
        [Test]
        public async Task ChatMarker_WithAttributesInAnyOrder_IsRecognised()
        {

            var (client, session) = await ConnectedPairAsync();

            ChatMarker? reported = null;
            client.OnChatMarker += (timestamp, sender, marker, ct) => { reported = marker; return Task.CompletedTask; };

            var id = await client.Connection.SendMessageAsync(JID.Parse(Bob), "markable", markable: true);

            await session.SendAsync(
                $"<message from='{Bob}/x' to='{client.FullJid}' type='chat'>" +
                $"<displayed id='{id}' xmlns='urn:xmpp:chat-markers:0'/></message>");

            await WaitFor(() => reported is not null, "the reported chat marker");

            Assert.Multiple(() =>
            {
                Assert.That(reported!.Type,       Is.EqualTo(ChatMarkerType.Displayed));
                Assert.That(reported!.MessageId,  Is.EqualTo(id));
            });

        }

        #endregion

        #region ChatMarker_InAForeignNamespace_IsIgnored()

        /// <summary>
        /// <c>&lt;received/&gt;</c> exists in XEP-0333 <b>and</b> in XEP-0184.
        /// Without a namespace check they cannot be told apart.
        /// </summary>
        [Test]
        public async Task ChatMarker_InAForeignNamespace_IsIgnored()
        {

            var (client, session) = await ConnectedPairAsync();

            ChatMarker? reported = null;
            client.OnChatMarker += (timestamp, sender, marker, ct) => { reported = marker; return Task.CompletedTask; };

            await session.SendAsync(
                $"<message from='{Bob}/x' to='{client.FullJid}' type='chat' id='cm2'>" +
                "<received id='abc' xmlns='urn:example:something-else'/>" +
                "<body>Text</body></message>");

            await WaitFor(() => client.LastReceivedMessageId == "cm2", "the delivered message");

            Assert.That(reported, Is.Null);

        }

        #endregion

        #region ReceiptRequest_WithDoubleQuotedNamespace_IsAnswered()

        /// <summary>
        /// The check looked literally for <c>xmlns='urn:xmpp:receipts'</c> -
        /// that is, only with single quotation marks. XML permits both forms;
        /// against a server using double ones every receipt failed to come.
        /// </summary>
        [Test]
        public async Task ReceiptRequest_WithDoubleQuotedNamespace_IsAnswered()
        {

            var (client, session) = await ConnectedPairAsync();

            await session.SendAsync(
                $"<message from='{Bob}/x' to='{client.FullJid}' type='chat' id='r1'>" +
                "<body>Please acknowledge</body>" +
                "<request xmlns=\"urn:xmpp:receipts\"/></message>");

            await WaitFor(() => session.Received.Any(f => f.Contains("urn:xmpp:receipts", StringComparison.Ordinal) &&
                                                          f.Contains("id='r1'", StringComparison.Ordinal)),
                          "the receipt from the client");

            Assert.Pass();

        }

        #endregion

        #region ReceiptRequest_InsideForwarded_IsNotAnswered()

        /// <summary>
        /// The request for a receipt in a forwarded message does not hold for
        /// the outer one - otherwise the client acknowledges a message it never
        /// received.
        /// </summary>
        [Test]
        public async Task ReceiptRequest_InsideForwarded_IsNotAnswered()
        {

            var (client, session) = await ConnectedPairAsync();

            await session.SendAsync(
                $"<message from='{Bob}/x' to='{client.FullJid}' type='chat' id='r2'>" +
                "<forwarded xmlns='urn:xmpp:forward:0'>" +
                "<message xmlns='jabber:client' id='inner'>" +
                "<request xmlns='urn:xmpp:receipts'/></message></forwarded>" +
                "<body>Text</body></message>");

            await WaitFor(() => client.LastReceivedMessageId == "r2", "the delivered message");

            var answered = await XMPPServer.WaitUntilAsync(
                               () => session.Received.Any(f => f.Contains("urn:xmpp:receipts", StringComparison.Ordinal)),
                               TimeSpan.FromSeconds(1));

            Assert.That(answered, Is.False,
                        "For an embedded request no receipt may be sent.");

        }

        #endregion

        #region Carbon_WithEntitiesInTheBody_IsUnescaped()

        /// <summary>
        /// The content of a mirrored message belongs resolved just like that of
        /// a direct one.
        /// </summary>
        [Test]
        public async Task Carbon_WithEntitiesInTheBody_IsUnescaped()
        {

            var (client, session) = await ConnectedPairAsync();

            CarbonMessage? reported = null;
            client.OnCarbonMessage += (timestamp, sender, carbon, ct) => { reported = carbon; return Task.CompletedTask; };

            await session.SendAsync(
                $"<message xmlns='jabber:client' from='{client.BareJid}' to='{client.FullJid}'>" +
                "<sent xmlns='urn:xmpp:carbons:2'>" +
                "<forwarded xmlns='urn:xmpp:forward:0'>" +
                $"<message xmlns='jabber:client' from='{client.BareJid}/other' to='{Bob}' type='chat'>" +
                "<body>3 &gt; 2 &amp;&amp; 1 &lt; 2</body>" +
                "</message></forwarded></sent></message>");

            await WaitFor(() => reported is not null, "the reported carbon");

            Assert.That(reported!.Body, Is.EqualTo("3 > 2 && 1 < 2"));

        }

        #endregion

    }

}
