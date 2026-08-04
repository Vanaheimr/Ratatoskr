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
    /// The store that outlives a restart (XEP-0384, stage 6).
    /// </summary>
    /// <remarks>
    /// <b>The check is always the same: restart and carry on.</b>
    /// A store that puts things away and hands them back again is not one yet -
    /// it has to put away enough that the far side notices nothing of the
    /// restart. This is why nothing here compares what was stored; it checks
    /// whether the conversation goes on.
    /// </remarks>
    [TestFixture]
    public class OmemoStoreTests
    {

        #region Helpers

        private static readonly Byte[] AssociatedData = Encoding.UTF8.GetBytes("AD");

        private String _file = "";

        [SetUp]
        public void FreshFile()
            => _file = Path.Combine(Path.GetTempPath(),
                                    $"omemo-test-{Guid.NewGuid():N}.json");

        [TearDown]
        public void CleanUpAfterwards()
        {
            try { if (File.Exists(_file)) File.Delete(_file); } catch { /* never mind */ }
        }

        private static String Hex(Byte[] bytes)
            => Convert.ToHexString(bytes).ToLowerInvariant();

        private static Byte[] Text(String s)
            => Encoding.UTF8.GetBytes(s);

        /// <summary>A pair of ratchets, as it comes about after X3DH.</summary>
        private static (DoubleRatchet Alice, DoubleRatchet Bob) Pair()
        {

            var sharedSecret  = RandomNumberGenerator.GetBytes(32);
            var bobsKey       = Curve25519.GenerateKeyPair();

            return (DoubleRatchet.InitiateAsSender(sharedSecret, bobsKey.PublicKey),
                    DoubleRatchet.InitiateAsReceiver(sharedSecret, bobsKey));

        }

        #endregion


        #region TheIdentity_SurvivesARestart()

        /// <summary>
        /// After a restart it is the same device - the same fingerprint, the
        /// same id, the same signature.
        /// </summary>
        /// <remarks>
        /// <b>The fingerprint is the point.</b> A new one means that every
        /// comparison any human being has ever made is worthless - and to its
        /// contacts, a client that creates new keys on every start looks like
        /// an attacker. Every single time.
        ///
        /// The signature is carried over and not recomputed: XEdDSA mixes
        /// randomness into every one of them, so a new one would look
        /// different from the published one, and the bundle in the PEP node
        /// would be at odds with the device.
        /// </remarks>
        [Test]
        public void TheIdentity_SurvivesARestart()
        {

            var store = new OmemoFileStore(_file);
            var first = store.LoadOrCreateIdentity();

            // "Restart": a new instance on the same file.
            var second = new OmemoFileStore(_file).LoadOrCreateIdentity();

            Assert.Multiple(() =>
            {

                Assert.That(second.Fingerprint, Is.EqualTo(first.Fingerprint),
                            "The fingerprint has changed.");

                Assert.That(second.DeviceId, Is.EqualTo(first.DeviceId));

                Assert.That(Hex(second.SignedPreKeySignature),
                            Is.EqualTo(Hex(first.SignedPreKeySignature)),
                            "The signature was recomputed instead of carried over.");

                Assert.That(second.AvailablePreKeys, Is.EqualTo(first.AvailablePreKeys));

                // And the published bundle is the same one, byte for byte.
                Assert.That(Hex(second.Bundle().SignedPreKey),
                            Is.EqualTo(Hex(first.Bundle().SignedPreKey)));

            });

        }

        #endregion

        #region AConsumedPreKey_StaysConsumedAcrossARestart()

        /// <summary>
        /// A used PreKey is still used after the restart.
        /// </summary>
        /// <remarks>
        /// <b>Otherwise the first message would be replayable.</b> Whoever
        /// catches an old one and plays it in again after the receiver has
        /// restarted would get the same session a second time - and the
        /// receiver would show the message as new. The restart is no special
        /// case in this; it is the opportunity, because it happens by itself.
        /// </remarks>
        [Test]
        public void AConsumedPreKey_StaysConsumedAcrossARestart()
        {

            var store = new OmemoFileStore(_file);
            var own   = store.LoadOrCreateIdentity();

            var used = own.Bundle().PreKeys[0].Id;

            Assert.That(own.TakePreKey(used), Is.Not.Null);

            store.SaveIdentity(own.Export());

            var afterRestart = new OmemoFileStore(_file).LoadOrCreateIdentity();

            Assert.Multiple(() =>
            {

                Assert.That(afterRestart.TakePreKey(used), Is.Null,
                            "The used PreKey is back.");

                Assert.That(afterRestart.AvailablePreKeys, Is.EqualTo(OmemoIdentity.PreKeyCount - 1));

            });

        }

        #endregion

        #region ASession_ContinuesAfterARestart()

        /// <summary>
        /// <b>The heart of this stage:</b> a conversation carries on across a
        /// restart.
        /// </summary>
        /// <remarks>
        /// What is checked is not whether the state looks the same, but
        /// whether the far side notices the restart. A freshly begun session
        /// would have a different root key; the far side would get messages
        /// whose checksum does not add up - <b>and that looks like an attack,
        /// not like a restart</b>.
        /// </remarks>
        [Test]
        public void ASession_ContinuesAfterARestart()
        {

            var (alice, bob) = Pair();
            var store        = new OmemoFileStore(_file);

            // A few messages back and forth, so that both ratchets are running.
            bob.Decrypt(alice.Encrypt(Text("one"), AssociatedData), AssociatedData);
            alice.Decrypt(bob.Encrypt(Text("two"), AssociatedData), AssociatedData);
            bob.Decrypt(alice.Encrypt(Text("three"), AssociatedData), AssociatedData);

            store.SaveSession("bob@example.org", 1, new OmemoSessionState(bob.Export(), AssociatedData));

            // Bob restarts.
            var bobAfterwards = DoubleRatchet.Import(
                                    new OmemoFileStore(_file).LoadSession("bob@example.org", 1)!.Ratchet);

            Assert.Multiple(() =>
            {

                Assert.That(bobAfterwards.Decrypt(alice.Encrypt(Text("after the restart"), AssociatedData), AssociatedData),
                            Is.EqualTo(Text("after the restart")),
                            "After the restart nothing can be read any more.");

                // And in the other direction just the same.
                Assert.That(alice.Decrypt(bobAfterwards.Encrypt(Text("and back"), AssociatedData), AssociatedData),
                            Is.EqualTo(Text("and back")),
                            "Alice cannot read Bob's reply after his restart.");

            });

        }

        #endregion

        #region SkippedKeys_SurviveARestart()

        /// <summary>
        /// The keys put aside survive the restart as well.
        /// </summary>
        /// <remarks>
        /// Without them every overtaken message that was in flight during the
        /// restart would be lost - and lost for good, because its key had
        /// already been computed and was then thrown away. The sender would
        /// never get any hint of it.
        /// </remarks>
        [Test]
        public void SkippedKeys_SurviveARestart()
        {

            var (alice, bob) = Pair();
            var store        = new OmemoFileStore(_file);

            var one    = alice.Encrypt(Text("one"),    AssociatedData);
            var two    = alice.Encrypt(Text("two"),    AssociatedData);
            var three  = alice.Encrypt(Text("three"),  AssociatedData);

            // The third arrives first - two keys go aside.
            bob.Decrypt(three, AssociatedData);

            Assert.That(bob.SkippedKeys, Is.EqualTo(2));

            store.SaveSession("bob@example.org", 1, new OmemoSessionState(bob.Export(), AssociatedData));

            var bobAfterwards = DoubleRatchet.Import(
                                    new OmemoFileStore(_file).LoadSession("bob@example.org", 1)!.Ratchet);

            Assert.Multiple(() =>
            {

                Assert.That(bobAfterwards.SkippedKeys, Is.EqualTo(2));

                Assert.That(bobAfterwards.Decrypt(one, AssociatedData), Is.EqualTo(Text("one")),
                            "The overtaken message is lost across the restart.");

                Assert.That(bobAfterwards.Decrypt(two, AssociatedData), Is.EqualTo(Text("two")));

            });

        }

        #endregion

        #region AMessageForTheRotatedSignedPreKey_StillArrives()

        /// <summary>
        /// A message naming the <b>rotated</b> Signed PreKey arrives - the one
        /// before it no longer does.
        /// </summary>
        /// <remarks>
        /// In D63 this case was expressly put off: the rotated key was not
        /// kept, and a message that was in flight during the change was lost.
        ///
        /// Exactly one is kept. <b>Every further one would take back a piece
        /// of what the change is there for</b> - whoever steals it opens the
        /// sessions it has opened.
        /// </remarks>
        [Test]
        public void AMessageForTheRotatedSignedPreKey_StillArrives()
        {

            var alice = OmemoIdentity.Create();
            var bob   = OmemoIdentity.Create();

            // Alice reaches for the bundle of right now.
            var atAlice = X3DH.Initiate(alice, bob.Bundle());
            var oldSpk  = bob.SignedPreKeyId;

            bob.RotateSignedPreKey();

            var atBob = X3DH.Accept(bob, alice.PublicIdentityKey, atAlice.EphemeralKey!,
                                    oldSpk, atAlice.UsedPreKeyId);

            Assert.That(Hex(atBob.SharedSecret), Is.EqualTo(Hex(atAlice.SharedSecret)),
                        "The message for the rotated Signed PreKey does not arrive.");

            // One more change - now the first one is gone for good.
            bob.RotateSignedPreKey();

            Assert.That(() => X3DH.Accept(bob, alice.PublicIdentityKey, atAlice.EphemeralKey!,
                                          oldSpk, null),
                        Throws.TypeOf<CryptographicException>(),
                        "The second to last Signed PreKey is still being kept.");

        }

        #endregion

        #region TheRotatedSignedPreKey_SurvivesARestart()

        /// <summary>
        /// The <b>rotated</b> Signed PreKey outlives the restart as well.
        /// </summary>
        /// <remarks>
        /// The case that needs it lasts minutes: a message is in flight, the
        /// device changes its Signed PreKey and restarts. If the rotated one
        /// were missing afterwards, the message would be lost - and the sender
        /// would learn nothing about it.
        ///
        /// The test came about through a surviving mutation: the rotated key
        /// could be left out on restoring without anything failing. What had
        /// been checked until then was only the change itself, not its
        /// survival.
        /// </remarks>
        [Test]
        public void TheRotatedSignedPreKey_SurvivesARestart()
        {

            var store = new OmemoFileStore(_file);
            var bob   = store.LoadOrCreateIdentity();
            var alice = OmemoIdentity.Create();

            var atAlice = X3DH.Initiate(alice, bob.Bundle());
            var oldSpk  = bob.SignedPreKeyId;

            bob.RotateSignedPreKey();
            store.SaveIdentity(bob.Export());

            // Restart in the middle of the change.
            var bobAfterwards = new OmemoFileStore(_file).LoadOrCreateIdentity();

            Assert.Multiple(() =>
            {

                Assert.That(bobAfterwards.PreviousSignedPreKeyId, Is.EqualTo(oldSpk),
                            "The rotated Signed PreKey is gone across the restart.");

                var atBob = X3DH.Accept(bobAfterwards, alice.PublicIdentityKey, atAlice.EphemeralKey!,
                                        oldSpk, atAlice.UsedPreKeyId);

                Assert.That(Hex(atBob.SharedSecret), Is.EqualTo(Hex(atAlice.SharedSecret)),
                            "The message that had been in flight can no longer be read after the restart.");

            });

        }

        #endregion

        #region ASavedSession_ReplacesTheOlderOne()

        /// <summary>
        /// Storing the same session twice means: the newer one holds.
        /// </summary>
        /// <remarks>
        /// <b>This too came about through a surviving mutation.</b> The store
        /// could be changed so that it puts the new version <i>beside</i> the
        /// old one instead of replacing it - and on loading, the old one would
        /// come first. No test had ever stored twice.
        ///
        /// The damage would be the worst this store can do: after a restart
        /// the ratchet would stand at an old point, and everything that has
        /// run since then could no longer be read - for both sides, with no
        /// discernible reason.
        /// </remarks>
        [Test]
        public void ASavedSession_ReplacesTheOlderOne()
        {

            var (alice, bob) = Pair();
            var store        = new OmemoFileStore(_file);

            bob.Decrypt(alice.Encrypt(Text("one"), AssociatedData), AssociatedData);
            store.SaveSession("bob@example.org", 1, new OmemoSessionState(bob.Export(), AssociatedData));

            // The conversation goes on, and the new point is stored.
            bob.Decrypt(alice.Encrypt(Text("two"), AssociatedData), AssociatedData);
            bob.Decrypt(alice.Encrypt(Text("three"), AssociatedData), AssociatedData);
            store.SaveSession("bob@example.org", 1, new OmemoSessionState(bob.Export(), AssociatedData));

            var loaded = new OmemoFileStore(_file).LoadSession("bob@example.org", 1)!.Ratchet;

            Assert.Multiple(() =>
            {

                Assert.That(loaded.ReceiveCount, Is.EqualTo(3u),
                            "What was loaded is an older point - the new version was put beside it.");

                Assert.That(DoubleRatchet.Import(loaded)
                                         .Decrypt(alice.Encrypt(Text("four"), AssociatedData), AssociatedData),
                            Is.EqualTo(Text("four")));

            });

        }

        #endregion

        #region AChangedIdentityKey_IsReported()

        /// <summary>
        /// A different IdentityKey under the same device id is <b>reported</b>
        /// and not taken over.
        /// </summary>
        /// <remarks>
        /// There are exactly two explanations for it: the human being has set
        /// up their device anew - or somebody is pushing themselves in
        /// between. <b>From the outside the two cannot be told apart</b>, and
        /// that is why it is not a decision a program can make.
        ///
        /// The old note stays where it is, together with its trust decision:
        /// whoever overwrote it would turn a confirmed identity into an
        /// unconfirmed one, and the warning would be gone after the first
        /// look.
        /// </remarks>
        [Test]
        public void AChangedIdentityKey_IsReported()
        {

            var store = new OmemoMemoryStore();

            var real     = OmemoIdentity.Create().PublicIdentityKey;
            var foreign  = OmemoIdentity.Create().PublicIdentityKey;

            Assert.Multiple(() =>
            {

                Assert.That(store.RecordIdentity("bob@example.org", 1, real),
                            Is.EqualTo(OmemoIdentityCheck.New));

                Assert.That(store.RecordIdentity("bob@example.org", 1, real),
                            Is.EqualTo(OmemoIdentityCheck.Known));

                Assert.That(store.RecordIdentity("bob@example.org", 1, foreign),
                            Is.EqualTo(OmemoIdentityCheck.Changed),
                            "The exchange was not reported.");

                // And the next time again - the warning does not use itself
                // up.
                Assert.That(store.RecordIdentity("bob@example.org", 1, foreign),
                            Is.EqualTo(OmemoIdentityCheck.Changed),
                            "The warning was gone after the first time.");

                Assert.That(Hex(store.KnownDevices().Single().IdentityKey), Is.EqualTo(Hex(real)),
                            "The old note was overwritten.");

            });

        }

        #endregion

        #region ATrustDecision_SurvivesAndBelongsToAKey()

        /// <summary>
        /// The trust decision outlives the restart - and belongs to a key, not
        /// to a number.
        /// </summary>
        /// <remarks>
        /// About an unknown device nothing can be decided, and that is no
        /// formality: whoever decided in advance for a device id would have
        /// decided for the first key that turns up under that number - and
        /// that can be anybody.
        /// </remarks>
        [Test]
        public void ATrustDecision_SurvivesAndBelongsToAKey()
        {

            var store = new OmemoFileStore(_file);
            var bob   = OmemoIdentity.Create().PublicIdentityKey;

            Assert.Multiple(() =>
            {

                Assert.That(store.SetTrust("bob@example.org", 1, OmemoTrust.Trusted), Is.False,
                            "About an unknown device a decision could be made.");

                store.RecordIdentity("bob@example.org", 1, bob);

                Assert.That(store.TrustOf("bob@example.org", 1), Is.EqualTo(OmemoTrust.Undecided),
                            "A freshly seen device counts as confirmed.");

                Assert.That(store.SetTrust("bob@example.org", 1, OmemoTrust.Trusted), Is.True);

            });

            var afterRestart = new OmemoFileStore(_file);

            Assert.Multiple(() =>
            {

                Assert.That(afterRestart.TrustOf("bob@example.org", 1), Is.EqualTo(OmemoTrust.Trusted),
                            "The decision has not outlived the restart.");

                Assert.That(afterRestart.KnownDevices().Single().Fingerprint,
                            Is.EqualTo(Convert.ToHexString(bob).ToLowerInvariant()));

                // Another account with the same device id is another device.
                Assert.That(afterRestart.TrustOf("carol@example.org", 1), Is.EqualTo(OmemoTrust.Undecided));

            });

        }

        #endregion

        #region AnUnreadableStore_DoesNotStartFresh()

        /// <summary>
        /// An unreadable file throws instead of carrying on with new keys.
        /// </summary>
        /// <remarks>
        /// <b>The convenient way would be the dangerous one here.</b> A client
        /// that starts afresh after a read error has changed its fingerprint
        /// without anybody having been asked - and the old file would be
        /// overwritten on the first store. A recoverable read error would turn
        /// into a final loss.
        /// </remarks>
        [Test]
        public void AnUnreadableStore_DoesNotStartFresh()
        {

            File.WriteAllText(_file, "{ this is not JSON");

            Assert.That(() => new OmemoFileStore(_file),
                        Throws.InstanceOf<Exception>(),
                        "The unreadable file was silently replaced.");

            Assert.That(File.ReadAllText(_file), Does.StartWith("{ this is not JSON"),
                        "The unreadable file was overwritten.");

        }

        #endregion

        #region TheStore_IsWrittenAtomically()

        /// <summary>
        /// Writing goes through a side file - and that one does not stay
        /// lying about.
        /// </summary>
        /// <remarks>
        /// If the process breaks off in the middle, the old version is still
        /// there in full. That matters more here than with the account store:
        /// a half-written session file costs not one login attempt, but every
        /// running session.
        /// </remarks>
        [Test]
        public void TheStore_IsWrittenAtomically()
        {

            var store = new OmemoFileStore(_file);

            store.LoadOrCreateIdentity();
            store.SaveSession("bob@example.org", 1, new OmemoSessionState(Pair().Bob.Export(), AssociatedData));

            Assert.Multiple(() =>
            {

                Assert.That(File.Exists(_file), Is.True);
                Assert.That(File.Exists(_file + ".new"), Is.False,
                            "The side file stayed lying about.");

                // And the file is complete JSON.
                Assert.That(() => System.Text.Json.JsonDocument.Parse(File.ReadAllText(_file)),
                            Throws.Nothing);

            });

        }

        #endregion

        #region TheStoredIdentity_ContainsTheSecrets()

        /// <summary>
        /// The store holds the secret parts - and says so.
        /// </summary>
        /// <remarks>
        /// <b>This test stands here as a statement, not as a check of an
        /// intention.</b> The file is not encrypted; whoever reads it reads
        /// the conversations along with it. An encryption with a key that lay
        /// beside it would be none, and one that a human being types in does
        /// not exist in this application.
        ///
        /// Whoever changes that later has to change this test - and sees while
        /// doing so what it is about.
        /// </remarks>
        [Test]
        public void TheStoredIdentity_ContainsTheSecrets()
        {

            var store = new OmemoFileStore(_file);
            var own   = store.LoadOrCreateIdentity();

            var text = File.ReadAllText(_file);

            Assert.That(text,
                        Does.Contain(Convert.ToBase64String(own.IdentityKey.PrivateKey)),
                        "The secret IdentityKey is not in the file in the clear - " +
                        "has the store been encrypted? Then the remark about it " +
                        "in OmemoFileStore wants correcting.");

        }

        #endregion

    }

}
