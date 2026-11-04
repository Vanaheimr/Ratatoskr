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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// How long an answer about somebody's devices is believed.
    /// </summary>
    /// <remarks>
    /// The clock is handed in, so none of this waits for anything. What is
    /// measured is the one decision the store makes - serve, or ask again -
    /// and the three answers it makes it for.
    /// </remarks>
    [TestFixture]
    public class OmemoDeviceListsTests
    {

        #region Data

        private static readonly JID bob = JID.Parse("bob@example.org");

        #endregion

        #region Helper functions

        /// <summary>
        /// A clock the test moves itself.
        /// </summary>
        private sealed class Hand
        {

            public DateTimeOffset Now { get; private set; } = DateTimeOffset.Parse("2026-11-04T01:00:00Z");

            public DateTimeOffset Read()
                => Now;

            public void Forward(TimeSpan by)
                => Now += by;

        }

        private static OmemoDeviceList Devices(params UInt32[] ids)
            => new ([.. ids.Select(id => new OmemoDevice(id))]);

        #endregion


        #region APublishedList_IsBelievedForItsSpan()

        [Test]
        public void APublishedList_IsBelievedForItsSpan()
        {

            var hand   = new Hand();
            var lists  = new OmemoDeviceLists(PublishedFor: TimeSpan.FromMinutes(15), Clock: hand.Read);

            lists.Remember(bob, Devices(17, 23), PepLookup.Published);

            hand.Forward(TimeSpan.FromMinutes(14));

            var still  = lists.Get(bob);

            hand.Forward(TimeSpan.FromMinutes(2));

            Assert.Multiple(() =>
            {

                Assert.That(still,                Is.Not.Null);
                Assert.That(still!.Value.Lookup,  Is.EqualTo(PepLookup.Published));
                Assert.That(still.Value.List!.Devices, Has.Count.EqualTo(2));

                Assert.That(lists.Get(bob), Is.Null,
                            "past its span the question is asked again, not answered from here");

                Assert.That(lists.Count, Is.Zero,
                            "and what is past does not lie around either");

            });

        }

        #endregion

        #region AnAbsentAnswer_IsBelievedForLess()

        /// <summary>
        /// "Publishes nothing" is the answer a caller turns into writing in
        /// the clear, so it is the one held shortest.
        /// </summary>
        [Test]
        public void AnAbsentAnswer_IsBelievedForLess()
        {

            var hand   = new Hand();
            var lists  = new OmemoDeviceLists(PublishedFor:  TimeSpan.FromMinutes(15),
                                              AbsentFor:     TimeSpan.FromMinutes(5),
                                              Clock:         hand.Read);

            lists.Remember(bob, null, PepLookup.Absent);

            hand.Forward(TimeSpan.FromMinutes(4));
            var within = lists.Get(bob);

            hand.Forward(TimeSpan.FromMinutes(2));
            var after  = lists.Get(bob);

            Assert.Multiple(() =>
            {

                Assert.That(within,               Is.Not.Null);
                Assert.That(within!.Value.Lookup, Is.EqualTo(PepLookup.Absent));
                Assert.That(within.Value.List,    Is.Null);

                Assert.That(after, Is.Null,
                            "somebody who switches OMEMO on is not written to in the clear " +
                            "for a quarter of an hour because this side asked too early");

            });

        }

        #endregion

        #region Silence_IsNeverKept()

        /// <summary>
        /// Nobody answered. That is not an answer, and it does not become one
        /// by being written down.
        /// </summary>
        [Test]
        public void Silence_IsNeverKept()
        {

            var lists = new OmemoDeviceLists();

            lists.Remember(bob, null, PepLookup.Unknown);

            Assert.Multiple(() =>
            {

                Assert.That(lists.Get(bob), Is.Null,
                            "a timeout that is remembered is a question that is never asked again");

                Assert.That(lists.Count, Is.Zero);

            });

        }

        #endregion

        #region Silence_DoesNotUnseatWhatIsKnown()

        /// <summary>
        /// And it does not throw away what was known either.
        /// </summary>
        /// <remarks>
        /// Whoever holds up one IQ would otherwise empty this store at will -
        /// which is the same lever as the downgrade, one floor down.
        /// </remarks>
        [Test]
        public void Silence_DoesNotUnseatWhatIsKnown()
        {

            var lists = new OmemoDeviceLists();

            lists.Remember(bob, Devices(17), PepLookup.Published);
            lists.Remember(bob, null,        PepLookup.Unknown);

            var known = lists.Get(bob);

            Assert.Multiple(() =>
            {
                Assert.That(known,               Is.Not.Null);
                Assert.That(known!.Value.Lookup, Is.EqualTo(PepLookup.Published));
            });

        }

        #endregion

        #region ANotification_ReplacesWhatWasBelievedBefore()

        /// <summary>
        /// The reason a remembered answer may be believed at all: the
        /// publisher says what is true now, unasked.
        /// </summary>
        [Test]
        public void ANotification_ReplacesWhatWasBelievedBefore()
        {

            var hand   = new Hand();
            var lists  = new OmemoDeviceLists(PublishedFor:  TimeSpan.FromMinutes(15),
                                              AbsentFor:     TimeSpan.FromMinutes(5),
                                              Clock:         hand.Read);

            lists.Remember(bob, null, PepLookup.Absent);

            hand.Forward(TimeSpan.FromMinutes(1));

            // XEP-0163: Bob has switched OMEMO on, and his server says so
            // without anybody asking.
            lists.Remember(bob, Devices(42), PepLookup.Published);

            hand.Forward(TimeSpan.FromMinutes(10));

            var known = lists.Get(bob);

            Assert.Multiple(() =>
            {

                Assert.That(known,               Is.Not.Null,
                            "the notification did not start the longer span");

                Assert.That(known!.Value.Lookup, Is.EqualTo(PepLookup.Published));

                Assert.That(known.Value.List!.Contains(42), Is.True);

            });

        }

        #endregion

        #region WhatIsForgotten_IsAskedForAgain()

        [Test]
        public void WhatIsForgotten_IsAskedForAgain()
        {

            var lists = new OmemoDeviceLists();

            lists.Remember(bob, Devices(17), PepLookup.Published);
            lists.Forget(bob);

            Assert.That(lists.Get(bob), Is.Null);

        }

        #endregion

        #region TheAddressIsTheBareOne()

        /// <summary>
        /// A device list belongs to an account, not to a resource.
        /// </summary>
        [Test]
        public void TheAddressIsTheBareOne()
        {

            var lists = new OmemoDeviceLists();

            lists.Remember(JID.Parse("bob@example.org/phone"), Devices(17), PepLookup.Published);

            Assert.Multiple(() =>
            {

                Assert.That(lists.Get(bob), Is.Not.Null,
                            "what was learnt from one resource is about the account");

                Assert.That(lists.Get(JID.Parse("bob@example.org/desktop")), Is.Not.Null);

                Assert.That(lists.Count, Is.EqualTo(1));

            });

        }

        #endregion

    }

}
