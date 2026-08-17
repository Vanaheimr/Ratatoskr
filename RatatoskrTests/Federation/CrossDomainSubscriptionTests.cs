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
    /// The subscription handshake from RFC 6121, section 3 across a domain
    /// border.
    /// </summary>
    /// <remarks>
    /// So far that did not work: the handshake assumed that the same server has
    /// both rosters in hand. Across the border each side carries only its own
    /// half, and what the other one knows it learns exclusively from what is
    /// expressly sent.
    ///
    /// Connected over <see cref="DirectServerLinks"/>: what is checked is the
    /// handshake, not the transport. That stanzas go over real sockets,
    /// dialback and TLS stands in the transport tests.
    /// </remarks>
    [TestFixture]
    public class CrossDomainSubscriptionTests
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

        /// <param name="beforeConnecting">
        /// Is called before the connection stands - for events that would
        /// otherwise be lost between the login and the attaching.
        /// </param>
        private async Task<XMPPClient> ConnectAsync(XMPPServer            server,
                                                    String                localPart,
                                                    Action<XMPPClient>?   beforeConnecting = null)
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

            beforeConnecting?.Invoke(client);

            await client.ConnectAsync();

            return client;

        }

        private static async Task WaitFor(Func<Boolean> condition, String what)
        {
            Assert.That(await XMPPServer.WaitUntilAsync(condition),
                        Is.True, $"Timeout while waiting for: {what}");
        }

        /// <summary>
        /// The subscription state as a server carries it.
        /// </summary>
        private static String? State(XMPPServer server, String account, String contact)
            => server.GetAccount(account)?.SubscriptionOf(contact);

        #endregion


        #region TheRequestReachesTheOtherDomain()

        /// <summary>
        /// Section 3.1.3: the request goes across the border and reaches the
        /// contact.
        /// </summary>
        [Test]
        public async Task TheRequestReachesTheOtherDomain()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var requests = new List<String>();
            bob.OnSubscriptionRequest += (timestamp, sender, from, _, ct) => { requests.Add(from.ToString()); return Task.CompletedTask; };

            await alice.AddContactAsync(bob.BareJid, "Bob");

            await WaitFor(() => requests.Count > 0, "the request at Bob's");

            Assert.Multiple(() =>
            {
                Assert.That(requests[0], Is.EqualTo("alice@left.example"));

                // Section 3.1.2: at the applicant's the request is open.
                Assert.That(State(_left, "alice@left.example", "bob@right.example"),
                            Is.EqualTo("none"));
            });

        }

        #endregion

        #region TheFullHandshakeSetsBothHalves()

        /// <summary>
        /// The whole procedure: request, approval, and both servers carry the
        /// matching half afterwards.
        /// </summary>
        /// <remarks>
        /// That is the core. Each side knows only its own half, and the
        /// agreement comes about solely from both interpreting the same
        /// sequence of stanzas differently - the one sets 'from', the other
        /// 'to'.
        /// </remarks>
        [Test]
        public async Task TheFullHandshakeSetsBothHalves()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var requests = new List<String>();
            bob.OnSubscriptionRequest += (timestamp, sender, from, _, ct) => { requests.Add(from.ToString()); return Task.CompletedTask; };

            await alice.AddContactAsync(bob.BareJid, "Bob");
            await WaitFor(() => requests.Count > 0, "the request at Bob's");

            await bob.AcceptSubscriptionAsync(alice.BareJid);

            await WaitFor(() => State(_left, "alice@left.example", "bob@right.example") == "to",
                          "the 'to' half at Alice's");

            Assert.Multiple(() =>
            {
                // Alice may see Bob.
                Assert.That(State(_left,  "alice@left.example", "bob@right.example"),
                            Is.EqualTo("to"));

                // Bob permits Alice to see him.
                Assert.That(State(_right, "bob@right.example",  "alice@left.example"),
                            Is.EqualTo("from"));
            });

        }

        #endregion

        #region AfterApproval_PresenceCrossesTheBoundary()

        /// <summary>
        /// And afterwards presence flows - without anybody having filled the
        /// roster by hand.
        /// </summary>
        /// <remarks>
        /// The federation tests so far have set the roster of both sides
        /// themselves, because the handshake across the border did not work.
        /// Only here does the permission come about out of the protocol.
        /// </remarks>
        [Test]
        public async Task AfterApproval_PresenceCrossesTheBoundary()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var requests = new List<String>();
            bob.OnSubscriptionRequest += (timestamp, sender, from, _, ct) => { requests.Add(from.ToString()); return Task.CompletedTask; };

            await alice.AddContactAsync(bob.BareJid, "Bob");
            await WaitFor(() => requests.Count > 0, "the request at Bob's");

            await bob.AcceptSubscriptionAsync(alice.BareJid);
            await WaitFor(() => State(_right, "bob@right.example", "alice@left.example") == "from",
                          "the 'from' half at Bob's");

            var seen = new List<String>();
            alice.OnPresenceChanged += (timestamp, sender, from, _, ct) => { seen.Add(from.ToString()); return Task.CompletedTask; };

            await bob.SetPresenceAsync();

            await WaitFor(() => seen.Any(g => g.StartsWith("bob@right.example", StringComparison.Ordinal)),
                          "Bob's presence at Alice's");

        }

        #endregion

        #region ApprovalItself_DeliversTheContactsPresence()

        /// <summary>
        /// Section 3.1.5: with the approval the server of the contact sends
        /// their current presence - the applicant is not supposed to have to
        /// wait until the contact does something of their own accord the next
        /// time.
        /// </summary>
        /// <remarks>
        /// Here <b>no</b> further presence is sent on purpose. The previous
        /// test does that and thereby covers up whether the approval alone is
        /// already enough.
        ///
        /// Across the border more hangs on that than politeness: the stored
        /// presence is undirected and carries only a 'from'. Without an address
        /// the far end discards it, because without a 'to' it does not know who
        /// it is for - within one server that would never stand out.
        /// </remarks>
        [Test]
        public async Task ApprovalItself_DeliversTheContactsPresence()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var requests = new List<String>();
            bob.OnSubscriptionRequest += (timestamp, sender, from, _, ct) => { requests.Add(from.ToString()); return Task.CompletedTask; };

            var seen = new List<String>();
            alice.OnPresenceChanged += (timestamp, sender, from, _, ct) => { seen.Add(from.ToString()); return Task.CompletedTask; };

            await alice.AddContactAsync(bob.BareJid, "Bob");
            await WaitFor(() => requests.Count > 0, "the request at Bob's");

            await bob.AcceptSubscriptionAsync(alice.BareJid);

            await WaitFor(() => seen.Any(g => g.StartsWith("bob@right.example", StringComparison.Ordinal)),
                          "Bob's presence on the grounds of the approval alone");

        }

        #endregion

        #region ARepeatedRequest_IsAnsweredByTheServer()

        /// <summary>
        /// Section 3.1.4: if the applicant may see the contact anyway already,
        /// their server answers the request itself.
        /// </summary>
        /// <remarks>
        /// Without that the contact would be asked anew at every repeated
        /// request although they approved long ago - and an applicant whose
        /// roster got lost would never come right again without bothering the
        /// contact.
        /// </remarks>
        [Test]
        public async Task ARepeatedRequest_IsAnsweredByTheServer()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var requests = new List<String>();
            bob.OnSubscriptionRequest += (timestamp, sender, from, _, ct) => { requests.Add(from.ToString()); return Task.CompletedTask; };

            await alice.AddContactAsync(bob.BareJid, "Bob");
            await WaitFor(() => requests.Count > 0, "the first request at Bob's");

            await bob.AcceptSubscriptionAsync(alice.BareJid);
            await WaitFor(() => State(_left, "alice@left.example", "bob@right.example") == "to",
                          "the approval at Alice's");

            // Alice asks once more - Bob is not supposed to notice.
            await alice.AddContactAsync(bob.BareJid, "Bob");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(requests, Has.Count.EqualTo(1),
                            "The contact must not be asked anew.");
                Assert.That(State(_left, "alice@left.example", "bob@right.example"),
                            Is.EqualTo("to"));
            });

        }

        #endregion

        #region ARevocationCrossesTheBoundary()

        /// <summary>
        /// Section 3.2: the revocation takes the permission from the other side
        /// - across the border as well.
        /// </summary>
        [Test]
        public async Task ARevocationCrossesTheBoundary()
        {

            var alice = await ConnectAsync(_left,  "alice");
            var bob   = await ConnectAsync(_right, "bob");

            var requests = new List<String>();
            bob.OnSubscriptionRequest += (timestamp, sender, from, _, ct) => { requests.Add(from.ToString()); return Task.CompletedTask; };

            await alice.AddContactAsync(bob.BareJid, "Bob");
            await WaitFor(() => requests.Count > 0, "the request at Bob's");

            await bob.AcceptSubscriptionAsync(alice.BareJid);
            await WaitFor(() => State(_left, "alice@left.example", "bob@right.example") == "to",
                          "the approval at Alice's");

            await bob.DenySubscriptionAsync(alice.BareJid);

            await WaitFor(() => State(_left, "alice@left.example", "bob@right.example") == "none",
                          "the revocation at Alice's");

            Assert.Multiple(() =>
            {
                Assert.That(State(_left,  "alice@left.example", "bob@right.example"),
                            Is.EqualTo("none"));
                Assert.That(State(_right, "bob@right.example",  "alice@left.example"),
                            Is.EqualTo("none"));
            });

        }

        #endregion

        #region ASubscriptionForAnUnknownLocalAccount_ChangesNothing()

        /// <summary>
        /// A request to an account that does not exist here changes nothing
        /// (RFC 6121, section 8.1).
        /// </summary>
        [Test]
        public async Task ASubscriptionForAnUnknownLocalAccount_ChangesNothing()
        {

            var accepted = await _right.ReceiveFromRemoteAsync(
                              "left.example",
                                 "<presence from='alice@left.example' to='nobody@right.example' type='subscribe'/>");

            Assert.Multiple(() =>
            {
                Assert.That(accepted, Is.True, "The stanza itself is in order.");
                Assert.That(_right.GetAccount("nobody@right.example"), Is.Null);
            });

        }

        #endregion

        #region ARequestToAnOfflineAccount_IsKeptAcrossTheBoundary()

        /// <summary>
        /// Section 3.1.3, rule 4 holds regardless of where the request came
        /// from: one from beyond the border is stored as well, until the
        /// contact can see it.
        /// </summary>
        /// <remarks>
        /// Across the border the case is the normal one and not the exception.
        /// Within one server both sides are mostly there at the same time;
        /// between two servers the one does not know the login times of the
        /// other, and a request arrives precisely when it suits - not when it
        /// suits.
        /// </remarks>
        [Test]
        public async Task ARequestToAnOfflineAccount_IsKeptAcrossTheBoundary()
        {

            var alice = await ConnectAsync(_left, "alice");

            // Bob exists, but he is not connected.
            _right.AddAccount("bob");

            await alice.AddContactAsync(JID.Parse("bob@right.example"), "Bob");

            await WaitFor(() => _right.GetAccount("bob@right.example")!
                                       .PendingSubscriptionRequests
                                        .ContainsKey("alice@left.example"),
                           "the stored request on the other side");

            var requests = new List<String>();

            await ConnectAsync(_right, "bob",
                               bob => bob.OnSubscriptionRequest += (timestamp, sender, from, _, ct) => { requests.Add(from.ToString()); return Task.CompletedTask; });

            await WaitFor(() => requests.Count > 0, "the handed-over request at Bob's");

            Assert.That(requests[0], Is.EqualTo("alice@left.example"));

        }

        #endregion

    }

}
