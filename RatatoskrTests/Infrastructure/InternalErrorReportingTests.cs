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
    /// If the processing of a frame fails, it is reported instead of swallowed.
    /// </summary>
    /// <remarks>
    /// Around the processing of a frame stood a <c>catch</c> without a filter,
    /// with the note "connection cut off - the normal case in a test". A
    /// measurement over the whole collection caught <b>not a single</b>
    /// exception there: the normal case it had long since ceased to be. What it
    /// still achieved was the noiseless swallowing of programming errors — in
    /// D15 a mutation survived only because its <c>NullReferenceException</c>
    /// vanished there.
    ///
    /// This collection checks the reporting route itself. Everything else checks
    /// it beside the point: the watch hangs on every test, and every report lets
    /// it fail.
    /// </remarks>
    [TestFixture]
    public class InternalErrorReportingTests : AXMPPTests
    {

        #region AFailureWhileHandlingAFrame_IsReported()

        /// <summary>
        /// The core: an exception while processing a frame is reported, with the
        /// exception <b>and</b> the frame.
        /// </summary>
        /// <remarks>
        /// The frame belongs with it and is no decoration. A report that says
        /// only "NullReferenceException" is of almost no use with a server that
        /// processes a thousand frames; only with the stanza in hand is the
        /// route that led there traceable.
        ///
        /// <b>What is sought is the report for one's own frame, not the
        /// first</b>, and that is the correction from D32. Before, the test took
        /// the first report at all — and thereby occasionally hit the automatic
        /// sign-on presence of the client, which was still under way when the
        /// switch was thrown. The test then failed with a report about
        /// <c>&lt;presence&gt;&lt;c .../&gt;&lt;/presence&gt;</c> instead of
        /// about the trigger. What is reported first is decided by the passage
        /// of time; what the test wants to know is another question.
        /// </remarks>
        [Test]
        public async Task AFailureWhileHandlingAFrame_IsReported()
        {

            ExpectInternalErrors();

            Server.AddAccount("alice");

            // Without reconnect: the stream ends after the failure, and a client
            // that tries again runs into the same one - as long as the switch
            // stands.
            var alice = CreateClient("alice", maxReconnectAttempts: 0);
            await alice.ConnectAsync();

            var reported = new ConcurrentQueue<(String Frame, Exception Error)>();
            Server.OnInternalError += (timestamp, sender, session, frame, e, ct) => {
                reported.Enqueue((frame, e));
                return Task.CompletedTask;
            };

            // Wait until the client is really quiet - not merely until
            // ConnectAsync comes back.
            //
            // That is the correction from D69, and it has a prehistory: after
            // the setup something is still under way - the first presence, the
            // answer to the roster fetch. If the switch below falls while
            // something of that is still arriving at the server, *that* frame
            // fails first, the server ends the stream with
            // <internal-server-error/> (RFC 6120, section 4.9.1.1), and the
            // message with the identifier sought is never sent. The test then
            // waits ten seconds for a report that cannot exist any more.
            //
            // That was always a race; it became visible only when the OMEMO
            // tests kept the machine busy enough. Two out of four full runs fell
            // over it - **a test that fails half the time measures nothing any
            // more**.
            var session = Server.SessionOf(alice.FullJid.ToString())!;

            var quiet = 0;
            var level = -1;

            await WaitFor(() =>
            {
                var now = session.Received.Count;
                quiet  = now == level ? quiet + 1 : 0;
                level = now;
                return quiet >= 3;
            },
            "a client from which nothing more follows");

            Server.FailFrameHandling = true;

            await alice.SendRawAsync("<message to='bob@localhost' id='trigger'><body>Hello</body></message>");

            await WaitFor(() => reported.Any(m => m.Frame.Contains("trigger", StringComparison.Ordinal)),
                          "the report for the frame triggered");

            var report = reported.First(m => m.Frame.Contains("trigger", StringComparison.Ordinal));

            Assert.Multiple(() =>
            {

                Assert.That(report.Error, Is.TypeOf<InvalidOperationException>());

                Assert.That(report.Frame, Does.Contain("trigger"),
                            "The report has to name the frame it went wrong at.");

                Assert.That(InternalErrors, Is.Not.Empty,
                            "And the guard of the test base has to see the same.");

            });

        }

        #endregion

        #region TheStreamEndsWithInternalServerError()

        /// <summary>
        /// After the report the stream ends — with
        /// <c>&lt;internal-server-error/&gt;</c> (RFC 6120, sections 4.9.3.8 and
        /// 4.9.1.1).
        /// </summary>
        /// <remarks>
        /// Until D21 the stream ran on, and at this place stood a test that held
        /// precisely that fast. It was not wrong but described a decision that
        /// has now fallen out differently: what the frame was supposed to change
        /// is half changed, and nobody knows how far. The client reckons with a
        /// state the server does not have any more — and of all things the error
        /// that most likely leaves state behind was the only one without
        /// consequences.
        ///
        /// Section 4.9.1.1 leaves no choice after that: "Stream-level errors are
        /// unrecoverable." The client learns the reason and can begin from the
        /// front; that is more than a socket falling shut tells it.
        ///
        /// No reconnect in this test — <c>internal-server-error</c> counts as
        /// repeatable, and the client would run into the same failure as long as
        /// the switch stands. What is checked here is the ending of the
        /// <b>first</b> stream.
        /// </remarks>
        [Test]
        public async Task TheStreamEndsWithInternalServerError()
        {

            ExpectInternalErrors();

            Server.AddAccount("alice");

            var alice = CreateClient("alice", maxReconnectAttempts: 0);

            var errors = new ConcurrentQueue<StreamError>();
            alice.OnStreamError += (timestamp, sender, e, ct) => { errors.Enqueue(e); return Task.CompletedTask; };

            var rawFrames = new ConcurrentQueue<String>();
            alice.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal))
                    rawFrames.Enqueue(x);

                return Task.CompletedTask;

            };

            await alice.ConnectAsync();

            var session = Server.SessionOf(alice.FullJid!.ToString())!;

            Server.FailFrameHandling = true;

            await alice.SendRawAsync("<message to='bob@localhost' id='fails'><body>Goes wrong</body></message>");

            await WaitFor(() => !errors.IsEmpty, "the stream error at the client");

            errors.TryDequeue(out var reported);

            await WaitFor(() => !session.IsOpen, "the end of the stream");

            Assert.Multiple(() =>
            {

                Assert.That(reported!.Condition, Is.EqualTo("internal-server-error"));

                Assert.That(reported.IsRecoverable, Is.True,
                            "The client may try again - the server has " +
                            "stumbled, not been destroyed.");

                // RFC 7395, section 3.6: over WebSocket <close/> stands for the
                // </stream:stream>. Without it the client sees a socket falling
                // shut without a farewell - a network failure and not an ended
                // stream.
                Assert.That(rawFrames.Any(x => x.Contains("<close",       StringComparison.Ordinal) &&
                                          x.Contains("xmpp-framing", StringComparison.Ordinal)),
                            Is.True,
                            "The stream has to be closed properly and not " +
                            "merely the connection fall away.");

            });

        }

        #endregion

        #region TheFailedFrameIsNotDeliveredAfterwards()

        /// <summary>
        /// The failed frame is not delivered after all.
        /// </summary>
        /// <remarks>
        /// The other side of the ending: that the stream ends must not mean that
        /// the half-processed stanza still gets somewhere. Bob waits for a
        /// message that Alice's server never finished processing — it must not
        /// arrive by a detour after all, for then the failure would be without
        /// consequence for the sender and invisible for the recipient.
        /// </remarks>
        [Test]
        public async Task TheFailedFrameIsNotDeliveredAfterwards()
        {

            ExpectInternalErrors();

            Server.AddAccount("alice");

            var alice = CreateClient("alice", maxReconnectAttempts: 0);
            await alice.ConnectAsync();

            var bob = await ConnectClientAsync("bob");

            var inbox = new ConcurrentQueue<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { inbox.Enqueue(m); return Task.CompletedTask; };

            Server.FailFrameHandling = true;

            await alice.SendRawAsync($"<message to='{bob.FullJid}' type='chat' id='fails'>" +
                                     "<body>Goes wrong</body></message>");

            await WaitFor(() => InternalErrors.Count > 0, "the report");

            await WaitAgainst(() => inbox.Any(m => m.MessageId == "fails"),
                              "the delivery of the failed frame");

        }

        #endregion

        #region ASecondServer_IsWatchedThroughWatched()

        /// <summary>
        /// <c>Watched</c> puts a second server under the same guard too - and
        /// gives it back.
        /// </summary>
        /// <remarks>
        /// Eleven fixtures run servers of their own and wire them up through
        /// this one route. If it were a pass-through that hangs nothing on, they
        /// would all be unguarded, and none of the other tests would notice:
        /// where no error occurs, a missing guard looks like an effective one -
        /// the same trap as with the old <c>catch</c>.
        ///
        /// It is therefore checked on the real route and not merely the return
        /// value: the second server gets a client, fails on purpose, and the
        /// report has to arrive at the guard of this test.
        ///
        /// What is waited for is <b>any</b> report and not the one for a
        /// particular frame. Only the second server fails on purpose, so the
        /// report can come from no other — and which frame reaches it first has
        /// hung on chance since D21: the first failed one ends the stream, and
        /// whether that is one's own message or an <c>&lt;a/&gt;</c> of the
        /// stream management is decided by timing. The test would otherwise
        /// check the order of the frames instead of the wiring.
        /// </remarks>
        [Test]
        public async Task ASecondServer_IsWatchedThroughWatched()
        {

            ExpectInternalErrors();

            var raw = new XMPPServer("second.example");

            await using var second = Watched(raw);

            Assert.That(second, Is.SameAs(raw),
                        "Watched has to give back the same server - otherwise the " +
                        "guard points at a different one from the one the test uses.");

            second.Start();
            second.AddAccount("carol");

            var connection = new XMPPConnection(JID.Parse($"carol@{second.Domain}"), "pw", second.Uri)
            {
                KeepaliveEnabled            = false,
                MaxReconnectAttempts        = 0,
                ServerCertificateValidator  = second.IsOwnCertificate
            };

            await using var carol = new XMPPClient(connection);
            await carol.ConnectAsync();

            second.FailFrameHandling = true;

            await carol.SendRawAsync("<message to='dave@second.example' id='at-the-second'/>");

            await WaitFor(() => InternalErrors.Count > 0,
                          "the report of the second server at the same guard");

        }

        #endregion

        #region TheGuardItselfFailsAndForgivesAsItShould()

        /// <summary>
        /// The guard itself: it stays silent as long as nothing is reported,
        /// lets things fail as soon as something is reported, and forgives only
        /// when asked to.
        /// </summary>
        /// <remarks>
        /// A guard that nothing triggers is itself unguarded — the same trap
        /// that covered the old <c>catch</c> for so long, only one level up. The
        /// mutation "always let through" survived every other test: where no
        /// error is reported, an ineffective guard behaves exactly like an
        /// effective one, and a test that <i>has to</i> fail cannot be written as
        /// a passing test.
        ///
        /// That is why the route through the server is not taken here; instead
        /// the guard is asked directly. That is the only place in the collection
        /// where an <c>Assert</c> is checked instead of checking.
        /// </remarks>
        [Test]
        public void TheGuardItselfFailsAndForgivesAsItShould()
        {

            var watch = new InternalErrorGuard();

            Assert.Multiple(() =>
            {

                Assert.DoesNotThrow(watch.AssertClean,
                                    "Without a report it must not fail.");

                watch.Record("NullReferenceException: object reference", "<message id='x'/>");

                var failure = Assert.Throws<AssertionException>(watch.AssertClean,
                                     "With a report it has to fail.");

                Assert.That(failure!.Message, Does.Contain("NullReferenceException"),
                            "And say in the process what was reported.");

                Assert.That(failure.Message, Does.Contain("<message id='x'/>"),
                            "Together with the frame it went wrong at.");

                watch.Expect();

                Assert.DoesNotThrow(watch.AssertClean,
                                    "Whoever asks it for leniency gets it.");

                watch.Reset();

                Assert.That(watch.Errors, Is.Empty,
                            "And the next test begins with an empty list.");

            });

        }

        #endregion

        #region AGreenRunReportsNothing()

        /// <summary>
        /// The counter-check, and the most important one: an ordinary run
        /// reports nothing.
        /// </summary>
        /// <remarks>
        /// It is the reason why the watch may hang on every test without making
        /// the collection unusable. If normal operation reported something
        /// continually — cut-off connections, say, as the old comment claimed —
        /// a watch that fails on that would be unusable, and one would have to
        /// filter after all.
        ///
        /// The test stands here expressly and not merely implicitly in the
        /// others: what it asserts is a statement about the server and not about
        /// this one run. And it holds fast that the measurement which justified
        /// this rebuild was no accident of one run.
        /// </remarks>
        [Test]
        public async Task AGreenRunReportsNothing()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            MakeContacts("alice", "bob");

            var inbox = new ConcurrentQueue<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { inbox.Enqueue(m); return Task.CompletedTask; };

            await alice.SendMessageAsync(bob.BareJid, "A quite ordinary run");
            await WaitFor(() => !inbox.IsEmpty, "the message");

            // A cut-off - precisely what the old comment took for the normal
            // case. The session stays in the list in the process: a stream with
            // a granted resumption is kept and not signed off (XEP-0198,
            // section 5). What is waited for is therefore that the connection is
            // gone, not the session.
            Server.KillSessionsOf(bob.BareJid.ToString());

            await WaitFor(() => Server.SessionsOf(bob.BareJid.ToString()).All(s => !s.IsOpen),
                          "the end of the connection");

            await alice.SendMessageAsync(bob.BareJid, "And another one, into the void");

            Assert.That(InternalErrors, Is.Empty,
                        "A cut-off is no internal error either.");

        }

        #endregion

    }

}
