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
    /// Stored subscription requests according to RFC 6121, section 3.1.3,
    /// rule 4: whoever is not connected right now shall get their requests
    /// nevertheless.
    /// </summary>
    /// <remarks>
    /// Without the storing a request to a logged-out account is lost for good -
    /// and unnoticed on both sides at that. The applicant sees an
    /// <c>ask='subscribe'</c> in their roster and waits for an answer; the
    /// contact never learned that they were asked. That was exactly the case
    /// here until recently.
    ///
    /// The section demands more than handing the request over once: what is
    /// stored is the <b>complete</b> stanza, and it is delivered at
    /// <b>every</b> newly available resource, until the contact approves or
    /// denies.
    /// </remarks>
    [TestFixture]
    public class OfflineSubscriptionRequestTests : AXMPPTests
    {

        #region Helper functions

        private String Alice => $"alice@{Server.Domain}";
        private String Bob   => $"bob@{Server.Domain}";

        /// <summary>
        /// A not yet connected client with an attached counter for incoming
        /// requests.
        /// </summary>
        /// <remarks>
        /// The counter is attached <b>before</b> connecting, not afterwards: a
        /// handed-over request comes immediately after the first presence, and
        /// a recipient that logs in only afterwards misses it depending on the
        /// timing. A test failing that way looks like an error in the server.
        /// </remarks>
        private (XMPPClient, List<(JID From, String? Status)>) PreparedClient(String localPart)
        {

            var client  = CreateClient(localPart);
            var arrived = new List<(JID, String?)>();

            client.OnSubscriptionRequest += (timestamp, sender, from, status, ct) => { arrived.Add((from, status)); return Task.CompletedTask; };

            return (client, arrived);

        }

        /// <summary>Logs a client out and waits until the server sees it.</summary>
        private async Task DisconnectAndWaitAsync(XMPPClient client, String bareJid)
        {
            await client.DisconnectAsync();
            await WaitFor(() => Server.SessionsOf(bareJid).Count == 0,
                          $"the end of the session of {bareJid}");
        }

        #endregion


        #region ARequestToAnOfflineAccount_ArrivesAtTheNextLogin()

        /// <summary>
        /// The core of rule 4: Bob is not there when Alice asks - and learns of
        /// it at the next login.
        /// </summary>
        [Test]
        public async Task ARequestToAnOfflineAccount_ArrivesAtTheNextLogin()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.AddContactAsync(JID.Parse(Bob), "Bob");

            // Only now does Bob come - the request has been lying for a while.
            var (bob, requests) = PreparedClient("bob");
            await bob.ConnectAsync();

            await WaitFor(() => requests.Count > 0, "the handed-over request");

            Assert.That(requests[0].From, Is.EqualTo(Alice));

        }

        #endregion

        #region NothingIsAddedToTheRosterBeforeApproval()

        /// <summary>
        /// Section 3.1.3, Security Warning: "the contact's server MUST NOT
        /// add an item for the user to the contact's roster" - as long as
        /// nothing has been approved.
        /// </summary>
        /// <remarks>
        /// The warning has a solid reason: an entry in the roster is visible to
        /// the contact and outlives the request. Whoever can write arbitrary
        /// strangers into foreign rosters can fill them up. The request is
        /// therefore stored next to the roster, not within it.
        /// </remarks>
        [Test]
        public async Task NothingIsAddedToTheRosterBeforeApproval()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.AddContactAsync(JID.Parse(Bob), "Bob");

            var (bob, requests) = PreparedClient("bob");
            await bob.ConnectAsync();

            await WaitFor(() => requests.Count > 0, "the handed-over request");

            Assert.That(Server.GetAccount(Bob)!.Roster, Is.Empty,
                        "Before the approval the applicant does not belong into Bob's roster.");

        }

        #endregion

        #region TheCompleteStanzaIsKept()

        /// <summary>
        /// Rule 4 demands the complete stanza, "including any extended
        /// content contained therein".
        /// </summary>
        /// <remarks>
        /// The extended content is no formality here: the
        /// <c>&lt;status/&gt;</c> of a request is the reason a human being
        /// decides on whether to approve. A request without it is a different
        /// request.
        /// </remarks>
        [Test]
        public async Task TheCompleteStanzaIsKept()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.Connection.SendRawAsync(
                      $"<presence to='{Bob}' type='subscribe'><status>We know each other from the platform</status></presence>");

            var (bob, requests) = PreparedClient("bob");
            await bob.ConnectAsync();

            await WaitFor(() => requests.Count > 0, "the handed-over request");

            Assert.That(requests[0].Status, Is.EqualTo("We know each other from the platform"));

        }

        #endregion

        #region TheRequestIsRepeatedUntilItIsAnswered()

        /// <summary>
        /// "The contact's server MUST continue to deliver the subscription
        /// request whenever the contact creates an available resource, until
        /// the contact either approves or denies the request."
        /// </summary>
        /// <remarks>
        /// Handing it over once is not enough. Whoever logs in, overlooks the
        /// request and logs out again would otherwise have lost it forever -
        /// and the applicant waited for an answer nobody can give any more.
        /// </remarks>
        [Test]
        public async Task TheRequestIsRepeatedUntilItIsAnswered()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.AddContactAsync(JID.Parse(Bob), "Bob");

            var (firstLogin, firstRequests) = PreparedClient("bob");
            await firstLogin.ConnectAsync();
            await WaitFor(() => firstRequests.Count > 0, "the request at the first login");

            // Away again without answering.
            await DisconnectAndWaitAsync(firstLogin, Bob);

            var (secondLogin, secondRequests) = PreparedClient("bob");
            await secondLogin.ConnectAsync();

            await WaitFor(() => secondRequests.Count > 0, "the request at the second login");

            Assert.That(secondRequests[0].From, Is.EqualTo(Alice));

        }

        #endregion

        #region AnApprovedRequest_IsNotRepeated()

        /// <summary>
        /// "... until the contact either approves or denies the request."
        /// An approval ends the handing over.
        /// </summary>
        [Test]
        public async Task AnApprovedRequest_IsNotRepeated()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.AddContactAsync(JID.Parse(Bob), "Bob");

            var (firstLogin, firstRequests) = PreparedClient("bob");
            await firstLogin.ConnectAsync();
            await WaitFor(() => firstRequests.Count > 0, "the request at the first login");

            await firstLogin.AcceptSubscriptionAsync(JID.Parse(Alice));
            await WaitFor(() => Server.GetAccount(Bob)?.SubscriptionOf(Alice) == "from",
                          "the approval");

            await DisconnectAndWaitAsync(firstLogin, Bob);

            var (secondLogin, secondRequests) = PreparedClient("bob");
            await secondLogin.ConnectAsync();

            await WaitAgainst(() => secondRequests.Count > 0,
                              "an already answered request once more");

        }

        #endregion

        #region ADeniedRequest_IsNotRepeated()

        /// <summary>
        /// A denial likewise - otherwise the same request would come again at
        /// every login, and denying would be without effect.
        /// </summary>
        [Test]
        public async Task ADeniedRequest_IsNotRepeated()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            await alice.AddContactAsync(JID.Parse(Bob), "Bob");

            var (firstLogin, firstRequests) = PreparedClient("bob");
            await firstLogin.ConnectAsync();
            await WaitFor(() => firstRequests.Count > 0, "the request at the first login");

            await firstLogin.DenySubscriptionAsync(JID.Parse(Alice));
            await WaitFor(() => Server.GetAccount(Alice)?.SubscriptionOf(Bob) is null or "none",
                          "the denial");

            await DisconnectAndWaitAsync(firstLogin, Bob);

            var (secondLogin, secondRequests) = PreparedClient("bob");
            await secondLogin.ConnectAsync();

            await WaitAgainst(() => secondRequests.Count > 0,
                              "an already denied request once more");

        }

        #endregion

        #region RepeatedRequests_AreStoredOnlyOnce()

        /// <summary>
        /// Rule 4: "MUST deliver only one of the requests when the contact
        /// next has an available resource; ... this helps to prevent
        /// 'subscription request spam'".
        /// </summary>
        /// <remarks>
        /// Without this limit the storing would itself be the weak spot:
        /// whoever sends a request a hundred times while the contact is away
        /// would deluge them with a hundred requests at the login.
        /// </remarks>
        [Test]
        public async Task RepeatedRequests_AreStoredOnlyOnce()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            for (var i = 0; i < 5; i++)
                await alice.Connection.SendRawAsync($"<presence to='{Bob}' type='subscribe'/>");

            var (bob, requests) = PreparedClient("bob");
            await bob.ConnectAsync();

            await WaitFor(() => requests.Count > 0, "the handed-over request");

            // The further ones could have arrived by now.
            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.That(requests, Has.Count.EqualTo(1));

        }

        #endregion

        #region AFurtherRequest_IsNotDeliveredAgain()

        /// <summary>
        /// Appendix A, table 6: if a request is already there, a further
        /// <c>subscribe</c> of the same sender <i>should</i> not be delivered
        /// once more.
        /// </summary>
        /// <remarks>
        /// The counter-check to <see cref="RepeatedRequests_AreStoredOnlyOnce"/>,
        /// and the sharper one: there the contact is logged out, and whether a
        /// repeated request is turned away or replaces the stored one looks the
        /// same in the end - delivered once. Here they are connected, and the
        /// difference becomes visible, because every accepted request goes out
        /// right away.
        ///
        /// What is checked is therefore the content as well: what is stored and
        /// delivered stays the <b>first</b> one. Were the last one to decide,
        /// somebody could exchange the reason they once asked with for another
        /// one arbitrarily often.
        /// </remarks>
        [Test]
        public async Task AFurtherRequest_IsNotDeliveredAgain()
        {

            var alice = await ConnectClientAsync("alice");

            var (bob, requests) = PreparedClient("bob");
            Server.AddAccount("bob");
            await bob.ConnectAsync();

            await alice.Connection.SendRawAsync(
                      $"<presence to='{Bob}' type='subscribe'><status>first</status></presence>");
            await WaitFor(() => requests.Count > 0, "the first request");

            await alice.Connection.SendRawAsync(
                      $"<presence to='{Bob}' type='subscribe'><status>second</status></presence>");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(requests, Has.Count.EqualTo(1));
                Assert.That(requests[0].Status, Is.EqualTo("first"),
                            "What stays stored is the request asked first.");
            });

        }

        #endregion

        #region AStatusChange_DoesNotRepeatTheRequest()

        /// <summary>
        /// The handing over happens at the <i>becoming</i> available, not at
        /// every presence.
        /// </summary>
        /// <remarks>
        /// The difference is easy to overlook and noticeable at once in
        /// operation: a client sends a new presence at every change to "away"
        /// or "busy". Were the handing over to hang on that, the user would be
        /// presented with the same unanswered request over and over.
        /// </remarks>
        [Test]
        public async Task AStatusChange_DoesNotRepeatTheRequest()
        {

            var alice = await ConnectClientAsync("alice");

            var (bob, requests) = PreparedClient("bob");
            Server.AddAccount("bob");
            await bob.ConnectAsync();

            await alice.AddContactAsync(JID.Parse(Bob), "Bob");
            await WaitFor(() => requests.Count > 0, "the request");

            // The same session, a new presence - the request is still
            // unanswered and still lies stored.
            await bob.SetPresenceAsync("away", "Lunch break");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.That(requests, Has.Count.EqualTo(1));

        }

        #endregion

        #region TheLimit_DropsFurtherRequestsInsteadOfTheStoredOnes()

        /// <summary>
        /// Security Warning to section 3.1.3: whoever keeps requests keeps what
        /// strangers send - hence an upper limit.
        /// </summary>
        /// <remarks>
        /// What is checked is not only <i>that</i> the limit takes hold, but in
        /// which direction: the new request is discarded, the already stored
        /// one stays. The other way round an attacker could push out the real
        /// request of an acquaintance on purpose - a limit that displaces
        /// instead of turning away would itself be the attack.
        /// </remarks>
        [Test]
        public async Task TheLimit_DropsFurtherRequestsInsteadOfTheStoredOnes()
        {

            Server.MaxStoredSubscriptionRequests = 1;

            var alice = await ConnectClientAsync("alice");
            var carol = await ConnectClientAsync("carol");
            Server.AddAccount("bob");

            await alice.AddContactAsync(JID.Parse(Bob), "Bob");
            await WaitFor(() => Server.GetAccount(Bob)!.PendingSubscriptionRequests.Count == 1,
                          "Alice's stored request");

            await carol.AddContactAsync(JID.Parse(Bob), "Bob");

            var (bob, requests) = PreparedClient("bob");
            await bob.ConnectAsync();

            await WaitFor(() => requests.Count > 0, "the handed-over request");
            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(requests, Has.Count.EqualTo(1));
                Assert.That(requests[0].From, Is.EqualTo(Alice),
                            "The request stored first stays.");
            });

        }

        #endregion

        #region ASecondResource_AlsoGetsTheRequest()

        /// <summary>
        /// "... whenever the contact creates an available resource": a second
        /// resource next to an already logged-in one as well.
        /// </summary>
        /// <remarks>
        /// The case looks artificial but is the normal one: phone and desktop
        /// at the same time. The request is answered where the human being is
        /// sitting right now - and that is not necessarily the resource that
        /// was there first.
        /// </remarks>
        [Test]
        public async Task ASecondResource_AlsoGetsTheRequest()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            var (first, firstRequests) = PreparedClient("bob");
            await first.ConnectAsync();

            await alice.AddContactAsync(JID.Parse(Bob), "Bob");
            await WaitFor(() => firstRequests.Count > 0, "the request at the first resource");

            var (second, secondRequests) = PreparedClient("bob");
            await second.ConnectAsync();

            await WaitFor(() => secondRequests.Count > 0, "the request at the second resource");

            Assert.That(secondRequests[0].From, Is.EqualTo(Alice));

        }

        #endregion

    }

}
