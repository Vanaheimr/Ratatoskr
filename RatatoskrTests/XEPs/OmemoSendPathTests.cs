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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// What the sending path puts on the wire, and what it does not.
    /// </summary>
    /// <remarks>
    /// Both of these were measured in a real conversation before they were
    /// written here. Writing to somebody whose client does the older OMEMO
    /// produced an <c>&lt;encrypted/&gt;</c> with a header carrying the
    /// sender's device and not one key - the recipient's client answered "omemo
    /// message that does not contain a key for this device" - and the same
    /// question about the same device list went to the same server before every
    /// single message, the first of them running into the IQ timeout.
    /// </remarks>
    [TestFixture]
    public class OmemoSendPathTests : AXMPPTests
    {

        #region WhatNobodyCanRead_DoesNotGoOut()

        /// <summary>
        /// Bob does no OMEMO. Then nothing encrypted goes to Bob - and the
        /// caller is told, rather than finding out from his client.
        /// </summary>
        [Test]
        public async Task WhatNobodyCanRead_DoesNotGoOut()
        {

            MakeContacts("alice", "bob");

            var alice   = await ConnectClientAsync("alice", createAccount: false);
            var bob     = await ConnectClientAsync("bob",   createAccount: false);

            // Bob switches nothing on: no device list, no bundle, nothing to
            // encrypt to.
            Assert.That(await alice.EnableOmemoAsync(), Is.True,
                        "Alice could not switch OMEMO on.");

            var bobJid  = JID.Parse($"bob@{Server.Domain}");

            var sent    = await alice.SendEncryptedMessageAsync(bobJid, "Shall we meet at eight?");

            Assert.Multiple(() =>
            {

                Assert.That(sent.Readable, Is.False,
                            "Bob has no device at all, so this is the wrong state to measure");

                Assert.That(sent.MessageId, Is.Null,
                            "an id was handed back for a stanza that should never have gone out");

                Assert.That(sent.NoOmemoThere, Is.True,
                            "Bob's server answered that there is nothing - without that the " +
                            "caller above may not fall back to the clear");

                // The point. An <encrypted/> whose header has no key in it is
                // readable by nobody - not by Bob, not by Alice's own further
                // devices - and whoever then falls back to the clear has sent
                // Bob both.
                var frames = Server.Sessions.SelectMany(session => session.Received.Concat(session.Sent)).ToList();

                Assert.That(frames.Any(frame => frame.Contains("<encrypted", StringComparison.Ordinal)),
                            Is.False,
                            "an encrypted element went over the wire that nobody could have read");

            });

        }

        #endregion

        #region TheSameQuestion_IsNotAskedBeforeEveryMessage()

        /// <summary>
        /// Three messages to the same person ask about their devices once, not
        /// three times.
        /// </summary>
        /// <remarks>
        /// The asking is on the sending path, before anything goes out, with
        /// the IQ timeout as its upper bound - so every repetition of it is a
        /// chance for somebody to wait ten seconds for a message that is
        /// already written.
        /// </remarks>
        [Test]
        public async Task TheSameQuestion_IsNotAskedBeforeEveryMessage()
        {

            MakeContacts("alice", "bob");

            var alice  = await ConnectClientAsync("alice", createAccount: false);
            var bob    = await ConnectClientAsync("bob",   createAccount: false);

            Assert.Multiple(() =>
            {
                Assert.That(alice.EnableOmemoAsync().GetAwaiter().GetResult(), Is.True,
                            "Alice could not switch OMEMO on.");
                Assert.That(bob.EnableOmemoAsync().GetAwaiter().GetResult(), Is.True,
                            "Bob could not switch OMEMO on.");
            });

            var bobJid  = JID.Parse($"bob@{Server.Domain}");

            await WaitFor(() => Server.Sessions.Any(session => JID.AreEqual(session.FullJid,
                                                                           alice.Connection.FullJid.ToString())),
                          "Alice's session on the server");

            var session = Server.Sessions.First(s => JID.AreEqual(s.FullJid, alice.Connection.FullJid.ToString()));

            for (var number = 1; number <= 3; number++)
            {

                var sent = await alice.SendEncryptedMessageAsync(bobJid, $"Message {number}");

                Assert.That(sent.Readable, Is.True,
                            $"message {number} reached nobody - then this test measures the wrong thing");

            }

            var asked = session.Received.Count(frame => frame.Contains("urn:xmpp:omemo:2:devices",
                                                                       StringComparison.Ordinal) &&
                                                        frame.Contains(bobJid.ToString(),
                                                                       StringComparison.Ordinal));

            Assert.That(asked, Is.LessThanOrEqualTo(1),
                        $"Bob's device list was asked for {asked} times for three messages");

        }

        #endregion

        #region WhatWasNeverSent_CannotBeCorrected()

        /// <summary>
        /// A message that did not go out is not the last message to anybody.
        /// </summary>
        /// <remarks>
        /// XEP-0308 corrects the last thing said to an address by naming its
        /// id. Were an unsent message written down as that, the next
        /// correction would name a stanza nobody has ever seen - and the one
        /// before it, which somebody did see, would stand uncorrected.
        /// </remarks>
        [Test]
        public async Task WhatWasNeverSent_CannotBeCorrected()
        {

            MakeContacts("alice", "bob");

            var alice   = await ConnectClientAsync("alice", createAccount: false);
            var bob     = await ConnectClientAsync("bob",   createAccount: false);

            Assert.That(await alice.EnableOmemoAsync(), Is.True,
                        "Alice could not switch OMEMO on.");

            var bobJid  = JID.Parse($"bob@{Server.Domain}");

            var sent    = await alice.SendEncryptedMessageAsync(bobJid, "Are you there?");

            Assert.That(sent.MessageId, Is.Null,
                        "nothing should have gone out, so this is the wrong state to measure");

            var correction = await alice.CorrectLastAsync("Are you there, Bob?", bobJid);

            Assert.That(correction, Is.Null,
                        "a message that never went out was taken for the last one said");

        }

        #endregion

        #region ANotification_TakesTheOlderAnswerBack()

        /// <summary>
        /// Bob publishes nothing, so Alice is told so and remembers it - and
        /// then Bob switches OMEMO on.
        /// </summary>
        /// <remarks>
        /// <b>This is what makes a remembered answer defensible at all.</b>
        /// Whoever changes their device list publishes it, and XEP-0163 tells
        /// everybody subscribed without anyone asking - so the usual way the
        /// remembered "publishes nothing" stops being true is that the truth
        /// arrives by itself, and not that a span runs out. Were it only the
        /// span, somebody who switches OMEMO on would be written to in the
        /// clear until it did.
        /// </remarks>
        [Test]
        public async Task ANotification_TakesTheOlderAnswerBack()
        {

            MakeContacts("alice", "bob");

            var alice   = await ConnectClientAsync("alice", createAccount: false);
            var bob     = await ConnectClientAsync("bob",   createAccount: false);

            Assert.That(await alice.EnableOmemoAsync(), Is.True,
                        "Alice could not switch OMEMO on.");

            var bobJid  = JID.Parse($"bob@{Server.Domain}");

            var first   = await alice.SendEncryptedMessageAsync(bobJid, "Are you there?");

            Assert.Multiple(() =>
            {

                Assert.That(first.Readable, Is.False,
                            "Bob publishes nothing yet, so this is the wrong state to measure");

                Assert.That(alice.Connection.OmemoDeviceLists.Get(bobJid)?.Lookup,
                            Is.EqualTo(PepLookup.Absent),
                            "the answer about Bob was not remembered at all");

            });

            Assert.That(await bob.EnableOmemoAsync(), Is.True,
                        "Bob could not switch OMEMO on.");

            await WaitFor(() => alice.Connection.OmemoDeviceLists.Get(bobJid)?.Lookup == PepLookup.Published,
                          "the notification about Bob's devices at Alice");

            var second  = await alice.SendEncryptedMessageAsync(bobJid, "There you are.");

            Assert.That(second.Readable, Is.True,
                        "Bob published his devices, and Alice went on writing past him");

        }

        #endregion

        #region OnesOwnPublishing_IsAnAnswerAsWell()

        /// <summary>
        /// Switching OMEMO on publishes this account's own device list, and
        /// what was just published needs no asking after.
        /// </summary>
        [Test]
        public async Task OnesOwnPublishing_IsAnAnswerAsWell()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);

            Assert.That(await alice.EnableOmemoAsync(), Is.True,
                        "Alice could not switch OMEMO on.");

            var known = alice.Connection.OmemoDeviceLists.Get(alice.BareJid);

            Assert.Multiple(() =>
            {

                Assert.That(known,               Is.Not.Null,
                            "this account's own list was published and then asked for again");

                Assert.That(known!.Value.Lookup, Is.EqualTo(PepLookup.Published));

                Assert.That(known.Value.List!.Contains(alice.Omemo!.Identity.DeviceId), Is.True);

            });

        }

        #endregion

    }

}
