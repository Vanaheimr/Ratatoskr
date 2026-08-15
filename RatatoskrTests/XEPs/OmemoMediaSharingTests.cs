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
    /// XEP-0454: the URL that carries the key to the file it points at.
    /// </summary>
    /// <remarks>
    /// The encryption here protects the file against the host storing it, not
    /// against the conversation - everybody who gets the message gets the key.
    /// What the tests are therefore about is the other half: that the key never
    /// travels to that host, and that a file which has been altered is refused
    /// rather than handed on.
    /// </remarks>
    [TestFixture]
    public class OmemoMediaSharingTests
    {

        #region AnAesgcmUrl_YieldsKeyAndNonce()

        [Test]
        public void AnAesgcmUrl_YieldsKeyAndNonce()
        {

            var nonce  = Convert.ToHexString(new Byte[12]);
            var key    = Convert.ToHexString(Enumerable.Range(0, 32).Select(i => (Byte) i).ToArray());

            var parsed = AesGcmUrl.TryParse(new Uri($"aesgcm://a.org/b#{nonce}{key}"),
                                            out var parsedKey, out var parsedNonce, out var problem);

            Assert.Multiple(() =>
            {
                Assert.That(parsed,          Is.True, problem);
                Assert.That(parsedNonce,     Has.Length.EqualTo(12));
                Assert.That(parsedKey,       Has.Length.EqualTo(32));
                Assert.That(parsedKey![0],   Is.EqualTo(0));
                Assert.That(parsedKey![31],  Is.EqualTo(31));
            });

        }

        #endregion

        #region AShorterKey_IsAlsoRead()

        /// <summary>
        /// 128 bit is a key length AES takes, and the XEP does not forbid it.
        /// </summary>
        [Test]
        public void AShorterKey_IsAlsoRead()
        {

            var material = new String('a', 24) + new String('b', 32);   // 12 + 16 bytes

            Assert.Multiple(() =>
            {
                Assert.That(AesGcmUrl.TryParse(new Uri($"aesgcm://a.org/b#{material}"),
                                               out var key, out _, out var problem),
                            Is.True, problem);
                Assert.That(key, Has.Length.EqualTo(16));
            });

        }

        #endregion

        #region AnUnusableKey_SaysWhy()

        /// <summary>
        /// A file that cannot be decrypted has to say what was missing. The
        /// older 16 byte IV is the case that actually occurs - and the one
        /// AES-GCM here cannot take.
        /// </summary>
        [Test]
        public void AnUnusableKey_SaysWhy()
        {

            var sixteenByteIv = new String('a', 32) + new String('b', 64);

            Assert.Multiple(() =>
            {

                Assert.That(AesGcmUrl.TryParse(new Uri($"aesgcm://a.org/b#{sixteenByteIv}"),
                                               out _, out _, out var ivProblem), Is.False);
                Assert.That(ivProblem, Does.Contain("16 byte IV"));

                Assert.That(AesGcmUrl.TryParse(new Uri("aesgcm://a.org/b"),
                                               out _, out _, out var noKey), Is.False);
                Assert.That(noKey, Does.Contain("no key"));

                Assert.That(AesGcmUrl.TryParse(new Uri("aesgcm://a.org/b#zzzz"),
                                               out _, out _, out var notHex), Is.False);
                Assert.That(notHex, Does.Contain("hex"));

                Assert.That(AesGcmUrl.TryParse(new Uri("https://a.org/b#" + new String('a', 88)),
                                               out _, out _, out var wrongScheme), Is.False);
                Assert.That(wrongScheme, Does.Contain("not an aesgcm URL"));

            });

        }

        #endregion

        #region TheKeyNeverReachesTheServer()

        /// <summary>
        /// The address the file is fetched from carries no fragment - and the
        /// fragment is the key.
        /// </summary>
        /// <remarks>
        /// An HTTP client would not send a fragment anyway. What is kept here
        /// is the step before that: the URL a caller passes on, writes into a
        /// log or puts into an error message no longer contains the key.
        /// </remarks>
        [Test]
        public void TheKeyNeverReachesTheServer()
        {

            var url   = new Uri("aesgcm://up.example.org/abc/photo.jpg#" + new String('a', 88));
            var https = AesGcmUrl.ToHttps(url);

            Assert.Multiple(() =>
            {
                Assert.That(https.Scheme,      Is.EqualTo("https"));
                Assert.That(https.Fragment,    Is.Empty);
                Assert.That(https.AbsoluteUri, Is.EqualTo("https://up.example.org/abc/photo.jpg"));
            });

        }

        #endregion

        #region ANonDefaultPort_Survives()

        /// <summary>
        /// The port belongs to the address and not to the scheme.
        /// </summary>
        [Test]
        public void ANonDefaultPort_Survives()
        {

            var https = AesGcmUrl.ToHttps(new Uri("aesgcm://up.example.org:5443/a/b.jpg#" + new String('a', 88)));

            Assert.That(https.AbsoluteUri, Is.EqualTo("https://up.example.org:5443/a/b.jpg"));

        }

        #endregion

        #region AnEncryptedFile_ComesBackAsItWent()

        [Test]
        public void AnEncryptedFile_ComesBackAsItWent()
        {

            var content = Encoding.UTF8.GetBytes("the file that was shared");
            var payload = Encrypt(content, out var key, out var nonce);

            Assert.That(AesGcmUrl.Decrypt(payload, key, nonce), Is.EqualTo(content));

        }

        #endregion

        #region AChangedFile_IsRefusedRatherThanReturned()

        /// <summary>
        /// The authentication tag is not decoration. Without checking it, the
        /// host storing the file could hand back anything at all and the caller
        /// would take it for the file that was sent.
        /// </summary>
        [Test]
        public void AChangedFile_IsRefusedRatherThanReturned()
        {

            var payload = Encrypt(Encoding.UTF8.GetBytes("the file that was shared"),
                                  out var key, out var nonce);

            payload[3] ^= 0xFF;

            Assert.Catch<CryptographicException>(() => AesGcmUrl.Decrypt(payload, key, nonce));

        }

        #endregion

        #region APayloadShorterThanItsTag_SaysSo()

        /// <summary>
        /// An empty or truncated answer is not a decryption failure but a
        /// download that did not happen. It gets a message of its own rather
        /// than an index out of range.
        /// </summary>
        [Test]
        public void APayloadShorterThanItsTag_SaysSo()
        {

            var key    = RandomNumberGenerator.GetBytes(32);
            var nonce  = RandomNumberGenerator.GetBytes(AesGcmUrl.NonceLength);

            Assert.Catch<CryptographicException>(() => AesGcmUrl.Decrypt(new Byte[8], key, nonce));

        }

        #endregion

        #region (private) Encrypt(Content, out Key, out Nonce)

        /// <summary>
        /// What a sending client does: encrypt, then append the tag.
        /// </summary>
        private static Byte[] Encrypt(Byte[] Content, out Byte[] Key, out Byte[] Nonce)
        {

            Key    = RandomNumberGenerator.GetBytes(32);
            Nonce  = RandomNumberGenerator.GetBytes(AesGcmUrl.NonceLength);

            var cipher  = new Byte[Content.Length];
            var tag     = new Byte[AesGcm.TagByteSizes.MaxSize];

            using (var aes = new AesGcm(Key, tag.Length))
                aes.Encrypt(Nonce, Content, cipher, tag);

            return [.. cipher, .. tag];

        }

        #endregion

    }

}
