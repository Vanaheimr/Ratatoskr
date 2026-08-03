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
using System.Diagnostics;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// The negotiation does not wait for ever: if an answer fails to come, the
    /// connection setup fails with a message instead of hanging.
    /// </summary>
    /// <remarks>
    /// The case showed up at five mutations, spread over D25 to D29, and every
    /// time in the same way: the run hung instead of failing — a result that is
    /// none. The same finding five times from five directions is no longer an
    /// observation but a property.
    ///
    /// **And the note about it was wrong in the detail.** It read "ConnectAsync
    /// waits without a deadline of its own for the answer to the resource
    /// binding". The binding very much has a deadline — <c>SendIqAsync</c> has
    /// set one all along. What had no deadline were the <b>reading</b> steps of
    /// the negotiation: the stream header, the features, every SASL round go
    /// through <c>ReceiveStanzaAsync</c>, and that waited on the caller's token
    /// alone. A note written from memory is not a survey — the same lesson as
    /// in D19 and D23, this time on a diagnosis instead of on a list.
    ///
    /// What a failure does not produce is silence: an error arrives, a closed
    /// socket arrives. Hence the switch
    /// <see cref="XMPPServer.AnswerStreamOpen"/> — a counterpart that accepts
    /// the connection and then says nothing more.
    /// </remarks>
    [TestFixture]
    public class NegotiationTimeoutTests : AXMPPTests
    {

        #region ASilentServer_DoesNotHangTheSetup()

        /// <summary>
        /// The heart of it: if the server stays silent after the stream is
        /// opened, <c>ConnectAsync</c> fails — and in finite time at that.
        /// </summary>
        /// <remarks>
        /// The test's own deadline is more generous than the client's and
        /// carries the statement all the same: if it runs out, the client has
        /// not given up, and that is exactly the fault this is about. Without
        /// it this test would hang the way the connection setup hung — a test
        /// that reproduces the fault it checks for is none.
        ///
        /// What is checked is the <b>return</b> and the reported error, not an
        /// exception: <c>ConnectInternalAsync</c> catches every connection
        /// error and reports it through <c>OnError</c> and the state. That is
        /// the way the house is built and was never the defect — the defect was
        /// that the call did not come back at all. Whether a quietly returning
        /// <c>ConnectAsync</c> is a good interface is another question and
        /// stands under "later".
        /// </remarks>
        [Test]
        public async Task ASilentServer_DoesNotHangTheSetup()
        {

            Server.AnswerStreamOpen = false;
            Server.AddAccount("alice");

            var client = CreateClient("alice", maxReconnectAttempts: 0);

            var reported = new ConcurrentQueue<String>();
            client.OnError += m => reported.Enqueue(m);

            var clock   = Stopwatch.StartNew();
            var attempt = FailingConnectAsync(client);

            var finished = await Task.WhenAny(attempt, Task.Delay(TimeSpan.FromSeconds(40)));

            Assert.That(finished, Is.SameAs(attempt),
                        "The connection setup hangs: the server stays silent, and the " +
                        "client waits without a deadline for an answer that never comes.");

            await attempt;
            clock.Stop();

            Assert.Multiple(() =>
            {

                Assert.That(client.IsConnected, Is.False,
                            "A silent server is not a successful setup.");

                Assert.That(reported, Is.Not.Empty,
                            "And it has to be reported - otherwise it cannot be told " +
                            "apart from a successful setup.");

            });

        }

        #endregion

        #region TheFailureNamesTheStepThatTimedOut()

        /// <summary>
        /// The message names the step it hung at.
        /// </summary>
        /// <remarks>
        /// A deadline that ran out without saying what was being waited for only
        /// shifts the search: the caller then knows that something did not come,
        /// but not what. That is exactly what cost me time several times today.
        /// </remarks>
        [Test]
        public async Task TheFailureNamesTheStepThatTimedOut()
        {

            Server.AnswerStreamOpen = false;
            Server.AddAccount("alice");

            var client = CreateClient("alice", maxReconnectAttempts: 0);

            var reported = new ConcurrentQueue<String>();
            client.OnError += m => reported.Enqueue(m);

            var attempt = FailingConnectAsync(client);
            var finished  = await Task.WhenAny(attempt, Task.Delay(TimeSpan.FromSeconds(40)));

            Assert.That(finished, Is.SameAs(attempt), "The connection setup hangs.");

            await attempt;

            reported.TryDequeue(out var message);

            Assert.Multiple(() =>
            {

                Assert.That(message, Does.Contain("negotiation"),
                            "The message has to say which phase it hung in.");

                Assert.That(message, Does.Contain("stream header"),
                            "And which step was being waited for.");

            });

        }

        #endregion

        #region AFailedSetup_ThrowsInsteadOfReturningQuietly()

        /// <summary>
        /// A failed setup throws — it does not come back in silence.
        /// </summary>
        /// <remarks>
        /// Until D31 <c>ConnectAsync</c> reported a failure only through
        /// <c>OnError</c> and the state. Whoever had subscribed to nothing saw
        /// <b>no difference</b> between success and failure — and carried on
        /// working on a connection that does not exist.
        ///
        /// That is the same ill as in D30, one level up: there no answer came
        /// at all, here one comes that says nothing. A return value would not
        /// have cured it — a return value can be ignored, and an ignored return
        /// value is silence again.
        ///
        /// Only the express call throws. The reconnect attempt in the
        /// background has no caller it could owe anything to, and goes on
        /// reporting through events.
        /// </remarks>
        [Test]
        public void AFailedSetup_ThrowsInsteadOfReturningQuietly()
        {

            Server.AnswerStreamOpen = false;
            Server.AddAccount("alice");

            var client = CreateClient("alice", maxReconnectAttempts: 0);

            var error = Assert.CatchAsync(async () => await client.ConnectAsync());

            Assert.Multiple(() =>
            {

                Assert.That(error, Is.Not.Null,
                            "A silent server is not a successful setup - " +
                            "and has to strike the caller, even without a subscription.");

                // The error thrown carries the same information as the one
                // reported. If it stood in the event only, the caller would have
                // to subscribe after all to learn what was going on - and that
                // is exactly what the throw is meant to spare them.
                Assert.That(error!.Message, Does.Contain("stream header"),
                            "The error thrown has to name the step, not merely " +
                            "the fact of the failure.");

            });

        }

        #endregion

        #region TheOriginalErrorSurvivesTheThrow()

        /// <summary>
        /// What is thrown is the original error, not a shell around it.
        /// </summary>
        /// <remarks>
        /// A wrong password is something other than a timeout, and the caller
        /// should be able to tell the one from the other without reading a
        /// message. A common shell over everything would take exactly that away
        /// from them.
        /// </remarks>
        [Test]
        public void TheOriginalErrorSurvivesTheThrow()
        {

            Server.AddAccount("alice");

            var client = CreateClient("alice", password: "wrong", maxReconnectAttempts: 0);

            Assert.That(async () => await client.ConnectAsync(),
                        Throws.InstanceOf<AuthenticationException>(),
                        "A wrong password stays an authentication error.");

        }

        #endregion

        #region AnAnsweringServer_IsUnaffected()

        /// <summary>
        /// The counter-check: the ordinary setup stays untouched.
        /// </summary>
        /// <remarks>
        /// Without it the collection would pass even if the deadline were so
        /// tight that it choked off every setup.
        /// </remarks>
        [Test]
        public async Task AnAnsweringServer_IsUnaffected()
        {

            var client = await ConnectClientAsync("alice");

            Assert.That(client.FullJid, Is.Not.Null);

        }

        #endregion

    }

}
