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

using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Math.EC.Rfc8032;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// A Curve25519 key pair: 32 bytes secret, 32 bytes public.
/// </summary>
/// <remarks>
/// The public part is the Montgomery u coordinate as it goes over the wire
/// (RFC 7748). For the signature it is converted into the Edwards form - see
/// <see cref="Curve25519.Verify"/>.
/// </remarks>
public sealed class Curve25519KeyPair
{

    /// <summary>
    /// The secret part, already clamped.
    /// </summary>
    public Byte[] PrivateKey { get; }

    /// <summary>
    /// The public part, 32 bytes of Montgomery u.
    /// </summary>
    public Byte[] PublicKey { get; }

    internal Curve25519KeyPair(Byte[] privateKey, Byte[] publicKey)
    {
        PrivateKey  = privateKey;
        PublicKey   = publicKey;
    }

}

/// <summary>
/// Curve25519 for OMEMO: key agreement per RFC 7748 and signatures per XEdDSA.
/// </summary>
/// <remarks>
/// <b>Why XEdDSA and not simply Ed25519?</b> OMEMO has exactly <i>one</i> key
/// per identity, and it has to be able to do both: agree on a shared secret
/// (only the Montgomery form can do that) and sign the signed prekey (only the
/// Edwards form can do that). XEdDSA converts the key for the signature instead
/// of demanding a second one - for a second key would be a second fingerprint,
/// and the human being who is supposed to compare it has only one in their
/// head.
///
/// The computing is done with BouncyCastle. To write the actual curve
/// arithmetic oneself would be the one place where an error costs nothing until
/// it costs everything: a wrong multiplication delivers not a wrong result but
/// a plausible one.
/// </remarks>
public static class Curve25519
{

    #region Data

    /// <summary>
    /// Length of a key in bytes.
    /// </summary>
    public const Int32 KeyLength        = 32;

    /// <summary>
    /// Length of a signature in bytes.
    /// </summary>
    public const Int32 SignatureLength  = 64;

    /// <summary>
    /// The prime field: 2^255 - 19.
    /// </summary>
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;

    /// <summary>
    /// The group order: 2^252 + 27742317777372353535851937790883648493.
    /// </summary>
    private static readonly BigInteger Q = BigInteger.Pow(2, 252) +
                                           BigInteger.Parse("27742317777372353535851937790883648493");

    #endregion

    #region Keys

    /// <summary>
    /// A new key pair from the random generator of the operating system.
    /// </summary>
    public static Curve25519KeyPair GenerateKeyPair()
        => KeyPairFromPrivate(RandomNumberGenerator.GetBytes(KeyLength));

    /// <summary>
    /// The key pair for a given secret part - for stored keys and for test
    /// vectors.
    /// </summary>
    /// <remarks>
    /// The secret part is stored <b>clamped</b> (RFC 7748, section 5): the
    /// lowest three bits cleared, the top one cleared, the second-from-top one
    /// set. That belongs here and not only in the agreement: XEdDSA goes on
    /// computing with the same scalar, and an unclamped one would yield a
    /// signature that does not fit one's own public key.
    /// </remarks>
    public static Curve25519KeyPair KeyPairFromPrivate(Byte[] privateKey)
    {

        if (privateKey.Length != KeyLength)
            throw new ArgumentException($"A Curve25519 key has {KeyLength} bytes, not {privateKey.Length}.",
                                        nameof(privateKey));

        var clamped = (Byte[]) privateKey.Clone();

        clamped[0]  &= 248;
        clamped[31] &= 127;
        clamped[31] |= 64;

        var publicKey = new Byte[KeyLength];
        X25519.ScalarMultBase(clamped, 0, publicKey, 0);

        return new Curve25519KeyPair(clamped, publicKey);

    }

    #endregion

    #region Agree(ownPrivateKey, otherPublicKey)

    /// <summary>
    /// The shared secret per RFC 7748 - 32 bytes.
    /// </summary>
    /// <remarks>
    /// A result of nothing but zeros is refused. It comes about when the other
    /// side sends a point of small order, and is then no secret but a number
    /// the attacker knows beforehand. RFC 7748, section 6.1 leaves the check
    /// optional; optional it is only where the public key comes from a
    /// trustworthy source - an OMEMO bundle comes from the server.
    /// </remarks>
    public static Byte[] Agree(Byte[] ownPrivateKey, Byte[] otherPublicKey)
    {

        if (ownPrivateKey.Length  != KeyLength ||
            otherPublicKey.Length != KeyLength)
            throw new ArgumentException($"Curve25519 keys have {KeyLength} bytes.");

        var shared = new Byte[KeyLength];

        if (!X25519.CalculateAgreement(ownPrivateKey, 0, otherPublicKey, 0, shared, 0))
            throw new CryptographicException(
                      "The key agreement yielded nothing but zeros - the other side has sent a " +
                      "point of small order.");

        return shared;

    }

    #endregion

    #region Sign(privateKey, message) / Verify(publicKey, message, signature)

