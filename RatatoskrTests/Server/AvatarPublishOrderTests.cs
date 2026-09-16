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

using System.Security.Cryptography;

using NUnit.Framework;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0084: the picture goes up before the notice does.
    /// </summary>
    /// <remarks>
    /// <b>An order, and orders are the hardest thing to test from the outside.</b>
    /// Both publishes succeed either way, so nothing a client can observe
    /// afterwards says which went first - and a round that fetched at the moment
    /// the notice arrived would be testing a race rather than a rule.
    ///
    /// So it is asked of the server, which wrote both stanzas down as they came.
    /// That is what the test server's <c>Received</c> is for, and this is the
    /// first lane to use it for an order rather than a count.
    ///
    /// Why the order matters: the metadata is what every subscriber is pushed,
    /// and the first thing a client does on being pushed it is fetch the picture
    /// by id. Announce first and every subscriber asks for something that is not
    /// there yet - and a client that caches the miss shows nothing until the
    /// next change.
    /// </remarks>
    [TestFixture]
    public class AvatarPublishOrderTests : AXMPPTests
    {

        #region ThePictureGoesUpBeforeTheNotice()

        [Test]
        public async Task ThePictureGoesUpBeforeTheNotice()
        {

            Server.OfferPersonalEventing = true;

            var alice   = await ConnectClientAsync();
            var picture = RandomNumberGenerator.GetBytes(256);

            Assert.That(await alice.PublishAvatarAsync(picture, "image/png"), Is.Not.Null,
                        "Nothing was published at all.");

            var session = Server.SessionOf(alice.FullJid!.ToString());

            Assert.That(session, Is.Not.Null);

            var data = session!.Received.ToList().FindIndex(
                           x => x.Contains($"node='{UserAvatar.DataNode}'",     StringComparison.Ordinal));

            var meta = session.Received.ToList().FindIndex(
                           x => x.Contains($"node='{UserAvatar.MetadataNode}'", StringComparison.Ordinal));

            Assert.Multiple(() =>
            {

                Assert.That(data, Is.GreaterThanOrEqualTo(0), "The picture was never published.");
                Assert.That(meta, Is.GreaterThanOrEqualTo(0), "The announcement was never published.");

                Assert.That(data, Is.LessThan(meta),
                            "The announcement went out before the picture, so every subscriber asks " +
                            "for a picture that is not there yet.");

            });

        }

        #endregion

        #region TheNoticeIsNotSentWhenThePictureCouldNotBe()

        /// <summary>
        /// A server that takes nothing gets no announcement either.
        /// </summary>
        /// <remarks>
        /// The other half of the same rule. A data item without metadata is a
        /// picture nobody is told about, which costs only space; metadata
        /// without data is an announcement of something that does not exist,
        /// which every subscriber acts on.
        ///
        /// Provoked by leaving personal eventing switched off, which is how this
        /// server says <c>&lt;service-unavailable/&gt;</c> to a publish - and is
        /// exactly what Prosody said until <c>mod_pep</c> was switched on.
        /// </remarks>
        [Test]
        public async Task TheNoticeIsNotSentWhenThePictureCouldNotBe()
        {

            Server.OfferPersonalEventing = false;

            var alice = await ConnectClientAsync();

            Assert.That(await alice.PublishAvatarAsync(RandomNumberGenerator.GetBytes(256), "image/png"),
                        Is.Null,
                        "A publish the server refused was reported as having worked.");

            var session = Server.SessionOf(alice.FullJid!.ToString())!;

            Assert.That(session.Received.Any(
                            x => x.Contains($"node='{UserAvatar.MetadataNode}'", StringComparison.Ordinal)),
                        Is.False,
                        "The picture could not be stored and the announcement went out anyway.");

        }

        #endregion

    }

}
