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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// Subscription pre-approval under RFC 6121, section 3.4: allowing a
    /// request before it has been made.
    /// </summary>
    /// <remarks>
    /// The section tells four cases apart, and all four hang on the same
    /// question - is there a request or not. The same
    /// <c>&lt;presence type='subscribed'/&gt;</c> is once a consent and once a
    /// pre-approval, and the stanza itself looks the same in both cases. The
    /// difference sits in the roster of the sender alone.
    /// </remarks>
    [TestFixture]
    public class PreApprovalTests : AXMPPTests
    {

        #region Helper functions

        private String Alice => $"alice@{Server.Domain}";
        private String Bob   => $"bob@{Server.Domain}";

        private String? SubscriptionOf(String owner, String contact)
            => Server.GetAccount(owner)?.SubscriptionOf(contact);

        private Boolean IsPreApproved(String owner, String contact)
            => Server.GetAccount(owner)?
                     .Roster.FirstOrDefault(e => e.Jid.Equals(contact, StringComparison.OrdinalIgnoreCase))?
                     .Approved == true;

        #endregion


        #region TheServerAdvertisesPreApproval()

        /// <summary>
        /// Section 3.4: a server that can do it has to announce it - and
        /// without the announcement a client must not use it.
        /// </summary>
        [Test]
        public async Task TheServerAdvertisesPreApproval()
        {

            var alice = await ConnectClientAsync("alice");

            Assert.Multiple(() =>
            {
                Assert.That(alice.Connection.ServerFeatures,
                            Does.Contain("urn:xmpp:features:pre-approval"));
                Assert.That(alice.ServerSupportsPreApproval, Is.True);
            });

        }

        #endregion

        #region WithoutAPendingRequest_SubscribedIsRememberedNotSent()

        /// <summary>
        /// Cases 3 and 4: without an open request it is pre-approved - and the
        /// stanza expressly does <b>not</b> go out.
        /// </summary>
        /// <remarks>
        /// The second half is the more important one and easy to overlook. If
        /// the <c>subscribed</c> went out all the same, the contact would get a
        /// consent to a question they never asked - their server would build a
        /// subscription out of it that the user knows nothing about.
        /// </remarks>
        [Test]
        public async Task WithoutAPendingRequest_SubscribedIsRememberedNotSent()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var atBob = new List<String>();
            bob.OnPresenceChanged += (from, type) => atBob.Add($"{from}/{type}");

            await alice.PreApproveContactAsync(Bob);

            await WaitFor(() => IsPreApproved(Alice, Bob), "the pre-approval at Alice");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(IsPreApproved(Alice, Bob), Is.True);

                // Pre-approved does not yet mean entitled.
                Assert.That(SubscriptionOf(Alice, Bob), Is.EqualTo("none"));

                Assert.That(atBob.Any(e => e.Contains("subscribed", StringComparison.Ordinal)),
                            Is.False,
                            "Without a request having been made no consent may go out.");
            });

        }

        #endregion

        #region APreApprovedRequest_IsAnsweredWithoutAskingTheUser()

        /// <summary>
        /// Section 3.4.2: if the contact is pre-approved, their request must not
        /// be delivered to the user at all - the server answers for them.
        /// </summary>
        [Test]
        public async Task APreApprovedRequest_IsAnsweredWithoutAskingTheUser()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var requestsAtAlice = new List<String>();
            alice.OnSubscriptionRequest += (from, _) => requestsAtAlice.Add(from);

            await alice.PreApproveContactAsync(Bob);
            await WaitFor(() => IsPreApproved(Alice, Bob), "the pre-approval");

            // Now Bob actually asks.
            await bob.AddContactAsync(Alice, "Alice");

            await WaitFor(() => SubscriptionOf(Bob, Alice) == "to",
                          "Bob's 'to' half from the automatic consent");

            Assert.Multiple(() =>
            {
                Assert.That(SubscriptionOf(Alice, Bob), Is.EqualTo("from"));
                Assert.That(SubscriptionOf(Bob,   Alice), Is.EqualTo("to"));

                Assert.That(requestsAtAlice, Is.Empty,
                            "A pre-approved request must not reach the user.");
            });

        }

        #endregion

        #region WithAPendingRequest_SubscribedIsANormalApproval()

        /// <summary>
        /// Case 2: if a request is there, the same <c>subscribed</c> is an
        /// ordinary consent - with forwarding.
        /// </summary>
        /// <remarks>
        /// The counter-check to the pre-approval. Without it the suspicion
        /// would stand that every <c>subscribed</c> is only pre-approved now
        /// and never delivered again.
        /// </remarks>
        [Test]
        public async Task WithAPendingRequest_SubscribedIsANormalApproval()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var requests = new List<String>();
            bob.OnSubscriptionRequest += (from, _) => requests.Add(from);

            await alice.AddContactAsync(Bob, "Bob");
            await WaitFor(() => requests.Count > 0, "the request at Bob");

            await bob.AcceptSubscriptionAsync(Alice);

            await WaitFor(() => SubscriptionOf(Alice, Bob) == "to",
                          "Alice's 'to' half");

            Assert.Multiple(() =>
            {
                Assert.That(SubscriptionOf(Bob,   Alice), Is.EqualTo("from"));
                Assert.That(SubscriptionOf(Alice, Bob),   Is.EqualTo("to"));

                // A request that was answered is no pre-approval.
                Assert.That(IsPreApproved(Bob, Alice), Is.False);
            });

        }

        #endregion

        #region AnEstablishedSubscription_IgnoresAFurtherSubscribed()

        /// <summary>
        /// Case 1: if the contact may see us anyway, a further
        /// <c>subscribed</c> is passed over in silence.
        /// </summary>
        [Test]
        public async Task AnEstablishedSubscription_IgnoresAFurtherSubscribed()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var requests = new List<String>();
            bob.OnSubscriptionRequest += (from, _) => requests.Add(from);

            await alice.AddContactAsync(Bob, "Bob");
            await WaitFor(() => requests.Count > 0, "the request at Bob");

            await bob.AcceptSubscriptionAsync(Alice);
            await WaitFor(() => SubscriptionOf(Bob, Alice) == "from", "the consent");

            // Once more - that must change nothing, and in particular produce
            // no pre-approval.
            //
            // Over the connection and not over the client: its
            // AcceptSubscriptionAsync demands an open request and would simply
            // do nothing here. The test would then have passed without ever
            // having sent the stanza off - and that is exactly how it ran
            // through at first.
            await bob.Connection.AcceptSubscriptionAsync(Alice);

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(SubscriptionOf(Bob, Alice), Is.EqualTo("from"));
                Assert.That(IsPreApproved(Bob, Alice),  Is.False);
            });

        }

        #endregion

        #region UnsubscribedCancelsThePreApproval()

        /// <summary>
        /// Section 3.4.2, note: a pre-approval can be taken back with an
        /// <c>unsubscribed</c>.
        /// </summary>
        [Test]
        public async Task UnsubscribedCancelsThePreApproval()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            await alice.PreApproveContactAsync(Bob);
            await WaitFor(() => IsPreApproved(Alice, Bob), "the pre-approval");

            await alice.DenySubscriptionAsync(Bob);
            await WaitFor(() => !IsPreApproved(Alice, Bob), "the taking back");

            var requests = new List<String>();
            alice.OnSubscriptionRequest += (from, _) => requests.Add(from);

            // Without the pre-approval Bob's request has to land at Alice
            // again.
            await bob.AddContactAsync(Alice, "Alice");

            await WaitFor(() => requests.Count > 0,
                          "the request at Alice after the pre-approval was taken back");

            Assert.That(SubscriptionOf(Alice, Bob), Is.EqualTo("none"));

        }

        #endregion

        #region WithPreApprovalTurnedOff_NothingIsRemembered()

        /// <summary>
        /// Without support nothing is announced and nothing pre-approved.
        /// </summary>
        /// <remarks>
        /// The section expressly leaves pre-approval optional. A server that
        /// switches it off may leave a <c>subscribed</c> without a request
        /// without consequence - it may only not announce it and then behave
        /// otherwise.
        /// </remarks>
        [Test]
        public async Task WithPreApprovalTurnedOff_NothingIsRemembered()
        {

            Server.OfferSubscriptionPreApproval = false;

            var alice = await ConnectClientAsync("alice");
            await ConnectClientAsync("bob");

            Assert.Multiple(() =>
            {
                Assert.That(alice.Connection.ServerFeatures,
                            Does.Not.Contain("urn:xmpp:features:pre-approval"));
                Assert.That(alice.ServerSupportsPreApproval, Is.False);
            });

            // Section 3.4.1: without the announcement the client must not even
            // try - the method refuses of its own accord.
            Assert.That(await alice.PreApproveContactAsync(Bob), Is.False);

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(IsPreApproved(Alice, Bob),   Is.False);
                Assert.That(SubscriptionOf(Alice, Bob),  Is.Not.EqualTo("from"));
            });

        }

        #endregion

    }

}