    /// <summary>
    /// Signs a message with the Montgomery key (XEdDSA).
    /// </summary>
    /// <remarks>
    /// The procedure comes from Signal's XEdDSA paper, section 2.4:
    /// <list type="number">
    /// <item>Determine the Edwards pair from the scalar <c>k</c> and choose the
    ///       sign so that the public point does not carry it.</item>
    /// <item><c>r</c> from <c>hash₁(a ‖ M ‖ Z)</c>, with 64 random bytes
    ///       <c>Z</c>.</item>
    /// <item><c>R = rB</c>, <c>h = SHA-512(R ‖ A ‖ M)</c>,
    ///       <c>s = r + h·a</c>.</item>
    /// </list>
    ///
    /// The <c>Z</c> is no decoration: without the random part <c>r</c> would be
    /// determined by the key and the message alone, and two signatures over the
    /// same message would be equal byte for byte. With Ed25519 that is
    /// deliberate and here it is a disclosure - the signed prekey is signed
    /// several times over its lifetime.
    ///
    /// The <c>hash₁</c> is SHA-512 with the prefix <c>0xFE</c> followed by 31
    /// bytes of <c>0xFF</c>. The prefix separates this use of the hash from the
    /// one in Ed25519 itself - without it the two procedures could be used
    /// against each other as an oracle.
    /// </remarks>
    public static Byte[] Sign(Byte[] privateKey, Byte[] message)
    {

        var (a, aPoint) = EdwardsKeyPair(privateKey);

        var z = RandomNumberGenerator.GetBytes(64);

        // hash₁(a ‖ M ‖ Z)
        var prefix = new Byte[32];
        prefix[0] = 0xFE;
        for (var i = 1; i < 32; i++)
            prefix[i] = 0xFF;

        var r = ReduceMod(SHA512.HashData([.. prefix, .. ToLittleEndian(a), .. message, .. z]), Q);

        var bigR = Ed25519Math.ScalarMultBaseEncoded(r);

        var h = ReduceMod(SHA512.HashData([.. bigR, .. aPoint, .. message]), Q);
        var s = (r + h * a) % Q;

        Byte[] signature = [.. bigR, .. ToLittleEndian(s)];

        // Check one's own signature before it leaves the house - with the
        // foreign verifier from BouncyCastle and not with the computation
        // above. That costs one verification and turns every computational
        // error here into an exception instead of a signature nobody can make
        // sense of. A signed prekey with a wrong signature otherwise shows up
        // only at the other side, and there it looks like an attack.
        if (!Verify(KeyPairFromPrivate(privateKey).PublicKey, message, signature))
            throw new CryptographicException(
                      "The XEdDSA signature produced does not verify against itself - " +
                      "the computation here is not right.");

        return signature;

    }

    /// <summary>
    /// Checks an XEdDSA signature against the Montgomery key.
    /// </summary>
    /// <remarks>
    /// It is checked with the ordinary Ed25519 procedure from BouncyCastle,
    /// after the public key has been converted. That is no convenience but the
    /// statement of XEdDSA: an XEdDSA signature <b>is</b> an Ed25519 signature
    /// for the converted key.
    /// </remarks>
    public static Boolean Verify(Byte[] publicKey, Byte[] message, Byte[] signature)
    {

        if (publicKey.Length != KeyLength)
            return false;

        try
        {
            return VerifyEdwards(MontgomeryToEdwards(publicKey), message, signature);
        }
        catch (Exception)
        {
            return false;
        }

    }

    /// <summary>
    /// Checks a signature against the key <b>in Ed25519 form</b>.
    /// </summary>
    /// <remarks>
    /// <b>The same question, two spellings of the key - and that is the trap of
    /// this extension.</b> XEP-0384 always transfers the identity key in
    /// Ed25519 form, but the Diffie-Hellman is computed in Montgomery form.
    /// Whoever gives the one version to the method for the other gets no error
    /// message: both are 32 bytes, the conversion runs through, and out comes a
    /// key no signature fits. When first writing these lines that is exactly
    /// what happened to me.
    ///
    /// That is why there are two methods with different names instead of one
    /// with a switch. A <c>Boolean isEdwards</c> would be invisible at the call
    /// site, and the call site is the place where one goes wrong.
    /// </remarks>
    public static Boolean VerifyEdwards(Byte[] edwardsPublicKey, Byte[] message, Byte[] signature)
    {

        if (edwardsPublicKey.Length != KeyLength || signature.Length != SignatureLength)
            return false;

        try
        {
            return Ed25519.Verify(signature, 0, edwardsPublicKey, 0, message, 0, message.Length);
        }
        catch (Exception)
        {
            // An unusable key is no valid signature, and no exception for the
            // caller: both mean "not from this sender", and the difference
            // would go only to the attacker.
            return false;
        }

    }

    #endregion

    #region MontgomeryToEdwards(publicKey)

