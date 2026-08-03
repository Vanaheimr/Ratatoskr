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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// Point arithmetic on Ed25519 - exactly as much of it as XEdDSA needs:
/// <c>kB</c> for a freely chosen scalar.
/// </summary>
/// <remarks>
/// <b>Why this stands here although BouncyCastle can do Ed25519.</b>
/// BouncyCastle gives out publicly only <c>Sign</c> and <c>Verify</c>; its
/// <c>ScalarMultBase</c> is internal. Both public routes derive the scalar from
/// a seed (SHA-512, clamped) - XEdDSA, however, has to compute with a
/// <i>given</i> scalar: the identity key and the nonce.
///
/// <b>Two ways out that were discarded</b>, and the reason belongs written
/// down, because both look tempting at first:
///
/// <list type="number">
/// <item><b>Producing the nonce from a seed by way of BouncyCastle's
/// <c>GeneratePublicKey</c>.</b> Then <c>r</c> would be clamped: a multiple of
/// 8 in a fixed window, so about four bits predictable. That is precisely what
/// the attack on biased nonces (hidden number problem) aims at - a few hundred
/// signatures suffice, and the identity key falls. <b>A biased nonce is no
/// small blemish but the usual way such keys are stolen.</b></item>
/// <item><b>Computing <c>R</c> by way of <c>X25519.ScalarMultBase</c> and
/// converting the u coordinate.</b> Founders on the same clamping - and on top
/// of that on the u coordinate not knowing the sign of x, which the signature
/// does lay down.</item>
/// </list>
///
/// What remains: compute it oneself. The formulas stand in RFC 8032,
/// section 5.1.4 and are <b>complete</b> for this curve - they have no special
/// cases one could stumble over. It is checked against the published vectors
/// from RFC 8032, section 7.1: from the seed the same scalar derivation as
/// Ed25519, and <c>sB</c> has to yield the public key printed there. That is a
/// check against foreign numbers and not against one's own computation.
///
/// <b>What this computation is not: hardened against timing measurement.</b>
/// <see cref="BigInteger"/> computes in variable time, and the loop below
/// branches over the bits of the scalar. Whoever can measure the running time
/// of this process finely enough learns something about the key - that presumes
/// access to the same machine. For a client running on the device of its user
/// that is the right order of concerns; for a server answering foreign requests
/// it would be the wrong one. It stands here so that nobody later takes it for
/// settled.
/// </remarks>
internal static class Ed25519Math
{

    #region Data

    /// <summary>The prime field: 2^255 - 19.</summary>
    internal static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;

    /// <summary>The curve parameter d = -121665/121666 mod p.</summary>
    private static readonly BigInteger D =
        BigInteger.Parse("37095705934669439343138083508754565189542113879843219016388785533085940283555");

    /// <summary>The x coordinate of the base point.</summary>
    private static readonly BigInteger Bx =
        BigInteger.Parse("15112221349535400772501151409588531511454012693041857206046113283949847762202");

    /// <summary>The y coordinate of the base point: 4/5 mod p.</summary>
    private static readonly BigInteger By =
        BigInteger.Parse("46316835694926478169428394003475163141307993866256225615783033603165251855960");

    #endregion

    #region ScalarMultBaseEncoded(scalar)

    /// <summary>
    /// <c>kB</c>, encoded as in RFC 8032, section 5.1.2: 32 bytes of y
    /// coordinate, least significant byte first, in the top bit the lowest bit
    /// of x.
    /// </summary>
    internal static Byte[] ScalarMultBaseEncoded(BigInteger scalar)
        => Encode(ScalarMult(scalar));

    /// <summary>
    /// Double-and-add over the bits of the scalar, from the top down.
    /// </summary>
    private static (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) ScalarMult(BigInteger scalar)
    {

        // The neutral point (0 : 1 : 1 : 0).
        var result     = (X: BigInteger.Zero, Y: BigInteger.One, Z: BigInteger.One, T: BigInteger.Zero);
        var basePoint  = (X: Bx, Y: By, Z: BigInteger.One, T: Bx * By % P);

        var k = ((scalar % Order) + Order) % Order;

        for (var bit = 254; bit >= 0; bit--)
        {

            result = Add(result, result);

            if (!((k >> bit) & BigInteger.One).IsZero)
                result = Add(result, basePoint);

        }

        return result;

    }

    /// <summary>The group order.</summary>
    internal static readonly BigInteger Order =
        BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");

    #endregion

    #region Add / Encode

    /// <summary>
    /// The complete addition formula for a = -1 in extended coordinates
    /// (RFC 8032, section 5.1.4).
    /// </summary>
    /// <remarks>
    /// Complete means: it holds even when both summands are the same point, and
    /// also for the neutral point. That is why no separate doubling stands here
    /// - a second formula would be a second place for the same error, and the
    /// saving would not show over 255 rounds.
    /// </remarks>
    private static (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) Add(
        (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) p1,
        (BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) p2)
    {

        var a = (p1.Y - p1.X) * (p2.Y - p2.X) % P;
        var b = (p1.Y + p1.X) * (p2.Y + p2.X) % P;
        var c = p1.T * 2 * D % P * p2.T % P;
        var d = p1.Z * 2 * p2.Z % P;

        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;

        return (Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));

    }

    /// <summary>
    /// Encodes a point: y as 32 bytes, least significant first, with the lowest
    /// bit of x in the top bit.
    /// </summary>
    private static Byte[] Encode((BigInteger X, BigInteger Y, BigInteger Z, BigInteger T) point)
    {

        var invZ  = BigInteger.ModPow(point.Z, P - 2, P);
        var x     = Mod(point.X * invZ);
        var y     = Mod(point.Y * invZ);

        var bytes = new Byte[32];
        var raw   = y.ToByteArray(isUnsigned: true, isBigEndian: false);

        Array.Copy(raw, bytes, Math.Min(raw.Length, 32));

        if (!x.IsEven)
            bytes[31] |= 0x80;

        return bytes;

    }

    /// <summary>A non-negative remainder modulo p.</summary>
    private static BigInteger Mod(BigInteger value)
    {
        var remainder = value % P;
        return remainder.Sign < 0 ? remainder + P : remainder;
    }

    #endregion

}
