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
    /// RFC 6121, section 8.5.2.1.3: An IQ request to a bare JID is not
    /// delivered but answered by the server itself.
    /// </summary>
    /// <remarks>
    /// The section says it twice — "the server itself MUST reply on behalf of
    /// the user" <b>and</b> "MUST NOT deliver the IQ stanza to any of the
    /// user's available resources". This doubling has a reason, and it lies in
    /// the nature of IQ.
    ///
    /// IQ is a question-answer pair held together by the <c>id</c>, and every
    /// received request <b>must</b> be answered (RFC 6120, section 8.2.3,
    /// rule 3). Whoever distributes a request to all resources gets a reply
    /// from all of them: The asker holds three replies to one <c>id</c> in
    /// their hand and cannot decide which one counts. With a message a multiple
    /// delivery would be a nuisance at worst; here it breaks the procedure.
    ///
    /// That is precisely what this server did: It handed every IQ request to a
    /// foreign address into the routing, and that distributed to a bare JID to
    /// every session it found.
    /// </remarks>
    [TestFixture]
    public class IqDeliveryRulesTests : AXMPPTests
    {

        #region Helper functions

        private String Bob => $"bob@{Server.Domain}";

        /// <summary>
        /// A ping request (XEP-0199) to an arbitrary address.
        /// </summary>
        private static String Request(String to, String id)
            => $"<iq type='get' id='{id}' to='{to}'>" +
               "<ping xmlns='urn:xmpp:ping'/></iq>";

        /// <summary>
        /// Logs a further client of the same account in and counts what comes
        /// in for it.
        /// </summary>
        private async Task<(XMPPClient, ConcurrentQueue<String>)> ResourceAsync(String localPart,
                                                                               String resource)
        {

            if (Server.GetAccount($"{localPart}@{Server.Domain}") is null)
                Server.AddAccount(localPart);

            var client = CreateClient(localPart);
            var inbox  = new ConcurrentQueue<String>();

            client.Connection.Resource  = resource;
            client.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("urn:xmpp:ping", StringComparison.Ordinal))
                {
                    inbox.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            await client.ConnectAsync();

            return (client, inbox);

        }

        /// <summary>
        /// Collects the stanza errors of a client.
        /// </summary>
        private static ConcurrentQueue<StanzaError> ErrorBasket(XMPPClient client)
        {

            var basket = new ConcurrentQueue<StanzaError>();
            client.Connection.OnStanzaError += (timestamp, sender, from, e, ct) => { basket.Enqueue(e); return Task.CompletedTask; };

            return basket;

        }

        /// <summary>
        /// Counts the incoming IQ stanzas carrying this id.
        /// </summary>
        private static ConcurrentQueue<String> ReplyBasket(XMPPClient client, String id)
        {

            var basket = new ConcurrentQueue<String>();

            client.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("<iq",          StringComparison.Ordinal) &&
                    x.Contains($"id='{id}'",   StringComparison.Ordinal))
                {
                    basket.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            return basket;

        }

        #endregion


        #region AnIqToAnAccount_IsAnsweredOnceAndReachesNoResource()

        /// <summary>
        /// The core: A request to the bare JID reaches <b>no</b> resource and
        /// is answered <b>once</b>.
        /// </summary>
        /// <remarks>
        /// Both halves in one test, because together they make up the
        /// statement. "Reaches no resource" alone would be fulfilled by a
        /// request vanishing silently as well — and that violates the duty to
        /// answer. "Is answered" alone would be fulfilled if all resources
        /// answered on top.
        ///
        /// Two resources and not one: With only one a reply from the server
        /// would look exactly like one from the resource, and the actual damage
        /// — several replies to one <c>id</c> — would be invisible.
        /// </remarks>
        [Test]
        public async Task AnIqToAnAccount_IsAnsweredOnceAndReachesNoResource()
        {

            var alice = await ConnectClientAsync("alice");

            var (mobile,  atTheMobile)  = await ResourceAsync("bob", "Mobile");
            var (desktop, atTheDesktop) = await ResourceAsync("bob", "Desktop");

            var replies = ReplyBasket(alice, "to-the-account");
            var errors  = ErrorBasket(alice);

            await alice.SendRawAsync(Request(Bob, "to-the-account"));

            await WaitFor(() => !errors.IsEmpty, "the reply of the server");

            // Give the two resources time to get the request after all.
            await Task.Delay(TimeSpan.FromSeconds(1));

            errors.TryDequeue(out var refused);

            Assert.Multiple(() =>
            {

                Assert.That(refused!.Condition, Is.EqualTo("service-unavailable"));

                Assert.That(replies, Has.Count.EqualTo(1),
                            "Exactly one reply to one id - otherwise the asker " +
                            "does not know which one counts.");

                Assert.That(atTheMobile,  Is.Empty, "The request must reach no resource.");
                Assert.That(atTheDesktop, Is.Empty, "The second one neither.");

                Assert.That(mobile.FullJid,  Is.Not.EqualTo(desktop.FullJid),
                            "The setup is only good for anything with two different resources.");

            });

        }

        #endregion

        #region AnIqToAnAbsentAccount_IsAnsweredToo()

        /// <summary>
        /// Section 8.5.2.2.3 demands literally the same as 8.5.2.1.3: Whether
        /// somebody is logged in does not change the reply.
        /// </summary>
        /// <remarks>
        /// The difference to the message is remarkable: There the offline store
        /// hangs on precisely this question. With IQ there is nothing to store —
        /// a question whose answer would arrive only tomorrow the asker has
        /// long given up on. That is why the delivery path does not even ask
        /// whether a resource is there, and this test holds fast that that is
        /// right.
        /// </remarks>
        [Test]
        public async Task AnIqToAnAbsentAccount_IsAnsweredToo()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            var errors = ErrorBasket(alice);

            await alice.SendRawAsync(Request(Bob, "to-the-absent-one"));

            await WaitFor(() => !errors.IsEmpty, "the reply of the server");

            errors.TryDequeue(out var refused);

            Assert.That(refused!.Condition, Is.EqualTo("service-unavailable"));

        }

        #endregion

        #region AnIqToAnUnknownAccount_IsAnswered()

        /// <summary>
        /// Section 8.5.1: For an account that does not exist an IQ request
        /// <b>must</b> be answered — unlike a message.
        /// </summary>
        /// <remarks>
        /// The difference is no oversight of the RFC but the consequence of the
        /// duty to answer: With a message the server may keep silent and
        /// thereby does not give away which accounts exist; with a request it
        /// has to answer, and the answer is the same as for an existing account
        /// without a reachable resource. That is precisely why it is harmless —
        /// <c>&lt;service-unavailable/&gt;</c> does not tell the two cases
        /// apart.
        ///
        /// That is the second part of the statement and the reason why this
        /// test stands next to the previous one: Were the replies different,
        /// the server would have given away a directory of its accounts.
        /// </remarks>
        [Test]
        public async Task AnIqToAnUnknownAccount_IsAnswered()
        {

            var alice = await ConnectClientAsync("alice");

            var errors = ErrorBasket(alice);

            await alice.SendRawAsync(Request($"doesnotexist@{Server.Domain}", "to-nobody"));

            await WaitFor(() => !errors.IsEmpty, "the reply of the server");

            errors.TryDequeue(out var refused);

            Assert.That(refused!.Condition, Is.EqualTo("service-unavailable"),
                        "An unknown account must give the same reply as a known " +
                        "one without a reachable resource.");

        }

        #endregion

        #region AnIqToAResource_IsDelivered()

        /// <summary>
        /// The counter-check: addressed to a matching resource the request is
        /// delivered (section 8.5.3.1).
        /// </summary>
        /// <remarks>
        /// Without it the collection would pass even if the server turned away
        /// every IQ request to another user — and thereby every understanding
        /// between two clients would be impossible. A ping between two human
        /// beings, a version query, a file transfer: everything goes to a full
        /// JID.
        ///
        /// The two are contacts, and that is no accessory: Section 8.5.3.1 lets
        /// the request through only if the asker may see the presence of the
        /// recipient. The counter-check to this counter-check stands further
        /// below.
        /// </remarks>
        [Test]
        public async Task AnIqToAResource_IsDelivered()
        {

            var alice = await ConnectClientAsync("alice");
            var (bob, atBob) = await ResourceAsync("bob", "Mobile");

            MakeContacts("alice", "bob");

            await alice.SendRawAsync(Request(bob.FullJid!.ToString(), "to-the-resource"));

            await WaitFor(() => !atBob.IsEmpty, "the request at the resource");

            Assert.Pass();

        }

        #endregion

        #region AnIqFromAStranger_IsRefusedEvenThoughTheResourceExists()

        /// <summary>
        /// Section 8.5.3.1: Whoever may not see the presence of the recipient
        /// does not get the request delivered — even if the resource is there.
        /// </summary>
        /// <remarks>
        /// The reason stands in section 11 and is finer than it looks at first:
        /// <b>The reply alone is already a piece of information.</b> Whoever
        /// asks a full JID and gets a result knows that this very resource is
        /// logged in at this moment. Without this check the presence of a human
        /// being could be queried without ever having asked them for permission
        /// — and resource names could be tried out one by one until one
        /// answers.
        ///
        /// What is checked is therefore also that the reply is <b>the same</b>
        /// as for a resource that does not exist. Were the two different, the
        /// check would be without effect: The asker would read out of the kind
        /// of refusal what it is meant to keep from them.
        /// </remarks>
        [Test]
        public async Task AnIqFromAStranger_IsRefusedEvenThoughTheResourceExists()
        {

            var alice = await ConnectClientAsync("alice");
            var (bob, atBob) = await ResourceAsync("bob", "Mobile");

            var errors = ErrorBasket(alice);

            await alice.SendRawAsync(Request(bob.FullJid!.ToString(), "from-a-stranger"));
            await WaitFor(() => !errors.IsEmpty, "the refusal");

            errors.TryDequeue(out var toTheExistingOne);

            // And the same question to a resource that does not exist.
            await alice.SendRawAsync(Request($"{Bob}/gone-already", "to-the-invented-one"));
            await WaitFor(() => !errors.IsEmpty, "the refusal for the invented resource");

            errors.TryDequeue(out var toTheInventedOne);

            Assert.Multiple(() =>
            {

                Assert.That(toTheExistingOne!.Condition, Is.EqualTo("service-unavailable"));

                Assert.That(toTheInventedOne!.Condition, Is.EqualTo(toTheExistingOne.Condition),
                            "An existing and an invented resource must give the same " +
                            "reply - otherwise the refusal betrays what it keeps quiet.");

                Assert.That(atBob, Is.Empty,
                            "The request must not reach the resource.");

            });

        }

        #endregion

        #region TheRosterHalfThatCountsIsTheRecipients()

        /// <summary>
        /// What is asked is the roster of the <b>recipient</b> for <c>from</c>
        /// or <c>both</c> — "that one may see me".
        /// </summary>
        /// <remarks>
        /// The direction is easy to mistake, and <c>both</c> covers the mistake
        /// up entirely: There both halves hold, and an implementation reading
        /// the wrong one does not stand out. That is why a one-sided state
        /// stands here.
        ///
        /// <c>to</c> in Bob's roster means: <b>Bob</b> sees Alice's presence,
        /// Alice however not Bob's. Whoever reads this half gives the
        /// information to exactly the wrong side — to everyone the recipient
        /// watches instead of to everyone who may watch them. That is no half
        /// check but the inversion of the intended one.
        /// </remarks>
        [Test]
        public async Task TheRosterHalfThatCountsIsTheRecipients()
        {

            var alice = await ConnectClientAsync("alice");
            var (bob, atBob) = await ResourceAsync("bob", "Mobile");

            // Bob sees Alice - Alice does not see Bob.
            SetServerRoster("bob", "alice", "to");

            var errors = ErrorBasket(alice);

            await alice.SendRawAsync(Request(bob.FullJid!.ToString(), "wrong-half"));

            await WaitFor(() => !errors.IsEmpty,
                          "the turning away despite the roster entry");

            errors.TryDequeue(out var refused);

            // And now the half that counts.
            SetServerRoster("bob", "alice", "from");

            await alice.SendRawAsync(Request(bob.FullJid!.ToString(), "right-half"));

            await WaitFor(() => !atBob.IsEmpty,
                          "the request after the right roster state");

            Assert.That(refused!.Condition, Is.EqualTo("service-unavailable"));

        }

        #endregion

        #region DirectedPresence_OpensTheDoorForAStranger()

        /// <summary>
        /// The second path out of section 8.5.3.1: directed presence
        /// (section 4.6) instead of a roster entry.
        /// </summary>
        /// <remarks>
        /// Without this path the check would be too strict, and that for the
        /// most frequent case of all: A conversation with somebody who is not
        /// in the roster begins with showing them one's presence
        /// (section 5.1). Whoever has shown their presence of their own accord
        /// can lose nothing any more through a reply — the asker knows already
        /// that the resource is there.
        ///
        /// Both halves stand in the test: first the turning away without
        /// directed presence, then the delivery with it. Without the first the
        /// second would prove nothing, because the request would arrive with
        /// the check switched off as well.
        ///
        /// Bob writes to Alice's <b>full JID</b>, and that is the usual case: A
        /// conversation begins with the resource one has just heard from. What
        /// has to be noted down is the bare JID nevertheless — otherwise the
        /// promise would count only for this one device, and Alice's request
        /// from the same resource would be sheer coincidence.
        /// </remarks>
        [Test]
        public async Task DirectedPresence_OpensTheDoorForAStranger()
        {

            var alice = await ConnectClientAsync("alice");
            var (bob, atBob) = await ResourceAsync("bob", "Mobile");

            var errors = ErrorBasket(alice);

            await alice.SendRawAsync(Request(bob.FullJid!.ToString(), "before"));
            await WaitFor(() => !errors.IsEmpty, "the turning away without directed presence");

            // Bob shows Alice his presence - to her full JID.
            await bob.SendRawAsync($"<presence to='{alice.FullJid}'/>");

            await WaitFor(() => Server.SessionOf(bob.FullJid!.ToString())!
                                      .HasDirectedPresenceTo(alice.BareJid.ToString()),
                          "the note of the directed presence");

            await alice.SendRawAsync(Request(bob.FullJid!.ToString(), "after"));

            await WaitFor(() => !atBob.IsEmpty,
                          "the request after the directed presence");

        }

        #endregion

        #region DirectedUnavailablePresence_ClosesTheDoorAgain()

        /// <summary>
        /// Section 4.6.1: "The server MUST remove from the directed presence
        /// list ... any entity to which the user sends directed unavailable
        /// presence."
        /// </summary>
        /// <remarks>
        /// A MUST, and the reason is exactly the check from 8.5.3.1: Were the
        /// note to stay, the stranger could go on asking after the user has
        /// expressly revoked their promise. A permission one cannot take back
        /// is none.
        /// </remarks>
        [Test]
        public async Task DirectedUnavailablePresence_ClosesTheDoorAgain()
        {

            var alice = await ConnectClientAsync("alice");
            var (bob, atBob) = await ResourceAsync("bob", "Mobile");

            await bob.SendRawAsync($"<presence to='{alice.BareJid}'/>");
            await WaitFor(() => Server.SessionOf(bob.FullJid!.ToString())!
                                      .HasDirectedPresenceTo(alice.BareJid.ToString()),
                          "the note of the directed presence");

            await alice.SendRawAsync(Request(bob.FullJid!.ToString(), "during"));
            await WaitFor(() => !atBob.IsEmpty, "the request at an open door");

            // Bob withdraws his presence towards Alice.
            await bob.SendRawAsync($"<presence to='{alice.BareJid}' type='unavailable'/>");

            await WaitFor(() => !Server.SessionOf(bob.FullJid!.ToString())!
                                       .HasDirectedPresenceTo(alice.BareJid.ToString()),
                          "the taking back of the note");

            var errors = ErrorBasket(alice);

            await alice.SendRawAsync(Request(bob.FullJid!.ToString(), "afterwards"));

            await WaitFor(() => !errors.IsEmpty,
                          "the turning away after the revocation");

        }

        #endregion

        #region GoingUnavailable_ClearsTheDirectedPresenceList()

        /// <summary>
        /// Section 4.6.1: "...then clearing the list when the user goes
        /// offline".
        /// </summary>
        /// <remarks>
        /// Directed presence is a promise for the duration of the presence.
        /// Were it to stay beyond the sign-off, a stranger could go on querying
        /// a signed-off resource — and conclude from the reply that it is still
        /// connected although it has signed off. That is precisely the
        /// information section 8.5.3.1 is meant to prevent.
        /// </remarks>
        [Test]
        public async Task GoingUnavailable_ClearsTheDirectedPresenceList()
        {

            var alice = await ConnectClientAsync("alice");
            var (bob, _) = await ResourceAsync("bob", "Mobile");

            await bob.SendRawAsync($"<presence to='{alice.BareJid}'/>");
            await WaitFor(() => Server.SessionOf(bob.FullJid!.ToString())!
                                      .HasDirectedPresenceTo(alice.BareJid.ToString()),
                          "the note of the directed presence");

            // Not directed at Alice, but the sign-off of one's own.
            await bob.SendRawAsync("<presence type='unavailable'/>");

            await WaitFor(() => Server.SessionOf(bob.FullJid!.ToString())?.IsAvailable == false,
                          "the sign-off at the server");

            Assert.That(Server.SessionOf(bob.FullJid!.ToString())!.DirectedPresenceTargets,
                        Is.Empty,
                        "The sign-off takes every directed presence back with it.");

        }

        #endregion

        #region AnIqToAVanishedResource_IsAnswered()

        /// <summary>
        /// Section 8.5.3.2.3: No matching resource, hence
        /// <c>&lt;service-unavailable/&gt;</c> — here without an exception for
        /// the kind.
        /// </summary>
        [Test]
        public async Task AnIqToAVanishedResource_IsAnswered()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            var errors = ErrorBasket(alice);

            await alice.SendRawAsync(Request($"{Bob}/gone-already", "to-the-vanished-one"));

            await WaitFor(() => !errors.IsEmpty, "the reply of the server");

            errors.TryDequeue(out var refused);

            Assert.That(refused!.Condition, Is.EqualTo("service-unavailable"));

        }

        #endregion

        #region AResultToAVanishedResource_IsNotAnswered()

        /// <summary>
        /// RFC 6120, section 8.2.3, rule 4: A reply is never answered.
        /// </summary>
        /// <remarks>
        /// Here two provisions stand against each other, and that is the reason
        /// for this test. Section 8.5.3.2.3 demands an error for "an IQ stanza"
        /// without a matching resource and does not tell the kinds apart;
        /// rule 4 forbids answering a reply. Rule 4 wins: An error on a
        /// <c>result</c> would go to somebody who has not asked anything, and
        /// would carry the <c>id</c> of a question they answered themselves.
        /// They can do nothing with it — and if they answered the error in
        /// turn, it would be a loop.
        ///
        /// Both kinds of reply are checked, because the ban is obvious only for
        /// <c>error</c>.
        /// </remarks>
        [Test]
        public async Task AResultToAVanishedResource_IsNotAnswered()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            var errors  = ErrorBasket(alice);
            var replies = ReplyBasket(alice, "no-question");

            await alice.SendRawAsync(
                      $"<iq type='result' id='no-question' to='{Bob}/gone-already'/>");

            await alice.SendRawAsync(
                      $"<iq type='error' id='also-none' to='{Bob}/gone-already'>" +
                      "<error type='cancel'>" +
                      "<service-unavailable xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                      "</error></iq>");

            await WaitAgainst(() => !errors.IsEmpty || !replies.IsEmpty,
                              "a reply to a reply");

        }

        #endregion

        #region AResultToAnAccount_ReachesNoResource()

        /// <summary>
        /// A reply to the bare JID belongs to nobody: It is neither distributed
        /// nor answered.
        /// </summary>
        /// <remarks>
        /// A reply belongs to exactly the resource that asked — and a bare JID
        /// names none. Distributing it to all of them would be worse than
        /// discarding it: Every resource would see a reply to a question it
        /// never put, and might consider its own open <c>id</c> settled.
        /// </remarks>
        [Test]
        public async Task AResultToAnAccount_ReachesNoResource()
        {

            var alice = await ConnectClientAsync("alice");

            var (mobile,  atTheMobile)  = await ResourceAsync("bob", "Mobile");
            var (desktop, atTheDesktop) = await ResourceAsync("bob", "Desktop");

            var errors = ErrorBasket(alice);

            await alice.SendRawAsync(
                      $"<iq type='result' id='to-everyone' to='{Bob}'>" +
                      "<ping xmlns='urn:xmpp:ping'/></iq>");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(atTheMobile,  Is.Empty);
                Assert.That(atTheDesktop, Is.Empty);
                Assert.That(errors,       Is.Empty, "And it is not answered either.");
            });

        }

        #endregion

        #region TheAnswerCarriesTheRequestedAddressAsSender()

        /// <summary>
        /// The reply comes from the address that was asked and carries the
        /// <c>id</c> of the question.
        /// </summary>
        /// <remarks>
        /// Both are the precondition for the asker being able to relate it at
        /// all. The <c>id</c> holds the pair together (RFC 6120,
        /// section 8.2.3, rule 3: "The response MUST preserve the 'id'
        /// attribute of the request"), and the sender answers the question
        /// "what has become of my request to Bob". Were it to come from the
        /// server, it would be the reply to a different question — and a client
        /// relating replies to the one asked would find no relation.
        /// </remarks>
        [Test]
        public async Task TheAnswerCarriesTheRequestedAddressAsSender()
        {

            var alice = await ConnectClientAsync("alice");
            Server.AddAccount("bob");

            var rawFrames = new ConcurrentQueue<String>();

            alice.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("with-sender", StringComparison.Ordinal))
                {
                    rawFrames.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            await alice.SendRawAsync(Request(Bob, "with-sender"));

            await WaitFor(() => !rawFrames.IsEmpty, "the reply on the wire");

            rawFrames.TryDequeue(out var stanza);

            Assert.Multiple(() =>
            {

                Assert.That(stanza, Does.Contain($"from='{Bob}'"),
                            "The reply comes from the address that was asked.");

                Assert.That(stanza, Does.Contain($"to='{alice.FullJid}'"),
                            "And goes to the resource that asked.");

                Assert.That(stanza, Does.Contain("id='with-sender'"));

            });

        }

        #endregion

    }

}
