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

using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// Servers asked for "any free port" must not take each other's.
    /// </summary>
    /// <remarks>
    /// Written for a defect that only ever appeared as somebody else's test
    /// going red: a fixture failing in its own SetUp with
    /// <c>AddressAlreadyInUse</c>, on Linux, in a full run, and passing every
    /// time it was looked at on its own.
    ///
    /// The cause was a window rather than a mistake in arithmetic. Asked for
    /// port 0 on localhost, the transport bound the IPv6 socket, learned which
    /// port it had been given, built an IPv4 socket for the same number - and
    /// left that one unbound until the server was started. <c>[::1]:P</c> was
    /// held the whole time; <c>127.0.0.1:P</c> belonged to nobody, and anything
    /// on the machine could take it in between.
    ///
    /// <b>What this test does not do, stated because it was written expecting
    /// otherwise:</b> it does not reproduce that race. Run against the broken
    /// code on Debian it passes, five times out of five.
    ///
    /// The reason is worth keeping, because it is the actual mechanism. Two
    /// servers cannot collide with each other: the first is holding
    /// <c>[::1]:P</c>, so the operating system will not hand <c>P</c> to the
    /// second one's IPv6 socket either. The competitor for <c>127.0.0.1:P</c>
    /// has to be something else binding an IPv4 ephemeral port - and the
    /// commonest such thing is an outgoing client connection to localhost. A
    /// full suite makes hundreds of those while fixtures start servers, which
    /// is exactly where the failure appeared and why it never appeared in a
    /// fixture run on its own.
    ///
    /// So what is measured here is the allocation itself - distinct, non-zero
    /// ports under concurrent construction - and not the regression. The
    /// regression's only known reproducer is a full suite under connection
    /// churn, where it showed up roughly once in several runs. Absence over a
    /// handful of runs is therefore weak evidence either way, and this comment
    /// exists so nobody reads a green here as proof of the fix.
    /// </remarks>
    [TestFixture]
    public class PortAllocationTests
    {

        #region ManyServersAtOnce_DoNotCollide()

        /// <summary>
        /// Thirty servers, constructed and started together, each asking only
        /// for "a free port".
        /// </summary>
        /// <remarks>
        /// The assertion is on distinct ports as much as on the absence of an
        /// exception. Two servers reporting the same port would mean one of
        /// them is not listening where it says it is - a quieter failure than a
        /// throw, and the one that would send a client to the wrong server.
        /// </remarks>
        [Test]
        public async Task ManyServersAtOnce_DoNotCollide()
        {

            const Int32 count = 30;

            var servers = new XMPPServer[count];
            var faults  = new List<Exception>();

            var started = Enumerable.Range(0, count).Select(i => Task.Run(() =>
            {
                try
                {
                    servers[i] = new XMPPServer("localhost", useTLS: false);
                    servers[i].Start();
                }
                catch (Exception e)
                {
                    lock (faults) faults.Add(e);
                }
            }));

            await Task.WhenAll(started);

            try
            {

                Assert.Multiple(() =>
                {

                    Assert.That(faults.Select(f => f.Message), Is.Empty,
                                "Asking for a free port must not fail because somebody else " +
                                "was asking at the same time.");

                    var ports = servers.Where (s => s is not null).
                                        Select(s => s.Port).
                                        ToArray();

                    Assert.That(ports, Has.None.EqualTo(0),
                                "A started server has to know the port it was given.");

                    Assert.That(ports.Distinct().Count(), Is.EqualTo(ports.Length),
                                "Two servers on one port means one of them is not where it says.");

                });

            }
            finally
            {
                foreach (var server in servers.Where(s => s is not null))
                {
                    try { await server.DisposeAsync(); }
                    catch { /* the teardown is not the measurement */ }
                }
            }

        }

        #endregion

    }

}
