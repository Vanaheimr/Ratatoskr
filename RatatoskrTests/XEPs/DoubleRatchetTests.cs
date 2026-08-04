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
    /// The double ratchet (XEP-0384, section 4.3).
    /// </summary>
    /// <remarks>
    /// <b>Here errors are silent, and that is why these tests look different
    /// from the rest.</b> A ratchet that does not run on goes on encrypting
    /// flawlessly - it only does so again and again with the same key. A test
    /// checking only "there and back yields the plaintext" would pass then as
    /// well. What is checked on top is therefore that the ciphertexts
    /// <i>differ</i>, that keys <i>vanish</i> and that a message in the wrong
    /// place is <i>turned away</i>.
    /// </remarks>
    [TestFixture]
    public class DoubleRatchetTests
    {

        #region Helper functions

        private static readonly Byte[] AssociatedData = Encoding.UTF8.GetBytes("AD");

        private static String Hex(Byte[] bytes)
            => Convert.ToHexString(bytes).ToLowerInvariant();

        private static Byte[] Text(String s)
            => Encoding.UTF8.GetBytes(s);

        /// <summary>
        /// A pair of ratchets as it comes about after X3DH: Alice calls, Bob
        /// has his signed PreKey.
        /// </summary>
        private static (DoubleRatchet Alice, DoubleRatchet Bob) Pair()
        {

            var sharedSecret  = RandomNumberGenerator.GetBytes(32);
            var bobsKey       = Curve25519.GenerateKeyPair();

            return (DoubleRatchet.InitiateAsSender(sharedSecret, bobsKey.PublicKey),
                    DoubleRatchet.InitiateAsReceiver(sharedSecret, bobsKey));

        }

        #endregion


        #region TheFirstMessage_Arrives()

        /// <summary>
        /// The simplest case - and at the same time the one in which the called
        /// side gets its chains in the first place.
        /// </summary>
        [Test]
        public void TheFirstMessage_Arrives()
        {

            var (alice, bob) = Pair();

            Assert.Multiple(() =>
            {

                Assert.That(alice.CanSend, Is.True,  "The calling side can send at once.");
                Assert.That(bob.CanSend,   Is.False, "The called side cannot send yet.");

            });

            var message = alice.Encrypt(Text("Hello Bob"), AssociatedData);

            Assert.Multiple(() =>
            {

                Assert.That(bob.Decrypt(message, AssociatedData), Is.EqualTo(Text("Hello Bob")));

                Assert.That(bob.CanSend, Is.True,
                            "After the first message the called side has to be able to answer.");

            });

        }

        #endregion

        #region EveryMessage_HasItsOwnKey()

        /// <summary>
        /// Twice the same plaintext yields two different ciphertexts.
        /// </summary>
        /// <remarks>
        /// <b>The test that finds a ratchet standing still.</b> If the
        /// symmetric chain does not run on, everything still decrypts correctly
        /// - only with the same key, the same IV and thereby the same
        /// ciphertext. Whoever writes the same text twice gives it away to
        /// everybody reading along.
        /// </remarks>
        [Test]
        public void EveryMessage_HasItsOwnKey()
        {

            var (alice, bob) = Pair();

            var first   = alice.Encrypt(Text("the same"), AssociatedData);
            var second  = alice.Encrypt(Text("the same"), AssociatedData);

            Assert.Multiple(() =>
            {

                Assert.That(Hex(second.Ciphertext), Is.Not.EqualTo(Hex(first.Ciphertext)),
                            "Twice the same ciphertext - the chain stands still.");

                Assert.That(first.Header.MessageNumber,  Is.EqualTo(0u));
                Assert.That(second.Header.MessageNumber, Is.EqualTo(1u));

                Assert.That(bob.Decrypt(first,  AssociatedData), Is.EqualTo(Text("the same")));
                Assert.That(bob.Decrypt(second, AssociatedData), Is.EqualTo(Text("the same")));

            });

        }

        #endregion

        #region AConversation_TurnsTheDhRatchet()

        /// <summary>
        /// A back and forth over several rounds - and the ratchet key changes
        /// while doing so.
        /// </summary>
        /// <remarks>
        /// That is the second ratchet and the reason for "break-in recovery":
        /// whoever has stolen the state once loses it again as soon as the two
        /// have written in both directions. Were the key not to change, the
        /// thief would stay in on it forever.
        /// </remarks>
        [Test]
        public void AConversation_TurnsTheDhRatchet()
        {

            var (alice, bob) = Pair();

            var firstKey = alice.Encrypt(Text("1"), AssociatedData);
            bob.Decrypt(firstKey, AssociatedData);

            var bobsReply = bob.Encrypt(Text("2"), AssociatedData);

            Assert.That(Hex(bobsReply.Header.DhPublicKey),
                        Is.Not.EqualTo(Hex(firstKey.Header.DhPublicKey)),
                        "Both sides use the same ratchet key.");

            Assert.That(alice.Decrypt(bobsReply, AssociatedData), Is.EqualTo(Text("2")));

            // And on, over several rounds.
            var keys = new List<String>();

            for (var i = 0; i < 5; i++)
            {

                var there = alice.Encrypt(Text($"A{i}"), AssociatedData);
                Assert.That(bob.Decrypt(there, AssociatedData), Is.EqualTo(Text($"A{i}")));

                var back = bob.Encrypt(Text($"B{i}"), AssociatedData);
                Assert.That(alice.Decrypt(back, AssociatedData), Is.EqualTo(Text($"B{i}")));

                keys.Add(Hex(there.Header.DhPublicKey));
                keys.Add(Hex(back.Header.DhPublicKey));

            }

            Assert.That(keys.Distinct().Count(), Is.EqualTo(keys.Count),
                        "A ratchet key occurred twice.");

        }

        #endregion

        #region MessagesOutOfOrder_StillArrive()

        /// <summary>
        /// Messages that were overtaken can still be read later.
        /// </summary>
        /// <remarks>
        /// The case is not made up: XMPP delivers over different paths, and a
        /// message can arrive behind a later one. Without the keys put aside it
        /// would be lost - and that for good, because its key would have been
        /// forgotten while fast-forwarding.
        /// </remarks>
        [Test]
        public void MessagesOutOfOrder_StillArrive()
        {

            var (alice, bob) = Pair();

            var one    = alice.Encrypt(Text("one"),  AssociatedData);
            var two    = alice.Encrypt(Text("two"),  AssociatedData);
            var three  = alice.Encrypt(Text("three"),  AssociatedData);

            // The third one comes first.
            Assert.That(bob.Decrypt(three, AssociatedData), Is.EqualTo(Text("three")));

            Assert.That(bob.SkippedKeys, Is.EqualTo(2),
                        "The two skipped keys were not kept.");

            Assert.Multiple(() =>
            {

                Assert.That(bob.Decrypt(one, AssociatedData), Is.EqualTo(Text("one")));
                Assert.That(bob.Decrypt(two, AssociatedData), Is.EqualTo(Text("two")));

            });

            Assert.That(bob.SkippedKeys, Is.EqualTo(0),
                        "A used key was not cleared away.");

        }

        #endregion

        #region AMessageFromAPreviousChain_StillArrives()

        /// <summary>
        /// A message out of the <b>previous</b> chain arrives, even when the
        /// ratchet has turned in the meantime.
        /// </summary>
        /// <remarks>
        /// That is what the <c>pn</c> in the header is there for: it tells the
        /// other side how long the previous chain was, so that it can calculate
        /// its remainder and put it aside before beginning the new one. Without
        /// this field every message that was under way during a change of
        /// direction would be lost - and changes of direction are the normal
        /// case of a conversation.
        /// </remarks>
        [Test]
        public void AMessageFromAPreviousChain_StillArrives()
        {

            var (alice, bob) = Pair();

            // Alice writes twice; the second one stays lying under way.
            var first   = alice.Encrypt(Text("first"), AssociatedData);
            var delayed = alice.Encrypt(Text("delayed"), AssociatedData);

            bob.Decrypt(first, AssociatedData);

            // Bob answers - with that the ratchet turns.
            var reply = bob.Encrypt(Text("Reply"), AssociatedData);
            alice.Decrypt(reply, AssociatedData);

            // Alice writes in the new chain.
            var newOne = alice.Encrypt(Text("new chain"), AssociatedData);

            Assert.That(newOne.Header.PreviousChainLength, Is.EqualTo(2u),
                        "The length of the previous chain does not stand in the header.");

            Assert.Multiple(() =>
            {

                Assert.That(bob.Decrypt(newOne, AssociatedData), Is.EqualTo(Text("new chain")));

                // And now the one left lying out of the old chain.
                Assert.That(bob.Decrypt(delayed, AssociatedData), Is.EqualTo(Text("delayed")),
                            "The message out of the previous chain is lost.");

            });

        }

        #endregion

        #region AReplayedMessage_IsRefused()

        /// <summary>
        /// The same message a second time is turned away.
        /// </summary>
        /// <remarks>
        /// Its key is gone after the first time - either consumed or removed
        /// from the store of the skipped ones. <b>That is no side effect but
        /// the purpose:</b> without it an old message could be played in as
        /// often as one likes, and the recipient would show it as new every
        /// time.
        /// </remarks>
        [Test]
        public void AReplayedMessage_IsRefused()
        {

            var (alice, bob) = Pair();

            var message = alice.Encrypt(Text("only once"), AssociatedData);

            Assert.That(bob.Decrypt(message, AssociatedData), Is.EqualTo(Text("only once")));

            Assert.That(() => bob.Decrypt(message, AssociatedData),
                        Throws.InstanceOf<Exception>(),
                        "The same message could be read a second time.");

        }

        #endregion

        #region ATamperedMessage_IsRefused()

        /// <summary>
        /// What is changed gives itself away - in the ciphertext as in the
        /// header.
        /// </summary>
        /// <remarks>
        /// The header is checked along, because it goes into the associated
        /// data (<c>ad ‖ OMEMOMessage.proto(header)</c>). Without that a valid
        /// message could be moved to another place of the chain: the recipient
        /// would then take a different key, and what they decrypt would be
        /// chance - but the origin would look intact.
        /// </remarks>
        [Test]
        public void ATamperedMessage_IsRefused()
        {

            // Every case gets a fresh pair, and that is no fussiness: a message
            // turned away changes the state of the ratchet nevertheless - a
            // fast-forward has taken place, a key is consumed. Had the cases
            // stood one after another on the same pair, the second would have
            // failed at the consequences of the first instead of at its own
            // reason.
            //
            // Precisely that is what the mutation "the HMAC is not checked" got
            // past: the third case - the foreign associated data - would have
            // struck it dead, but checked on a ratchet that had already run on
            // through the two cases before it, and therefore threw for an
            // entirely different reason.

            Assert.Multiple(() =>
            {

                {
                    var (alice, bob) = Pair();
                    var message    = alice.Encrypt(Text("unchanged"), AssociatedData);

                    var ciphertext = (Byte[]) message.Ciphertext.Clone();
                    ciphertext[0] ^= 0x01;

                    Assert.That(() => bob.Decrypt(message with { Ciphertext = ciphertext }, AssociatedData),
                                Throws.TypeOf<CryptographicException>(),
                                "A changed byte in the ciphertext got through.");
                }

                {
                    var (alice, bob) = Pair();
                    var message    = alice.Encrypt(Text("unchanged"), AssociatedData);

                    Assert.That(() => bob.Decrypt(
                                    message with { Header = message.Header with { MessageNumber = 7 } },
                                    AssociatedData),
                                Throws.InstanceOf<Exception>(),
                                "A shifted number got through.");
                }

                {
                    // The sharpest of the three: nothing is changed at the
                    // ciphertext, and the message key holds. The associated
                    // data alone is a different one - if it is not checked,
                    // this message decrypts without objection, and a valid
                    // message could be moved into a foreign session.
                    var (alice, bob) = Pair();
                    var message    = alice.Encrypt(Text("unchanged"), AssociatedData);

                    Assert.That(() => bob.Decrypt(message, Text("other associated data")),
                                Throws.TypeOf<CryptographicException>(),
                                "A foreign associated data got through - the session is not bound.");
                }

            });

        }

        #endregion

        #region ARidiculousMessageNumber_IsRefused()

        /// <summary>
        /// A message with a very large number is turned away instead of letting
        /// the recipient calculate.
        /// </summary>
        /// <remarks>
        /// <b>That is a defence, not a question of order.</b> Without an upper
        /// bound a single message with <c>n = 4000000000</c> is enough, and the
        /// recipient calculates four billion keys before noticing that it does
        /// not hold. An attacker needs neither a key nor access for that - they
        /// need only this one number.
        ///
        /// The check therefore stands <b>before</b> the loop: one standing in
        /// it would come too late.
        /// </remarks>
        [Test]
        public void ARidiculousMessageNumber_IsRefused()
        {

            var (alice, bob) = Pair();

            var first = alice.Encrypt(Text("one"), AssociatedData);
            bob.Decrypt(first, AssociatedData);

            var malicious = alice.Encrypt(Text("two"), AssociatedData);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            Assert.That(() => bob.Decrypt(
                            malicious with { Header = malicious.Header with { MessageNumber = 4_000_000_000 } },
                            AssociatedData),
                        Throws.TypeOf<CryptographicException>(),
                        "The nonsensical number was accepted.");

            stopwatch.Stop();

            Assert.Multiple(() =>
            {

                Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(1000),
                            "The refusal calculated instead of checking.");

                Assert.That(bob.SkippedKeys, Is.LessThanOrEqualTo(DoubleRatchet.MaxSkip),
                            "More keys were kept than permitted.");

            });

        }

        #endregion

        #region TheChainStep_MatchesTheSpecificationLiterally()

        /// <summary>
        /// The message key out of <c>HMAC(ck, 0x01)</c>, the next chain key out
        /// of <c>HMAC(ck, 0x02)</c> - and that in this assignment.
        /// </summary>
        /// <remarks>
        /// <b>Here stood a test that checked nothing.</b> It recalculated the
        /// two constants in the test itself and established that they deliver
        /// different results - about the source it said nothing. The mutation
        /// setting both to <c>0x01</c> survived it consequently.
        ///
        /// <b>And that would have been the heaviest gap of this whole
        /// stage:</b> were the message and the chain key the same bytes,
        /// anybody reading a single message along could calculate the whole
        /// further chain. Out of forward secrecy would come its opposite - and
        /// nothing would look different, because both sides calculate equally
        /// wrongly after all.
        /// </remarks>
        [Test]
        public void TheChainStep_MatchesTheSpecificationLiterally()
        {

            var chain = RandomNumberGenerator.GetBytes(32);

            var (mk, ck) = DoubleRatchet.AdvanceChain(chain);

            Assert.Multiple(() =>
            {

                Assert.That(Hex(mk),
                            Is.EqualTo(Hex(HMACSHA256.HashData(chain, new Byte[] { 0x01 }))),
                            "The message key does not come about out of 0x01.");

                Assert.That(Hex(ck),
                            Is.EqualTo(Hex(HMACSHA256.HashData(chain, new Byte[] { 0x02 }))),
                            "The next chain key does not come about out of 0x02.");

                Assert.That(Hex(mk), Is.Not.EqualTo(Hex(ck)));

            });

        }

        #endregion

        #region TheRootChain_MatchesTheSpecificationLiterally()

        /// <summary>
        /// The root chain: the root key is the <b>salt</b>, the Diffie-Hellman
        /// value the input material, "OMEMO Root Chain" the info string, and
        /// the 64 bytes divide into the new root and the new chain.
        /// </summary>
        /// <remarks>
        /// <b>The same lesson for the fourth time, and this time the most
        /// expensive one.</b> Without this test four mutations survived: salt
        /// and input material swapped, the info string gone, and - worst - both
        /// halves out of the same one. The last one makes the root and the
        /// chain key the same bytes; out of one message read along the root and
        /// out of it the whole session could be rolled up.
        ///
        /// None of them stood out, because <b>both sides use the same function
        /// and therefore went on agreeing</b>. A test checking "both come out
        /// with the same thing" cannot tell a wrong derivation from a right one
        /// - it checks only that it is equal on both sides.
        ///
        /// So again: the provision a second time literally, recalculated with a
        /// second HKDF.
        /// </remarks>
        [Test]
        public void TheRootChain_MatchesTheSpecificationLiterally()
        {

            var root      = RandomNumberGenerator.GetBytes(32);
            var dhValue   = RandomNumberGenerator.GetBytes(32);

            var hkdf = new Org.BouncyCastle.Crypto.Generators.HkdfBytesGenerator(
                           new Org.BouncyCastle.Crypto.Digests.Sha256Digest());

            hkdf.Init(new Org.BouncyCastle.Crypto.Parameters.HkdfParameters(
                          dhValue,                                        // input material
                          root,                                        // salt
                          Encoding.UTF8.GetBytes("OMEMO Root Chain")));  // info

            var expected = new Byte[64];
            hkdf.GenerateBytes(expected, 0, expected.Length);

            var (newRoot, newChain) = DoubleRatchet.DeriveRootChain(root, dhValue);

            Assert.Multiple(() =>
            {

                Assert.That(Hex(newRoot), Is.EqualTo(Hex(expected[..32])),
                            "The new root is not the first half.");

                Assert.That(Hex(newChain), Is.EqualTo(Hex(expected[32..])),
                            "The new chain is not the second half.");

                Assert.That(Hex(newRoot), Is.Not.EqualTo(Hex(newChain)),
                            "Root and chain are the same bytes - the session could be rolled up.");

            });

        }

        #endregion

        #region TheMessageKeyMaterial_MatchesTheSpecificationLiterally()

        /// <summary>
        /// The material of a message key: 80 bytes, salt out of 32 zero bytes,
        /// info "OMEMO Message Key Material".
        /// </summary>
        [Test]
        public void TheMessageKeyMaterial_MatchesTheSpecificationLiterally()
        {

            var mk = RandomNumberGenerator.GetBytes(32);

            var hkdf = new Org.BouncyCastle.Crypto.Generators.HkdfBytesGenerator(
                           new Org.BouncyCastle.Crypto.Digests.Sha256Digest());

            hkdf.Init(new Org.BouncyCastle.Crypto.Parameters.HkdfParameters(
                          mk,
                          new Byte[32],
                          Encoding.UTF8.GetBytes("OMEMO Message Key Material")));

            var expected = new Byte[80];
            hkdf.GenerateBytes(expected, 0, expected.Length);

            var (key, authKey, iv) = DoubleRatchet.Material(mk);

            Assert.Multiple(() =>
            {
                Assert.That(Hex(key),      Is.EqualTo(Hex(expected[..32])));
                Assert.That(Hex(authKey),  Is.EqualTo(Hex(expected[32..64])));
                Assert.That(Hex(iv),       Is.EqualTo(Hex(expected[64..])));
            });

        }

        #endregion

        #region TheHeader_IsEncodedAsSpecified()

        /// <summary>
        /// The header as <c>OMEMOMessage.proto</c> - recalculated field by
        /// field.
        /// </summary>
        /// <remarks>
        /// <b>The same precaution for the fourth time as in D62 and D63.</b>
        /// These bytes go into the associated data; both sides have to form the
        /// same ones out of the same header. A wrong field number or a
        /// different order would not stand out in the house - both sides
        /// calculate equally wrongly after all -, and only a foreign client
        /// would get nothing but invalid checksums.
        ///
        /// That is why the expected bytes stand written out here: <c>08</c> is
        /// field 1 as a varint, <c>10</c> field 2 as a varint, <c>1a</c>
        /// field 3 as length-delimited, <c>20</c> the length 32.
        /// </remarks>
        [Test]
        public void TheHeader_IsEncodedAsSpecified()
        {

            var dh = new Byte[32];
            for (var i = 0; i < 32; i++)
                dh[i] = (Byte) i;

            var encoded = new RatchetHeader(dh, 300, 5).Encode();

            Assert.Multiple(() =>
            {

                // n = 5 (field 1), pn = 300 (field 2, two varint bytes),
                // dh_pub = 32 bytes (field 3).
                Assert.That(Hex(encoded),
                            Is.EqualTo("0805" + "10ac02" + "1a20" + Hex(dh)));

                // And the counter-check: read back it yields the same values
                // again.
                var fields = Protobuf.Read(encoded).ToList();

                Assert.That(fields, Has.Count.EqualTo(3));
                Assert.That(fields[0].Field, Is.EqualTo(1));
                Assert.That(fields[0].Number, Is.EqualTo(5u));
                Assert.That(fields[1].Field, Is.EqualTo(2));
                Assert.That(fields[1].Number, Is.EqualTo(300u));
                Assert.That(fields[2].Field, Is.EqualTo(3));
                Assert.That(Hex(fields[2].Data), Is.EqualTo(Hex(dh)));

            });

        }

        #endregion

        #region ALongConversation_StaysInStep()

        /// <summary>
        /// Fifty messages in alternating directions, delivered partly out of
        /// order.
        /// </summary>
        /// <remarks>
        /// The test that puts the three cases of decrypting against each other:
        /// a key put aside, a change of direction and fast-forwarding in the
        /// running chain. Each on its own is easy; it goes wrong at their
        /// edges, and those occur only in a longer course.
        /// </remarks>
        [Test]
        public void ALongConversation_StaysInStep()
        {

            var (alice, bob) = Pair();

            var random    = new Random(20260801);
            var inFlight  = new List<(RatchetMessage Message, String Text, Boolean FromAlice)>();

            for (var round = 0; round < 25; round++)
            {

                var fromAlice  = round % 3 != 2;
                var text       = $"Message {round}";

                // Whoever cannot send just now listens first.
                if (!fromAlice && !bob.CanSend)
                    continue;

                inFlight.Add(((fromAlice ? alice : bob).Encrypt(Text(text), AssociatedData), text, fromAlice));

                // Now and then what is lying about is delivered - in an
                // arbitrary order.
                if (round % 4 == 3)
                {

                    foreach (var (message, text_, fromAlice_) in inFlight.OrderBy(_ => random.Next()))
                        Assert.That((fromAlice_ ? bob : alice).Decrypt(message, AssociatedData),
                                    Is.EqualTo(Text(text_)),
                                    text_);

                    inFlight.Clear();

                }

            }

            foreach (var (message, text, fromAlice) in inFlight.OrderBy(_ => random.Next()))
                Assert.That((fromAlice ? bob : alice).Decrypt(message, AssociatedData),
                            Is.EqualTo(Text(text)),
                            text);

        }

        #endregion

    }

}
