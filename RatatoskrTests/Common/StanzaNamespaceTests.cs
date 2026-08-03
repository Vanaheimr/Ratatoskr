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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// The content namespace of a stanza (RFC 6120, section 4.8.1).
    /// </summary>
    /// <remarks>
    /// Two errors that both became visible only against Prosody and both have
    /// the same cause: a stanza did not carry the namespace the stream under it
    /// expected.
    ///
    /// Over TCP that never shows, because the namespace stands once at the
    /// <c>&lt;stream:stream&gt;</c> and holds for everything in it. Over
    /// WebSocket this element does not exist (RFC 7395, section 3.3.3), and
    /// across the domain boundary it changes from <c>jabber:client</c> to
    /// <c>jabber:server</c>.
    /// </remarks>
    [TestFixture]
    public class StanzaNamespaceTests
    {

        #region Apply_StampsStanzasThatCarryNone()

        /// <summary>
        /// A stanza without a namespace of its own gets one.
        /// </summary>
        /// <remarks>
        /// Our own server never objected to that, because it recognises stanzas
        /// by the local name. Prosody did: it answered the bind IQ with
        /// <c>&lt;unsupported-stanza-type/&gt;</c> and closed the stream. With
        /// that the client could sign on at no RFC 7395 conformant server.
        /// </remarks>
        [Test]
        public void Apply_StampsStanzasThatCarryNone()
        {

            Assert.Multiple(() =>
            {

                Assert.That(StanzaNamespace.Apply("<presence/>", StanzaNamespace.Client),
                            Is.EqualTo("<presence xmlns='jabber:client'/>"));

                Assert.That(StanzaNamespace.Apply(
                                "<message to='bob@example.com' type='chat'><body>Hello</body></message>",
                                StanzaNamespace.Client),
                            Is.EqualTo("<message xmlns='jabber:client' to='bob@example.com' " +
                                       "type='chat'><body>Hello</body></message>"));

                // The namespace of the child element is not that of the stanza -
                // precisely the confusion a "there is an xmlns somewhere in
                // there" would founder on.
                Assert.That(StanzaNamespace.Apply(
                                "<iq type='set' id='bind1'>" +
                                "<bind xmlns='urn:ietf:params:xml:ns:xmpp-bind'/></iq>",
                                StanzaNamespace.Client),
                            Is.EqualTo("<iq xmlns='jabber:client' type='set' id='bind1'>" +
                                       "<bind xmlns='urn:ietf:params:xml:ns:xmpp-bind'/></iq>"));

            });

        }

        #endregion

        #region Apply_ExchangesTheNamespaceAtTheDomainBoundary()

        /// <summary>
        /// What came from a client goes out as <c>jabber:server</c>.
        /// </summary>
        /// <remarks>
        /// The second error, and the client fix was what brought it out: as long
        /// as the stanza carried no namespace at all, it silently inherited the
        /// right one on the S2S stream. With <c>jabber:client</c> in it, it is
        /// no valid stanza there any more - Prosody answered with an error IQ,
        /// and the ping round trip failed.
        /// </remarks>
        [Test]
        public void Apply_ExchangesTheNamespaceAtTheDomainBoundary()
        {

            Assert.Multiple(() =>
            {

                Assert.That(StanzaNamespace.Apply(
                                "<iq xmlns='jabber:client' from='alice@a.example' " +
                                "to='b.example' type='get' id='ping-1'>" +
                                "<ping xmlns='urn:xmpp:ping'/></iq>",
                                StanzaNamespace.Server),
                            Is.EqualTo("<iq xmlns='jabber:server' from='alice@a.example' " +
                                       "to='b.example' type='get' id='ping-1'>" +
                                       "<ping xmlns='urn:xmpp:ping'/></iq>"));

                // In the other spelling of the quotation marks as well.
                Assert.That(StanzaNamespace.Apply(
                                "<message xmlns=\"jabber:client\"><body>x</body></message>",
                                StanzaNamespace.Server),
                            Is.EqualTo("<message xmlns='jabber:server'><body>x</body></message>"));

            });

        }

        #endregion

        #region Apply_LeavesEverythingElseAlone()

        /// <summary>
        /// What is touched is only what is a stanza and is not right yet.
        /// </summary>
        /// <remarks>
        /// Nonzas belong in their own namespace - to hang an
        /// <c>&lt;enable/&gt;</c> over to <c>jabber:client</c> would make it
        /// unreadable. And a stanza that already carries the wanted namespace
        /// would otherwise come back with a second declaration and would be
        /// no well-formed XML any more.
        /// </remarks>
        [Test]
        public void Apply_LeavesEverythingElseAlone()
        {

            var untouched = new[] {
                "<enable xmlns='urn:xmpp:sm:3'/>",
                "<r xmlns='urn:xmpp:sm:3'/>",
                "<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='SCRAM-SHA-1'>abc</auth>",
                "<open xmlns='urn:ietf:params:xml:ns:xmpp-framing' to='example.com' version='1.0'/>",
                "<iq xmlns='jabber:client' type='get' id='ping1'/>",
                "<message xmlns=\"jabber:client\"><body>x</body></message>"
            };

            Assert.Multiple(() =>
            {
                foreach (var xml in untouched)
                    Assert.That(StanzaNamespace.Apply(xml, StanzaNamespace.Client), Is.EqualTo(xml),
                                $"Touched although there was nothing to do: {xml}");
            });

        }

        #endregion

        #region Apply_IsNotFooledByAPrefixDeclaration()

        /// <summary>
        /// <c>xmlns:foo</c> declares no default namespace.
        /// </summary>
        /// <remarks>
        /// A stanza with a prefix declaration and without a default namespace
        /// still stands in none - whoever mixes the two up lets precisely it
        /// through.
        /// </remarks>
        [Test]
        public void Apply_IsNotFooledByAPrefixDeclaration()
        {

            Assert.That(StanzaNamespace.Apply(
                            "<iq xmlns:db='jabber:server:dialback' type='get' id='x'/>",
                            StanzaNamespace.Server),
                        Is.EqualTo("<iq xmlns='jabber:server' xmlns:db='jabber:server:dialback' " +
                                   "type='get' id='x'/>"));

        }

        #endregion

        #region Apply_SurvivesAGreaterThanInsideAnAttribute()

        /// <summary>
        /// A <c>&gt;</c> in an attribute value does not end the start tag.
        /// </summary>
        /// <remarks>
        /// XML demands no escaping of <c>&gt;</c> in attribute values. Whoever
        /// lets the start tag end at the first <c>&gt;</c> looks for the
        /// namespace in half the stanza and puts it in the wrong place.
        /// </remarks>
        [Test]
        public void Apply_SurvivesAGreaterThanInsideAnAttribute()
        {

            Assert.That(StanzaNamespace.Apply("<message id='a>b' xmlns='jabber:client'/>",
                                              StanzaNamespace.Client),
                        Is.EqualTo("<message id='a>b' xmlns='jabber:client'/>"));

        }

        #endregion

    }

}
