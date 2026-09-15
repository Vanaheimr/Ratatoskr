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
using System.Xml.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0313 where it meets everything else: a result arriving over a real
    /// connection.
    /// </summary>
    /// <remarks>
    /// <see cref="MessageArchiveTests"/> drives the manager and checks what it
    /// makes of what it is given. What it cannot check is the question the whole
    /// extension turns on: <b>a result and a message are the same stanza shape
    /// arriving over the same connection</b>, and every branch the connection
    /// has further along would handle a result correctly - as the message it
    /// once was.
    ///
    /// That is the same trap as the room presences of XEP-0045, and it fails
    /// more convincingly: an occupant that wrongly becomes a contact looks odd,
    /// while a replayed conversation looks exactly like a conversation.
    /// </remarks>
    [TestFixture]
    public class MessageArchiveWiringTests : AXMPPTests
    {

        #region Helper functions

        private async Task<XMPPSession> SessionOfAsync(XMPPClient client)
        {

            await WaitFor(() => Server.SessionOf(client.FullJid.ToString()) is not null,
                          "the server session for the client");

            return Server.SessionOf(client.FullJid.ToString())!;

        }

        #endregion


        #region AnArchivedMessageDoesNotArriveAsANewOne()

        /// <summary>
        /// The one test this whole extension needed.
        /// </summary>
        /// <remarks>
        /// Without the branch that asks whether a message is a result, opening a
        /// conversation replays its history as new arrivals - every time, and
        /// convincingly: each of those really is a message that really was sent.
        /// </remarks>
        [Test]
        public async Task AnArchivedMessageDoesNotArriveAsANewOne()
        {

            var client   = await ConnectClientAsync();
            var session  = await SessionOfAsync(client);

            var arrived  = new ConcurrentQueue<XMPPMessage>();
            client.OnMessage += (t, s, m, ct) => { arrived.Enqueue(m); return Task.CompletedTask; };

            await session.SendAsync(
                $"<message from='{Server.Domain}' to='{client.FullJid}'>" +
                    "<result xmlns='urn:xmpp:mam:2' queryid='whoever' id='a1'>" +
                        "<forwarded xmlns='urn:xmpp:forward:0'>" +
                            "<delay xmlns='urn:xmpp:delay' stamp='2019-05-04T13:37:00Z'/>" +
                            $"<message xmlns='jabber:client' type='chat' from='alice@{Server.Domain}' " +
                                    $"to='{client.BareJid}'><body>said long ago</body></message>" +
                        "</forwarded>" +
                    "</result>" +
                "</message>");

            await WaitAgainst(() => arrived.Any(m => m.Body == "said long ago"),
                              "a message out of the archive arriving as a new one");

            // And an ordinary message still gets through, so the branch above is
            // not simply swallowing everything.
            await session.SendAsync(
                $"<message from='alice@{Server.Domain}/home' to='{client.FullJid}' type='chat'>" +
                    "<body>said just now</body>" +
                "</message>");

            await WaitFor(() => arrived.Any(m => m.Body == "said just now"),
                          "an ordinary message");

            Assert.That(arrived.Select(m => m.Body), Is.EqualTo(new[] { "said just now" }));

        }

        #endregion

        #region What is not asked here

        // The other half - that a result over the wire reaches the query that
        // asked for it - was written here and taken out again, because this
        // fixture cannot ask it without a race.
        //
        // A query needs an answer, and this project's test server keeps no
        // archive: it refuses the query with an error, correctly. That refusal
        // and the result put in by hand are then two stanzas travelling to the
        // same client, and whichever arrives first decides. The test passed in
        // isolation and failed in a full run, which is the worst of both.
        //
        // It is not needed here either. Take the archive branch out of the
        // connection and the room-archive rounds against Prosody and ejabberd
        // go red - measured, not assumed - because those ask a service that
        // really answers. A positive test belongs where the far side can play
        // its part.

        #endregion

    }

}
