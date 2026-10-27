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
    /// Which answers to a PEP fetch say that the thing is not there, and which
    /// only say that the asking did not work.
    /// </summary>
    /// <remarks>
    /// <b>A table rather than prose, because the list is the decision.</b>
    /// Whatever lands on <see cref="PepLookup.Absent"/> is a reason for a
    /// sender to write in the clear instead, and whatever lands on
    /// <see cref="PepLookup.Unknown"/> is a reason to refuse. Three conditions
    /// are on one side and everything else is on the other, and the only way to
    /// see that it stays that way is to write the two sides out.
    /// </remarks>
    [TestFixture]
    public class PepLookupTests
    {

        #region Helper functions

        private static XElement ErrorIq(String condition, String type)
            => XElement.Parse(
                   "<iq xmlns='jabber:client' type='error' id='pep-1' from='bob@example.org'>" +
                   $"<error type='{type}'>" +
                   $"<{condition} xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                   "</error></iq>");

        #endregion

        #region NothingCameBack_IsNotAnAnswer()

        /// <summary>
        /// No answer at all is the case the whole distinction exists for.
        /// </summary>
        /// <remarks>
        /// It is also the one anybody can produce: holding up or dropping a
        /// single stanza is within reach of the recipient's server, of this
        /// side's server, and of whoever sits between them. If silence came
        /// back looking like "publishes nothing", any of them could decide that
        /// the next message travels in the clear.
        /// </remarks>
        [Test]
        public void NothingCameBack_IsNotAnAnswer()
        {

            Assert.That(PepAnswer.WhatItSays(null),
                        Is.EqualTo(PepLookup.Unknown),
                        "a timeout was read as a statement about what the far end publishes");

        }

        #endregion

        #region AResult_IsLeftToItsReader()

        /// <summary>
        /// A result says the asking worked; what is in it is somebody else's
        /// question.
        /// </summary>
        [Test]
        public void AResult_IsLeftToItsReader()
        {

            Assert.That(PepAnswer.WhatItSays(
                            XElement.Parse("<iq xmlns='jabber:client' type='result' id='pep-1' " +
                                           "from='bob@example.org'/>")),
                        Is.EqualTo(PepLookup.Published));

        }

        #endregion

        #region WhatARefusalSays(Condition, Type, Expected)

        /// <summary>
        /// Every refusal this side has a settled opinion about.
        /// </summary>
        /// <remarks>
        /// <b>The two halves are not symmetrical and should not look it.</b>
        /// Reading "there is nothing there" too generously means writing in the
        /// clear to somebody who was expecting encryption; reading it too
        /// narrowly means refusing to write to somebody who simply does no
        /// OMEMO, which the switch per conversation can still get around. One
        /// of those two mistakes is recoverable.
        ///
        /// <c>forbidden</c> is the one worth looking at twice: a node this side
        /// is not allowed to read is a node that <i>exists</i>, so the account
        /// behind it may very well be doing OMEMO with everybody but this one.
        /// </remarks>
        [TestCase("item-not-found",           "cancel",  PepLookup.Absent,
                  TestName = "A node that is not there is an answer")]
        [TestCase("service-unavailable",      "cancel",  PepLookup.Absent,
                  TestName = "... and so is a server that does no PEP")]
        [TestCase("feature-not-implemented",  "cancel",  PepLookup.Absent,
                  TestName = "... whichever way it says so")]
        [TestCase("internal-server-error",    "wait",    PepLookup.Unknown,
                  TestName = "A server that broke has said nothing about the account")]
        [TestCase("remote-server-timeout",    "wait",    PepLookup.Unknown,
                  TestName = "... nor has a server that could not be reached")]
        [TestCase("recipient-unavailable",    "wait",    PepLookup.Unknown,
                  TestName = "... nor one that says to come back later")]
        [TestCase("forbidden",                "auth",    PepLookup.Unknown,
                  TestName = "A node this side may not read is a node that is there")]
        [TestCase("not-authorized",           "auth",    PepLookup.Unknown,
                  TestName = "... and so is one this side is not signed in for")]
        [TestCase("undefined-condition",      "cancel",  PepLookup.Unknown,
                  TestName = "An error that names nothing says nothing")]
        [TestCase("item-not-found",           "wait",    PepLookup.Unknown,
                  TestName = "Try again later is not a statement about what is there")]
        public void WhatARefusalSays(String Condition, String Type, PepLookup Expected)
        {

            Assert.That(PepAnswer.WhatItSays(ErrorIq(Condition, Type)),
                        Is.EqualTo(Expected));

        }

        #endregion

        #region AnErrorWithNoErrorInIt_SaysNothing()

        /// <summary>
        /// A stanza of type error whose error element cannot be read.
        /// </summary>
        /// <remarks>
        /// Not a shape any server ought to send, which is the reason to decide
        /// about it here rather than let the fallthrough decide: an
        /// unrecognisable answer is one this side learnt nothing from, and that
        /// is Unknown by the same reasoning as everything else on that side.
        /// </remarks>
        [Test]
        public void AnErrorWithNoErrorInIt_SaysNothing()
        {

            Assert.That(PepAnswer.WhatItSays(
                            XElement.Parse("<iq xmlns='jabber:client' type='error' id='pep-1' " +
                                           "from='bob@example.org'/>")),
                        Is.EqualTo(PepLookup.Unknown));

        }

        #endregion

    }

}
