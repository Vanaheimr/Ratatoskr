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
    /// RFC 6121, section 2.1.6: a roster push may only be applied if it carries
    /// no from attribute or if the from matches one's own bare JID. Without
    /// this check any sender can manipulate the local roster.
    /// </summary>
    [TestFixture]
    public class RosterPushSecurityTests : AXMPPTests
    {

        #region SpoofedRosterPush_IsIgnored()

        /// <summary>
        /// A push from a foreign sender must not create a contact.
        /// </summary>
        [Test]
        public async Task SpoofedRosterPush_IsIgnored()
        {

            var client = await ConnectClientAsync();
            var alerts = new List<String>();

            client.OnSpoofingAttempt += m => { lock (alerts) alerts.Add(m); };

            await Server.PushAsync(client.FullJid,
                "<iq type='set' id='spoof-1' from='evil@example.com'>" +
                "<query xmlns='jabber:iq:roster'>" +
                "<item jid='hacker@evil.com' name='Trojan' subscription='both'/>" +
                "</query></iq>");

            await Task.Delay(300);

            Assert.Multiple(() =>
            {
                Assert.That(client.Roster.GetItem("hacker@evil.com"), Is.Null,
                            "The forged contact was taken into the roster.");

                Assert.That(alerts, Has.Count.EqualTo(1),
                            "No spoofing attempt was reported.");
            });

        }

        #endregion

        #region SpoofedRosterPush_IsNotAcknowledged()

        /// <summary>
        /// A push that was discarded must not be acknowledged with an iq
        /// type='result'.
        /// </summary>
        [Test]
        public async Task SpoofedRosterPush_IsNotAcknowledged()
        {

            var client   = await ConnectClientAsync();
            var session  = Server.SessionOf(client.FullJid)!;

            await Server.PushAsync(client.FullJid,
                "<iq type='set' id='spoof-2' from='evil@example.com'>" +
                "<query xmlns='jabber:iq:roster'>" +
                "<item jid='hacker@evil.com' subscription='both'/>" +
                "</query></iq>");

            await Task.Delay(300);

            Assert.That(session.CountReceived("id='spoof-2'"), Is.Zero,
                        "The client acknowledged the forged push.");

        }

        #endregion

        #region SpoofedRemove_DoesNotDeleteContact()

        /// <summary>
        /// A forged subscription='remove' must not delete a real contact from
        /// the roster.
        /// </summary>
        [Test]
        public async Task SpoofedRemove_DoesNotDeleteContact()
        {

            var client = await ConnectClientAsync();

            // A real contact, created by a legitimate push
            await Server.PushAsync(client.FullJid,
                "<iq type='set' id='legit-1'>" +
                "<query xmlns='jabber:iq:roster'>" +
                "<item jid='friend@localhost' name='Friend' subscription='both'/>" +
                "</query></iq>");

            await WaitFor(() => client.Roster.GetItem("friend@localhost") is not null,
                          "the creation of the real contact");

            // The attack: a foreign sender wants to delete them
            await Server.PushAsync(client.FullJid,
                "<iq type='set' id='spoof-3' from='evil@example.com'>" +
                "<query xmlns='jabber:iq:roster'>" +
                "<item jid='friend@localhost' subscription='remove'/>" +
                "</query></iq>");

            await Task.Delay(300);

            Assert.That(client.Roster.GetItem("friend@localhost"), Is.Not.Null,
                        "The real contact was deleted by a forged push.");

        }

        #endregion

        #region RosterPushWithoutFrom_IsApplied()

        /// <summary>
        /// A push without a from comes implicitly from one's own account and is
        /// valid.
        /// </summary>
        [Test]
        public async Task RosterPushWithoutFrom_IsApplied()
        {

            var client = await ConnectClientAsync();

            await Server.PushAsync(client.FullJid,
                "<iq type='set' id='legit-2'>" +
                "<query xmlns='jabber:iq:roster'>" +
                "<item jid='colleague@localhost' name='Colleague' subscription='to'/>" +
                "</query></iq>");

            await WaitFor(() => client.Roster.GetItem("colleague@localhost") is not null,
                          "the taking over of the push without a from");

            Assert.That(client.Roster.GetItem("colleague@localhost")!.Name, Is.EqualTo("Colleague"));

        }

        #endregion

        #region RosterPushFromOwnBareJid_IsApplied()

        /// <summary>
        /// A push with one's own bare JID as the from is valid as well.
        /// </summary>
        [Test]
        public async Task RosterPushFromOwnBareJid_IsApplied()
        {

            var client = await ConnectClientAsync();

            await Server.PushAsync(client.FullJid,
                $"<iq type='set' id='legit-3' from='{client.BareJid}'>" +
                "<query xmlns='jabber:iq:roster'>" +
                "<item jid='boss@localhost' name='Boss' subscription='both'/>" +
                "</query></iq>");

            await WaitFor(() => client.Roster.GetItem("boss@localhost") is not null,
                          "the taking over of the push with one's own bare JID");

            Assert.Pass();

        }

        #endregion

    }

}
