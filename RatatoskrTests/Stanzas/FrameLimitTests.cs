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
    /// How much a peer may send before it is refused.
    /// </summary>
    /// <remarks>
    /// RFC 6120, section 13.12 asks for a limit, and there was none on either
    /// side. What makes this cheap for whoever tries it is that neither an
    /// element nor a WebSocket message has to be finished: a tag that is opened
    /// and never closed, or a message announced in continuation frames without
    /// end, and the receiver grows until the machine gives out. It costs the
    /// sender the sending and nothing else.
    /// </remarks>
    [TestFixture]
    public class FrameLimitTests : AXMPPTests
    {

        #region ASplitterFrameThatNeverEnds_IsRefused()

        /// <summary>
        /// The server side. An element is opened and then simply fed, and the
        /// buffer that assembles it used to have no bound at all.
        /// </summary>
        [Test]
        public void ASplitterFrameThatNeverEnds_IsRefused()
        {

            var splitter = new XmlStreamSplitter();

            splitter.Push("<stream:stream xmlns:stream='http://etherx.jabber.org/streams'>");
            splitter.Push("<message><body>");

            // A megabyte at a time, so that the growth is visible in steps
            // rather than in one allocation.
            var chunk = new String('x', 1024 * 1024);

            Assert.Throws<XmlStreamSplitter.OverlongFrameException>(() =>
            {
                for (var i = 0; i < 8; i++)
                    splitter.Push(chunk);
            });

        }

        #endregion

        #region ASplitterFrameBelowTheLimit_StillArrives()

        /// <summary>
        /// The counter-check, so that the limit is not simply a refusal of
        /// everything sizeable. A large but finished stanza has to come through
        /// - a roster of many thousand entries is a real thing.
        /// </summary>
        [Test]
        public void ASplitterFrameBelowTheLimit_StillArrives()
        {

            var splitter = new XmlStreamSplitter();

            splitter.Push("<stream:stream xmlns:stream='http://etherx.jabber.org/streams'>");

            var big    = new String('x', 1024 * 1024);
            var frames = splitter.Push($"<message><body>{big}</body></message>");

            Assert.Multiple(() =>
            {
                Assert.That(frames,     Has.Count.EqualTo(1));
                Assert.That(frames[0],  Has.Length.GreaterThan(1024 * 1024));
            });

        }

        #endregion

        #region AnOverlongStanzaFromTheServer_EndsTheConnection()

        /// <summary>
        /// The client side, and end to end: the server sends more than the
        /// client will assemble, and the client gives the connection up instead
        /// of reading to the end.
        /// </summary>
        /// <remarks>
        /// <b>Giving it up is the point.</b> Reading the frame to its end in
        /// order to discard it afterwards is doing exactly the work that was
        /// asked for. Whoever sends this is broken or hostile, and in both
        /// cases there is nothing further to talk about.
        ///
        /// The client reconnects by itself after a drop, which is why what is
        /// measured here is the drop and not the state afterwards.
        /// </remarks>
        [Test]
        public async Task AnOverlongStanzaFromTheServer_EndsTheConnection()
        {

            var client  = await ConnectClientAsync("alice", maxReconnectAttempts: 0);
            var session = Server.SessionOf(client.FullJid.ToString())!;

            var dropped = false;
            client.Connection.OnStateChanged += (timestamp, sender, _, now, ct) => {
                if (now != ConnectionState.Connected)
                    dropped = true;

                return Task.CompletedTask;

            };

            var oversized = new String('x', (Int32) XMPPConnection.MaxStanzaBytes + 1024);

            await session.SendAsync($"<message from='bob@{Server.Domain}'><body>{oversized}</body></message>");

            await WaitFor(() => dropped, "the connection to be given up");

            Assert.That(dropped, Is.True);

        }

        #endregion

    }

}
