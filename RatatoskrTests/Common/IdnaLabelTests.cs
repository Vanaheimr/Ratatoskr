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
    /// IDNA2008 at the label level: RFC 5891, section 4.2, and the back-check of
    /// an A-label.
    /// </summary>
    /// <remarks>
    /// A domain part is not a string but a sequence of labels, and the rules
    /// take hold per label. Two of them are no formalities but protection
    /// against two addresses for the same thing:
    ///
    /// <list type="bullet">
    ///   <item>An A-label has to be computable back <b>and</b> yield precisely
    ///         itself in the process. Otherwise there would be several valid
    ///         spellings for one name.</item>
    ///   <item>An A-label that wraps pure ASCII is none: the same label would
    ///         stand there once as itself and once in wrapping.</item>
    /// </list>
    /// </remarks>
    [TestFixture]
    public class IdnaLabelTests
    {

        #region Helper functions

        private static Boolean Valid(String domain)
            => Idna.IsValidDomain(domain, out _);

        private static String? Reason(String domain)
        {
            Idna.IsValidDomain(domain, out var reason);
            return reason;
        }

        #endregion


        #region OrdinaryNames_AreValid()

        /// <summary>
        /// The counter-check first: what is a domain name stays one.
        /// </summary>
        [Test]
        public void OrdinaryNames_AreValid()
        {

            Assert.Multiple(() =>
            {
                Assert.That(Valid("example.com"),            Is.True, Reason("example.com"));
                Assert.That(Valid("localhost"),              Is.True, Reason("localhost"));
                Assert.That(Valid("a.example.com"),          Is.True, Reason("a.example.com"));
                Assert.That(Valid("xn--bcher-kva.example"),  Is.True, Reason("xn--bcher-kva.example"));
                Assert.That(Valid("bücher.example"),         Is.True, Reason("bücher.example"));
                Assert.That(Valid("a-b.example"),            Is.True, Reason("a-b.example"));
            });

        }

        #endregion

        #region TheAceLabel_IsCheckedByDecodingIt()

        /// <summary>
        /// An A-label is not believed but recomputed.
        /// </summary>
        [Test]
        public void TheAceLabel_IsCheckedByDecodingIt()
        {

            Assert.Multiple(() =>
            {

                Assert.That(Punycode.Decode("bcher-kva"), Is.EqualTo("bücher"),
                            "The best-known example of them all.");

                Assert.That(Valid("xn--nichtpunycode$.example"), Is.False,
                            "What looks like an A-label and is none is refused.");

                Assert.That(Valid("xn--abc-.example"), Is.False,
                            "An A-label that wraps pure ASCII is none.");

                Assert.That(Valid("xn--tda.example"), Is.True,
                            Reason("xn--tda.example"));

                Assert.That(Valid("xn--TDA.example"), Is.False,
                            "The same meaning, another spelling: punycode digits are " +
                            "case-insensitive, the canonical A-label is not.");

            });

        }

        #endregion

        #region TheHyphenRules()

        /// <summary>
        /// RFC 5891, section 4.2.3.1: no hyphen at the edge, no double hyphen at
        /// the third and fourth position.
        /// </summary>
        /// <remarks>
        /// The second rule keeps free the position where the prefix of an
        /// A-label stands. A U-label that carries <c>--</c> there would look
        /// like a wrapping and would be none.
        /// </remarks>
        [Test]
        public void TheHyphenRules()
        {

            Assert.Multiple(() =>
            {
                Assert.That(Valid("-abc.example"),  Is.False, "hyphen at the start");
                Assert.That(Valid("abc-.example"),  Is.False, "hyphen at the end");
                Assert.That(Valid("ab--cd.example"), Is.False, "'--' at the third and fourth position");
                Assert.That(Valid("a-b-c.example"),  Is.True,  "Single hyphens are fine.");
            });

        }

        #endregion

        #region ACombiningMarkAtTheStart_IsRefused()

        /// <summary>
        /// RFC 5891, section 4.2.3.2: a label does not begin with a combining
        /// mark - it would have nothing to combine with.
        /// </summary>
        [Test]
        public void ACombiningMarkAtTheStart_IsRefused()
        {

            Assert.That(Valid("́abc.example"), Is.False);

        }

        #endregion

        #region EmptyAndOverlongLabels_AreRefused()

        /// <summary>
        /// An empty label does not exist - not even as a dot at the end.
        /// </summary>
        [Test]
        public void EmptyAndOverlongLabels_AreRefused()
        {

            Assert.Multiple(() =>
            {
                Assert.That(Valid("a..example"),      Is.False, "two dots");
                Assert.That(Valid("example.com."),    Is.False, "dot at the end");
                Assert.That(Valid(new String('a', 64) + ".example"), Is.False, "64 characters");
                Assert.That(Valid(new String('a', 63) + ".example"), Is.True,  "63 are permitted");
            });

        }

        #endregion

        #region WhatIsNoLabelCharacter()

        /// <summary>
        /// The code point level takes effect right through to here: underscore
        /// and capital letter belong in no label.
        /// </summary>
        /// <remarks>
        /// At the JID itself the capital letter does not show - the domain part
        /// is lower-cased beforehand. It stands here all the same, because the
        /// check has to be right on its own: it will be called from elsewhere
        /// too.
        /// </remarks>
        [Test]
        public void WhatIsNoLabelCharacter()
        {

            Assert.Multiple(() =>
            {
                Assert.That(Valid("exam_ple.example"),  Is.False, "underscore");
                Assert.That(Valid("EXAMPLE.com"),       Is.False, "capital letter");
                Assert.That(Valid("exa mple.com"),      Is.False, "space");
                Assert.That(Valid("exa♚mple.com"),      Is.False, "symbol");
            });

        }

        #endregion

        #region TheContextualRulesApplyToLabelsToo()

        /// <summary>
        /// A context-dependent code point hangs on its surroundings in a domain
        /// label too (RFC 5892, appendix A.3).
        /// </summary>
        /// <remarks>
        /// <c>col·la.example</c> really exists - the middle dot belongs to the
        /// Catalan alphabet. The same characters in another order yield no
        /// label. Both check at the same time that the label level <i>asks</i>
        /// the rule at all: to check the check on its own does not suffice (see
        /// D43).
        /// </remarks>
        [Test]
        public void TheContextualRulesApplyToLabelsToo()
        {

            Assert.Multiple(() =>
            {
                Assert.That(Valid("col·la.example"), Is.True, Reason("col·la.example"));
                Assert.That(Valid("co·lla.example"), Is.False);
            });

        }

        #endregion

        #region AddressLiterals_AreNotDomainNames()

        /// <summary>
        /// RFC 7622, section 3.2 permits, beside the domain name, an IPv4
        /// literal and a bracketed IPv6 literal.
        /// </summary>
        /// <remarks>
        /// Without this exception <c>127.0.0.1</c> would fall over the label
        /// rules: a label of nothing but digits is indeed permitted, but a
        /// literal is no domain name at all and has nothing to do with IDNA.
        /// With <c>[::1]</c> the refusal would even be certain - colons are no
        /// label characters.
        /// </remarks>
        [Test]
        public void AddressLiterals_AreNotDomainNames()
        {

            Assert.Multiple(() =>
            {
                Assert.That(Valid("127.0.0.1"),        Is.True,  Reason("127.0.0.1"));
                Assert.That(Valid("[::1]"),            Is.True,  Reason("[::1]"));
                Assert.That(Valid("[2001:db8::1]"),    Is.True,  Reason("[2001:db8::1]"));
                Assert.That(Valid("::1"),              Is.False, "Without brackets it is none.");
            });

        }

        #endregion

        #region TheDomainpartOfAJid_GoesThroughTheseRules()

        /// <summary>
        /// And all that holds for the domain part of a JID, not only on its own.
        /// </summary>
        /// <remarks>
        /// Without this test the wiring would be unchecked: a mutation that
        /// throws the result of the check away and goes on came through the
        /// whole collection. To check the check on its own does not suffice -
        /// somebody has to look whether it is <i>asked</i> as well.
        /// </remarks>
        [Test]
        public void TheDomainpartOfAJid_GoesThroughTheseRules()
        {

            Assert.Multiple(() =>
            {

                Assert.That(JidUtilities.TryParse("alice@exa_mple.com",   out _), Is.False, "underscore");
                Assert.That(JidUtilities.TryParse("alice@-example.com",   out _), Is.False, "hyphen at the start");
                Assert.That(JidUtilities.TryParse("alice@a..example.com", out _), Is.False, "empty label");
                Assert.That(JidUtilities.TryParse("alice@xn--abc-.com",   out _), Is.False, "A-label over ASCII");

                Assert.That(JidUtilities.TryParse("alice@bücher.example", out var books), Is.True);
                Assert.That(books.Domainpart, Is.EqualTo("bücher.example"),
                            "A U-label stays a U-label - nothing is rewritten here.");

                Assert.That(JidUtilities.TryParse("alice@[::1]",          out _), Is.True,  "IPv6 literal");
                Assert.That(JidUtilities.TryParse("alice@127.0.0.1",      out _), Is.True,  "IPv4 literal");

            });

        }

        #endregion

        #region TheReasonIsNamed()

        /// <summary>
        /// The reason names the label and the rule - not merely "invalid".
        /// </summary>
        /// <remarks>
        /// A refused address is, for the sender, a lost message. Whoever refuses
        /// it should be able to say what it was down to.
        /// </remarks>
        [Test]
        public void TheReasonIsNamed()
        {

            Assert.Multiple(() =>
            {
                Assert.That(Reason("exam_ple.example"), Does.Contain("U+005F"));
                Assert.That(Reason("-abc.example"),     Does.Contain("-abc"));
                Assert.That(Reason("a..example"),       Does.Contain("empty"));
            });

        }

        #endregion

    }

}
