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
    /// XEP-0030, section 4: disco#items - the sub-units of an entity.
    /// </summary>
    /// <remarks>
    /// The occasion is not a missing feature but a false promise.
    /// <c>LocalFeatures</c> has carried
    /// <c>http://jabber.org/protocol/disco#items</c> all along, an items query
    /// was never answered: it fell through to the
    /// <c>&lt;service-unavailable/&gt;</c>. Announced and then refused is the
    /// one combination that must not exist - a far end believing the features
    /// gets an error on a question it was invited to put.
    ///
    /// The answer is an <b>empty</b> list, and that is no makeshift: A client
    /// has no sub-units. "I have none" and "do not ask me" are different
    /// pieces of information, and only the first one is true here.
    /// </remarks>
    [TestFixture]
    public class DiscoItemsTests : AXMPPTests
    {

        #region Helper functions

        /// <summary>
        /// Queries the client over its server session and returns its answer
        /// raw.
        /// </summary>
        private async Task<String> AskTheClientAsync(XMPPSession  session,
                                                     String       id,
                                                     String       ns,
                                                     String?      node = null)
        {

            var nodeAttribute = node is not null ? $" node='{node}'" : "";

            await session.SendAsync(
                      $"<iq type='get' id='{id}' from='{Server.Domain}' to='{session.FullJid}'>" +
                      $"<query xmlns='{ns}'{nodeAttribute}/></iq>");

            await WaitFor(() => session.Received.Any(f => f.Contains($"id='{id}'", StringComparison.Ordinal)),
                          $"the answer to '{id}'");

            return session.Received.First(f => f.Contains($"id='{id}'", StringComparison.Ordinal));

        }

        /// <summary>The bound session of the logged-in client.</summary>
        private async Task<XMPPSession> SessionAsync(XMPPClient client)
        {

            await WaitFor(() => Server.Sessions.Any(s => JID.AreEqual(s.FullJid, client.Connection.FullJid.ToString())),
                          "the bound session of the client");

            return Server.Sessions.First(s => JID.AreEqual(s.FullJid, client.Connection.FullJid.ToString()));

        }

        #endregion


        #region TheAnnouncedFeature_IsActuallyAnswered()

        /// <summary>
        /// The core: what stands in the feature list has to be answered as
        /// well. Both in one test, because only the contradiction is the error.
        /// </summary>
        [Test]
        public async Task TheAnnouncedFeature_IsActuallyAnswered()
        {

            var client   = await ConnectClientAsync("alice");
            var session  = await SessionAsync(client);

            var features = await AskTheClientAsync(session, "features",
                                                   "http://jabber.org/protocol/disco#info");

            var items = await AskTheClientAsync(session, "items",
                                                "http://jabber.org/protocol/disco#items");

            Assert.Multiple(() =>
            {

                Assert.That(features, Does.Contain("var='http://jabber.org/protocol/disco#items'"),
                            "Without the announcement there would be nothing to check here.");

                Assert.That(items, Does.Contain("type='result'"),
                            $"Announced and then refused: {items}");

                Assert.That(items, Does.Contain("xmlns='http://jabber.org/protocol/disco#items'"),
                            $"The answer belongs to the question that was put: {items}");

            });

        }

        #endregion

        #region WithoutItems_TheListIsEmptyAndNotTheInfoAnswer()

        /// <summary>
        /// A client without sub-units answers with an empty list - and not with
        /// its feature list.
        /// </summary>
        /// <remarks>
        /// The second half is the counter-check against the most obvious
        /// shortcut: to hang the items query onto the info answer. It would be
        /// green for "something comes back" and wrong in everything else.
        /// </remarks>
        [Test]
        public async Task WithoutItems_TheListIsEmptyAndNotTheInfoAnswer()
        {

            var client  = await ConnectClientAsync("alice");
            var session = await SessionAsync(client);

            var reply = await AskTheClientAsync(session, "empty",
                                                "http://jabber.org/protocol/disco#items");

            Assert.Multiple(() =>
            {

                Assert.That(reply, Does.Contain("type='result'"));

                Assert.That(reply, Does.Not.Contain("<item "),
                            $"This client has no sub-units: {reply}");

                Assert.That(reply, Does.Not.Contain("<identity"),
                            $"That is the answer to disco#info, not to disco#items: {reply}");

                Assert.That(reply, Does.Not.Contain("<feature"),
                            $"That is the answer to disco#info, not to disco#items: {reply}");

                Assert.That(reply, Does.Not.Contain("node="),
                            $"Without a node in the question none in the answer: {reply}");

            });

        }

        #endregion

        #region ConfiguredItems_AreListed()

        /// <summary>
        /// What stands in <c>LocalItems</c> stands in the answer as well - with
        /// <c>jid</c>, <c>node</c> and <c>name</c>.
        /// </summary>
        /// <remarks>
        /// Without this test "always an empty list" would be a passing
        /// solution, and the list would stay an ornament.
        /// </remarks>
        [Test]
        public async Task ConfiguredItems_AreListed()
        {

            var client  = await ConnectClientAsync("alice");
            var session = await SessionAsync(client);

            client.Connection.Disco!.LocalItems.Add(
                new DiscoItem("service.example.test", "urn:example:branch", "A service"));

            var reply = await AskTheClientAsync(session, "with-content",
                                                "http://jabber.org/protocol/disco#items");

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("jid='service.example.test'"));
                Assert.That(reply, Does.Contain("node='urn:example:branch'"));
                Assert.That(reply, Does.Contain("name='A service'"));
            });

        }

        #endregion

        #region AnItemsRequestWithNode_IsRefused()

        /// <summary>
        /// A branch that does not exist here gets
        /// <c>&lt;item-not-found/&gt;</c> - the same rule as with disco#info
        /// (see D39).
        /// </summary>
        /// <remarks>
        /// For disco#items a <c>node</c> is a branch in the tree of sub-units,
        /// not the caps node from XEP-0115. This client has not a single one.
        /// An empty list would be the wrong answer here: It would mean "this
        /// branch exists, it is empty" instead of "this branch does not exist".
        /// </remarks>
        [Test]
        public async Task AnItemsRequestWithNode_IsRefused()
        {

            var client  = await ConnectClientAsync("alice");
            var session = await SessionAsync(client);

            var reply = await AskTheClientAsync(session, "branch",
                                                "http://jabber.org/protocol/disco#items",
                                                 "urn:example:no-branch");

            Assert.Multiple(() =>
            {

                Assert.That(reply, Does.Contain("type='error'"));
                Assert.That(reply, Does.Contain("<item-not-found"));

                Assert.That(reply, Does.Contain("node='urn:example:no-branch'"),
                            "The error names the question it answers as well " +
                            $"(RFC 6120, section 8.3.1): {reply}");

                Assert.That(reply, Does.Contain("xmlns='http://jabber.org/protocol/disco#items'"),
                            $"The error takes the items request back, not just any: {reply}");

            });

        }

        #endregion

    }

}
