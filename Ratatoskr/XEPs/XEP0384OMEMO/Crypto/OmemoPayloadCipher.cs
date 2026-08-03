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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// An encrypted payload together with what every recipient needs for it.
/// </summary>
/// <param name="Ciphertext">The ciphertext as it goes into the <c>&lt;payload/&gt;</c>.</param>
/// <param name="KeyAndHmac">
/// Key and truncated HMAC one after the other - 48 bytes. Exactly that goes
/// through the double ratchet per recipient (XEP-0384, section 4.4, step 6).
/// </param>
public sealed record OmemoPayload(Byte[] Ciphertext, Byte[] KeyAndHmac);

/// <summary>
/// XEP-0384, section 4.4: the payload itself - AES-256-CBC with HMAC-SHA-256.
/// </summary>
/// <remarks>
/// <b>One key per message, and it does not go to the recipients.</b> The text
/// is encrypted exactly once; through the ratchet goes only the 48-byte value,
/// per recipient. With ten devices that saves no computing time but prevents
/// something: that the same plaintext stands there ten times under different
/// keys.
///
/// <b>The HMAC does not stand beside the message.</b> It travels inside the
/// encrypted part, and that is the actual trick of the procedure: whoever
/// alters the payload cannot alter the HMAC along with it, because they do not
/// know it - and whoever knows it has broken the ratchet. An HMAC standing
/// beside it, by contrast, could be recomputed by anybody who has the key, and
/// the key lies with every recipient.
///
/// Out of the 32-byte key HKDF makes 80 bytes: cipher key, authentication key
/// and IV. That is why no IV travels with the message - it is derived from the
/// key and is a different one for every message, because the key is one too.
/// </remarks>
public static class OmemoPayloadCipher
{

    #region Data

    /// <summary>The info string of the derivation (section 4.4).</summary>
    public const String Info = "OMEMO Payload";

    /// <summary>Length of the message key in bytes.</summary>
    public const Int32 KeyLength = 32;

    /// <summary>
    /// Length of the truncated HMAC in bytes (section 4.4: "Truncate the output
    /// of the HMAC to 16 bytes/128 bits by cutting off excess bytes from the
    /// end").
    /// </summary>
    public const Int32 HmacLength = 16;

    #endregion

    #region Material(key)

    /// <summary>
    /// The 80 bytes of key material from the message key: 32 bytes cipher key,
    /// 32 bytes authentication key, 16 bytes IV.
    /// </summary>
    /// <remarks>
    /// The salt is 32 zero bytes and not nothing. HKDF treats both the same
    /// (RFC 5869, section 2.2 sets a missing salt to exactly these zeros) - but
    /// the specification writes "256 zero-bits as HKDF salt", and whoever takes
    /// a shortcut here has to explain to the next reader why they do something
    /// other than the text.
    /// </remarks>
    public static (Byte[] Key, Byte[] AuthKey, Byte[] Iv) Material(Byte[] messageKey)
    {

        if (messageKey.Length != KeyLength)
            throw new ArgumentException($"The message key has {KeyLength} bytes, not {messageKey.Length}.",
                                        nameof(messageKey));

        var material = HKDF.DeriveKey(HashAlgorithmName.SHA256,
                                      ikm:   messageKey,
                                      salt:  new Byte[32],
                                      info:  Encoding.UTF8.GetBytes(Info),
                                      outputLength: 80);

        return (material[..32], material[32..64], material[64..]);

    }

    #endregion

    #region Encrypt(plaintext) / Decrypt(ciphertext, keyAndHmac)

    /// <summary>
    /// Encrypts the plaintext with a freshly drawn key.
    /// </summary>
    public static OmemoPayload Encrypt(Byte[] plaintext)
        => Encrypt(plaintext, RandomNumberGenerator.GetBytes(KeyLength));

    /// <summary>
    /// Encrypts with a given key - for test vectors and for callers who manage
    /// the key themselves.
    /// </summary>
    public static OmemoPayload Encrypt(Byte[] plaintext, Byte[] messageKey)
    {

        var (key, authKey, iv) = Material(messageKey);

        using var aes = Aes.Create();
        aes.Key = key;

        var ciphertext = aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);

        // Encrypt-then-MAC: what is computed over is the ciphertext, not the
        // plaintext. The other way round the recipient would have to decrypt
        // before knowing whether they may.
        var hmac = HMACSHA256.HashData(authKey, ciphertext)[..HmacLength];

        return new OmemoPayload(ciphertext, [.. messageKey, .. hmac]);

    }

    /// <summary>
    /// Decrypts the payload - or throws when the HMAC is not right.
    /// </summary>
    /// <remarks>
    /// The comparison is in fixed time. A comparison that stops at the first
    /// differing byte betrays, by its duration, how far the attacker has
    /// already got - and with 16 bytes of 256 possibilities each that would be
    /// 4096 attempts instead of 2¹²⁸.
    /// </remarks>
    public static Byte[] Decrypt(Byte[] ciphertext, Byte[] keyAndHmac)
    {

        if (keyAndHmac.Length != KeyLength + HmacLength)
            throw new ArgumentException(
                      $"Key and HMAC have {KeyLength + HmacLength} bytes between them, " +
                      $"not {keyAndHmac.Length}.",
                      nameof(keyAndHmac));

        var (key, authKey, iv) = Material(keyAndHmac[..KeyLength]);

        var expected = HMACSHA256.HashData(authKey, ciphertext)[..HmacLength];

        if (!CryptographicOperations.FixedTimeEquals(expected, keyAndHmac[KeyLength..]))
            throw new CryptographicException(
                      "The HMAC of the payload is not right - it was altered on the way.");

        // The key has to go to the object, not merely the IV to the call: a
        // freshly created Aes has a random key, and DecryptCbc takes it
        // silently. That then decrypted with a key nobody knows - and because
        // the HMAC was right beforehand, everything looked correct until the
        // padding failed.
        using var aes = Aes.Create();
        aes.Key = key;

        return aes.DecryptCbc(ciphertext, iv, PaddingMode.PKCS7);

    }

    #endregion

}
