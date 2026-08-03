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
/// The result of an X3DH exchange.
/// </summary>
/// <param name="SharedSecret">
/// The shared secret, 32 bytes - the beginning of the double ratchet.
/// </param>
/// <param name="AssociatedData">
/// <c>AD = Encode(IK_A) ‖ Encode(IK_B)</c>, both in Ed25519 form. Goes into
/// every message of the session as associated data that is checked along.
/// </param>
/// <param name="EphemeralKey">
/// The public part of the one-time key of the initiator. Null when accepting -
/// there it is known and comes from outside.
/// </param>
/// <param name="UsedPreKeyId">
/// The prekey used, or null when none was in stock.
/// </param>
public sealed record X3DHResult(Byte[]   SharedSecret,
                                Byte[]   AssociatedData,
                                Byte[]?  EphemeralKey,
                                UInt32?  UsedPreKeyId);

/// <summary>
/// X3DH per XEP-0384, section 4.2 - the beginning of a session without both
/// sides having to be there at the same time.
/// </summary>
/// <remarks>
/// <b>Why four Diffie-Hellmans and not one.</b> Each answers a different
/// question, and only together do they yield what one expects of the beginning
/// of a session:
///
/// <list type="bullet">
/// <item><c>DH1 = DH(IK_A, SPK_B)</c> - proves to Bob that it really is Alice
///       writing; her identity key goes into it.</item>
/// <item><c>DH2 = DH(EK_A, IK_B)</c> - proves to Alice that it really is Bob
///       reading.</item>
/// <item><c>DH3 = DH(EK_A, SPK_B)</c> - brings the freshness: Alice's one-time
///       key against Bob's rotated one. Whoever steals both identity keys later
///       does not get at this session.</item>
/// <item><c>DH4 = DH(EK_A, OPK_B)</c> - sees to it that two first messages to
///       the same device yield different sessions. It is omitted when Bob's
///       stock is empty; then exactly this property is missing and nothing
///       else.</item>
/// </list>
///
/// <b>The order is part of the prescription</b>, not a matter of taste: both
/// sides hang the four values one after the other and derive from that.
/// Whoever swaps them gets an equally good secret - only a different one from
/// the counterpart. The error then shows up not here but only with the first
/// message, and there it looks like a forgery.
///
/// <b>The 32 bytes of 0xFF in front</b> are no ornament. They separate this
/// derivation from every other one that uses the same curve: without them a
/// value that comes into being elsewhere as a Diffie-Hellman result could be
/// reused here as a session secret.
/// </remarks>
public static class X3DH
{

    #region Data

    /// <summary>The info string (XEP-0384, section 4.2).</summary>
    public const String Info = "OMEMO X3DH";

    #endregion

    #region Initiate(own, theirBundle, preKeyId)

    /// <summary>
    /// Alice begins: out of the bundle of the counterpart comes a shared
    /// secret, without the counterpart having to do anything.
    /// </summary>
    /// <param name="own">One's own key material.</param>
    /// <param name="theirBundle">The bundle of the counterpart.</param>
    /// <param name="preKeyId">
    /// Which prekey is used; without a value the first of the bundle. It stays
    /// null only when the bundle brings none at all.
    /// </param>
    /// <exception cref="CryptographicException">
    /// When the signature over the signed prekey is not right. <b>Here it
    /// aborts and does not warn:</b> a bundle with a wrong signature is either
    /// damaged or slipped in, and in both cases a session on it is worse than
    /// none - it would look like an encrypted one.
    /// </exception>
    public static X3DHResult Initiate(OmemoIdentity  own,
                                      OmemoBundle    theirBundle,
                                      UInt32?        preKeyId = null)
    {

        if (!theirBundle.SignatureIsValid())
            throw new CryptographicException(
                      "The signature over the signed prekey is not right - the bundle does not come " +
                      "from the identity key it names.");

        var ephemeral  = Curve25519.GenerateKeyPair();

        var theirIk    = theirBundle.IdentityKeyForAgreement();
        var theirSpk   = theirBundle.SignedPreKey;

        var preKey     = preKeyId.HasValue
                             ? theirBundle.PreKeys.FirstOrDefault(p => p.Id == preKeyId.Value)
                             : theirBundle.PreKeys.FirstOrDefault();

        if (preKeyId.HasValue && preKey is null)
            throw new CryptographicException($"The bundle knows no prekey with the identifier {preKeyId}.");

        var dh1 = Curve25519.Agree(own.IdentityKey.PrivateKey,  theirSpk);
        var dh2 = Curve25519.Agree(ephemeral.PrivateKey,        theirIk);
        var dh3 = Curve25519.Agree(ephemeral.PrivateKey,        theirSpk);
        var dh4 = preKey is not null
                      ? Curve25519.Agree(ephemeral.PrivateKey,  preKey.PublicKey)
                      : [];

        return new X3DHResult(
                   Derive(dh1, dh2, dh3, dh4),
                   AssociatedData(own.PublicIdentityKey, theirBundle.IdentityKey),
                   ephemeral.PublicKey,
                   preKey?.Id);

    }

