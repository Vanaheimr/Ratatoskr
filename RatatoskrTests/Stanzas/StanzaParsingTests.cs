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
    /// Stanzas in unusual but perfectly valid spelling.
    ///
    /// All the forms here are permitted by XML and RFC 6120 and occur with real
    /// servers. A parser that does not understand them loses messages silently -
    /// precisely that is the reason why the stanza evaluation was moved from
    /// regular expressions to an XML parser.
    /// </summary>
    [TestFixture]
    public class StanzaParsingTests : AXMPPTests
    {

        #region Helper functions

        private async Task<(XMPPClient Client, XMPPSession Session)> ConnectedPairAsync()
        {

            var client = await ConnectClientAsync();

            await WaitFor(() => Server.SessionOf(client.FullJid) is not null,
                          "the server session for the client");

            return (client, Server.SessionOf(client.FullJid)!);

        }

        /// <summary>
        /// Sends a raw stanza to the client and waits for OnMessage.
        /// </summary>
        private async Task<XMPPMessage?> DeliverAsync(XMPPClient client, XMPPSession session, String stanza)
        {

            XMPPMessage? received = null;
            client.OnMessage += m => received = m;

            await session.SendAsync(stanza);

            await XMPPServer.WaitUntilAsync(() => received is not null, TimeSpan.FromSeconds(3));

            return received;

        }

        #endregion


        #region Message_WithNamespacePrefix_IsDelivered()

        /// <summary>
        /// The element name may carry a prefix, as long as it is bound to
        /// <c>jabber:client</c>. A recognition by way of
        /// <c>StartsWith("&lt;message")</c> discards such stanzas completely.
        /// </summary>
        [Test]
        public async Task Message_WithNamespacePrefix_IsDelivered()
        {

            var (client, session) = await ConnectedPairAsync();

            var received = await DeliverAsync(client, session,
                               $"<c:message xmlns:c='jabber:client' from='bob@{Server.Domain}/x' " +
                               $"to='{client.FullJid}' type='chat' id='m1'>" +
                               "<c:body>With a prefix</c:body></c:message>");

            Assert.That(received?.Body, Is.EqualTo("With a prefix"));

        }

        #endregion

        #region Message_WithLanguageTaggedBody_IsDelivered()

        /// <summary>
        /// <c>&lt;body/&gt;</c> may carry an <c>xml:lang</c> - RFC 6121
        /// section 5.2.3 provides for that expressly. A pattern like
        /// <c>&lt;body&gt;(...)&lt;/body&gt;</c> then does not find it any more.
        /// </summary>
        [Test]
        public async Task Message_WithLanguageTaggedBody_IsDelivered()
        {

            var (client, session) = await ConnectedPairAsync();

            var received = await DeliverAsync(client, session,
                               $"<message from='bob@{Server.Domain}/x' to='{client.FullJid}' " +
                               "type='chat' id='m2'>" +
                               "<body xml:lang='de'>With a language tag</body></message>");

            Assert.That(received?.Body, Is.EqualTo("With a language tag"));

        }

        #endregion

        #region Message_WithEntities_IsUnescaped()

        /// <summary>
        /// Entities belong resolved by the parser. Whoever passes the content on
        /// raw shows the user <c>&amp;lt;</c> instead of <c>&lt;</c>.
        /// </summary>
        [Test]
        public async Task Message_WithEntities_IsUnescaped()
        {

            var (client, session) = await ConnectedPairAsync();

            var received = await DeliverAsync(client, session,
                               $"<message from='bob@{Server.Domain}/x' to='{client.FullJid}' " +
                               "type='chat' id='m3'>" +
                               "<body>1 &lt; 2 &amp;&amp; 3 &gt; 2</body></message>");

            Assert.That(received?.Body, Is.EqualTo("1 < 2 && 3 > 2"));

        }

        #endregion

        #region Message_WithNestedBody_UsesTheOuterOne()

        /// <summary>
        /// XEP-0297: a forwarded message sits completely inside
        /// <c>&lt;forwarded/&gt;</c> - together with a <c>&lt;body/&gt;</c> of
        /// its own. A pattern without a notion of nesting takes the first, that
        /// is, the inner one.
        /// </summary>
        [Test]
        public async Task Message_WithNestedBody_UsesTheOuterOne()
        {

            var (client, session) = await ConnectedPairAsync();

            var received = await DeliverAsync(client, session,
                               $"<message from='bob@{Server.Domain}/x' to='{client.FullJid}' " +
                               "type='chat' id='m4'>" +
                               "<forwarded xmlns='urn:xmpp:forward:0'>" +
                               "<message xmlns='jabber:client'><body>inside</body></message>" +
                               "</forwarded>" +
                               "<body>outside</body></message>");

            Assert.That(received?.Body, Is.EqualTo("outside"));

        }

        #endregion

        #region Message_WithAttributeContainingTheIdOfANestedElement_UsesTheOuterOne()

        /// <summary>
        /// An unanchored attribute pattern finds attributes in child elements
        /// too. Because the attributes of the outer element always stand first
        /// in the text, that shows only when the outer element does not have the
        /// attribute sought at all: the id then comes from the embedded message.
        ///
        /// That is no hair-splitting - a receipt or a chat marker on this id
        /// would go to a message that was never sent.
        /// </summary>
        [Test]
        public async Task Message_WithoutId_DoesNotBorrowOneFromANestedElement()
        {

            var (client, session) = await ConnectedPairAsync();

            var received = await DeliverAsync(client, session,
                               $"<message to='{client.FullJid}' type='chat' " +
                               $"from='bob@{Server.Domain}/x'>" +
                               "<forwarded xmlns='urn:xmpp:forward:0'>" +
                               "<message xmlns='jabber:client' id='inside'><body>x</body></message>" +
                               "</forwarded>" +
                               "<body>Text</body></message>");

            Assert.Multiple(() =>
            {
                Assert.That(received?.Body,       Is.EqualTo("Text"));
                Assert.That(received?.MessageId,  Is.Null,
                            "The outer message has no id - it must not borrow one.");
            });

        }

        #endregion

        #region Presence_WithLanguageTaggedStatus_IsRead()

        /// <summary>
        /// <c>&lt;status/&gt;</c> too often carries an <c>xml:lang</c> in
        /// practice.
        /// </summary>
        [Test]
        public async Task Presence_WithLanguageTaggedStatus_IsRead()
        {

            var (client, session) = await ConnectedPairAsync();
            var bob = $"bob@{Server.Domain}";

            await client.AddContactAsync(bob, "Bob");
            await WaitFor(() => client.GetContact(bob) is not null, "the contact in the roster");

            await session.SendAsync(
                $"<presence from='{bob}/x' to='{client.FullJid}'>" +
                "<show>away</show>" +
                "<status xml:lang='de'>Out for lunch</status></presence>");

            await WaitFor(() => client.GetContact(bob)!.Presence == PresenceState.Away,
                          "the presence change to away");

            Assert.That(client.GetContact(bob)!.PresenceStatus, Is.EqualTo("Out for lunch"));

        }

        #endregion

        #region Presence_WithNamespacePrefix_IsRead()

        /// <summary>
        /// The same prefix problem as with message.
        /// </summary>
        [Test]
        public async Task Presence_WithNamespacePrefix_IsRead()
        {

            var (client, session) = await ConnectedPairAsync();
            var bob = $"bob@{Server.Domain}";

            await client.AddContactAsync(bob, "Bob");
            await WaitFor(() => client.GetContact(bob) is not null, "the contact in the roster");

            await session.SendAsync(
                $"<c:presence xmlns:c='jabber:client' from='{bob}/x' to='{client.FullJid}'>" +
                "<c:show>dnd</c:show></c:presence>");

            await WaitFor(() => client.GetContact(bob)!.Presence == PresenceState.Dnd,
                          "the presence change to dnd");

            Assert.That(client.GetContact(bob)!.Presence, Is.EqualTo(PresenceState.Dnd));

        }

        #endregion

        #region RosterPush_WithAttributesInAnyOrder_IsApplied()

        /// <summary>
        /// The earlier pattern demanded the attributes in the order <c>jid</c>,
        /// <c>name</c>, <c>subscription</c>. XML knows no attribute order - a
        /// server that writes them differently was silently ignored and the
        /// contact was missing from the roster.
        /// </summary>
        [Test]
        public async Task RosterPush_WithAttributesInAnyOrder_IsApplied()
        {

            var (client, session) = await ConnectedPairAsync();
            var carol = $"carol@{Server.Domain}";

            await session.SendAsync(
                $"<iq type='set' id='push-1' to='{client.FullJid}'>" +
                "<query xmlns='jabber:iq:roster'>" +
                $"<item subscription='both' name='Carol' jid='{carol}'/>" +
                "</query></iq>");

            await WaitFor(() => client.GetContact(carol) is not null, "the contact from the push");

            Assert.That(client.GetContact(carol)!.Name, Is.EqualTo("Carol"));

        }

        #endregion

        #region RosterPush_KeepsGroups()

        /// <summary>
        /// Groups stand as child elements in the <c>&lt;item/&gt;</c>. The
        /// attribute pattern never saw them, so every push lost the group
        /// assignment.
        /// </summary>
        [Test]
        public async Task RosterPush_KeepsGroups()
        {

            var (client, session) = await ConnectedPairAsync();
            var dave = $"dave@{Server.Domain}";

            await session.SendAsync(
                $"<iq type='set' id='push-2' to='{client.FullJid}'>" +
                "<query xmlns='jabber:iq:roster'>" +
                $"<item jid='{dave}' name='Dave' subscription='both'>" +
                "<group>Work</group><group>Project X</group>" +
                "</item></query></iq>");

            await WaitFor(() => client.GetContact(dave) is not null, "the contact from the push");

            Assert.That(client.GetContact(dave)!.Groups,
                        Is.EquivalentTo(new[] { "Work", "Project X" }));

        }

        #endregion

        #region RosterPush_UnescapesEntitiesInNames()

        /// <summary>
        /// A display name with an <c>&amp;</c> comes over the wire escaped and
        /// belongs resolved.
        /// </summary>
        [Test]
        public async Task RosterPush_UnescapesEntitiesInNames()
        {

            var (client, session) = await ConnectedPairAsync();
            var eve = $"eve@{Server.Domain}";

            await session.SendAsync(
                $"<iq type='set' id='push-3' to='{client.FullJid}'>" +
                "<query xmlns='jabber:iq:roster'>" +
                $"<item jid='{eve}' name='Eve &amp; Co. &lt;Support&gt;' subscription='both'/>" +
                "</query></iq>");

            await WaitFor(() => client.GetContact(eve) is not null, "the contact from the push");

            Assert.That(client.GetContact(eve)!.Name, Is.EqualTo("Eve & Co. <Support>"));

        }

        #endregion

        #region Message_WithUnusualButValidSpelling_IsStillDelivered()

        /// <summary>
        /// Control group: quotation mark style, attribute order and additional
        /// whitespace in the tag are valid and worked before as well. These
        /// cases must not break through the change.
        /// </summary>
        [Test]
        public async Task Message_WithUnusualButValidSpelling_IsStillDelivered()
        {

            var (client, session) = await ConnectedPairAsync();

            var received = await DeliverAsync(client, session,
                               "<message   type=\"chat\"   id=\"m5\"\n" +
                               $"           to=\"{client.FullJid}\"\n" +
                               $"           from=\"bob@{Server.Domain}/x\" >" +
                               "<body>Double quotation marks</body></message>");

            Assert.Multiple(() =>
            {
                Assert.That(received?.Body,       Is.EqualTo("Double quotation marks"));
                Assert.That(received?.MessageId,  Is.EqualTo("m5"));
            });

        }

        #endregion

    }

}
