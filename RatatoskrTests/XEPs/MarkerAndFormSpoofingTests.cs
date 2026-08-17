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
    /// Two statements a stranger could make about somebody else's affairs.
    /// </summary>
    /// <remarks>
    /// Both were listed under "low / design" in the review and both turned out
    /// to be real. They share a shape: the sender is named honestly - the server
    /// stamps it - but the statement is not about the sender. A marker is about
    /// a message, a subscription request is about a node, and displaying either
    /// without asking who said it lets a stranger write in somebody else's
    /// ledger.
    /// </remarks>
    [TestFixture]
    public class MarkerAndFormSpoofingTests : AXMPPTests
    {

        #region AMarkerFromTheRecipient_IsBelieved()

        /// <summary>
        /// The counter-check first, and it carries more weight than usual here:
        /// a check on markers that refused the ordinary case would take the
        /// read marks out of the client altogether, and nobody would notice
        /// until somebody missed them.
        /// </summary>
        [Test]
        public async Task AMarkerFromTheRecipient_IsBelieved()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);
            var bob   = await ConnectClientAsync("bob",   createAccount: false);

            var seen = new List<ChatMarker>();
            alice.Connection.OnChatMarker += (timestamp, sender, marker, ct) => { lock (seen) seen.Add(marker);  return Task.CompletedTask; };

            var id = await alice.Connection.SendMessageAsync($"bob@{Server.Domain}",
                                                             "have you read this?",
                                                             markable: true);

            await bob.Connection.SendChatMarkerAsync($"alice@{Server.Domain}",
                                                     id,
                                                     ChatMarkerType.Displayed);

            // Collected rather than caught one at a time: Bob's client answers
            // a markable message with a Received of its own accord, so two
            // markers come for the same message. That both arrive is the
            // stronger statement anyway.
            //
            // Waited for as a pair, and that is the correction the Debian leg
            // of CI paid for. This waited for the Displayed alone and then
            // asserted the Received was there too - which held on Windows,
            // where the automatic answer happened always to come first, and
            // failed on Linux the first time the suite ran there: "Expected:
            // some item equal to Received. But was: < Displayed >". Nothing
            // orders the two. They are separate stanzas from separate
            // decisions, one made by Bob's client and one by this test, and an
            // assertion is only entitled to wait for what it goes on to check.
            await WaitFor(() => { lock (seen) return seen.Any(m => m.Type == ChatMarkerType.Displayed) &&
                                                       seen.Any(m => m.Type == ChatMarkerType.Received); },
                          "both markers from Bob - his client's automatic Received and the Displayed");

            // That both got through is the wait above, which is where it
            // belongs - naming which of the two is missing is something the
            // timeout message does and an assertion afterwards cannot. What is
            // left to check here is what they are about.
            lock (seen)
                Assert.That(seen.Select(m => m.MessageId), Is.All.EqualTo(id),
                            "Every marker that got through is about the message that was sent.");

        }

        #endregion

        #region AMarkerFromSomebodyElse_IsRefused()

        /// <summary>
        /// The finding. Mallory marks a message that went to Bob.
        /// </summary>
        /// <remarks>
        /// XEP-0184 receipts have had this check from the start and markers
        /// never got one, although they say the same kind of thing about the
        /// same kind of message. What the forgery buys is small and real: a
        /// read mark on a message the recipient never read.
        /// </remarks>
        [Test]
        public async Task AMarkerFromSomebodyElse_IsRefused()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice", createAccount: false);

            Server.AddAccount("mallory");
            var mallory = await ConnectClientAsync("mallory", createAccount: false);

            ChatMarker? seen      = null;
            String?     spoofing  = null;

            alice.Connection.OnChatMarker      += (timestamp, sender, marker, ct)  => { seen     = marker; return Task.CompletedTask; };
            alice.Connection.OnSpoofingAttempt += (timestamp, sender, message, ct) => { spoofing = message; return Task.CompletedTask; };

            var id = await alice.Connection.SendMessageAsync($"bob@{Server.Domain}",
                                                             "for Bob only",
                                                             markable: true);

            await mallory.Connection.SendChatMarkerAsync($"alice@{Server.Domain}",
                                                         id,
                                                         ChatMarkerType.Displayed);

            await WaitFor(() => spoofing is not null, "the forged marker to be reported");

            Assert.Multiple(() =>
            {
                Assert.That(spoofing, Does.Contain("mallory"));
                Assert.That(seen,     Is.Null,
                            "Nothing a stranger says about somebody else's message may reach " +
                            "the application.");
            });

        }

        #endregion

        #region ASubscriptionRequestFromAContact_IsRefused()

        /// <summary>
        /// XEP-0060, section 8.6: the request that asks the owner of a node to
        /// let somebody in is sent by whoever hosts the node. It was taken from
        /// anybody at all.
        /// </summary>
        /// <remarks>
        /// The damage is social rather than technical, and that makes it worse
        /// rather than better: the client shows a question that reads
        /// "somebody applies for access to your node", with the applicant and
        /// the node chosen by whoever sent it. Whoever then answers it grants
        /// what a stranger wrote down.
        /// </remarks>
        [Test]
        public async Task ASubscriptionRequestFromAContact_IsRefused()
        {

            var alice = await ConnectClientAsync("alice");

            PubSubSubscribeAuthorization? asked     = null;
            String?                       spoofing  = null;

            alice.Connection.OnPubSubSubscriptionRequest += (timestamp, sender, a, ct) => { asked    = a; return Task.CompletedTask; };
            alice.Connection.OnSpoofingAttempt           += (timestamp, sender, m, ct) => { spoofing = m; return Task.CompletedTask; };

            // A form built the way the service builds one - and sent by a user.
            await alice.Connection.ProcessStanzaAsync(
                $"<message from='mallory@{Server.Domain}' to='{alice.FullJid}'>" +
                  "<x xmlns='jabber:x:data' type='form'>" +
                    "<field var='FORM_TYPE' type='hidden'>" +
                      "<value>http://jabber.org/protocol/pubsub#subscribe_authorization</value></field>" +
                    $"<field var='pubsub#subscriber_jid'><value>mallory@{Server.Domain}</value></field>" +
                    "<field var='pubsub#node'><value>urn:xmpp:omemo:2:devices</value></field>" +
                    "<field var='pubsub#allow' type='boolean'><value>false</value></field>" +
                  "</x>" +
                "</message>");

            await WaitFor(() => spoofing is not null, "the forged request to be reported");

            Assert.Multiple(() =>
            {
                Assert.That(spoofing, Does.Contain("mallory"));
                Assert.That(asked,    Is.Null,
                            "A question about one's own node may only come from whoever hosts it.");
            });

        }

        #endregion

        #region ASubscriptionRequestFromAService_IsPassedOn()

        /// <summary>
        /// The counter-check: a component addresses itself without a localpart,
        /// and a user never can - the server stamps a client's full JID onto
        /// everything it sends.
        /// </summary>
        [Test]
        public async Task ASubscriptionRequestFromAService_IsPassedOn()
        {

            var alice = await ConnectClientAsync("alice");

            PubSubSubscribeAuthorization? asked = null;
            alice.Connection.OnPubSubSubscriptionRequest += (timestamp, sender, a, ct) => { asked = a; return Task.CompletedTask; };

            await alice.Connection.ProcessStanzaAsync(
                $"<message from='pubsub.{Server.Domain}' to='{alice.FullJid}'>" +
                  "<x xmlns='jabber:x:data' type='form'>" +
                    "<field var='FORM_TYPE' type='hidden'>" +
                      "<value>http://jabber.org/protocol/pubsub#subscribe_authorization</value></field>" +
                    $"<field var='pubsub#subscriber_jid'><value>bob@{Server.Domain}</value></field>" +
                    "<field var='pubsub#node'><value>news</value></field>" +
                    "<field var='pubsub#allow' type='boolean'><value>false</value></field>" +
                  "</x>" +
                "</message>");

            await WaitFor(() => asked is not null, "the request from the service");

            Assert.That(asked!.NodeId, Is.EqualTo("news"));

        }

        #endregion

    }

}
