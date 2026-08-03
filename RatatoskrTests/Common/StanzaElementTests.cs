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
    /// The reader the switch for incoming frames hangs on.
    /// </summary>
    /// <remarks>
    /// Checked without a server, and that is no economising: the questions here
    /// are questions to a string. A fixture with a server could answer them too,
    /// but only by the detour of an effect — and where no detour is needed, it
    /// only covers up what is being measured.
    /// </remarks>
    [TestFixture]
    public class StanzaElementTests
    {

        #region NameOf_ReadsTheNameToItsEnd()

        /// <summary>
        /// The name ends at the first character that no longer belongs to it.
        /// </summary>
        /// <remarks>
        /// The case that set this whole thing off stands in the middle:
        /// <c>&lt;presence-probe/&gt;</c> is called <c>presence-probe</c> and not
        /// <c>presence</c>. The hyphen belongs to the name (XML 1.0,
        /// section 2.3), and whoever does not read it along makes another
        /// element out of it.
        /// </remarks>
        [Test]
        [TestCase("<iq/>",                     "iq")]
        [TestCase("<iq type='get' id='x'/>",   "iq")]
        [TestCase("<iq>text</iq>",             "iq")]
        [TestCase("<iqbogus/>",                "iqbogus")]
        [TestCase("<presence-probe/>",         "presence-probe")]
        [TestCase("<messages/>",               "messages")]
        [TestCase("<opencast/>",               "opencast")]
        [TestCase("<a_b/>",                    "a_b")]
        [TestCase("<a1/>",                     "a1")]
        public void NameOf_ReadsTheNameToItsEnd(String xml, String expected)
        {
            Assert.That(StanzaElement.NameOf(xml), Is.EqualTo(expected));
        }

        #endregion

        #region NameOf_DropsTheNamespacePrefix()

        /// <summary>
        /// The prefix does not belong to the type: <c>&lt;client:iq/&gt;</c> is
        /// an <c>iq</c>.
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 4.8.1 lays down the namespace, not the abbreviation
        /// it is addressed under. A server that founders on the prefix founders
        /// on a freedom the RFC expressly leaves — and two counterparts make
        /// different use of it: <c>&lt;stream:features/&gt;</c> and
        /// <c>&lt;features/&gt;</c> are the same element.
        /// </remarks>
        [Test]
        [TestCase("<client:iq/>",        "iq")]
        [TestCase("<stream:features/>",  "features")]
        [TestCase("<db:result/>",        "result")]
        public void NameOf_DropsTheNamespacePrefix(String xml, String expected)
        {
            Assert.That(StanzaElement.NameOf(xml), Is.EqualTo(expected));
        }

        #endregion

        #region NameOf_SkipsLeadingWhitespace()

        /// <summary>
        /// Leading whitespace before the element is passed over.
        /// </summary>
        /// <remarks>
        /// Over WebSocket a frame usually comes without any, but over TCP the
        /// splitter stands before a stream in which whitespace is permitted as a
        /// keepalive (RFC 6120, section 4.6.1). A reader that founders on that
        /// would founder on a space.
        /// </remarks>
        [Test]
        [TestCase(" <iq/>")]
        [TestCase("\r\n\t<iq/>")]
        public void NameOf_SkipsLeadingWhitespace(String xml)
        {
            Assert.That(StanzaElement.NameOf(xml), Is.EqualTo("iq"));
        }

        #endregion

        #region NameOf_HasNoNameWithoutAnElement()

        /// <summary>
        /// What begins with no element has no name — and must not invent one.
        /// </summary>
        /// <remarks>
        /// <c>&lt;/iq&gt;</c> stands there expressly: a closing element is no
        /// element that arrives. Without this distinction a switch would take a
        /// <c>&lt;/stream:stream&gt;</c> for a stream that begins.
        /// </remarks>
        [Test]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("no XML")]
        [TestCase("<>")]
        [TestCase("</iq>")]
        public void NameOf_HasNoNameWithoutAnElement(String xml)
        {
            Assert.That(StanzaElement.NameOf(xml), Is.Null);
        }

        #endregion

        #region IsStanza_KnowsTheThreeFromSection81()

        /// <summary>
        /// RFC 6120, section 8.1 knows three stanzas and no fourth.
        /// </summary>
        [Test]
        [TestCase("<message/>",        true)]
        [TestCase("<presence/>",       true)]
        [TestCase("<iq/>",             true)]
        [TestCase("<client:message/>", true)]
        [TestCase("<iqbogus/>",        false)]
        [TestCase("<messages/>",       false)]
        [TestCase("<presence-probe/>", false)]
        [TestCase("<open/>",           false)]
        [TestCase("<r/>",              false)]
        [TestCase("no XML",            false)]
        public void IsStanza_KnowsTheThreeFromSection81(String xml, Boolean expected)
        {
            Assert.That(StanzaElement.IsStanza(xml), Is.EqualTo(expected));
        }

        #endregion

        #region Is_ComparesTheWholeName()

        /// <summary>
        /// <c>Is</c> compares the whole name and not its beginning.
        /// </summary>
        [Test]
        [TestCase("<open xmlns='urn:ietf:params:xml:ns:xmpp-framing'/>", "open",  true)]
        [TestCase("<opencast/>",                                         "open",  false)]
        [TestCase("<close/>",                                            "close", true)]
        [TestCase("<closet/>",                                           "close", false)]
        [TestCase("<r xmlns='urn:xmpp:sm:3'/>",                          "r",     true)]
        [TestCase("<resume xmlns='urn:xmpp:sm:3'/>",                     "r",     false)]
        [TestCase("<a xmlns='urn:xmpp:sm:3' h='1'/>",                    "a",     true)]
        [TestCase("<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl'/>",    "a",     false)]
        public void Is_ComparesTheWholeName(String xml, String name, Boolean expected)
        {
            Assert.That(StanzaElement.Is(xml, name), Is.EqualTo(expected));
        }

        #endregion

    }

}
