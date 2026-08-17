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
    /// The negotiation of the stream in valid but unusual spelling.
    ///
    /// It ran over text patterns until recently:
    /// <c>&lt;mechanism&gt;…&lt;/mechanism&gt;</c> without attributes,
    /// <c>xmlns</c> as the first attribute, <c>Contains</c> on the whole frame.
    /// These tests record everything a server may send without the connection
    /// setup grasping at the wrong thing.
    /// </summary>
    [TestFixture]
    public class StreamNegotiationTests
    {

        #region Helper functions

        private static XElement Features(String inner)
            => XElement.Parse("<stream:features xmlns:stream='http://etherx.jabber.org/streams'>" +
                              inner +
                              "</stream:features>");

        #endregion


        #region SaslMechanisms_ReadsIndentedElements()

        /// <summary>
        /// A server that indents its features writes the name of the mechanism
        /// with a line break and spaces around it. The earlier pattern gave it
        /// back uncut, and the comparison against "PLAIN" that followed came to
        /// nothing - the client took the server for one without SASL.
        /// </summary>
        [Test]
        public void SaslMechanisms_ReadsIndentedElements()
        {

            var features = Features("<mechanisms xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>\n" +
                                    "    <mechanism>\n      SCRAM-SHA-256\n    </mechanism>\n" +
                                    "    <mechanism>\n      PLAIN\n    </mechanism>\n" +
                                    "  </mechanisms>");

            Assert.That(StreamNegotiation.SaslMechanisms(features),
                        Is.EqualTo(new[] { "SCRAM-SHA-256", "PLAIN" }));

        }

        #endregion

        #region SaslMechanisms_ReadsRepeatedNamespaceDeclaration()

        /// <summary>
        /// The namespace may be repeated on the child element - superfluous but
        /// valid, and that is exactly how some libraries serialise it. The
        /// earlier pattern demanded a <c>&lt;mechanism&gt;</c> with no
        /// attributes at all and then found nothing whatsoever.
        /// </summary>
        [Test]
        public void SaslMechanisms_ReadsRepeatedNamespaceDeclaration()
        {

            var features = Features("<mechanisms xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>" +
                                    "<mechanism xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>PLAIN</mechanism>" +
                                    "</mechanisms>");

            Assert.That(StreamNegotiation.SaslMechanisms(features), Is.EqualTo(new[] { "PLAIN" }));

        }

        #endregion

        #region SaslMechanisms_IgnoresMechanismsOfAnotherFeature()

        /// <summary>
        /// The search runs inside the <c>&lt;mechanisms/&gt;</c> of SASL, not
        /// somewhere in the frame. An element of the same name belonging to
        /// another extension - the mechanism list of an encryption layer, say -
        /// must not get into the choice, otherwise the client tries a mechanism
        /// the server never offered for SASL.
        /// </summary>
        [Test]
        public void SaslMechanisms_IgnoresMechanismsOfAnotherFeature()
        {

            var features = Features("<mechanisms xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>" +
                                    "<mechanism>PLAIN</mechanism></mechanisms>" +
                                    "<mechanisms xmlns='urn:example:something-else'>" +
                                    "<mechanism>MAGIC</mechanism></mechanisms>");

            Assert.That(StreamNegotiation.SaslMechanisms(features), Is.EqualTo(new[] { "PLAIN" }));

        }

        #endregion

        #region FeatureNamespaces_FindsTrailingXmlns()

        /// <summary>
        /// The earlier pattern demanded <c>xmlns</c> as the first attribute.
        /// The BCL serialises it as the last, though, and XML prescribes no
        /// order - such features were missing from the list, and the server
        /// looked less capable than it is.
        /// </summary>
        [Test]
        public void FeatureNamespaces_FindsTrailingXmlns()
        {

            var features = Features("<c hash='sha-1' node='http://example.org/srv' ver='abc='" +
                                    " xmlns='http://jabber.org/protocol/caps'/>");

            Assert.That(StreamNegotiation.FeatureNamespaces(features),
                        Does.Contain("http://jabber.org/protocol/caps"));

        }

        #endregion

        #region FeatureNamespaces_IgnoresNestedElements()

        /// <summary>
        /// A feature is announced by a <b>direct</b> child of
        /// <c>&lt;features/&gt;</c>. The earlier pattern searched the whole text
        /// and took up namespaces from inside a feature as well - the client
        /// then took the server to be capable of something that occurred there
        /// only as a detail.
        /// </summary>
        [Test]
        public void FeatureNamespaces_IgnoresNestedElements()
        {

            var features = Features("<mechanisms xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>" +
                                    "<hint xmlns='urn:example:inside'/>" +
                                    "</mechanisms>");

            Assert.That(StreamNegotiation.FeatureNamespaces(features),
                        Is.EqualTo(new[] { "urn:ietf:params:xml:ns:xmpp-sasl" }));

        }

        #endregion

        #region RequiresSession_IgnoresTheOptionalOfAnotherFeature()

        /// <summary>
        /// The heart of the fault: <c>&lt;optional/&gt;</c> belongs to exactly
        /// one feature at a time. XEP-0198 puts it into its own, and the earlier
        /// check <c>!Contains("optional")</c> read that as a statement about the
        /// session - a server that announces both never got the required session
        /// asked for.
        /// </summary>
        [Test]
        public void RequiresSession_IgnoresTheOptionalOfAnotherFeature()
        {

            var features = Features("<session xmlns='urn:ietf:params:xml:ns:xmpp-session'/>" +
                                    "<sm xmlns='urn:xmpp:sm:3'><optional/></sm>");

            Assert.That(StreamNegotiation.RequiresSession(features), Is.True);

        }

        #endregion

        #region RequiresSession_False_WhenTheSessionItselfIsOptional()

        /// <summary>
        /// The counter-check: its own <c>&lt;optional/&gt;</c> counts.
        /// </summary>
        [Test]
        public void RequiresSession_False_WhenTheSessionItselfIsOptional()
        {

            var features = Features("<session xmlns='urn:ietf:params:xml:ns:xmpp-session'><optional/></session>");

            Assert.That(StreamNegotiation.RequiresSession(features), Is.False);

        }

        #endregion

        #region OffersBind_AcceptsAPrefixedNamespace()

        /// <summary>
        /// Whether the server sets the bind namespace as the default or binds it
        /// through a prefix is its own affair. The earlier check
        /// <c>Contains("&lt;bind")</c> did not match a <c>&lt;b:bind/&gt;</c> -
        /// the client would have skipped the binding and carried on without a
        /// resource.
        /// </summary>
        [Test]
        public void OffersBind_AcceptsAPrefixedNamespace()
        {

            var features = Features("<b:bind xmlns:b='urn:ietf:params:xml:ns:xmpp-bind'/>");

            Assert.That(StreamNegotiation.OffersBind(features), Is.True);

        }

        #endregion

        #region ReadBoundJid_ResolvesEntities()

        /// <summary>
        /// The earlier grab with <c>&lt;jid&gt;([^&lt;]+)&lt;/jid&gt;</c>
        /// fetched the raw text: <c>a&amp;amp;b</c> did not become
        /// <c>a&amp;b</c>. The client would have reported itself from then on
        /// with a JID that does not exist in that form.
        /// </summary>
        [Test]
        public void ReadBoundJid_ResolvesEntities()
        {

            var iq = XElement.Parse("<iq type='result' id='bind1'>" +
                                    "<bind xmlns='urn:ietf:params:xml:ns:xmpp-bind'>" +
                                    "<jid>a&amp;b@example.org/console</jid>" +
                                    "</bind></iq>");

            Assert.That(StreamNegotiation.ReadBoundJid(iq), Is.EqualTo("a&b@example.org/console"));

        }

        #endregion

        #region ReadBoundJid_TrimsSurroundingWhitespace()

        /// <summary>
        /// Here too the indenting strikes: the JID must not be carried on with
        /// line breaks in the name.
        /// </summary>
        [Test]
        public void ReadBoundJid_TrimsSurroundingWhitespace()
        {

            var iq = XElement.Parse("<iq type='result' id='bind1'>\n" +
                                    "  <bind xmlns='urn:ietf:params:xml:ns:xmpp-bind'>\n" +
                                    "    <jid>\n      alice@example.org/console\n    </jid>\n" +
                                    "  </bind>\n</iq>");

            Assert.That(StreamNegotiation.ReadBoundJid(iq), Is.EqualTo("alice@example.org/console"));

        }

        #endregion

        #region ReadBoundJid_ReturnsNullForARejection()

        /// <summary>
        /// A refused binding has to be recognisable as a refusal. The client
        /// used to fall back on the JID it had wished for itself and reported
        /// itself online with a resource it was never assigned.
        /// </summary>
        [Test]
        public void ReadBoundJid_ReturnsNullForARejection()
        {

            var iq = XElement.Parse("<iq type='error' id='bind1'><error type='cancel'>" +
                                    "<not-allowed xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                                    "</error></iq>");

            Assert.That(StreamNegotiation.ReadBoundJid(iq), Is.Null);

        }

        #endregion

        #region ReadBoundJid_IgnoresAJidOfAnotherPayload()

        /// <summary>
        /// The JID has to come from the <c>&lt;bind/&gt;</c>. The text search
        /// found every <c>&lt;jid/&gt;</c> in the frame - including one
        /// belonging to a quite different payload.
        /// </summary>
        [Test]
        public void ReadBoundJid_IgnoresAJidOfAnotherPayload()
        {

            var iq = XElement.Parse("<iq type='result' id='bind1'>" +
                                    "<query xmlns='urn:example:something-else'>" +
                                    "<jid>foreign@example.org/x</jid></query></iq>");

            Assert.That(StreamNegotiation.ReadBoundJid(iq), Is.Null);

        }

        #endregion

        #region IsSasl_ChecksTheNamespace()

        /// <summary>
        /// <c>&lt;success/&gt;</c> is a common element name. Only one from the
        /// SASL namespace ends the authentication; the earlier search for the
        /// character sequence <c>"&lt;success"</c> in the raw text would have
        /// accepted any other one too.
        /// </summary>
        [Test]
        public void IsSasl_ChecksTheNamespace()
        {

            var foreign = XElement.Parse("<success xmlns='urn:example:something-else'/>");
            var real  = XElement.Parse("<success xmlns='urn:ietf:params:xml:ns:xmpp-sasl'/>");

            Assert.Multiple(() =>
            {
                Assert.That(StreamNegotiation.IsSasl(foreign, "success"), Is.False);
                Assert.That(StreamNegotiation.IsSasl(real,  "success"), Is.True);
            });

        }

        #endregion

        #region SaslPayload_IsEmptyWithoutAServerFinalMessage()

        /// <summary>
        /// The ground the SCRAM check stands on: a <c>&lt;success/&gt;</c>
        /// without content carries no server-final-message. Under RFC 5802,
        /// section 5 the signature is thereby not checkable - the connection
        /// setup now breaks off there instead of dropping the mutual
        /// authentication in silence.
        /// </summary>
        [Test]
        public void SaslPayload_IsEmptyWithoutAServerFinalMessage()
        {

            var empty   = XElement.Parse("<success xmlns='urn:ietf:params:xml:ns:xmpp-sasl'/>");
            var filled = XElement.Parse("<success xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>\n" +
                                          "  dj1yUnRDMXBUUw==\n</success>");

            Assert.Multiple(() =>
            {
                Assert.That(StreamNegotiation.SaslPayload(empty),     Is.Empty);
                Assert.That(StreamNegotiation.SaslPayload(filled), Is.EqualTo("dj1yUnRDMXBUUw=="));
            });

        }

        #endregion

        #region SaslFailureCondition_SkipsTheTextElement()

        /// <summary>
        /// RFC 6120, section 6.5 allows an explanatory <c>&lt;text/&gt;</c>
        /// beside the condition without laying down the order. What belongs
        /// reported is the condition, not the explanatory text.
        /// </summary>
        [Test]
        public void SaslFailureCondition_SkipsTheTextElement()
        {

            var failure = XElement.Parse("<failure xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>" +
                                         "<text>Wrong password</text>" +
                                         "<not-authorized/></failure>");

            Assert.That(StreamNegotiation.SaslFailureCondition(failure), Is.EqualTo("not-authorized"));

        }

        #endregion

    }

}
