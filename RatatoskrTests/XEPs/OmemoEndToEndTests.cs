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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// OMEMO from one end to the other: two real clients, a real server, one
    /// encrypted message.
    /// </summary>
    /// <remarks>
    /// <b>The test the seven stages run up to.</b> It uses nothing from the
    /// inside: Alice switches OMEMO on, writes, Bob reads. In between lie key
    /// creation, PEP publication, bundle fetching, X3DH, ratchet, protobuf,
    /// SCE and the store - and not one of them is touched here on its own.
    ///
    /// What is checked on top of that is what does <b>not</b> stand on the
    /// wire: the plaintext must not appear in any stanza the server has seen.
    /// Without that check the test would pass even if the message went along
    /// unencrypted beside it.
    /// </remarks>
    [TestFixture]
    public class OmemoEndToEndTests : AXMPPTests
    {

        #region AMessage_TravelsEncrypted()

        /// <summary>
        /// Alice writes encrypted, Bob reads - and the server never sees the
        /// plaintext.
        /// </summary>
        [Test]
        public async Task AMessage_TravelsEncrypted()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);
            var bob   = await ConnectClientAsync("bob",   createAccount: false);

            Assert.Multiple(() =>
            {
                Assert.That(alice.EnableOmemoAsync().GetAwaiter().GetResult(), Is.True,
                            "Alice could not switch OMEMO on.");
                Assert.That(bob.EnableOmemoAsync().GetAwaiter().GetResult(), Is.True,
                            "Bob could not switch OMEMO on.");
            });

            XMPPMessage?    received  = null;
            OmemoDecrypted? info      = null;

            bob.OnEncryptedMessage += (timestamp, sender, message, omemo, ct) =>
            {
                received = message;
                info     = omemo;

                return Task.CompletedTask;

            };

            const String secret = "Shall we meet at eight?";

            var skipped = await alice.SendEncryptedMessageAsync($"bob@{Server.Domain}", secret);

            Assert.That(skipped, Is.Empty,
                        "Not all devices could read along: " +
                        String.Join(", ", skipped.Select(u => $"{u.Jid}/{u.DeviceId}: {u.Reason}")));

            await WaitFor(() => received is not null, "the decrypted message at Bob");

            Assert.Multiple(() =>
            {

                Assert.That(received!.Body, Is.EqualTo(secret));

                Assert.That(info!.SenderDeviceId, Is.EqualTo(alice.Omemo!.Identity.DeviceId),
                            "The message is attributed to the wrong device.");

                Assert.That(info.IdentityCheck, Is.EqualTo(OmemoIdentityCheck.New),
                            "Alice's device was supposedly known to Bob already.");

                // And now the point without which the test would be worth
                // nothing: the plaintext must stand nowhere on the wire.
                var allStanzas = Server.Sessions.SelectMany(s => s.Received.Concat(s.Sent)).ToList();

                Assert.That(allStanzas.Any(f => f.Contains(secret, StringComparison.Ordinal)),
                            Is.False,
                            "The plaintext stands in a stanza the server has seen.");

                // As a cross-check: the ciphertext very much is there.
                Assert.That(allStanzas.Any(f => f.Contains("urn:xmpp:omemo:2", StringComparison.Ordinal)),
                            Is.True,
                            "No OMEMO stanza went over the wire at all - " +
                            "then the test is checking something else.");

            });

        }

        #endregion

        #region TwoMessagesInARow_BothArrive()

        /// <summary>
        /// Two messages one after the other, without a reply in between.
        /// </summary>
        /// <remarks>
        /// <b>The test that finds a missing store of the session.</b> In an
        /// alternating conversation it does not show: decrypting the reply
        /// stores the session anyway, and the next message finds a current
        /// point. Only two messages in a row show whether the <i>sending</i>
        /// itself keeps the progress - otherwise the second would stand at the
        /// same place of the chain as the first, and the receiver would take
        /// it for a repetition.
        ///
        /// This is exactly what a mutation got past, until this test existed.
        /// </remarks>
        [Test]
        public async Task TwoMessagesInARow_BothArrive()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);
            var bob   = await ConnectClientAsync("bob",   createAccount: false);

            await alice.EnableOmemoAsync();
            await bob.EnableOmemoAsync();

            var atBob = new List<String>();
            bob.OnEncryptedMessage += (timestamp, sender, n, _, ct) => { lock (atBob) atBob.Add(n.Body);  return Task.CompletedTask; };

            await alice.SendEncryptedMessageAsync($"bob@{Server.Domain}", "the first");
            await WaitFor(() => { lock (atBob) return atBob.Count == 1; }, "the first message");

            // Without a reply in between.
            await alice.SendEncryptedMessageAsync($"bob@{Server.Domain}", "the second");
            await WaitFor(() => { lock (atBob) return atBob.Count == 2; },
                          "the second message without a reply in between");

            // And a third one, so that the step after it sits as well.
            await alice.SendEncryptedMessageAsync($"bob@{Server.Domain}", "the third");
            await WaitFor(() => { lock (atBob) return atBob.Count == 3; }, "the third message");

            Assert.That(atBob, Is.EqualTo(new[] { "the first", "the second", "the third" }));

        }

        #endregion

        #region TheOwnOtherDevice_ReadsAlong()

        /// <summary>
        /// The own second device reads along - the own <b>first</b> one does
        /// not.
        /// </summary>
        /// <remarks>
        /// Without the copy to the own devices, the own computer would not see
        /// what the own phone has written; the conversation would stand
        /// differently on every device.
        ///
        /// The <i>sending</i> device on the other hand gets no entry - it
        /// would have to keep a session with itself. Both stand here together
        /// because both hang on the same line and the mutations survived in
        /// both directions.
        /// </remarks>
        [Test]
        public async Task TheOwnOtherDevice_ReadsAlong()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);
            var bob   = await ConnectClientAsync("bob",   createAccount: false);

            var alicesSecond = CreateClient("alice");
            alicesSecond.Connection.Resource = "second-device";
            await alicesSecond.ConnectAsync();

            await alice.EnableOmemoAsync();
            await alicesSecond.EnableOmemoAsync();
            await bob.EnableOmemoAsync();

            String? atTheSecond = null;
            alicesSecond.OnEncryptedMessage += (timestamp, sender, n, _, ct) => { atTheSecond = n.Body; return Task.CompletedTask; };

            var arrived = false;
            bob.OnEncryptedMessage += (timestamp, sender, _, _, ct) => { arrived = true; return Task.CompletedTask; };

            await alice.SendEncryptedMessageAsync($"bob@{Server.Domain}", "written to Bob");

            await WaitFor(() => arrived,          "the message at Bob");
            await WaitFor(() => atTheSecond is not null,
                          "the message at the own second device");

            Assert.That(atTheSecond, Is.EqualTo("written to Bob"));

            // And the sending device gets no entry for itself.
            //
            // What is searched for is the message and not just any stanza with
            // the OMEMO namespace: the first of those is a PEP publication,
            // and nothing can be read off it about key entries.
            var stanza = Server.Sessions
                               .SelectMany(s => s.Sent)
                               .First(f => f.StartsWith("<message", StringComparison.Ordinal) &&
                                           f.Contains("<encrypted",  StringComparison.Ordinal));

            var element = System.Xml.Linq.XElement.Parse(stanza);

            Assert.That(OmemoEncryptedElement.TryRead(element, out var loaded), Is.True);

            Assert.That(loaded!.KeyFor($"alice@{Server.Domain}", alice.Omemo!.Identity.DeviceId),
                        Is.Null,
                        "The sending device got a key entry for itself.");

        }

        #endregion

        #region TheEnvelope_CarriesTheSenderInside()

        /// <summary>
        /// The sender stands <b>inside</b> the encrypted envelope - and is
        /// matched against the one of the stanza.
        /// </summary>
        /// <remarks>
        /// The outer sender can be changed by anybody who gets the stanza into
        /// their fingers; the inner one cannot. Without it a ciphertext could
        /// be caught and passed on under a foreign name - the encryption would
        /// stay valid, and the receiver would see a message that was never
        /// addressed to them.
        ///
        /// Two mutations got through here as long as the information did not
        /// reach the caller: building the envelope without a sender, and not
        /// matching it on reading. Both only show if somebody can <i>see</i>
        /// the inner sender.
        /// </remarks>
        [Test]
        public async Task TheEnvelope_CarriesTheSenderInside()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);
            var bob   = await ConnectClientAsync("bob",   createAccount: false);

            await alice.EnableOmemoAsync();
            await bob.EnableOmemoAsync();

            OmemoDecrypted? info = null;
            bob.OnEncryptedMessage += (timestamp, sender, _, o, ct) => { info = o; return Task.CompletedTask; };

            await alice.SendEncryptedMessageAsync($"bob@{Server.Domain}", "signed from the inside");

            await WaitFor(() => info is not null, "the message at Bob");

            Assert.That(info!.EnvelopeFrom, Is.EqualTo($"alice@{Server.Domain}"),
                        "The envelope names no sender, or a different one.");

        }

        #endregion

        #region AForwardedMessage_IsRefused()

        /// <summary>
        /// A valid message, passed on under a foreign name, is refused.
        /// </summary>
        /// <remarks>
        /// <b>The attack the associated data of XEP-0420 stands against</b> -
        /// and the only way to really show its check at work.
        ///
        /// Alice writes to Bob and Mallory at once; both get their own key
        /// entry. Mallory sends the same <c>&lt;encrypted/&gt;</c> stanza
        /// unchanged on to Bob, under her own name. Bob's entry in it is
        /// untouched, his ratchet step works out, the checksum adds up -
        /// <b>everything cryptographically impeccable</b>. Only inside it says
        /// "from Alice" and outside "from Mallory".
        ///
        /// Without the match Bob would see a message that Alice never
        /// addressed to him, delivered by somebody else. As long as the
        /// information was only carried along and not compared, this exact
        /// mutation got through all the other tests.
        /// </remarks>
        [Test]
        public async Task AForwardedMessage_IsRefused()
        {

            MakeContacts("alice", "bob");
            MakeContacts("alice", "mallory");

            var alice    = await ConnectClientAsync("alice",   createAccount: false);
            var bob      = await ConnectClientAsync("bob",     createAccount: false);
            var mallory  = await ConnectClientAsync("mallory", createAccount: true);

            await alice.EnableOmemoAsync();
            await bob.EnableOmemoAsync();
            await mallory.EnableOmemoAsync();

            var atBob = 0;
            bob.OnEncryptedMessage += (timestamp, sender, _, _, ct) => { Interlocked.Increment(ref atBob); return Task.CompletedTask; };

            // One message to both - both get their entry.
            System.Xml.Linq.XNamespace client = "jabber:client";

            var result = await alice.Omemo!.EncryptAsync(
                                   [$"bob@{Server.Domain}", $"mallory@{Server.Domain}"],
                                   [new System.Xml.Linq.XElement(client + "body", "for the two of you only")]);

            Assert.That(result.Skipped, Is.Empty);

            // Mallory passes it on to Bob unchanged - under her own name.
            await mallory.SendRawAsync(
                      $"<message xmlns='jabber:client' to='bob@{Server.Domain}' type='chat'>" +
                      $"{result.Element.ToXml()}</message>");

            await WaitAgainst(() => atBob > 0,
                              "a message passed on under a foreign name");

        }

        #endregion

        #region AConversation_RunsInBothDirections()

        /// <summary>
        /// There and back, several times - and with that the Diffie-Hellman
        /// ratchet turns as well.
        /// </summary>
        /// <remarks>
        /// The first message builds the session up, the reply turns the
        /// ratchet, everything further runs over that. <b>Only this course
        /// checks the way a conversation actually takes</b> - a single message
        /// says nothing about it.
        /// </remarks>
        [Test]
        public async Task AConversation_RunsInBothDirections()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);
            var bob   = await ConnectClientAsync("bob",   createAccount: false);

            await alice.EnableOmemoAsync();
            await bob.EnableOmemoAsync();

            var atBob    = new List<String>();
            var atAlice  = new List<String>();

            bob.OnEncryptedMessage    += (timestamp, sender, n, _, ct) => { lock (atBob)   atBob.Add(n.Body);  return Task.CompletedTask; };
            alice.OnEncryptedMessage  += (timestamp, sender, n, _, ct) => { lock (atAlice) atAlice.Add(n.Body);  return Task.CompletedTask; };

            for (var i = 0; i < 3; i++)
            {

                await alice.SendEncryptedMessageAsync($"bob@{Server.Domain}", $"from Alice {i}");
                await WaitFor(() => { lock (atBob) return atBob.Count == i + 1; },
                              $"message {i} at Bob");

                await bob.SendEncryptedMessageAsync($"alice@{Server.Domain}", $"from Bob {i}");
                await WaitFor(() => { lock (atAlice) return atAlice.Count == i + 1; },
                              $"reply {i} at Alice");

            }

            Assert.Multiple(() =>
            {

                Assert.That(atBob,   Is.EqualTo(new[] { "from Alice 0", "from Alice 1", "from Alice 2" }));
                Assert.That(atAlice, Is.EqualTo(new[] { "from Bob 0",   "from Bob 1",   "from Bob 2" }));

            });

        }

        #endregion

        #region TheFingerprints_MatchOnBothSides()

        /// <summary>
        /// What Bob sees as Alice's fingerprint is the one Alice has.
        /// </summary>
        /// <remarks>
        /// <b>The whole question of trust hangs on this.</b> Two human beings
        /// compare this string over another way - on the telephone, in the
        /// same room. If the two renderings did not agree, the comparison
        /// would be impossible, and nobody could ever confirm anything.
        /// </remarks>
        [Test]
        public async Task TheFingerprints_MatchOnBothSides()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);
            var bob   = await ConnectClientAsync("bob",   createAccount: false);

            await alice.EnableOmemoAsync();
            await bob.EnableOmemoAsync();

            var arrived = false;
            bob.OnEncryptedMessage += (timestamp, sender, _, _, ct) => { arrived = true; return Task.CompletedTask; };

            await alice.SendEncryptedMessageAsync($"bob@{Server.Domain}", "Hello");
            await WaitFor(() => arrived, "the message at Bob");

            var notedAtBob = bob.Omemo!.KnownDevices()
                                .Single(d => d.DeviceId == alice.Omemo!.Identity.DeviceId);

            Assert.Multiple(() =>
            {

                Assert.That(notedAtBob.Fingerprint, Is.EqualTo(alice.Omemo!.Fingerprint),
                            "Bob sees a different fingerprint from the one Alice has.");

                Assert.That(notedAtBob.Trust, Is.EqualTo(OmemoTrust.Undecided),
                            "A freshly seen device counts as confirmed.");

                Assert.That(notedAtBob.BareJid, Is.EqualTo($"alice@{Server.Domain}"));

                // And the decision can be made.
                Assert.That(bob.Omemo.SetTrust($"alice@{Server.Domain}",
                                               alice.Omemo.Identity.DeviceId,
                                               OmemoTrust.Trusted),
                            Is.True);

            });

        }

        #endregion

        #region ADistrustedDevice_IsLeftOut()

        /// <summary>
        /// A refused device gets nothing - and the sender learns of it.
        /// </summary>
        /// <remarks>
        /// <b>The learning of it is the point.</b> A sender who does not
        /// notice that their counterpart was left out takes the conversation
        /// for held and wonders about the reply that fails to come. This is
        /// why the result names every skipped device together with the reason,
        /// instead of quietly building a shorter list of receivers.
        /// </remarks>
        [Test]
        public async Task ADistrustedDevice_IsLeftOut()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);
            var bob   = await ConnectClientAsync("bob",   createAccount: false);

            await alice.EnableOmemoAsync();
            await bob.EnableOmemoAsync();

            var arrived = false;
            bob.OnEncryptedMessage += (timestamp, sender, _, _, ct) => { arrived = true; return Task.CompletedTask; };

            // The first message makes Bob's device known at Alice.
            await alice.SendEncryptedMessageAsync($"bob@{Server.Domain}", "the first");
            await WaitFor(() => arrived, "the first message");

            Assert.That(alice.Omemo!.SetTrust($"bob@{Server.Domain}",
                                              bob.Omemo!.Identity.DeviceId,
                                              OmemoTrust.Distrusted),
                        Is.True);

            arrived = false;

            var skipped = await alice.SendEncryptedMessageAsync($"bob@{Server.Domain}", "the second");

            Assert.Multiple(() =>
            {

                Assert.That(skipped, Has.Count.EqualTo(1),
                            "The refused device was not reported.");

                Assert.That(skipped[0].DeviceId, Is.EqualTo(bob.Omemo.Identity.DeviceId));
                Assert.That(skipped[0].Reason,   Does.Contain("refused"));

            });

            await WaitAgainst(() => arrived, "a message to the refused device");

        }

        #endregion

        #region WithoutOmemo_NothingIsSentInTheClear()

        /// <summary>
        /// Without OMEMO switched on it throws - and sends nothing.
        /// </summary>
        /// <remarks>
        /// <b>The worst of all mistakes would be to send unencrypted here.</b>
        /// The caller wanted to encrypt; if they silently got an ordinary
        /// message instead, they would take it for protected. An exception is
        /// loud, an unencrypted message is not.
        /// </remarks>
        [Test]
        public async Task WithoutOmemo_NothingIsSentInTheClear()
        {

            var alice   = await ConnectClientAsync("alice");
            var session = Server.SessionOf(alice.FullJid)!;

            Server.AddAccount("bob");

            var before = session.Received.Count;

            Assert.That(async () => await alice.SendEncryptedMessageAsync($"bob@{Server.Domain}", "secret"),
                        Throws.TypeOf<InvalidOperationException>());

            await WaitAgainst(() => session.Received.Skip(before)
                                           .Any(f => f.Contains("secret", StringComparison.Ordinal)),
                              "a message sent unencrypted");

        }

        #endregion

        #region AConsumedPreKey_IsPersistedImmediately()

        /// <summary>
        /// The PreKey used up in the key exchange is gone from the store at
        /// once - not only at the next store.
        /// </summary>
        /// <remarks>
        /// <b>Otherwise it would be back after a restart</b>, and the same
        /// first message could be played in a second time. The restart is no
        /// special case in this; it is the opportunity, because it happens by
        /// itself.
        ///
        /// <b>Which identifier is gone is asked, not how many are left.</b>
        /// Counting was the measure until the stock began being filled back up
        /// in the same step - since then the number is the same before and
        /// after, and only the identity of the missing key still says anything.
        /// Both halves are asserted, because they fail in opposite directions:
        /// a spent key left in the store is replayable, and a stock that is not
        /// refilled leaves the published bundle advertising keys that are gone.
        /// </remarks>
        [Test]
        public async Task AConsumedPreKey_IsPersistedImmediately()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);
            var bob   = await ConnectClientAsync("bob",   createAccount: false);

            var bobsStore = new OmemoMemoryStore();

            await alice.EnableOmemoAsync();
            await bob.EnableOmemoAsync(bobsStore);

            var before = bobsStore.LoadIdentity()!.PreKeys.Select(pk => pk.Id).ToHashSet();

            var arrived = false;
            bob.OnEncryptedMessage += (timestamp, sender, _, _, ct) => { arrived = true; return Task.CompletedTask; };

            await alice.SendEncryptedMessageAsync($"bob@{Server.Domain}", "the first");
            await WaitFor(() => arrived, "the message at Bob");

            var after = bobsStore.LoadIdentity()!.PreKeys.Select(pk => pk.Id).ToHashSet();

            Assert.Multiple(() =>
            {

                Assert.That(before.Except(after).Count(), Is.EqualTo(1),
                            "Exactly the PreKey used up has to be gone from the store - " +
                            "after a restart the message would otherwise be acceptable a " +
                            "second time.");

                Assert.That(after, Has.Count.EqualTo(before.Count),
                            "And one has to have taken its place, or the published bundle " +
                            "goes on advertising a key that is no longer there.");

            });

        }

        #endregion

        #region AChangedIdentityKey_StopsTheMessage()

        /// <summary>
        /// If the IdentityKey of a device has changed, nothing goes to it any
        /// more - and the sender learns the reason.
        /// </summary>
        /// <remarks>
        /// The case is brought about by noting a <i>wrong</i> key for Bob's
        /// device in Alice's store. From Alice's view that looks exactly like
        /// an attacker who has exchanged Bob's bundle - and precisely then
        /// nothing may go out.
        /// </remarks>
        [Test]
        public async Task AChangedIdentityKey_StopsTheMessage()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);
            var bob   = await ConnectClientAsync("bob",   createAccount: false);

            var alicesStore = new OmemoMemoryStore();

            await alice.EnableOmemoAsync(alicesStore);
            await bob.EnableOmemoAsync();

            // Alice has noted Bob's device with a different key.
            alicesStore.RecordIdentity($"bob@{Server.Domain}",
                                       bob.Omemo!.Identity.DeviceId,
                                       OmemoIdentity.Create().PublicIdentityKey);

            var arrived = false;
            bob.OnEncryptedMessage += (timestamp, sender, _, _, ct) => { arrived = true; return Task.CompletedTask; };

            var skipped = await alice.SendEncryptedMessageAsync($"bob@{Server.Domain}", "secret");

            Assert.Multiple(() =>
            {

                Assert.That(skipped, Has.Count.EqualTo(1),
                            "The device with the changed key was not skipped.");

                Assert.That(skipped[0].Reason, Does.Contain("identity key"));

            });

            await WaitAgainst(() => arrived, "a message despite a changed IdentityKey");

        }

        #endregion

        #region EnablingOmemo_KeepsAForeignEntryInTheOwnList()

        /// <summary>
        /// Switching on adds to the own device list and does not write it
        /// anew.
        /// </summary>
        /// <remarks>
        /// <b>This test had to take a detour, and the reason is
        /// instructive.</b> A test with two real clients does not find the
        /// mistake: if the second device displaces the first, the first
        /// notices the PEP notification and enters itself again at once
        /// (D66) - the end state is right again, and the test sees nothing.
        ///
        /// This is why there is an entry here for a device that does not exist
        /// at all. It cannot defend itself, and so what the switching on does
        /// to the list stays visible.
        /// </remarks>
        [Test]
        public async Task EnablingOmemo_KeepsAForeignEntryInTheOwnList()
        {

            var alice = await ConnectClientAsync("alice");

            // A device to which there is no client.
            await alice.Connection.PublishOmemoDeviceListAsync(
                      new OmemoDeviceList([new OmemoDevice(4711, "old phone")]));

            await alice.EnableOmemoAsync();

            var list = await alice.Connection.FetchOmemoDeviceListAsync($"alice@{Server.Domain}");

            Assert.Multiple(() =>
            {

                Assert.That(list!.Contains(4711u), Is.True,
                            "The switching on has written the list anew and displaced the other " +
                            "device.");

                Assert.That(list.Contains(alice.Omemo!.Identity.DeviceId), Is.True);

            });

        }

        #endregion

        #region TheDeviceList_KeepsTheOtherDevices()

        /// <summary>
        /// When a second device switches OMEMO on, the first one stays in the
        /// list.
        /// </summary>
        /// <remarks>
        /// Whoever wrote the list anew on switching on would displace every
        /// other device of the same human being - and from then on those would
        /// get nothing any more, without anybody noticing. The re-entry from
        /// D66 healed that only at the next time, and only if the other device
        /// happens to be online.
        /// </remarks>
        [Test]
        public async Task TheDeviceList_KeepsTheOtherDevices()
        {

            var first = await ConnectClientAsync("alice");

            var second = CreateClient("alice");
            second.Connection.Resource = "second-device";
            await second.ConnectAsync();

            await first.EnableOmemoAsync();
            await second.EnableOmemoAsync();

            var list = await second.Connection.FetchOmemoDeviceListAsync($"alice@{Server.Domain}");

            Assert.Multiple(() =>
            {

                Assert.That(list!.Contains(first.Omemo!.Identity.DeviceId), Is.True,
                            "The first device was displaced from the list.");

                Assert.That(list.Contains(second.Omemo!.Identity.DeviceId), Is.True);

                Assert.That(list.Devices, Has.Count.EqualTo(2));

            });

        }

        #endregion

    }

}
