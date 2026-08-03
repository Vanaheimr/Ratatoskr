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
    /// The target picture from the work plan: two servers with different
    /// domains, a real client at each, and a message goes from the one to the
    /// other.
    ///
    /// The two are connected over <see cref="DirectServerLinks"/>, so without a
    /// net in between. What is <b>not</b> checked here is therefore just as
    /// important as what is checked: there is no stream, no TLS between the
    /// servers, no dialback. What is checked is routing, addressing and
    /// delivery across the domain border - and the sender check a real
    /// transport builds on later.
    /// </summary>
    [TestFixture]
    public class FederationTests
    {

        #region Data

        private XMPPServer _left  = null!;
        private XMPPServer _right = null!;
        private readonly List<XMPPClient> _clients = [];
        private readonly InternalErrorGuard _guard = new();

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void TwoServers()
        {

            // The guard on both: An error on the one server often comes about
            // through a stanza the other one sent.
            _guard.Reset();

            _left   = _guard.Watched(new XMPPServer("left.example"));
            _right  = _guard.Watched(new XMPPServer("right.example"));

            _left.Start();
            _right.Start();

            DirectServerLinks.Connect(_left, _right);

        }

        [TearDown]
        public async Task CleanUp()
        {

            foreach (var client in _clients)
            {
                try { await client.DisposeAsync(); }
                catch { /* does not matter in the teardown */ }
            }

            _clients.Clear();

            await _left.DisposeAsync();
            await _right.DisposeAsync();

            _guard.AssertClean();

        }

        #endregion

        #region Helper functions

        /// <summary>Connects a client to one of the two servers.</summary>
        private async Task<XMPPClient> ConnectAsync(XMPPServer server, String localPart)
        {

            if (server.GetAccount($"{localPart}@{server.Domain}") is null)
                server.AddAccount(localPart);

            var connection = new XMPPConnection($"{localPart}@{server.Domain}",
                                                "pw",
                                                server.Uri)
            {
                KeepaliveEnabled            = false,
                MaxReconnectAttempts        = 0,
                ServerCertificateValidator  = server.IsOwnCertificate
            };

            var client = new XMPPClient(connection);
            _clients.Add(client);

            await client.ConnectAsync();

            return client;

        }

        /// <summary>
        /// Enters a contact into both rosters across the domain border, as it
        /// would be after a complete subscription handshake.
        /// </summary>
        private void MakeContacts(XMPPClient a, XMPPClient b)
        {

            _left.GetAccount(a.BareJid)!.SetRosterEntry(new RosterEntry(b.BareJid, null, "both"));
            _right.GetAccount(b.BareJid)!.SetRosterEntry(new RosterEntry(a.BareJid, null, "both"));

        }

        private static async Task WaitFor(Func<Boolean> condition, String what)
        {
            Assert.That(await XMPPServer.WaitUntilAsync(condition),
                        Is.True, $"Timeout while waiting for: {what}");
        }

        #endregion


        #region MessageCrossesTheDomainBoundary()

        /// <summary>
        /// The core of the whole point.
        /// </summary>
        [Test]
        public async Task MessageCrossesTheDomainBoundary()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            bob.OnMessage += m => received.Add(m);

            await alice.SendMessageAsync(bob.BareJid, "Hello across the border!");

            await WaitFor(() => received.Count > 0, "the message on the other server");

            Assert.Multiple(() =>
            {
                Assert.That(received[0].Body,         Is.EqualTo("Hello across the border!"));
                Assert.That(received[0].FromBareJid,  Is.EqualTo("alice@left.example"));
            });

        }

        #endregion

        #region TheAnswerFindsItsWayBack()

        /// <summary>
        /// And back - the direction is not the same question, because it runs
        /// over the second half of the wiring.
        /// </summary>
        [Test]
        public async Task TheAnswerFindsItsWayBack()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var atBob    = new List<XMPPMessage>();
            var atAlice  = new List<XMPPMessage>();

            bob.OnMessage    += m => atBob.Add(m);
            alice.OnMessage  += m => atAlice.Add(m);

            await alice.SendMessageAsync(bob.BareJid, "Question");
            await WaitFor(() => atBob.Count > 0, "the question at Bob's");

            await bob.SendMessageAsync(atBob[0].FromBareJid, "Answer");
            await WaitFor(() => atAlice.Count > 0, "the answer at Alice's");

            Assert.Multiple(() =>
            {
                Assert.That(atAlice[0].Body,         Is.EqualTo("Answer"));
                Assert.That(atAlice[0].FromBareJid,  Is.EqualTo("bob@right.example"));
            });

        }

        #endregion

        #region PresenceCrossesTheBoundary()

        /// <summary>
        /// Presence takes the same path - to contacts with <c>from</c> or
        /// <c>both</c>, no matter which domain they lie on (RFC 6121,
        /// section 4.2.2).
        /// </summary>
        [Test]
        public async Task PresenceCrossesTheBoundary()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            MakeContacts(alice, bob);

            var seen = new List<(String From, String Type)>();
            bob.OnPresenceChanged += (from, type) => seen.Add((from, type));

            await alice.SetPresenceAsync();

            await WaitFor(() => seen.Any(g => g.From.StartsWith("alice@left.example", StringComparison.Ordinal)),
                          "Alice's presence at Bob's");

        }

        #endregion

        #region PresenceStaysAwayFromNonSubscribers()

        /// <summary>
        /// The counter-check: without permission nothing goes across the border
        /// either.
        /// </summary>
        /// <remarks>
        /// Without this test the previous one would pass even if the server
        /// simply sent presence to every foreign domain it knows.
        /// </remarks>
        [Test]
        public async Task PresenceStaysAwayFromNonSubscribers()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            // No MakeContacts: the two do not know each other.

            var seen = new List<String>();
            bob.OnPresenceChanged += (from, _) => seen.Add(from);

            await alice.SetPresenceAsync();

            var came = await XMPPServer.WaitUntilAsync(
                           () => seen.Any(f => f.StartsWith("alice@left.example", StringComparison.Ordinal)),
                          TimeSpan.FromSeconds(2));

            Assert.That(came, Is.False,
                        "Presence may only go to contacts with from or both.");

        }

        #endregion

        #region SpoofedSender_IsRejected()

        /// <summary>
        /// A far end may speak exclusively for its own domain.
        /// </summary>
        /// <remarks>
        /// That is the check for whose sake dialback exists at all. Without it
        /// every server one ever speaks to could smuggle in messages in the
        /// name of any other one - and a client would have no way of noticing.
        /// </remarks>
        [Test]
        public async Task SpoofedSender_IsRejected()
        {

            var bob = await ConnectAsync(_right, "bob");

            var received = new List<XMPPMessage>();
            var refused  = new List<(String Peer, String Reason)>();

            bob.OnMessage                  += m => received.Add(m);
            _right.OnRemoteStanzaRejected += (peer, reason) => refused.Add((peer, reason));

            // left.example claims to speak for a third domain.
            var accepted = await _right.ReceiveFromRemoteAsync(
                              "left.example",
                                 $"<message from='boss@bank.example' to='{bob.BareJid}' type='chat'>" +
                                 "<body>Please transfer 10000 euros.</body></message>");

            Assert.Multiple(() =>
            {
                Assert.That(accepted, Is.False, "The stanza should have been turned away.");
                Assert.That(received,  Is.Empty, "It must not reach the client.");
                Assert.That(refused, Is.Not.Empty, "The turning away has to be reported.");
            });

        }

        #endregion

        #region RelayingForThirdParties_IsRejected()

        /// <summary>
        /// And the server does not relay for third parties - a stanza to a
        /// domain that is not its own it does not even take in.
        /// </summary>
        /// <remarks>
        /// Otherwise it would be an open relay: whoever is connected once could
        /// write to any other domain over it and obscure the origin.
        /// </remarks>
        [Test]
        public async Task RelayingForThirdParties_IsRejected()
        {

            var refused = new List<String>();
            _right.OnRemoteStanzaRejected += (_, reason) => refused.Add(reason);

            var accepted = await _right.ReceiveFromRemoteAsync(
                              "left.example",
                                 "<message from='alice@left.example' to='who@faraway.example' type='chat'>" +
                                 "<body>Pass it on, please.</body></message>");

            Assert.Multiple(() =>
            {
                Assert.That(accepted, Is.False);
                Assert.That(refused, Is.Not.Empty);
            });

        }

        #endregion

        #region UnknownDomain_StillYieldsAnError()

        /// <summary>
        /// A domain there is no connection to still leads to the error - even
        /// when there are other connections.
        /// </summary>
        [Test]
        public async Task UnknownDomain_StillYieldsAnError()
        {

            var alice   = await ConnectAsync(_left, "alice");
            var errors  = new List<StanzaError>();

            alice.OnStanzaError += (_, e) => errors.Add(e);

            await alice.SendMessageAsync("who@faraway.example", "Hello?");

            await WaitFor(() => errors.Count > 0, "the error for the unknown domain");

            Assert.That(errors[0].Condition, Is.EqualTo("remote-server-not-found"));

        }

        #endregion

        #region ConnectingAServerToItself_IsRefused()

        /// <summary>
        /// To connect two servers on the same domain yields nothing and is
        /// almost surely an oversight.
        /// </summary>
        [Test]
        public async Task ConnectingAServerToItself_IsRefused()
        {

            await using var duplicate = _guard.Watched(new XMPPServer("left.example"));

            Assert.Throws<ArgumentException>(() => DirectServerLinks.Connect(_left, duplicate));

        }

        #endregion

        #region AStanzaFromAbroad_ReachesTheClientAsJabberClient()

        /// <summary>
        /// What comes in from a foreign server goes to the local client as
        /// <c>jabber:client</c> - not as <c>jabber:server</c>.
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 4.8.1 gives every stream its content namespace:
        /// <c>jabber:server</c> between servers, <c>jabber:client</c> on the
        /// client connection. At the transition it has to change along. The
        /// outbound direction was fixed already, the inbound one was left
        /// lying - our own client does not mind, because it recognises stanzas
        /// by the local name and does not look at the namespace at all.
        ///
        /// Precisely this leniency covered up the error on the other side for
        /// years: the client sent its stanzas out without any namespace, and
        /// nobody noticed until Prosody turned the bind IQ away.
        ///
        /// What is checked is on the wire, not at the event: what the client
        /// makes of it is a different question from what it gets.
        /// </remarks>
        [Test]
        public async Task AStanzaFromAbroad_ReachesTheClientAsJabberClient()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            MakeContacts(alice, bob);

            await alice.SendMessageAsync(bob.BareJid, "Across the border");

            var bobsSession = _right.SessionOf(bob.FullJid!)!;

            await WaitFor(() => bobsSession.Sent.Any(f => f.Contains("Across the border",
                                                                    StringComparison.Ordinal)),
                           "the delivered message");

            var delivered = bobsSession.Sent.First(f => f.Contains("Across the border",
                                                                  StringComparison.Ordinal));

            Assert.Multiple(() =>
            {

                Assert.That(delivered, Does.Contain("xmlns='jabber:client'"),
                            "The stanza arrived without the namespace of the client connection.");

                Assert.That(delivered, Does.Not.Contain("jabber:server"),
                            "The namespace of the server connection has been passed through.");

            });

        }

        #endregion

        #region EverythingTheServerSendsCarriesTheClientNamespace()

        /// <summary>
        /// What the server itself produces carries the namespace as well.
        /// </summary>
        /// <remarks>
        /// Over TCP it would stand once at the <c>&lt;stream:stream&gt;</c> and
        /// would hold for everything within. Over WebSocket this element does
        /// not exist: every frame has to be readable on its own, "complete with
        /// all relevant namespace and language declarations" (RFC 7395,
        /// section 3.3.3).
        ///
        /// Up to here the server sent none at all - presence, roster pushes,
        /// error IQs, all without. It never stood out, because our client
        /// recognises them by the local name. A foreign client would be
        /// allowed to be stricter, and we would learn of it only then.
        ///
        /// Nonzas stay out of it: they bring their own namespace along, and to
        /// rehang an <c>&lt;enabled/&gt;</c> onto <c>jabber:client</c> would
        /// make it unreadable.
        /// </remarks>
        [Test]
        public async Task EverythingTheServerSendsCarriesTheClientNamespace()
        {

            var alice = await ConnectAsync(_left, "alice");

            var session = _left.SessionOf(alice.FullJid!)!;

            await WaitFor(() => session.Sent.Any(f => f.StartsWith("<iq", StringComparison.Ordinal)),
                          "any stanza from the server");

            var stanzas = session.Sent.Where(f => f.StartsWith("<message",  StringComparison.Ordinal) ||
                                                  f.StartsWith("<presence", StringComparison.Ordinal) ||
                                                  f.StartsWith("<iq",       StringComparison.Ordinal))
                                      .ToList();

            var nonzas  = session.Sent.Where(f => f.StartsWith("<open",     StringComparison.Ordinal) ||
                                                  f.StartsWith("<features", StringComparison.Ordinal) ||
                                                  f.StartsWith("<success",  StringComparison.Ordinal) ||
                                                  f.StartsWith("<enabled",  StringComparison.Ordinal))
                                      .ToList();

            Assert.Multiple(() =>
            {

                Assert.That(stanzas, Is.Not.Empty);

                foreach (var s in stanzas)
                    Assert.That(s, Does.Contain("xmlns='jabber:client'"),
                                $"Gone out without a namespace: {s}");

                foreach (var n in nonzas)
                    Assert.That(n, Does.Not.Contain("jabber:client"),
                                $"Put the stanza namespace on a nonza: {n}");

            });

        }

        #endregion

    }

}
