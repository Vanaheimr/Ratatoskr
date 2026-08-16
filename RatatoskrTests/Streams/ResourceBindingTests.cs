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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// RFC 6120, section 7: the resource binding.
    ///
    /// The client asked for <c>console-&lt;process id&gt;</c> and had no plan
    /// B. A server that refuses a resource already in use instead of handing
    /// out a free one itself - section 7.7.2.2 leaves it the choice - thereby
    /// made every second client in the same process fail.
    /// </summary>
    [TestFixture]
    public class ResourceBindingTests : AXMPPTests
    {

        #region Setup

        /// <summary>
        /// The RFC 6120 binding for this whole fixture, which is what it is
        /// about.
        /// </summary>
        /// <remarks>
        /// XEP-0386 does not extend that binding, it replaces it - and takes
        /// the client's say in the matter along with it. There a client cannot
        /// ask for a resource at all: it offers a tag and the server generates
        /// <c>tag/something</c> around it. So "the configured resource is
        /// requested" and "a rejected binding is not retried" are not questions
        /// that can be put on the inline path - there is no request to make and
        /// no <c>&lt;iq/&gt;</c> to reject.
        ///
        /// Which is exactly why it stays measured here. Nearly every server in
        /// the world still speaks only the older binding, and with Bind 2
        /// preferred everywhere else this fixture is where their route is
        /// exercised. InlineBindTests covers the newer one.
        /// </remarks>
        [SetUp]
        public void UseTheIqBinding()
        {
            Server.OfferBind2 = false;
        }

        #endregion

        #region Helper functions

        /// <summary>
        /// Creates a client without reconnecting.
        /// </summary>
        /// <remarks>
        /// A failed binding otherwise makes the client connect anew up to
        /// twenty times. These tests ask whether the binding copes
        /// <b>itself</b>, though - finding the way to the goal over a reconnect
        /// would be no answer to that, only a slow repetition of the same
        /// question.
        /// </remarks>
        private XMPPClient SingleAttemptClient(String localPart = "alice")
        {

            if (Server.GetAccount($"{localPart}@{Server.Domain}") is null)
                Server.AddAccount(localPart);

            var client = CreateClient(localPart);
            client.Connection.MaxReconnectAttempts = 0;

            return client;

        }

        #endregion


        #region ConflictingResource_IsRetriedWithoutOne()

        /// <summary>
        /// The heart of it: after a <c>&lt;conflict/&gt;</c> the client binds
        /// once more without a wish and takes what the server hands out.
        /// </summary>
        [Test]
        public async Task ConflictingResource_IsRetriedWithoutOne()
        {

            Server.ConflictOnUsedResource = true;

            var first  = await ConnectClientAsync("alice");
            var second = SingleAttemptClient();

            await second.ConnectAsync();

            Assert.Multiple(() =>
            {
                Assert.That(second.IsConnected, Is.True,
                            "The second resource has to come about despite the conflict.");
                Assert.That(second.FullJid, Is.Not.EqualTo(first.FullJid));
                Assert.That(second.BareJid, Is.EqualTo(first.BareJid));
            });

        }

        #endregion

        #region ConflictingResource_KeepsBothSessionsUsable()

        /// <summary>
        /// The newly handed-out resource must be addressable as well - otherwise
        /// the client would be connected, but under a JID the server does not
        /// know.
        /// </summary>
        [Test]
        public async Task ConflictingResource_KeepsBothSessionsUsable()
        {

            Server.ConflictOnUsedResource = true;

            await ConnectClientAsync("alice");
            var second = SingleAttemptClient();

            await second.ConnectAsync();

            await WaitFor(() => Server.SessionOf(second.FullJid) is not null,
                          "the server session for the newly handed-out resource");

        }

        #endregion

        #region NonConflictRejection_IsNotRetried()

        /// <summary>
        /// Only a conflict justifies the second attempt. If the binding is
        /// refused for another reason, the same error would come back - the
        /// client breaks off instead of trying again.
        /// </summary>
        [Test]
        public async Task NonConflictRejection_IsNotRetried()
        {

            Server.FailBind = true;

            var client  = SingleAttemptClient();
            var errors  = new List<String>();

            client.OnError += e => errors.Add(e);

            await FailingConnectAsync(client);

            Assert.Multiple(() =>
            {
                Assert.That(client.IsConnected, Is.False);
                Assert.That(errors, Is.Not.Empty);
                Assert.That(Server.AllReceived.Count(f => f.Contains("urn:ietf:params:xml:ns:xmpp-bind",
                                                                     StringComparison.Ordinal)),
                            Is.EqualTo(1),
                            "A refusal without a conflict deserves exactly one attempt.");
            });

        }

        #endregion

        #region ConfiguredResource_IsRequested()

        /// <summary>
        /// <c>console-&lt;process id&gt;</c> was hard-wired - in a library
        /// doubly unfitting: the name claims a console, and two users of the
        /// same library in the same process got the same wish.
        /// </summary>
        [Test]
        public async Task ConfiguredResource_IsRequested()
        {

            var client = SingleAttemptClient();
            client.Connection.Resource = "phone";

            await client.ConnectAsync();

            Assert.That(client.FullJid, Does.EndWith("/phone"));

        }

        #endregion

        #region NoResource_LetsTheServerChoose()

        /// <summary>
        /// Without a wish the server hands one out (RFC 6120, section 7.6).
        /// That is the same way the client goes after a conflict.
        /// </summary>
        [Test]
        public async Task NoResource_LetsTheServerChoose()
        {

            var client = SingleAttemptClient();
            client.Connection.Resource = null;

            await client.ConnectAsync();

            Assert.Multiple(() =>
            {
                Assert.That(client.IsConnected, Is.True);
                Assert.That(client.FullJid, Does.Contain("/"));
            });

        }

        #endregion

        #region UsedResource_IsVariedByDefault()

        /// <summary>
        /// The counter-check to the default: without the switch the server
        /// hands out a differing resource itself, and the client notices
        /// nothing of the conflict. That is how the widespread servers behave.
        /// </summary>
        [Test]
        public async Task UsedResource_IsVariedByDefault()
        {

            var first  = await ConnectClientAsync("alice");
            var second = await ConnectClientAsync("alice");

            Assert.Multiple(() =>
            {
                Assert.That(second.IsConnected, Is.True);
                Assert.That(second.FullJid, Is.Not.EqualTo(first.FullJid));
            });

        }

        #endregion

    }

}
