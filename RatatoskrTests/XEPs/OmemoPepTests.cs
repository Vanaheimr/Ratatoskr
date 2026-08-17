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
    /// The PEP distribution of the device list and the bundles (XEP-0384,
    /// section 5.2) - over a real server.
    /// </summary>
    /// <remarks>
    /// <b>This stage is the first in four that checks XMPP again instead of
    /// cryptography</b> - and thereby the first where a run says more than a
    /// recomputed provision. The test server got a subset of PEP for it:
    /// publish, fetch, notify.
    ///
    /// The heart of it is the way across the server boundary: Alice publishes,
    /// <b>Bob fetches, and what he fetches has to pass his own signature
    /// check.</b> With that, all the stages so far hang together for the first
    /// time - a bundle that comes out of the store of a server and whose
    /// origin the receiver recomputes himself.
    /// </remarks>
    [TestFixture]
    public class OmemoPepTests : AXMPPTests
    {

        #region Helpers

        private static String Hex(Byte[] bytes)
            => Convert.ToHexString(bytes).ToLowerInvariant();

        #endregion


        #region TheDeviceList_RoundTripsThroughXml()

        /// <summary>The device list as XML - there and back.</summary>
        [Test]
        public void TheDeviceList_RoundTripsThroughXml()
        {

            var list = new OmemoDeviceList([new OmemoDevice(31415, "phone"),
                                            new OmemoDevice(27182)]);

            var xml = list.ToXml();

            Assert.That(OmemoDeviceList.TryRead(xml, out var loaded), Is.True);

            Assert.Multiple(() =>
            {

                Assert.That(loaded!.Devices, Has.Count.EqualTo(2));
                Assert.That(loaded.Devices[0].Id,     Is.EqualTo(31415u));
                Assert.That(loaded.Devices[0].Label,  Is.EqualTo("phone"));
                Assert.That(loaded.Devices[1].Label,  Is.Null,
                            "A missing label is not an empty string.");

                Assert.That(loaded.Contains(27182u), Is.True);
                Assert.That(loaded.Contains(1u),     Is.False);

            });

        }

        #endregion

        #region ABrokenDeviceEntry_DoesNotTakeTheOthersWithIt()

        /// <summary>
        /// A device with a crooked id is passed over, the list stays.
        /// </summary>
        /// <remarks>
        /// The reason is reachability: whoever threw away the whole list could
        /// no longer write to any of the remaining devices. A single unusable
        /// entry must not take all the others with it.
        /// </remarks>
        [Test]
        public void ABrokenDeviceEntry_DoesNotTakeTheOthersWithIt()
        {

            var xml = XElement.Parse(
                          "<devices xmlns='urn:xmpp:omemo:2'>" +
                          "<device id='1'/>" +
                          "<device id='not-a-number'/>" +
                          "<device/>" +
                          "<device id='0'/>" +
                          "<device id='2'/>" +
                          "</devices>");

            Assert.That(OmemoDeviceList.TryRead(xml, out var list), Is.True);

            Assert.That(list!.Devices.Select(d => d.Id), Is.EqualTo(new UInt32[] { 1, 2 }),
                        "What was left over was not exactly the usable entries.");

        }

        #endregion

        #region TheBundle_RoundTripsThroughXml()

        /// <summary>
        /// The bundle as XML - and the signature survives the way.
        /// </summary>
        /// <remarks>
        /// The second part is the important one: an encoding that carries all
        /// the fields across correctly and loses a byte in doing so would not
        /// show up in a field comparison - but it does in the signature check.
        /// </remarks>
        [Test]
        public void TheBundle_RoundTripsThroughXml()
        {

            var own    = OmemoIdentity.Create();
            var bundle = own.Bundle();

            Assert.That(OmemoPep.TryReadBundle(bundle.ToXml(), out var loaded), Is.True);

            Assert.Multiple(() =>
            {

                Assert.That(Hex(loaded!.IdentityKey),            Is.EqualTo(Hex(bundle.IdentityKey)));
                Assert.That(loaded.SignedPreKeyId,               Is.EqualTo(bundle.SignedPreKeyId));
                Assert.That(Hex(loaded.SignedPreKey),            Is.EqualTo(Hex(bundle.SignedPreKey)));
                Assert.That(Hex(loaded.SignedPreKeySignature),   Is.EqualTo(Hex(bundle.SignedPreKeySignature)));
                Assert.That(loaded.PreKeys,                      Has.Count.EqualTo(bundle.PreKeys.Count));

                Assert.That(loaded.SignatureIsValid(), Is.True,
                            "The signature does not survive the way through the XML.");

            });

        }

        #endregion

        #region AnIncompleteBundle_IsRefused()

        /// <summary>
        /// A bundle without an IdentityKey, without a Signed PreKey or without
        /// a signature is refused.
        /// </summary>
        /// <remarks>
        /// Reading is strict here, unlike with the device list: without an
        /// IdentityKey the signature cannot be checked, without a Signed
        /// PreKey nothing can be agreed. To accept half a bundle would mean
        /// building a session on something whose origin nobody has checked.
        /// </remarks>
        [Test]
        public void AnIncompleteBundle_IsRefused()
        {

            var complete = OmemoIdentity.Create().Bundle().ToXml();

            Assert.Multiple(() =>
            {

                foreach (var part in new[] { "ik", "spk", "spks" })
                {

                    var mutilated = new XElement(complete);
                    mutilated.Elements().First(e => e.Name.LocalName == part).Remove();

                    Assert.That(OmemoPep.TryReadBundle(mutilated, out _), Is.False, part);

                }

                // A bundle entirely without PreKeys, on the other hand, is
                // usable - the session comes about without them too, it only
                // loses one property.
                var withoutPreKeys = new XElement(complete);
                withoutPreKeys.Elements().First(e => e.Name.LocalName == "prekeys").Remove();

                Assert.That(OmemoPep.TryReadBundle(withoutPreKeys, out var loaded), Is.True);
                Assert.That(loaded!.PreKeys, Is.Empty);

            });

        }

        #endregion


        #region AnEmptyIdentityKey_IsRefused()

        /// <summary>
        /// An <c>&lt;ik/&gt;</c> without content is no IdentityKey.
        /// </summary>
        /// <remarks>
        /// <b>The difference from the missing element is the whole point.</b>
        /// A missing one throws on reading and is refused by that anyway; an
        /// empty one delivers a field of zero bytes without complaint. Without
        /// the express check a bundle with an empty IdentityKey would get
        /// through, and the signature check on it would answer a question
        /// about a key that does not exist.
        ///
        /// Noticed through a surviving mutation: it removed the check, and the
        /// test went on passing - it only had the case with the <i>missing</i>
        /// element.
        /// </remarks>
        [Test]
        public void AnEmptyIdentityKey_IsRefused()
        {

            var complete = OmemoIdentity.Create().Bundle().ToXml();

            Assert.Multiple(() =>
            {

                foreach (var part in new[] { "ik", "spk", "spks" })
                {

                    var empty = new XElement(complete);
                    empty.Elements().First(e => e.Name.LocalName == part).Value = "";

                    Assert.That(OmemoPep.TryReadBundle(empty, out _), Is.False, $"<{part}/> empty");

                    // And - the sharper case - a value that is valid Base64
                    // and still no key: three bytes instead of thirty-two.
                    //
                    // The check for "empty" alone does not strike it down, and
                    // that is exactly what two surviving mutations showed:
                    // they removed the length check, and the test stayed
                    // green, because it only knew the empty case. Too short a
                    // key would get through as far as the curve arithmetic.
                    var tooShort = new XElement(complete);
                    tooShort.Elements().First(e => e.Name.LocalName == part).Value = "AAAA";

                    Assert.That(OmemoPep.TryReadBundle(tooShort, out _), Is.False, $"<{part}/> too short");

                }

            });

        }

        #endregion

        #region TheItemId_IsLiterallyCurrent()

        /// <summary>
        /// XEP-0384, section 5.2: "The item id must be set to
        /// <c>current</c>."
        /// </summary>
        /// <remarks>
        /// <b>The same precaution for the fifth time.</b> The id could be set
        /// to something else without any test saying a word - publishing and
        /// fetching use the same constant and go on finding each other. Only a
        /// foreign client would search in vain, and there is none here. So the
        /// value stands here literally.
        /// </remarks>
        [Test]
        public void TheItemId_IsLiterallyCurrent()
        {
            Assert.Multiple(() =>
            {
                Assert.That(OmemoDeviceList.ItemId, Is.EqualTo("current"));
                Assert.That(OmemoDeviceList.Node,   Is.EqualTo("urn:xmpp:omemo:2:devices"));
                Assert.That(OmemoPep.BundlesNode,   Is.EqualTo("urn:xmpp:omemo:2:bundles"));
            });
        }

        #endregion

        #region ABundle_TravelsFromOneAccountToAnother()

        /// <summary>
        /// The whole way: Alice publishes, Bob fetches - and checks the
        /// signature himself.
        /// </summary>
        /// <remarks>
        /// <b>The test this stage is about.</b> It joins everything for the
        /// first time: the key material from D63, the XML rendering of today,
        /// the store of the server and the signature check at the receiver.
        /// And it checks the actual promise of PEP - <b>Bob gets Alice's
        /// bundle without Alice doing anything.</b>
        /// </remarks>
        [Test]
        public async Task ABundle_TravelsFromOneAccountToAnother()
        {

            var alice     = await ConnectClientAsync("alice");
            var bob       = await ConnectClientAsync("bob");

            var identity = OmemoIdentity.Create();

            var listPublished = await alice.Connection.PublishOmemoDeviceListAsync(
                                          new OmemoDeviceList([new OmemoDevice(identity.DeviceId,
                                                                               "phone")]));

            var bundlePublished = await alice.Connection.PublishOmemoBundleAsync(
                                            identity.DeviceId, identity.Bundle());

            Assert.Multiple(() =>
            {
                Assert.That(listPublished,   Is.True, "The device list could not be published.");
                Assert.That(bundlePublished, Is.True, "The bundle could not be published.");
            });

            var list = await bob.Connection.FetchOmemoDeviceListAsync(JID.Parse($"alice@{Server.Domain}"));

            Assert.That(list, Is.Not.Null, "Bob does not find Alice's device list.");
            Assert.That(list!.Contains(identity.DeviceId), Is.True);

            var bundle = await bob.Connection.FetchOmemoBundleAsync(JID.Parse($"alice@{Server.Domain}"),
                                                                     identity.DeviceId);

            Assert.That(bundle, Is.Not.Null, "Bob does not get Alice's bundle.");

            Assert.Multiple(() =>
            {

                Assert.That(Hex(bundle!.IdentityKey), Is.EqualTo(Hex(identity.PublicIdentityKey)));
                Assert.That(bundle.SignatureIsValid(), Is.True);

                // And with that a session can be begun at once - the actual
                // purpose of the whole distribution.
                var bobsIdentity = OmemoIdentity.Create();

                Assert.That(() => X3DH.Initiate(bobsIdentity, bundle), Throws.Nothing,
                            "No session can be begun from the fetched bundle.");

            });

        }

        #endregion

        #region TwoBundles_AreFetchedIndividually()

        /// <summary>
        /// Two devices, two bundles - and each one is fetched on its own.
        /// </summary>
        /// <remarks>
        /// <b>The reason for one entry per device.</b> A sender fetches
        /// exactly the bundle he needs instead of all of them - and a device
        /// that has used up its PreKey writes only its own entry anew.
        ///
        /// The test came about through a surviving mutation: whoever passes
        /// over the item id on fetching delivers <i>all</i> bundles, and the
        /// caller takes the first. With only one published device that is the
        /// same result - <b>with two the sender gets the wrong device</b>,
        /// encrypted for a phone that is not reading along at all, and nobody
        /// sees a mistake.
        /// </remarks>
        [Test]
        public async Task TwoBundles_AreFetchedIndividually()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var phone   = OmemoIdentity.Create(1000);
            var desktop = OmemoIdentity.Create(2000);

            await alice.Connection.PublishOmemoBundleAsync(phone.DeviceId, phone.Bundle());
            await alice.Connection.PublishOmemoBundleAsync(desktop.DeviceId, desktop.Bundle());

            var forPhone   = await bob.Connection.FetchOmemoBundleAsync(JID.Parse($"alice@{Server.Domain}"), 1000);
            var forDesktop = await bob.Connection.FetchOmemoBundleAsync(JID.Parse($"alice@{Server.Domain}"), 2000);

            Assert.Multiple(() =>
            {

                Assert.That(Hex(forPhone!.IdentityKey), Is.EqualTo(Hex(phone.PublicIdentityKey)),
                            "For device 1000 a foreign bundle arrived.");

                Assert.That(Hex(forDesktop!.IdentityKey), Is.EqualTo(Hex(desktop.PublicIdentityKey)),
                            "For device 2000 a foreign bundle arrived.");

                Assert.That(Hex(forPhone.IdentityKey), Is.Not.EqualTo(Hex(forDesktop.IdentityKey)));

            });

        }

        #endregion

        #region AnUnknownNode_IsAnsweredWithItemNotFound()

        /// <summary>
        /// A node with nothing in it is answered with
        /// <c>&lt;item-not-found/&gt;</c> - not with an empty result.
        /// </summary>
        /// <remarks>
        /// For the own client the two would be the same: either way it finds
        /// no entry. For a foreign one it is the difference between "there is
        /// nothing here" and "here is the answer, and it is empty" - and
        /// XEP-0060 provides the error for the first case.
        /// </remarks>
        [Test]
        public async Task AnUnknownNode_IsAnsweredWithItemNotFound()
        {

            var alice   = await ConnectClientAsync("alice");
            var session = Server.SessionOf(alice.FullJid.ToString())!;

            var before = session.Sent.Count;

            await alice.SendRawAsync(
                      "<iq type='get' id='empty-1'>" +
                      "<pubsub xmlns='http://jabber.org/protocol/pubsub'>" +
                      $"<items node='{OmemoPep.BundlesNode}'><item id='4711'/></items>" +
                      "</pubsub></iq>");

            await WaitFor(() => session.Sent.Skip(before)
                                       .Any(f => f.Contains("id='empty-1'", StringComparison.Ordinal)),
                          "the answer to the empty node");

            var reply = session.Sent.Skip(before)
                               .First(f => f.Contains("id='empty-1'", StringComparison.Ordinal));

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='error'"));
                Assert.That(reply, Does.Contain("item-not-found"));
            });

        }

        #endregion

        #region ARejectedPublish_IsReported()

        /// <summary>
        /// If the server refuses the publishing, the client reports it -
        /// instead of claiming success.
        /// </summary>
        /// <remarks>
        /// <b>This is the reason these methods have a return value at all</b>
        /// (see D38). Whoever publishes their device list and does not learn
        /// that it failed is unreachable for all their contacts and notices
        /// nothing of it: everything looks as it always does, only nobody
        /// writes to them encrypted any more.
        ///
        /// The case is brought about over a server without PEP - then the
        /// request goes the ordinary way and gets
        /// <c>&lt;service-unavailable/&gt;</c>.
        /// </remarks>
        [Test]
        public async Task ARejectedPublish_IsReported()
        {

            Server.OfferPersonalEventing = false;

            var alice = await ConnectClientAsync("alice");

            var reported = await alice.Connection.PublishOmemoDeviceListAsync(
                                     new OmemoDeviceList([new OmemoDevice(4711)]));

            Assert.Multiple(() =>
            {

                Assert.That(reported, Is.False,
                            "The client reports a success there never was.");

                Assert.That(Server.GetAccount($"alice@{Server.Domain}")!.PepNodes, Is.Empty);

            });

        }

        #endregion

        #region AnUnpublishedBundle_IsNotFound()

        /// <summary>
        /// Whoever has published nothing has nothing to fetch - and an account
        /// that does not exist looks just the same.
        /// </summary>
        /// <remarks>
        /// The equal treatment is deliberate: otherwise it could be found out
        /// over PEP which accounts exist on this server - the same
        /// consideration as with the registration (RFC 6120, section 13.11,
        /// see D50).
        /// </remarks>
        [Test]
        public async Task AnUnpublishedBundle_IsNotFound()
        {

            var bob = await ConnectClientAsync("bob");

            Server.AddAccount("alice");

            var withoutList     = await bob.Connection.FetchOmemoDeviceListAsync(JID.Parse($"alice@{Server.Domain}"));
            var withoutAccount  = await bob.Connection.FetchOmemoDeviceListAsync(JID.Parse($"nobody@{Server.Domain}"));
            var withoutBundle   = await bob.Connection.FetchOmemoBundleAsync(JID.Parse($"alice@{Server.Domain}"), 1);

            Assert.Multiple(() =>
            {
                Assert.That(withoutList,    Is.Null, "An account without a device list delivers one.");
                Assert.That(withoutAccount, Is.Null, "An account that does not exist delivers a device list.");
                Assert.That(withoutBundle,  Is.Null);
            });

        }

        #endregion

        #region ATamperedBundle_IsRefusedOnFetching()

        /// <summary>
        /// A bundle with a wrong signature does not reach the caller in the
        /// first place.
        /// </summary>
        /// <remarks>
        /// The server is the party OMEMO protects against - it keeps the
        /// bundle in stock and could exchange it. This is why the connection
        /// checks the signature itself and does <b>not</b> pass an invalid
        /// bundle on: to hand an unchecked one through would mean leaving the
        /// check to the one most likely to forget it.
        /// </remarks>
        [Test]
        public async Task ATamperedBundle_IsRefusedOnFetching()
        {

            var alice  = await ConnectClientAsync("alice");
            var bob    = await ConnectClientAsync("bob");

            var real     = OmemoIdentity.Create();
            var foreign  = OmemoIdentity.Create();

            // Alice's bundle with the Signed PreKey of a stranger - this is
            // how it would look if the server had exchanged it.
            var planted = real.Bundle() with { SignedPreKey = foreign.SignedPreKey.PublicKey };

            await alice.Connection.PublishOmemoBundleAsync(real.DeviceId, planted);

            Assert.That(await bob.Connection.FetchOmemoBundleAsync(JID.Parse($"alice@{Server.Domain}"),
                                                                    real.DeviceId),
                        Is.Null,
                        "A planted bundle reached the caller.");

        }

        #endregion

        #region PublishingIntoAForeignNode_IsForbidden()

        /// <summary>
        /// Into the PEP node of somebody else nobody writes.
        /// </summary>
        /// <remarks>
        /// Whoever were allowed to could exchange foreign bundles - and that
        /// is exactly the attack the signature over the Signed PreKey stands
        /// against. Two safeguards against the same thing are no waste here:
        /// the one works against the server, the other against every other
        /// user of the same server.
        /// </remarks>
        [Test]
        public async Task PublishingIntoAForeignNode_IsForbidden()
        {

            var alice   = await ConnectClientAsync("alice");
            var session = Server.SessionOf(alice.FullJid.ToString())!;

            Server.AddAccount("bob");

            var before = session.Sent.Count;

            await alice.SendRawAsync(
                      "<iq type='set' id='foreign-1' to='bob@" + Server.Domain + "'>" +
                      "<pubsub xmlns='http://jabber.org/protocol/pubsub'>" +
                      $"<publish node='{OmemoDeviceList.Node}'>" +
                      "<item id='current'><devices xmlns='urn:xmpp:omemo:2'>" +
                      "<device id='666'/></devices></item>" +
                      "</publish></pubsub></iq>");

            await WaitFor(() => session.Sent.Skip(before)
                                       .Any(f => f.Contains("id='foreign-1'", StringComparison.Ordinal)),
                          "the answer to the foreign publishing");

            var reply = session.Sent.Skip(before)
                               .First(f => f.Contains("id='foreign-1'", StringComparison.Ordinal));

            Assert.Multiple(() =>
            {

                Assert.That(reply, Does.Contain("type='error'"));
                Assert.That(reply, Does.Contain("forbidden"));

                Assert.That(Server.GetAccount($"bob@{Server.Domain}")!.PepNodes, Is.Empty,
                            "The foreign node was written into all the same.");

            });

        }

        #endregion

        #region ANewDeviceList_ReachesTheContacts()

        /// <summary>
        /// When Alice publishes a new device list, Bob learns of it - without
        /// asking.
        /// </summary>
        /// <remarks>
        /// Without this notification every sender would have to fetch the list
        /// before every message. With it he learns of a new device in the
        /// moment it comes about - and that is the difference between
        /// "encrypted to all devices" and "encrypted to the ones I last saw".
        /// </remarks>
        [Test]
        public async Task ANewDeviceList_ReachesTheContacts()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);
            var bob   = await ConnectClientAsync("bob",   createAccount: false);

            OmemoDeviceList? received = null;
            String?          fromWhom = null;

            bob.Connection.OnOmemoDeviceListChanged += (timestamp, sender, from, list, ct) =>
            {
                fromWhom = from;
                received = list;

                return Task.CompletedTask;

            };

            await alice.Connection.PublishOmemoDeviceListAsync(
                      new OmemoDeviceList([new OmemoDevice(4711, "phone")]));

            await WaitFor(() => received is not null, "the notification about Alice's device list");

            Assert.Multiple(() =>
            {
                Assert.That(fromWhom, Is.EqualTo($"alice@{Server.Domain}"));
                Assert.That(received!.Contains(4711u), Is.True);
            });

        }

        #endregion

        #region AForeignDeviceList_DoesNotTriggerAReannounce()

        /// <summary>
        /// In the device list of <b>somebody else</b> the own device has no
        /// business.
        /// </summary>
        /// <remarks>
        /// Without the check of whose list is coming in, this client would
        /// enter itself into <i>every</i> list it gets to see - and would
        /// publish into a foreign node in doing so, which the server refuses.
        /// The mistake would be without consequence and wrong all the same:
        /// with every contact who adds a device, a refused request would go
        /// out.
        ///
        /// Noticed through a surviving mutation - the test before it checked
        /// only the own list.
        /// </remarks>
        [Test]
        public async Task AForeignDeviceList_DoesNotTriggerAReannounce()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);
            var bob   = await ConnectClientAsync("bob",   createAccount: false);

            bob.Connection.OmemoDeviceId = 9999;

            var received = false;
            bob.Connection.OnOmemoDeviceListChanged += (timestamp, sender, _, _, ct) => { received = true; return Task.CompletedTask; };

            await alice.Connection.PublishOmemoDeviceListAsync(
                      new OmemoDeviceList([new OmemoDevice(1234)]));

            await WaitFor(() => received, "the notification about Alice's device list");

            // What is asked is whether Bob's client has sent anything at all -
            // and not whether Alice's list stayed unchanged.
            //
            // The first version checked the second of those and was worthless
            // by that: the server refuses foreign nodes anyway, so the list
            // stayed clean even when Bob tried. The test passed the mutation
            // that triggers exactly this attempt. What has to be checked is
            // the thing under test, not its neighbour.
            var bobsSession = Server.SessionOf(bob.FullJid.ToString())!;

            await WaitAgainst(() => bobsSession.Received.Any(
                                        f => f.Contains(OmemoPep.PubSubNamespace, StringComparison.Ordinal) &&
                                             f.Contains("<publish", StringComparison.Ordinal)),
                              "a publishing by Bob");

            Assert.That(Server.GetAccount($"alice@{Server.Domain}")!
                              .GetPepItems(OmemoDeviceList.Node, OmemoDeviceList.ItemId)[0].Payload,
                        Does.Not.Contain("9999"),
                        "Bob's device stands in Alice's device list.");

        }

        #endregion

        #region AMissingOwnDevice_IsReannounced()

        /// <summary>
        /// XEP-0384, section 5.2: if the own device is missing from the own
        /// list, it enters itself again - <b>without displacing the
        /// others</b>.
        /// </summary>
        /// <remarks>
        /// The case is not made up: another device of the same human being -
        /// or a tidying server - writes the list anew and forgets this device.
        /// From then on nobody writes to it encrypted any more, and <b>it
        /// notices nothing of it</b>, because nothing is missing for it: it
        /// goes on getting everything that comes unencrypted.
        ///
        /// This is why both are checked: that it enters itself again, and that
        /// the other device stays standing in doing so.
        /// </remarks>
        [Test]
        public async Task AMissingOwnDevice_IsReannounced()
        {

            var alice = await ConnectClientAsync("alice");

            // A second device of the same account - with its own resource,
            // otherwise the two would quarrel over the same one.
            var second = CreateClient("alice");
            second.Connection.Resource = "second-device";
            await second.ConnectAsync();

            alice.Connection.OmemoDeviceId = 1000;

            // The first device enters itself.
            await alice.Connection.PublishOmemoDeviceListAsync(
                      new OmemoDeviceList([new OmemoDevice(1000)]));

            // The second device writes the list anew - and forgets the first.
            await second.Connection.PublishOmemoDeviceListAsync(
                     new OmemoDeviceList([new OmemoDevice(2000)]));

            await WaitFor(() =>
            {

                var entries = Server.GetAccount($"alice@{Server.Domain}")!
                                    .GetPepItems(OmemoDeviceList.Node, OmemoDeviceList.ItemId);

                return entries.Count == 1 &&
                       entries[0].Payload.Contains("id=\"1000\"", StringComparison.Ordinal);

            },
            "the re-entry of the first device");

            var list = Server.GetAccount($"alice@{Server.Domain}")!
                             .GetPepItems(OmemoDeviceList.Node, OmemoDeviceList.ItemId)[0].Payload;

            Assert.That(list, Does.Contain("id=\"2000\""),
                        "The re-entry has displaced the other device.");

        }

        #endregion

    }

}