    /// <summary>
    /// Converts the Montgomery u coordinate into the Edwards y coordinate:
    /// <c>y = (u - 1) / (u + 1) mod p</c>.
    /// </summary>
    /// <remarks>
    /// The sign bit stays cleared - precisely that is what
    /// <see cref="EdwardsKeyPair"/> makes sure of when signing. The two curves
    /// are the same curve in another spelling, and this formula is the
    /// translator; it stands in RFC 7748, section 4.1.
    ///
    /// It is checked at a point both sides know: the X25519 base point
    /// <c>u = 9</c> has to yield the Ed25519 base point.
    /// </remarks>
    internal static Byte[] MontgomeryToEdwards(Byte[] publicKey)
    {

        var raw = (Byte[]) publicKey.Clone();

        // RFC 7748, section 5: the top bit of the u coordinate is discarded on
        // reading. Whoever left it standing would compute with a number the
        // other side did not mean at all.
        raw[31] &= 127;

        var u = new BigInteger(raw, isUnsigned: true, isBigEndian: false);

        var numerator   = ((u - 1)                                  % P + P) % P;
        var denominator = BigInteger.ModPow((u + 1) % P, P - 2, P);   // inverse by Fermat's little theorem

        if (denominator.IsZero)
            throw new CryptographicException("The public key cannot be converted (u = -1).");

        return ToLittleEndian(numerator * denominator % P);

    }

    #endregion

    #region EdwardsToMontgomery(publicKey)

    /// <summary>
    /// The opposite direction: <c>u = (1 + y) / (1 - y) mod p</c>.
    /// </summary>
    /// <remarks>
    /// Needed for foreign bundles: XEP-0384 <b>always</b> transfers the identity
    /// key in Ed25519 form ("The public key is ALWAYS transferred in its Ed25519
    /// form"), but what is computed with it is a Diffie-Hellman, and only the
    /// Montgomery form can do that.
    ///
    /// The sign bit is discarded, and that is no loss: the u coordinate does not
    /// know it, and both points with the same y yield the same shared secret.
    /// Precisely for that reason the way back out of
    /// <see cref="MontgomeryToEdwards"/> is unambiguous enough to carry at all.
    /// </remarks>
    public static Byte[] EdwardsToMontgomery(Byte[] publicKey)
    {

        if (publicKey.Length != KeyLength)
            throw new ArgumentException($"An Ed25519 key has {KeyLength} bytes, not {publicKey.Length}.",
                                        nameof(publicKey));

        var raw = (Byte[]) publicKey.Clone();
        raw[31] &= 127;   // the sign bit of x does not belong to y

        var y = new BigInteger(raw, isUnsigned: true, isBigEndian: false);

        var denominator = BigInteger.ModPow(((1 - y) % P + P) % P, P - 2, P);

        if (denominator.IsZero)
            throw new CryptographicException("The public key cannot be converted (y = 1).");

        return ToLittleEndian(((1 + y) % P + P) % P * denominator % P);

    }

    #endregion

    #region Helper functions

    /// <summary>
    /// The Edwards pair for the Montgomery scalar: the scalar with a fitting
    /// sign and the public point belonging to it, without a sign bit.
    /// </summary>
    /// <remarks>
    /// XEdDSA, section 2.4: <c>E = kB</c>; if <c>E</c> carries the sign bit, the
    /// computation goes on with <c>-k</c>. Afterwards the scalar fits a public
    /// point without a sign - and precisely that is what the verifier gets from
    /// the u coordinate, which knows no sign.
    /// </remarks>
    private static (BigInteger Scalar, Byte[] PublicPoint) EdwardsKeyPair(Byte[] privateKey)
    {

        var k = KeyPairFromPrivate(privateKey).PrivateKey;

        var e = Ed25519Math.ScalarMultBaseEncoded(
                    new BigInteger(k, isUnsigned: true, isBigEndian: false));

        var sign   = (e[31] & 0x80) != 0;

        var point  = (Byte[]) e.Clone();
        point[31] &= 0x7F;

        // First reduce, then negate. A clamped scalar is nearly 2^255 large and
        // thus far above the group order of about 2^252 - a mere (Q - k) % Q
        // would be negative, and C# keeps the sign with %. The computation then
        // did not come out wrong but did not come out at all: the encoding
        // throws.
        //
        // It hit exactly half of all keys, namely those with the sign bit set. A
        // test with a single generated key would have been green in every second
        // run.
        var scalar = new BigInteger(k, isUnsigned: true, isBigEndian: false) % Q;

        return (sign ? (Q - scalar) % Q : scalar, point);

    }

    /// <summary>
    /// A hash result as a number modulo <paramref name="modulus"/>.
    /// </summary>
    private static BigInteger ReduceMod(Byte[] hash, BigInteger modulus)
        => new BigInteger(hash, isUnsigned: true, isBigEndian: false) % modulus;

    /// <summary>
    /// A number as 32 bytes, least significant first.
    /// </summary>
    private static Byte[] ToLittleEndian(BigInteger value)
    {

        var bytes  = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        var field  = new Byte[32];

        Array.Copy(bytes, field, Math.Min(bytes.Length, 32));

        return field;

    }

    #endregion

}
