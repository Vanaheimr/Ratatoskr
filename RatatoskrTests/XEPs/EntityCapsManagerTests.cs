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

using System.Security.Cryptography;
using System.Text;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0115 Entity Capabilities against the test vector from section 5.2
    /// ("Simple Generation Example").
    ///
    /// If the verification string does not hold, far ends recalculating the
    /// hash discard our own capabilities - the error stays invisible in
    /// operation though, because many clients do not check.
    /// </summary>
    [TestFixture]
    public class EntityCapsManagerTests
    {

        #region Data

        // XEP-0115, section 5.2
        private const String Xep0115_SimpleVer =
            "QgayPKawpkPSDYmwT/WM94uAlu0=";

        private const String Xep0115_SimpleS =
            "client/pc//Exodus 0.9.1<" +
            "http://jabber.org/protocol/caps<" +
            "http://jabber.org/protocol/disco#info<" +
            "http://jabber.org/protocol/disco#items<" +
            "http://jabber.org/protocol/muc<";

        // XEP-0115, section 5.3 ("Complex Generation Example") - two
        // identities differing only in xml:lang and name, and an XEP-0128 data
        // form.
        private const String Xep0115_ComplexVer =
            "q07IKJEyjvHSyhy//CH0CxmKi8w=";

        private const String Xep0115_ComplexS =
            "client/pc/el/Ψ 0.11<" +
            "client/pc/en/Psi 0.11<" +
            "http://jabber.org/protocol/caps<" +
            "http://jabber.org/protocol/disco#info<" +
            "http://jabber.org/protocol/disco#items<" +
            "http://jabber.org/protocol/muc<" +
            "urn:xmpp:dataforms:softwareinfo<" +
            "ip_version<ipv4<ipv6<" +
            "os<Mac<" +
            "os_version<10.5.1<" +
            "software<Psi<" +
            "software_version<0.11<";

        #endregion

        #region Helper functions

        /// <summary>
        /// A DiscoManager with exactly the given identities and features.
        /// </summary>
        private static DiscoManager Disco(DiscoIdentity identity, params String[] features)
        {

            var disco = new DiscoManager(_ => Task.CompletedTask);

            disco.LocalIdentities.Clear();
            disco.LocalIdentities.Add(identity);

            disco.LocalFeatures.Clear();
            disco.LocalFeatures.AddRange(features);

            return disco;

        }

        /// <summary>
        /// A DiscoManager with the given identities and without features.
        /// </summary>
        private static DiscoManager DiscoWithIdentities(params DiscoIdentity[] identities)
        {

            var disco = new DiscoManager(_ => Task.CompletedTask);

            disco.LocalIdentities.Clear();
            disco.LocalIdentities.AddRange(identities);

            disco.LocalFeatures.Clear();

            return disco;

        }

        private static String Sha1Base64(String s)
            => Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(s)));

        /// <summary>
        /// One field of a data form.
        /// </summary>
        private static DiscoField Field(String var, params String[] values)
            => new(var, null, values);

        /// <summary>
        /// The softwareinfo form from XEP-0115, section 5.3.
        /// </summary>
        private static DiscoForm SoftwareInfo()
            => new([
                   new DiscoField("FORM_TYPE", "hidden", ["urn:xmpp:dataforms:softwareinfo"]),
                   Field("ip_version",       "ipv4", "ipv6"),
                   Field("os",               "Mac"),
                   Field("os_version",       "10.5.1"),
                   Field("software",         "Psi"),
                   Field("software_version", "0.11")
               ]);

        /// <summary>
        /// The two identities from XEP-0115, section 5.3.
        /// </summary>
        private static DiscoIdentity[] PsiIdentities()
            => [
                   new("client", "pc", "Psi 0.11", "en"),
                   new("client", "pc", "Ψ 0.11",   "el")
               ];

        private static readonly String[] PsiFeatures = [
            "http://jabber.org/protocol/caps",
            "http://jabber.org/protocol/disco#info",
            "http://jabber.org/protocol/disco#items",
            "http://jabber.org/protocol/muc"
        ];

        #endregion


        #region Xep0115_SimpleGenerationExample_ProducesExpectedVer()

        /// <summary>
        /// The verification string from XEP-0115 section 5.2 has to be
        /// reproduced exactly.
        /// </summary>
        [Test]
        public void Xep0115_SimpleGenerationExample_ProducesExpectedVer()
        {

            var disco = Disco(new DiscoIdentity("client", "pc", "Exodus 0.9.1"),
                              "http://jabber.org/protocol/caps",
                              "http://jabber.org/protocol/disco#info",
                              "http://jabber.org/protocol/disco#items",
                              "http://jabber.org/protocol/muc");

            var caps = new EntityCapsManager(disco);

            Assert.That(caps.CalculateVerificationString(), Is.EqualTo(Xep0115_SimpleVer));

        }

        #endregion

        #region Xep0115_TestVector_MatchesIndependentlyComputedHash()

        /// <summary>
        /// Counter-check: the published ver value really is the SHA-1 hash of
        /// the S string printed in the XEP. With that it is established that
        /// the test vector itself is right.
        /// </summary>
        [Test]
        public void Xep0115_TestVector_MatchesIndependentlyComputedHash()
        {
            Assert.That(Sha1Base64(Xep0115_SimpleS), Is.EqualTo(Xep0115_SimpleVer));
        }

        #endregion

        #region VerificationString_WorksOnForeignDataToo()

        /// <summary>
        /// The same calculation over foreign details - the test vector from
        /// section 5.2, this time not out of our own lists.
        /// </summary>
        /// <remarks>
        /// Until then the hash could be formed only over our own features. With
        /// that it was a value this client does produce but never checks — and
        /// precisely the checking is the purpose of the procedure.
        /// </remarks>
        [Test]
        public void VerificationString_WorksOnForeignDataToo()
        {

            var ver = EntityCapsManager.VerificationString(
                          [new DiscoIdentity("client", "pc", "Exodus 0.9.1")],
                          ["http://jabber.org/protocol/caps",
                           "http://jabber.org/protocol/disco#info",
                           "http://jabber.org/protocol/disco#items",
                           "http://jabber.org/protocol/muc"]);

            Assert.That(ver, Is.EqualTo(Xep0115_SimpleVer));

        }

        #endregion

        #region Xep0115_ComplexGenerationExample_ProducesExpectedVer()

        /// <summary>
        /// The test vector from XEP-0115 section 5.3 - two identities differing
        /// only in <c>xml:lang</c> and name, and an XEP-0128 data form.
        /// </summary>
        /// <remarks>
        /// The vector covers both things together that the simple one from 5.2
        /// does not show: that the language goes into the hash between type and
        /// name, and that the form fields are appended in the order the XEP
        /// demands. Without it the calculation would be checked only against
        /// itself.
        /// </remarks>
        [Test]
        public void Xep0115_ComplexGenerationExample_ProducesExpectedVer()
        {

            var ver = EntityCapsManager.VerificationString(PsiIdentities(),
                                                           PsiFeatures,
                                                           [SoftwareInfo()]);

            Assert.That(ver, Is.EqualTo(Xep0115_ComplexVer));

        }

        #endregion

        #region Xep0115_ComplexTestVector_MatchesIndependentlyComputedHash()

        /// <summary>
        /// Counter-check as with the simple vector: the printed ver value is
        /// the SHA-1 hash of the printed S string.
        /// </summary>
        [Test]
        public void Xep0115_ComplexTestVector_MatchesIndependentlyComputedHash()
        {
            Assert.That(Sha1Base64(Xep0115_ComplexS), Is.EqualTo(Xep0115_ComplexVer));
        }

        #endregion

        #region SoftwareInfo_OmitsWhatIsNotGiven()

        /// <summary>
        /// XEP-0232: What is not given does not turn up in the form - and that
        /// for each of the four fields separately.
        /// </summary>
        /// <remarks>
        /// An empty field is not the same as a missing one: It would go into
        /// the verification string and would make the hash different from that
        /// of an entity giving the same information. "I say nothing about my
        /// operating system" and "my operating system is called empty string"
        /// are two different statements, and only the first one is meant.
        ///
        /// Each field on its own, because a test always filling all four checks
        /// the rule only on the one it leaves out.
        /// </remarks>
        [Test]
        public void SoftwareInfo_OmitsWhatIsNotGiven()
        {

            var empty = DiscoForm.SoftwareInfo();

            Assert.Multiple(() =>
            {

                Assert.That(empty.Fields, Has.Count.EqualTo(1),
                            "Without any detail the FORM_TYPE field alone stays.");

                Assert.That(empty.FormType, Is.EqualTo("urn:xmpp:dataforms:softwareinfo"));

                // And each field on its own: precisely the given one is there.
                Assert.That(Fields(DiscoForm.SoftwareInfo(Software:        "J")),
                            Is.EqualTo(new[] { "software" }));

                Assert.That(Fields(DiscoForm.SoftwareInfo(SoftwareVersion: "1")),
                            Is.EqualTo(new[] { "software_version" }));

                Assert.That(Fields(DiscoForm.SoftwareInfo(OperatingSystem: "W")),
                            Is.EqualTo(new[] { "os" }));

                Assert.That(Fields(DiscoForm.SoftwareInfo(OSVersion:       "11")),
                            Is.EqualTo(new[] { "os_version" }));

            });

            static String[] Fields(DiscoForm form)
                => [.. form.Fields.Where(f => !f.IsFormType).Select(f => f.Var)];

        }

        #endregion

        #region Forms_FieldsAndValues_AreAllSorted()

        /// <summary>
        /// Forms, fields and values are sorted - the order in which they arrive
        /// must not change the hash.
        /// </summary>
        /// <remarks>
        /// Otherwise two entities with the same range of functions would
        /// calculate different values depending on how their server puts the
        /// answer together - and every check would fail with honest far ends.
        /// </remarks>
        [Test]
        public void Forms_FieldsAndValues_AreAllSorted()
        {

            DiscoForm Form(String type, params DiscoField[] fields)
                => new([new DiscoField("FORM_TYPE", "hidden", [type]), .. fields]);

            var forwards = EntityCapsManager.VerificationString(
                               [new DiscoIdentity("client", "pc", "Test")],
                                [],
                                [Form("urn:test:a", Field("x", "1", "2")),
                                 Form("urn:test:b", Field("y", "3"), Field("z", "4"))]);

            // The same details, entered backwards everywhere.
            var backwards = EntityCapsManager.VerificationString(
                                [new DiscoIdentity("client", "pc", "Test")],
                                  [],
                                  [Form("urn:test:b", Field("z", "4"), Field("y", "3")),
                                   Form("urn:test:a", Field("x", "2", "1"))]);

            Assert.That(backwards, Is.EqualTo(forwards));

        }

        #endregion

        #region AFormWithoutAHiddenFormType_IsIgnored()

        /// <summary>
        /// A form without a valid FORM_TYPE does not count - XEP-0115
        /// section 5.4: "ignore the form but continue processing".
        /// </summary>
        /// <remarks>
        /// That is the difference that makes all the difference: such a form
        /// does not make the answer invalid, it only does not go into the hash.
        /// Whoever calculates it in instead arrives at a different value with a
        /// far end keeping to the XEP; whoever discards the whole answer
        /// refuses it the information without a reason.
        /// </remarks>
        [Test]
        public void AFormWithoutAHiddenFormType_IsIgnored()
        {

            var without = EntityCapsManager.VerificationString(
                              [new DiscoIdentity("client", "pc", "Test")],
                           ["urn:test:a"]);

            // Entirely without a FORM_TYPE.
            var withoutType = EntityCapsManager.VerificationString(
                                  [new DiscoIdentity("client", "pc", "Test")],
                              ["urn:test:a"],
                              [new DiscoForm([Field("os", "Mac")])]);

            // With a FORM_TYPE, but not declared as hidden.
            var wrongType = EntityCapsManager.VerificationString(
                                [new DiscoIdentity("client", "pc", "Test")],
                                  ["urn:test:a"],
                                  [new DiscoForm([
                                       new DiscoField("FORM_TYPE", "text-single", ["urn:test:form"]),
                                       Field("os", "Mac")
                                   ])]);

            Assert.Multiple(() =>
            {
                Assert.That(withoutType,     Is.EqualTo(without));
                Assert.That(wrongType, Is.EqualTo(without));
            });

        }

        #endregion

        #region VerificationString_IsIndependentOfInsertionOrder()

        /// <summary>
        /// The order in which features are registered must not influence the
        /// hash - otherwise two instances of the same client calculate
        /// different values.
        /// </summary>
        [Test]
        public void VerificationString_IsIndependentOfInsertionOrder()
        {

            var identity = new DiscoIdentity("client", "pc", "Exodus 0.9.1");

            var forward = new EntityCapsManager(Disco(identity,
                              "http://jabber.org/protocol/caps",
                              "http://jabber.org/protocol/disco#info",
                              "http://jabber.org/protocol/disco#items",
                              "http://jabber.org/protocol/muc"));

            var reverse = new EntityCapsManager(Disco(identity,
                              "http://jabber.org/protocol/muc",
                              "http://jabber.org/protocol/disco#items",
                              "http://jabber.org/protocol/disco#info",
                              "http://jabber.org/protocol/caps"));

            Assert.That(reverse.CalculateVerificationString(),
                        Is.EqualTo(forward.CalculateVerificationString()));

        }

        #endregion

        #region CapsElement_CarriesSha1HashAndVer()

        /// <summary>
        /// The c element for the presence has to carry the namespace,
        /// hash='sha-1', node and the calculated ver value.
        /// </summary>
        [Test]
        public void CapsElement_CarriesSha1HashAndVer()
        {

            var caps = new EntityCapsManager(
                           Disco(new DiscoIdentity("client", "pc", "Exodus 0.9.1"),
                                 "http://jabber.org/protocol/caps",
                                 "http://jabber.org/protocol/disco#info",
                                 "http://jabber.org/protocol/disco#items",
                                 "http://jabber.org/protocol/muc"))
                       {
                           Node = "https://example.org/client"
                       };

            var element = caps.GetCapsElement();

            Assert.Multiple(() =>
            {
                Assert.That(element, Does.Contain("xmlns='http://jabber.org/protocol/caps'"));
                Assert.That(element, Does.Contain("hash='sha-1'"));
                Assert.That(element, Does.Contain("node='https://example.org/client'"));
                Assert.That(element, Does.Contain($"ver='{Xep0115_SimpleVer}'"));
            });

        }

        #endregion

        #region Features_AreSortedByOctetOrder()

        /// <summary>
        /// REGRESSION TEST - XEP-0115 section 5.1 demands a sorting in octet
        /// order.
        ///
        /// CalculateVerificationString formerly used <c>Order()</c>, that is,
        /// the culture-dependent default comparison: there 'a' stands before
        /// 'B', in octet order on the other hand 'B' (0x42) before 'a' (0x61).
        /// For the current feature list of the client both orders coincide by
        /// chance, so the official test vector alone does not uncover the
        /// error.
        /// </summary>
        [Test]
        public void Features_AreSortedByOctetOrder()
        {

            var identity = new DiscoIdentity("client", "pc", "Test");

            var caps = new EntityCapsManager(Disco(identity, "urn:test:a", "urn:test:B"));

            // Octet order: 'B' (0x42) before 'a' (0x61)
            var expected = Sha1Base64("client/pc//Test<urn:test:B<urn:test:a<");

            Assert.That(caps.CalculateVerificationString(), Is.EqualTo(expected));

        }

        #endregion

        #region Identities_AreSortedByOctetOrderIncludingName()

        /// <summary>
        /// REGRESSION TEST - XEP-0115 section 5.1 sorts identities over
        /// category/type/xml:lang/name in octet order.
        ///
        /// Two identities with the same category/type therefore have to order
        /// themselves over the name, and that octet by octet ('B' 0x42 before
        /// 'a' 0x61). CalculateVerificationString formerly sorted only over
        /// category/type; for equal prefixes the insertion order thereby
        /// stayed.
        /// </summary>
        [Test]
        public void Identities_AreSortedByOctetOrderIncludingName()
        {

            var caps = new EntityCapsManager(
                           DiscoWithIdentities(new DiscoIdentity("client", "pc", "a"),
                                               new DiscoIdentity("client", "pc", "B")));

            var expected = Sha1Base64("client/pc//B<client/pc//a<");

            Assert.That(caps.CalculateVerificationString(), Is.EqualTo(expected));

        }

        #endregion

    }

}
