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

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// Extension methods for salted passwords.
/// </summary>
public static class SaltedPasswordExtensions
{

    /// <summary>Is this salted password absent or unset?</summary>
    public static Boolean IsNullOrEmpty(this SaltedPassword? SaltedPassword)
        => !SaltedPassword.HasValue || SaltedPassword.Value.IsNullOrEmpty;

    /// <summary>Is this salted password present?</summary>
    public static Boolean IsNotNullOrEmpty(this SaltedPassword? SaltedPassword)
        => SaltedPassword.HasValue && SaltedPassword.Value.IsNotNullOrEmpty;

}


/// <summary>
/// <c>SaltedPassword = Hi(Normalize(password), salt, i)</c> - RFC 5802,
/// section 3, together with the three parameters it was derived under.
/// </summary>
/// <remarks>
/// <b>Derived once and kept.</b> The PBKDF2 behind it is deliberately
/// expensive - servers name iteration counts in the tens of thousands - and it
/// was being paid again at every reconnect, for the same password, the same
/// salt and the same count. It is the same value every time; there is no reason
/// to compute it twice.
///
/// <b>The parameters travel with it, and that is not decoration.</b> A salted
/// password is only valid for the mechanism, salt and iteration count it was
/// derived under; the server names all three afresh at every authentication and
/// may change any of them. Carrying them here is what lets
/// <see cref="Matches"/> answer whether the kept value still applies, instead
/// of the caller remembering three loose variables and comparing them by hand -
/// where forgetting the salt would not fail loudly but authenticate with the
/// wrong key.
///
/// <b>The bytes do not come out</b>, save through <see cref="ToArray"/>, which
/// exists for the one caller that must transmit them: XEP-0480's upgrade task
/// sends the salted password itself. Everything else asks for what it actually
/// wants - <see cref="ClientKey"/>, <see cref="StoredKey"/>,
/// <see cref="ServerKey"/> - so the secret stays behind the three derivations
/// RFC 5802 defines rather than being passed around as a byte array anyone may
/// keep a reference to.
///
/// <b>What this does not do is keep the password out of memory.</b> It cannot:
/// the plaintext has to exist for the length of one PBKDF2. What it does is
/// make that the only moment - one derivation per parameter set instead of one
/// per connection.
/// </remarks>
public readonly struct SaltedPassword : IEquatable<SaltedPassword>
{

    #region Data

    /// <summary>
    /// The derived key material. Private, and copied in and out, so that a
    /// caller cannot reach into it - a <c>readonly</c> field holding an array
    /// protects the reference and not one byte of the contents.
    /// </summary>
    private readonly Byte[]? bytes;

    private readonly Byte[]? salt;

    #endregion

    #region Properties

    /// <summary>Which SCRAM mechanism this was derived for.</summary>
    public SCRAMMechanism  Mechanism     { get; }

    /// <summary>How many PBKDF2 iterations went into it.</summary>
    public UInt32          Iterations    { get; }

    /// <summary>The salt it was derived with.</summary>
    public Byte[]          Salt

        => salt is null ? [] : [.. salt];

    /// <summary>Is this the default, which is no key at all?</summary>
    [MemberNotNullWhen(false, nameof(bytes))]
    public Boolean         IsNullOrEmpty

        => bytes is null || bytes.Length == 0;

    /// <summary>Is there key material here?</summary>
    [MemberNotNullWhen(true, nameof(bytes))]
    public Boolean         IsNotNullOrEmpty

        => bytes is not null && bytes.Length > 0;

    #endregion

    #region Constructor(s)

    private SaltedPassword(SCRAMMechanism  Mechanism,
                           Byte[]          Bytes,
                           Byte[]          Salt,
                           UInt32          Iterations)
    {

        this.Mechanism   = Mechanism;
        this.bytes       = Bytes;
        this.salt        = Salt;
        this.Iterations  = Iterations;

    }

    #endregion


    #region (static) Derive(Mechanism, Password, Salt, Iterations)

    /// <summary>
    /// RFC 5802, section 3: <c>Hi(Normalize(password), salt, i)</c>.
    /// </summary>
    /// <param name="Mechanism">Which SCRAM mechanism - it decides the hash and the output length.</param>
    /// <param name="Password">The password, already through SASLprep.</param>
    /// <param name="Salt">The salt the server named.</param>
    /// <param name="Iterations">The iteration count the server named.</param>
    /// <remarks>
    /// The output length is the hash length and not a free choice: RFC 5802
    /// defines SaltedPassword as the output of Hi with the mechanism's own hash
    /// function, and HMAC over a longer key would silently be HMAC over a
    /// different key than the server computed.
    /// </remarks>
    public static SaltedPassword Derive(SCRAMMechanism  Mechanism,
                                        String          Password,
                                        Byte[]          Salt,
                                        UInt32          Iterations)
    {

        var hashName = Mechanism == SCRAMMechanism.ScramSha256
                           ? HashAlgorithmName.SHA256
                           : HashAlgorithmName.SHA1;

        var derived  = Rfc2898DeriveBytes.Pbkdf2(
                           Encoding.UTF8.GetBytes(Password),
                           Salt,
                           (Int32) Iterations,
                           hashName,
                           hashName == HashAlgorithmName.SHA256 ? 32 : 20
                       );

        return new SaltedPassword(Mechanism, derived, [.. Salt], Iterations);

    }

    #endregion

    #region (static) FromBytes(Mechanism, Bytes, Salt, Iterations)

    /// <summary>
    /// A salted password that was computed elsewhere - by the far end of
    /// XEP-0480's upgrade task, or read back from a store.
    /// </summary>
    public static SaltedPassword FromBytes(SCRAMMechanism  Mechanism,
                                           Byte[]          Bytes,
                                           Byte[]          Salt,
                                           UInt32          Iterations)

        => new (Mechanism, [.. Bytes], [.. Salt], Iterations);

    #endregion


    #region Matches(Mechanism, Salt, Iterations)

    /// <summary>
    /// Does this salted password still apply to what the server has just
    /// named?
    /// </summary>
    /// <remarks>
    /// All three have to agree. A changed salt is the one that would otherwise
    /// go unnoticed - the authentication would simply fail, with a message
    /// about a wrong password for a password that is right.
    /// </remarks>
    public Boolean Matches(SCRAMMechanism  Mechanism,
                           Byte[]          Salt,
                           UInt32          Iterations)

        => IsNotNullOrEmpty              &&
           this.Mechanism  == Mechanism  &&
           this.Iterations == Iterations &&
           salt is not null              &&
           salt.AsSpan().SequenceEqual(Salt);

    #endregion

    #region ClientKey() / StoredKey() / ServerKey()

    /// <summary>RFC 5802: <c>ClientKey = HMAC(SaltedPassword, "Client Key")</c>.</summary>
    public Byte[] ClientKey()

        => Hmac("Client Key"u8.ToArray());

    /// <summary>RFC 5802: <c>StoredKey = H(ClientKey)</c> - what a server keeps.</summary>
    public Byte[] StoredKey()
    {

        using var hash = Mechanism == SCRAMMechanism.ScramSha256
                             ? SHA256.Create()
                             : (HashAlgorithm) SHA1.Create();

        return hash.ComputeHash(ClientKey());

    }

    /// <summary>RFC 5802: <c>ServerKey = HMAC(SaltedPassword, "Server Key")</c>.</summary>
    public Byte[] ServerKey()

        => Hmac("Server Key"u8.ToArray());

    private Byte[] Hmac(Byte[] Data)
    {

        if (IsNullOrEmpty)
            throw new InvalidOperationException(
                      "This salted password is the default and holds no key material.");

        using HMAC hmac = Mechanism == SCRAMMechanism.ScramSha256
                              ? new HMACSHA256(bytes)
                              : new HMACSHA1  (bytes);

        return hmac.ComputeHash(Data);

    }

    #endregion

    #region ToArray()

    /// <summary>
    /// The key material itself, as a copy.
    /// </summary>
    /// <remarks>
    /// For the one caller that has to transmit it: XEP-0480, section 3 has the
    /// client send <c>base64(SaltedPassword)</c> as the upgrade task's answer.
    /// Anything else wants <see cref="ClientKey"/>, <see cref="StoredKey"/> or
    /// <see cref="ServerKey"/> instead.
    /// </remarks>
    public Byte[] ToArray()

        => bytes is null ? [] : [.. bytes];

    #endregion


    #region Operator overloading

    public static Boolean operator == (SaltedPassword SaltedPassword1, SaltedPassword SaltedPassword2)
        =>  SaltedPassword1.Equals(SaltedPassword2);

    public static Boolean operator != (SaltedPassword SaltedPassword1, SaltedPassword SaltedPassword2)
        => !SaltedPassword1.Equals(SaltedPassword2);

    #endregion

    #region IEquatable<SaltedPassword> Members

    /// <summary>
    /// Equal when the parameters and the key material agree.
    /// </summary>
    /// <remarks>
    /// The key material is compared through
    /// <see cref="CryptographicOperations.FixedTimeEquals"/>. There is no
    /// pressing attack on this particular comparison - both sides are already
    /// in this process - but a secret compared with <c>SequenceEqual</c> is a
    /// habit that travels, and the cost here is nothing.
    /// </remarks>
    public Boolean Equals(SaltedPassword Other)
    {

        if (Mechanism  != Other.Mechanism ||
            Iterations != Other.Iterations)
            return false;

        if (bytes is null || Other.bytes is null)
            return bytes is null && Other.bytes is null;

        return CryptographicOperations.FixedTimeEquals(bytes, Other.bytes);

    }

    public override Boolean Equals(Object? Object)

        => Object is SaltedPassword saltedPassword &&
           Equals(saltedPassword);

    /// <summary>
    /// Over the parameters only - never over the key material.
    /// </summary>
    /// <remarks>
    /// Equal values still hash equally, because equal values have equal
    /// parameters. Hashing the secret would put a fingerprint of it into every
    /// container it is dropped into, and buy nothing: nobody keeps thousands of
    /// these in a dictionary.
    /// </remarks>
    public override Int32 GetHashCode()

        => HashCode.Combine(Mechanism,
                            Iterations,
                            salt is null ? 0 : salt.Length,
                            bytes is null ? 0 : bytes.Length);

    #endregion

    #region ToString()

    /// <summary>
    /// The parameters, and expressly not the key.
    /// </summary>
    public override String ToString()

        => IsNullOrEmpty
               ? "(none)"
               : $"{Mechanism}, {Iterations} iterations, {salt?.Length ?? 0} bytes of salt";

    #endregion

}
