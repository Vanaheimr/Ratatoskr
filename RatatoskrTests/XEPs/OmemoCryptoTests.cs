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

using System.Numerics;
using System.Security.Cryptography;
using System.Text;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// The crypto building blocks for OMEMO (XEP-0384) - against published
    /// vectors.
    /// </summary>
    /// <remarks>
    /// <b>Why foreign numbers and not our own.</b> An encryption checks itself
    /// too easily: whoever can decrypt what they encrypted themselves has
    /// shown that they make the same error twice. Only numbers somebody else
    /// wrote down have force of proof - RFC 7748 for X25519, RFC 8032 for the
    /// point arithmetic, RFC 5869 for HKDF, RFC 4231 for HMAC, NIST SP 800-38A
    /// for AES-CBC.
    ///
    /// The vectors stand here as a statement about <i>which</i> procedure is
    /// meant as well. An exchange of SHA-256 for SHA-1 would otherwise stand
    /// out nowhere - both deliver bytes, and both can be decrypted again.
    /// </remarks>
    [TestFixture]
    public class OmemoCryptoTests
    {

        #region Helper functions

        private static Byte[] Hex(String hex)
            => Convert.FromHexString(hex.Replace(" ", "").Replace("\n", ""));

        private static String Hex(Byte[] bytes)
            => Convert.ToHexString(bytes).ToLowerInvariant();

        #endregion


        #region X25519_MatchesRfc7748Section61()

        /// <summary>
        /// RFC 7748, section 6.1: Alice and Bob, their keys and the shared
        /// secret value.
        /// </summary>
        [Test]
        public void X25519_MatchesRfc7748Section61()
        {

            var alice = Curve25519.KeyPairFromPrivate(
                            Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a"));

            var bob   = Curve25519.KeyPairFromPrivate(
                            Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb"));

            Assert.Multiple(() =>
            {

                Assert.That(Hex(alice.PublicKey),
                            Is.EqualTo("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"),
                            "Alice's public key");

                Assert.That(Hex(bob.PublicKey),
                            Is.EqualTo("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f"),
                            "Bob's public key");

                Assert.That(Hex(Curve25519.Agree(alice.PrivateKey, bob.PublicKey)),
                            Is.EqualTo("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742"),
                            "The shared secret value");

                // And both directions yield the same one - that is the point of
                // the exercise and would not be said with a one-sided check.
                Assert.That(Hex(Curve25519.Agree(bob.PrivateKey, alice.PublicKey)),
                            Is.EqualTo(Hex(Curve25519.Agree(alice.PrivateKey, bob.PublicKey))));

            });

        }

        #endregion

        #region X25519_MatchesRfc7748Section52()

        /// <summary>
        /// RFC 7748, section 5.2: a single scalar multiplication with a
        /// u coordinate that is not the base point.
        /// </summary>
        /// <remarks>
        /// The vector from 6.1 alone would let a mix-up through that stands out
        /// here: it uses only the base point and the respective other public
        /// key, both well-formed values out of our own generation.
        /// </remarks>
        [Test]
        public void X25519_MatchesRfc7748Section52()
        {
            Assert.That(
                Hex(Curve25519.Agree(
                        Hex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"),
                        Hex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c"))),
                Is.EqualTo("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552"));
        }

        #endregion

        #region ALowOrderPoint_IsRefused()

        /// <summary>
        /// A point of small order yields nothing but zeroes - and is turned
        /// away.
        /// </summary>
        /// <remarks>
        /// The result would be no secret but a number the attacker knows
        /// beforehand: they send such a point as their bundle, and every
        /// session derived from it has a key they calculated along. RFC 7748,
        /// section 6.1 leaves the check optional; optional it is only where the
        /// public key comes from a trustworthy source - an OMEMO bundle comes
        /// from the server.
        /// </remarks>
        [Test]
        public void ALowOrderPoint_IsRefused()
        {

            var own = Curve25519.GenerateKeyPair();

            Assert.Multiple(() =>
            {

                foreach (var point in new[] {
                             "0000000000000000000000000000000000000000000000000000000000000000",
                             "0100000000000000000000000000000000000000000000000000000000000000",
                             "e0eb7a7c3b41b8ae1656e3faf19fc46ada098deb9c32b1fd866205165f49b800"
                         })
                    Assert.That(() => Curve25519.Agree(own.PrivateKey, Hex(point)),
                                Throws.TypeOf<CryptographicException>(),
                                point);

            });

        }

        #endregion

        #region TheScalarMultiplication_MatchesRfc8032Section71()

        /// <summary>
        /// Our own point arithmetic against RFC 8032, section 7.1.
        /// </summary>
        /// <remarks>
        /// <b>The most important test of this file.</b> The calculation in
        /// <c>Ed25519Math</c> stands there because BouncyCastle does not hand
        /// its <c>ScalarMultBase</c> out - and self-written curve arithmetic is
        /// precisely the place where an error delivers no wrong result but a
        /// plausible one.
        ///
        /// What is checked goes over the detour Ed25519 itself takes: out of
        /// the seed the scalar is formed with SHA-512 and clamping, and
        /// <c>sB</c> has to yield the public key printed in the RFC. With that
        /// the check hangs on foreign numbers and not on our own calculation.
        /// </remarks>
        [Test]
        public void TheScalarMultiplication_MatchesRfc8032Section71()
        {

            (String Seed, String PublicKey)[] vectors = [

               ("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
                "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"),

                ("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
                 "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c"),

                ("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
                 "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025")

            ];

            Assert.Multiple(() =>
            {

                foreach (var (seed, expected) in vectors)
                {

                    var h = SHA512.HashData(Hex(seed))[..32];

                    h[0]  &= 248;
                    h[31] &= 127;
                    h[31] |= 64;

                    Assert.That(Hex(Ed25519Math.ScalarMultBaseEncoded(
                                        new BigInteger(h, isUnsigned: true, isBigEndian: false))),
                                Is.EqualTo(expected),
                                seed);

                }

            });

        }

        #endregion

        #region TheBasePoints_AreTheSamePoint()

        /// <summary>
        /// The conversion from the Montgomery into the Edwards form, checked at
        /// the only point both sides name.
        /// </summary>
        /// <remarks>
        /// X25519 calculates from <c>u = 9</c> (RFC 7748, section 4.1), Ed25519
        /// from its base point (RFC 8032, section 5.1) - and it is the same
        /// point in two spellings. If the conversion does not yield it, it is
        /// wrong, and in such a way that every signature check afterwards fails
        /// without saying why.
        /// </remarks>
        [Test]
        public void TheBasePoints_AreTheSamePoint()
        {

            var u9 = new Byte[32];
            u9[0] = 9;

            Assert.That(Hex(Curve25519.MontgomeryToEdwards(u9)),
                        Is.EqualTo(Hex(Ed25519Math.ScalarMultBaseEncoded(BigInteger.One))
                                       // The u coordinate does not know the sign bit;
                                       // the conversion always delivers it cleared.
                                       .Substring(0, 62) + "66"));

        }

        #endregion

        #region ASignature_VerifiesWithAForeignVerifier()

        /// <summary>
        /// XEdDSA: signed with the Montgomery key, checked with the ordinary
        /// Ed25519 verifier out of BouncyCastle.
        /// </summary>
        /// <remarks>
        /// That is the statement of XEdDSA and at the same time the only
        /// independent check to be had here: there are no published XEdDSA
        /// vectors. The verifier however comes from a foreign hand and does not
        /// know our own calculation - it accepts only what every other Ed25519
        /// verifier accepts as well, and precisely that is what matters: the
        /// far end checks with its own.
        /// </remarks>
        [Test]
        public void ASignature_VerifiesWithAForeignVerifier()
        {

            var theKey   = Curve25519.GenerateKeyPair();
            var message  = Encoding.UTF8.GetBytes("Signed PreKey number 1");

            var signature    = Curve25519.Sign(theKey.PrivateKey, message);

            Assert.Multiple(() =>
            {

                Assert.That(signature.Length, Is.EqualTo(64));

                Assert.That(Curve25519.Verify(theKey.PublicKey, message, signature), Is.True,
                            "Our own signature does not check out.");

                // A different key, the same message.
                Assert.That(Curve25519.Verify(Curve25519.GenerateKeyPair().PublicKey, message, signature),
                            Is.False,
                            "The signature holds for a foreign key as well.");

            });

        }

        #endregion

        #region ATamperedSignature_IsRefused()

        /// <summary>
        /// Every changed place - in the message as in the signature - leads to
        /// a refusal.
        /// </summary>
        [Test]
        public void ATamperedSignature_IsRefused()
        {

            var theKey     = Curve25519.GenerateKeyPair();
            var message    = Encoding.UTF8.GetBytes("Signed PreKey number 1");
            var signature  = Curve25519.Sign(theKey.PrivateKey, message);

            Assert.Multiple(() =>
            {

                var other = Encoding.UTF8.GetBytes("Signed PreKey number 2");
                Assert.That(Curve25519.Verify(theKey.PublicKey, other, signature), Is.False,
                            "A different message is accepted.");

                // Every byte of the signature on its own - R and s.
                for (var i = 0; i < signature.Length; i++)
                {

                    var bent = (Byte[]) signature.Clone();
                    bent[i] ^= 0x01;

                    Assert.That(Curve25519.Verify(theKey.PublicKey, message, bent), Is.False,
                                $"Byte {i} of the signature must not be changeable.");

                }

                Assert.That(Curve25519.Verify(theKey.PublicKey, message, signature[..63]), Is.False,
                            "A signature that is too short is accepted.");

            });

        }

        #endregion

        #region ASpuriousHighBit_IsIgnored()

        /// <summary>
        /// The topmost bit of the u coordinate is discarded on reading
        /// (RFC 7748, section 5).
        /// </summary>
        /// <remarks>
        /// Our own keys never carry it - a u coordinate is smaller than 2^255.
        /// A foreign bundle however comes from the server, and whoever sets the
        /// bit would change the key without this masking: the signature of the
        /// signed PreKey would not check out any more, and the far end would
        /// see an attack where a trifle stands.
        /// </remarks>
        [Test]
        public void ASpuriousHighBit_IsIgnored()
        {

            var pair       = Curve25519.GenerateKeyPair();
            var message    = Encoding.UTF8.GetBytes("with bit 255 set");
            var signature  = Curve25519.Sign(pair.PrivateKey, message);

            var bent   = (Byte[]) pair.PublicKey.Clone();
            bent[31]  |= 0x80;

            Assert.That(Curve25519.Verify(bent, message, signature), Is.True,
                        "The topmost bit was calculated in instead of being discarded.");

        }

        #endregion

        #region BothSignsOfTheScalar_Work()

        /// <summary>
        /// Both signs of the scalar, and that reliably.
        /// </summary>
        /// <remarks>
        /// XEdDSA carries on with <c>-k</c> when <c>kB</c> carries the sign bit
        /// - that is the case with half of all keys. A test with one generated
        /// key therefore does not check this branch in every second run, and an
        /// error in it would look like a flaky test.
        ///
        /// Precisely that happened at the first run: the negation ran beyond
        /// the group order and yielded a negative number. The calculation then
        /// did not come out wrongly but not at all - which is a piece of luck.
        /// The silent case would have been a signature nobody can check.
        ///
        /// Hence so many keys here that both branches occur with a probability
        /// bordering on certainty - and the counting says afterwards that it
        /// was so.
        /// </remarks>
        [Test]
        public void BothSignsOfTheScalar_Work()
        {

            var message  = Encoding.UTF8.GetBytes("both signs");
            var negated  = 0;

            for (var i = 0; i < 32; i++)
            {

                var pair       = Curve25519.GenerateKeyPair();
                var signature  = Curve25519.Sign(pair.PrivateKey, message);

                Assert.That(Curve25519.Verify(pair.PublicKey, message, signature), Is.True,
                            $"Key {i} does not sign checkably.");

                // If kB carries the sign bit, a negation had to happen.
                var kB = Ed25519Math.ScalarMultBaseEncoded(
                             new BigInteger(pair.PrivateKey, isUnsigned: true, isBigEndian: false));

                if ((kB[31] & 0x80) != 0)
                    negated++;

            }

            Assert.That(negated, Is.GreaterThan(0).And.LessThan(32),
                        "Only keys of one sign occurred - the test then checks only half the path.");

        }

        #endregion

        #region TwoSignatures_AreNotTheSame()

        /// <summary>
        /// Twice the same message yields two different signatures.
        /// </summary>
        /// <remarks>
        /// XEdDSA mixes 64 random bytes into the nonce (section 2.4). Without
        /// them the signature would be determined by key and message alone -
        /// with Ed25519 that is on purpose, here it would be a giving away: the
        /// signed PreKey is signed several times over its lifetime, and two
        /// equal signatures would tell somebody reading along that nothing has
        /// changed.
        /// </remarks>
        [Test]
        public void TwoSignatures_AreNotTheSame()
        {

            var theKey   = Curve25519.GenerateKeyPair();
            var message  = Encoding.UTF8.GetBytes("twice the same");

            Assert.That(Hex(Curve25519.Sign(theKey.PrivateKey, message)),
                        Is.Not.EqualTo(Hex(Curve25519.Sign(theKey.PrivateKey, message))));

        }

        #endregion

        #region TheKdf_IsHkdfSha256()

        /// <summary>
        /// RFC 5869, appendix A.1 - which procedure is meant here.
        /// </summary>
        /// <remarks>
        /// What is checked is not our own derivation but the building block
        /// underneath: an exchange of SHA-256 for SHA-1 would otherwise stand
        /// out nowhere. Both deliver bytes, and both can be decrypted again.
        /// </remarks>
        [Test]
        public void TheKdf_IsHkdfSha256()
        {
            Assert.That(
                Hex(HKDF.DeriveKey(HashAlgorithmName.SHA256,
                                   ikm:           Hex("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b"),
                                   salt:          Hex("000102030405060708090a0b0c"),
                                   info:          Hex("f0f1f2f3f4f5f6f7f8f9"),
                                   outputLength:  42)),
                Is.EqualTo("3cb25f25faacd57a90434f64d0362f2a" +
                           "2d2d0a90cf1a5a4c5db02d56ecc4c5bf" +
                           "34007208d5b887185865"));
        }

        #endregion

        #region TheMac_IsHmacSha256()

        /// <summary>RFC 4231, test case 1 - the same for the HMAC.</summary>
        [Test]
        public void TheMac_IsHmacSha256()
        {
            Assert.That(
                Hex(HMACSHA256.HashData(Hex("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b"),
                                        Encoding.UTF8.GetBytes("Hi There"))),
                Is.EqualTo("b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7"));
        }

        #endregion

        #region TheBlockCipher_IsAes256Cbc()

        /// <summary>
        /// NIST SP 800-38A, appendix F.2.5 - the first block of AES-256-CBC.
        /// </summary>
        [Test]
        public void TheBlockCipher_IsAes256Cbc()
        {

            using var aes = Aes.Create();
            aes.Key = Hex("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");

            Assert.That(
                Hex(aes.EncryptCbc(Hex("6bc1bee22e409f96e93d7e117393172a"),
                                   Hex("000102030405060708090a0b0c0d0e0f"),
                                   PaddingMode.None)),
                Is.EqualTo("f58c4c04d6e5f1ba779eabfb5f7bfbd6"));

        }

        #endregion

        #region ThePayloadMaterial_IsSplitAsSpecified()

        /// <summary>
        /// XEP-0384, section 4.4: 80 bytes out of the message key - 32 bytes of
        /// key, 32 bytes of authentication, 16 bytes of IV.
        /// </summary>
        [Test]
        public void ThePayloadMaterial_IsSplitAsSpecified()
        {

            var theKey = RandomNumberGenerator.GetBytes(32);

            var (key, authKey, iv) = OmemoPayloadCipher.Material(theKey);

            Assert.Multiple(() =>
            {

                Assert.That(key.Length,      Is.EqualTo(32));
                Assert.That(authKey.Length,  Is.EqualTo(32));
                Assert.That(iv.Length,       Is.EqualTo(16));

                // The three parts come out of one derivation and have to be
                // different - a key that is at the same time its own IV lifts
                // the mode of operation.
                Assert.That(Hex(key), Is.Not.EqualTo(Hex(authKey)));

                // The same input, the same material: the IV does not travel
                // with the message, the recipient has to derive it.
                var (key2, _, iv2) = OmemoPayloadCipher.Material(theKey);
                Assert.That(Hex(key2), Is.EqualTo(Hex(key)));
                Assert.That(Hex(iv2),  Is.EqualTo(Hex(iv)));

                // A different key, a different IV - otherwise the IV would
                // repeat itself over all messages of one session.
                Assert.That(Hex(OmemoPayloadCipher.Material(RandomNumberGenerator.GetBytes(32)).Iv),
                            Is.Not.EqualTo(Hex(iv)));

            });

        }

        #endregion

        #region ThePayloadMaterial_MatchesASecondImplementation()

        /// <summary>
        /// The same derivation, calculated with a second HKDF - and with the
        /// parameters from XEP-0384, section 4.4 written out literally.
        /// </summary>
        /// <remarks>
        /// <b>This test came about through a surviving mutation.</b> The info
        /// string could be set to <c>""</c> without a test saying anything -
        /// because all of them checked only the structure of the 80 bytes, not
        /// their value. The error would never have stood out in this house: two
        /// clients with the same wrong string understand each other perfectly.
        /// Only a foreign far end - Conversations, Dino, Gajim - would get a
        /// jumble of letters, and there is none of those here.
        ///
        /// That is why the provision stands here a second time and literally:
        /// 32 zero bytes as salt, "OMEMO Payload" as info, 80 bytes of output.
        /// Whoever changes the value in the source has to change it along here
        /// - and then they see that they are leaving the specification.
        ///
        /// The calculation goes over BouncyCastle's HKDF and not over the one
        /// of the BCL: otherwise the same calculation would check itself.
        /// </remarks>
        [Test]
        public void ThePayloadMaterial_MatchesASecondImplementation()
        {

            var theKey = Hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

            var hkdf = new Org.BouncyCastle.Crypto.Generators.HkdfBytesGenerator(
                           new Org.BouncyCastle.Crypto.Digests.Sha256Digest());

            hkdf.Init(new Org.BouncyCastle.Crypto.Parameters.HkdfParameters(
                          theKey,
                          new Byte[32],
                          Encoding.UTF8.GetBytes("OMEMO Payload")));

            var expected = new Byte[80];
            hkdf.GenerateBytes(expected, 0, expected.Length);

            var (key, authKey, iv) = OmemoPayloadCipher.Material(theKey);

            Assert.Multiple(() =>
            {
                Assert.That(Hex(key),      Is.EqualTo(Hex(expected[..32])),  "Cipher key");
                Assert.That(Hex(authKey),  Is.EqualTo(Hex(expected[32..64])), "Authentication key");
                Assert.That(Hex(iv),       Is.EqualTo(Hex(expected[64..])),   "IV");
            });

        }

        #endregion

        #region ThePayload_IsEncryptedAndAuthenticated()

        /// <summary>
        /// The usual path: encrypt, decrypt, and the 48 bytes that go through
        /// the ratchet per recipient.
        /// </summary>
        [Test]
        public void ThePayload_IsEncryptedAndAuthenticated()
        {

            var plaintext = Encoding.UTF8.GetBytes("Shall we meet at eight?");

            var payload = OmemoPayloadCipher.Encrypt(plaintext);

            Assert.Multiple(() =>
            {

                Assert.That(payload.KeyAndHmac.Length, Is.EqualTo(48),
                            "32 bytes of key and 16 bytes of HMAC go through the ratchet.");

                Assert.That(payload.Ciphertext.Length % 16, Is.EqualTo(0),
                            "AES-CBC with PKCS#7 ends on a block boundary.");

                Assert.That(Hex(payload.Ciphertext), Does.Not.Contain(Hex(plaintext)),
                            "The plaintext stands in the ciphertext.");

                Assert.That(OmemoPayloadCipher.Decrypt(payload.Ciphertext, payload.KeyAndHmac),
                            Is.EqualTo(plaintext));

            });

        }

        #endregion

        #region ATamperedPayload_IsRefused()

        /// <summary>
        /// A changed byte in the ciphertext or in the HMAC leads to a refusal -
        /// and not to a jumble of letters.
        /// </summary>
        /// <remarks>
        /// The check happens <b>before</b> the decryption (encrypt-then-MAC).
        /// The other way round the recipient would have to decrypt before
        /// knowing whether they may - and an attacker would get an oracle with
        /// the error messages of the padding, with which CBC can be rolled up
        /// byte by byte.
        /// </remarks>
        [Test]
        public void ATamperedPayload_IsRefused()
        {

            var payload = OmemoPayloadCipher.Encrypt(Encoding.UTF8.GetBytes("Shall we meet at eight?"));

            Assert.Multiple(() =>
            {

                var bent = (Byte[]) payload.Ciphertext.Clone();
                bent[0] ^= 0x01;

                Assert.That(() => OmemoPayloadCipher.Decrypt(bent, payload.KeyAndHmac),
                            Throws.TypeOf<CryptographicException>(),
                            "A changed byte in the ciphertext gets through.");

                var wrongHmac = (Byte[]) payload.KeyAndHmac.Clone();
                wrongHmac[47] ^= 0x01;

                Assert.That(() => OmemoPayloadCipher.Decrypt(payload.Ciphertext, wrongHmac),
                            Throws.TypeOf<CryptographicException>(),
                            "A changed HMAC gets through.");

                var wrongKey = (Byte[]) payload.KeyAndHmac.Clone();
                wrongKey[0] ^= 0x01;

                Assert.That(() => OmemoPayloadCipher.Decrypt(payload.Ciphertext, wrongKey),
                            Throws.TypeOf<CryptographicException>(),
                            "A wrong key gets through - the HMAC hangs on the same material.");

            });

        }

        #endregion

    }

}
