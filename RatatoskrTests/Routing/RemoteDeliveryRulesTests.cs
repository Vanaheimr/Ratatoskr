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
    /// RFC 6121, section 8.5 holds for <b>every</b> inbound stanza — for the
    /// one crossing the server border as well.
    /// </summary>
    /// <remarks>
    /// The section speaks of an "inbound stanza" throughout and does not
    /// distinguish whether it came from a client or from another server. For
    /// the recipient the difference is none either: It is a message to their
    /// account.
    ///
    /// This server nevertheless treated the two origins differently. What came
    /// across the border went into the routing unexamined — without an offline
    /// store, without regard for negative priorities, without a distinction by
    /// kind. The gap thereby lay in precisely the most frequent case: The
    /// acquaintance on another server is the normal case and not the
    /// exception, and whoever builds an offline store builds it above all for
    /// them.
    ///
    /// What is checked runs over two real servers with
    /// <see cref="DirectServerLinks"/> in between and a real client at each of
    /// them. That matters more than it looks: An error reply has to find the
    /// way back across the border, and a recording at the server would prove
    /// only that it was sent off.
    /// </remarks>
    [TestFixture]
    public class RemoteDeliveryRulesTests
    {

        #region Data

        private XMPPServer _left   = null!;
        private XMPPServer _right  = null!;
        private readonly List<XMPPClient> _clients = [];
        private readonly InternalErrorGuard _guard = new();

        private const String Alice = "alice@left.example";
        private const String Bob   = "bob@right.example";

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

        /// <summary>
        /// A not yet connected client at one of the two servers.
        /// </summary>
        /// <remarks>
        /// Separated from the connecting, because an inbox has to be attached
        /// <b>before</b> the first presence: A handed-over message comes
        /// immediately afterwards, and a recipient logging in only later misses
        /// it depending on the timing.
        /// </remarks>
        private XMPPClient Create(XMPPServer  server,
                                  String      localPart,
                                  String?     resource  = null,
                                  Int32?      priority  = null)
        {

            if (server.GetAccount($"{localPart}@{server.Domain}") is null)
                server.AddAccount(localPart);

            var connection = new XMPPConnection($"{localPart}@{server.Domain}",
                                                "pw",
                                                server.Uri)
            {
                KeepaliveEnabled            = false,
                MaxReconnectAttempts        = 0,
                PresencePriority            = priority,
                ServerCertificateValidator  = server.IsOwnCertificate
            };

            if (resource is not null)
                connection.Resource = resource;

            var client = new XMPPClient(connection);
            _clients.Add(client);

            return client;

        }

        private async Task<XMPPClient> ConnectAsync(XMPPServer  server,
                                                    String      localPart,
                                                    String?     resource  = null,
                                                    Int32?      priority  = null)
        {

            var client = Create(server, localPart, resource, priority);
            await client.ConnectAsync();

            return client;

        }

        private static async Task WaitFor(Func<Boolean> condition, String what)
        {
            Assert.That(await XMPPServer.WaitUntilAsync(condition),
                        Is.True, $"Timeout while waiting for: {what}");
        }

        private static async Task WaitAgainst(Func<Boolean> condition, String what)
        {
            Assert.That(await XMPPServer.WaitUntilAsync(condition, TimeSpan.FromSeconds(2)),
                        Is.False, $"Should not have come about: {what}");
        }

        /// <summary>
        /// The offline store of Bob's account on the right server.
        /// </summary>
        private IReadOnlyList<OfflineMessage> BobsStore
            => _right.GetAccount(Bob)!.OfflineMessages;

        #endregion


        #region AMessageToAnAbsentUser_IsStoredOnTheirServer()

        /// <summary>
        /// The core: Bob is not logged in on his server when Alice writes from
        /// another one — and reads it at the next login.
        /// </summary>
        /// <remarks>
        /// Before, this message vanished: The routing found no session and did
        /// nothing. Alice considered it delivered, Bob never learned that it
        /// existed. Precisely the case the store is made for — and the only one
        /// in which it is of any use to a human being, because two accounts on
        /// the same server are the exception.
        /// </remarks>
        [Test]
        public async Task AMessageToAnAbsentUser_IsStoredOnTheirServer()
        {

            var alice = await ConnectAsync(_left, "alice");
            _right.AddAccount("bob");

            await alice.SendMessageAsync(JID.Parse(Bob), "See you later");

            await WaitFor(() => BobsStore.Count == 1,
                          "the stored message on Bob's server");

            var bob    = Create(_right, "bob");
            var inbox  = new ConcurrentQueue<XMPPMessage>();

            bob.OnMessage += (timestamp, sender, m, ct) => { inbox.Enqueue(m); return Task.CompletedTask; };

            await bob.ConnectAsync();

            await WaitFor(() => !inbox.IsEmpty, "the handed-over message");

            inbox.TryDequeue(out var message);

            Assert.Multiple(() =>
            {

                Assert.That(message!.Body,          Is.EqualTo("See you later"));

                Assert.That(message.FromBareJid.ToString(), Is.EqualTo(Alice),
                            "The handing over happens with the sender from the other domain.");

                Assert.That(BobsStore,              Is.Empty);

            });

        }

        #endregion

        #region AStoredRemoteMessage_ArrivesInTheClientNamespace()

        /// <summary>
        /// What came across the border carries <c>jabber:server</c>; what goes
        /// to a client has to carry <c>jabber:client</c> (RFC 6120,
        /// section 4.8.1).
        /// </summary>
        /// <remarks>
        /// The store is the place where that easily goes wrong: What is kept is
        /// the stanza as it came in, and it is delivered much later over a
        /// different stream. Were the namespace change not at the exit but at
        /// the entrance, it would come out with the wrong one — and a client
        /// that checks would discard it.
        /// </remarks>
        [Test]
        public async Task AStoredRemoteMessage_ArrivesInTheClientNamespace()
        {

            var alice = await ConnectAsync(_left, "alice");
            _right.AddAccount("bob");

            await alice.SendMessageAsync(JID.Parse(Bob), "Out of the store");

            await WaitFor(() => BobsStore.Count == 1, "the stored message");

            Assert.That(BobsStore[0].Stanza, Does.Contain("jabber:server"),
                        "What is kept is what came across the border.");

            var bob        = Create(_right, "bob");
            var rawFrames  = new ConcurrentQueue<String>();

            bob.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.Contains("Out of the store", StringComparison.Ordinal))
                    rawFrames.Enqueue(x);

                return Task.CompletedTask;

            };

            await bob.ConnectAsync();

            await WaitFor(() => !rawFrames.IsEmpty, "the handed-over message on the wire");

            rawFrames.TryDequeue(out var stanza);

            Assert.Multiple(() =>
            {
                Assert.That(stanza, Does.Contain("jabber:client"));
                Assert.That(stanza, Does.Not.Contain("jabber:server"));
            });

        }

        #endregion

        #region AGroupchatAcrossTheBorder_IsRefusedToTheSender()

        /// <summary>
        /// Section 8.5.2.1.1 holds across the border too: A <c>groupchat</c> to
        /// an account is refused — and the refusal finds the way back.
        /// </summary>
        /// <remarks>
        /// The way back is the actual subject of this test. Within one server
        /// an error reply goes into the stream of the sender; across the border
        /// there is no stream, but only the <c>from</c> of the stanza that came
        /// in and the way out again. To mistake the two would not stand out:
        /// The refusal would simply find nobody, and Alice would wait for an
        /// answer that never existed.
        ///
        /// That it arrives at the <b>client</b> and was not merely sent off at
        /// the server is the reason for the two real servers in this setup.
        /// </remarks>
        [Test]
        public async Task AGroupchatAcrossTheBorder_IsRefusedToTheSender()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var atBob    = new ConcurrentQueue<XMPPMessage>();
            var errors   = new ConcurrentQueue<(JID? From, StanzaError Error)>();

            bob.OnMessage                  += (timestamp, sender, m, ct) => { atBob.Enqueue(m); return Task.CompletedTask; };
            alice.Connection.OnStanzaError += (timestamp, sender, from, e, ct) => { errors.Enqueue((from, e)); return Task.CompletedTask; };

            await alice.SendRawAsync(
                      $"<message to='{Bob}' type='groupchat' id='across-the-border'>" +
                      "<body>Belongs into a room</body></message>");

            await WaitFor(() => !errors.IsEmpty, "the refusal back across the border");

            errors.TryDequeue(out var refused);

            Assert.Multiple(() =>
            {

                Assert.That(refused.Error.Condition, Is.EqualTo("service-unavailable"));

                Assert.That(refused.From.ToString(), Is.EqualTo(Bob),
                            "The reply comes from the address it did not go to - " +
                            "Alice's question is what has become of her message to Bob, " +
                            "and not what Bob's server thinks of its own accord.");

                Assert.That(atBob, Is.Empty,
                            "A groupchat to an account must not reach a resource.");

            });

        }

        #endregion

        #region WithoutTheStore_TheRemoteSenderIsTold()

        /// <summary>
        /// The second path permitted by section 8.5.2.2.1, across the border as
        /// well: no store, but <c>&lt;service-unavailable/&gt;</c>.
        /// </summary>
        /// <remarks>
        /// The refusal has two different places of origin in the server here -
        /// the one for <c>groupchat</c>, the other for the store that has run
        /// full or been switched off. Both need the way back, and to check only
        /// one of them would leave the other in the dark: In precisely the case
        /// for which this server had no answer at all until recently, none
        /// would come again.
        /// </remarks>
        [Test]
        public async Task WithoutTheStore_TheRemoteSenderIsTold()
        {

            _right.StoreOfflineMessages = false;

            var alice = await ConnectAsync(_left, "alice");
            _right.AddAccount("bob");

            var errors = new ConcurrentQueue<StanzaError>();
            alice.Connection.OnStanzaError += (timestamp, sender, from, e, ct) => { errors.Enqueue(e); return Task.CompletedTask; };

            await alice.SendMessageAsync(JID.Parse(Bob), "Does not arrive");

            await WaitFor(() => !errors.IsEmpty, "the refusal back across the border");

            errors.TryDequeue(out var refused);

            Assert.Multiple(() =>
            {
                Assert.That(refused!.Condition, Is.EqualTo("service-unavailable"));
                Assert.That(BobsStore,          Is.Empty);
            });

        }

        #endregion

        #region ARefusalAcrossTheBorder_IsNotAnsweredWithARefusal()

        /// <summary>
        /// RFC 6120, section 8.3.1: An error stanza is never followed by an
        /// error.
        /// </summary>
        /// <remarks>
        /// Between two servers that is no formality but the difference between
        /// a lost message and two servers pushing notices at each other until
        /// one of them gives up. The danger is new here: As long as only
        /// clients took this path, every reply ended in a stream. Now it goes
        /// out again, and the recipient is a machine that could answer in turn.
        ///
        /// Both are checked in one: the error to the account (which has to be
        /// passed over silently) and the one to a resource that does not exist.
        /// </remarks>
        [Test]
        public async Task ARefusalAcrossTheBorder_IsNotAnsweredWithARefusal()
        {

            var alice = await ConnectAsync(_left, "alice");
            _right.AddAccount("bob");

            var atAlice = new ConcurrentQueue<StanzaError>();
            alice.Connection.OnStanzaError += (timestamp, sender, from, e, ct) => { atAlice.Enqueue(e); return Task.CompletedTask; };

            const String errorBody = "<error type='cancel'>" +
                                     "<service-unavailable xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                                     "</error>";

            await alice.SendRawAsync($"<message to='{Bob}' type='error' id='to-the-account'>{errorBody}</message>");
            await alice.SendRawAsync($"<message to='{Bob}/nowhere' type='error' id='to-the-resource'>{errorBody}</message>");

            await WaitAgainst(() => !atAlice.IsEmpty,
                              "an error as the answer to an error");

            Assert.That(BobsStore, Is.Empty,
                        "An error stanza does not belong into the store either.");

        }

        #endregion

        #region ANegativePriorityAcrossTheBorder_GetsNothingFromTheAccount()

        /// <summary>
        /// The negative priority takes effect against messages from another
        /// server as well — and the message is thereby not lost but lies
        /// stored.
        /// </summary>
        /// <remarks>
        /// Before, the priority was without effect for this path: The routing
        /// delivered to every session it found. A second device expressly
        /// keeping out of the traffic to the account got the message
        /// nevertheless — and because it thereby counted as delivered, the
        /// human being saw it on the device they were not sitting at.
        ///
        /// The second half belongs to it: The device stays addressable
        /// directly. Without it a negative priority would be a logging out, and
        /// that is precisely what it is not.
        /// </remarks>
        [Test]
        public async Task ANegativePriorityAcrossTheBorder_GetsNothingFromTheAccount()
        {

            var alice         = await ConnectAsync(_left, "alice");
            var secondDevice  = Create(_right, "bob", "SecondDevice", priority: -1);
            var inbox         = new ConcurrentQueue<XMPPMessage>();

            secondDevice.OnMessage += (timestamp, sender, m, ct) => { inbox.Enqueue(m); return Task.CompletedTask; };

            await secondDevice.ConnectAsync();

            await WaitFor(() => _right.SessionOf(secondDevice.FullJid!.ToString())?.PresencePriority == -1,
                          "the negative priority on Bob's server");

            await alice.SendRawAsync(
                      $"<message to='{Bob}' type='chat' id='to-the-account'>" +
                      "<body>To the account</body></message>");

            await WaitFor(() => BobsStore.Count == 1,
                          "the storing instead of the delivery");

            // Addressed to the same resource - that must arrive.
            await alice.SendRawAsync(
                      $"<message to='{secondDevice.FullJid}' type='chat' id='to-the-resource'>" +
                      "<body>To the resource</body></message>");

            await WaitFor(() => inbox.Any(m => m.MessageId == "to-the-resource"),
                          "the directed message");

            Assert.That(inbox.Any(m => m.MessageId == "to-the-account"), Is.False,
                        "What went to the account must not reach a negative priority.");

        }

        #endregion

        #region AChatToAVanishedResource_IsHandledLikeTheAccount()

        /// <summary>
        /// Section 8.5.3.2.1 across the border: A <c>chat</c> to a resource
        /// that does not exist is handled as if it had gone to the account.
        /// </summary>
        /// <remarks>
        /// Across the border this case is more frequent than at home, and that
        /// for a simple reason: The full JID of the counterpart the client has
        /// from a message that came through the net, and between it and their
        /// answer lies more time. If the other one switched the device in the
        /// meantime, the resource is gone — and the sender never meant it, but
        /// their counterpart.
        /// </remarks>
        [Test]
        public async Task AChatToAVanishedResource_IsHandledLikeTheAccount()
        {

            var alice = await ConnectAsync(_left, "alice");
            _right.AddAccount("bob");

            await alice.SendRawAsync(
                      $"<message to='{Bob}/gone-already' type='chat' id='stored'>" +
                      "<body>Are you gone?</body></message>");

            await WaitFor(() => BobsStore.Count == 1,
                          "the stored message to the vanished resource");

            var bob    = Create(_right, "bob", "New");
            var inbox  = new ConcurrentQueue<XMPPMessage>();

            bob.OnMessage += (timestamp, sender, m, ct) => { inbox.Enqueue(m); return Task.CompletedTask; };

            await bob.ConnectAsync();

            await WaitFor(() => inbox.Any(m => m.MessageId == "stored"),
                          "the handed-over message");

            // And now Bob is there, only under a different name.
            await alice.SendRawAsync(
                      $"<message to='{Bob}/gone-already' type='chat' id='delivered'>" +
                      "<body>Not after all</body></message>");

            await WaitFor(() => inbox.Any(m => m.MessageId == "delivered"),
                          "the delivery to the reachable resource");

            Assert.That(BobsStore, Is.Empty,
                        "As long as a resource is reachable, nothing is stored.");

        }

        #endregion

        #region AHeadlineToAnAbsentUser_IsNotStored()

        /// <summary>
        /// The counter-check: a <c>headline</c> is not stored, not from another
        /// server either.
        /// </summary>
        /// <remarks>
        /// Without it the collection would pass even if simply everything that
        /// was not deliverable were stored across the border — and getting a
        /// notice from yesterday handed over at the login is worse than missing
        /// it: It looks like today's one.
        /// </remarks>
        [Test]
        public async Task AHeadlineToAnAbsentUser_IsNotStored()
        {

            var alice = await ConnectAsync(_left, "alice");
            _right.AddAccount("bob");

            await alice.SendRawAsync(
                      $"<message to='{Bob}' type='headline' id='notice'>" +
                      "<body>Price has fallen</body></message>");

            // Afterwards one that has to be stored - it establishes that the
            // store ran while the notice fell through.
            await alice.SendMessageAsync(JID.Parse(Bob), "And this one stays lying");

            await WaitFor(() => BobsStore.Count == 1, "the stored message");

            Assert.That(BobsStore[0].Stanza, Does.Contain("And this one stays lying"));

        }

        #endregion

        #region AnIqAcrossTheBorder_ToAnAccount_IsAnsweredOnce()

        /// <summary>
        /// Section 8.5.2.1.3 holds across the border too: A request to an
        /// account is answered by the server of the recipient and distributed
        /// to no resource.
        /// </summary>
        /// <remarks>
        /// Across the border the damage is greater than at home. A foreign
        /// server distributing a request to all resources sends the asker
        /// several replies to one <c>id</c> — and they have no way of laying it
        /// at the far end's door: To them it looks as if their own client had
        /// lost count.
        /// </remarks>
        [Test]
        public async Task AnIqAcrossTheBorder_ToAnAccount_IsAnsweredOnce()
        {

            var alice = await ConnectAsync(_left, "alice");

            var mobile   = Create(_right, "bob", "Mobile");
            var desktop  = Create(_right, "bob", "Desktop");

            var atTheMobile   = new ConcurrentQueue<String>();
            var atTheDesktop  = new ConcurrentQueue<String>();

            mobile.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("urn:xmpp:ping", StringComparison.Ordinal))
                {
                    atTheMobile.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            desktop.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("urn:xmpp:ping", StringComparison.Ordinal))
                {
                    atTheDesktop.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            await mobile.ConnectAsync();
            await desktop.ConnectAsync();

            var replies = new ConcurrentQueue<String>();
            var errors  = new ConcurrentQueue<StanzaError>();

            alice.Connection.OnStanzaError += (timestamp, sender, from, e, ct) => { errors.Enqueue(e); return Task.CompletedTask; };
            alice.Connection.OnRawXml      += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("<iq",              StringComparison.Ordinal) &&
                    x.Contains("id='across-the-border'", StringComparison.Ordinal))
                {
                    replies.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            await alice.SendRawAsync(
                      $"<iq type='get' id='across-the-border' to='{Bob}'>" +
                      "<ping xmlns='urn:xmpp:ping'/></iq>");

            await WaitFor(() => !errors.IsEmpty, "the reply of Bob's server");

            // Give the resources time to get the request after all.
            await Task.Delay(TimeSpan.FromSeconds(1));

            errors.TryDequeue(out var refused);

            Assert.Multiple(() =>
            {

                Assert.That(refused!.Condition, Is.EqualTo("service-unavailable"));

                Assert.That(replies, Has.Count.EqualTo(1),
                            "Exactly one reply to one id.");

                Assert.That(atTheMobile,  Is.Empty, "The request must reach no resource.");
                Assert.That(atTheDesktop, Is.Empty, "The second one neither.");

            });

        }

        #endregion

        #region AnIqAcrossTheBorder_ToAResource_IsDelivered()

        /// <summary>
        /// The counter-check across the border: addressed to a matching
        /// resource the request arrives, and the reply finds its way back.
        /// </summary>
        /// <remarks>
        /// That is the whole point of IQ between two servers — a version query,
        /// a ping, a file transfer go to a full JID. Without this
        /// counter-check the collection would pass even if every request across
        /// the border were turned away.
        ///
        /// The two are contacts, because section 8.5.3.1 lets the request
        /// through only if the asker may see the presence of the recipient.
        /// What is tended for that is the roster on Bob's server: There the
        /// decision falls, and there stands the half that counts.
        /// </remarks>
        [Test]
        public async Task AnIqAcrossTheBorder_ToAResource_IsDelivered()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            _right.GetAccount(Bob)!.SetRosterEntry(new RosterEntry(Alice, null, "both"));

            var replies = new ConcurrentQueue<String>();

            alice.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("type='result'",         StringComparison.Ordinal) &&
                    x.Contains("id='to-the-resource'",  StringComparison.Ordinal))
                {
                    replies.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            await alice.SendRawAsync(
                      $"<iq type='get' id='to-the-resource' to='{bob.FullJid}'>" +
                      "<ping xmlns='urn:xmpp:ping'/></iq>");

            await WaitFor(() => !replies.IsEmpty, "Bob's reply back across the border");

            Assert.Pass();

        }

        #endregion

        #region AnIqToTheServersOwnAddress_IsNotClaimedByTheUserPath()

        /// <summary>
        /// The delivery path for users does not touch a request to the server
        /// address.
        /// </summary>
        /// <remarks>
        /// Section 8.5.2 deals with an address "of the form
        /// <c>&lt;localpart@domainpart&gt;</c>". A request to the domain itself
        /// addresses the server and not a user.
        ///
        /// Until D36 it therefore stayed <b>unanswered</b> — a gap that was
        /// expressly noted here: The server answers ping and disco#info at its
        /// own address, but the answers stood in the way for local clients and
        /// wanted a session.
        ///
        /// What this test has protected against all along is the obvious
        /// mistake: to take the <b>user</b> delivery path for the server
        /// address as well. That one answers everything with
        /// <c>&lt;service-unavailable/&gt;</c> — a ping therefore too, and that
        /// would be wrong. A <c>result</c> it cannot produce at all; that is
        /// precisely how the mistake can be recognised.
        /// </remarks>
        [Test]
        public async Task APingToTheServersOwnAddress_IsAnsweredByTheServerItself()
        {

            var alice = await ConnectAsync(_left, "alice");

            var replies = new ConcurrentQueue<String>();

            alice.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("to-the-server", StringComparison.Ordinal))
                {
                    replies.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            await alice.SendRawAsync(
                      $"<iq type='get' id='to-the-server' to='{_right.Domain}'>" +
                      "<ping xmlns='urn:xmpp:ping'/></iq>");

            await WaitFor(() => !replies.IsEmpty, "the reply of the foreign server");

            replies.TryDequeue(out var reply);

            Assert.Multiple(() =>
            {

                Assert.That(reply, Does.Contain("type='result'"),
                            "A ping is answered - and the user delivery path " +
                            "could not produce a result at all.");

                Assert.That(reply, Does.Contain($"from='{_right.Domain}'"),
                            "The one that answered is the server that was asked.");

            });

        }

        #endregion

        #region ADiscoInfoToTheServersOwnAddress_IsAnswered()

        /// <summary>
        /// The same for disco#info: The far end learns what this server can do.
        /// </summary>
        /// <remarks>
        /// The practical case behind the rule. A foreign server asks before the
        /// first conversation what the counterpart is capable of; if the
        /// question stays unanswered, it takes them for a server without
        /// features — and does not even notice that it learned nothing.
        /// </remarks>
        [Test]
        public async Task ADiscoInfoToTheServersOwnAddress_IsAnswered()
        {

            var alice = await ConnectAsync(_left, "alice");

            var replies = new ConcurrentQueue<String>();

            alice.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("what-can-you-do", StringComparison.Ordinal))
                {
                    replies.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            await alice.SendRawAsync(
                      $"<iq type='get' id='what-can-you-do' to='{_right.Domain}'>" +
                      "<query xmlns='http://jabber.org/protocol/disco#info'/></iq>");

            await WaitFor(() => !replies.IsEmpty, "the information of the foreign server");

            replies.TryDequeue(out var reply);

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='result'"));
                Assert.That(reply, Does.Contain("<identity"));
                Assert.That(reply, Does.Contain("urn:xmpp:ping"));
            });

        }

        #endregion

        #region AnUnknownRequestToTheServersOwnAddress_IsRefusedNotIgnored()

        /// <summary>
        /// And what the server does not know gets an error instead of silence.
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 8.2.3, rule 3 knows no third possibility: A
        /// <c>get</c> or <c>set</c> is followed by <c>result</c> or
        /// <c>error</c>. Silence lets the far end wait into its timeout — and
        /// it never learns whether the question arrived or merely was not
        /// understood.
        /// </remarks>
        [Test]
        public async Task AnUnknownRequestToTheServersOwnAddress_IsRefusedNotIgnored()
        {

            var alice = await ConnectAsync(_left, "alice");

            var errors = new ConcurrentQueue<StanzaError>();
            alice.Connection.OnStanzaError += (timestamp, sender, from, e, ct) => { errors.Enqueue(e); return Task.CompletedTask; };

            await alice.SendRawAsync(
                      $"<iq type='get' id='does-not-know-it' to='{_right.Domain}'>" +
                      "<query xmlns='urn:example:does-not-exist'/></iq>");

            await WaitFor(() => !errors.IsEmpty, "the refusal of the foreign server");

            errors.TryDequeue(out var refused);

            Assert.That(refused!.Condition, Is.EqualTo("service-unavailable"));

        }

        #endregion

        #region AResultToTheServersOwnAddress_IsNotAnswered()

        /// <summary>
        /// A <b>reply</b> to the server address is followed by nothing.
        /// </summary>
        /// <remarks>
        /// Rule 4 holds here as well, and it is the counter-check to rule 3
        /// above: Whoever answers every stanza to the server address sends an
        /// error back on a <c>result</c> — to somebody who has not asked
        /// anything, under the <c>id</c> of a question they answered
        /// themselves. Two servers keeping it that way push notices at each
        /// other.
        /// </remarks>
        [Test]
        public async Task AResultToTheServersOwnAddress_IsNotAnswered()
        {

            var alice = await ConnectAsync(_left, "alice");

            var replies = new ConcurrentQueue<String>();

            alice.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("no-question", StringComparison.Ordinal))
                {
                    replies.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            await alice.SendRawAsync(
                      $"<iq type='result' id='no-question' to='{_right.Domain}'/>");

            await WaitAgainst(() => !replies.IsEmpty, "a reply to a reply");

        }

        #endregion

        #region AProbeFromAnotherServer_IsAnsweredNotDelivered()

        /// <summary>
        /// RFC 6121, section 4.3: A presence probe the server answers itself —
        /// it reaches no client.
        /// </summary>
        /// <remarks>
        /// Up to here it went into the routing and landed at Bob's client. That
        /// was wrong in both directions: The client got to see a stanza that is
        /// not meant for it and that it cannot answer anything to, and Alice's
        /// server never got an answer — it asks after Bob's state and receives
        /// silence, although Bob's server has the information.
        ///
        /// The same asymmetry as with the message and the IQ, and the last of
        /// its kind: For a local client the probe has been answered all along.
        ///
        /// Both halves stand in the test. "Arrives" alone would be fulfilled if
        /// the probe were passed on in addition; "does not reach the client"
        /// alone would be fulfilled if it vanished without a trace.
        /// </remarks>
        [Test]
        public async Task AProbeFromAnotherServer_IsAnsweredNotDelivered()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            // First wait until Bob's *first* presence is processed, and only
            // then set the roster. The other way round it is a race: If the
            // first presence already meets the entry, it goes to Alice over the
            // ordinary distribution - and the test would pass without a probe
            // ever having been answered. That is exactly what it did at first.
            await WaitFor(() => _right.SessionOf(bob.FullJid!.ToString())?.IsAvailable == true,
                          "Bob's first presence on his server");

            // Bob lets Alice see his state - without that every probe stays
            // unanswered, and the test would check the silence only.
            _right.GetAccount(Bob)!.SetRosterEntry(new RosterEntry(Alice, null, "from"));

            var atAlice = new ConcurrentQueue<(JID From, String? Type)>();
            var atBob   = new ConcurrentQueue<(JID From, String? Type)>();

            alice.Connection.OnPresence += (timestamp, sender, from, type, ct) => { atAlice.Enqueue((from, type)); return Task.CompletedTask; };
            bob.Connection.OnPresence   += (timestamp, sender, from, type, ct) => { atBob.Enqueue((from, type)); return Task.CompletedTask; };

            await alice.SendRawAsync($"<presence to='{Bob}' type='probe'/>");

            await WaitFor(() => atAlice.Any(p => p.From.ToString().StartsWith(Bob, StringComparison.Ordinal)),
                          "Bob's state as the answer to the probe");

            // Give the probe time to turn up at Bob's after all.
            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.That(atBob.Any(p => p.Type == "probe"), Is.False,
                        "A probe must reach no client.");

        }

        #endregion

        #region AProbeWithoutPermission_IsNotAnswered()

        /// <summary>
        /// Without permission the probe stays unanswered — and does not give
        /// away that the account exists either.
        /// </summary>
        /// <remarks>
        /// What is asked is the roster of the <b>one being asked about</b> for
        /// <c>from</c> or <c>both</c>: "that one may see me". The same half as
        /// with the IQ check from section 8.5.3.1 — and the same danger of
        /// mistaking it, which is why a one-sided roster stands here: Alice may
        /// <i>not</i> see Bob's state, but Bob may see Alice's.
        ///
        /// Section 8.5.1 leaves <c>&lt;unsubscribed/&gt;</c> and silence open
        /// for an unknown account; this server keeps silent, and thereby an
        /// unknown account looks exactly like an existing one without
        /// permission. That is the point of the choice.
        /// </remarks>
        [Test]
        public async Task AProbeWithoutPermission_IsNotAnswered()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            await WaitFor(() => _right.SessionOf(bob.FullJid!.ToString())?.IsAvailable == true,
                          "Bob's first presence on his server");

            // The wrong half: Bob sees Alice, Alice does not see Bob.
            _right.GetAccount(Bob)!.SetRosterEntry(new RosterEntry(Alice, null, "to"));

            var atAlice = new ConcurrentQueue<(JID From, String? Type)>();
            alice.Connection.OnPresence += (timestamp, sender, from, type, ct) => { atAlice.Enqueue((from, type)); return Task.CompletedTask; };

            await alice.SendRawAsync($"<presence to='{Bob}' type='probe'/>");

            await WaitAgainst(() => atAlice.Any(p => p.From.ToString().StartsWith(Bob, StringComparison.Ordinal)),
                              "an answer to an unauthorised probe");

            // And for an account that does not exist, the same picture.
            await alice.SendRawAsync($"<presence to='doesnotexist@{_right.Domain}' type='probe'/>");

            await WaitAgainst(() => atAlice.Count > 0,
                              "an answer to the probe to an unknown account");

        }

        #endregion

        #region PresenceAcrossTheBorder_StillTakesTheDirectPath()

        /// <summary>
        /// Only messages take the new path. Presence still goes to the
        /// resources directly.
        /// </summary>
        /// <remarks>
        /// The switch asks after the element and not after the origin. Were it
        /// to ask wrongly, presence would run through the delivery rules for
        /// messages — and those know no <c>&lt;presence/&gt;</c>: It has no
        /// <c>type</c> they could interpret, would thereby count as
        /// <c>normal</c> and land in the store. At the next login it would come
        /// out as a presence from the day before yesterday.
        ///
        /// The first half checks with an <b>absent</b> Bob, and that is the
        /// point: As long as he is connected, his presence arrives on both
        /// paths — the message route delivers it to a reachable resource just
        /// the same. The wrong path becomes visible only where the delivery
        /// rules do something other than the routing, and that is the store.
        /// </remarks>
        [Test]
        public async Task PresenceAcrossTheBorder_StillTakesTheDirectPath()
        {

            var alice = Create(_left, "alice");
            _right.AddAccount("bob");

            _left.GetAccount(Alice)!.SetRosterEntry(new RosterEntry(Bob,   null, "both"));
            _right.GetAccount(Bob)!.SetRosterEntry(new RosterEntry(Alice, null, "both"));

            // Bob is not there. Alice's presence goes across the border
            // nevertheless - his server only has nothing to give it to.
            await alice.ConnectAsync();
            await alice.SetPresenceAsync("away", "Lunch break");

            await WaitAgainst(() => BobsStore.Count > 0,
                              "presence in the offline store");

            // And the counter-check: It arrives when somebody is there.
            var bob        = Create(_right, "bob");
            var presences  = new ConcurrentQueue<String>();

            bob.Connection.OnPresence += (timestamp, sender, from, show, ct) => { presences.Enqueue(from.ToString()); return Task.CompletedTask; };

            await bob.ConnectAsync();
            await alice.SetPresenceAsync("dnd", "Please do not disturb");

            await WaitFor(() => presences.Any(f => f.StartsWith(Alice, StringComparison.Ordinal)),
                          "the presence across the border");

        }

        #endregion

        #region AnIqWithAnUnknownType_DoesNotCrossTheBorder()

        /// <summary>
        /// RFC 6120, section 8.2.3, rule 2: An IQ stanza with an unknown
        /// <c>type</c> is turned away by one's own server already — "the
        /// recipient <b>or an intermediate router</b>".
        /// </summary>
        /// <remarks>
        /// The sender of the error is the whole statement here. Were it to come
        /// from <c>right.example</c>, the stanza would have gone across the
        /// border and been turned away only over there — the test would pass,
        /// and the rule for the router would still not be implemented. Only
        /// <c>left.example</c> proves that it did not leave its own server.
        ///
        /// Why a router should pass judgement at all instead of handing on: A
        /// stanza that is neither question nor answer nobody can answer at the
        /// destination. If everyone hands it on, it wanders through the net,
        /// and the sender never learns what became of it.
        /// </remarks>
        [Test]
        public async Task AnIqWithAnUnknownType_DoesNotCrossTheBorder()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            _right.GetAccount(Bob)!.SetRosterEntry(new RosterEntry(Alice, null, "from"));

            var atAlice = new ConcurrentQueue<String>();
            var atBob   = new ConcurrentQueue<String>();

            alice.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("id='foreign-type'", StringComparison.Ordinal))
                {
                    atAlice.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            bob.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("id='foreign-type'", StringComparison.Ordinal))
                {
                    atBob.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            await alice.SendRawAsync(
                      $"<iq type='maybe' id='foreign-type' to='{bob.FullJid}'>" +
                      "<ping xmlns='urn:xmpp:ping'/></iq>");

            await WaitFor(() => !atAlice.IsEmpty, "the refusal from Alice's own server");

            atAlice.TryDequeue(out var refused);

            Assert.Multiple(() =>
            {

                Assert.That(refused, Does.Contain("<bad-request "));

                Assert.That(refused, Does.Contain($"from='{_left.Domain}'"),
                            "The one that turned it away is one's own server, not the far end's.");

                Assert.That(atBob, Is.Empty,
                            "And it arrived nowhere.");

            });

        }

        #endregion

        #region AnIqWithAnUnknownTypeFromAnotherServer_IsRefused()

        /// <summary>
        /// The same rule for the other role: as the recipient of a stanza from
        /// the far end.
        /// </summary>
        /// <remarks>
        /// What is fed in here is fed in by hand, and that is no artifice but
        /// the only possibility: A client of this collection would never get
        /// this far, because its own server turns it away already (see the test
        /// above). The case is real nevertheless — a foreign server
        /// implementation not knowing rule 2 hands exactly that across the
        /// border.
        ///
        /// Bob is logged in and lets Alice see his state. Without both the test
        /// would be worthless: The stanza would then not arrive at his end even
        /// without any check, and the proof "reaches no client" would prove
        /// only that nobody was there.
        /// </remarks>
        [Test]
        public async Task AnIqWithAnUnknownTypeFromAnotherServer_IsRefused()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            _right.GetAccount(Bob)!.SetRosterEntry(new RosterEntry(Alice, null, "from"));

            var atAlice = new ConcurrentQueue<String>();
            var atBob   = new ConcurrentQueue<String>();

            alice.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("id='from-over-there'", StringComparison.Ordinal))
                {
                    atAlice.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            bob.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("id='from-over-there'", StringComparison.Ordinal))
                {
                    atBob.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            await _right.AcceptFromRemoteAsync(
                      _left.Domain,
                      $"<iq type='maybe' id='from-over-there' " +
                      $"from='{alice.FullJid}' to='{bob.FullJid}'>" +
                      "<ping xmlns='urn:xmpp:ping'/></iq>");

            await WaitFor(() => !atAlice.IsEmpty, "the refusal back across the border");

            atAlice.TryDequeue(out var refused);

            Assert.Multiple(() =>
            {

                Assert.That(refused, Does.Contain("<bad-request "));

                Assert.That(refused, Does.Contain($"from='{_right.Domain}'"),
                            "The one that turned it away is the server of the recipient.");

                Assert.That(atBob, Is.Empty,
                            "Bob does not get to see it.");

            });

        }

        #endregion

        #region AMalformedRecipientFromAnotherServer_IsRefusedAndAnswered()

        /// <summary>
        /// A <c>to</c> that is no JID is not accepted from a far end either —
        /// and the sender learns of it.
        /// </summary>
        /// <remarks>
        /// D51 introduced the check for stanzas from clients; for the path
        /// across the border it was missing. There it meets the more likely
        /// case: One's own client is written by the same library, the foreign
        /// implementation is not.
        ///
        /// <c>IsLocal</c> alone is not enough, because it looks at the domain
        /// only. <c>b ob@right.example</c> belongs here and is no address
        /// nevertheless — the stanza ran all the way into the delivery and
        /// looked there like one to an absent recipient.
        ///
        /// The second address checks the <b>order</b>: With
        /// <c>bob@-right.example</c> the domain is not one already, and
        /// <c>IsLocal</c> would therefore take it for that of a third party.
        /// Were the check to stand behind it, the reason would read "foreign
        /// recipient" — rightly turned away, wrongly reasoned, and the sender
        /// would look for the error in the wrong place.
        /// </remarks>
        [TestCase("b ob@",  TestName = "Space in the localpart")]
        [TestCase("bob@-",  TestName = "Hyphen at the start of the domain")]
        public async Task AMalformedRecipientFromAnotherServer_IsRefusedAndAnswered(String beginning)
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            _right.GetAccount(Bob)!.SetRosterEntry(new RosterEntry(Alice, null, "from"));

            var atAlice = new ConcurrentQueue<String>();
            var atBob   = new ConcurrentQueue<String>();

            alice.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("id='from-over-there'", StringComparison.Ordinal))
                {
                    atAlice.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            bob.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("id='from-over-there'", StringComparison.Ordinal))
                {
                    atBob.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            var verdict = await _right.AcceptFromRemoteAsync(
                             _left.Domain,
                             $"<message type='chat' id='from-over-there' " +
                             $"from='{alice.FullJid}' to='{beginning}{_right.Domain}'>" +
                             "<body>To an address that is none</body></message>");

            await WaitFor(() => !atAlice.IsEmpty, "the refusal back across the border");

            atAlice.TryDequeue(out var refused);

            Assert.Multiple(() =>
            {

                Assert.That(verdict, Is.EqualTo(RemoteStanzaResult.MalformedRecipient),
                            "The reason belongs named, otherwise it stands there as 'foreign recipient'.");

                Assert.That(refused, Does.Contain("<jid-malformed "));

                Assert.That(refused, Does.Contain($"from='{_right.Domain}'"),
                            "The one that turned it away is the server of the recipient, not the recipient.");

                Assert.That(atBob, Is.Empty,
                            "Bob does not get to see it.");

            });

        }

        #endregion

        #region AMalformedSenderFromAnotherServer_IsRefusedWithoutAnAnswer()

        /// <summary>
        /// A <c>from</c> that is no JID is turned away — and not answered.
        /// </summary>
        /// <remarks>
        /// There would be nobody for an answer to go to either: The address of
        /// the sender is none. That the check stands <b>before</b> the question
        /// of responsibility has the same reason — applying <c>DomainOf</c> to
        /// a string that is no JID compares fragments and then calls the result
        /// a "foreign domain".
        ///
        /// For the stream this is the same case as a <c>from</c> the far end
        /// may not speak for, according to RFC 6120, section 8.1.1.1:
        /// <c>&lt;invalid-from/&gt;</c>, and the stream ends. That is what
        /// <c>S2SStreamTests</c> checks.
        /// </remarks>
        [Test]
        public async Task AMalformedSenderFromAnotherServer_IsRefusedWithoutAnAnswer()
        {

            await ConnectAsync(_left,  "alice");
            var bob = await ConnectAsync(_right, "bob");

            _right.GetAccount(Bob)!.SetRosterEntry(new RosterEntry(Alice, null, "from"));

            var atBob = new ConcurrentQueue<String>();

            bob.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("id='from-over-there'", StringComparison.Ordinal))
                {
                    atBob.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            var verdict = await _right.AcceptFromRemoteAsync(
                             _left.Domain,
                             $"<message type='chat' id='from-over-there' " +
                             $"from='al ice@{_left.Domain}' to='{bob.FullJid}'>" +
                             "<body>From an address that is none</body></message>");

            Assert.That(verdict, Is.EqualTo(RemoteStanzaResult.MalformedSender));

            await WaitAgainst(() => !atBob.IsEmpty, "the delivery to Bob");

        }

        #endregion

        #region AMalformedRecipientInAnErrorStanza_IsNotAnswered()

        /// <summary>
        /// An error stanza is not followed by an error, not across the border
        /// either (RFC 6120, section 8.3.1).
        /// </summary>
        /// <remarks>
        /// Across the border the rule weighs heavier than in one's own house:
        /// Two servers answering each other push the notice back and forth
        /// until one of them gives up — and neither of the two notices that it
        /// is stuck in a loop.
        /// </remarks>
        [Test]
        public async Task AMalformedRecipientInAnErrorStanza_IsNotAnswered()
        {

            var alice = await ConnectAsync(_left,  "alice");
            await ConnectAsync(_right, "bob");

            var atAlice = new ConcurrentQueue<String>();

            alice.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("id='from-over-there'", StringComparison.Ordinal))
                {
                    atAlice.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            var verdict = await _right.AcceptFromRemoteAsync(
                             _left.Domain,
                             $"<message type='error' id='from-over-there' " +
                             $"from='{alice.FullJid}' to='b ob@{_right.Domain}'>" +
                             "<error type='cancel'><gone xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/></error>" +
                             "</message>");

            Assert.That(verdict, Is.EqualTo(RemoteStanzaResult.MalformedRecipient));

            await WaitAgainst(() => !atAlice.IsEmpty, "an answer to an error stanza");

        }

        #endregion

    }

}
