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
    /// The dialback key against the published vector from XEP-0220,
    /// section 2.1.1.
    /// </summary>
    /// <remarks>
    /// The vector is no ornament here but the reason why the implementation is
    /// right. Two obvious readings of the procedure each deliver a key that is
    /// consistent in itself but wrong - <c>SHA256(Secret)</c> as raw bytes
    /// instead of as a hex string, and the domains in reverse order. Both stand
    /// out only against a foreign vector; against our own counter-check either
    /// of them would be green.
    /// </remarks>
    [TestFixture]
    public class DialbackKeyTests
    {

        #region Data

        // XEP-0220, section 2.1.1:
        //
        //   key = HMAC-SHA256(
        //           SHA256('s3cr3tf0rd14lb4ck'),
        //             { 'montague.example', ' ', 'capulet.example', ' ', 'D60000229F' }
        //         )
        //       = b4835385f37fe2895af6c196b59097b16862406db80559900d96bf6fa7d23df3
        //
        // montague.example is the accepting server there (target domain),
        // capulet.example the building one (sender domain).

        private const String Secret        = "s3cr3tf0rd14lb4ck";
        private const String TargetDomain  = "montague.example";
        private const String SenderDomain  = "capulet.example";
        private const String StreamId      = "D60000229F";

        private const String ExpectedKey =
            "b4835385f37fe2895af6c196b59097b16862406db80559900d96bf6fa7d23df3";

        #endregion


        #region TheKeyMatchesThePublishedVector()

        /// <summary>
        /// The vector from the XEP, reproduced exactly.
        /// </summary>
        [Test]
        public void TheKeyMatchesThePublishedVector()
        {

            Assert.That(DialbackKey.Generate(Secret, TargetDomain, SenderDomain, StreamId),
                        Is.EqualTo(ExpectedKey));

        }

        #endregion

        #region TheDomainOrderMatters()

        /// <summary>
        /// Target domain before sender domain. Swapped, a well-formed key would
        /// come out as well - only a different one.
        /// </summary>
        /// <remarks>
        /// What are compared are two <b>generated</b> keys and not one against
        /// the published constant. The difference is not cosmetic: against the
        /// constant such a test still passes even when the checked component
        /// falls out of the derivation entirely - the result then differs all
        /// the more. Asked this way it answers precisely what it claims: that
        /// the order makes a difference.
        /// </remarks>
        [Test]
        public void TheDomainOrderMatters()
        {

            var inOrder  = DialbackKey.Generate(Secret, TargetDomain, SenderDomain, StreamId);
            var swapped  = DialbackKey.Generate(Secret, SenderDomain, TargetDomain, StreamId);

            Assert.That(swapped, Is.Not.EqualTo(inOrder));

        }

        #endregion

        #region TheStreamIdBindsTheKeyToOneConnection()

        /// <summary>
        /// A different stream id yields a different key - on that hangs the
        /// fact that a recorded key is of no use on a second connection.
        /// </summary>
        /// <remarks>
        /// Two generated keys against each other here as well: checked against
        /// the constant, this test stayed green when the stream id was taken
        /// out of the derivation entirely by way of trial - it would therefore
        /// not have noticed precisely the error it is written against.
        /// </remarks>
        [Test]
        public void TheStreamIdBindsTheKeyToOneConnection()
        {

            var oneConnection      = DialbackKey.Generate(Secret, TargetDomain, SenderDomain, StreamId);
            var anotherConnection  = DialbackKey.Generate(Secret, TargetDomain, SenderDomain, "417GAF25");

            Assert.That(anotherConnection, Is.Not.EqualTo(oneConnection));

        }

        #endregion

        #region AnotherSecretYieldsAnotherKey()

        /// <summary>
        /// Without the secret nobody arrives at the key - that is the whole
        /// point of dialback.
        /// </summary>
        [Test]
        public void AnotherSecretYieldsAnotherKey()
        {

            var own      = DialbackKey.Generate(Secret,           TargetDomain, SenderDomain, StreamId);
            var foreign  = DialbackKey.Generate("something else", TargetDomain, SenderDomain, StreamId);

            Assert.That(foreign, Is.Not.EqualTo(own));

        }

        #endregion

        #region VerifyAcceptsTheCorrectKey()

        /// <summary>
        /// The reverse direction: the authoritative server recalculates and
        /// recognises its own key again.
        /// </summary>
        [Test]
        public void VerifyAcceptsTheCorrectKey()
        {

            Assert.That(DialbackKey.Verify(Secret, TargetDomain, SenderDomain, StreamId, ExpectedKey),
                        Is.True);

        }

        #endregion

        #region VerifyIsCaseInsensitiveAboutHex()

        /// <summary>
        /// Hex in capital letters is the same key. A server writing it that way
        /// violates nothing.
        /// </summary>
        [Test]
        public void VerifyIsCaseInsensitiveAboutHex()
        {

            Assert.That(DialbackKey.Verify(Secret, TargetDomain, SenderDomain, StreamId,
                                           ExpectedKey.ToUpperInvariant()),
                        Is.True);

        }

        #endregion

        #region VerifyIgnoresSurroundingWhitespace()

        /// <summary>
        /// In the XEP the key stands indented between the tags - the line break
        /// before and after does not belong to it.
        /// </summary>
        [Test]
        public void VerifyIgnoresSurroundingWhitespace()
        {

            Assert.That(DialbackKey.Verify(Secret, TargetDomain, SenderDomain, StreamId,
                                           $"\n        {ExpectedKey}\n      "),
                        Is.True);

        }

        #endregion

        #region VerifyRejectsAForgedKey()

        /// <summary>
        /// A key that did not come about with this secret is refused.
        /// </summary>
        [Test]
        public void VerifyRejectsAForgedKey()
        {

            var forged = DialbackKey.Generate("the attacker's secret",
                                              TargetDomain, SenderDomain, StreamId);

            Assert.That(DialbackKey.Verify(Secret, TargetDomain, SenderDomain, StreamId, forged),
                        Is.False);

        }

        #endregion

        #region VerifyRejectsSomethingThatIsNotHexAtAll()

        /// <summary>
        /// What is no hex at all leads to a refusal and not to an exception -
        /// the input comes from the far end.
        /// </summary>
        [Test]
        public void VerifyRejectsSomethingThatIsNotHexAtAll()
        {

            Assert.That(DialbackKey.Verify(Secret, TargetDomain, SenderDomain, StreamId, "no key at all"),
                        Is.False);

        }

        #endregion

        #region EverySecretIsDifferent()

        /// <summary>
        /// <see cref="DialbackKey.NewSecret"/> does not deliver the same thing
        /// twice.
        /// </summary>
        [Test]
        public void EverySecretIsDifferent()
        {

            var a = DialbackKey.NewSecret();
            var b = DialbackKey.NewSecret();

            Assert.Multiple(() =>
            {
                Assert.That(a, Is.Not.EqualTo(b));
                Assert.That(a, Has.Length.EqualTo(64), "32 bytes as hex.");
            });

        }

        #endregion

    }

}
