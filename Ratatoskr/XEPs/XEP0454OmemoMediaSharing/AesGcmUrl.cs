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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;


#region (record) EncryptedFile

/// <summary>
/// XEP-0454: a file ready to be stored, and the key that is not stored with it.
/// </summary>
/// <param name="Payload">
/// What goes to the upload service: ciphertext followed by the authentication
/// tag.
/// </param>
/// <param name="Key">The key. It travels to the recipient and nowhere else.</param>
/// <param name="Nonce">The 12 byte IV, which travels with the key.</param>
/// <remarks>
/// The three are kept apart on purpose. <see cref="Payload"/> is the only part
/// that may be handed to anything that stores or transmits it; the other two go
/// into a URL fragment, which is the one piece of a URL that is never sent to
/// the host.
/// </remarks>
public sealed record EncryptedFile(Byte[] Payload, Byte[] Key, Byte[] Nonce);

#endregion


/// <summary>
/// XEP-0454: a shared file whose key travels in the URL.
/// </summary>
/// <remarks>
/// <c>aesgcm://host/path#[iv][key]</c>, the fragment in hex. The fragment never
/// leaves the client - a URL fragment is not sent to the server - so the host
/// storing the file cannot read it. What that also means is that whoever
/// receives the message receives the key: the encryption protects the file
/// against the storage, not against the conversation.
///
/// <b>What is here and what is not.</b> This encrypts and decrypts, reads and
/// writes the URL, and it does not fetch or store anything. Moving the bytes is
/// a decision an application has to make and a library must not make for it:
/// whether an incoming message may cause a request at all, how large a file may
/// be, how long it may take, which addresses are refused. A protocol library
/// that downloads on its own hands that decision to whoever sends the message.
///
/// The IV is 12 bytes as everybody sends it, and 16 in an older reading of the
/// XEP. Only 12 can be used here, because AES-GCM in .NET takes no other nonce
/// length. That is named rather than caught silently: a file nobody can decrypt
/// should say so, not appear to have been stored.
/// </remarks>
public static class AesGcmUrl
{

    /// <summary>
    /// The scheme this recognises.
    /// </summary>
    public const String Scheme = "aesgcm";

    /// <summary>
    /// The only nonce length AES-GCM takes here (RFC 5116, section 5.1 - and
    /// <see cref="AesGcm"/> allows no other).
    /// </summary>
    public const Int32 NonceLength = 12;


    #region IsAesGcmUrl(URL)

    /// <summary>
    /// Is this one of ours? The scheme alone says so - nothing else uses it.
    /// </summary>
    public static Boolean IsAesGcmUrl(Uri URL)

