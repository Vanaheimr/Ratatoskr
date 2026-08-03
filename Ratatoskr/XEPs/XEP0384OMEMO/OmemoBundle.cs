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

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// A prekey with its identifier.
/// </summary>
/// <param name="Id">
/// A positive integer (XEP-0384, section 5.3.2: 1 to 2³¹-1).
/// </param>
/// <param name="PublicKey">The public part, 32 bytes of Montgomery u.</param>
public sealed record OmemoPreKey(UInt32 Id, Byte[] PublicKey);

/// <summary>
/// The public bundle of a device (XEP-0384, section 5.3.2) - everything a
/// stranger needs in order to begin a session without asking back.
/// </summary>
/// <param name="IdentityKey">
/// The identity key <b>in Ed25519 form</b>. The section permits both internal
/// forms but lays down the transfer: "The public key is ALWAYS transferred in
/// its Ed25519 form."
/// </param>
/// <param name="SignedPreKeyId">The identifier of the signed prekey.</param>
/// <param name="SignedPreKey">The signed prekey, 32 bytes of Montgomery u.</param>
/// <param name="SignedPreKeySignature">
/// The signature of the identity key over the signed prekey.
/// </param>
/// <param name="PreKeys">The prekeys usable once.</param>
/// <remarks>
/// <b>The bundle is the only place where a session begins without the other
/// side.</b> Bob is offline, Alice writes to him in encrypted form all the
/// same - that works only because his server keeps his keys in stock. With
/// that the server is also the obvious attacker: it could slip in a bundle of
/// its own. Against that exactly two things help - the signature over the
/// signed prekey (the server cannot forge it without having Bob's identity key)
/// and the fingerprint a human being compares (against an exchanged identity
/// key only that helps).
/// </remarks>
public sealed record OmemoBundle(Byte[]                       IdentityKey,
                                 UInt32                       SignedPreKeyId,
                                 Byte[]                       SignedPreKey,
                                 Byte[]                       SignedPreKeySignature,
                                 IReadOnlyList<OmemoPreKey>   PreKeys)
{

    /// <summary>
    /// The identity key in Montgomery form, as Diffie-Hellman needs it.
    /// </summary>
    public Byte[] IdentityKeyForAgreement()
        => Curve25519.EdwardsToMontgomery(IdentityKey);

    /// <summary>
    /// Is the signature over the signed prekey valid?
    /// </summary>
    /// <remarks>
    /// <b>To be asked before every use, and without exception.</b> A bundle
    /// comes from the server of the other side - that is, from precisely the
    /// party an end-to-end encryption is supposed to protect against. Without
    /// this check it could replace the signed prekey with its own and read
    /// along with every first message; the fingerprint of the identity key
    /// would stay unchanged in the process, and the human being who compares it
    /// would see nothing.
    ///
    /// What is signed is the signed prekey <b>in Montgomery form</b>, just as
    /// it stands in the bundle. The specification says at this place only "the
    /// signed PreKey signature"; which encoding is meant does not stand there,
    /// and there is no foreign counterpart here against which that could be
    /// checked. <b>That is the most likely reading and an unchecked
    /// assumption</b> - if it is not right, the check against foreign clients
    /// founders on this one line.
    /// </remarks>
    public Boolean SignatureIsValid()
        => Curve25519.VerifyEdwards(IdentityKey, SignedPreKey, SignedPreKeySignature);

}
