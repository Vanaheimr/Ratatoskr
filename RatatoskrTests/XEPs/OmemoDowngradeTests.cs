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
    /// When an encrypted message reaches nobody, does the sender know that the
    /// recipient cannot read one - or only that this side could not find out?
    /// </summary>
    /// <remarks>
    /// <b>The question a fallback to the clear hangs on.</b> This class's own
    /// remark has said since the beginning that sending unencrypted is the
    /// worst of the three answers for a single unreachable device, "the sender
    /// believes they have encrypted" - and that an attacker who makes a bundle
    /// unreachable thereby gets the plaintext. The same holds one level up, for
    /// a whole recipient whose device list did not arrive, and there the
    /// reasoning had not been followed: an unreadable message was an unreadable
    /// message, whatever the reason, and the caller above fell back to the clear
    /// for the first message of any conversation.
    ///
    /// So one value says it: <see cref="OmemoEncryptionResult.NoOmemoFor"/>, true
    /// only where the recipient's server answered and the answer was that there
    /// is nothing. Built on the managers directly, because what is measured is
    /// the bookkeeping and a server in between would only add ways to be slow.
    /// </remarks>
    [TestFixture]
    public class OmemoDowngradeTests
    {

        #region Data

        private static readonly JID  me   = JID.Parse("me@example.org");
        private static readonly JID  bob  = JID.Parse("bob@example.org");

        #endregion

        #region Helper functions

        /// <summary>
        /// A manager whose two fetches answer out of a table, the way a server
        /// would - one entry per address, and the lookup along with it.
        /// </summary>
        private static OmemoManager Manager(Func<JID, (OmemoDeviceList? List, PepLookup Lookup)>          Lists,
                                            Func<JID, UInt32, (OmemoBundle? Bundle, PepLookup Lookup)>?   Bundles = null)

            => new (new OmemoMemoryStore(),
                    me,
                    jid            => Task.FromResult(Lists(jid)),
                    (jid, device)  => Task.FromResult(Bundles is not null
                                                          ? Bundles(jid, device)
                                                          : (null, PepLookup.Absent)));

        private static (OmemoDeviceList? List, PepLookup Lookup) Nothing(PepLookup lookup)
            => (null, lookup);

        private static (OmemoDeviceList? List, PepLookup Lookup) Devices(params UInt32[] ids)
            => (new OmemoDeviceList([.. ids.Select(id => new OmemoDevice(id))]), PepLookup.Published);

        private static IReadOnlyList<XElement> Body(String text)
            => [new XElement("body", text)];

        /// <summary>
        /// What was said about this recipient, out of the skipped entries.
        /// </summary>
        private static OmemoSkippedDevice About(OmemoEncryptionResult result, JID jid)
            => result.Skipped.First(device => device.Jid.Bare == jid.Bare);

        #endregion

        #region ASilentServer_IsNotAnAnswerAboutTheRecipient()

        /// <summary>
        /// Nobody answered about Bob's devices, and that says nothing about
        /// Bob.
        /// </summary>
        /// <remarks>
        /// <b>This is the downgrade, in the one place it can still be
        /// stopped.</b> Further up there is only "the message reached nobody",
        /// and the answer to that - write it in the clear instead - is right for
        /// most of the world and wrong exactly here. Whoever can delay one
        /// stanza by a few seconds produces this state at will, and the message
        /// that then goes out in the clear is the first one of a conversation:
        /// the one that says what the conversation is about.
        /// </remarks>
        [Test]
        public async Task ASilentServer_IsNotAnAnswerAboutTheRecipient()
        {

            var manager = Manager(jid => jid.Bare == bob.Bare
                                             ? Nothing(PepLookup.Unknown)
                                             : Nothing(PepLookup.Absent));

            var result  = await manager.EncryptAsync([bob], Body("Eight at the station, then."));

            Assert.Multiple(() =>
            {

                Assert.That(result.Element.Keys.ContainsKey(bob.Bare), Is.False,
                            "nothing was encrypted to Bob, or this test measures the wrong state");

                Assert.That(result.NoOmemoFor(bob), Is.False,
                            "silence about Bob's devices came back as Bob having none - which is a " +
                            "caller's reason to send this in the clear");

                Assert.That(About(result, bob).Reason, Does.Contain("no answer"),
                            "and the reason shown to a human being says nothing about why");

            });

        }

        #endregion

        #region ARecipientWhoPublishesNothing_IsAnAnswer()

        /// <summary>
        /// The case that must stay a fallback: no node, and the server said so.
        /// </summary>
        /// <remarks>
        /// Most of the world does no OMEMO. Were this to refuse as well, the
        /// program could write to almost nobody - which is why the distinction
        /// has to be made at all rather than refusing whenever the encryption
        /// reaches nobody.
        /// </remarks>
        [Test]
        public async Task ARecipientWhoPublishesNothing_IsAnAnswer()
        {

            var manager = Manager(_ => Nothing(PepLookup.Absent));

            var result  = await manager.EncryptAsync([bob], Body("In the clear, then."));

            Assert.Multiple(() =>
            {

                Assert.That(result.NoOmemoFor(bob), Is.True,
                            "a contact without OMEMO became a contact this program cannot write to");

                Assert.That(About(result, bob).Reason, Does.Contain("publishes no"));

            });

        }

        #endregion

        #region AnEmptyPublishedList_IsAnAnswerAsWell()

        /// <summary>
        /// A list that is there and holds nothing.
        /// </summary>
        /// <remarks>
        /// What whoever removes their last device leaves behind: Conversations
        /// publishes the empty list rather than retracting the node. It is a
        /// statement and belongs with the refusal that names the node as
        /// missing - and it arrives as a result, so nothing but this line puts
        /// it on that side.
        /// </remarks>
        [Test]
        public async Task AnEmptyPublishedList_IsAnAnswerAsWell()
        {

            var manager = Manager(jid => jid.Bare == bob.Bare
                                             ? Devices()
                                             : Nothing(PepLookup.Absent));

            var result  = await manager.EncryptAsync([bob], Body("In the clear, then."));

            Assert.That(result.NoOmemoFor(bob), Is.True,
                        "an empty list is somebody saying they have no device, and it was not read " +
                        "as one");

        }

        #endregion

        #region ADeviceWithoutAFetchableBundle_SaysNothingAboutItsOwner()

        /// <summary>
        /// Bob publishes one device and its key material cannot be had.
        /// </summary>
        /// <remarks>
        /// <b>Both ways round, and neither of them is a fallback.</b> A bundle
        /// nobody answered about is the attack the signature check exists for,
        /// one step earlier. A device that publishes no bundle at all is a
        /// broken client - but its owner has published a device, which is them
        /// saying they do OMEMO, so writing to them in the clear contradicts the
        /// one thing they did say.
        /// </remarks>
        [TestCase(PepLookup.Unknown,  "no answer",   TestName = "A bundle nobody answered about")]
        [TestCase(PepLookup.Absent,   "no bundle",   TestName = "A device that publishes no bundle")]
        public async Task ADeviceWithoutAFetchableBundle_SaysNothingAboutItsOwner(PepLookup  Lookup,
                                                                                  String     Reason)
        {

            var manager = Manager(jid      => jid.Bare == bob.Bare
                                                  ? Devices(4711)
                                                  : Nothing(PepLookup.Absent),
                                  (_, _)   => (null, Lookup));

            var result  = await manager.EncryptAsync([bob], Body("Eight at the station, then."));

            Assert.Multiple(() =>
            {

                Assert.That(result.Element.Keys.ContainsKey(bob.Bare), Is.False,
                            "nothing was encrypted to Bob, or this test measures the wrong state");

                Assert.That(result.NoOmemoFor(bob), Is.False,
                            "a device that could not be written to was read as its owner having none");

                Assert.That(About(result, bob).DeviceId, Is.EqualTo(4711u),
                            "the entry is about the device and not about the whole account");

                Assert.That(About(result, bob).Reason, Does.Contain(Reason));

            });

        }

        #endregion

        #region AnAnswerIsAboutTheAddressItCameFor()

        /// <summary>
        /// One's own devices are fetched in the same pass, and what is learnt
        /// about them is not learnt about the recipient.
        /// </summary>
        /// <remarks>
        /// <b>Why this is a per-recipient question and not one flag on the
        /// result.</b> The encryption goes to the recipient and to one's own
        /// further devices - so a silent answer about one's own list would
        /// otherwise refuse a message to a contact whose list arrived perfectly
        /// well, and an answered one about oneself would license a fallback
        /// nobody said anything about.
        /// </remarks>
        [Test]
        public async Task AnAnswerIsAboutTheAddressItCameFor()
        {

            // Bob said he has nothing; about this account nobody said anything.
            var hisAnswer = await Manager(jid => jid.Bare == bob.Bare
                                                     ? Nothing(PepLookup.Absent)
                                                     : Nothing(PepLookup.Unknown))
                                      .EncryptAsync([bob], Body("In the clear, then."));

            // And the other way round.
            var ownAnswer = await Manager(jid => jid.Bare == bob.Bare
                                                     ? Nothing(PepLookup.Unknown)
                                                     : Nothing(PepLookup.Absent))
                                      .EncryptAsync([bob], Body("Eight at the station, then."));

            Assert.Multiple(() =>
            {

                Assert.That(hisAnswer.NoOmemoFor(bob), Is.True,
                            "this account's own server being silent withheld an answer Bob had given");

                Assert.That(ownAnswer.NoOmemoFor(bob), Is.False,
                            "an answer about this account was taken for an answer about Bob");

            });

        }

        #endregion

    }

}
