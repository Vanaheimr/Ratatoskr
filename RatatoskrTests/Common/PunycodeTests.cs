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
    /// Punycode per RFC 3492, against the examples from section 7.1.
    /// </summary>
    /// <remarks>
    /// The RFC brings its touchstones along itself: eleven strings in eight
    /// scripts, each with its encoded form. To compute against them is the
    /// difference between "my encoder and my decoder agree" and "my encoding is
    /// the one everybody else reads too".
    ///
    /// Both directions stand here, and both are needed: decoding is done to see
    /// what an A-label means; encoding is done to check that it is the
    /// <b>only</b> spelling of that meaning (RFC 5891, section 4.2.2 - another
    /// one would be a second address for the same thing).
    /// </remarks>
    [TestFixture]
    public class PunycodeTests
    {

        #region Data

        /// <summary>
        /// RFC 3492, section 7.1 - the examples with their encoded form.
        /// </summary>
        private static readonly (String PlainText, String Encoded, String Script)[] Examples =
        [
            ("ليهمابتكلموشعربي؟",
             "egbpdaj6bu4bxfgehfvwxn",
             "Arabic (A)"),

            ("他们为什么不说中文",
             "ihqwcrb4cv8a8dqg056pqjye",
             "Chinese, simplified (B)"),

            ("他們爲什麽不說中文",
             "ihqwctvzc91f659drss3x8bo0yb",
             "Chinese, traditional (C)"),

            ("Pročprostěnemluvíčesky",
             "Proprostnemluvesky-uyb24dma41a",
             "Czech (D)"),

            ("למההםפשוטלאמדבריםעברית",
             "4dbcagdahymbxekheh6e0a7fei0b",
             "Hebrew (E)"),

            ("यहलोगहिन्दीक्योंनहींबोलसकतेहैं",
             "i1baa7eci9glrd9b2ae1bj0hfcgg6iyaf8o0a1dig0cd",
             "Hindi, Devanagari (F)"),

            ("なぜみんな日本語を話してくれないのか",
             "n8jok5ay5dzabd5bym9f0cm5685rrjetr6pdxa",
             "Japanese (G)"),

            ("PorquénopuedensimplementehablarenEspañol",
             "PorqunopuedensimplementehablarenEspaol-fmd56a",
             "Spanish (I)"),

            ("TạisaohọkhôngthểchỉnóitiếngViệt",
             "TisaohkhngthchnitingVit-kjcr8268qyxafd2f1b9g",
             "Vietnamese (J)"),

            ("3年B組金八先生",
             "3B-ww4c5e180e575a65lsy2b",
             "Japanese (L) - with ASCII in between"),

            ("ひとつ屋根の下2",
             "2-u9tlzr9756bt3uc0v",
             "Japanese (O) - ASCII at the end")
        ];

        #endregion


        #region Rfc3492_Examples_Decode()

        /// <summary>
        /// Every encoded form yields its plain text.
        /// </summary>
        [Test]
        public void Rfc3492_Examples_Decode()
        {

            Assert.Multiple(() =>
            {
                foreach (var (plaintext, encoded, script) in Examples)
                    Assert.That(Punycode.Decode(encoded), Is.EqualTo(plaintext), script);
            });

        }

        #endregion

        #region Rfc3492_Examples_Encode()

        /// <summary>
        /// And every plain text yields precisely this encoded form.
        /// </summary>
        [Test]
        public void Rfc3492_Examples_Encode()
        {

            Assert.Multiple(() =>
            {
                foreach (var (plaintext, encoded, script) in Examples)
                    Assert.That(Punycode.Encode(plaintext), Is.EqualTo(encoded), script);
            });

        }

        #endregion

        #region BrokenInput_IsRefusedNotGuessed()

        /// <summary>
        /// What is no punycode yields <c>null</c> - and no exception.
        /// </summary>
        /// <remarks>
        /// The content comes from an address somebody sent. An exception in the
        /// middle of the stanza handling would be the wrong answer to "that is
        /// no valid label".
        /// </remarks>
        [Test]
        public void BrokenInput_IsRefusedNotGuessed()
        {

            Assert.Multiple(() =>
            {
                Assert.That(Punycode.Decode("$"),           Is.Null, "no digit of the base-36 alphabet");
                Assert.That(Punycode.Decode("abc-ä"),       Is.Null, "non-ASCII in the encoded part");
                Assert.That(Punycode.Decode("9999999999"),  Is.Null, "overflow");
                Assert.That(Punycode.Decode(""),            Is.Null, "empty");

                // Counter-check to the line above: 'a-' is no breakage but the
                // correct encoding of 'a'. Without it the assumption would stand
                // here that every separator at the end is an error.
                Assert.That(Punycode.Decode("a-"),          Is.EqualTo("a"));
            });

        }

        #endregion

        #region PureAscii_StaysItself()

        /// <summary>
        /// Pure ASCII stays ASCII - with the separator at the end.
        /// </summary>
        [Test]
        public void PureAscii_StaysItself()
        {

            Assert.Multiple(() =>
            {
                Assert.That(Punycode.Encode("abc"),  Is.EqualTo("abc-"));
                Assert.That(Punycode.Decode("abc-"), Is.EqualTo("abc"));

                // RFC 3492, section 5: the digits are case-insensitive - 'T'
                // counts like 't'. It is encoded in lower case all the same, and
                // precisely by that an A-label is recognised in its canonical
                // form.
                Assert.That(Punycode.Decode("TDA"), Is.EqualTo("ü"));
                Assert.That(Punycode.Encode("ü"),   Is.EqualTo("tda"));
            });

        }

        #endregion

    }

}
