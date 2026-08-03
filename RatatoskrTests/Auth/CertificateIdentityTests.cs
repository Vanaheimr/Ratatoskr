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
using System.Security.Cryptography.X509Certificates;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// Which domains a certificate vouches for - the check that SASL-EXTERNAL
    /// stands on.
    /// </summary>
    /// <remarks>
    /// The entire burden of proof of the procedure sits here. Dialback asks back
    /// at a recorded address and notices a fault by the answer failing to come;
    /// SASL-EXTERNAL notices nothing at all if this function is too generous -
    /// the connection would come about and would look from the outside like a
    /// check that had passed.
    /// </remarks>
    [TestFixture]
    public class CertificateIdentityTests
    {

        #region Helper functions

        /// <summary>
        /// Builds a certificate with a chosen common name and chosen dNSName
        /// entries.
        /// </summary>
        /// <param name="dnsNames">
        /// null leaves the SAN extension out altogether; an empty array creates
        /// it with an entry that is not a domain - those are two different
        /// cases.
        /// </param>
        private static X509Certificate2 MakeCertificate(String    commonName,
                                                        String[]? dnsNames  = null,
                                                        Boolean   ipSanOnly = false)
        {

            using var key = RSA.Create(2048);

            var request = new CertificateRequest($"CN={commonName}",
                                                 key,
                                                 HashAlgorithmName.SHA256,
                                                 RSASignaturePadding.Pkcs1);

            if (dnsNames is not null || ipSanOnly)
            {

                var san = new SubjectAlternativeNameBuilder();

                foreach (var name in dnsNames ?? [])
                    san.AddDnsName(name);

                if (ipSanOnly)
                    san.AddIpAddress(System.Net.IPAddress.Loopback);

                request.CertificateExtensions.Add(san.Build());

            }

            var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                                                   DateTimeOffset.UtcNow.AddYears(1));

            return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);

        }

        #endregion


        #region TheSubjectAlternativeNamesAreTheIdentities()

        [Test]
        public void TheSubjectAlternativeNamesAreTheIdentities()
        {

            using var cert = MakeCertificate("links.example", ["links.example", "im.links.example"]);

            Assert.Multiple(() =>
            {
                Assert.That(CertificateIdentity.DomainsOf(cert),
                            Is.EquivalentTo(new[] { "links.example", "im.links.example" }));
                Assert.That(CertificateIdentity.Authorises(cert, "links.example"),     Is.True);
                Assert.That(CertificateIdentity.Authorises(cert, "im.links.example"),  Is.True);
            });

        }

        #endregion

        #region AForeignDomain_IsNotAuthorised()

        [Test]
        public void AForeignDomain_IsNotAuthorised()
        {

            using var cert = MakeCertificate("links.example", ["links.example"]);

            Assert.That(CertificateIdentity.Authorises(cert, "rechts.example"), Is.False);

        }

        #endregion

        #region TheComparisonIgnoresCase()

        /// <summary>
        /// Domain names do not differ in their spelling.
        /// </summary>
        [Test]
        public void TheComparisonIgnoresCase()
        {

            using var cert = MakeCertificate("links.example", ["Links.EXAMPLE"]);

            Assert.That(CertificateIdentity.Authorises(cert, "links.example"), Is.True);

        }

        #endregion

        #region WithoutAnySan_TheCommonNameCounts()

        /// <summary>
        /// A certificate with no SAN extension at all is read by way of the
        /// common name.
        /// </summary>
        [Test]
        public void WithoutAnySan_TheCommonNameCounts()
        {

            using var cert = MakeCertificate("links.example");

            Assert.Multiple(() =>
            {
                Assert.That(CertificateIdentity.DomainsOf(cert), Is.EquivalentTo(new[] { "links.example" }));
                Assert.That(CertificateIdentity.Authorises(cert, "links.example"), Is.True);
            });

        }

        #endregion

        #region WithASan_TheCommonNameNoLongerCounts()

        /// <summary>
        /// As soon as there is a SAN extension, the common name no longer counts
        /// (RFC 6125, section 6.4.4).
        /// </summary>
        /// <remarks>
        /// That is the most important line of this file. Were the check to fall
        /// back on the common name, a certificate with <c>CN=victim.example</c>
        /// and any harmless SAN would be enough to speak for
        /// <c>victim.example</c> - and such a certificate is to be had from any
        /// CA that does not check the CN, because by today's understanding it
        /// means nothing anyway.
        /// </remarks>
        [Test]
        public void WithASan_TheCommonNameNoLongerCounts()
        {

            using var cert = MakeCertificate("victim.example", ["harmless.example"]);

            Assert.Multiple(() =>
            {
                Assert.That(CertificateIdentity.Authorises(cert, "harmless.example"), Is.True);
                Assert.That(CertificateIdentity.Authorises(cert, "victim.example"),   Is.False,
                            "With a SAN present the common name must no longer hold.");
            });

        }

        #endregion

        #region ASanWithoutAnyDnsName_AuthorisesNothing()

        /// <summary>
        /// A SAN extension that names only an IP address vouches for no domain -
        /// not even the one from the common name.
        /// </summary>
        [Test]
        public void ASanWithoutAnyDnsName_AuthorisesNothing()
        {

            using var cert = MakeCertificate("victim.example", ipSanOnly: true);

            Assert.Multiple(() =>
            {
                Assert.That(CertificateIdentity.DomainsOf(cert), Is.Empty);
                Assert.That(CertificateIdentity.Authorises(cert, "victim.example"), Is.False);
            });

        }

        #endregion

        #region AWildcard_AuthorisesNothing()

        /// <summary>
        /// Wildcards do not hold here - neither for the subdomain nor for
        /// themselves.
        /// </summary>
        /// <remarks>
        /// Deliberately so, and the test records it, so that the decision does
        /// not tip over unnoticed. Whoever admits wildcards has to settle on
        /// exactly one reading; the common mistakes in doing so - the wildcard
        /// covers several labels, or it covers the bare domain as well - are
        /// both too generous.
        /// </remarks>
        [Test]
        public void AWildcard_AuthorisesNothing()
        {

            using var cert = MakeCertificate("*.links.example", ["*.links.example"]);

            Assert.Multiple(() =>
            {
                Assert.That(CertificateIdentity.Authorises(cert, "im.links.example"), Is.False);
                Assert.That(CertificateIdentity.Authorises(cert, "links.example"),    Is.False);
            });

        }

        #endregion

        #region AnEmptyDomain_AuthorisesNothing()

        /// <summary>
        /// Asking after nothing is not a check that passed.
        /// </summary>
        [Test]
        public void AnEmptyDomain_AuthorisesNothing()
        {

            using var cert = MakeCertificate("links.example", ["links.example"]);

            Assert.Multiple(() =>
            {
                Assert.That(CertificateIdentity.Authorises(cert, ""),    Is.False);
                Assert.That(CertificateIdentity.Authorises(cert, "   "), Is.False);
            });

        }

        #endregion

    }

}