    #endregion

    #region Accept(own, theirIdentityKey, theirEphemeralKey, signedPreKeyId, preKeyId)

    /// <summary>
    /// Bob accepts: the same four values, computed from the other direction.
    /// </summary>
    /// <param name="own">One's own key material.</param>
    /// <param name="theirIdentityKey">
    /// The identity key of the counterpart <b>in Ed25519 form</b>, just as it
    /// came over the wire.
    /// </param>
    /// <param name="theirEphemeralKey">Their one-time key, Montgomery form.</param>
    /// <param name="signedPreKeyId">
    /// Which signed prekey the counterpart used. If it does not agree with the
    /// current one, the message has been under way with a rotated key - this
    /// state knows only the current one and refuses it.
    /// </param>
    /// <param name="preKeyId">
    /// Which prekey they used, or null. It is <b>used up</b> in the process.
    /// </param>
    public static X3DHResult Accept(OmemoIdentity  own,
                                    Byte[]         theirIdentityKey,
                                    Byte[]         theirEphemeralKey,
                                    UInt32         signedPreKeyId,
                                    UInt32?        preKeyId)
    {

        // The current one or the one superseded - a message that was sent off
        // before the rotation names the old one and is to be read all the same.
        // Everything beyond that is gone for good, and that is deliberate.
        var signedPreKey = own.SignedPreKeyFor(signedPreKeyId)
                               ?? throw new CryptographicException(
                                      $"The message names the signed prekey {signedPreKeyId}; this " +
                                      $"device has {own.SignedPreKeyId}" +
                                      (own.PreviousSignedPreKeyId is UInt32 old ? $" and {old}" : "") +
                                      ".");

        var preKey = preKeyId.HasValue ? own.TakePreKey(preKeyId.Value) : null;

        if (preKeyId.HasValue && preKey is null)
            throw new CryptographicException(
                      $"The prekey {preKeyId} is unknown or already used up. A second " +
                      "session on the same prekey would be replayable.");

        var theirIk = Curve25519.EdwardsToMontgomery(theirIdentityKey);

        // The same four values, each from the other side: where Alice takes her
        // secret part and Bob's public one, Bob takes his secret one and hers.
        var dh1 = Curve25519.Agree(signedPreKey.PrivateKey,       theirIk);
        var dh2 = Curve25519.Agree(own.IdentityKey.PrivateKey,    theirEphemeralKey);
        var dh3 = Curve25519.Agree(signedPreKey.PrivateKey,       theirEphemeralKey);
        var dh4 = preKey is not null
                      ? Curve25519.Agree(preKey.PrivateKey,      theirEphemeralKey)
                      : [];

        return new X3DHResult(
                   Derive(dh1, dh2, dh3, dh4),
                   AssociatedData(theirIdentityKey, own.PublicIdentityKey),
                   null,
                   preKeyId);

    }

    #endregion

    #region Helper functions

    /// <summary>
    /// The derivation: 32 bytes of 0xFF, then the four Diffie-Hellman values,
    /// through HKDF-SHA-256.
    /// </summary>
    internal static Byte[] Derive(Byte[] dh1, Byte[] dh2, Byte[] dh3, Byte[] dh4)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256,
                          ikm:           [.. Enumerable.Repeat((Byte) 0xFF, 32), .. dh1, .. dh2, .. dh3, .. dh4],
                          salt:          new Byte[32],
                          info:          Encoding.UTF8.GetBytes(Info),
                          outputLength:  32);

    /// <summary>
    /// <c>AD = Encode(IK_A) ‖ Encode(IK_B)</c> - always the initiator first.
    /// </summary>
    /// <remarks>
    /// The order is the statement: it holds fast who began. If the keys hung
    /// there in any order, both sides would compute different associated data -
    /// and every message would founder on a check that has nothing to do with
    /// its content.
    /// </remarks>
    internal static Byte[] AssociatedData(Byte[] initiatorIdentityKey, Byte[] responderIdentityKey)
        => [.. initiatorIdentityKey, .. responderIdentityKey];

    #endregion

}
