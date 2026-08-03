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
    /// XEP-0198: the two building blocks of the counting without a network -
    /// what counts as a stanza, and when a sequence number counts as
    /// acknowledged.
    /// </summary>
    [TestFixture]
    public class StanzaCountingTests
    {

        #region Stanzas_AreCounted()

        /// <summary>
        /// XEP-0198 section 2 counts exactly message, presence and iq.
        /// </summary>
        [Test]
        [TestCase("<message to='a@b' type='chat'><body>x</body></message>")]
        [TestCase("<presence/>")]
        [TestCase("<iq type='get' id='1'><ping xmlns='urn:xmpp:ping'/></iq>")]
        [TestCase("<message/>")]
        [TestCase("  <presence type='unavailable'/>")]
        [TestCase("<client:message xmlns:client='jabber:client'/>")]
        public void Stanzas_AreCounted(String xml)
        {
            Assert.That(StreamManagementManager.IsCountableStanza(xml), Is.True);
        }

        #endregion

        #region Nonzas_AreNotCounted()

        /// <summary>
        /// Nonzas do not count. Especially delicate are <c>&lt;a/&gt;</c> and
        /// <c>&lt;r/&gt;</c>: they run over the same sending path as real
        /// stanzas at every keepalive.
        /// </summary>
        [Test]
        [TestCase("<r xmlns='urn:xmpp:sm:3'/>")]
        [TestCase("<a xmlns='urn:xmpp:sm:3' h='12'/>")]
        [TestCase("<enable xmlns='urn:xmpp:sm:3' resume='true'/>")]
        [TestCase("<enabled xmlns='urn:xmpp:sm:3' id='x'/>")]
        [TestCase("<open xmlns='urn:ietf:params:xml:ns:xmpp-framing' to='example.org'/>")]
        [TestCase("<close xmlns='urn:ietf:params:xml:ns:xmpp-framing'/>")]
        [TestCase("<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='PLAIN'>eA==</auth>")]
        [TestCase("<success xmlns='urn:ietf:params:xml:ns:xmpp-sasl'/>")]
        [TestCase("<stream:features xmlns:stream='http://etherx.jabber.org/streams'/>")]
        [TestCase("")]
        [TestCase("not XML")]
        public void Nonzas_AreNotCounted(String xml)
        {
            Assert.That(StreamManagementManager.IsCountableStanza(xml), Is.False);
        }

        #endregion

        #region ElementsWithStanzaPrefix_AreNotCounted()

        /// <summary>
        /// A mere prefix comparison such as <c>StartsWith("&lt;a")</c> would
        /// match <c>&lt;auth/&gt;</c> as well. The element name has to agree in
        /// full.
        /// </summary>
        [Test]
        [TestCase("<iqbogus/>")]
        [TestCase("<messages/>")]
        [TestCase("<presence-probe/>")]
        public void ElementsWithStanzaPrefix_AreNotCounted(String xml)
        {
            Assert.That(StreamManagementManager.IsCountableStanza(xml), Is.False);
        }

        #endregion

        #region TheServerCountsTheSameThings()

        /// <summary>
        /// The same question on the server side — and the same answer.
        /// </summary>
        /// <remarks>
        /// <see cref="XMPPSession.IsStanza"/> is deliberately implemented
        /// independently: if both sides used the same helper, the tests that
        /// hold the two counters against each other would be checking both sides
        /// with the same logic, and a shared error of thought would stay
        /// undiscovered.
        ///
        /// Independent does not mean unchecked, though. Until D26 the server
        /// side compared prefixes: <c>&lt;iqbogus/&gt;</c> counted there and did
        /// not at the client — of all things the two counters that have to run
        /// alike would have drifted apart, and the other end would have taken
        /// the <c>h</c> for a protocol violation. This test holds the two to the
        /// same answer without forcing them onto the same way.
        /// </remarks>
        [Test]
        [TestCase("<message/>",        true)]
        [TestCase("<presence/>",       true)]
        [TestCase("<iq/>",             true)]
        [TestCase("<iq type='get'/>",  true)]
        [TestCase("<client:iq/>",      true)]
        [TestCase("<iqbogus/>",        false)]
        [TestCase("<messages/>",       false)]
        [TestCase("<presence-probe/>", false)]
        [TestCase("<r xmlns='urn:xmpp:sm:3'/>", false)]
        public void TheServerCountsTheSameThings(String xml, Boolean expected)
        {

            Assert.Multiple(() =>
            {

                Assert.That(XMPPSession.IsStanza(xml),
                            Is.EqualTo(expected),
                            "server side");

                Assert.That(StreamManagementManager.IsCountableStanza(xml), Is.EqualTo(expected),
                            "client side");

            });

        }

        #endregion

        #region Acknowledgement_UsesModuloArithmetic()

        /// <summary>
        /// The counter is a 32-bit value that overflows to 0 after 2^32-1
        /// (XEP-0198, section 4). A simple <c>Seq &lt;= h</c> would leave the
        /// stanzas still outstanding right after the overflow lying in the
        /// queue for ever.
        /// </summary>
        [Test]
        [TestCase(1u,          1u,          true,  TestName = "Acknowledged exactly")]
        [TestCase(1u,          5u,          true,  TestName = "Older than h")]
        [TestCase(5u,          1u,          false, TestName = "Newer than h")]
        [TestCase(5u,          4u,          false, TestName = "One too new")]
        [TestCase(UInt32.MaxValue, 1u,      true,  TestName = "Overflow: h has turned over")]
        [TestCase(UInt32.MaxValue, 0u,      true,  TestName = "Overflow: h exactly at 0")]
        [TestCase(1u,          UInt32.MaxValue, false, TestName = "h lies far back")]
        public void Acknowledgement_UsesModuloArithmetic(UInt32 seq, UInt32 h, Boolean expected)
        {
            Assert.That(StreamManagementManager.IsAcknowledged(seq, h), Is.EqualTo(expected));
        }

        #endregion

        #region LastAcknowledged_IsTheirNumber_NotOurs()

        /// <summary>
        /// <c>LastAcknowledged</c> reports what the counterpart has counted -
        /// not what we have counted.
        /// </summary>
        /// <remarks>
        /// The distinction is the whole purpose of the property: the run against
        /// a foreign server compares it with <c>OutboundCount</c> in order to
        /// tell agreement from mere toleration. If it gave back our own counter,
        /// that comparison would always add up and the run would check nothing.
        ///
        /// Hence an <c>h</c> here that lies beside our own state on purpose:
        /// nothing was sent, seven are acknowledged.
        /// </remarks>
        [Test]
        public void LastAcknowledged_IsTheirNumber_NotOurs()
        {

            var manager = new StreamManagementManager(_ => Task.CompletedTask);

            manager.ProcessEnabled("<enabled xmlns='urn:xmpp:sm:3'/>");
            manager.ProcessAck("<a xmlns='urn:xmpp:sm:3' h='7'/>");

            Assert.Multiple(() =>
            {
                Assert.That(manager.LastAcknowledged, Is.EqualTo(7u));
                Assert.That(manager.OutboundCount,    Is.Zero);
            });

        }

        #endregion

    }

}
