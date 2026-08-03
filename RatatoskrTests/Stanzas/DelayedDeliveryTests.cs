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
    /// XEP-0203: the stamp that says a message did not come into being just
    /// now.
    /// </summary>
    /// <remarks>
    /// What is checked here is the reading at the stanza; that it arrives in the
    /// client and determines the time displayed stands in
    /// <c>OfflineMessageTests.AStoredMessage_KeepsTheTimeItWasWritten</c>.
    /// </remarks>
    [TestFixture]
    public class DelayedDeliveryTests
    {

        #region Helper functions

        private static XElement Message(String content)
            => XElement.Parse($"<message xmlns='jabber:client' from='bob@example' " +
                              $"to='alice@example'>{content}<body>Hello</body></message>");

        #endregion


        #region AStamp_IsRead()

        /// <summary>
        /// The ordinary case: moment and originator.
        /// </summary>
        [Test]
        public void AStamp_IsRead()
        {

            var read = DelayedDelivery.TryRead(
                              Message("<delay xmlns='urn:xmpp:delay' from='example' " +
                                        "stamp='2026-07-31T20:14:05Z'>Offline Storage</delay>"),
                              out var stamp,
                              out var by);

            Assert.Multiple(() =>
            {

                Assert.That(read, Is.True);

                Assert.That(stamp.UtcDateTime,
                            Is.EqualTo(new DateTime(2026, 7, 31, 20, 14, 5, DateTimeKind.Utc)));

                Assert.That(by, Is.EqualTo("example"));

            });

        }

        #endregion

        #region TheStampKeepsItsZone()

        /// <summary>
        /// The time zone part is read and not overwritten.
        /// </summary>
        /// <remarks>
        /// XEP-0203, section 3 demands UTC, but the reading must not rely on
        /// that: a stamp with a zone specification is unambiguous, and whoever
        /// turns it into the time zone of <i>this</i> machine shifts a message
        /// from another country by hours. What is meant stands in the string and
        /// not in the surroundings.
        /// </remarks>
        [Test]
        public void TheStampKeepsItsZone()
        {

            var read = DelayedDelivery.TryRead(
                              Message("<delay xmlns='urn:xmpp:delay' stamp='2026-07-31T22:14:05+02:00'/>"),
                              out var stamp,
                              out _);

            Assert.Multiple(() =>
            {
                Assert.That(read, Is.True);
                Assert.That(stamp.Offset, Is.EqualTo(TimeSpan.FromHours(2)));
                Assert.That(stamp.UtcDateTime.Hour, Is.EqualTo(20));
            });

        }

        #endregion

        #region WithoutAStamp_NothingIsRead()

        /// <summary>An ordinary message carries none.</summary>
        [Test]
        public void WithoutAStamp_NothingIsRead()
        {
            Assert.That(DelayedDelivery.TryRead(Message(""), out _, out _),
                        Is.False);
        }

        #endregion

        #region AnUnreadableStamp_CountsAsNone()

        /// <summary>
        /// What cannot be read counts like no stamp.
        /// </summary>
        /// <remarks>
        /// It comes from the counterpart, and what comes from there must
        /// overturn nothing here. The message is then just as old as it arrived
        /// - that is the poorer information, but no wrong time of day and no
        /// crash.
        ///
        /// The last case came along through a surviving mutation: a stamp
        /// <b>without a zone specification</b> violates section 3 but could be
        /// read - and was interpreted as local time. That is the worst of all
        /// readings: the message shifts by exactly the zone difference, but
        /// looks perfectly plausible in the process.
        /// </remarks>
        [TestCase("<delay xmlns='urn:xmpp:delay' stamp='yesterday evening'/>",  TestName = "No moment")]
        [TestCase("<delay xmlns='urn:xmpp:delay' stamp=''/>",               TestName = "Empty stamp")]
        [TestCase("<delay xmlns='urn:xmpp:delay'/>",                        TestName = "Without the attribute")]
        [TestCase("<delay xmlns='urn:xmpp:delay' stamp='2026-07-31T20:14:05'/>",
                  TestName = "Without a zone")]
        public void AnUnreadableStamp_CountsAsNone(String delay)
        {
            Assert.That(DelayedDelivery.TryRead(Message(delay), out _, out _),
                        Is.False);
        }

        #endregion

        #region AStampFromAnotherNamespace_IsIgnored()

        /// <summary>
        /// The old <c>jabber:x:delay</c> from XEP-0091 is not read.
        /// </summary>
        /// <remarks>
        /// XEP-0091 has been withdrawn by the XSF as <i>obsolete</i>, and its
        /// time format is another one (<c>CCYYMMDDThh:mm:ss</c>). To read it
        /// along here would mean maintaining a second format nobody sends any
        /// more - and at precisely the place where an error would again yield a
        /// wrong time of day.
        /// </remarks>
        [Test]
        public void AStampFromAnotherNamespace_IsIgnored()
        {
            Assert.That(DelayedDelivery.TryRead(
                            Message("<x xmlns='jabber:x:delay' stamp='20260731T20:14:05'/>"),
                            out _, out _),
                        Is.False);
        }

        #endregion

        #region AStampInsideAForwardedMessage_IsNotTheOuterOne()

        /// <summary>
        /// The stamp of a packed message does not date the outer one.
        /// </summary>
        /// <remarks>
        /// The case the reading looks only at direct children for: a carbon
        /// (XEP-0280) and a forwarding (XEP-0297) bring the <i>inner</i> message
        /// together with its stamp along in their <c>&lt;forwarded/&gt;</c>.
        /// Whoever searches the whole stanza dates the outer one to the time of
        /// the inner one - and is wrong precisely when it matters.
        /// </remarks>
        [Test]
        public void AStampInsideAForwardedMessage_IsNotTheOuterOne()
        {

            var carbon = Message(
                             "<received xmlns='urn:xmpp:carbons:2'>" +
                             "<forwarded xmlns='urn:xmpp:forward:0'>" +
                             "<delay xmlns='urn:xmpp:delay' stamp='2020-01-01T00:00:00Z'/>" +
                             "<message xmlns='jabber:client'><body>inside</body></message>" +
                             "</forwarded></received>");

            Assert.That(DelayedDelivery.TryRead(carbon, out _, out _), Is.False,
                        "The stamp of the inner message was ascribed to the outer one.");

        }

        #endregion

    }

}
