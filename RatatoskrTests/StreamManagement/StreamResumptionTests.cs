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
    /// XEP-0198, section 5: the server holds a torn stream ready instead of
    /// burying it at once.
    /// </summary>
    /// <remarks>
    /// What is checked here is the half that gets by without a returner: that
    /// an <c>&lt;enable resume='true'/&gt;</c> is answered, that a torn
    /// connection does <b>not</b> end the session, and that it does end after
    /// the deadline has run out. The <c>&lt;resume/&gt;</c> itself sits in the
    /// setup phase of the client - before the resource binding, after the login
    /// - and is only checkable once the client sends it.
    ///
    /// The difference is visible to the contacts and therefore delicate: until
    /// now the server produced a sign-off in the client's name at once upon the
    /// tear (RFC 6121, section 4.5.2). Whoever may come back must not be signed
    /// off - otherwise the contacts would see an out and an in where in truth
    /// nothing happened. But if the returner fails to come, the sign-off has to
    /// follow, otherwise the contacts keep the resource as online for ever.
    /// </remarks>
    [TestFixture]
    public class StreamResumptionTests : AXMPPTests
    {

        #region Data

        /// <summary>
        /// Short enough for the expiry to be waited out in the test, long
        /// enough that it does not fall in the middle of the setup.
        /// </summary>
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

        #endregion

        #region Helper functions

        /// <summary>
        /// Connects a client without stream management of its own and
        /// negotiates it by hand afterwards - with or without resumption.
        /// </summary>
        /// <remarks>
        /// By hand, because the client does not yet ask for the resumption
        /// itself. As soon as it does, this can disappear.
        /// </remarks>
        private async Task<(XMPPClient Client, XMPPSession Session)> WithStreamManagementAsync(
                                                                        Boolean resume,
                                                                        String  localPart = "alice")
        {

            var client = await ConnectClientAsync(localPart,
                                                  streamManagement:     false,
                                                  maxReconnectAttempts: 0);

            await client.SendRawAsync(
                      $"<enable xmlns='urn:xmpp:sm:3'{(resume ? " resume='true'" : "")}/>");

            var session = Server.SessionOf(client.FullJid)!;

            await WaitFor(() => session.StreamManagementEnabled,
                          "the negotiated stream management");

            return (client, session);

        }

        /// <summary>The answer of the server to an <c>&lt;enable/&gt;</c>.</summary>
        private static String? EnabledFrame(XMPPSession session)
            => session.Sent.LastOrDefault(f => f.StartsWith("<enabled", StringComparison.Ordinal));

        /// <summary>
        /// The last refusal the server has sent over one of its open sessions.
        /// </summary>
        /// <remarks>
        /// Over all sessions and not over a particular one, because the refusal
        /// arrives on the <b>new</b> stream - the old one is precisely the
        /// reason for the request. The torn session is no longer open and
        /// therefore no longer stands in <c>Sessions</c>.
        /// </remarks>
        private String? Refusal()
            => Server.Sessions
                     .SelectMany(s => s.Sent)
                     .LastOrDefault(f => f.StartsWith("<failed", StringComparison.Ordinal));

        /// <summary>
        /// Tears the session down and waits until the server has laid it aside
        /// as resumable.
        /// </summary>
        /// <remarks>
        /// Without this waiting there is a race in every test that expects a
        /// successful resumption: the client comes back after its reconnect
        /// delay, the server lays the session aside at its own pace. If the
        /// client comes first, its <c>&lt;resume/&gt;</c> finds nothing there
        /// and binds anew — which is right, only the test then checks something
        /// other than what it is meant to.
        ///
        /// Noticed as a rare failure in the full run, never on its own. The
        /// message then read "the stream was negotiated afresh" — which was
        /// true and said nothing about the code being checked. With this
        /// precondition it fails instead, and with the reason.
        /// </remarks>
        private async Task KillAndAwaitParked(XMPPSession session)
        {

            session.Kill();

            await WaitFor(() => Server.ResumableStreamCount > 0,
                          "the session laid aside by the server");

        }

        #endregion


        #region EnableWithResume_IsAnsweredWithAnUnguessableId()

        /// <summary>
        /// If the client asks after resumption, it gets an id.
        /// </summary>
        /// <remarks>
        /// XEP-0198, section 5.1: the id is the only secret that identifies the
        /// returner. Whoever knows it can take the stream over - which is why
        /// it must not be derivable from anything public.
        ///
        /// The earlier version sent <c>id='sm-{connection number}'</c>. That is
        /// a small number anyone reading along can count out, and with the
        /// resumption it would have become a way in. Without the resumption the
        /// id was without consequence - which is exactly why it never showed
        /// up.
        /// </remarks>
        [Test]
        public async Task EnableWithResume_IsAnsweredWithAnUnguessableId()
        {

            var (_, first)  = await WithStreamManagementAsync(resume: true, localPart: "alice");
            var (_, second) = await WithStreamManagementAsync(resume: true, localPart: "bob");

            Assert.Multiple(() =>
            {

                Assert.That(EnabledFrame(first), Does.Contain("resume='true'"),
                            "The server has not promised the resumption.");

                Assert.That(first.ResumptionId, Is.Not.Null.And.Length.GreaterThanOrEqualTo(22),
                            "Too short not to be guessed.");

                Assert.That(EnabledFrame(first), Does.Contain($"id='{first.ResumptionId}'"));

                // Exactly the earlier form.
                Assert.That(first.ResumptionId, Is.Not.EqualTo($"sm-{first.ConnectionNumber}"));

                // And two sessions get different ids.
                //
                // A check stood here at first that the id must not contain the
                // connection number as a substring. That says nothing: a random
                // id of 22 characters contains almost every single digit
                // somewhere. Run on its own the test passed, in the full run -
                // with a different connection number - it failed.
                Assert.That(first.ResumptionId, Is.Not.EqualTo(second.ResumptionId));

            });

        }

        #endregion

        #region EnableWithoutResume_PromisesNothing()

        /// <summary>
        /// Without asking no promise - and thereby nothing the server would
        /// have to keep.
        /// </summary>
        [Test]
        public async Task EnableWithoutResume_PromisesNothing()
        {

            var (_, session) = await WithStreamManagementAsync(resume: false);

            Assert.Multiple(() =>
            {
                Assert.That(EnabledFrame(session), Does.Not.Contain("resume='true'"));
                Assert.That(session.ResumptionId,  Is.Null);
            });

        }

        #endregion

        #region ADroppedResumableStream_DoesNotLogTheUserOut()

        /// <summary>
        /// If the connection of a resumable stream tears, the resource stays
        /// available to its contacts.
        /// </summary>
        /// <remarks>
        /// That is the point of the whole exercise. Without it the server
        /// produces a sign-off at once upon the tear, and a client that comes
        /// back two seconds later has shown its contacts a disappearance in the
        /// meantime that never took place.
        /// </remarks>
        [Test]
        public async Task ADroppedResumableStream_DoesNotLogTheUserOut()
        {

            MakeContacts("alice", "bob");

            var (_, aliceSession) = await WithStreamManagementAsync(resume: true);
            var bob                   = await ConnectClientAsync("bob", createAccount: false);

            var signOffs = 0;
            bob.OnPresenceChanged += (from, type) =>
            {
                if (type == "unavailable" && from.StartsWith($"alice@{Server.Domain}", StringComparison.Ordinal))
                    Interlocked.Increment(ref signOffs);
            };

            // The client sends the first presence during the setup already;
            // without it the resource does not count as available (RFC 6121,
            // 4.2.1) and there would be nothing to sign off either.
            await WaitFor(() => aliceSession.IsAvailable, "Alice to be available");

            aliceSession.Kill();

            await WaitAgainst(() => signOffs > 0,
                              "a sign-off from Alice, although her stream is being kept");

            Assert.That(Server.ResumableStreamCount, Is.EqualTo(1),
                        "The stream was not kept.");

        }

        #endregion

        #region AKeptStreamExpires_AndThenTheContactsSeeIt()

        /// <summary>
        /// If nobody comes back, the session ends after all - and the sign-off
        /// is made up for.
        /// </summary>
        /// <remarks>
        /// The counter-check to the previous test, and without it the gain
        /// would be none: a deferred sign-off that never comes is worse than
        /// one that is too early. The contacts would then keep the resource as
        /// online for ever, and no fault would ever be visible.
        /// </remarks>
        [Test]
        public async Task AKeptStreamExpires_AndThenTheContactsSeeIt()
        {

            Server.ResumptionTimeout = Deadline;

            MakeContacts("alice", "bob");

            var (_, aliceSession) = await WithStreamManagementAsync(resume: true);
            var bob                   = await ConnectClientAsync("bob", createAccount: false);

            var signOffs = 0;
            bob.OnPresenceChanged += (from, type) =>
            {
                if (type == "unavailable" && from.StartsWith($"alice@{Server.Domain}", StringComparison.Ordinal))
                    Interlocked.Increment(ref signOffs);
            };

            // The client sends the first presence during the setup already;
            // without it the resource does not count as available (RFC 6121,
            // 4.2.1) and there would be nothing to sign off either.
            await WaitFor(() => aliceSession.IsAvailable, "Alice to be available");

            aliceSession.Kill();

            await WaitFor(() => signOffs > 0,
                          "the sign-off made up for after the deadline ran out",
                          Deadline + TimeSpan.FromSeconds(10));

            Assert.Multiple(() =>
            {

                Assert.That(signOffs, Is.EqualTo(1),
                            "The sign-off came more than once.");

                Assert.That(Server.ResumableStreamCount, Is.Zero,
                            "The expired stream is still lying about.");

            });

        }

        #endregion

        #region AnInvisibleClient_KeepsItsResumableStream()

        /// <summary>
        /// The resumption hangs on the stream, not on the presence: a client
        /// that has made itself invisible keeps its kept stream too.
        /// </summary>
        /// <remarks>
        /// Two things were confused here. The resumption is promised with an
        /// <c>&lt;enabled resume='true'/&gt;</c> and thereby belongs to the
        /// stream; the presence tells the contacts something about the person
        /// in front of it. The laying aside demanded an <i>available</i>
        /// session all the same — and whoever signed off without ending the
        /// connection lost the promise in silence: their
        /// <c>&lt;resume/&gt;</c> got a <c>&lt;failed/&gt;</c>, and everything
        /// unacknowledged was gone.
        ///
        /// That did not show up at this case but at a test that occasionally
        /// ran into the timeout: it tore the connection down as soon as the
        /// resumption was promised — and in the setup of the client that is
        /// <i>before</i> its first presence. On a quiet machine the presence
        /// came in time, under load not always.
        /// </remarks>
        [Test]
        public async Task AnInvisibleClient_KeepsItsResumableStream()
        {

            var alice   = await ConnectClientAsync("alice", maxReconnectAttempts: 0);
            var session = Server.SessionOf(alice.FullJid!)!;

            await WaitFor(() => alice.StreamManagement?.CanResume == true,
                          "a promised resumption");

            // Invisible but connected - the stream carries on.
            await alice.SendRawAsync("<presence type='unavailable'/>");

            await WaitFor(() => !session.IsAvailable,
                          "the signed-off but open session");

            session.Kill();

            await WaitFor(() => Server.ResumableStreamCount == 1,
                          "the kept stream");

            Assert.That(session.ResumptionId, Is.Not.Null,
                        "The id belongs to the stream and outlives the sign-off.");

        }

        #endregion

        #region AStreamWithoutResume_IsAnnouncedAtOnce()

        /// <summary>
        /// Without a promised resumption the behaviour stays as it was.
        /// </summary>
        /// <remarks>
        /// The test records that the deferral hangs on the promise and not on
        /// stream management as such. Without it the sign-off could be deferred
        /// for everyone by mistake, and the delay would show up only in
        /// service.
        /// </remarks>
        [Test]
        public async Task AStreamWithoutResume_IsAnnouncedAtOnce()
        {

            MakeContacts("alice", "bob");

            var (_, aliceSession) = await WithStreamManagementAsync(resume: false);
            var bob                   = await ConnectClientAsync("bob", createAccount: false);

            var signOffs = 0;
            bob.OnPresenceChanged += (from, type) =>
            {
                if (type == "unavailable" && from.StartsWith($"alice@{Server.Domain}", StringComparison.Ordinal))
                    Interlocked.Increment(ref signOffs);
            };

            // The client sends the first presence during the setup already;
            // without it the resource does not count as available (RFC 6121,
            // 4.2.1) and there would be nothing to sign off either.
            await WaitFor(() => aliceSession.IsAvailable, "Alice to be available");

            aliceSession.Kill();

            await WaitFor(() => signOffs > 0, "the immediate sign-off");

            Assert.That(Server.ResumableStreamCount, Is.Zero);

        }

        #endregion

        #region AcknowledgedStanzas_LeaveTheBuffer()

        /// <summary>
        /// What the client has acknowledged the server does not keep any
        /// longer.
        /// </summary>
        /// <remarks>
        /// The buffer carries the stanzas that would have to be sent on after a
        /// resumption (XEP-0198, section 5). It may hold only what has not
        /// arrived yet - otherwise it would grow without end, and the returner
        /// would get everything twice that it has long had.
        /// </remarks>
        [Test]
        public async Task AcknowledgedStanzas_LeaveTheBuffer()
        {

            MakeContacts("alice", "bob");

            var (_, aliceSession) = await WithStreamManagementAsync(resume: true);
            var bob               = await ConnectClientAsync("bob", createAccount: false);

            for (var i = 0; i < 3; i++)
                await bob.SendMessageAsync($"alice@{Server.Domain}", $"Message {i}");

            await WaitFor(() => aliceSession.StanzasSentToClient >= 3,
                          "three delivered messages");

            Assert.That(aliceSession.UnacknowledgedToClient, Is.GreaterThanOrEqualTo(3),
                        "Nothing buffered - then there would be nothing to send on after a resumption.");

            // Refer to the state *at the moment of the enquiry*, not to an
            // empty buffer: traffic carries on. Bob's client acknowledges the
            // three messages with XEP-0184 delivery receipts, and those are
            // stanzas to Alice in their turn - if one of them arrives between
            // the <r/> and the <a/>, the buffer is never empty.
            //
            // "The buffer is empty" was wrong in about every third full run,
            // never when run on its own. What the test means is: what was
            // acknowledged is no longer in there.
            var stateAtTheEnquiry = aliceSession.StanzasSentToClient;

            await aliceSession.RequestAckAsync();

            await WaitFor(() => aliceSession.LastAckFromClient >= stateAtTheEnquiry,
                          "the <a/> of the client about the state at the moment of the enquiry");

            Assert.That(aliceSession.PendingToClient.Any(e => e.Seq <= stateAtTheEnquiry), Is.False,
                        "Acknowledged stanzas are still lying in the buffer.");

        }

        #endregion

        #region TheClientResumesInsteadOfBindingAnew()

        /// <summary>
        /// After a tear the client resumes the stream instead of binding a new
        /// resource.
        /// </summary>
        /// <remarks>
        /// The full JID is the visible proof. With an ordinary fresh setup the
        /// server hands out a new resource, and to the contacts the returner is
        /// someone other than the one who disappeared - running conversations
        /// pointing at the full address run into nothing. After a resumption it
        /// is the same address, because it is the same stream.
        /// </remarks>
        [Test]
        public async Task TheClientResumesInsteadOfBindingAnew()
        {

            var alice    = await ConnectClientAsync(reconnectDelay: TimeSpan.FromMilliseconds(200));
            var before   = alice.FullJid;
            var session  = Server.SessionOf(before!)!;

            await WaitFor(() => alice.StreamManagement?.CanResume == true,
                          "a promised resumption");

            var resumeId = alice.StreamManagement!.ResumeId;

            // Wait for the *finished* setup, not for the picking up of the
            // stream: the server clears it out of its list in the middle of the
            // client's setup phase, and whoever waits only for that checks the
            // client in a state it is about to leave again. That is exactly
            // what the mutation creating the manager afresh at every setup got
            // past at first.
            var reconnected = 0;
            alice.OnStateChanged += (_, newState) =>
            {
                if (newState == ConnectionState.Connected)
                    Interlocked.Increment(ref reconnected);
            };

            await KillAndAwaitParked(session);

            await WaitFor(() => reconnected > 0,
                          "the resumed session",
                          TimeSpan.FromSeconds(20));

            Assert.Multiple(() =>
            {

                Assert.That(alice.FullJid, Is.EqualTo(before),
                            "The client bound a new resource instead of resuming.");

                // The full JID alone does not suffice as proof: the resource is
                // fixed per process, a new bind would give the same address. An
                // unchanged id exists only without a new <enabled/>.
                Assert.That(alice.StreamManagement.ResumeId, Is.EqualTo(resumeId),
                            "The stream was negotiated afresh instead of resumed.");

                Assert.That(Server.SessionOf(before!), Is.Not.Null);

            });

        }

        #endregion

        #region WhatArrivedDuringTheOutage_IsDeliveredAfterwards()

        /// <summary>
        /// What was delivered during the tear comes on after the resumption.
        /// </summary>
        /// <remarks>
        /// The real gain of the whole extension, and the reason for the buffer
        /// from R1. Without it the resumption would be mere cosmetics on the
        /// full JID: the messages the server wrote into a dead connection would
        /// be gone, and nobody would learn of it - neither the sender nor the
        /// recipient.
        /// </remarks>
        [Test]
        public async Task WhatArrivedDuringTheOutage_IsDeliveredAfterwards()
        {

            MakeContacts("alice", "bob");

            var alice   = await ConnectClientAsync(reconnectDelay: TimeSpan.FromMilliseconds(500));
            var bob     = await ConnectClientAsync("bob", createAccount: false);
            var session = Server.SessionOf(alice.FullJid!)!;

            await WaitFor(() => alice.StreamManagement?.CanResume == true,
                          "a promised resumption");

            var arrived = new List<String>();
            alice.OnMessage += m => { lock (arrived) arrived.Add(m.Body); };

            // The connection is dead, the server does not know it yet: what it
            // sends now goes into the buffer.
            session.Kill();

            await bob.SendMessageAsync($"alice@{Server.Domain}", "Sent in the dark");

            await WaitFor(() => { lock (arrived) return arrived.Contains("Sent in the dark"); },
                          "the message sent on afterwards",
                          TimeSpan.FromSeconds(20));

        }

        #endregion

        #region AStolenId_DoesNotHandOverTheStream()

        /// <summary>
        /// The id alone does not suffice - the returner has to be logged in on
        /// the same account.
        /// </summary>
        /// <remarks>
        /// The gravest spot of the whole extension. The id travels over the
        /// wire; whoever gets hold of it would have, without this check, a
        /// foreign session along with full JID, roster and running
        /// conversations - without ever having seen the password.
        ///
        /// It is thereby no proof of identity but only a selection:
        /// <i>which</i> of the kept streams of this account is meant. The
        /// client identified itself beforehand, over SASL.
        /// </remarks>
        [Test]
        public async Task AStolenId_DoesNotHandOverTheStream()
        {

            var alice   = await ConnectClientAsync("alice", maxReconnectAttempts: 0);
            var session = Server.SessionOf(alice.FullJid!)!;

            await WaitFor(() => alice.StreamManagement?.CanResume == true,
                          "a promised resumption");

            var aliceResumeId = alice.StreamManagement!.ResumeId;
            var aliceJid     = alice.FullJid;

            session.Kill();
            await WaitFor(() => Server.ResumableStreamCount == 1, "the kept stream");

            // Mallory is properly logged in - only as Mallory - and presents
            // Alice's id.
            var mallory = await ConnectClientAsync("mallory", maxReconnectAttempts: 0);

            await mallory.SendRawAsync(
                      $"<resume xmlns='urn:xmpp:sm:3' h='0' previd='{aliceResumeId}'/>");

            var mallorySession = Server.SessionOf(mallory.FullJid!)!;

            await WaitFor(() => mallorySession.Sent.Any(f => f.StartsWith("<failed", StringComparison.Ordinal)),
                          "the refusal");

            Assert.Multiple(() =>
            {

                Assert.That(mallory.FullJid, Is.Not.EqualTo(aliceJid),
                            "Mallory has taken over Alice's address.");

                Assert.That(Server.ResumableStreamCount, Is.EqualTo(1),
                            "Alice's stream was handed out.");

            });

        }

        #endregion

        #region TheResumedCountPreventsADoubleDelivery()

        /// <summary>
        /// What the server already had the client does not send again after the
        /// resumption.
        /// </summary>
        /// <remarks>
        /// The client holds on to every stanza it sends until an <c>h</c>
        /// acknowledges it. After a tear it therefore has a queue full of
        /// stanzas the server has long since processed - it just never had
        /// occasion to acknowledge them. Were it to send them all on bluntly,
        /// every recipient would get them twice.
        ///
        /// That is exactly what the <c>h</c> in the <c>&lt;resumed/&gt;</c>
        /// carries against: it reports how far the server got, and clears the
        /// queue up to there. Only what comes after that goes out again.
        ///
        /// <b>Not covered</b> is the reverse case - a stanza the client sends
        /// successfully and that never reaches the server. Within the same
        /// process it does not exist: a torn socket makes the sending fail at
        /// once and loudly, and a stanza that was not sent is not counted in in
        /// the first place.
        /// </remarks>
        [Test]
        public async Task TheResumedCountPreventsADoubleDelivery()
        {

            MakeContacts("alice", "bob");

            var alice   = await ConnectClientAsync(reconnectDelay: TimeSpan.FromMilliseconds(200));
            var bob     = await ConnectClientAsync("bob", createAccount: false);
            var session = Server.SessionOf(alice.FullJid!)!;

            await WaitFor(() => alice.StreamManagement?.CanResume == true,
                          "a promised resumption");

            var arrived = new List<String>();
            bob.OnMessage += m => { lock (arrived) arrived.Add(m.Body); };

            await alice.SendMessageAsync($"bob@{Server.Domain}", "Only once");

            await WaitFor(() => { lock (arrived) return arrived.Count == 1; },
                          "the message at Bob");

            Assert.That(alice.StreamManagement!.UnackedCount, Is.GreaterThan(0),
                        "Nothing outstanding - then there would be nothing to get wrong when resuming.");

            var reconnected = 0;
            alice.OnStateChanged += (_, newState) =>
            {
                if (newState == ConnectionState.Connected)
                    Interlocked.Increment(ref reconnected);
            };

            await KillAndAwaitParked(session);

            await WaitFor(() => reconnected > 0,
                          "the resumed session",
                          TimeSpan.FromSeconds(20));

            // In D7 a lengthened deadline stood here, because this test
            // occasionally turned red under load. That was the wrong
            // explanation: it was never about waiting time, but about a queue
            // that did not empty of its own accord as long as the sending on
            // asked for no acknowledgement (see D9). The deadline stands at the
            // default again.
            await WaitFor(() => alice.StreamManagement.UnackedCount == 0,
                          "the emptying of the queue after the resumption");

            await WaitAgainst(() => { lock (arrived) return arrived.Count > 1; },
                              "a second delivery of the same message");

        }

        #endregion

        #region AnExpiredStream_FallsBackToAFreshBind()

        /// <summary>
        /// If the deadline has run out, the client sets up normally.
        /// </summary>
        /// <remarks>
        /// The error path, and without it the extension would be more dangerous
        /// than its use: a client that cannot fall back on a
        /// <c>&lt;failed/&gt;</c> would not come online at all any more after a
        /// longer disturbance. The new resource is the right thing here - the
        /// old stream is gone for good.
        /// </remarks>
        [Test]
        public async Task AnExpiredStream_FallsBackToAFreshBind()
        {

            Server.ResumptionTimeout = TimeSpan.FromMilliseconds(1);

            var alice   = await ConnectClientAsync(reconnectDelay: TimeSpan.FromSeconds(3));
            var session = Server.SessionOf(alice.FullJid!)!;

            await WaitFor(() => alice.StreamManagement?.CanResume == true,
                          "a promised resumption");

            // The id tells the two cases apart, not the full JID: the resource
            // is fixed per process (console-{ProcessId}), so a new bind gives
            // the same address. A resumption keeps its id, a new <enabled/>
            // brings a new one.
            var oldResumeId = alice.StreamManagement!.ResumeId;

            session.Kill();

            // The sweeper runs once a second, the reconnect only after that.
            await WaitFor(() => alice.IsConnected &&
                                alice.StreamManagement.ResumeId is not null &&
                                alice.StreamManagement.ResumeId != oldResumeId,
                          "a fresh setup after the deadline ran out",
                          TimeSpan.FromSeconds(30));

            Assert.That(Server.SessionOf(alice.FullJid!), Is.Not.Null,
                        "The client holds itself to be connected, the server does not know it.");

        }

        #endregion

        #region StanzasLostInFlight_GoOutAgainAfterResumption()

        /// <summary>
        /// What the client sent successfully and the server never processed
        /// goes out again after the resumption.
        /// </summary>
        /// <remarks>
        /// The case the buffer on the client side exists for at all - and the
        /// one that stayed unchecked up to here, because it could not be
        /// brought about within the same process: a torn socket makes the
        /// sending fail at once and loudly, and a stanza that was not sent is
        /// not counted in in the first place. What was missing was a stanza
        /// that leaves the wire and still does not arrive.
        ///
        /// <c>SwallowClientStanzas</c> brings about exactly that: the server
        /// takes the frame and throws it away before it counts it or passes it
        /// on. To the client it looks like a successful send, to the server as
        /// though nothing ever came - the same picture as with a connection
        /// that falls apart between the sending and the processing.
        ///
        /// That the message arrives in the end hangs on the sending on alone:
        /// without it it is gone, and neither sender nor recipient would learn
        /// of it.
        /// </remarks>
        [Test]
        public async Task StanzasLostInFlight_GoOutAgainAfterResumption()
        {

            MakeContacts("alice", "bob");

            var alice   = await ConnectClientAsync(reconnectDelay: TimeSpan.FromMilliseconds(200));
            var bob     = await ConnectClientAsync("bob", createAccount: false);
            var session = Server.SessionOf(alice.FullJid!)!;

            await WaitFor(() => alice.StreamManagement?.CanResume == true,
                          "a promised resumption");

            var arrived = new List<String>();
            bob.OnMessage += m => { lock (arrived) arrived.Add(m.Body); };

            var openBefore = alice.StreamManagement!.UnackedCount;

            // From here on the server swallows what Alice sends.
            Server.SwallowClientStanzas = true;

            await alice.SendMessageAsync($"bob@{Server.Domain}", "Lost in flight");

            await WaitFor(() => alice.StreamManagement.UnackedCount > openBefore,
                          "the message sent off but unacknowledged");

            await WaitAgainst(() => { lock (arrived) return arrived.Count > 0; },
                              "a delivery, although the server is swallowing");

            Server.SwallowClientStanzas = false;

            var reconnected = 0;
            alice.OnStateChanged += (_, newState) =>
            {
                if (newState == ConnectionState.Connected)
                    Interlocked.Increment(ref reconnected);
            };

            await KillAndAwaitParked(session);

            await WaitFor(() => reconnected > 0,
                          "the resumed session",
                          TimeSpan.FromSeconds(20));

            await WaitFor(() => { lock (arrived) return arrived.Contains("Lost in flight"); },
                          "the message sent on afterwards",
                          TimeSpan.FromSeconds(20));

            // The sending on happens without counting again: the stanza already
            // carries its sequence number. Were the client to count it a second
            // time, its outgoing counter would run away from the server's
            // receiving counter, and from then on every <a h='…'/> would
            // acknowledge the wrong stanzas.
            //
            // A RequestAckAsync by hand stood here once. It was necessary
            // because the sending on did not enquire itself - and it thereby
            // covered up exactly the fault it could have shown (see D9).
            await WaitFor(() => alice.StreamManagement.LastAcknowledged ==
                                alice.StreamManagement.OutboundCount,
                          "an ack about exactly our own state");

            Assert.That(arrived.Count(b => b == "Lost in flight"), Is.EqualTo(1),
                        "The message sent on afterwards arrived several times.");

        }

        #endregion

        #region AnUnknownId_IsRefusedWithoutACount()

        /// <summary>
        /// If the server does not know the id, its refusal names no state
        /// either.
        /// </summary>
        /// <remarks>
        /// The <c>h</c> in the <c>&lt;failed/&gt;</c> is optional under
        /// XEP-0198, section 5 ("MAY also include") and means a measurement:
        /// how much of the old stream the server had processed. A fixed
        /// <c>h='0'</c> stood here until now - a number nobody measured, and
        /// the claim "of everything you sent, nothing arrived". Whoever
        /// believes it and sends on accordingly delivers everything a second
        /// time.
        ///
        /// Knowing nothing is the normal case here: the server was restarted,
        /// or the deadline ran out long ago and the sweeper has been. Then the
        /// attribute belongs left out and not guessed.
        /// </remarks>
        [Test]
        public async Task AnUnknownId_IsRefusedWithoutACount()
        {

            var alice = await ConnectClientAsync("alice", maxReconnectAttempts: 0);

            await WaitFor(() => alice.StreamManagement?.CanResume == true,
                          "a promised resumption");

            await alice.SendRawAsync(
                      "<resume xmlns='urn:xmpp:sm:3' h='0' previd='this-id-never-existed'/>");

            await WaitFor(() => Refusal() is not null, "the refusal of the server");

            var failed = Refusal()!;

            Assert.Multiple(() =>
            {

                Assert.That(failed, Does.Contain("item-not-found"),
                            "XEP-0198, section 5: an unknown id is an item-not-found.");

                Assert.That(failed, Does.Not.Contain(" h='"),
                            $"The server names a state it does not know: {failed}");

            });

        }

        #endregion

        #region AStolenId_IsNotEvenConfirmed()

        /// <summary>
        /// Whoever presents a foreign id learns nothing about the foreign
        /// stream from the refusal.
        /// </summary>
        /// <remarks>
        /// The counter-check to <see cref="AnExpiredStream_IsRefusedWithTheCountItReached"/>:
        /// there the state is information to the owner, here it would be
        /// information to a stranger. An <c>h</c> would give away two things -
        /// that this stream exists at all, and how much has run over it. A
        /// guessed attempt would thereby become a probe.
        ///
        /// The difference lies not in where the id was found but in the
        /// account: information goes only to whoever would have access to the
        /// stream anyway.
        /// </remarks>
        [Test]
        public async Task AStolenId_IsNotEvenConfirmed()
        {

            var alice   = await ConnectClientAsync("alice", maxReconnectAttempts: 0);
            var session = Server.SessionOf(alice.FullJid!)!;

            await WaitFor(() => alice.StreamManagement?.CanResume == true,
                          "a promised resumption");

            var aliceResumeId = alice.StreamManagement!.ResumeId;

            session.Kill();
            await WaitFor(() => Server.ResumableStreamCount == 1, "the kept stream");

            Assert.That(session.StanzasReceivedFromClient, Is.GreaterThan(0u),
                        "Without processed stanzas there would be nothing to give away either.");

            // Mallory is properly logged in - only as Mallory.
            var mallory = await ConnectClientAsync("mallory", maxReconnectAttempts: 0);

            await mallory.SendRawAsync(
                      $"<resume xmlns='urn:xmpp:sm:3' h='0' previd='{aliceResumeId}'/>");

            await WaitFor(() => Refusal() is not null, "the refusal of the server");

            Assert.That(Refusal(), Does.Not.Contain(" h='"),
                        $"The refusal gives away the state of a foreign stream: {Refusal()}");

        }

        #endregion

        #region AnExpiredStream_IsRefusedWithTheCountItReached()

        /// <summary>
        /// If the expired stream is still lying there, the refusal names how
        /// far the server had got.
        /// </summary>
        /// <remarks>
        /// The case from XEP-0198, section 5: "If the server recognizes the
        /// 'previd' as an earlier session that has timed out the server MAY
        /// also include a 'h' attribute indicating the number of stanzas
        /// received before the timeout."
        ///
        /// To the client that is the line between two things it otherwise
        /// cannot tell apart: what the server has processed is delivered and
        /// must not go out a second time; only what is outstanding beyond that
        /// is really lost.
        /// </remarks>
        [Test]
        public async Task AnExpiredStream_IsRefusedWithTheCountItReached()
        {

            MakeContacts("alice", "bob");

            // Otherwise this case can only be hit in a race: the sweeper goes
            // through once a second, and what it has swept the server no longer
            // knows.
            Server.SweepResumableStreams = false;

            // Three seconds, so that the race does not come out well by chance
            // either: the sweeper would have come past twice in that time. With
            // the 200 ms of the other tests this one would pass even if the
            // switch above had no effect - the returner would simply beat the
            // sweeper to it.
            var alice   = await ConnectClientAsync(reconnectDelay: TimeSpan.FromSeconds(3));
            var bob     = await ConnectClientAsync("bob", createAccount: false);
            var session = Server.SessionOf(alice.FullJid!)!;

            await WaitFor(() => alice.StreamManagement?.CanResume == true,
                          "a promised resumption");

            var arrived = new List<String>();
            bob.OnMessage += m => { lock (arrived) arrived.Add(m.Body); };

            await alice.SendMessageAsync($"bob@{Server.Domain}", "Handled");

            await WaitFor(() => { lock (arrived) return arrived.Count == 1; },
                          "the delivered message");

            // The deadline has run out before it begins to run.
            Server.ResumptionTimeout = TimeSpan.Zero;

            await KillAndAwaitParked(session);

            var reached = session.StanzasReceivedFromClient;

            Assert.That(reached, Is.GreaterThan(0u),
                        "Without processed stanzas h='0' would be the truth as well.");

            await WaitFor(() => Refusal() is not null,
                          "the refusal of the server",
                          TimeSpan.FromSeconds(20));

            Assert.That(Refusal(), Does.Contain($" h='{reached}'"),
                        $"The server does not name what it processed: {Refusal()}");

        }

        #endregion

        #region WhatTheServerHandled_IsNotReportedAsLost()

        /// <summary>
        /// What the server has processed by its own account does not count as
        /// lost to the client after a failed resumption.
        /// </summary>
        /// <remarks>
        /// The wiring without which the state from the <c>&lt;failed/&gt;</c>
        /// would be mere decoration: the client did not read it and declared
        /// every unacknowledged stanza lost - including the ones that had long
        /// been at the recipient. The acknowledging happens by way of
        /// <c>ProcessAck</c> and thereby in the same modulo arithmetic as an
        /// <c>&lt;a h='…'/&gt;</c>; a comparison of its own here would be a
        /// second understanding of the same computation.
        ///
        /// What makes the fault dangerous is the obvious reaction to it: a
        /// client that sends what was lost again (section 4 expressly
        /// recommends it) thereby delivers everything a second time.
        /// </remarks>
        [Test]
        public async Task WhatTheServerHandled_IsNotReportedAsLost()
        {

            MakeContacts("alice", "bob");

            // Both as in the previous test, and for the same reasons: the
            // sweeper standing still and a waiting time that would give it two
            // opportunities.
            Server.SweepResumableStreams = false;

            var alice   = await ConnectClientAsync(reconnectDelay: TimeSpan.FromSeconds(3));
            var bob     = await ConnectClientAsync("bob", createAccount: false);
            var session = Server.SessionOf(alice.FullJid!)!;

            await WaitFor(() => alice.StreamManagement?.CanResume == true,
                          "a promised resumption");

            var oldResumeId = alice.StreamManagement!.ResumeId;

            List<String>? lost = null;
            alice.StreamManagement.OnStanzasLost += list => lost = list;

            var arrived = new List<String>();
            bob.OnMessage += m => { lock (arrived) arrived.Add(m.Body); };

            await alice.SendMessageAsync($"bob@{Server.Domain}", "Handled");

            await WaitFor(() => { lock (arrived) return arrived.Count == 1; },
                          "the delivered message");

            Assert.That(alice.StreamManagement.UnackedCount, Is.GreaterThan(0),
                        "Nothing outstanding - then there would be nothing to declare lost wrongly either.");

            Server.ResumptionTimeout = TimeSpan.Zero;

            await KillAndAwaitParked(session);

            // Wait for the finished fresh setup, not for the mere connection:
            // between the rebuff and the new <enabled/> the id is null, and an
            // "unequal to the old one" would hold there already.
            await WaitFor(() => alice.IsConnected &&
                                alice.StreamManagement.ResumeId is not null &&
                                alice.StreamManagement.ResumeId != oldResumeId,
                          "the fresh setup after the rebuff",
                          TimeSpan.FromSeconds(20));

            Assert.That((lost ?? []).Any(s => s.Contains("Handled")), Is.False,
                        "The client holds a message to be lost that the server delivered.");

        }

        #endregion

    }

}
