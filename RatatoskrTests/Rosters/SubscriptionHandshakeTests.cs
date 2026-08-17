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
    /// RFC 6121, section 3: the subscription handshake.
    ///
    /// The presence filtering evaluates the subscription states - but up to
    /// here the server could not <b>bring them about</b>: <c>subscribe</c> and
    /// <c>subscribed</c> were merely passed on, without changing the rosters.
    /// That left the way the client offers with
    /// <c>AcceptSubscriptionAsync</c> without consequence.
    /// </summary>
    [TestFixture]
    public class SubscriptionHandshakeTests : AXMPPTests
    {

        #region Helper functions

        private String Alice => $"alice@{Server.Domain}";
        private String Bob   => $"bob@{Server.Domain}";

        private String? SubscriptionOf(String owner, String contact)
            => Server.GetAccount(owner)?.SubscriptionOf(contact);

        private String? AskOf(String owner, String contact)
            => Server.GetAccount(owner)?.Roster
                     .FirstOrDefault(e => String.Equals(e.Jid, contact, StringComparison.OrdinalIgnoreCase))
                     ?.Ask;

        /// <summary>
        /// Connects a client and collects every presence announcement from now
        /// on as <c>jid|type</c>.
        /// </summary>
        private async Task<(XMPPClient Client, ConcurrentQueue<String> Presences)> WatcherAsync(String localPart)
        {

            var client     = await ConnectClientAsync(localPart);
            var presences  = new ConcurrentQueue<String>();

            client.OnPresenceChanged += (timestamp, sender, from, type, ct) => { presences.Enqueue($"{from}|{type}"); return Task.CompletedTask; };

            return (client, presences);

        }

        /// <summary>
        /// Alice asks Bob, Bob accepts - the complete handshake over the public
        /// client API.
        /// </summary>
        private async Task<(XMPPClient Alice, XMPPClient Bob)> HandshakeAsync()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var requests = new ConcurrentQueue<String>();
            bob.OnSubscriptionRequest += (timestamp, sender, from, _, ct) => { requests.Enqueue(from); return Task.CompletedTask; };

            await alice.AddContactAsync(Bob, "Bob");

            await WaitFor(() => requests.Any(r => r.Equals(Alice, StringComparison.OrdinalIgnoreCase)),
                          "the contact request at Bob");

            await bob.AcceptSubscriptionAsync(Alice);

            await WaitFor(() => SubscriptionOf(Bob, Alice) is "from" or "both",
                          "the subscription state after the acceptance");

            return (alice, bob);

        }

        #endregion


        #region Subscribe_MarksThePendingRequestInTheRoster()

        /// <summary>
        /// RFC 6121, section 3.1.2: the request creates the entry - with
        /// <c>subscription='none'</c>, since nothing is allowed yet - and notes
        /// it as open through <c>ask='subscribe'</c>.
        /// </summary>
        [Test]
        public async Task Subscribe_MarksThePendingRequestInTheRoster()
        {

            var alice = await ConnectClientAsync("alice");
            await ConnectClientAsync("bob");

            await alice.AddContactAsync(Bob, "Bob");

            await WaitFor(() => AskOf(Alice, Bob) == "subscribe", "the open request in Alice's roster");

            Assert.That(SubscriptionOf(Alice, Bob), Is.EqualTo("none"),
                        "An open request allows nothing yet.");

        }

        #endregion

        #region Subscribe_ReachesTheContact()

        /// <summary>
        /// The request has to arrive at the contact - that worked before
        /// already, because directed presence was forwarded. Stays as a
        /// counter-check.
        /// </summary>
        [Test]
        public async Task Subscribe_ReachesTheContact()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var requests = new ConcurrentQueue<String>();
            bob.OnSubscriptionRequest += (timestamp, sender, from, _, ct) => { requests.Enqueue(from); return Task.CompletedTask; };

            await alice.AddContactAsync(Bob, "Bob");

            await WaitFor(() => requests.Any(r => r.Equals(Alice, StringComparison.OrdinalIgnoreCase)),
                          "the contact request at Bob");

        }

        #endregion

        #region Approval_SetsBothSidesOfTheRoster()

        /// <summary>
        /// RFC 6121, sections 3.1.5 and 3.1.6: the acceptance writes into
        /// <b>both</b> rosters, each in the fitting direction. Bob's entry for
        /// Alice gets <c>from</c> ("Alice sees me"), Alice's entry for Bob
        /// <c>to</c> ("I see Bob"), and the open request is settled.
        /// </summary>
        [Test]
        public async Task Approval_SetsBothSidesOfTheRoster()
        {

            await HandshakeAsync();

            await WaitFor(() => SubscriptionOf(Alice, Bob) is "to" or "both",
                          "the subscription state at Alice");

            Assert.Multiple(() =>
            {
                Assert.That(SubscriptionOf(Bob, Alice), Is.AnyOf("from", "both"));
                Assert.That(AskOf(Alice, Bob), Is.Null, "The request is answered.");
            });

        }

        #endregion

        #region Approval_MakesThePresenceFlow()

        /// <summary>
        /// The real purpose: after the acceptance Alice sees Bob's presence.
        /// Without the change of state the server filtered it away, because
        /// nothing stood in either roster.
        /// </summary>
        [Test]
        public async Task Approval_MakesThePresenceFlow()
        {

            var (alice, bob) = await HandshakeAsync();

            var atAlices = new ConcurrentQueue<String>();
            alice.OnPresenceChanged += (timestamp, sender, from, type, ct) => { atAlices.Enqueue($"{from}|{type}"); return Task.CompletedTask; };

            await bob.SetPresenceAsync("away", "Later");

            // Insist on 'available': a <presence type='subscribed'/> runs
            // through the same event and would otherwise be half the answer
            // already.
            await WaitFor(() => atAlices.Any(p => p.StartsWith(Bob, StringComparison.OrdinalIgnoreCase) &&
                                                  p.EndsWith("|available", StringComparison.Ordinal)),
                          "Bob's presence at Alice");

        }

        #endregion

        #region Approval_DeliversTheCurrentPresenceAtOnce()

        /// <summary>
        /// RFC 6121, section 3.1.5: "The contact's server MUST then also send
        /// current presence to the user from each of the contact's available
        /// resources." The applicant should not have to wait until the contact
        /// next sends something of their own accord.
        /// </summary>
        [Test]
        public async Task Approval_DeliversTheCurrentPresenceAtOnce()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var requests = new ConcurrentQueue<String>();
            bob.OnSubscriptionRequest += (timestamp, sender, from, _, ct) => { requests.Enqueue(from); return Task.CompletedTask; };

            var atAlices = new ConcurrentQueue<String>();
            alice.OnPresenceChanged += (timestamp, sender, from, type, ct) => { atAlices.Enqueue($"{from}|{type}"); return Task.CompletedTask; };

            await alice.AddContactAsync(Bob, "Bob");
            await WaitFor(() => requests.Any(r => r.Equals(Alice, StringComparison.OrdinalIgnoreCase)),
                          "the contact request at Bob");

            await bob.AcceptSubscriptionAsync(Alice);

            // Bob deliberately sends nothing after: the server has to hand the
            // presence on of its own accord. Insist on 'available': the
            // <presence type='subscribed'/> itself runs through the same event
            // and would otherwise be half the answer already.
            await WaitFor(() => atAlices.Any(p => p.StartsWith(Bob, StringComparison.OrdinalIgnoreCase) &&
                                                  p.EndsWith("|available", StringComparison.Ordinal)),
                          "Bob's presence handed on");

        }

        #endregion

        #region Denial_GrantsNothing()

        /// <summary>
        /// A refusal closes the request without allowing anything.
        /// </summary>
        [Test]
        public async Task Denial_GrantsNothing()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var requests = new ConcurrentQueue<String>();
            bob.OnSubscriptionRequest += (timestamp, sender, from, _, ct) => { requests.Enqueue(from); return Task.CompletedTask; };

            await alice.AddContactAsync(Bob, "Bob");
            await WaitFor(() => requests.Any(r => r.Equals(Alice, StringComparison.OrdinalIgnoreCase)),
                          "the contact request at Bob");

            await bob.DenySubscriptionAsync(Alice);

            await WaitFor(() => AskOf(Alice, Bob) is null, "the request settled at Alice");

            Assert.Multiple(() =>
            {
                Assert.That(SubscriptionOf(Alice, Bob), Is.EqualTo("none"));
                Assert.That(Server.GetAccount(Bob)!.IsPresenceSubscriber(Alice), Is.False,
                            "A refusal must not bring about any visibility.");
            });

        }

        #endregion

        #region Cancellation_SendsUnavailable()

        /// <summary>
        /// RFC 6121, section 3.2.2: "the contact's server MUST send a presence
        /// stanza of type 'unavailable' from all of the contact's online
        /// resources". Otherwise Alice would keep Bob's last known state for
        /// ever - although she may no longer see him.
        /// </summary>
        [Test]
        public async Task Cancellation_SendsUnavailable()
        {

            var (alice, bob) = await HandshakeAsync();

            var atAlices = new ConcurrentQueue<String>();
            alice.OnPresenceChanged += (timestamp, sender, from, type, ct) => { atAlices.Enqueue($"{from}|{type}"); return Task.CompletedTask; };

            await bob.SendRawAsync($"<presence to='{Alice}' type='unsubscribed'/>");

            await WaitFor(() => atAlices.Any(p => p.StartsWith(Bob, StringComparison.OrdinalIgnoreCase) &&
                                                  p.EndsWith("|unavailable", StringComparison.Ordinal)),
                          "the unavailable after the withdrawal");

        }

        #endregion

        #region Unsubscribe_EndsTheOwnSubscription()

        /// <summary>
        /// RFC 6121, section 3.3: Alice cancels of her own accord. Afterwards
        /// she no longer sees Bob - and Bob's entry for her loses the
        /// <c>from</c>.
        /// </summary>
        /// <remarks>
        /// The cancelling runs over <c>CancelSubscriptionAsync</c> and no
        /// longer over a presence written by hand. The difference is not
        /// cosmetic: until D57 that was the only one of the four transitions
        /// from section 3 the client did <b>not</b> offer — the building block
        /// for it stood there unused, and this test bridged the gap unnoticed
        /// by writing the stanza itself.
        /// </remarks>
        [Test]
        public async Task Unsubscribe_EndsTheOwnSubscription()
        {

            var (alice, _) = await HandshakeAsync();

            await alice.CancelSubscriptionAsync(Bob);

            // Wait on Bob's side, not on Alice's: the server changes the roster
            // of the sender first and then that of the other side. Whoever
            // waits for the first may check the second before it exists.
            await WaitFor(() => !Server.GetAccount(Bob)!.IsPresenceSubscriber(Alice),
                          "the visibility withdrawn in Bob's roster");

            Assert.That(SubscriptionOf(Alice, Bob), Is.AnyOf("none", "from"),
                        "Alice has cancelled her own subscription.");

        }

        #endregion

        #region RosterSet_DoesNotResetTheSubscription()

        /// <summary>
        /// RFC 6121, section 2.3: a roster set changes name and groups, but
        /// <b>not</b> the subscription state. The server used to take the
        /// missing attribute as <c>none</c> - merely renaming a contact would
        /// thereby have deleted the permission just granted.
        /// </summary>
        [Test]
        public async Task RosterSet_DoesNotResetTheSubscription()
        {

            var (alice, _) = await HandshakeAsync();

            await WaitFor(() => SubscriptionOf(Alice, Bob) is "to" or "both", "the subscription before the renaming");

            var before = SubscriptionOf(Alice, Bob);

            await alice.SendRawAsync(
                "<iq type='set' id='rename-1'><query xmlns='jabber:iq:roster'>" +
                $"<item jid='{Bob}' name='Bobby'/></query></iq>");

            await WaitFor(() => Server.GetAccount(Alice)!.Roster
                                      .Any(e => e.Jid.Equals(Bob, StringComparison.OrdinalIgnoreCase) &&
                                                e.Name == "Bobby"),
                          "the renamed contact");

            Assert.That(SubscriptionOf(Alice, Bob), Is.EqualTo(before),
                        "A roster set must not touch the subscription state.");

        }

        #endregion

        #region GrantAndRevoke_ChangeOnlyTheirOwnHalf()

        /// <summary>
        /// The four transitions one by one. Whoever takes them for a scale from
        /// none to both loses exactly the other direction: out of <c>both</c> a
        /// withdrawal would make <c>none</c> instead of the remaining half.
        /// </summary>
        [TestCase("none", "from", "none", "to")]
        [TestCase("to",   "both", "to",   "to")]
        [TestCase("from", "from", "none", "both")]
        [TestCase("both", "both", "to",   "both")]
        public void GrantAndRevoke_ChangeOnlyTheirOwnHalf(String start,
                                                          String afterGrantFrom,
                                                          String afterRevokeFrom,
                                                          String afterGrantTo)
        {

            Assert.Multiple(() =>
            {
                Assert.That(XMPPServer.GrantFrom(start),  Is.EqualTo(afterGrantFrom),  "GrantFrom");
                Assert.That(XMPPServer.RevokeFrom(start), Is.EqualTo(afterRevokeFrom), "RevokeFrom");
                Assert.That(XMPPServer.GrantTo(start),    Is.EqualTo(afterGrantTo),    "GrantTo");
            });

        }

        #endregion

        #region RevokeTo_KeepsTheOtherDirection()

        /// <summary>The other direction to <c>RevokeFrom</c>.</summary>
        [TestCase("none", "none")]
        [TestCase("to",   "none")]
        [TestCase("from", "from")]
        [TestCase("both", "from")]
        public void RevokeTo_KeepsTheOtherDirection(String start, String expected)
        {
            Assert.That(XMPPServer.RevokeTo(start), Is.EqualTo(expected));
        }

        #endregion

    }

}
