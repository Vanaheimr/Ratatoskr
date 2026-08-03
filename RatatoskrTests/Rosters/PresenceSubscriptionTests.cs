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
    /// RFC 6121, section 4: presence is no broadcast.
    ///
    /// Who gets it is decided by the subscription state in the roster of the
    /// sender: only contacts with <c>from</c> or <c>both</c>, plus one's own
    /// further resources. The test server has distributed it to everyone up to
    /// here - every session thereby learned who else is online.
    /// </summary>
    [TestFixture]
    public class PresenceSubscriptionTests : AXMPPTests
    {

        #region Helper functions

        /// <summary>
        /// Connects a client and collects every presence announcement from now
        /// on as <c>jid|type</c>.
        /// </summary>
        private async Task<(XMPPClient Client, ConcurrentQueue<String> Presences)> WatcherAsync(String localPart)
        {

            var client     = await ConnectClientAsync(localPart);
            var presences  = new ConcurrentQueue<String>();

            client.OnPresenceChanged += (from, type) => presences.Enqueue($"{from}|{type}");

            return (client, presences);

        }

        private static Boolean Saw(ConcurrentQueue<String> presences, XMPPClient who)
            => presences.Any(p => p.StartsWith(who.BareJid, StringComparison.OrdinalIgnoreCase));

        #endregion


        #region Presence_ReachesAContactWithSubscriptionFrom()

        /// <summary>
        /// The ground of it: whoever has <c>from</c> or <c>both</c> gets it.
        /// </summary>
        [Test]
        public async Task Presence_ReachesAContactWithSubscriptionFrom()
        {

            MakeContacts("alice", "bob");

            var alice           = await ConnectClientAsync("alice");
            var (bob, atBobs)   = await WatcherAsync("bob");

            await alice.SetPresenceAsync("away", "Back in a moment");

            await WaitFor(() => Saw(atBobs, alice), "Alice's presence at Bob");

        }

        #endregion

        #region Presence_DoesNotReachANonContact()

        /// <summary>
        /// The heart of it: Carol stands in no roster and must not learn that
        /// Alice is online. She used to get it, because the distribution went
        /// to all sessions - one session on the server sufficed to read along
        /// with everyone else's presence.
        /// </summary>
        [Test]
        public async Task Presence_DoesNotReachANonContact()
        {

            MakeContacts("alice", "bob");

            var alice             = await ConnectClientAsync("alice");
            var (_, atCarols)     = await WatcherAsync("carol");

            await alice.SetPresenceAsync("away");

            await WaitAgainst(() => Saw(atCarols, alice), "Alice's presence at Carol");

        }

        #endregion

        #region Presence_DoesNotReachAContactWithSubscriptionToOnly()

        /// <summary>
        /// Subscriptions are directed. If Bob stands in Alice's roster with
        /// <c>to</c> only, then <b>Alice</b> sees Bob's presence - not the
        /// other way round.
        /// </summary>
        [Test]
        public async Task Presence_DoesNotReachAContactWithSubscriptionToOnly()
        {

            SetServerRoster("alice", "bob", "to");
            SetServerRoster("bob", "alice", "from");

            var alice          = await ConnectClientAsync("alice");
            var (_, atBobs)    = await WatcherAsync("bob");

            await alice.SetPresenceAsync("away");

            await WaitAgainst(() => Saw(atBobs, alice), "Alice's presence at Bob");

        }

        #endregion

        #region Presence_ReachesTheOwnOtherResources()

        /// <summary>
        /// RFC 6121, section 4.4.2: the further resources of one's own account
        /// always get it, with no roster entry at all.
        ///
        /// That held before as well - the test stands as regression cover for
        /// the filtering, not as proof of a fault that was fixed.
        /// </summary>
        [Test]
        public async Task Presence_ReachesTheOwnOtherResources()
        {

            var first           = await ConnectClientAsync("alice");
            var (_, atSecond)  = await WatcherAsync("alice");

            await first.SetPresenceAsync("dnd");

            await WaitFor(() => Saw(atSecond, first), "the first resource's presence at the second");

        }

        #endregion

        #region NewlyOnlineClient_LearnsAboutContactsAlreadyOnline()

        /// <summary>
        /// RFC 6121, section 4.3.1: at the login the server asks after the
        /// state of the client's contacts for it. Without that a client learns
        /// only of contacts that log in <b>after</b> it - whoever was online
        /// already stayed invisible to it until they sent something of their
        /// own accord.
        /// </summary>
        [Test]
        public async Task NewlyOnlineClient_LearnsAboutContactsAlreadyOnline()
        {

            MakeContacts("alice", "bob");

            var bob = await ConnectClientAsync("bob");
            await bob.SetPresenceAsync("away", "Here for a while");

            var (_, atAlices) = await WatcherAsync("alice");

            await WaitFor(() => Saw(atAlices, bob), "the presence of the already logged-in Bob at Alice");

        }

        #endregion

        #region NewlyOnlineClient_LearnsNothingAboutNonContacts()

        /// <summary>
        /// The counter-check: the same way must give no information about
        /// strangers.
        /// </summary>
        [Test]
        public async Task NewlyOnlineClient_LearnsNothingAboutNonContacts()
        {

            var bob = await ConnectClientAsync("bob");
            await bob.SetPresenceAsync("away");

            var (_, atCarols) = await WatcherAsync("carol");

            await WaitAgainst(() => Saw(atCarols, bob), "Bob's presence at the unrelated Carol");

        }

        #endregion

        #region NewlyOnlineClient_LearnsNothingAboutAnUnavailableResource()

        /// <summary>
        /// A resource that has already signed off must not be handed on to a
        /// contact logging in.
        /// </summary>
        /// <remarks>
        /// RFC 6121, section 4.2.1: a resource that has signed off has no state
        /// to report. But the server remembered the sign-off as the last
        /// presence and handed it on to every contact that logged in
        /// afterwards.
        ///
        /// That was at the same time the cause of a failure that hit about
        /// every second full test run: if the server processed a contact's
        /// first presence only <b>after</b> the sign-off, that contact got it
        /// twice - once from the distribution, once from the handing on. Which
        /// order came about hung on the load.
        /// </remarks>
        [Test]
        public async Task NewlyOnlineClient_LearnsNothingAboutAnUnavailableResource()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice");

            await alice.SendRawAsync("<presence type='unavailable'/>");

            await WaitFor(() => Server.SessionOf(alice.FullJid)?.IsAvailable == false,
                          "Alice's sign-off on the server");

            var (_, atBobs) = await WatcherAsync("bob");

            await WaitAgainst(() => atBobs.Any(p => p.EndsWith("|unavailable", StringComparison.Ordinal)),
                              "a handed-on sign-off of the already signed-off Alice");

        }

        #endregion

        #region Probe_FromASubscriber_IsAnswered()

        /// <summary>
        /// An express probe (RFC 6121, section 4.3) the server answers with the
        /// current state - provided the asker may see it.
        /// </summary>
        [Test]
        public async Task Probe_FromASubscriber_IsAnswered()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice");
            await alice.SetPresenceAsync("dnd", "Do not disturb");

            var (bob, atBobs) = await WatcherAsync("bob");

            // Wait for what the login itself brings along first (section
            // 4.3.1), and *then* clear. Merely clearing is a race: if the
            // delivery from the login arrives only afterwards, it counts as the
            // answer to the probe — and the test would pass even with a server
            // that does not answer probes at all.
            await WaitFor(() => Saw(atBobs, alice), "Alice's state after the login");

            atBobs.Clear();

            await bob.SendRawAsync($"<presence type='probe' to='{alice.BareJid}'/>");

            await WaitFor(() => Saw(atBobs, alice), "the answer to the presence probe");

        }

        #endregion

        #region Probe_FromANonSubscriber_IsIgnored()

        /// <summary>
        /// Without the permission the probe stays unanswered. RFC 6121,
        /// section 4.3.2 leaves the server the choice between
        /// <c>&lt;unsubscribed/&gt;</c> and silence; silence does not even give
        /// away whether the account exists.
        /// </summary>
        [Test]
        public async Task Probe_FromANonSubscriber_IsIgnored()
        {

            var alice = await ConnectClientAsync("alice");
            await alice.SetPresenceAsync("dnd");

            var (carol, atCarols) = await WatcherAsync("carol");

            atCarols.Clear();

            await carol.SendRawAsync($"<presence type='probe' to='{alice.BareJid}'/>");

            await WaitAgainst(() => Saw(atCarols, alice), "the answer to the unauthorised probe");

        }

        #endregion

        #region Disconnect_MakesTheResourceUnavailable()

        /// <summary>
        /// RFC 6121, section 4.5.2: if the connection ends without the client
        /// having sent a <c>&lt;presence type='unavailable'/&gt;</c> itself,
        /// the server produces one in its name. Without that the contacts keep
        /// the resource as online for ever.
        /// </summary>
        [Test]
        public async Task Disconnect_MakesTheResourceUnavailable()
        {

            MakeContacts("alice", "bob");

            var alice          = await ConnectClientAsync("alice");
            var (_, atBobs)    = await WatcherAsync("bob");

            await alice.DisconnectAsync();

            await WaitFor(() => atBobs.Any(p => p.EndsWith("|unavailable", StringComparison.Ordinal)),
                          "the unavailable for the disconnected resource");

        }

        #endregion

        #region LostConnection_MakesTheResourceUnavailable()

        /// <summary>
        /// The same case, but roughly: the session is torn down without the
        /// client being able to say anything about it. That is exactly what the
        /// rule is there for.
        /// </summary>
        /// <remarks>
        /// Since XEP-0198 section 5 the sign-off no longer comes in the same
        /// breath: a torn stream is kept at first, because its client may come
        /// back, and only when it fails to come is the sign-off made up for.
        /// The rule from RFC 6121 holds unchanged - only after the deadline has
        /// run out.
        ///
        /// Hence a short deadline here. The default of one minute is right for
        /// service and unusable for a test.
        /// </remarks>
        [Test]
        public async Task LostConnection_MakesTheResourceUnavailable()
        {

            Server.ResumptionTimeout = TimeSpan.FromMilliseconds(1);

            MakeContacts("alice", "bob");

            var alice          = await ConnectClientAsync("alice");
            var (_, atBobs)    = await WatcherAsync("bob");

            Server.SessionOf(alice.FullJid)!.Kill();

            await WaitFor(() => atBobs.Any(p => p.EndsWith("|unavailable", StringComparison.Ordinal)),
                          "the unavailable for the torn resource",
                          TimeSpan.FromSeconds(20));

        }

        #endregion

        #region LostConnection_TellsOnlyTheSubscribers()

        /// <summary>
        /// The sign-off is a presence statement too and must not reach
        /// strangers - otherwise the end of a session would give away exactly
        /// what its beginning keeps quiet.
        /// </summary>
        [Test]
        public async Task LostConnection_TellsOnlyTheSubscribers()
        {

            MakeContacts("alice", "bob");

            var alice           = await ConnectClientAsync("alice");
            var (_, atCarols)   = await WatcherAsync("carol");

            Server.SessionOf(alice.FullJid)!.Kill();

            await WaitAgainst(() => atCarols.Any(p => p.EndsWith("|unavailable", StringComparison.Ordinal)),
                              "the unavailable at the unrelated Carol");

        }

        #endregion

        #region OwnUnavailable_IsNotRepeatedByTheServer()

        /// <summary>
        /// If the client has signed off properly, the matter is settled - the
        /// tearing down of the connection must not send the sign-off a second
        /// time.
        /// </summary>
        [Test]
        public async Task OwnUnavailable_IsNotRepeatedByTheServer()
        {

            MakeContacts("alice", "bob");

            var alice          = await ConnectClientAsync("alice");
            var (_, atBobs)    = await WatcherAsync("bob");

            await alice.SendRawAsync("<presence type='unavailable'/>");

            await WaitFor(() => atBobs.Any(p => p.EndsWith("|unavailable", StringComparison.Ordinal)),
                          "Alice's own sign-off");

            await alice.DisconnectAsync();

            // Give the counter-check its time: a second sign-off would come
            // immediately after the tearing down of the connection.
            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.That(atBobs.Count(p => p.EndsWith("|unavailable", StringComparison.Ordinal)),
                        Is.EqualTo(1),
                        "The sign-off may arrive exactly once.");

        }

        #endregion

        #region IsPresenceSubscriber_ReadsTheSubscriptionState()

        /// <summary>
        /// The direction at issue: <c>from</c> and <c>both</c> mean "the
        /// contact sees me". A <c>to</c> means the opposite, and confusing the
        /// two would give the presence to exactly the wrong half of the roster.
        /// </summary>
        [TestCase("both",   true)]
        [TestCase("from",   true)]
        [TestCase("to",     false)]
        [TestCase("none",   false)]
        [TestCase("remove", false)]
        public void IsPresenceSubscriber_ReadsTheSubscriptionState(String subscription, Boolean expected)
        {

            var account = new XMPPAccount("alice@localhost", "pw");
            account.SetRosterEntry(new RosterEntry("bob@localhost", null, subscription));

            Assert.That(account.IsPresenceSubscriber("bob@localhost"), Is.EqualTo(expected));

        }

        #endregion

        #region IsPresenceSubscriber_IsFalseForAnUnknownContact()

        /// <summary>Whoever does not stand in the roster at all sees nothing.</summary>
        [Test]
        public void IsPresenceSubscriber_IsFalseForAnUnknownContact()
        {

            var account = new XMPPAccount("alice@localhost", "pw");

            Assert.That(account.IsPresenceSubscriber("foreign@localhost"), Is.False);

        }

        #endregion

    }

}
