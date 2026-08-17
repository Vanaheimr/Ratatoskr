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
    /// XEP-0386, Bind 2: the resource is bound inside the login instead of by
    /// an <c>&lt;iq/&gt;</c> after it.
    /// </summary>
    /// <remarks>
    /// The saving is one round trip, and the change in who decides is larger
    /// than that: under RFC 6120 a client asks for a resource and usually gets
    /// it, under XEP-0386 it cannot ask at all. It may offer a tag, and the
    /// server builds the resource around it.
    /// </remarks>
    [TestFixture]
    public class InlineBindTests : AXMPPTests
    {

        #region TheLoginBinds_WithoutASeparateIq()

        /// <summary>
        /// The point of the extension, stated as the absence of a frame.
        /// </summary>
        /// <remarks>
        /// Counting the binding <c>&lt;iq/&gt;</c> rather than asserting
        /// something about how it felt: the client is bound either way, and the
        /// only external difference is whether that stanza was sent. If the
        /// inline path silently stopped working, everything else here would go
        /// on passing.
        /// </remarks>
        [Test]
        public async Task TheLoginBinds_WithoutASeparateIq()
        {

            var alice    = await ConnectClientAsync("alice");
            var session  = Server.SessionOf(alice.FullJid.ToString())!;

            Assert.Multiple(() =>
            {

                Assert.That(alice.Connection.BoundInline, Is.True);

                Assert.That(alice.FullJid, Does.Contain("/"),
                            "A full JID all the same - only reached differently.");

                Assert.That(session.Received.Any(f => f.Contains("urn:ietf:params:xml:ns:xmpp-bind",
                                                                 StringComparison.Ordinal)),
                            Is.False,
                            "The RFC 6120 binding stanza must not be sent as well.");

            });

        }

        #endregion

        #region TheSuccessCarriesTheFullJidAndABoundElement()

        /// <summary>
        /// <c>&lt;bound/&gt;</c> is the signal and
        /// <c>&lt;authorization-identifier/&gt;</c> the identity - and the
        /// identity is the *full* JID here, where without an inline binding it
        /// is the bare one.
        /// </summary>
        /// <remarks>
        /// Reading the resource out of the identifier without checking for
        /// <c>&lt;bound/&gt;</c> would mistake the two on any server that chose
        /// not to bind, so both are asserted.
        /// </remarks>
        [Test]
        public async Task TheSuccessCarriesTheFullJidAndABoundElement()
        {

            var alice    = await ConnectClientAsync("alice");
            var session  = Server.SessionOf(alice.FullJid.ToString())!;

            var success  = session.Sent.First(f => f.StartsWith("<success", StringComparison.Ordinal));

            Assert.Multiple(() =>
            {
                Assert.That(success, Does.Contain("<bound xmlns='urn:xmpp:bind:0'/>"));
                Assert.That(success, Does.Contain($"<authorization-identifier>{alice.FullJid}<"));
            });

        }

        #endregion

        #region TheTagBecomesThePrefixOfTheResource()

        /// <summary>
        /// XEP-0386 recommends <c>tag/server-generated</c>, and the tag is the
        /// only influence a client has over its own resource.
        /// </summary>
        /// <remarks>
        /// Which also means the resource contains a slash - and that is not a
        /// curiosity but the case most likely to break something downstream.
        /// RFC 7622 splits a JID at the *first* slash, so everything after it,
        /// further slashes included, is the resourcepart. A parser that split
        /// at the last one would produce a different resource and a bare JID
        /// that is not this account.
        /// </remarks>
        [Test]
        public async Task TheTagBecomesThePrefixOfTheResource()
        {

            Server.AddAccount("alice");

            var alice = CreateClient("alice");
            alice.Connection.BindTag = "Ratatoskr";

            await alice.ConnectAsync();

            var parsed = JidUtilities.Parse(alice.FullJid);

            Assert.Multiple(() =>
            {

                Assert.That(parsed.Resourcepart, Does.StartWith("Ratatoskr/"),
                            "The tag is carried into the resource as a prefix.");

                Assert.That(parsed.Localpart,  Is.EqualTo("alice"));
                Assert.That(parsed.Domainpart, Is.EqualTo(Server.Domain));

                Assert.That(alice.Connection.Resource, Is.EqualTo(parsed.Resourcepart),
                            "And the connection knows the resource it was given.");

            });

        }

        #endregion

        #region TwoClientsWithTheSameTag_GetDifferentResources()

        /// <summary>
        /// The tag is a hint, not an identity: the server-generated tail is
        /// what keeps two of the same client apart.
        /// </summary>
        /// <remarks>
        /// Drawn at random rather than counted up, deliberately - a counter in
        /// the resource would tell every client how many others of its kind are
        /// connected to the account.
        /// </remarks>
        [Test]
        public async Task TwoClientsWithTheSameTag_GetDifferentResources()
        {

            Server.AddAccount("alice");

            var first = CreateClient("alice");
            first.Connection.BindTag = "Phone";
            await first.ConnectAsync();

            var second = CreateClient("alice");
            second.Connection.BindTag = "Phone";
            await second.ConnectAsync();

            Assert.Multiple(() =>
            {

                Assert.That(first.FullJid,  Does.Contain("Phone/"));
                Assert.That(second.FullJid, Does.Contain("Phone/"));

                Assert.That(second.FullJid, Is.Not.EqualTo(first.FullJid),
                            "Two sessions of one account may not share a resource.");

            });

        }

        #endregion

        #region WithoutTheInlineOffer_TheIqBindingIsUsed()

        /// <summary>
        /// A server that offers no inline binding is not left behind - which is
        /// every server that has not implemented XEP-0386.
        /// </summary>
        [Test]
        public async Task WithoutTheInlineOffer_TheIqBindingIsUsed()
        {

            Server.OfferBind2 = false;

            var alice    = await ConnectClientAsync("alice");
            var session  = Server.SessionOf(alice.FullJid.ToString())!;

            Assert.Multiple(() =>
            {

                Assert.That(alice.Connection.BoundInline, Is.False);
                Assert.That(alice.IsConnected,            Is.True);
                Assert.That(alice.FullJid,                Does.Contain("/"));

                Assert.That(session.Received.Any(f => f.Contains("urn:ietf:params:xml:ns:xmpp-bind",
                                                                 StringComparison.Ordinal)),
                            Is.True,
                            "The RFC 6120 route, taken because the newer one was not offered.");

            });

        }

        #endregion

        #region AClientThatDeclines_UsesTheIqBinding()

        /// <summary>
        /// The other half of the switch: the offer stands and the client passes
        /// it up.
        /// </summary>
        /// <remarks>
        /// Worth having because it is what keeps the RFC 6120 binding reachable
        /// against a server that has moved on - and that path is still what
        /// nearly every deployment in the world speaks.
        /// </remarks>
        [Test]
        public async Task AClientThatDeclines_UsesTheIqBinding()
        {

            Server.AddAccount("alice");

            var alice = CreateClient("alice");
            alice.Connection.UseInlineBind = false;

            await alice.ConnectAsync();

            Assert.Multiple(() =>
            {
                Assert.That(alice.Connection.BoundInline, Is.False);
                Assert.That(alice.IsConnected,            Is.True);
                Assert.That(alice.FullJid,                Does.Contain("/"));
            });

        }

        #endregion

        #region AnInlineBoundSession_ReceivesMessages()

        /// <summary>
        /// The binding has to reach everything that waits for one, not only the
        /// client's own idea of its JID.
        /// </summary>
        /// <remarks>
        /// The <c>&lt;iq/&gt;</c> route fires OnSessionBound and flushes what a
        /// real server delivers right after a binding; the inline route has to
        /// do the same, and forgetting it would leave a session that looks
        /// connected on both ends and receives nothing. Delivery is the
        /// shortest statement of "properly bound" there is.
        /// </remarks>
        [Test]
        public async Task AnInlineBoundSession_ReceivesMessages()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);
            var bob   = await ConnectClientAsync("bob",   createAccount: false);

            Assert.That(alice.Connection.BoundInline, Is.True, "The premise.");

            XMPPMessage? arrived = null;
            bob.OnMessage += (timestamp, sender, m, ct) => { arrived = m; return Task.CompletedTask; };

            await alice.Connection.SendMessageAsync(JID.Parse($"bob@{Server.Domain}"), "over an inline binding");

            await WaitFor(() => arrived is not null, "the message at Bob");

            Assert.That(arrived!.Body, Is.EqualTo("over an inline binding"));

        }

        #endregion

    }

}
