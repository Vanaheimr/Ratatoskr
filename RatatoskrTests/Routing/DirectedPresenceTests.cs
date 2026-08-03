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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// RFC 6121, section 4.6.3, rule 2: When a resource becomes unavailable,
    /// the sign-off goes to the recipients of its directed presence as well.
    /// </summary>
    /// <remarks>
    /// The rule closes a gap nobody notices otherwise. Whoever shows a stranger
    /// their presence does not thereby stand in that stranger's roster — and
    /// would never get an ending without this path. The stranger would keep the
    /// resource as present forever.
    ///
    /// The case is the normal one and not the exception: A conversation with
    /// somebody who is not in the roster begins according to section 5.1 with
    /// exactly that, sending them directed presence. Since D17 who may ask this
    /// resource anything at all hangs on it too (section 8.5.3.1) — a promise
    /// that never ends would thereby be doubly unpleasant.
    ///
    /// Two paths lead into unavailability, and both stand here: the client's
    /// own sign-off and the torn connection, where the server creates it in
    /// their name (section 4.5.2).
    /// </remarks>
    [TestFixture]
    public class DirectedPresenceTests : AXMPPTests
    {

        #region Helper functions

        /// <summary>
        /// Collects the presence notices of a client.
        /// </summary>
        private static ConcurrentQueue<(String From, String? Type)> PresenceBasket(XMPPClient client)
        {

            var basket = new ConcurrentQueue<(String, String?)>();
            client.Connection.OnPresence += (from, type) => basket.Enqueue((from, type));

            return basket;

        }

        /// <summary>Only the sign-offs out of it.</summary>
        private static Int32 SignOffs(ConcurrentQueue<(String From, String? Type)> basket)
            => basket.Count(p => p.Type == "unavailable");

        #endregion


        #region GoingUnavailable_TellsTheDirectedTarget()

        /// <summary>
        /// The core: Bob shows Alice his presence, signs off — and Alice learns
        /// of it although she stands in no roster.
        /// </summary>
        /// <remarks>
        /// The first half establishes that Alice hears anything at all only
        /// because of the directed presence. Without it every presence between
        /// the two would be one among strangers, and that must not exist — the
        /// test would then pass with a server distributing presence to everyone
        /// as well.
        /// </remarks>
        [Test]
        public async Task GoingUnavailable_TellsTheDirectedTarget()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var atAlice = PresenceBasket(alice);

            // Bob shows Alice his presence - and only her.
            await bob.SendRawAsync($"<presence to='{alice.BareJid}'/>");

            await WaitFor(() => atAlice.Any(p => p.Type != "unavailable"),
                          "the directed presence at Alice");

            // And now he signs off.
            await bob.SendRawAsync("<presence type='unavailable'/>");

            await WaitFor(() => SignOffs(atAlice) > 0, "the sign-off at Alice");

            Assert.That(Server.SessionOf(bob.FullJid!)!.DirectedPresenceTargets, Is.Empty,
                        "And the promise is thereby settled (section 4.6.1).");

        }

        #endregion

        #region ATornConnection_AlsoTellsTheDirectedTarget()

        /// <summary>
        /// The same, when the connection tears instead of signing off.
        /// </summary>
        /// <remarks>
        /// The more important of the two paths, because it is the more frequent
        /// one: A client mostly disappears without saying goodbye. The sign-off
        /// is then created by the server in their name (section 4.5.2) — and
        /// were it to go to the roster only, the stranger would be left behind
        /// with a presence that never ends.
        ///
        /// Without stream management, because a stream with a promised
        /// resumption is suspended and not signed off (XEP-0198, section 5).
        /// Then there is rightly no sign-off, and the test would check nothing.
        /// </remarks>
        [Test]
        public async Task ATornConnection_AlsoTellsTheDirectedTarget()
        {

            var alice = await ConnectClientAsync("alice");

            Server.AddAccount("bob");

            var bob = CreateClient("bob", streamManagement: false);
            await bob.ConnectAsync();

            var atAlice = PresenceBasket(alice);

            await bob.SendRawAsync($"<presence to='{alice.BareJid}'/>");

            await WaitFor(() => atAlice.Any(p => p.Type != "unavailable"),
                          "the directed presence at Alice");

            Server.KillSessionsOf(bob.BareJid);

            await WaitFor(() => SignOffs(atAlice) > 0,
                          "the sign-off at Alice after the tear");

            Assert.Pass();

        }

        #endregion

        #region AStatusChange_DoesNotEndTheDirectedPromise()

        /// <summary>
        /// A status change in the middle of the session does not end the
        /// promise.
        /// </summary>
        /// <remarks>
        /// Rule 2 holds "after having sent initial presence and before sending
        /// unavailable presence broadcast (i.e., during the user's presence
        /// session)". An ordinary presence — "away", "busy" — does not end this
        /// session; only the sign-off does that.
        ///
        /// In operation this is the most frequent incident of all: A client
        /// sends a new presence at every change, and whoever clears the list
        /// while doing so takes two things from the counterpart — the sign-off
        /// at the end and, since D17, the right to ask anything at all
        /// (section 8.5.3.1). Both in the middle of the conversation and
        /// without anyone noticing.
        ///
        /// The test uncovered a mutation that survived every other one: to
        /// fetch the list at <b>every</b> presence instead of at the sign-off
        /// only. No other test sent an ordinary presence after the directed
        /// one — the order that is the rule in operation.
        /// </remarks>
        [Test]
        public async Task AStatusChange_DoesNotEndTheDirectedPromise()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var atAlice = PresenceBasket(alice);

            await bob.SendRawAsync($"<presence to='{alice.BareJid}'/>");

            await WaitFor(() => Server.SessionOf(bob.FullJid!)!
                                      .HasDirectedPresenceTo(alice.BareJid),
                          "the note of the directed presence");

            // Bob changes to "away" - the presence session carries on.
            await bob.SetPresenceAsync("away", "Lunch break");

            Assert.That(Server.SessionOf(bob.FullJid!)!.HasDirectedPresenceTo(alice.BareJid),
                        Is.True,
                        "A status change does not end the presence session.");

            // And the sign-off still finds Alice.
            await bob.SendRawAsync("<presence type='unavailable'/>");

            await WaitFor(() => SignOffs(atAlice) > 0,
                          "the sign-off at Alice after the status change");

        }

        #endregion

        #region ARosterContact_IsNotToldTwice()

        /// <summary>
        /// Whoever stands in the roster with <c>from</c> gets the sign-off
        /// <b>once</b> — not additionally over the directed path.
        /// </summary>
        /// <remarks>
        /// The RFC expressly limits rule 2 to entities that do <b>not</b> stand
        /// in the roster with <c>from</c> or <c>both</c>, and that is no
        /// formality: A contact gets the sign-off over the ordinary
        /// distribution already. Were it to come twice, a client counting
        /// presence instead of replacing it would get confused.
        ///
        /// The setup is the delicate part: Bob sends the directed presence
        /// <i>before</i> Alice may see his state. Afterwards she stands in both
        /// pots, and only the limitation in the server prevents the second
        /// delivery. The other way round — the roster first — the note would
        /// not be needed at all under rule 1, and the test would check the
        /// wrong case.
        /// </remarks>
        [Test]
        public async Task ARosterContact_IsNotToldTwice()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var atAlice = PresenceBasket(alice);

            // First the directed presence ...
            await bob.SendRawAsync($"<presence to='{alice.BareJid}'/>");

            await WaitFor(() => Server.SessionOf(bob.FullJid!)!
                                      .HasDirectedPresenceTo(alice.BareJid),
                          "the note of the directed presence");

            // ... and only afterwards the roster entry: Alice may see Bob's state.
            SetServerRoster("bob", "alice", "from");

            await bob.SendRawAsync("<presence type='unavailable'/>");

            await WaitFor(() => SignOffs(atAlice) > 0, "the sign-off");

            // A second one could have arrived by now.
            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.That(SignOffs(atAlice), Is.EqualTo(1),
                        "A contact gets the sign-off once, not twice.");

        }

        #endregion

        #region AWithdrawnDirectedPresence_GetsNoSecondUnavailable()

        /// <summary>
        /// The parenthesis of the rule: "if the user has not yet sent directed
        /// unavailable presence to that entity".
        /// </summary>
        /// <remarks>
        /// Whoever has already expressly withdrawn their presence towards a
        /// stranger gets no second sign-off when signing off.
        ///
        /// The parenthesis coincides with the list: A directed sign-off takes
        /// the recipient out (section 4.6.1), and what does not stand in it is
        /// not notified either. Two provisions, one implementation — and hence
        /// one test holding both at once.
        /// </remarks>
        [Test]
        public async Task AWithdrawnDirectedPresence_GetsNoSecondUnavailable()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var atAlice = PresenceBasket(alice);

            await bob.SendRawAsync($"<presence to='{alice.BareJid}'/>");

            await WaitFor(() => Server.SessionOf(bob.FullJid!)!
                                      .HasDirectedPresenceTo(alice.BareJid),
                          "the note of the directed presence");

            // Bob withdraws his presence towards Alice.
            await bob.SendRawAsync($"<presence to='{alice.BareJid}' type='unavailable'/>");

            await WaitFor(() => SignOffs(atAlice) > 0, "the directed sign-off");

            // And signs off entirely afterwards.
            await bob.SendRawAsync("<presence type='unavailable'/>");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.That(SignOffs(atAlice), Is.EqualTo(1),
                        "The withdrawn promise brings no second sign-off.");

        }

        #endregion

        #region WhoLeaves_LosesTheirPlaceInTheList()

        /// <summary>
        /// Section 4.6.1, SHOULD part: Whoever sends the user a sign-off
        /// disappears from their list of directed presence.
        /// </summary>
        /// <remarks>
        /// The two halves of the sentence look alike and mean the opposite. The
        /// MUST concerns one's <b>own</b> withdrawal — "any entity to which the
        /// user sends directed unavailable presence" —, the SHOULD the reverse
        /// direction: "any entity that <i>sends</i> unavailable presence
        /// <i>to</i> the user". The other one leaves, and with that the
        /// temporary relation is over as well.
        ///
        /// That becomes visible only over section 8.5.3.1: As long as Alice
        /// stands in Bob's list she may query his resource. Were she to leave
        /// and come back, she would have kept her right to ask although Bob has
        /// shown her nothing any more — a permission outliving its occasion.
        ///
        /// No roster entry between the two, and that is the core of the setup:
        /// Were Alice Bob's contact, her right to ask would come over the
        /// roster and the list would be immaterial. The case is observable only
        /// without a roster.
        /// </remarks>
        [Test]
        public async Task WhoLeaves_LosesTheirPlaceInTheList()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            // Bob shows Alice his presence - she may ask him now.
            await bob.SendRawAsync($"<presence to='{alice.BareJid}'/>");

            await WaitFor(() => Server.SessionOf(bob.FullJid!)!
                                      .HasDirectedPresenceTo(alice.BareJid),
                          "the note of the directed presence");

            // Alice signs off at Bob's.
            await alice.SendRawAsync($"<presence to='{bob.BareJid}' type='unavailable'/>");

            await WaitFor(() => !Server.SessionOf(bob.FullJid!)!
                                       .HasDirectedPresenceTo(alice.BareJid),
                          "the forgetting of the sender");

            // And with that her right to ask has expired.
            var errors = new ConcurrentQueue<StanzaError>();
            alice.Connection.OnStanzaError += (from, e) => errors.Enqueue(e);

            await alice.SendRawAsync(
                      $"<iq type='get' id='after-the-sign-off' to='{bob.FullJid}'>" +
                      "<ping xmlns='urn:xmpp:ping'/></iq>");

            await WaitFor(() => !errors.IsEmpty,
                          "the turning away of the query after the sign-off");

            errors.TryDequeue(out var refused);

            Assert.That(refused!.Condition, Is.EqualTo("service-unavailable"));

        }

        #endregion

        #region AnAvailablePresence_DoesNotRemoveTheSender()

        /// <summary>
        /// The counter-check: Only a <b>sign-off</b> takes the sender out, no
        /// ordinary presence.
        /// </summary>
        /// <remarks>
        /// Without it the collection would pass even if every incoming presence
        /// cleared the list — and then the promise would be gone at the first
        /// sign of life of the counterpart. Alice shows Bob her presence, and
        /// that is exactly what must not cost her the right to ask.
        /// </remarks>
        [Test]
        public async Task AnAvailablePresence_DoesNotRemoveTheSender()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            await bob.SendRawAsync($"<presence to='{alice.BareJid}'/>");

            await WaitFor(() => Server.SessionOf(bob.FullJid!)!
                                      .HasDirectedPresenceTo(alice.BareJid),
                          "the note of the directed presence");

            // Alice shows Bob her presence - no sign-off.
            await alice.SendRawAsync($"<presence to='{bob.BareJid}'/>");

            await WaitAgainst(() => !Server.SessionOf(bob.FullJid!)!
                                          .HasDirectedPresenceTo(alice.BareJid),
                              "the forgetting at an ordinary presence");

        }

        #endregion

        #region TheTwoRulesCompose_ALeavingTargetIsForgottenThroughRule2()

        /// <summary>
        /// The two rules mesh: Alice's sign-off reaches Bob over rule 2 — and
        /// for that very reason he forgets her.
        /// </summary>
        /// <remarks>
        /// Without a roster Bob hears of Alice's sign-off only if <b>he</b>
        /// stands in <i>her</i> list. That is exactly what section 4.6.3,
        /// rule 2 establishes (D20). And because the sign-off then arrives at
        /// his end, the SHOULD part from 4.6.1 takes hold — without either of
        /// the two rules knowing of the other.
        ///
        /// The test holds this meshing fast, because it breaks easily: Whoever
        /// limits one of the two rules to "contacts only" makes the other one
        /// unreachable, and both would still pass on their own.
        /// </remarks>
        [Test]
        public async Task TheTwoRulesCompose_ALeavingTargetIsForgottenThroughRule2()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            // Both show each other their presence - no roster involved.
            await bob.SendRawAsync($"<presence to='{alice.BareJid}'/>");
            await alice.SendRawAsync($"<presence to='{bob.BareJid}'/>");

            await WaitFor(() => Server.SessionOf(bob.FullJid!)!
                                      .HasDirectedPresenceTo(alice.BareJid) &&
                                Server.SessionOf(alice.FullJid!)!
                                      .HasDirectedPresenceTo(bob.BareJid),
                          "the notes on both sides");

            // Alice signs off entirely - not directed at Bob.
            await alice.SendRawAsync("<presence type='unavailable'/>");

            await WaitFor(() => !Server.SessionOf(bob.FullJid!)!
                                       .HasDirectedPresenceTo(alice.BareJid),
                          "the forgetting over the path from rule 2");

        }

        #endregion

        #region AOneSidedRoster_StillForgetsTheLeavingSender()

        /// <summary>
        /// The forgetting happens over the broadcast path as well — and there
        /// it is observable, although it does not look so at first.
        /// </summary>
        /// <remarks>
        /// The two roster halves are easy to mistake for one another here, and
        /// the mistake leads to exactly the wrong conclusion, that this path
        /// were immaterial:
        ///
        /// <list type="bullet">
        ///   <item>
        ///     That Alice's sign-off reaches Bob over the ordinary distribution
        ///     is decided by <b>Alice's</b> roster: Bob stands in it with
        ///     <c>from</c> — he may see it.
        ///   </item>
        ///   <item>
        ///     Whether Alice may ask Bob anything is decided by <b>Bob's</b>
        ///     roster. That one is empty here, and thereby her right to ask
        ///     hangs on the list of directed presence alone.
        ///   </item>
        /// </list>
        ///
        /// Both together make up the case: The sign-off arrives without Bob
        /// having Alice in the roster — and without the forgetting she would
        /// keep her right to ask beyond her own sign-off. A mutant removing
        /// this line would survive every other test of this collection.
        /// </remarks>
        [Test]
        public async Task AOneSidedRoster_StillForgetsTheLeavingSender()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            // Only Alice's half: Bob may see her state. Bob's roster stays
            // empty.
            SetServerRoster("alice", "bob", "from");

            await bob.SendRawAsync($"<presence to='{alice.BareJid}'/>");

            await WaitFor(() => Server.SessionOf(bob.FullJid!)!
                                      .HasDirectedPresenceTo(alice.BareJid),
                          "the note of the directed presence");

            // Alice's sign-off goes to Bob over the ordinary distribution.
            await alice.SendRawAsync("<presence type='unavailable'/>");

            await WaitFor(() => !Server.SessionOf(bob.FullJid!)!
                                       .HasDirectedPresenceTo(alice.BareJid),
                          "the forgetting over the broadcast path");

        }

        #endregion

        #region AOneSidedRoster_ForgetsAlsoAfterATornConnection()

        /// <summary>
        /// The same, when Alice's connection tears instead of signing off.
        /// </summary>
        /// <remarks>
        /// The second broadcast path: The sign-off is then created by the
        /// server in Alice's name (section 4.5.2). It is a place of its own in
        /// the code and therefore needs a test of its own — without it Alice
        /// would keep her right to ask in precisely the case that is the more
        /// frequent one in operation.
        ///
        /// Without stream management, because a suspended stream is not signed
        /// off (XEP-0198, section 5).
        /// </remarks>
        [Test]
        public async Task AOneSidedRoster_ForgetsAlsoAfterATornConnection()
        {

            Server.AddAccount("alice");

            var alice = CreateClient("alice", streamManagement: false);
            await alice.ConnectAsync();

            var bob = await ConnectClientAsync("bob");

            SetServerRoster("alice", "bob", "from");

            await bob.SendRawAsync($"<presence to='{alice.BareJid}'/>");

            await WaitFor(() => Server.SessionOf(bob.FullJid!)!
                                      .HasDirectedPresenceTo(alice.BareJid),
                          "the note of the directed presence");

            Server.KillSessionsOf(alice.BareJid);

            await WaitFor(() => !Server.SessionOf(bob.FullJid!)!
                                       .HasDirectedPresenceTo(alice.BareJid),
                          "the forgetting after the tear");

        }

        #endregion

        #region AStranger_HearsNothing()

        /// <summary>
        /// The counter-check: Without directed presence and without a roster
        /// entry nobody learns anything.
        /// </summary>
        /// <remarks>
        /// It is the boundary of the whole rule. Without it the collection
        /// would pass even if the server sent the sign-off to every logged-in
        /// session — and that would be no handing over but a presence leak:
        /// Whoever has never asked and never been shown anything has no right
        /// to learn when somebody leaves.
        /// </remarks>
        [Test]
        public async Task AStranger_HearsNothing()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var atAlice = PresenceBasket(alice);

            await bob.SendRawAsync("<presence type='unavailable'/>");

            await WaitAgainst(() => SignOffs(atAlice) > 0,
                              "a sign-off to somebody who knows nothing of Bob");

        }

        #endregion

    }

}
