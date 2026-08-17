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

using System.Diagnostics;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// On a reconnect the previous connection has to be taken down completely.
    /// If the old CancellationTokenSource is merely overwritten instead of
    /// cancelled, the receive and keepalive loops carry on running and add up
    /// with every reconnect.
    /// </summary>
    [TestFixture]
    public class ReconnectTests : AXMPPTests
    {

        #region Data

        private static readonly TimeSpan Keepalive = TimeSpan.FromMilliseconds(500);

        #endregion

        #region Helper functions

        /// <summary>
        /// Counts what the keepalive loop actually sends.
        /// </summary>
        /// <remarks>
        /// The loop picks its means according to the situation: if XEP-0198 has
        /// been negotiated it sends an <c>&lt;r/&gt;</c>, otherwise a XEP-0199
        /// ping. These two tests counted nothing but pings until recently - and
        /// when stream management became the default, they counted nothing at
        /// all.
        ///
        /// One test turned red from that, the other <b>green</b>: "no pings are
        /// at most seven pings" holds, and a test that no longer measures
        /// anything does not say so of its own accord. Hence both procedures
        /// here and both tests over both.
        /// </remarks>
        private static Int32 KeepaliveCount(XMPPSession session, Boolean streamManagement)

            => streamManagement
                   ? session.CountReceived("<r xmlns='urn:xmpp:sm:3'/>")
                   : session.CountReceived("urn:xmpp:ping");

        #endregion

        #region Reconnect_EstablishesExactlyOneNewConnection()

        /// <summary>
        /// Every torn connection leads to exactly one new server connection.
        /// </summary>
        [Test]
        public async Task Reconnect_EstablishesExactlyOneNewConnection()
        {

            var client = await ConnectClientAsync(keepalive: Keepalive,
                                                  reconnectDelay: TimeSpan.FromMilliseconds(200));

            Assert.That(client.IsConnected, Is.True);

            const Int32 kills = 3;

            for (var i = 0; i < kills; i++)
            {

                var before = Server.ConnectionCount;
                Server.KillAllSessions();

                await WaitFor(() => Server.ConnectionCount > before && client.IsConnected,
                              $"reconnect {i + 1} of {kills}",
                              TimeSpan.FromSeconds(20));

            }

            Assert.That(Server.ConnectionCount, Is.EqualTo(kills + 1),
                        "The server saw more or fewer connections than expected.");

        }

        #endregion

        #region Reconnect_DoesNotAccumulateKeepaliveLoops()

        /// <summary>
        /// After several reconnects only one keepalive loop may still be
        /// running. With the earlier leak, four loops fired in parallel after
        /// three tears - measured 17 instead of 6 pings in three seconds.
        /// </summary>
        [Test]
        [TestCase(true,  TestName = "Reconnect_DoesNotAccumulateKeepaliveLoops(Stream Management)")]
        [TestCase(false, TestName = "Reconnect_DoesNotAccumulateKeepaliveLoops(Ping)")]
        public async Task Reconnect_DoesNotAccumulateKeepaliveLoops(Boolean streamManagement)
        {

            var client = await ConnectClientAsync(keepalive: Keepalive,
                                                  reconnectDelay: TimeSpan.FromMilliseconds(200),
                                                  streamManagement: streamManagement);

            const Int32 kills = 3;

            for (var i = 0; i < kills; i++)
            {

                var before = Server.ConnectionCount;
                Server.KillAllSessions();

                await WaitFor(() => Server.ConnectionCount > before && client.IsConnected,
                              $"reconnect {i + 1} of {kills}",
                              TimeSpan.FromSeconds(20));

            }

            // Measuring window: count only the current session
            var session = Server.SessionOf(client.FullJid.ToString())!;
            await Task.Delay(300);

            var before2  = KeepaliveCount(session, streamManagement);
            var window   = TimeSpan.FromSeconds(3);

            await Task.Delay(window);

            var counted   = KeepaliveCount(session, streamManagement) - before2;
            var expected  = (Int32) (window.TotalMilliseconds / Keepalive.TotalMilliseconds);
            var limit     = expected + 2;

            Assert.Multiple(() =>
            {

                Assert.That(counted, Is.LessThanOrEqualTo(limit),
                            $"{counted} keepalives in {window.TotalSeconds}s, expected at most {limit}. " +
                            $"That points to {Math.Round((Double) counted / expected, 1)} parallel keepalive loops.");

                // The lower bound is what really gained something: without it
                // this test passes even when no keepalive fires at all - and
                // that is what it was for a while.
                Assert.That(counted, Is.GreaterThan(0),
                            "Not a single keepalive in the measuring window - then this test checks nothing.");

            });

        }

        #endregion

        #region Disconnect_StopsKeepalive()

        /// <summary>
        /// After disconnecting no keepalive may fire any more.
        /// </summary>
        [Test]
        [TestCase(true,  TestName = "Disconnect_StopsKeepalive(Stream Management)")]
        [TestCase(false, TestName = "Disconnect_StopsKeepalive(Ping)")]
        public async Task Disconnect_StopsKeepalive(Boolean streamManagement)
        {

            var client   = await ConnectClientAsync(keepalive: Keepalive,
                                                    streamManagement: streamManagement);
            var session  = Server.SessionOf(client.FullJid.ToString())!;

            await WaitFor(() => KeepaliveCount(session, streamManagement) > 0,
                          "the first keepalive");

            await client.DisconnectAsync();
            await Task.Delay(300);

            var afterDisconnect = KeepaliveCount(session, streamManagement);

            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.That(KeepaliveCount(session, streamManagement), Is.EqualTo(afterDisconnect),
                        "Further keepalives arrived after disconnecting.");

        }

        #endregion

        #region Disconnect_WithSilentServer_ReturnsWithinCloseTimeout()

        /// <summary>
        /// If the server does not answer the close frame, DisconnectAsync must
        /// still come back promptly - the close handshake is limited to three
        /// seconds, after which the socket is torn down.
        /// </summary>
        [Test]
        public async Task Disconnect_WithSilentServer_ReturnsWithinCloseTimeout()
        {

            Server.CompleteCloseHandshake = false;

            var client = await ConnectClientAsync();
            Assert.That(client.IsConnected, Is.True);

            var sw = Stopwatch.StartNew();
            await client.DisconnectAsync();
            sw.Stop();

            Assert.Multiple(() =>
            {
                Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)),
                            $"DisconnectAsync hung for {sw.Elapsed.TotalSeconds:F1}s.");

                // Without a lower bound the test would pass even when the server
                // does not stay silent but tears the connection down - the
                // client then comes back at once and the time limit never took
                // hold. That is exactly how it ran through once while the
                // transport was being rebuilt.
                Assert.That(sw.Elapsed, Is.GreaterThan(TimeSpan.FromSeconds(2)),
                            $"DisconnectAsync came back after {sw.Elapsed.TotalSeconds:F1}s - " +
                            "the time limit of the close handshake cannot have taken hold.");

                Assert.That(client.IsConnected, Is.False);
            });

        }

        #endregion

    }

}
