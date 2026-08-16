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

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// RFC 5929, section 4.1: which hash, over which bytes.
    /// </summary>
    /// <remarks>
    /// Both ends compute this independently and never exchange it - the value
    /// only ever appears inside a SCRAM proof. So a disagreement does not show
    /// up as a mismatch anybody can read; it shows up as "authentication
    /// failed", which is what a wrong password looks like too. That is the
    /// reason to pin the rules here rather than trust the exchange to reveal
    /// them.
    /// </remarks>
    [TestFixture]
    public class TlsServerEndPointTests
    {

        #region Helper

        private static X509Certificate2 SelfSigned(HashAlgorithmName hash)
        {

            using var key = RSA.Create(2048);

            var request = new CertificateRequest("CN=test.example",
                                                 key,
                                                 hash,
                                                 RSASignaturePadding.Pkcs1);

            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                                            DateTimeOffset.UtcNow.AddDays(1));

        }

        #endregion


        #region TheWeakHashes_ArePromotedToSha256()

        /// <summary>
        /// "MD5 and SHA-1 are both replaced by SHA-256" - and that exception is
        /// the substance of the rule, not a footnote to it.
        /// </summary>
        /// <remarks>
        /// A binding computed with a broken hash is worth nothing: whoever can
        /// produce a colliding certificate produces a colliding binding along
        /// with it, and the channel binding then certifies the attacker's
        /// connection as readily as the real one.
        /// </remarks>
        [Test]
        public void TheWeakHashes_ArePromotedToSha256()
        {

            Assert.Multiple(() =>
            {

                // md5WithRSAEncryption, sha1WithRSAEncryption, dsa-with-sha1,
                // ecdsa-with-SHA1.
                foreach (var weak in new[] { "1.2.840.113549.1.1.4",
                                             "1.2.840.113549.1.1.5",
                                             "1.2.840.10040.4.3",
                                             "1.2.840.10045.4.1" })

                    Assert.That(TlsServerEndPoint.HashAlgorithmFor(weak),
                                Is.EqualTo(HashAlgorithmName.SHA256),
                                $"{weak} has to be promoted, not used.");

            });

        }

        #endregion

        #region TheStrongHashes_AreKept()

        /// <summary>
        /// SHA-384 and SHA-512 stay themselves - promoting those to SHA-256
        /// would be a downgrade wearing the same clothes as the rule above.
        /// </summary>
        [Test]
        public void TheStrongHashes_AreKept()
        {

            Assert.Multiple(() =>
            {

                Assert.That(TlsServerEndPoint.HashAlgorithmFor("1.2.840.113549.1.1.11"),
                            Is.EqualTo(HashAlgorithmName.SHA256));

                Assert.That(TlsServerEndPoint.HashAlgorithmFor("1.2.840.113549.1.1.12"),
                            Is.EqualTo(HashAlgorithmName.SHA384));

                Assert.That(TlsServerEndPoint.HashAlgorithmFor("1.2.840.113549.1.1.13"),
                            Is.EqualTo(HashAlgorithmName.SHA512));

                Assert.That(TlsServerEndPoint.HashAlgorithmFor("1.2.840.10045.4.3.3"),
                            Is.EqualTo(HashAlgorithmName.SHA384));

            });

        }

        #endregion

        #region ASignatureWithoutAReadableHash_HasNoBinding()

        /// <summary>
        /// Ed25519 and Ed448 carry no hash in the signature algorithm, and
        /// RSASSA-PSS keeps it in the parameters rather than the OID. RFC 5929
        /// leaves the binding undefined there.
        /// </summary>
        /// <remarks>
        /// Null and not SHA-256, although SHA-256 is what several
        /// implementations picked. A guess here does not fail loudly: it
        /// produces a binding the far side computes differently, and the login
        /// then fails looking exactly like a wrong password. Declining to offer
        /// the binding costs one round of channel binding and nothing else.
        /// </remarks>
        [Test]
        public void ASignatureWithoutAReadableHash_HasNoBinding()
        {

            Assert.Multiple(() =>
            {
                Assert.That(TlsServerEndPoint.HashAlgorithmFor("1.3.101.112"),           Is.Null, "Ed25519");
                Assert.That(TlsServerEndPoint.HashAlgorithmFor("1.3.101.113"),           Is.Null, "Ed448");
                Assert.That(TlsServerEndPoint.HashAlgorithmFor("1.2.840.113549.1.1.10"), Is.Null, "RSASSA-PSS");
                Assert.That(TlsServerEndPoint.HashAlgorithmFor("1.2.3.4.5"),             Is.Null, "unknown");
                Assert.That(TlsServerEndPoint.HashAlgorithmFor(null),                    Is.Null);
                Assert.That(TlsServerEndPoint.For(null),                                 Is.Null);
            });

        }

        #endregion

        #region TheBinding_IsTheHashOfTheCertificateAsItTravelled()

        /// <summary>
        /// Over <c>RawData</c> - the DER bytes that went across the wire.
        /// </summary>
        /// <remarks>
        /// Recomputed here by hand rather than compared against a stored
        /// constant: a self-signed certificate is different on every run, so
        /// there is no fixed vector to keep. What is checked is that the
        /// implementation hashes the certificate itself and picks the length
        /// the algorithm implies.
        /// </remarks>
        [Test]
        public void TheBinding_IsTheHashOfTheCertificateAsItTravelled()
        {

            using var sha256 = SelfSigned(HashAlgorithmName.SHA256);
            using var sha512 = SelfSigned(HashAlgorithmName.SHA512);

            Assert.Multiple(() =>
            {

                Assert.That(TlsServerEndPoint.For(sha256),
                            Is.EqualTo(SHA256.HashData(sha256.RawData)));

                Assert.That(TlsServerEndPoint.For(sha512),
                            Is.EqualTo(SHA512.HashData(sha512.RawData)));

                Assert.That(TlsServerEndPoint.For(sha256)!.Length, Is.EqualTo(32));
                Assert.That(TlsServerEndPoint.For(sha512)!.Length, Is.EqualTo(64));

            });

        }

        #endregion

        #region TwoCertificates_BindDifferently()

        /// <summary>
        /// The property the whole thing rests on: a man in the middle presents
        /// a different certificate, so he binds to a different value.
        /// </summary>
        [Test]
        public void TwoCertificates_BindDifferently()
        {

            using var mine     = SelfSigned(HashAlgorithmName.SHA256);
            using var his      = SelfSigned(HashAlgorithmName.SHA256);

            Assert.That(TlsServerEndPoint.For(mine),
                        Is.Not.EqualTo(TlsServerEndPoint.For(his)));

        }

        #endregion

    }

}
