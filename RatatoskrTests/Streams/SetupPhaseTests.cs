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
    /// The setup phase between the resource binding and <c>Connected</c>.
    ///
    /// There the client switches carbons on and fetches the roster. Whatever
    /// else arrives during that time - messages handed on later, presence,
    /// roster pushes - belongs delivered just as much as later on. A real
    /// server sends it as soon as the resource is bound, and does not wait
    /// until the client has finished its setup.
    /// </summary>
    [TestFixture]
    public class SetupPhaseTests : AXMPPTests
    {

        #region Helper functions

        /// <summary>
        /// Creates a client along with its account, hangs the events on and
        /// only connects afterwards - events from the setup phase would
        /// otherwise be lost before the test can see them.
        /// </summary>
        private XMPPClient PreparedClient(String localPart = "alice")
        {

            if (Server.GetAccount($"{localPart}@{Server.Domain}") is null)
                Server.AddAccount(localPart);

            return CreateClient(localPart);

        }

        #endregion


        #region MessageAfterBind_IsDelivered()

        /// <summary>
        /// The heart of it: a message that arrives between the binding and
        /// <c>Connected</c> was read off the socket by the setup phase and
        /// discarded in silence. It has to arrive.
        /// </summary>
        [Test]
        public async Task MessageAfterBind_IsDelivered()
        {

            Server.DeliverAfterBind.Add(
                "<message from='bob@localhost/desktop' to='{jid}' type='chat' id='offline-1'>" +
                "<body>Still open from yesterday</body></message>");

            var client    = PreparedClient();
            var received  = new List<XMPPMessage>();

            client.OnMessage += (timestamp, sender, m, ct) => { received.Add(m); return Task.CompletedTask; };

            await client.ConnectAsync();

            await WaitFor(() => received.Count == 1,
                          "the message handed on from the setup phase");

            Assert.That(received[0].Body, Is.EqualTo("Still open from yesterday"));

        }

        #endregion

        #region PresenceAfterBind_IsDelivered()

        /// <summary>
        /// The same for presence. Only the delivery is checked, not the roster:
        /// at that point the contact is not in it at all, because the presence
        /// runs ahead of the roster fetch.
        /// </summary>
        [Test]
        public async Task PresenceAfterBind_IsDelivered()
        {

            Server.DeliverAfterBind.Add(
                "<presence from='bob@localhost/desktop' to='{jid}'><show>dnd</show></presence>");

            var client     = PreparedClient();
            var presences  = new List<String>();

            client.OnPresenceChanged += (timestamp, sender, from, type, ct) => { presences.Add(from.ToString()); return Task.CompletedTask; };

            await client.ConnectAsync();

            await WaitFor(() => presences.Contains("bob@localhost/desktop"),
                          "the presence from the setup phase");

        }

        #endregion

        #region RosterPushAfterBind_IsApplied()

        /// <summary>
        /// A roster push straight after the binding. It carries no 'from' and
        /// is thereby authorised under RFC 6121, section 2.1.6.
        /// </summary>
        /// <remarks>
        /// The contact stands in the roster of the account as well, and since
        /// the replacing (see D8) that is necessary: the push comes in the
        /// setup phase, so <i>before</i> the roster result, and the result is
        /// the state. A server that pushes an entry it does not itself keep
        /// contradicts itself - before, that did not show, because the client
        /// merely worked the result in.
        ///
        /// What the test is meant to check is thereby still checked: that a
        /// stanza from the setup phase does not get lost. The client used to
        /// read up to ten frames off the socket itself there and discarded
        /// everything it did not expect.
        /// </remarks>
        [Test]
        public async Task RosterPushAfterBind_IsApplied()
        {

            Server.DeliverAfterBind.Add(
                "<iq type='set' id='push-early' to='{jid}'>" +
                "<query xmlns='jabber:iq:roster'>" +
                "<item jid='carol@localhost' name='Carol' subscription='both'/>" +
                "</query></iq>");

            // The client first - it creates the account whose roster is about
            // to be filled.
            var client = PreparedClient();

            Server.GetAccount($"alice@{Server.Domain}")!
                  .SetRosterEntry(new RosterEntry("carol@localhost", "Carol", "both"));

            await client.ConnectAsync();

            await WaitFor(() => client.Roster.GetItem(JID.Parse("carol@localhost")) is not null,
                          "the roster push from the setup phase");

            Assert.That(client.Roster.GetItem(JID.Parse("carol@localhost"))?.Name, Is.EqualTo("Carol"));

        }

        #endregion

        #region MessageMentioningTheRosterId_IsNotMistakenForTheAnswer()

        /// <summary>
        /// The matching ran over <c>Contains("id='roster1'")</c> - that is, over
        /// the text of the whole frame. A message carrying that character
        /// sequence in its text was thereby taken for the roster answer: the
        /// client stopped waiting, found no <c>&lt;query/&gt;</c> and was left
        /// without contacts.
        /// </summary>
        [Test]
        public async Task MessageMentioningTheRosterId_IsNotMistakenForTheAnswer()
        {

            var account = Server.AddAccount("alice");
            account.SetRosterEntry(new RosterEntry("dave@localhost", "Dave", "both"));

            // The first message gets the carbons loop to take its answer for
            // found; only through that does the roster loop get to see the
            // second one at all.
            Server.DeliverAfterBind.Add(
                "<message from='bob@localhost/desktop' to='{jid}' type='chat'>" +
                "<body>Does it say id='carbons-enable' there?</body></message>");

            Server.DeliverAfterBind.Add(
                "<message from='bob@localhost/desktop' to='{jid}' type='chat'>" +
                "<body>Look, it says id='roster1' in there</body></message>");

            var client = PreparedClient();

            await client.ConnectAsync();

            await WaitFor(() => client.Roster.GetItem(JID.Parse("dave@localhost")) is not null,
                          "the roster despite the feigned answer");

        }

        #endregion

        #region MessageMentioningTheCarbonsId_IsNotMistakenForTheAnswer()

        /// <summary>
        /// The same text matching with XEP-0280: a message carrying
        /// <c>id='carbons-enable'</c> in its text counted as the answer.
        /// Because it carries no <c>type='result'</c>, the client afterwards
        /// held carbons to be unavailable - although the server confirmed them
        /// right after.
        /// </summary>
        [Test]
        public async Task MessageMentioningTheCarbonsId_IsNotMistakenForTheAnswer()
        {

            Server.DeliverAfterBind.Add(
                "<message from='bob@localhost/desktop' to='{jid}' type='chat'>" +
                "<body>Does it really say id='carbons-enable'?</body></message>");

            var client = PreparedClient();

            await client.ConnectAsync();

            Assert.That(client.CarbonsEnabled, Is.True,
                        "Carbons should have been active after the server's confirmation.");

        }

        #endregion

        #region RejectedBind_IsNotReportedAsSuccess()

        /// <summary>
        /// The bound JID was sought with
        /// <c>&lt;jid&gt;([^&lt;]+)&lt;/jid&gt;</c>; if the search came up
        /// empty, the client silently took the JID it had wished for itself. A
        /// refused binding thereby looked like a successful one, and the client
        /// reported itself online with a JID it was never assigned.
        /// </summary>
        [Test]
        public async Task RejectedBind_IsNotReportedAsSuccess()
        {

            // FailBind refuses the RFC 6120 <iq/>, and there is no <iq/> on the
            // inline path to refuse - XEP-0386 binds inside the <success/> or
            // not at all. The defect this test was written for lives in reading
            // that <iq/> result, so it stays on the route where the result
            // exists.
            Server.OfferBind2 = false;
            Server.FailBind   = true;

            var client  = PreparedClient();
            var errors  = new List<String>();

            // A refused binding otherwise sends the client through twenty
            // reconnects with exponential backoff - the test run hung a good six
            // minutes on this one question, and the runner broke it off when the
            // test ran on its own. Getting to the same result over a reconnect
            // would be no answer either, only a slow repetition of the same
            // question.
            client.Connection.MaxReconnectAttempts = 0;

            client.OnError += (timestamp, sender, e, ct) => { errors.Add(e); return Task.CompletedTask; };

            await FailingConnectAsync(client);

            Assert.Multiple(() =>
            {
                Assert.That(client.IsConnected, Is.False,
                            "After a refused binding the client must not count as connected.");
                Assert.That(errors, Is.Not.Empty,
                            "A refused binding has to be reported.");
            });

        }

        #endregion

        #region RequiredSession_IsRequested()

        /// <summary>
        /// The legacy session (RFC 3921) was skipped as soon as the word
        /// "optional" occurred anywhere in the stream features. But XEP-0198
        /// puts exactly that element into its own feature
        /// (<c>&lt;sm&gt;&lt;optional/&gt;&lt;/sm&gt;</c>) - a server that
        /// announces both never got the required session asked for.
        /// </summary>
        [Test]
        public async Task RequiredSession_IsRequested()
        {

            Server.SessionRequired = true;

            var client = PreparedClient();

            await client.ConnectAsync();

            await WaitFor(() => Server.AllReceived.Any(f => f.Contains("urn:ietf:params:xml:ns:xmpp-session",
                                                                       StringComparison.Ordinal)),
                          "the legacy session that was asked for");

        }

        #endregion

        #region OptionalSession_IsNotRequested()

        /// <summary>
        /// The counter-check: if the session is announced as
        /// <c>&lt;optional/&gt;</c>, it is not asked for.
        /// </summary>
        [Test]
        public async Task OptionalSession_IsNotRequested()
        {

            var client = PreparedClient();

            await client.ConnectAsync();

            Assert.That(Server.AllReceived.Any(f => f.Contains("urn:ietf:params:xml:ns:xmpp-session",
                                                               StringComparison.Ordinal)),
                        Is.False,
                        "A session announced as optional does not belong asked for.");

        }

        #endregion

    }

}
