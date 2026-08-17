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

using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// The splitter that makes single frames out of the TCP character stream of
    /// an XMPP stream.
    /// </summary>
    /// <remarks>
    /// These tests are the reason why the splitter is a building block of its
    /// own and not a handle in the receive loop: over localhost the packets fall
    /// on element boundaries almost always by chance, so a faulty splitter would
    /// never show there. That is why the splitting here is done on purpose at
    /// the most inconvenient places - in the middle of a tag, in the middle of
    /// an attribute value, character by character.
    /// </remarks>
    [TestFixture]
    public class XmlStreamSplitterTests
    {

        #region Data

        private const String Header =
            "<stream:stream xmlns='jabber:server' " +
            "xmlns:stream='http://etherx.jabber.org/streams' " +
            "from='left.example' to='right.example' id='abc' version='1.0'>";

        #endregion

        #region Helper functions

        /// <summary>
        /// Pushes the text in in one piece.
        /// </summary>
        private static List<String> All(String text)
            => [.. new XmlStreamSplitter().Push(text)];

        /// <summary>
        /// Pushes the text in character by character.
        /// </summary>
        private static List<String> CharacterByCharacter(String text)
        {

            var splitter  = new XmlStreamSplitter();
            var frames    = new List<String>();

            foreach (var c in text)
                frames.AddRange(splitter.Push(c.ToString()));

            return frames;

        }

        #endregion


        #region TheStreamHeaderComesOutOnItsOwn()

        /// <summary>
        /// The stream header is an open tag - it must not wait for its closing
        /// one, otherwise no frame would ever come out.
        /// </summary>
        [Test]
        public void TheStreamHeaderComesOutOnItsOwn()
        {

            var frames = All(Header);

            Assert.Multiple(() =>
            {
                Assert.That(frames, Has.Count.EqualTo(1));
                Assert.That(frames[0], Is.EqualTo(Header));
            });

        }

        #endregion

        #region StanzasAfterTheHeaderComeOutOneByOne()

        [Test]
        public void StanzasAfterTheHeaderComeOutOneByOne()
        {

            var frames = All(Header +
                               "<message from='a@left.example' to='b@right.example'><body>one</body></message>" +
                               "<presence from='a@left.example'/>");

            Assert.Multiple(() =>
            {
                Assert.That(frames, Has.Count.EqualTo(3));
                Assert.That(frames[1], Does.Contain("one").And.StartWith("<message"));
                Assert.That(frames[2], Is.EqualTo("<presence from='a@left.example'/>"));
            });

        }

        #endregion

        #region AStanzaSplitAcrossReads_IsStillOneFrame()

        /// <summary>
        /// The actual point: TCP knows no message boundaries.
        /// </summary>
        [Test]
        public void AStanzaSplitAcrossReads_IsStillOneFrame()
        {

            var splitter = new XmlStreamSplitter();

            Assert.That(splitter.Push(Header), Has.Count.EqualTo(1));

            // Split in the middle of the tag.
            Assert.That(splitter.Push("<message from='a@left.exa"), Is.Empty);
            Assert.That(splitter.Push("mple' to='b@right.example'><bo"), Is.Empty);
            Assert.That(splitter.Push("dy>two</body>"), Is.Empty);

            var finished = splitter.Push("</message>");

            Assert.Multiple(() =>
            {
                Assert.That(finished, Has.Count.EqualTo(1));
                Assert.That(finished[0], Does.Contain("two"));
                Assert.That(finished[0], Does.StartWith("<message"));
                Assert.That(finished[0], Does.EndWith("</message>"));
            });

        }

        #endregion

        #region CharacterByCharacter_YieldsTheSameFrames()

        /// <summary>
        /// The sharpest counter-check: every single character a read.
        /// </summary>
        /// <remarks>
        /// Whoever accidentally bases the splitter on whole reads instead of on
        /// carried-along state founders here and almost nowhere else.
        /// </remarks>
        [Test]
        public void CharacterByCharacter_YieldsTheSameFrames()
        {

            var stream = Header +
                        "<message to='b@right.example'><body>three</body></message>" +
                        "<iq type='get' id='1'><ping xmlns='urn:xmpp:ping'/></iq>" +
                        "</stream:stream>";

            Assert.That(CharacterByCharacter(stream), Is.EqualTo(All(stream)));

        }

        #endregion

        #region SeveralStanzasInOneRead_AreSeparated()

        /// <summary>
        /// And the other direction: several stanzas in a single read.
        /// </summary>
        [Test]
        public void SeveralStanzasInOneRead_AreSeparated()
        {

            var splitter = new XmlStreamSplitter();
            splitter.Push(Header);

            var frames = splitter.Push("<presence/><presence type='unavailable'/><message/>");

            Assert.That(frames, Has.Count.EqualTo(3));

        }

        #endregion

        #region AGreaterThanInsideAnAttribute_DoesNotEndTheTag()

        /// <summary>
        /// A <c>&gt;</c> in an attribute value is valid XML and does not end the
        /// tag.
        /// </summary>
        /// <remarks>
        /// The <b>self-closing</b> tag is the carrying case here, and that is no
        /// detail: with an ordinary element a missing handling of quotation
        /// marks does not show, because the element boundary at the end stays
        /// the same - the closing tag brings the depth to zero either way. Only
        /// when the <c>/&gt;</c> is overlooked does the splitter count one level
        /// too many and never deliver the frame. A first version of this test
        /// checked only the ordinary case and survived the mutation.
        /// </remarks>
        [Test]
        public void AGreaterThanInsideAnAttribute_DoesNotEndTheTag()
        {

            var splitter = new XmlStreamSplitter();
            splitter.Push(Header);

            var selfClosing = "<presence status='a>b'/>";
            var ordinary       = "<message subject='a &gt; b' id='x>y'><body>four</body></message>";

            var frames = splitter.Push(selfClosing + ordinary);

            Assert.Multiple(() =>
            {
                Assert.That(frames,     Has.Count.EqualTo(2));
                Assert.That(frames[0],  Is.EqualTo(selfClosing));
                Assert.That(frames[1],  Is.EqualTo(ordinary));
            });

        }

        #endregion

        #region ATagInsideCData_IsNotAnElement()

        /// <summary>
        /// In CDATA anything may stand, including something that looks like a
        /// tag.
        /// </summary>
        [Test]
        public void ATagInsideCData_IsNotAnElement()
        {

            var splitter = new XmlStreamSplitter();
            splitter.Push(Header);

            var stanza  = "<message><body><![CDATA[</message><evil/>]]></body></message>";
            var frames  = splitter.Push(stanza);

            Assert.Multiple(() =>
            {
                Assert.That(frames, Has.Count.EqualTo(1));
                Assert.That(frames[0], Is.EqualTo(stanza));
            });

        }

        #endregion

        #region NestedElementsOfTheSameName_AreCountedCorrectly()

        /// <summary>
        /// Nested elements of the same name - the closing tag of the inner one
        /// does not end the outer one.
        /// </summary>
        /// <remarks>
        /// XEP-0280 carbons and XEP-0297 forwarding nest
        /// <c>&lt;message/&gt;</c> inside each other in exactly this way; that
        /// is no constructed case.
        /// </remarks>
        [Test]
        public void NestedElementsOfTheSameName_AreCountedCorrectly()
        {

            var splitter = new XmlStreamSplitter();
            splitter.Push(Header);

            var stanza = "<message><sent xmlns='urn:xmpp:carbons:2'><forwarded>" +
                         "<message><body>inside</body></message>" +
                         "</forwarded></sent></message>";

            var frames = splitter.Push(stanza);

            Assert.Multiple(() =>
            {
                Assert.That(frames, Has.Count.EqualTo(1));
                Assert.That(frames[0], Is.EqualTo(stanza));
            });

        }

        #endregion

        #region TheXmlDeclaration_IsNotMistakenForTheHeader()

        /// <summary>
        /// Some servers send an XML declaration ahead. It is no element and must
        /// not pass as the stream header.
        /// </summary>
        [Test]
        public void TheXmlDeclaration_IsNotMistakenForTheHeader()
        {

            var frames = All("<?xml version='1.0'?>" + Header + "<presence/>");

            Assert.Multiple(() =>
            {
                Assert.That(frames, Has.Count.EqualTo(2));
                Assert.That(frames[0], Is.EqualTo(Header));
                Assert.That(frames[1], Is.EqualTo("<presence/>"));
            });

        }

        #endregion

        #region WhitespaceBetweenStanzas_IsNotAFrame()

        /// <summary>
        /// Between stanzas whitespace often stands - among other things as a
        /// keepalive. It yields no frame.
        /// </summary>
        [Test]
        public void WhitespaceBetweenStanzas_IsNotAFrame()
        {

            var splitter = new XmlStreamSplitter();
            splitter.Push(Header);

            Assert.Multiple(() =>
            {
                Assert.That(splitter.Push("\n  \t "), Is.Empty);
                Assert.That(splitter.Push("  <presence/>\n"), Has.Count.EqualTo(1));
            });

        }

        #endregion

        #region TheClosingStreamTag_IsItsOwnFrame()

        /// <summary>
        /// The end of the stream has to arrive up top, otherwise nobody notices
        /// that the counterpart has finished properly.
        /// </summary>
        [Test]
        public void TheClosingStreamTag_IsItsOwnFrame()
        {

            var frames = All(Header + "<presence/></stream:stream>");

            Assert.Multiple(() =>
            {
                Assert.That(frames, Has.Count.EqualTo(3));
                Assert.That(frames[2], Is.EqualTo("</stream:stream>"));
            });

        }

        #endregion

        #region AnIncompleteStanza_YieldsNothingYet()

        /// <summary>
        /// What is half received is held back, not half delivered.
        /// </summary>
        [Test]
        public void AnIncompleteStanza_YieldsNothingYet()
        {

            var splitter = new XmlStreamSplitter();
            splitter.Push(Header);

            Assert.That(splitter.Push("<message><body>unfinis"), Is.Empty);

        }

        #endregion

    }

}
