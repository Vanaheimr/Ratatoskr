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
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// RFC 6121, section 3 from the client's side: <c>subscribed</c>,
    /// <c>unsubscribed</c> and <c>unsubscribe</c> are changes of state, not
    /// presence announcements.
    ///
    /// They used to run through <c>UpdatePresence</c>. Because everything
    /// without a <c>type='unavailable'</c> counts as present there, of all
    /// things the message "you may not see me any more" made the contact
    /// online.
    /// </summary>
    [TestFixture]
    public class SubscriptionStateTests : AXMPPTests
    {

        #region Helper functions

        private String Bob => $"bob@{Server.Domain}";

        private async Task<(XMPPClient Client, XMPPSession Session)> ConnectedPairAsync()
        {

            var client = await ConnectClientAsync();

            await WaitFor(() => Server.SessionOf(client.FullJid.ToString()) is not null, "the server session for the client");

            return (client, Server.SessionOf(client.FullJid.ToString())!);

        }

        /// <summary>
        /// Puts Bob into the client roster by roster push with a particular
        /// subscription state - without going through the handshake, so that
        /// the test checks exactly one step.
        /// </summary>
        private async Task SeedContactAsync(XMPPClient          client,
                                            XMPPSession         session,
                                            String              subscription,
                                            SubscriptionState   expected)
        {

            await session.SendAsync(
                $"<iq type='set' id='seed-{subscription}'><query xmlns='jabber:iq:roster'>" +
                $"<item jid='{Bob}' name='Bob' subscription='{subscription}'/></query></iq>");

            await WaitFor(() => client.GetContact(JID.Parse(Bob))?.Subscription == expected,
                          $"a contact with subscription='{subscription}'");

        }

        #endregion


        #region Subscribed_DoesNotMarkTheContactOnline()

        /// <summary>
        /// The heart of it: a grant says nothing about whether the contact is
        /// there right now. Whether they are online the client learns from
        /// their presence - which does come promptly with a freshly granted
        /// subscription, but as a stanza of its own.
        /// </summary>
        [Test]
        public async Task Subscribed_DoesNotMarkTheContactOnline()
        {

            var (client, session) = await ConnectedPairAsync();
            await SeedContactAsync(client, session, "none", SubscriptionState.None);

            await session.SendAsync($"<presence from='{Bob}' to='{client.FullJid}' type='subscribed'/>");

            await WaitFor(() => client.GetContact(JID.Parse(Bob))?.Subscription == SubscriptionState.To,
                          "the grant taken over");

            Assert.That(client.GetContact(JID.Parse(Bob))!.Presence, Is.EqualTo(PresenceState.Offline),
                        "A grant is no presence announcement.");

        }

        #endregion

        #region Unsubscribed_DoesNotMarkTheContactOnline()

        /// <summary>
        /// Even plainer with the withdrawal: "you may not see me any more" set
        /// the contact to online.
        /// </summary>
        [Test]
        public async Task Unsubscribed_DoesNotMarkTheContactOnline()
        {

            var (client, session) = await ConnectedPairAsync();
            await SeedContactAsync(client, session, "to", SubscriptionState.To);

            await session.SendAsync($"<presence from='{Bob}' to='{client.FullJid}' type='unsubscribed'/>");

            await WaitFor(() => client.GetContact(JID.Parse(Bob))?.Subscription == SubscriptionState.None,
                          "the subscription withdrawn");

            Assert.That(client.GetContact(JID.Parse(Bob))!.Presence, Is.EqualTo(PresenceState.Offline),
                        "A withdrawal is no presence announcement.");

        }

        #endregion

        #region Unsubscribe_DoesNotMarkTheContactOnline()

        /// <summary>
        /// And with the cancelling of the other direction just the same.
        /// </summary>
        [Test]
        public async Task Unsubscribe_DoesNotMarkTheContactOnline()
        {

            var (client, session) = await ConnectedPairAsync();
            await SeedContactAsync(client, session, "from", SubscriptionState.From);

            await session.SendAsync($"<presence from='{Bob}' to='{client.FullJid}' type='unsubscribe'/>");

            await WaitFor(() => client.GetContact(JID.Parse(Bob))?.Subscription == SubscriptionState.None,
                          "the other direction cancelled");

            Assert.That(client.GetContact(JID.Parse(Bob))!.Presence, Is.EqualTo(PresenceState.Offline),
                        "A cancellation is no presence announcement.");

        }

        #endregion

        #region Unsubscribed_KeepsTheOtherDirection()

        /// <summary>
        /// With <c>Both</c> the withdrawal may take only its own half: Bob goes
        /// on seeing us, we no longer see him.
        /// </summary>
        [Test]
        public async Task Unsubscribed_KeepsTheOtherDirection()
        {

            var (client, session) = await ConnectedPairAsync();
            await SeedContactAsync(client, session, "both", SubscriptionState.Both);

            await session.SendAsync($"<presence from='{Bob}' to='{client.FullJid}' type='unsubscribed'/>");

            await WaitFor(() => client.GetContact(JID.Parse(Bob))?.Subscription == SubscriptionState.From,
                          "the remaining other direction");

        }

        #endregion

        #region Unsubscribe_KeepsTheOtherDirection()

        /// <summary>
        /// The same the other way round.
        /// </summary>
        [Test]
        public async Task Unsubscribe_KeepsTheOtherDirection()
        {

            var (client, session) = await ConnectedPairAsync();
            await SeedContactAsync(client, session, "both", SubscriptionState.Both);

            await session.SendAsync($"<presence from='{Bob}' to='{client.FullJid}' type='unsubscribe'/>");

            await WaitFor(() => client.GetContact(JID.Parse(Bob))?.Subscription == SubscriptionState.To,
                          "the remaining own direction");

        }

        #endregion

        #region Unsubscribed_ClearsAStalePresence()

        /// <summary>
        /// Without <c>To</c> no presence announcements come any more. What the
        /// client saw last would from now on be a frozen state that can grow
        /// arbitrarily old - so the contact counts as offline.
        ///
        /// The test server does send an <c>unavailable</c> along with the
        /// withdrawal (RFC 6121, section 3.2.2); here the withdrawal comes
        /// deliberately without one, so that the client is checked on its own.
        /// </summary>
        [Test]
        public async Task Unsubscribed_ClearsAStalePresence()
        {

            var (client, session) = await ConnectedPairAsync();
            await SeedContactAsync(client, session, "both", SubscriptionState.Both);

            await session.SendAsync($"<presence from='{Bob}/x' to='{client.FullJid}'><show>dnd</show></presence>");
            await WaitFor(() => client.GetContact(JID.Parse(Bob))?.Presence == PresenceState.Dnd, "a visible state");

            await session.SendAsync($"<presence from='{Bob}' to='{client.FullJid}' type='unsubscribed'/>");

            await WaitFor(() => client.GetContact(JID.Parse(Bob))?.Presence == PresenceState.Offline,
                          "the state discarded after the withdrawal");

        }

        #endregion

        #region Subscribe_StillRaisesTheRequest()

        /// <summary>
        /// Counter-check: <c>subscribe</c> is still a contact request and no
        /// change of state.
        /// </summary>
        [Test]
        public async Task Subscribe_StillRaisesTheRequest()
        {

            var (client, session) = await ConnectedPairAsync();

            JID? requested = null;
            client.OnSubscriptionRequest += (timestamp, sender, from, _, ct) => { requested = from; return Task.CompletedTask; };

            await session.SendAsync($"<presence from='{Bob}' to='{client.FullJid}' type='subscribe'/>");

            await WaitFor(() => requested is not null, "the contact request reported");

            Assert.That(requested.ToString(), Is.EqualTo(Bob));

        }

        #endregion

        #region GrantAndRevoke_ChangeOnlyTheirOwnHalf()

        /// <summary>
        /// The four transitions one by one, without a server. <c>To</c> and
        /// <c>From</c> are separate halves: out of <c>Both</c> a withdrawal
        /// makes the respective other one, not <c>None</c>.
        /// </summary>
        [TestCase(SubscriptionState.None, SubscriptionState.To,   SubscriptionState.None, SubscriptionState.From, SubscriptionState.None)]
        [TestCase(SubscriptionState.To,   SubscriptionState.To,   SubscriptionState.None, SubscriptionState.Both, SubscriptionState.To)]
        [TestCase(SubscriptionState.From, SubscriptionState.Both, SubscriptionState.From, SubscriptionState.From, SubscriptionState.None)]
        [TestCase(SubscriptionState.Both, SubscriptionState.Both, SubscriptionState.From, SubscriptionState.Both, SubscriptionState.To)]
        public void GrantAndRevoke_ChangeOnlyTheirOwnHalf(SubscriptionState  start,
                                                          SubscriptionState  afterGrantTo,
                                                          SubscriptionState  afterRevokeTo,
                                                          SubscriptionState  afterGrantFrom,
                                                          SubscriptionState  afterRevokeFrom)
        {

            Assert.Multiple(() =>
            {
                Assert.That(start.GrantTo(),     Is.EqualTo(afterGrantTo),     "GrantTo");
                Assert.That(start.RevokeTo(),    Is.EqualTo(afterRevokeTo),    "RevokeTo");
                Assert.That(start.GrantFrom(),   Is.EqualTo(afterGrantFrom),   "GrantFrom");
                Assert.That(start.RevokeFrom(),  Is.EqualTo(afterRevokeFrom),  "RevokeFrom");
            });

        }

        #endregion

    }

}
