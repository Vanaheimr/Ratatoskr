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

using System.Text.RegularExpressions;

using NUnit.Framework;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XMPPConnection serialises outgoing stanzas through a SemaphoreSlim,
    /// because the WebSocket contract allows only one outstanding send and
    /// sending happens from several directions at once (keepalive, automatic
    /// receipts from the receive loop, user actions).
    ///
    /// A note on where this stands: ClientWebSocket already serialises
    /// internally under .NET 10, and an unguarded call did not break in
    /// measurements. The lock secures the promise expressly instead of relying
    /// on an undocumented implementation detail.
    /// </summary>
    [TestFixture]
    public class SendLockTests : AXMPPTests
    {

        #region Data

        private const Int32 PayloadSize = 40_000;
        private const Int32 Burst       = 200;

        #endregion

        #region ConcurrentSends_ArriveIntactAndComplete()

        /// <summary>
        /// 200 simultaneous sends with 40 kB of payload each must run through
        /// without an error and arrive unfalsified. If the frames mix, either
        /// the length is wrong or the body is no longer uniform.
        /// </summary>
        [Test]
        public async Task ConcurrentSends_ArriveIntactAndComplete()
        {

            var client   = await ConnectClientAsync();
            var session  = Server.SessionOf(client.FullJid)!;

            var errors = await Task.WhenAll(
                             Enumerable.Range(0, Burst).Select(i => Task.Run(async () =>
                             {
                                 try
                                 {
                                     await client.SendRawAsync(Payload(i));
                                     return (Exception?) null;
                                 }
                                 catch (Exception ex)
                                 {
                                     return ex;
                                 }
                             })));

            var failed = errors.Where(e => e is not null).ToList();

            Assert.That(failed, Is.Empty,
                        $"{failed.Count} of {Burst} parallel sends failed, " +
                        $"first error: {failed.FirstOrDefault()?.Message}");

            await WaitFor(() => Inspect(session.Received).intact == Burst,
                          $"the arrival of all {Burst} stanzas",
                          TimeSpan.FromSeconds(20));

            var (intact, corrupt) = Inspect(session.Received);

            Assert.Multiple(() =>
            {
                Assert.That(intact,  Is.EqualTo(Burst), "Stanzas are missing.");
                Assert.That(corrupt, Is.Zero,           "Damaged stanzas have arrived.");
            });

        }

        #endregion

        #region Helper functions

        /// <summary>A stanza whose body consists of exactly one repeated character.</summary>
        /// <remarks>
        /// Until D26 an invented <c>&lt;p/&gt;</c> stood here. The thought was
        /// right — the frame is meant to be <b>without consequence</b>, so that
        /// this test measures the send lock and the intactness and not, in
        /// passing, the delivery. The way there no longer carries: since D26 the
        /// server ends a stream on which an unknown element arrives (RFC 6120,
        /// section 4.9.3.24), and the first of the 200 frames tore the
        /// connection down for the remaining 199.
        ///
        /// Being without consequence is achieved differently now: an <c>iq</c>
        /// of type <c>result</c> without a recipient is an <b>answer to the
        /// server about nothing</b>. RFC 6120, section 8.2.3, rule 4 forbids
        /// answering it; so it is accepted, recorded and dropped. Exactly what
        /// the <c>&lt;p/&gt;</c> achieved, only with an element the protocol
        /// actually has.
        /// </remarks>
        private static String Payload(Int32 i)
            => $"<iq type='result' id='burst-{i}'>" +
               new String((Char) ('A' + i % 26), PayloadSize) +
               "</iq>";

        /// <summary>Counts complete and damaged payload frames.</summary>
        private static (Int32 intact, Int32 corrupt) Inspect(IEnumerable<String> frames)
        {

            Int32 intact = 0, corrupt = 0;

            // The frame does not arrive as it was sent: the client puts the
            // namespace jabber:client on every stanza (RFC 7395, section 3.3.3
            // - over WebSocket there is no enclosing <stream:stream> for it to
            // inherit one from). The <p/> of earlier days did not get one,
            // because it was not a stanza.
            //
            // So the order of the attributes is not pinned down here, but the
            // frame as a whole is: beginning, id, body and end. That is what
            // this is about - that two simultaneous sends do not slide into
            // each other.
            foreach (var f in frames.Where(x => x.Contains("id='burst-", StringComparison.Ordinal)))
            {

                var m = Regex.Match(f, @"^<iq\b[^>]*\bid='burst-(\d+)'[^>]*>(.*)</iq>$", RegexOptions.Singleline);

                if (!m.Success)
                {
                    corrupt++;
                    continue;
                }

                var expected = (Char) ('A' + Int32.Parse(m.Groups[1].Value) % 26);
                var body     = m.Groups[2].Value;

                if (body.Length == PayloadSize && body.All(c => c == expected))
                    intact++;
                else
                    corrupt++;

            }

            return (intact, corrupt);

        }

        #endregion

    }

}
