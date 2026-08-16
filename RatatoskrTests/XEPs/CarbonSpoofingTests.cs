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
    /// XEP-0280 has exactly one rule: a carbon comes from one's own bare JID and
    /// from nowhere else. These tests are about the path on which it was
    /// missing.
    /// </summary>
    /// <remarks>
    /// An encrypted carbon used to be unwrapped by a branch of its own, before
    /// the carbon check ever ran - the namespace looked for anywhere in the
    /// stanza, the <c>&lt;forwarded/&gt;</c> among all descendants, and no
    /// question at all about the sender. So the one path that decrypted a
    /// wrapped message was the one path with no check on it.
    /// </remarks>
    [TestFixture]
    public class CarbonSpoofingTests
    {

        #region Data & helper functions

        private const String Mine = "alice@example.org";

        private static CarbonManager Manager()
            => new(Mine);

        /// <summary>A carbon as XEP-0280 builds one.</summary>
        private static XElement Carbon(String from, String innerFrom, String kind = "received")
            => XElement.Parse(
                   $"<message from='{from}' to='{Mine}/laptop'>" +
                     $"<{kind} xmlns='{CarbonManager.Namespace}'>" +
                       "<forwarded xmlns='urn:xmpp:forward:0'>" +
                         $"<message from='{innerFrom}' to='{Mine}' type='chat'>" +
                           "<body>inside</body>" +
                         "</message>" +
                       "</forwarded>" +
                     $"</{kind}>" +
                   "</message>");

        #endregion


        #region AGenuineCarbon_IsUnwrapped()

        /// <summary>
        /// The ordinary case: one's own server forwards what one's own other
        /// device wrote, and the inner message comes out.
        /// </summary>
        [Test]
        public void AGenuineCarbon_IsUnwrapped()
        {

            var inner = Manager().UnwrapVerified(Carbon(Mine, "bob@example.com"), Mine);

            Assert.Multiple(() =>
            {
                Assert.That(inner,                     Is.Not.Null);
                Assert.That(inner!.Attr("from"),       Is.EqualTo("bob@example.com"));
                Assert.That(inner.ChildValue("body"),  Is.EqualTo("inside"));
            });

        }

        #endregion

        #region ACarbonFromSomebodyElse_IsNotUnwrapped()

        /// <summary>
        /// The finding itself. A stranger builds a well-formed carbon and puts
        /// whatever they like inside it.
        /// </summary>
        /// <remarks>
        /// Whether the wrapped message would survive further checks is beside
        /// the point. Nothing that came from somebody else's address may be
        /// taken for something one's own server forwarded - that is the whole
        /// of XEP-0280's security section, and it has to hold before anything
        /// inside is looked at.
        /// </remarks>
        [Test]
        public void ACarbonFromSomebodyElse_IsNotUnwrapped()
        {

            Assert.Multiple(() =>
            {

                Assert.That(Manager().UnwrapVerified(Carbon("mallory@example.org", "bob@example.com"),
                                                     "mallory@example.org"),
                            Is.Null);

                // The same local part on another domain is a different person.
                Assert.That(Manager().UnwrapVerified(Carbon("alice@evil.example", "bob@example.com"),
                                                     "alice@evil.example"),
                            Is.Null);

            });

        }

        #endregion

        #region AForwardedElementSomewhereElse_IsNoCarbon()

        /// <summary>
        /// A <c>&lt;forwarded/&gt;</c> that is not sitting under a
        /// <c>&lt;sent/&gt;</c> or <c>&lt;received/&gt;</c> is not a carbon,
        /// however deep in the stanza it lies.
        /// </summary>
        /// <remarks>
        /// The old unwrapping searched all descendants. Anybody could then hang
        /// a forwarded message anywhere inside an ordinary message of their own
        /// - even inside the body - and have its content read as though one's
        /// own server had vouched for it. Walking direct children is what makes
        /// the difference, and the sender check alone would not have caught
        /// this one: the stanza below comes from a genuine contact.
        /// </remarks>
        [Test]
        public void AForwardedElementSomewhereElse_IsNoCarbon()
        {

            var stanza = XElement.Parse(
                             $"<message from='{Mine}' to='{Mine}/laptop'>" +
                               "<body>look at this</body>" +
                               "<quote>" +
                                 "<forwarded xmlns='urn:xmpp:forward:0'>" +
                                   "<message from='mallory@example.com'><body>planted</body></message>" +
                                 "</forwarded>" +
                               "</quote>" +
                             "</message>");

            // From one's own address, so the sender check passes - and it is
            // still not a carbon.
            Assert.That(Manager().UnwrapVerified(stanza, Mine), Is.Null);

        }

        #endregion

        #region ASentCarbon_IsUnwrappedToo()

        /// <summary>
        /// Both directions are carbons. A <c>&lt;sent/&gt;</c> carries what one
        /// of one's own devices wrote, and one's own other device wants to see
        /// it just as much.
        /// </summary>
        [Test]
        public void ASentCarbon_IsUnwrappedToo()
        {

            var inner = Manager().UnwrapVerified(Carbon(Mine, $"{Mine}/phone", "sent"), Mine);

            Assert.That(inner?.Attr("from"), Is.EqualTo($"{Mine}/phone"));

        }

        #endregion

        #region AnEnvelopeWithoutASender_IsRefusedWhereOneIsExpected()

        /// <summary>
        /// XEP-0420 puts the sender inside the encrypted envelope so that it
        /// cannot be changed from outside. Skipping the comparison when the
        /// envelope names nobody handed that decision back to whoever wrote the
        /// envelope.
        /// </summary>
        /// <remarks>
        /// The affix is there against passing-on: somebody catches a ciphertext
        /// and sends it along under their own name. An attacker doing that
        /// leaves the <c>&lt;from/&gt;</c> out, and under the old rule the check
        /// then did not run - it protected against everybody except the one it
        /// was written for.
        /// </remarks>
        [Test]
        public void AnEnvelopeWithoutASender_IsRefusedWhereOneIsExpected()
        {

            var withoutSender = new SceEnvelope([new XElement("body", "hello")]).ToXml();

            Assert.Multiple(() =>
            {

                Assert.That(SceEnvelope.TryRead(withoutSender, out _, "bob@example.com"),
                            Is.False,
                            "Expected a sender, got none - that is not a match.");

                Assert.That(SceEnvelope.TryRead(withoutSender, out var anonymous),
                            Is.True,
                            "Without an expectation the envelope is still readable; the " +
                            "affix is optional in XEP-0420 and only the comparison is not.");

                Assert.That(anonymous!.From, Is.Null);

            });

        }

        #endregion

        #region AnEnvelopeNamingItsSender_IsStillCompared()

        /// <summary>
        /// The counter-check, so that the refusal above is not simply a
        /// refusal of everything.
        /// </summary>
        [Test]
        public void AnEnvelopeNamingItsSender_IsStillCompared()
        {

            var envelope = new SceEnvelope([new XElement("body", "hello")],
                                           From: "bob@example.com").ToXml();

            Assert.Multiple(() =>
            {
                Assert.That(SceEnvelope.TryRead(envelope, out _, "bob@example.com/phone"), Is.True,
                            "Compared bare - another resource of the same account is the same account.");
                Assert.That(SceEnvelope.TryRead(envelope, out _, "mallory@example.com"),   Is.False);
            });

        }

        #endregion

    }

}
