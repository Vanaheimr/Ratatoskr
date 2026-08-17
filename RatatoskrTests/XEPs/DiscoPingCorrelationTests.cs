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

using System.Xml.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// Disco and Ping keep pending maps of their own - and had the same gap in
    /// them as the general one.
    /// </summary>
    /// <remarks>
    /// The general requests were closed first, because the OMEMO bundle fetch
    /// runs on them. These two were left over: <c>disco-info-2</c> and
    /// <c>ping-1</c> are just as countable, and an answer to either was
    /// believed whoever sent it.
    ///
    /// What it buys an attacker is smaller here. A forged disco answer does not
    /// poison the caps cache - that recomputes the <c>ver</c> hash and throws
    /// away what does not match - and a forged pong is a wrong round-trip time.
    /// But "smaller" is a reason to do it later, not a reason to leave one rule
    /// holding in one place and not in the other.
    /// </remarks>
    [TestFixture]
    public class DiscoPingCorrelationTests
    {

        #region Data & helper functions

        private const String Mine = "alice@example.org";
        private const String Bob  = "bob@example.com";

        private static DiscoManager Disco()
            => new(_ => Task.CompletedTask, Mine);

        private static PingManager Ping()
            => new(_ => Task.CompletedTask, Mine);

        private static XElement InfoResult()
            => XElement.Parse($"<iq type='result'><query xmlns='{DiscoManager.InfoNamespace}'>" +
                              "<feature var='urn:xmpp:ping'/></query></iq>");

        private static XElement ItemsResult()
            => XElement.Parse($"<iq type='result'><query xmlns='{DiscoManager.ItemsNamespace}'>" +
                              "<item jid='conference.example.com'/></query></iq>");

        #endregion


        #region ADiscoInfoAnswerFromSomebodyElse_IsNotTaken()

        /// <summary>
        /// A stranger answers the question that was put to Bob - and the
        /// question goes on waiting for Bob.
        /// </summary>
        /// <remarks>
        /// The second half is the one easy to get wrong. Recognising the
        /// forgery and taking the pending entry out along the way exchanges one
        /// damage for another: Bob's real answer then belongs to nobody.
        /// </remarks>
        [Test]
        public async Task ADiscoInfoAnswerFromSomebodyElse_IsNotTaken()
        {

            var disco = Disco();
            var query = disco.QueryInfoAsync(JID.Parse(Bob));

            Assert.Multiple(() =>
            {

                Assert.That(disco.ProcessInfoResult("disco-info-1", InfoResult(), "mallory@example.com"),
                            Is.False, "A stranger's answer is not this query's.");

                Assert.That(query.IsCompleted,
                            Is.False, "And it must not have taken the waiting party's place.");

            });

            Assert.That(disco.ProcessInfoResult("disco-info-1", InfoResult(), Bob),
                        Is.True, "Bob's answer still arrives.");

            var info = await query.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.That(info?.From, Is.EqualTo(Bob));

        }

        #endregion

        #region ADiscoItemsAnswerFromSomebodyElse_IsNotTaken()

        [Test]
        public async Task ADiscoItemsAnswerFromSomebodyElse_IsNotTaken()
        {

            var disco = Disco();
            var query = disco.QueryItemsAsync(JID.Parse(Bob));

            Assert.That(disco.ProcessItemsResult("disco-items-1", ItemsResult(), "mallory@example.com"),
                        Is.False);

            Assert.That(disco.ProcessItemsResult("disco-items-1", ItemsResult(), Bob),
                        Is.True);

            var items = await query.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.That(items?.From, Is.EqualTo(Bob));

        }

        #endregion

        #region TheOwnServerAnsweringForAnUnreachableTarget_IsTaken()

        /// <summary>
        /// One's own server answers <b>instead of</b> the addressee whenever it
        /// cannot reach them, and under its own domain.
        /// </summary>
        /// <remarks>
        /// This case is in here because it was got wrong first. The rule
        /// demanded the addressee and nobody else, so a
        /// <c>remote-server-not-found</c> for an unknown domain was refused -
        /// and the caller, instead of the error that says what happened, got
        /// silence until its timeout ran out. A defence that swallows errors is
        /// hard to tell from a network that swallows them.
        /// </remarks>
        [Test]
        public async Task TheOwnServerAnsweringForAnUnreachableTarget_IsTaken()
        {

            var disco = Disco();
            var query = disco.QueryInfoAsync(JID.Parse("somebody@far.example"));

            Assert.That(await disco.ProcessErrorAsync("disco-info-1",
                                           new StanzaError(StanzaErrorType.Cancel, "remote-server-not-found"),
                                           "example.org"),
                        Is.True, "The own domain stands in for whoever could not be reached.");

            Assert.That(await query.WaitAsync(TimeSpan.FromSeconds(3)), Is.Null);

        }

        #endregion

        #region APongFromSomebodyElse_IsNotBelieved()

        /// <summary>
        /// A round-trip time that anybody may write is not a measurement - and
        /// the keepalive runs on this one.
        /// </summary>
        [Test]
        public async Task APongFromSomebodyElse_IsNotBelieved()
        {

            var ping = Ping();
            var task = ping.PingAsync(JID.Parse(Bob));

            // Outside the Assert.Multiple, because that one takes a synchronous
            // lambda and the answer now has to be awaited.
            var believed = await ping.ProcessPongAsync("ping-1", "mallory@example.com");

            Assert.Multiple(() =>
            {
                Assert.That(believed,          Is.False);
                Assert.That(task.IsCompleted,  Is.False);
            });

            Assert.That(await ping.ProcessPongAsync("ping-1", Bob), Is.True);
            Assert.That(await task.WaitAsync(TimeSpan.FromSeconds(3)), Is.Not.Null);

        }

        #endregion

        #region APingToTheOwnServer_IsAnsweredByIt()

        /// <summary>
        /// A ping without a target goes to one's own server, and it may answer
        /// without naming itself at all - the counter-check that the rule is
        /// not drawn so tight that the keepalive stops working.
        /// </summary>
        [Test]
        public async Task APingToTheOwnServer_IsAnsweredByIt()
        {

            var ping = Ping();
            var task = ping.PingAsync();

            Assert.That(await ping.ProcessPongAsync("ping-1", null), Is.True);
            Assert.That(await task.WaitAsync(TimeSpan.FromSeconds(3)), Is.Not.Null);

        }

        #endregion

    }

}