        => URL.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase);

    #endregion

    #region TryParse(URL, out Key, out Nonce, out Problem)

    /// <summary>
    /// Reads key and nonce out of the fragment.
    /// </summary>
    /// <param name="Problem">
    /// Why it could not be read. Meant to be shown or written down: a file that
    /// was not stored should say what was missing.
    /// </param>
    public static Boolean TryParse(Uri          URL,
                                   out Byte[]?  Key,
                                   out Byte[]?  Nonce,
                                   out String?  Problem)
    {

        Key      = null;
        Nonce    = null;
        Problem  = null;

        if (!IsAesGcmUrl(URL))
        {
            Problem = $"not an {Scheme} URL";
            return false;
        }

        // Uri keeps the '#'. An empty fragment and a missing one are the same
        // thing here: no key.
        var fragment = URL.Fragment.TrimStart('#');

        if (fragment.Length == 0)
        {
            Problem = "no key in the URL fragment";
            return false;
        }

        Byte[] material;

        try
        {
            material = Convert.FromHexString(fragment);
        }
        catch (FormatException)
        {
            Problem = "the URL fragment is not hex";
            return false;
        }

        // 12 + 32 is what is sent in practice; 12 + 16 is a shorter key and
        // equally usable. 16 + 32 is the older reading of the XEP and cannot be
        // decrypted here.
        if (material.Length == 48)
        {
            Problem = "16 byte IV (older XEP-0454 form); AES-GCM here takes 12 bytes only";
            return false;
        }

        var keyLength = material.Length - NonceLength;

        if (keyLength != 32 && keyLength != 16)
        {
            Problem = $"unexpected key material of {material.Length} bytes";
            return false;
        }

        Nonce  = material[..NonceLength];
        Key    = material[NonceLength..];

        return true;

    }

    #endregion

    #region ToHttps(URL)

    /// <summary>
    /// The address the file actually lies at.
    /// </summary>
    /// <remarks>
    /// The fragment is dropped along the way, and deliberately: it is the key.
    /// It would not be sent by an HTTP client anyway, but a URL carrying a key
    /// has no business being passed around a program either, where it may end
    /// up in a log line or an error message.
    /// </remarks>
    public static Uri ToHttps(Uri URL)

        => new UriBuilder(URL) {
               Scheme    = "https",
               Fragment  = "",
               Port      = URL.IsDefaultPort ? -1 : URL.Port
           }.Uri;

    #endregion

    #region Encrypt(Plaintext, KeyLength = 32)

    /// <summary>
    /// Encrypts a file and draws the key it travels with.
    /// </summary>
    /// <param name="Plaintext">The file.</param>
    /// <param name="KeyLength">32 bytes, or 16 for an equally usable shorter key.</param>
    /// <remarks>
    /// <b>The key and the nonce are drawn here and cannot be passed in, and that
    /// is the one thing this method is strict about.</b> AES-GCM forgives
    /// nothing about a repeated nonce under the same key: two files encrypted
    /// with the same pair hand anybody who has both the difference of their
    /// plaintexts, and - worse, because it is silent - the material to forge an
    /// authentication tag that the receiving side will accept. An overload
    /// taking a nonce would be used to encrypt two versions of the same picture,
    /// which is exactly the case that loses everything.
    ///
    /// Every file therefore gets a new key as well as a new nonce. Reusing the
    /// key across files would be safe with fresh nonces and is still not done:
    /// the key travels in the URL to whoever is being sent the file, so a key
    /// shared between files is a second file handed to whoever was sent the
    /// first.
    ///
    /// What comes back is the payload exactly as it has to be stored -
    /// ciphertext followed by the 16 byte tag, which is what
    /// <see cref="AesGcm"/> writes and what XEP-0454 prescribes.
    /// </remarks>
    public static EncryptedFile Encrypt(ReadOnlySpan<Byte>  Plaintext,
                                        Int32               KeyLength = 32)
    {

        if (KeyLength is not (16 or 32))
            throw new ArgumentOutOfRangeException(
                      nameof(KeyLength),
                      "XEP-0454 travels a 32 byte key, or a 16 byte one; nothing else is read back.");

        var key         = RandomNumberGenerator.GetBytes(KeyLength);
        var nonce       = RandomNumberGenerator.GetBytes(NonceLength);

        var tagLength   = AesGcm.TagByteSizes.MaxSize;
        var payload     = new Byte[Plaintext.Length + tagLength];

        using var aes = new AesGcm(key, tagLength);

        aes.Encrypt(nonce,
                    Plaintext,
                    payload.AsSpan(0, Plaintext.Length),
                    payload.AsSpan(Plaintext.Length));

        return new EncryptedFile(payload, key, nonce);

    }

    #endregion

    #region ToAesGcm(URL, Key, Nonce)

    /// <summary>
    /// The address to send somebody: where the file lies, with the key on it.
    /// </summary>
    /// <param name="URL">Where the ciphertext was stored - the <c>https</c> address.</param>
    /// <remarks>
    /// The inverse of <see cref="ToHttps"/>, and the fragment is why the scheme
    /// changes at all: <c>aesgcm://</c> is not a transport but a mark saying
    /// "there is a key on the end of this, and it is not to be sent to the
    /// host". No HTTP client would send a fragment anyway; the scheme is what
    /// stops a program from treating the address as an ordinary link and
    /// handing it somewhere it does not belong.
    ///
    /// Lower-case hex, because that is what is sent. The XEP says hex and
    /// nothing about case - <see cref="TryParse"/> therefore reads either, and
    /// writing the common one keeps us out of the corner where a stricter
    /// reader lives.
    /// </remarks>
    public static Uri ToAesGcm(Uri     URL,
                               Byte[]  Key,
                               Byte[]  Nonce)
    {

        if (Nonce.Length != NonceLength)
            throw new ArgumentException(
                      $"The nonce has to be {NonceLength} bytes; this one is {Nonce.Length}.",
                      nameof(Nonce));

        var material = new Byte[Nonce.Length + Key.Length];

        Nonce.CopyTo(material, 0);
        Key.  CopyTo(material, Nonce.Length);

        return new UriBuilder(URL) {
                   Scheme    = Scheme,
                   Fragment  = Convert.ToHexString(material).ToLowerInvariant(),
                   Port      = URL.IsDefaultPort ? -1 : URL.Port
               }.Uri;

    }

    #endregion

    #region Decrypt(Payload, Key, Nonce)

    /// <summary>
    /// Decrypts what was downloaded.
    /// </summary>
    /// <remarks>
    /// The authentication tag is the last 16 bytes of the payload, as XEP-0454
    /// prescribes. It is not optional: without checking it, the storage host
    /// could hand back anything it liked and the caller would take it for the
    /// received file. <see cref="AesGcm"/> throws when it does not match, and
    /// that throw is the check.
    /// </remarks>
    public static Byte[] Decrypt(ReadOnlySpan<Byte> Payload,
                                 Byte[]             Key,
                                 Byte[]             Nonce)
    {

        if (Payload.Length <= AesGcm.TagByteSizes.MaxSize)
            throw new CryptographicException(
                      $"The file is {Payload.Length} bytes and therefore not even " +
                      $"as long as the authentication tag it has to carry.");

        var tagLength   = AesGcm.TagByteSizes.MaxSize;
        var ciphertext  = Payload[..^tagLength];
        var tag         = Payload[^tagLength..];
        var plaintext   = new Byte[ciphertext.Length];

        using var aes = new AesGcm(Key, tagLength);

        aes.Decrypt(Nonce, ciphertext, tag, plaintext);

        return plaintext;

    }

    #endregion

}
