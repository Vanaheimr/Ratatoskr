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
    /// SASLprep (RFC 4013), first against the example table from section 3.
    /// </summary>
    /// <remarks>
    /// Seven lines that cover all four steps of the profile: mapping,
    /// normalisation, prohibitions and the bidi check. They are the reason this
    /// implementation does not mark its own homework - the tables behind it
    /// come from RFC 3454 and are generated from it by
    /// <c>tools/stringprep/generate.py</c>.
    ///
    /// Every character checked stands there as an escape sequence and not as
    /// itself. Half the collection consists of characters that are invisible or
    /// that turn the writing direction around; as a literal in the source it
    /// would not be visible what is actually being checked - and while editing
    /// it would be lost unnoticed. (This file demonstrated exactly that once.)
    /// </remarks>
    [TestFixture]
    public class SaslPrepTests
    {

        #region Data

        // The characters at issue - named rather than inserted.
        private const String SoftHyphen        = "\u00AD";
        private const String FeminineOrdinal   = "ª";
        private const String RomanNine         = "Ⅸ";
        private const String Bell              = "\u0007";
        private const String NoBreakSpace      = "\u00A0";
        private const String OghamSpace        = "\u1680";
        private const String IdeographicSpace  = "\u3000";
        private const String ArabicAlef        = "ا";
        private const String ArabicBeh         = "ب";
        private const String HebrewAlef        = "א";
        private const String Unassigned32      = "ȡ";

        #endregion

        #region Rfc4013_ExampleTable()

        /// <summary>
        /// The example table from RFC 4013, section 3, line by line.
        /// </summary>
        [Test]
        public void Rfc4013_ExampleTable()
        {

            Assert.Multiple(() =>
            {

                // 1: SOFT HYPHEN falls away
                Assert.That(SaslPrep.Prepare("I" + SoftHyphen + "X"), Is.EqualTo("IX"));

                // 2: unchanged
                Assert.That(SaslPrep.Prepare("user"), Is.EqualTo("user"));

                // 3: upper and lower case stay - so this does not match 2
                Assert.That(SaslPrep.Prepare("USER"), Is.EqualTo("USER"));

                // 4: NFKC maps the feminine ordinal onto an a
                Assert.That(SaslPrep.Prepare(FeminineOrdinal), Is.EqualTo("a"));

                // 5: NFKC takes the roman nine apart - after which it matches 1
                Assert.That(SaslPrep.Prepare(RomanNine), Is.EqualTo("IX"));

                // 6: prohibited character
                Assert.That(() => SaslPrep.Prepare(Bell),
                            Throws.TypeOf<AuthenticationException>());

                // 7: bidi - arabic alef, then the digit one
                Assert.That(() => SaslPrep.Prepare(ArabicAlef + "1"),
                            Throws.TypeOf<AuthenticationException>());

            });

        }

        #endregion

        #region NonAsciiSpaces_BecomeAnOrdinarySpace()

        /// <summary>
        /// RFC 4013, section 2.1: spaces outside ASCII become U+0020.
        /// </summary>
        /// <remarks>
        /// The everyday case: a non-breaking space looks like an ordinary one
        /// and arises by itself on some keyboards. Without this mapping they
        /// would be two different passwords.
        /// </remarks>
        [Test]
        public void NonAsciiSpaces_BecomeAnOrdinarySpace()
        {

            Assert.Multiple(() =>
            {
                Assert.That(SaslPrep.Prepare("a" + NoBreakSpace     + "b"), Is.EqualTo("a b"));
                Assert.That(SaslPrep.Prepare("a" + OghamSpace       + "b"), Is.EqualTo("a b"));
                Assert.That(SaslPrep.Prepare("a" + IdeographicSpace + "b"), Is.EqualTo("a b"));
            });

        }

        #endregion

        #region UnassignedCodePoints_AreRefused()

        /// <summary>
        /// RFC 4013, section 2.5: what was unassigned in Unicode 3.2 does not
        /// belong in a stored password.
        /// </summary>
        /// <remarks>
        /// The reason is not pedantry: a code point without a settled meaning
        /// can be given one later, and then two counterparts normalise it
        /// differently. Whoever takes it into their password today has a
        /// different one tomorrow.
        ///
        /// U+0221 vouches at the same time for the table really being nailed to
        /// Unicode 3.2 and not coming from the running .NET version: there it
        /// has long been a latin small letter.
        /// </remarks>
        [Test]
        public void UnassignedCodePoints_AreRefused()
        {

            Assert.Multiple(() =>
            {

                Assert.That(() => SaslPrep.Prepare("a" + Unassigned32 + "b"),
                            Throws.TypeOf<AuthenticationException>());

                // As a query string it is admissible.
                Assert.That(SaslPrep.Prepare("a" + Unassigned32 + "b", AllowUnassigned: true),
                            Is.EqualTo("a" + Unassigned32 + "b"));

            });

        }

        #endregion

        #region ProhibitedCharacters_AreRefused()

        /// <summary>
        /// A cross-section through the prohibition tables C.2 to C.9.
        /// </summary>
        [Test]
        public void ProhibitedCharacters_AreRefused()
        {

            var prohibited = new (String Name, String Input)[]
            {
                ("ASCII control characters (C.2.1)",   "a\u0000b"),
                ("control characters (C.2.2)",         "a\u0080b"),
                ("private use (C.3)",                  "a\uE000b"),
                ("non-characters (C.4)",               "a\uFDD0b"),
                ("inappropriate for plain text (C.6)", "a\uFFFCb"),
                ("canonically inappropriate (C.7)",    "a\u2FF0b"),
                ("changing the display (C.8)",         "a\u202Ab"),
                ("tagging (C.9)",                      "a\U000E0001b")
            };

            Assert.Multiple(() =>
            {
                foreach (var (name, input) in prohibited)
                    Assert.That(() => SaslPrep.Prepare(input),
                                Throws.TypeOf<AuthenticationException>(),
                                $"Let through: {name}.");
            });

        }

        #endregion

        #region ALoneSurrogate_IsRefused()

        /// <summary>
        /// A lone surrogate is half a character (table C.5).
        /// </summary>
        /// <remarks>
        /// The way through <c>EnumerateRunes</c> would have replaced it in
        /// silence by U+FFFD - and thereby led two different inputs to the same
        /// password.
        /// </remarks>
        [Test]
        public void ALoneSurrogate_IsRefused()
        {

            Assert.Multiple(() =>
            {

                Assert.That(() => SaslPrep.Prepare("a\uD800b"),
                            Throws.TypeOf<AuthenticationException>());

                Assert.That(() => SaslPrep.Prepare("a\uDC00b"),
                            Throws.TypeOf<AuthenticationException>());

                // The complete pair, by contrast, is an ordinary character.
                Assert.That(SaslPrep.Prepare("a\U00010330b"),
                            Is.EqualTo("a\U00010330b"));

            });

        }

        #endregion

        #region BidiRules()

        /// <summary>
        /// RFC 3454, section 6: the two rules for writing directions.
        /// </summary>
        /// <remarks>
        /// A string made of both directions is displayed differently depending
        /// on its surroundings - whoever reads it does not necessarily see what
        /// stands in it.
        /// </remarks>
        [Test]
        public void BidiRules()
        {

            Assert.Multiple(() =>
            {

                // Right-to-left throughout: admissible.
                Assert.That(SaslPrep.Prepare(ArabicAlef + ArabicBeh),
                            Is.EqualTo(ArabicAlef + ArabicBeh));

                // Left-to-right throughout: admissible.
                Assert.That(SaslPrep.Prepare("abc"), Is.EqualTo("abc"));

                // Rule 2: both directions together.
                Assert.That(() => SaslPrep.Prepare(ArabicAlef + "a" + ArabicBeh),
                            Throws.TypeOf<AuthenticationException>(),
                            "Arabic with a latin letter in between.");

                Assert.That(() => SaslPrep.Prepare(HebrewAlef + "a" + HebrewAlef),
                            Throws.TypeOf<AuthenticationException>(),
                            "Hebrew with a latin letter in between.");

                // Rule 3: begins right-to-left, does not end right-to-left.
                Assert.That(() => SaslPrep.Prepare(ArabicAlef + "1"),
                            Throws.TypeOf<AuthenticationException>());

                // And the other way round.
                Assert.That(() => SaslPrep.Prepare("1" + ArabicAlef),
                            Throws.TypeOf<AuthenticationException>());

                // Digits between right-to-left characters, by contrast, are in
                // order: they stand neither in D.1 nor in D.2, and the
                // beginning and the end are right.
                Assert.That(SaslPrep.Prepare(ArabicAlef + "1" + ArabicBeh),
                            Is.EqualTo(ArabicAlef + "1" + ArabicBeh));

            });

        }

        #endregion

        #region Prepare_IsIdempotent()

        /// <summary>
        /// Preparing twice changes nothing further.
        /// </summary>
        /// <remarks>
        /// That is the property everything else hangs on: the server stores the
        /// key of a prepared string, the client prepares afresh at every login.
        /// Were the procedure not idempotent, the two would drift further apart
        /// with every pass.
        /// </remarks>
        [Test]
        public void Prepare_IsIdempotent()
        {

            var inputs = new[] {
                "user",
                "I" + SoftHyphen + "X",
                RomanNine,
                "a" + NoBreakSpace + "b",
                "ordinary",
                ArabicAlef + ArabicBeh,
                "groß"
            };

            Assert.Multiple(() =>
            {
                foreach (var input in inputs)
                {
                    var once = SaslPrep.Prepare(input);
                    Assert.That(SaslPrep.Prepare(once), Is.EqualTo(once),
                                $"Not idempotent: {input}");
                }
            });

        }

        #endregion

        #region TheEmptyString_StaysEmpty()

        /// <summary>
        /// The empty string goes through without complaint - in particular the
        /// bidi check does not stumble over the missing first character.
        /// </summary>
        [Test]
        public void TheEmptyString_StaysEmpty()
        {
            Assert.That(SaslPrep.Prepare(""), Is.EqualTo(""));
        }

        #endregion

    }

}
