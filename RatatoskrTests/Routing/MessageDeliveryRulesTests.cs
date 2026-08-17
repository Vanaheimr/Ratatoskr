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

using System.Collections.Concurrent;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// RFC 6121, section 8.5: Where a message goes depends on its kind
    /// <b>and</b> on the shape of the address.
    /// </summary>
    /// <remarks>
    /// Up to here the server delivered everything alike. The distinction is no
    /// formality — two of the rules are MUST rules, and both prevent something
    /// the sender does not want:
    ///
    /// <list type="bullet">
    ///   <item>
    ///     A <c>groupchat</c> to an account is never deliverable. It belongs
    ///     into a room; addressed to a bare JID, no resource would know what to
    ///     do with it.
    ///   </item>
    ///   <item>
    ///     A resource with a negative priority gets nothing that only went to
    ///     the account. That is precisely what a client sets it for — the
    ///     device stays addressable directly and keeps out of the rest.
    ///   </item>
    /// </list>
    /// </remarks>
    [TestFixture]
    public class MessageDeliveryRulesTests : AXMPPTests
    {

        #region Helper functions

        /// <summary>
        /// Logs a second client of the same account in and sets its priority.
        /// </summary>
        private async Task<XMPPClient> ResourceAsync(String localPart, String resource, Int32 priority)
        {

            if (Server.GetAccount($"{localPart}@{Server.Domain}") is null)
                Server.AddAccount(localPart);

            var client = CreateClient(localPart);
            client.Connection.Resource = resource;

            await client.ConnectAsync();

            await client.SendRawAsync($"<presence><priority>{priority}</priority></presence>");

            await WaitFor(() => Server.SessionOf(client.FullJid!)?.PresencePriority == priority,
                          $"the priority {priority} for {resource}");

            return client;

        }

        #endregion


        #region AGroupchatToAnAccount_IsRefused()

        /// <summary>
        /// A <c>groupchat</c> to a bare JID is not delivered but refused
        /// (section 8.5.2.1.1).
        /// </summary>
        /// <remarks>
        /// What is checked as well is <b>whom</b> the refusal is addressed to.
        /// That sounds like a formality and is none: A stanza to a client must
        /// be addressed to it (RFC 6120, section 8.1.1), and a client checking
        /// that would discard a refusal carrying someone else's <c>to</c>
        /// silently. That it arrives in the right stream is the one half; that
        /// it names the right recipient the other - and the two are easy to
        /// mistake for one another, because delivery already works when only
        /// the first one holds.
        /// </remarks>
        [Test]
        public async Task AGroupchatToAnAccount_IsRefused()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var inbox = new ConcurrentQueue<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { inbox.Enqueue(m); return Task.CompletedTask; };

            var errors = new ConcurrentQueue<StanzaError>();
            alice.Connection.OnStanzaError += (timestamp, sender, from, e, ct) => { errors.Enqueue(e); return Task.CompletedTask; };

            var rawFrames = new ConcurrentQueue<String>();
            alice.Connection.OnRawXml += (timestamp, sender, x, ct) =>
            {
                if (x.StartsWith("<<<", StringComparison.Ordinal) &&
                    x.Contains("to-the-account", StringComparison.Ordinal))
                {
                    rawFrames.Enqueue(x);
                }

                return Task.CompletedTask;

            };

            await alice.SendRawAsync(
                      $"<message to='{bob.BareJid}' type='groupchat' id='to-the-account'>" +
                      "<body>Belongs into a room</body></message>");

            await WaitFor(() => !errors.IsEmpty, "the refusal at the sender");

            errors.TryDequeue(out var refused);
            rawFrames.TryDequeue(out var stanza);

            Assert.Multiple(() =>
            {

                Assert.That(refused!.Condition, Is.EqualTo("service-unavailable"));

                Assert.That(stanza, Does.Contain($"to='{alice.FullJid}'"),
                            "The refusal must be addressed to the sender, " +
                            "not to the address it did not go to.");

                Assert.That(inbox, Is.Empty,
                            "A groupchat to an account must not reach a resource.");

            });

        }

        #endregion

        #region AGroupchatToAResource_IsDelivered()

        /// <summary>
        /// The counter-check: addressed to a matching resource the same
        /// <c>groupchat</c> is delivered (section 8.5.3.1).
        /// </summary>
        /// <remarks>
        /// That is exactly how a room delivers - it sends to
        /// <c>user@server/resource</c>, not to the account. Without this
        /// counter-check the collection would pass even if <c>groupchat</c> did
        /// not arrive at all any more, and the room feature would be unusable.
        /// </remarks>
        [Test]
        public async Task AGroupchatToAResource_IsDelivered()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var inbox = new ConcurrentQueue<XMPPMessage>();
            bob.OnMessage += (timestamp, sender, m, ct) => { inbox.Enqueue(m); return Task.CompletedTask; };

            await alice.SendRawAsync(
                      $"<message to='{bob.FullJid}' type='groupchat' id='to-the-resource'>" +
                      "<body>From the room</body></message>");

            await WaitFor(() => !inbox.IsEmpty, "the delivery to the resource");

            inbox.TryDequeue(out var received);

            Assert.That(received!.Type, Is.EqualTo(MessageType.GroupChat));

        }

        #endregion

        #region AHeadlineReachesEveryResource()

        /// <summary>
        /// A <c>headline</c> to the bare JID goes to <b>all</b> resources with
        /// a non-negative priority (section 8.5.2.1.1).
        /// </summary>
        /// <remarks>
        /// It is a notice to the human being and not to a device — which one of
        /// them they are looking at right now nobody knows. An ordinary message
        /// goes to one resource instead; the counter-check stands in the same
        /// test, because otherwise it would pass even if simply everything went
        /// to everyone.
        /// </remarks>
        [Test]
        public async Task AHeadlineReachesEveryResource()
        {

            var alice   = await ConnectClientAsync("alice");
            var mobile  = await ResourceAsync("bob", "Mobile",  1);
            var desktop = await ResourceAsync("bob", "Desktop", 1);

            var atTheMobile  = new ConcurrentQueue<XMPPMessage>();
            var atTheDesktop = new ConcurrentQueue<XMPPMessage>();

            mobile.OnMessage  += (timestamp, sender, m, ct) => { atTheMobile.Enqueue(m); return Task.CompletedTask; };
            desktop.OnMessage += (timestamp, sender, m, ct) => { atTheDesktop.Enqueue(m); return Task.CompletedTask; };

            await alice.SendRawAsync(
                      $"<message to='{mobile.BareJid}' type='headline' id='notice'>" +
                      "<body>Price has fallen</body></message>");

            await WaitFor(() => !atTheMobile.IsEmpty && !atTheDesktop.IsEmpty,
                          "the notice on both devices");

            // And now an ordinary message - that one goes to one resource.
            await alice.SendRawAsync(
                      $"<message to='{mobile.BareJid}' type='chat' id='to-one'>" +
                      "<body>Only to you</body></message>");

            await WaitFor(() => atTheMobile.Any(m => m.MessageId == "to-one") ||
                                atTheDesktop.Any(m => m.MessageId == "to-one"),
                          "the ordinary message");

            Assert.That(atTheMobile.Count(m => m.MessageId == "to-one") +
                        atTheDesktop.Count(m => m.MessageId == "to-one"),
                        Is.EqualTo(1),
                        "An ordinary message goes to one resource, not to all of them.");

        }

        #endregion

        #region ANegativePriority_ReceivesNothingFromTheAccount()

        /// <summary>
        /// A resource with a negative priority gets nothing that went to the
        /// bare JID — but stays addressable directly
        /// (sections 8.5.2.1.1 and 8.5.3.1).
        /// </summary>
        /// <remarks>
        /// Both halves belong together. Without the second one the negative
        /// priority would be a logging out, and that is precisely what it is
        /// not: The device stays reachable, it only keeps out of the traffic
        /// that is addressed to the account.
        /// </remarks>
        [Test]
        public async Task ANegativePriority_ReceivesNothingFromTheAccount()
        {

            var alice        = await ConnectClientAsync("alice");
            var secondDevice = await ResourceAsync("bob", "SecondDevice", -1);

            var inbox = new ConcurrentQueue<XMPPMessage>();
            secondDevice.OnMessage += (timestamp, sender, m, ct) => { inbox.Enqueue(m); return Task.CompletedTask; };

            await alice.SendRawAsync(
                      $"<message to='{secondDevice.BareJid}' type='chat' id='to-the-account'>" +
                      "<body>To the account</body></message>");

            // Addressed to the same resource - that must arrive.
            await alice.SendRawAsync(
                      $"<message to='{secondDevice.FullJid}' type='chat' id='to-the-resource'>" +
                      "<body>To the resource</body></message>");

            await WaitFor(() => inbox.Any(m => m.MessageId == "to-the-resource"),
                          "the directed message");

            Assert.That(inbox.Any(m => m.MessageId == "to-the-account"), Is.False,
                        "What went to the account must not reach a negative priority.");

        }

        #endregion

        #region AnErrorToAnAccount_IsSilentlyIgnored()

        /// <summary>
        /// An error message to the bare JID is silently passed over
        /// (section 8.5.2.1.1).
        /// </summary>
        /// <remarks>
        /// Answering an error with an error would be the beginning of a loop.
        /// Addressed to a matching resource it must be delivered though — it is
        /// after all the answer to something that this very resource sent.
        /// </remarks>
        [Test]
        public async Task AnErrorToAnAccount_IsSilentlyIgnored()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var atBob = new ConcurrentQueue<StanzaError>();
            bob.Connection.OnStanzaError += (timestamp, sender, from, e, ct) => { atBob.Enqueue(e); return Task.CompletedTask; };

            var atAlice = new ConcurrentQueue<StanzaError>();
            alice.Connection.OnStanzaError += (timestamp, sender, from, e, ct) => { atAlice.Enqueue(e); return Task.CompletedTask; };

            const String errorBody = "<error type='cancel'>" +
                                     "<service-unavailable xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                                     "</error>";

            await alice.SendRawAsync(
                      $"<message to='{bob.BareJid}' type='error' id='to-the-account'>{errorBody}</message>");

            await alice.SendRawAsync(
                      $"<message to='{bob.FullJid}' type='error' id='to-the-resource'>{errorBody}</message>");

            await WaitFor(() => !atBob.IsEmpty, "the directed error");

            Assert.Multiple(() =>
            {

                Assert.That(atBob, Has.Count.EqualTo(1),
                            "Only the directed error may arrive.");

                Assert.That(atAlice, Is.Empty,
                            "And an error is not followed by an error.");

            });

        }

        #endregion

        #region ThePriorityIsReadFromThePresence()

        /// <summary>
        /// The priority is read from the presence; if it is missing or
        /// unusable, 0 applies.
        /// </summary>
        /// <remarks>
        /// An unreadable number must not prevent a delivery - it is a wish of
        /// the client and no contract. The range is limited to -128 to +127 by
        /// RFC 6121, section 4.7.2.3.
        /// </remarks>
        [Test]
        public void ThePriorityIsReadFromThePresence()
        {

            Assert.Multiple(() =>
            {

                Assert.That(XMPPSession.ReadPriority("<presence/>"), Is.EqualTo(0));

                Assert.That(XMPPSession.ReadPriority("<presence><priority>5</priority></presence>"),
                            Is.EqualTo(5));

                Assert.That(XMPPSession.ReadPriority("<presence><priority>-1</priority></presence>"),
                            Is.EqualTo(-1));

                Assert.That(XMPPSession.ReadPriority("<presence><priority>lots</priority></presence>"),
                            Is.EqualTo(0),
                            "Something unusable counts as 0 and not as an error.");

                Assert.That(XMPPSession.ReadPriority("<presence><priority>9999</priority></presence>"),
                            Is.EqualTo(127),
                            "The range ends at +127.");

            });

        }

        #endregion

    }

}
