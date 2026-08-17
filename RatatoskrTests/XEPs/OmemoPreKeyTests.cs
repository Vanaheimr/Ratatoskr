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

using System.Xml.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// What happens to a prekey after it has been used - and what happens when
    /// two messages want the same ratchet at the same time.
    /// </summary>
    /// <remarks>
    /// Both are operational faults rather than attacks, and both cost messages
    /// in ordinary use. That makes them easy to miss: nothing here reports an
    /// error on the side that causes it. The prekey runs out on the sender's
    /// side, in the form of a message that cannot be read; the ratchet race
    /// shows up on the recipient's, as a checksum error for a message the sender
    /// believes was delivered.
    ///
    /// Built on the managers directly and not through two connections. What is
    /// being measured is the bookkeeping of the key material, and a server in
    /// between would only add ways for the test to be slow.
    /// </remarks>
    [TestFixture]
    public class OmemoPreKeyTests
    {

        #region Helper functions

        /// <summary>
        /// Two managers that can reach each other's bundles and device lists,
        /// the way two accounts on a server would.
        /// </summary>
        private static (OmemoManager Alice, OmemoManager Bob) TwoParties()
        {

            OmemoManager? alice = null;
            OmemoManager? bob   = null;

            OmemoManager Party(String jid, Func<OmemoManager?> other)
                => new(new OmemoMemoryStore(),
                       JID.Parse(jid),
                       _        => Task.FromResult<OmemoDeviceList?>(
                                       new OmemoDeviceList([new OmemoDevice(other()!.Identity.DeviceId)])),
                       (_, _)   => Task.FromResult<OmemoBundle?>(other()!.Identity.Bundle()));

            alice = Party("alice@example.org", () => bob);
            bob   = Party("bob@example.org",   () => alice);

            return (alice, bob);

        }

        private static IReadOnlyList<XElement> Body(String text)
            => [new XElement("body", text)];

        #endregion


        #region AConsumedPreKey_IsReplacedAndTheBundleIsAnnounced()

        /// <summary>
        /// An incoming key exchange spends one prekey. Afterwards the stock is
        /// full again and the bundle has been announced as changed.
        /// </summary>
        /// <remarks>
        /// Until now only the spending happened. The published bundle went on
        /// advertising the spent key and never gained a new one, so the second
        /// stranger to reach for it got a key that was gone - X3DH throws on
        /// that, deliberately, because a prekey that holds twice makes the
        /// session replayable. The result was a first message nobody could read
        /// and no hint anywhere as to why.
        /// </remarks>
        [Test]
        public async Task AConsumedPreKey_IsReplacedAndTheBundleIsAnnounced()
        {

            var (alice, bob) = TwoParties();

            var announced = 0;
            bob.OnBundleChanged += (timestamp, sender, ct) => { Interlocked.Increment(ref announced); return Task.CompletedTask; };

            var before = bob.Identity.AvailablePreKeys;

            var encrypted = await alice.EncryptAsync([JID.Parse("bob@example.org")], Body("first contact"));
            var decrypted = await bob.DecryptAsync(encrypted.Element, JID.Parse("alice@example.org"));

            Assert.Multiple(() =>
            {

                Assert.That(decrypted,                      Is.Not.Null,
                            "The key exchange itself has to work, or the rest measures nothing.");

                Assert.That(before,                         Is.EqualTo(OmemoIdentity.PreKeyCount));

                Assert.That(bob.Identity.AvailablePreKeys,  Is.EqualTo(OmemoIdentity.PreKeyCount),
                            "The stock has to be full again - one was spent, one belongs added.");

                Assert.That(announced,                      Is.EqualTo(1),
                            "And somebody has to be told, or the refilled stock stays at home.");

            });

        }

        #endregion

        #region ARefilledPreKey_NeverCarriesAnIdentifierThatWasAlreadyUsed()

        /// <summary>
        /// The identifiers run on. A reused one would not be an ordinal but a
        /// confusion: a message left lying under way names the old prekey and
        /// would find a different key under the same number on arrival.
        /// </summary>
        /// <remarks>
        /// The interesting case is the empty stock, and it used to be the broken
        /// one. The next identifier was read off the largest one <i>in stock</i>,
        /// which held as long as anything was in stock and began again at 1 the
        /// moment nothing was - handing out the whole spent range a second time.
        /// </remarks>
        [Test]
        public void ARefilledPreKey_NeverCarriesAnIdentifierThatWasAlreadyUsed()
        {

            var identity = OmemoIdentity.Create();
            var first    = identity.Bundle().PreKeys.Select(pk => pk.Id).ToHashSet();

            // Empty it completely - the case the old arithmetic fell over on.
            foreach (var id in first)
                identity.TakePreKey(id);

            Assert.That(identity.AvailablePreKeys, Is.Zero, "The stock has to be empty for this.");

            identity.ReplenishPreKeys();

            var second = identity.Bundle().PreKeys.Select(pk => pk.Id).ToHashSet();

            Assert.Multiple(() =>
            {
                Assert.That(second, Has.Count.EqualTo(OmemoIdentity.PreKeyCount));
                Assert.That(second.Overlaps(first), Is.False,
                            "Not one identifier may come round a second time.");
            });

        }

        #endregion

        #region TwoMessagesAtOnce_AreBothReadable()

        /// <summary>
        /// Two messages to the same device, encrypted at the same time, and both
        /// have to arrive.
        /// </summary>
        /// <remarks>
        /// A ratchet step is a read-modify-write: load the state, import it,
        /// advance it, write it back. Run twice at once, both read the same
        /// state, both produce the message with the same number, and one of the
        /// two saved states overwrites the other. The recipient can read exactly
        /// one of them; the other fails its checksum, and the sender learns
        /// nothing of it.
        ///
        /// The lock inside DoubleRatchet is no help - it guards one instance,
        /// and each call imports one of its own out of the same stored state.
        /// The gate has to sit around the whole load-to-save, one per session.
        /// </remarks>
        [Test]
        public async Task TwoMessagesAtOnce_AreBothReadable()
        {

            var (alice, bob) = TwoParties();

            // The session first, so that both of the two below take the ratchet
            // path and race on it rather than on the key exchange.
            var opening = await alice.EncryptAsync([JID.Parse("bob@example.org")], Body("first contact"));
            await bob.DecryptAsync(opening.Element, JID.Parse("alice@example.org"));

            var both = await Task.WhenAll(
                           Task.Run(() => alice.EncryptAsync([JID.Parse("bob@example.org")], Body("one"))),
                           Task.Run(() => alice.EncryptAsync([JID.Parse("bob@example.org")], Body("two"))));

            var read = new List<String?>();

            foreach (var message in both)
            {
                var plain = await bob.DecryptAsync(message.Element, JID.Parse("alice@example.org"));
                read.Add(plain?.Content.FirstOrDefault(e => e.Name.LocalName == "body")?.Value);
            }

            Assert.That(read, Is.EquivalentTo(new[] { "one", "two" }),
                        "Both messages were sent and both were confirmed as sent, so both " +
                        "have to be readable.");

        }

        #endregion

    }

}
