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
    /// The Bidi rule from RFC 5893, section 2.
    /// </summary>
    /// <remarks>
    /// <b>The rule does not always hold, but catchingly.</b> As soon as a single
    /// label of a domain name carries right-to-left characters, the whole name
    /// is a "Bidi domain name" - and then <i>all</i> labels have to meet the six
    /// conditions, including those of pure ASCII. Precisely that is the part one
    /// overlooks when reading and forgets when implementing:
    /// <c>9abc.example</c> is a valid domain name, <c>9abc.אבג</c> is none.
    ///
    /// The Bidi classes come from <c>BidiClasses</c>, produced from
    /// <c>DerivedBidiClass.txt</c>. Without this table the rule could not be
    /// implemented: whether a letter is R, AL or L hangs on its script and is
    /// derivable from no property .NET delivers.
    /// </remarks>
    [TestFixture]
    public class IdnaBidiTests
    {

        #region Data

        private const String Hebrew     = "אבג";   // ALEF BET GIMEL, class R
        private const String Arabic       = "مثال";  // class AL
        private const String ArabicDigit = "٢";    // ARABIC-INDIC DIGIT TWO, class AN

        private static Boolean Valid(String domain)
            => Idna.IsValidDomain(domain, out _);

        private static String? Reason(String domain)
        {
            Idna.IsValidDomain(domain, out var reason);
            return reason;
        }

        #endregion


        #region WithoutAnRtlLabel_TheRuleDoesNotApply()

        /// <summary>
        /// The counter-check first, and it is the more important half: in a name
        /// without a right-to-left label the rule does not hold.
        /// </summary>
        /// <remarks>
        /// <c>9abc</c> begins with a European digit (EN) and thereby violates
        /// condition 1 - but only if the rule holds at all. Whoever applies it
        /// always refuses domain names by the dozen that have existed for thirty
        /// years.
        /// </remarks>
        [Test]
        public void WithoutAnRtlLabel_TheRuleDoesNotApply()
        {

            Assert.Multiple(() =>
            {
                Assert.That(Valid("9abc.example"),  Is.True, Reason("9abc.example"));
                Assert.That(Valid("example.com"),   Is.True, Reason("example.com"));
                Assert.That(Valid("3com.example"),  Is.True, Reason("3com.example"));
            });

        }

        #endregion

        #region TheRuleIsCatching()

        /// <summary>
        /// A single right-to-left label makes the whole name a Bidi name - and
        /// then the rule holds for the others too.
        /// </summary>
        [Test]
        public void TheRuleIsCatching()
        {

            Assert.Multiple(() =>
            {

                Assert.That(Valid($"{Hebrew}.example"), Is.True,
                            Reason($"{Hebrew}.example"));

                Assert.That(Valid($"9abc.{Hebrew}"), Is.False,
                            "The same label that is permitted without neighbours.");

                Assert.That(Reason($"9abc.{Hebrew}"), Does.Contain("9abc"),
                            "And the reason names the label it is down to.");

                // A label of nothing but Arabic digits likewise makes the name a
                // Bidi name (AN counts along) - and then violates condition 1
                // itself.
                Assert.That(Valid($"{ArabicDigit}.example"), Is.False,
                            "A label of nothing but Arabic digits.");

                // The right-to-left label sits here in its ASCII wrapping.
                // Whoever lets the Bidi rule run over the wrapping sees nothing
                // but Latin letters and finds nothing.
                Assert.That(Valid("9abc.xn--4dbcagdahymbxekheh6e0a7fei0b"), Is.False,
                            "Hebrew as an A-label, beside it a label with a digit at the start.");

            });

        }

        #endregion

        #region AnRtlLabel_KeepsItsDirection()

        /// <summary>
        /// Conditions 1, 2 and 5: a label has a direction, and its first
        /// character determines it.
        /// </summary>
        /// <remarks>
        /// <c>a{Hebrew}</c> begins on the left and carries right-to-left
        /// characters: per condition 1 it is an LTR label, and condition 5
        /// permits no R in it. The other way round, an RTL label may carry no
        /// Latin letters (condition 2).
        /// </remarks>
        [Test]
        public void AnRtlLabel_KeepsItsDirection()
        {

            Assert.Multiple(() =>
            {

                Assert.That(Valid($"a{Hebrew}.example"), Is.False,
                            "An LTR label with Hebrew characters.");

                Assert.That(Valid($"{Hebrew}a.example"), Is.False,
                            "An RTL label with Latin characters.");

                // The same two cases, but with the foreign character in the
                // middle instead of at the end. That is no fine polish: at the
                // end they already founder on conditions 3 and 6 respectively -
                // that conditions 2 and 5 do something as well is shown only by
                // this form.
                Assert.That(Valid($"אaב.example"), Is.False,
                            "Condition 2: an L in the middle of a right-to-left label.");

                Assert.That(Valid($"aאb.example"), Is.False,
                            "Condition 5: an R in the middle of a left-to-right label.");

                Assert.That(Valid($"{Arabic}.example"),    Is.True,
                            Reason($"{Arabic}.example"));

            });

        }

        #endregion

        #region AnRtlLabel_DoesNotMixTheTwoKindsOfDigits()

        /// <summary>
        /// Condition 4: in a right-to-left label European and Arabic digits do
        /// not stand beside each other.
        /// </summary>
        /// <remarks>
        /// That is another rule than A.8/A.9 from RFC 5892 and hits another
        /// pair: there it was about the two <i>Arabic</i> digit series, here
        /// about Arabic beside European ones. Both say the same about why: two
        /// digit sequences beside each other that are read in opposite
        /// directions yield an address nobody can read out with confidence.
        /// </remarks>
        [Test]
        public void AnRtlLabel_DoesNotMixTheTwoKindsOfDigits()
        {

            Assert.Multiple(() =>
            {

                Assert.That(Valid($"{Hebrew}1.example"),  Is.True,
                            Reason($"{Hebrew}1.example"));

                Assert.That(Valid($"{Arabic}{ArabicDigit}.example"), Is.True,
                            Reason($"{Arabic}{ArabicDigit}.example"));

                Assert.That(Valid($"{Arabic}1{ArabicDigit}.example"), Is.False,
                            "European and Arabic digit in the same label.");

            });

        }

        #endregion

        #region TheEndOfALabel()

        /// <summary>
        /// Conditions 3 and 6: what a label may end on.
        /// </summary>
        /// <remarks>
        /// These two conditions are not reachable through
        /// <see cref="Idna.IsValidDomain"/> - the characters a label could end
        /// wrongly on (separators and special characters) are already refused at
        /// the code point level. The rule checks them all the same, for it is
        /// the rule from the RFC and not the subset this caller happens to let
        /// through. So it is asked directly here.
        /// </remarks>
        [Test]
        public void TheEndOfALabel()
        {

            const String MiddleDot      = "·";  // MIDDLE DOT, class ON
            const String Nsm        = "֑";  // HEBREW ACCENT ETNAHTA, class NSM

            Assert.Multiple(() =>
            {

                Assert.That(Idna.SatisfiesBidiRule(Hebrew + Nsm, out _), Is.True,
                            "Condition 3: after the last R, NSM may follow.");

                Assert.That(Idna.SatisfiesBidiRule(Hebrew + MiddleDot, out _), Is.False,
                            "Condition 3: an ON at the end is none of the permitted characters.");

                Assert.That(Idna.SatisfiesBidiRule("abc" + MiddleDot, out _), Is.False,
                            "Condition 6: the same for an LTR label.");

                Assert.That(Idna.SatisfiesBidiRule("abc1", out _), Is.True,
                            "Condition 6: a European digit may end an LTR label.");

            });

        }

        #endregion

        #region TheFirstCharacterDecides()

        /// <summary>
        /// Condition 1: neither a digit nor a neutral character may open a
        /// label.
        /// </summary>
        [Test]
        public void TheFirstCharacterDecides()
        {

            Assert.Multiple(() =>
            {
                Assert.That(Idna.SatisfiesBidiRule("1abc", out _),          Is.False, "EN at the start");
                Assert.That(Idna.SatisfiesBidiRule(ArabicDigit + "ب", out _), Is.False, "AN at the start");
                Assert.That(Idna.SatisfiesBidiRule("abc", out _),           Is.True);
                Assert.That(Idna.SatisfiesBidiRule(Hebrew, out _),      Is.True);
            });

        }

        #endregion

    }

}
