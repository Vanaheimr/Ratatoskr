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
    /// XEP-0030, section 3.2: "If the request included a 'node' attribute, the
    /// response MUST mirror the specified 'node' attribute to ensure coherence
    /// between the request and the response."
    /// </summary>
    /// <remarks>
    /// The mirroring is no decoration. XEP-0115, section 6.2 lets a far end ask
    /// with <c>node#ver</c> and stores the answer under precisely this key; the
    /// <c>node</c> given back is the assurance that the answer belongs to the
    /// question. If it is missing, a strict far end does not fill its cache and
    /// asks anew at every presence - the use of XEP-0115 falls away without
    /// anything looking broken.
    ///
    /// We demanded that of others for a long time and did not deliver it
    /// ourselves: <c>EntityCapsManager</c> has asked with <c>node#ver</c> all
    /// along, our own answer never carried a <c>node</c>.
    ///
    /// The second half is the more unpleasant one: A node that does not exist
    /// here was answered up to then just like none at all - with the full
    /// feature list. With that this side claimed to carry every node ever
    /// thought up. An outdated <c>ver</c> expressly belongs to it: It asks
    /// after the state of back then, and that does not exist any more. To send
    /// the current list would be the wrong answer to the right question - the
    /// asker recalculates the announced hash, gets a different one and would
    /// have to take us for a forger.
    /// </remarks>
    [TestFixture]
    public class DiscoNodeTests : AXMPPTests
    {

        #region Helper functions

        /// <summary>
        /// Queries the <b>client</b> over its server session for disco#info and
        /// returns its answer raw.
        /// </summary>
        private async Task<String> AskTheClientAsync(XMPPSession  session,
                                                     String       id,
                                                     String?      node)
        {

            var nodeAttribute = node is not null ? $" node='{node}'" : "";

            await session.SendAsync(
                      $"<iq type='get' id='{id}' from='{Server.Domain}' to='{session.FullJid}'>" +
                      $"<query xmlns='http://jabber.org/protocol/disco#info'{nodeAttribute}/></iq>");

            await WaitFor(() => session.Received.Any(f => f.Contains($"id='{id}'", StringComparison.Ordinal)),
                          $"the disco#info answer to '{id}'");

            return session.Received.First(f => f.Contains($"id='{id}'", StringComparison.Ordinal));

        }

        /// <summary>
        /// The bound session of the only logged-in client.
        /// </summary>
        private async Task<XMPPSession> SessionAsync(XMPPClient client)
        {

            await WaitFor(() => Server.Sessions.Any(s => JID.AreEqual(s.FullJid, client.Connection.FullJid.ToString())),
                          "the bound session of the client");

            return Server.Sessions.First(s => JID.AreEqual(s.FullJid, client.Connection.FullJid.ToString()));

        }

        /// <summary>
        /// Queries the <b>server</b> at its own address for disco#info and
        /// returns its answer raw.
        /// </summary>
        private async Task<String> AskTheServerAsync(XMPPClient  client,
                                                     String      id,
                                                     String?     node)
        {

            var nodeAttribute = node is not null ? $" node='{node}'" : "";
            var replies       = new ConcurrentQueue<String>();

            client.Connection.OnRawXml += (timestamp, sender, xml, ct) =>
            {
                if (xml.Contains("<<<", StringComparison.Ordinal) &&
                    xml.Contains(id,    StringComparison.Ordinal))
                {
                    replies.Enqueue(xml);
                }

                return Task.CompletedTask;

            };

            await client.SendRawAsync(
                      $"<iq type='get' id='{id}' to='{Server.Domain}'>" +
                      $"<query xmlns='http://jabber.org/protocol/disco#info'{nodeAttribute}/></iq>");

            await WaitFor(() => !replies.IsEmpty, $"the disco#info answer of the server to '{id}'");

            replies.TryDequeue(out var reply);

            return reply!;

        }

        #endregion


        #region WithoutANode_TheAnswerCarriesNone()

        /// <summary>
        /// The counter-check first: Without a <c>node</c> in the question the
        /// answer must carry none either.
        /// </summary>
        /// <remarks>
        /// Without this test "always hang some <c>node</c> on" would be a
        /// passing solution. It would be wrong: A <c>node</c> in the answer to
        /// a question without a node claims the information holds only for one
        /// part.
        /// </remarks>
        [Test]
        public async Task WithoutANode_TheAnswerCarriesNone()
        {

            var client  = await ConnectClientAsync("alice");
            var session = await SessionAsync(client);

            var reply = await AskTheClientAsync(session, "without-node", null);

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='result'"));
                Assert.That(reply, Does.Not.Contain("node="),
                            $"The answer carries a node although none was asked for: {reply}");
            });

        }

        #endregion

        #region OurOwnCapsNode_IsMirrored()

        /// <summary>
        /// The core: the question after <c>node#ver</c> - the way XEP-0115,
        /// section 6.2 puts it - gets the same <c>node</c> back.
        /// </summary>
        [Test]
        public async Task OurOwnCapsNode_IsMirrored()
        {

            var client  = await ConnectClientAsync("alice");
            var session = await SessionAsync(client);

            var caps    = client.Connection.EntityCaps!;
            var node    = $"{caps.Node}#{caps.CalculateVerificationString()}";

            var reply = await AskTheClientAsync(session, "caps-node", node);

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='result'"));
                Assert.That(reply, Does.Contain($"node='{node}'"),
                            $"The node of the question is missing in the answer: {reply}");
                Assert.That(reply, Does.Contain("<identity"),
                            "The answer to one's own caps node is the full information.");
                Assert.That(reply, Does.Contain("urn:xmpp:ping"));
            });

        }

        #endregion

        #region TheBareCapsNode_IsAnsweredToo()

        /// <summary>
        /// The caps node without <c>#ver</c> designates us as well and is
        /// answered.
        /// </summary>
        /// <remarks>
        /// XEP-0115 says "SHOULD" to the form <c>node#ver</c>, not "MUST".
        /// Whoever names only the node asks after this entity without nailing a
        /// state down - that is answerable, and with today's one.
        /// </remarks>
        [Test]
        public async Task TheBareCapsNode_IsAnsweredToo()
        {

            var client  = await ConnectClientAsync("alice");
            var session = await SessionAsync(client);

            var node    = client.Connection.EntityCaps!.Node;

            var reply = await AskTheClientAsync(session, "bare-node", node);

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='result'"));
                Assert.That(reply, Does.Contain($"node='{node}'"));
            });

        }

        #endregion

        #region AStaleVerificationString_IsRefused()

        /// <summary>
        /// A <c>ver</c> that is not ours any more asks after a state that does
        /// not exist any more - and gets <c>&lt;item-not-found/&gt;</c>.
        /// </summary>
        /// <remarks>
        /// That is the decision of this point, and it is uncomfortable:
        /// widespread servers send the current list here. That is more
        /// convenient and wrong. The asker checks the announced hash against
        /// the answer according to XEP-0115, section 5.4; if they get the new
        /// list for an old <c>ver</c>, it yields a different hash. They then
        /// have the choice of taking us for a forger or of giving the check up
        /// - our own <c>EntityCapsManager</c> would refuse the answer. An error
        /// is the more honest information: this state does not exist here any
        /// more.
        /// </remarks>
        [Test]
        public async Task AStaleVerificationString_IsRefused()
        {

            var client  = await ConnectClientAsync("alice");
            var session = await SessionAsync(client);

            var node    = $"{client.Connection.EntityCaps!.Node}#FromYesterdayXXXXXXXXXXX=";

            var reply = await AskTheClientAsync(session, "stale-ver", node);

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='error'"));
                Assert.That(reply, Does.Contain("<item-not-found"));
                Assert.That(reply, Does.Contain("urn:ietf:params:xml:ns:xmpp-stanzas"));
                Assert.That(reply, Does.Contain($"node='{node}'"),
                            "The error names the question it answers as well " +
                            $"(RFC 6120, section 8.3.1): {reply}");
                Assert.That(reply, Does.Not.Contain("<identity"),
                            $"An error carries no information: {reply}");
            });

        }

        #endregion

        #region AForeignNode_IsRefused()

        /// <summary>
        /// A node that has nothing to do with us gets
        /// <c>&lt;item-not-found/&gt;</c> as well instead of the full list.
        /// </summary>
        [Test]
        public async Task AForeignNode_IsRefused()
        {

            var client  = await ConnectClientAsync("alice");
            var session = await SessionAsync(client);

            var reply = await AskTheClientAsync(session, "foreign-node",
                                                "http://jabber.org/protocol/commands");

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='error'"));
                Assert.That(reply, Does.Contain("<item-not-found"));
                Assert.That(reply, Does.Not.Contain("<feature"),
                            $"An unknown node gets no feature list: {reply}");
            });

        }

        #endregion

        #region TheServerAnswersWithoutANode()

        /// <summary>
        /// The same counter-check for the server: the ordinary question stays
        /// answered unchanged.
        /// </summary>
        [Test]
        public async Task TheServerAnswersWithoutANode()
        {

            var client  = await ConnectClientAsync("alice");

            var reply = await AskTheServerAsync(client, "server-without-node", null);

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='result'"));
                Assert.That(reply, Does.Contain("category='server'"));
                Assert.That(reply, Does.Not.Contain("node="),
                            $"The answer carries a node although none was asked for: {reply}");
            });

        }

        #endregion

        #region ANodeOutsideTheQuery_DoesNotCount()

        /// <summary>
        /// A <c>node</c> attribute that does not belong to the query does not
        /// make the query into one after a node.
        /// </summary>
        /// <remarks>
        /// The server reads its frames as strings, not as a tree - on purpose,
        /// because it is not supposed to look at the client with the same
        /// glasses the client looks at itself with. The price is that "does
        /// <c>node=</c> stand anywhere in the frame" and "does the query carry
        /// a <c>node</c>" are two different things. Without this test the
        /// difference would be unestablished, and the ordinary query of a
        /// client enclosing anything with its request would get an error.
        /// </remarks>
        [Test]
        public async Task ANodeOutsideTheQuery_DoesNotCount()
        {

            var client  = await ConnectClientAsync("alice");

            var replies = new ConcurrentQueue<String>();

            client.Connection.OnRawXml += (timestamp, sender, xml, ct) =>
            {
                if (xml.Contains("<<<",           StringComparison.Ordinal) &&
                    xml.Contains("node-beside",  StringComparison.Ordinal))
                {
                    replies.Enqueue(xml);
                }

                return Task.CompletedTask;

            };

            await client.SendRawAsync(
                      $"<iq type='get' id='node-beside' to='{Server.Domain}'>" +
                      "<query xmlns='http://jabber.org/protocol/disco#info'/>" +
                      "<enclosure xmlns='urn:example:whatever' node='not-asked'/></iq>");

            await WaitFor(() => !replies.IsEmpty, "the disco#info answer of the server");

            replies.TryDequeue(out var reply);

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='result'"),
                            $"The node of the enclosure does not belong to the query: {reply}");
                Assert.That(reply, Does.Contain("category='server'"));
            });

        }

        #endregion

        #region TheServerHasNoNodes()

        /// <summary>
        /// The server announces no capabilities and carries no nodes - a
        /// question after a node gets <c>&lt;item-not-found/&gt;</c>.
        /// </summary>
        /// <remarks>
        /// The switch <c>FailDiscoInfo</c> has always answered with the text
        /// "This node does not exist here." - a statement the server could not
        /// make at all, because it never looked at the attribute. Now it holds.
        /// </remarks>
        [Test]
        public async Task TheServerHasNoNodes()
        {

            var client  = await ConnectClientAsync("alice");

            var reply = await AskTheServerAsync(client, "server-with-node",
                                                "http://jabber.org/protocol/offline");

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='error'"));
                Assert.That(reply, Does.Contain("<item-not-found"));
                Assert.That(reply, Does.Contain("node='http://jabber.org/protocol/offline'"),
                            "The error names the question it answers as well " +
                            $"(RFC 6120, section 8.3.1): {reply}");
                Assert.That(reply, Does.Not.Contain("category='server'"),
                            $"An unknown node gets no information: {reply}");
            });

        }

        #endregion

    }

}
