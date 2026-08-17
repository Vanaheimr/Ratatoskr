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
    /// The watch over all servers: it finds even the one nobody signed up.
    /// </summary>
    /// <remarks>
    /// The case this is about is not the error but the <b>forgetting</b>:
    /// somebody writes a new fixture, creates a server without
    /// <c>Watched(…)</c> — and from then on this server swallows programming
    /// errors noiselessly again, without anything turning red. Precisely for
    /// that reason no test held the wiring; it was secured by a source
    /// inspection by hand (see D19).
    ///
    /// These tests create the forgotten server on purpose. They are the only
    /// place in the collection where a <c>new XMPPServer(…)</c> <b>without</b>
    /// <c>Watched(…)</c> is right.
    /// </remarks>
    [TestFixture]
    public class GlobalErrorWatchTests : AXMPPTests
    {

        #region AnUnwatchedServer_IsStillSeen()

        /// <summary>
        /// A server that no fixture has signed up reports all the same.
        /// </summary>
        /// <remarks>
        /// <c>ExpectInternalErrors()</c> does not stand here because the error
        /// <i>would be</i> expected, but because it <b>is</b>: without this line
        /// the watch would let this test fail - and that is the proof it
        /// carries.
        /// </remarks>
        [Test]
        public async Task AnUnwatchedServer_IsStillSeen()
        {

            ExpectInternalErrors();

            // Without Watched(…) - the case that is to be caught.
            await using var forgotten = new XMPPServer("forgotten.example");

            forgotten.Start();
            forgotten.FailFrameHandling = true;

            var client = new XMPPClient(
                             new XMPPConnection($"alice@{forgotten.Domain}", "pw", forgotten.Uri)
                             {
                                 MaxReconnectAttempts        = 0,
                                 KeepaliveEnabled            = false,
                                 ServerCertificateValidator  = forgotten.IsOwnCertificate
                             });

            forgotten.AddAccount("alice");

            // The setup fails, for the very first frame blows up in the
            // server's face. That is exactly the point.
            try { await client.ConnectAsync(); }
            catch { /* expected */ }

            await WaitFor(() => GlobalErrorWatchAttribute.Errors.Count > 0,
                          "the report of the watch over all servers");

            Assert.That(GlobalErrorWatchAttribute.Errors[0],
                        Does.Contain("FailFrameHandling"),
                        "What is reported is the exception together with the reason.");

            await client.DisposeAsync();

        }

        #endregion

        #region TheWatchFailsTheTest_AndStartsTheNextOneClean()

        /// <summary>
        /// The watch actually lets things fail — and begins the next test empty
        /// handed again.
        /// </summary>
        /// <remarks>
        /// Without this test the worst version would be a passing one: a watch
        /// that takes everything in and never makes anything of it. It would
        /// look like a safeguard, would be none, and the whole collection would
        /// stay green — precisely the same trap that
        /// <see cref="InternalErrorGuard.Record"/> defuses for the watch per
        /// fixture.
        ///
        /// The second part belongs with it, because otherwise it would depend on
        /// the order of the tests: if a report stayed standing past the end of
        /// the test, that would show only to the <i>following</i> test — and
        /// which one that is, the test runner decides. Here the transition
        /// itself is staged: report, let it fail, begin the next test, look.
        /// </remarks>
        [Test]
        public void TheWatchFailsTheTest_AndStartsTheNextOneClean()
        {

            var watch = new GlobalErrorWatchAttribute();

            GlobalErrorWatchAttribute.Record("Invented: NullReferenceException in the delivery route");

            Assert.That(GlobalErrorWatchAttribute.Errors, Is.Not.Empty);

            Assert.That(() => watch.AfterTest(null!),
                        Throws.InstanceOf<AssertionException>(),
                        "A watch that only takes things in and never makes anything of it is none.");

            // The next test begins - and finds nothing there any more. With that
            // the real pass at the end of this test is quiet again too.
            watch.BeforeTest(null!);

            Assert.That(GlobalErrorWatchAttribute.Errors, Is.Empty,
                        "A report that stays standing lets the next test fail.");

        }

        #endregion

        #region AWatchedServerWithoutErrors_KeepsTheWatchSilent()

        /// <summary>
        /// And the counter-check: an ordinary test lets it stay silent.
        /// </summary>
        /// <remarks>
        /// Without this test a watch that <i>always</i> reports would be a
        /// passing solution - and the whole collection red. That it is not,
        /// every other test does check along, but only as a side effect; here it
        /// stands as an assertion.
        ///
        /// The assertion holds at the same time for the separation between the
        /// tests: what the previous one reported has to be gone at the beginning
        /// of this one, otherwise it would fail.
        /// </remarks>
        [Test]
        public async Task AWatchedServerWithoutErrors_KeepsTheWatchSilent()
        {

            var alice = await ConnectClientAsync();

            await alice.SendMessageAsync(JID.Parse($"alice@{Server.Domain}"), "To myself");

            await WaitAgainst(() => GlobalErrorWatchAttribute.Errors.Count > 0,
                              "a report although nothing went wrong");

        }

        #endregion

    }

}
