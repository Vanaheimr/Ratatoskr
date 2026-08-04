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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// X3DH (XEP-0384, section 4.2): a session begins without both being there
    /// at the same time.
    /// </summary>
    /// <remarks>
    /// The core of every test here is the same: <b>both sides have to come out
    /// with the same thing.</b> An error in the order of the four
    /// Diffie-Hellman values, in the assignment of the keys or in the
    /// associated data delivers no bad secret - it delivers a flawless one that
    /// only the other side does not know. Without this comparison that stands
    /// out only at the first message, and there it looks like a forgery.
    /// </remarks>
    [TestFixture]
    public class X3DHTests
    {

        #region Helper functions

        private static String Hex(Byte[] bytes)
            => Convert.ToHexString(bytes).ToLowerInvariant();

        #endregion


        #region BothSides_DeriveTheSameSecret()

        /// <summary>
        /// The whole purpose: Alice calculates out of Bob's bundle, Bob
        /// calculates out of Alice's message, and both have the same thing.
        /// </summary>
        [Test]
        public void BothSides_DeriveTheSameSecret()
        {

            var alice = OmemoIdentity.Create();
            var bob   = OmemoIdentity.Create();

            var atAlice = X3DH.Initiate(alice, bob.Bundle());

            var atBob   = X3DH.Accept(bob,
                                      alice.PublicIdentityKey,
                                       atAlice.EphemeralKey!,
                                       bob.SignedPreKeyId,
                                       atAlice.UsedPreKeyId);

            Assert.Multiple(() =>
            {

                Assert.That(Hex(atBob.SharedSecret), Is.EqualTo(Hex(atAlice.SharedSecret)),
                            "The two sides have different secrets.");

                Assert.That(atAlice.SharedSecret.Length, Is.EqualTo(32));

                Assert.That(Hex(atBob.AssociatedData), Is.EqualTo(Hex(atAlice.AssociatedData)),
                            "The associated data does not agree - the order of the identity keys.");

                Assert.That(atAlice.UsedPreKeyId, Is.Not.Null,
                            "A fresh bundle brings PreKeys along; none was used.");

            });

        }

        #endregion

        #region TwoSessions_DoNotShareASecret()

        /// <summary>
        /// Two sessions to the same device yield different secrets.
        /// </summary>
        /// <remarks>
        /// That is what the one-time keys and the PreKeys are there for. Were
        /// two first messages equal, an old one could be played in again - and
        /// the far end would answer it as if it were new.
        /// </remarks>
        [Test]
        public void TwoSessions_DoNotShareASecret()
        {

            var alice   = OmemoIdentity.Create();
            var bob     = OmemoIdentity.Create();
            var bundle  = bob.Bundle();

            var first   = X3DH.Initiate(alice, bundle, bundle.PreKeys[0].Id);
            var second  = X3DH.Initiate(alice, bundle, bundle.PreKeys[1].Id);

            Assert.Multiple(() =>
            {

                Assert.That(Hex(second.SharedSecret), Is.Not.EqualTo(Hex(first.SharedSecret)));

                Assert.That(Hex(second.EphemeralKey!), Is.Not.EqualTo(Hex(first.EphemeralKey!)),
                            "Twice the same one-time key.");

                // And the associated data is the same in both: it describes who
                // is speaking with whom, not which session.
                Assert.That(Hex(second.AssociatedData), Is.EqualTo(Hex(first.AssociatedData)));

            });

        }

        #endregion

        #region AUsedPreKey_IsGone()

        /// <summary>
        /// A used PreKey is gone afterwards - and a second attempt on it fails.
        /// </summary>
        /// <remarks>
        /// Taking out and deleting are one step. A PreKey holding twice yields
        /// the same session twice, and with that it is repeatable.
        /// </remarks>
        [Test]
        public void AUsedPreKey_IsGone()
        {

            var alice   = OmemoIdentity.Create();
            var bob     = OmemoIdentity.Create();
            var bundle  = bob.Bundle();

            var before  = bob.AvailablePreKeys;
            var atAlice = X3DH.Initiate(alice, bundle);

            X3DH.Accept(bob, alice.PublicIdentityKey, atAlice.EphemeralKey!,
                        bob.SignedPreKeyId, atAlice.UsedPreKeyId);

            Assert.Multiple(() =>
            {

                Assert.That(bob.AvailablePreKeys, Is.EqualTo(before - 1),
                            "The PreKey was not consumed.");

                Assert.That(() => X3DH.Accept(bob, alice.PublicIdentityKey, atAlice.EphemeralKey!,
                                              bob.SignedPreKeyId, atAlice.UsedPreKeyId),
                            Throws.TypeOf<CryptographicException>(),
                            "The same first message could be accepted a second time.");

            });

        }

        #endregion

        #region WithoutAPreKey_TheSessionStillStarts()

        /// <summary>
        /// If the store is empty, the session begins nevertheless - only
        /// without the fourth Diffie-Hellman.
        /// </summary>
        /// <remarks>
        /// That is expressly provided for and costs exactly one property: two
        /// first messages to the same device could then yield the same session
        /// if the one-time key were the same as well. A refusal would be the
        /// worse answer - it would make an empty store into a failure of
        /// reachability.
        /// </remarks>
        [Test]
        public void WithoutAPreKey_TheSessionStillStarts()
        {

            var alice = OmemoIdentity.Create();
            var bob   = OmemoIdentity.Create();

            var withoutPreKeys = bob.Bundle() with { PreKeys = [] };

            var atAlice = X3DH.Initiate(alice, withoutPreKeys);

            var atBob   = X3DH.Accept(bob, alice.PublicIdentityKey, atAlice.EphemeralKey!,
                                      bob.SignedPreKeyId, null);

            Assert.Multiple(() =>
            {
                Assert.That(atAlice.UsedPreKeyId, Is.Null);
                Assert.That(Hex(atBob.SharedSecret), Is.EqualTo(Hex(atAlice.SharedSecret)));
            });

        }

        #endregion

        #region ATamperedBundle_IsRefused()

        /// <summary>
        /// A bundle with a wrong signature leads to a break-off, not to a
        /// warning.
        /// </summary>
        /// <remarks>
        /// The bundle comes from the server of the far end - so from precisely
        /// the party an end-to-end encryption is supposed to protect against.
        /// Were it to exchange the signed PreKey for one of its own, it would
        /// read every first message along, and the fingerprint of the identity
        /// key would stay unchanged in doing so: the human being comparing it
        /// would see nothing.
        ///
        /// A session on such a bundle would be worse than none - it would look
        /// like an encrypted one.
        /// </remarks>
        [Test]
        public void ATamperedBundle_IsRefused()
        {

            var alice = OmemoIdentity.Create();
            var bob   = OmemoIdentity.Create();
            var evil  = OmemoIdentity.Create();

            Assert.Multiple(() =>
            {

                // The signed PreKey exchanged, the signature left standing.
                var substituted                       = bob.Bundle() with {
                                         SignedPreKey = evil.SignedPreKey.PublicKey
                                     };

                Assert.That(substituted.SignatureIsValid(), Is.False);
                Assert.That(() => X3DH.Initiate(alice, substituted),
                            Throws.TypeOf<CryptographicException>(),
                            "A substituted signed PreKey got through.");

                // The identity key exchanged, everything else left: then the
                // signature does not fit the named sender any more.
                var foreignIk = bob.Bundle() with { IdentityKey = evil.PublicIdentityKey };

                Assert.That(foreignIk.SignatureIsValid(), Is.False);
                Assert.That(() => X3DH.Initiate(alice, foreignIk),
                            Throws.TypeOf<CryptographicException>());

                // A single bent byte in the signature.
                var bent = (Byte[]) bob.Bundle().SignedPreKeySignature.Clone();
                bent[0] ^= 0x01;

                Assert.That((bob.Bundle() with { SignedPreKeySignature = bent }).SignatureIsValid(),
                            Is.False);

            });

        }

        #endregion

        #region AnUnknownSignedPreKey_IsRefused()

        /// <summary>
        /// If the far end names a signed PreKey that never existed here, it is
        /// turned away instead of guessed.
        /// </summary>
        /// <remarks>
        /// <b>This test changed with D67, and that belongs noted.</b> Until
        /// then every signed PreKey except the current one was turned away -
        /// including the one just superseded, and with it every message that
        /// was under way during the change. Since the session store precisely
        /// <i>one</i> is kept; what checks that is
        /// <c>AMessageForTheRotatedSignedPreKey_StillArrives</c>.
        ///
        /// What stays here is the question this test was always about: an id
        /// belonging to <b>no</b> existing key is turned away and not replaced
        /// by the nearest one to hand.
        /// </remarks>
        [Test]
        public void AnUnknownSignedPreKey_IsRefused()
        {

            var alice = OmemoIdentity.Create();
            var bob   = OmemoIdentity.Create();

            var atAlice = X3DH.Initiate(alice, bob.Bundle());

            Assert.That(() => X3DH.Accept(bob, alice.PublicIdentityKey, atAlice.EphemeralKey!,
                                          99u,
                                          atAlice.UsedPreKeyId),
                        Throws.TypeOf<CryptographicException>(),
                        "An unknown id was replaced by the nearest key to hand.");

        }

        #endregion

        #region TheIdentityKey_TravelsInEdwardsForm()

        /// <summary>
        /// The identity key goes over the wire in Ed25519 form and is
        /// calculated back for the Diffie-Hellman.
        /// </summary>
        /// <remarks>
        /// XEP-0384, section 5.3.2: "The public key is ALWAYS transferred in
        /// its Ed25519 form." Both directions have to fit together - otherwise
        /// the one side calculates with a different point than the other, and
        /// that without an error message: both are 32 valid bytes.
        /// </remarks>
        [Test]
        public void TheIdentityKey_TravelsInEdwardsForm()
        {

            var own = OmemoIdentity.Create();

            Assert.Multiple(() =>
            {

                Assert.That(Hex(own.PublicIdentityKey),
                            Is.Not.EqualTo(Hex(own.IdentityKey.PublicKey)),
                            "Both forms are equal - then nothing was converted.");

                Assert.That(Hex(Curve25519.EdwardsToMontgomery(own.PublicIdentityKey)),
                            Is.EqualTo(Hex(own.IdentityKey.PublicKey)),
                            "There and back does not yield the same key.");

                Assert.That(own.Fingerprint, Has.Length.EqualTo(64));

                // And our own signature checks out over the Ed25519 form - that
                // is the version the far end gets.
                Assert.That(Curve25519.VerifyEdwards(own.PublicIdentityKey,
                                                     own.SignedPreKey.PublicKey,
                                                     own.SignedPreKeySignature),
                            Is.True);

            });

        }

        #endregion

        #region ThePreKeys_AreDistinctAndNumbered()

        /// <summary>
        /// A hundred PreKeys, all different, numbered consecutively - and the
        /// refilling happens without reusing the ids.
        /// </summary>
        /// <remarks>
        /// A reused id would be a mix-up: a message that stayed lying under way
        /// and names the old PreKey would find a new one under the same number
        /// on arrival - and would yield a session that never existed.
        /// </remarks>
        [Test]
        public void ThePreKeys_AreDistinctAndNumbered()
        {

            var own    = OmemoIdentity.Create();
            var bundle = own.Bundle();

            Assert.Multiple(() =>
            {

                Assert.That(bundle.PreKeys, Has.Count.EqualTo(OmemoIdentity.PreKeyCount));

                Assert.That(bundle.PreKeys.Select(p => p.Id).Distinct().Count(),
                            Is.EqualTo(OmemoIdentity.PreKeyCount),
                            "Two PreKeys share one id.");

                Assert.That(bundle.PreKeys.Select(p => Hex(p.PublicKey)).Distinct().Count(),
                            Is.EqualTo(OmemoIdentity.PreKeyCount),
                            "Two PreKeys are the same key.");

                Assert.That(bundle.PreKeys.All(p => p.Id > 0), Is.True,
                            "Section 5.3.2 demands positive ids.");

            });

            // Consume two, refill: a hundred again, and the two ids do not come
            // back.
            var used = new[] { bundle.PreKeys[0].Id, bundle.PreKeys[1].Id };

            foreach (var id in used)
                own.TakePreKey(id);

            var after = own.ReplenishPreKeys();

            Assert.Multiple(() =>
            {

                Assert.That(after, Has.Count.EqualTo(OmemoIdentity.PreKeyCount));

                foreach (var id in used)
                    Assert.That(after.Any(p => p.Id == id), Is.False,
                                $"The id {id} was reused.");

            });

        }

        #endregion

        #region TheRotation_ChangesKeyAndSignature()

        /// <summary>
        /// The change of the signed PreKey renews the key, the id and the
        /// signature - and leaves the identity key standing.
        /// </summary>
        /// <remarks>
        /// The change is the reason why a stolen key does not open everything
        /// retroactively. The identity key must not change along with it: on
        /// its fingerprint hangs every comparison a human being ever made.
        /// </remarks>
        [Test]
        public void TheRotation_ChangesKeyAndSignature()
        {

            var own     = OmemoIdentity.Create();
            var before  = own.Bundle();

            own.RotateSignedPreKey();

            var after = own.Bundle();

            Assert.Multiple(() =>
            {

                Assert.That(Hex(after.SignedPreKey), Is.Not.EqualTo(Hex(before.SignedPreKey)));
                Assert.That(after.SignedPreKeyId,    Is.GreaterThan(before.SignedPreKeyId));

                Assert.That(Hex(after.SignedPreKeySignature),
                            Is.Not.EqualTo(Hex(before.SignedPreKeySignature)));

                Assert.That(after.SignatureIsValid(), Is.True,
                            "The new signed PreKey is not validly signed.");

                Assert.That(Hex(after.IdentityKey), Is.EqualTo(Hex(before.IdentityKey)),
                            "The identity key changed along - every fingerprint comparison would be worthless.");

            });

        }

        #endregion

        #region TheAssociatedData_IsInitiatorThenResponder()

        /// <summary>
        /// <c>AD = Encode(IK_A) ‖ Encode(IK_B)</c> - the calling one first, and
        /// that literally.
        /// </summary>
        /// <remarks>
        /// <b>This test came about through a surviving mutation as well</b>,
        /// and it is the same pattern for the third time: the order could be
        /// turned round in the helper function without a test saying anything -
        /// both sides call the same function and go on agreeing. A comparison
        /// of "both get the same thing" cannot find such a thing on principle.
        ///
        /// The damage would occur only towards a foreign client: its associated
        /// data would look different, every message would fail at a check that
        /// has nothing to do with its content - and the search for the error
        /// would begin at the encryption instead of at these 64 bytes.
        ///
        /// That is why what stands here is not "both equal" but which half
        /// belongs to whom.
        /// </remarks>
        [Test]
        public void TheAssociatedData_IsInitiatorThenResponder()
        {

            var alice = OmemoIdentity.Create();
            var bob   = OmemoIdentity.Create();

            var atAlice = X3DH.Initiate(alice, bob.Bundle());

            var atBob   = X3DH.Accept(bob, alice.PublicIdentityKey, atAlice.EphemeralKey!,
                                      bob.SignedPreKeyId, atAlice.UsedPreKeyId);

            Assert.Multiple(() =>
            {

                Assert.That(atAlice.AssociatedData, Has.Length.EqualTo(64));

                Assert.That(Hex(atAlice.AssociatedData[..32]),
                            Is.EqualTo(Hex(alice.PublicIdentityKey)),
                            "The first half belongs to the calling one.");

                Assert.That(Hex(atAlice.AssociatedData[32..]),
                            Is.EqualTo(Hex(bob.PublicIdentityKey)),
                            "The second half belongs to the called one.");

                Assert.That(Hex(atBob.AssociatedData), Is.EqualTo(Hex(atAlice.AssociatedData)),
                            "And the called one calculates the same associated data.");

            });

        }

        #endregion

        #region TheDerivation_MatchesTheSpecificationLiterally()

        /// <summary>
        /// The derivation, with a second HKDF and the provisions from
        /// section 4.2 written out literally.
        /// </summary>
        /// <remarks>
        /// <b>Out of the same experience as in D62.</b> The 0xFF prefix and the
        /// info string can both be changed without any other test saying
        /// anything: both sides calculate with the same function after all and
        /// go on agreeing. The damage would occur only towards a foreign client
        /// - and there is none here.
        ///
        /// So the provision stands here a second time and literally: 32 bytes
        /// of 0xFF in front, 32 zero bytes as salt, "OMEMO X3DH" as info, 32
        /// bytes of output, HKDF over SHA-256. Whoever changes one of these in
        /// the source has to change it along here - and sees in doing so that
        /// they are leaving the specification.
        /// </remarks>
        [Test]
        public void TheDerivation_MatchesTheSpecificationLiterally()
        {

            Byte[] dh1 = [.. Enumerable.Repeat((Byte) 0x01, 32)];
            Byte[] dh2 = [.. Enumerable.Repeat((Byte) 0x02, 32)];
            Byte[] dh3 = [.. Enumerable.Repeat((Byte) 0x03, 32)];
            Byte[] dh4 = [.. Enumerable.Repeat((Byte) 0x04, 32)];

            var hkdf = new Org.BouncyCastle.Crypto.Generators.HkdfBytesGenerator(
                           new Org.BouncyCastle.Crypto.Digests.Sha256Digest());

            hkdf.Init(new Org.BouncyCastle.Crypto.Parameters.HkdfParameters(
                          [.. Enumerable.Repeat((Byte) 0xFF, 32), .. dh1, .. dh2, .. dh3, .. dh4],
                          new Byte[32],
                          System.Text.Encoding.UTF8.GetBytes("OMEMO X3DH")));

            var expected = new Byte[32];
            hkdf.GenerateBytes(expected, 0, expected.Length);

            Assert.That(Hex(X3DH.Derive(dh1, dh2, dh3, dh4)), Is.EqualTo(Hex(expected)));

        }

        #endregion

        #region TheOrderOfTheFour_Matters()

        /// <summary>
        /// The four Diffie-Hellman values go in in a fixed order.
        /// </summary>
        /// <remarks>
        /// The test recalculates the derivation with swapped values by hand and
        /// establishes that something else comes out. That is no matter of
        /// course but the statement: whoever swaps here gets an equally good
        /// secret - only a different one from the far end. The error then shows
        /// itself not in this calculation but only at the first message, and
        /// there it looks like a forgery.
        /// </remarks>
        [Test]
        public void TheOrderOfTheFour_Matters()
        {

            var alice  = OmemoIdentity.Create();
            var bob    = OmemoIdentity.Create();
            var bundle = bob.Bundle();

            var correct = X3DH.Initiate(alice, bundle);

            // The same four values, a different order - recalculated by hand,
            // so that the swap is visible and not hidden in a mutation.
            var theirIk  = bundle.IdentityKeyForAgreement();
            var theirSpk = bundle.SignedPreKey;
            var preKey   = bundle.PreKeys.First(p => p.Id == correct.UsedPreKeyId);

            // The one-time key has stayed secret; without it the right
            // calculation cannot be repeated. So a second session with a known
            // one-time key.
            var ephemeral = Curve25519.GenerateKeyPair();

            var dh1 = Curve25519.Agree(alice.IdentityKey.PrivateKey, theirSpk);
            var dh2 = Curve25519.Agree(ephemeral.PrivateKey,         theirIk);
            var dh3 = Curve25519.Agree(ephemeral.PrivateKey,         theirSpk);
            var dh4 = Curve25519.Agree(ephemeral.PrivateKey,         preKey.PublicKey);

            Byte[] Derive(params Byte[][] values)
                => System.Security.Cryptography.HKDF.DeriveKey(
                       HashAlgorithmName.SHA256,
                       ikm:           [.. Enumerable.Repeat((Byte) 0xFF, 32), .. values.SelectMany(w => w)],
                       salt:          new Byte[32],
                       info:          System.Text.Encoding.UTF8.GetBytes(X3DH.Info),
                       outputLength:  32);

            Assert.That(Hex(Derive(dh2, dh1, dh3, dh4)),
                        Is.Not.EqualTo(Hex(Derive(dh1, dh2, dh3, dh4))),
                        "The order of the four values changes nothing - then nobody here is checking.");

        }

        #endregion

    }

}
