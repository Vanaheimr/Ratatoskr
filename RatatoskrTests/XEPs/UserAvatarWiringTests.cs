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

using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0084 where an announcement arrives over a real connection.
    /// </summary>
    /// <remarks>
    /// <see cref="UserAvatarTests"/> drives the reading and checks what it makes
    /// of what it is given. What it cannot reach is the branch that decides
    /// <b>whether to raise the event at all</b> - and that branch has three
    /// cases where an obvious reading has two.
    ///
    /// The three: a picture, a removal, and an announcement that says nothing.
    /// The first two have to arrive; the third must not, because "I could not
    /// read that" and "this person has no picture" lead to opposite things on a
    /// screen. The first version of the code said so in a comment and did not do
    /// it - both branches ended at an empty list - and a mutation that removed
    /// the distinction survived the whole suite, because the distinction was not
    /// there to remove.
    /// </remarks>
    [TestFixture]
    public class UserAvatarWiringTests : AXMPPTests
    {

        #region Helper functions

        private async Task<XMPPSession> SessionOfAsync(XMPPClient client)
        {

            await WaitFor(() => Server.SessionOf(client.FullJid.ToString()) is not null,
                          "the server session for the client");

            return Server.SessionOf(client.FullJid.ToString())!;

        }

        /// <summary>
        /// A PEP event as a server sends one, with whatever payload is handed in.
        /// </summary>
        private static String AnnouncementOf(String from, String payload)

            => $"<message from='{from}' type='headline'>" +
                "<event xmlns='http://jabber.org/protocol/pubsub#event'>" +
               $"<items node='{UserAvatar.MetadataNode}'>" +
                "<item id='x'>" + payload + "</item>" +
                "</items></event></message>";

        #endregion


        #region ThreeKindsOfAnnouncement()

        /// <summary>
        /// A picture arrives, a removal arrives, and nonsense does not.
        /// </summary>
        [Test]
        public async Task ThreeKindsOfAnnouncement()
        {

            var alice    = await ConnectClientAsync();
            var session  = await SessionOfAsync(alice);

            var heard = new ConcurrentQueue<IReadOnlyList<AvatarInfo>>();

            alice.Connection.OnAvatarChanged += (t, s, jid, infos, ct) =>
            {
                heard.Enqueue(infos);
                return Task.CompletedTask;
            };

            // 1. A picture.
            await session.SendAsync(AnnouncementOf(
                "bob@localhost",
                $"<metadata xmlns='{UserAvatar.MetadataNode}'>" +
                 "<info id='abc' bytes='12' type='image/png'/></metadata>"));

            await WaitFor(() => heard.Count == 1, "the announcement of a picture");

            Assert.That(heard.Single()[0].Id, Is.EqualTo("abc"));

            // 2. A removal, which has to arrive - a node left alone goes on
            //    announcing the old picture, so this cannot be silence.
            await session.SendAsync(AnnouncementOf(
                "bob@localhost",
                $"<metadata xmlns='{UserAvatar.MetadataNode}'/>"));

            await WaitFor(() => heard.Count == 2, "the announcement of a removal");

            Assert.That(heard.Last(), Is.Empty,
                        "A removal did not arrive as an empty list.");

            // 3. Something unreadable, which must not - it is not news that
            //    somebody has no picture, it is no news at all.
            await session.SendAsync(AnnouncementOf(
                "bob@localhost",
                "<something xmlns='urn:example:nonsense'/>"));

            await session.SendAsync(AnnouncementOf(
                "bob@localhost",
                $"<metadata xmlns='{UserAvatar.MetadataNode}'>" +
                 "<info bytes='12' type='image/png'/></metadata>"));

            // Nothing to wait for, so a moment is given and then the count has to
            // be unchanged. A second is long enough: the two above crossed the
            // same connection in milliseconds.
            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.That(heard, Has.Count.EqualTo(2),
                        "An announcement that could not be read arrived as 'this person has no " +
                        "picture', which would take a face off the screen because a stanza was " +
                        "malformed.");

        }

        #endregion

    }

}
