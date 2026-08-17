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
    /// RFC 6120, section 8.3.3.8: if no JID stands in the <c>to</c>, the server
    /// answers with <c>&lt;jid-malformed/&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The check itself has existed in full since D42 through D45 - RFC 7622
    /// with PRECIS, IDNA2008, the Bidi rule and the context-dependent rules from
    /// appendix A. <b>The server never asked it.</b> What arrived went into the
    /// delivery, and an impossible recipient looked there like an absent one:
    /// the sender got silence or a storage nobody ever fetches for them.
    ///
    /// A checked rule without a caller is not half a rule but none. The same gap
    /// stood in D43 (the IDNA check was finished and not wired into the JID) and
    /// in D45 - which is why every test here checks the route over the wire and
    /// not the function.
    /// </remarks>
    [TestFixture]
    public class MalformedRecipientTests : AXMPPTests
    {

        #region Helper functions

        /// <summary>
        /// Sends a stanza and gives back the answer the server sent to it.
        /// </summary>
        private async Task<String> AnswerToAsync(XMPPClient client, String stanza)
        {

            var session = Server.SessionOf(client.FullJid)!;
            var before  = session.Sent.Count;

            await client.SendRawAsync(stanza);

            await WaitFor(() => SentSince(session, before).Any(f => f.Contains("type='error'", StringComparison.Ordinal)),
                          $"the refusal by the server of: {stanza}");

            return SentSince(session, before).First(f => f.Contains("type='error'", StringComparison.Ordinal));

        }

        /// <summary>What the server has sent since this mark.</summary>
        private static IEnumerable<String> SentSince(XMPPSession session, Int32 mark)
            => session.Sent.Skip(mark);

        #endregion


        #region AMessageToANonJid_IsRefused(...)

        /// <summary>
        /// Five addresses that are none - and each for a different reason.
        /// </summary>
        /// <remarks>
        /// A single impossible address would leave open how far the check
        /// reaches: <c>alice@</c> is already noticed by a comparison against two
        /// empty strings, <c>alice@-localhost</c> only by the label rule from
        /// RFC 5891, and the space in the local part only by the PRECIS
        /// IdentifierClass. Five reasons, so that a test cannot pass by covering
        /// the simplest of them.
        /// </remarks>
        [TestCase("@localhost",         TestName = "Without a local part")]
        [TestCase("alice@",             TestName = "Without a domain part")]
        [TestCase("alice@localhost/",   TestName = "With an empty resource")]
        [TestCase("al ice@localhost",   TestName = "With a space in the local part")]
        [TestCase("alice@-localhost",   TestName = "With a hyphen at the start of a label")]
        public async Task AMessageToANonJid_IsRefused(String recipient)
        {

            var alice = await ConnectClientAsync();

            var response = await AnswerToAsync(
                              alice,
                              $"<message to='{recipient}' type='chat'><body>Hello</body></message>");

            Assert.Multiple(() =>
            {

                // A message is answered by a message. Between the element name
                // and the type stands the namespace: every stanza to a client
                // carries jabber:client.
                Assert.That(response, Does.StartWith("<message"));
                Assert.That(response, Does.Contain("type='error'"));

                Assert.That(response, Does.Contain("jid-malformed"));

                Assert.That(response, Does.Contain("type='modify'"),
                            "RFC 6120, section 8.3.3.8: the error type is 'modify'.");

                // Not the intended recipient, as with service-unavailable: there
                // the server answered for somebody, here for nobody - the
                // address is none.
                Assert.That(response, Does.Contain($"from='{Server.Domain}'"));

            });

        }

        #endregion

        #region AnIqToANonJid_KeepsItsId()

        /// <summary>
        /// The refusal of a request carries its <c>id</c> - otherwise an asker
        /// with several pending requests knows only that one has failed.
        /// </summary>
        [Test]
        public async Task AnIqToANonJid_KeepsItsId()
        {

            var alice = await ConnectClientAsync();

            var response = await AnswerToAsync(
                              alice,
                              "<iq type='get' id='question-1' to='alice@@localhost'>" +
                              "<query xmlns='jabber:iq:version'/></iq>");

            Assert.Multiple(() =>
            {
                Assert.That(response, Does.StartWith("<iq"));
                Assert.That(response, Does.Contain("type='error'"));
                Assert.That(response, Does.Contain("id='question-1'"));
                Assert.That(response, Does.Contain("jid-malformed"));
            });

        }

        #endregion

        #region APresenceToANonJid_IsRefusedAsWell()

        /// <summary>
        /// Directed presence too, and with the same element.
        /// </summary>
        /// <remarks>
        /// Undirected presence carries no <c>to</c> and must not be hit by this
        /// - every other test of the collection checks that along, for without
        /// it no session counts as available.
        /// </remarks>
        [Test]
        public async Task APresenceToANonJid_IsRefusedAsWell()
        {

            var alice = await ConnectClientAsync();

            var response = await AnswerToAsync(alice, "<presence to='alice@localhost/'/>");

            Assert.Multiple(() =>
            {
                Assert.That(response, Does.StartWith("<presence"));
                Assert.That(response, Does.Contain("type='error'"));
                Assert.That(response, Does.Contain("jid-malformed"));
            });

        }

        #endregion

        #region AnErrorStanza_IsNotAnsweredWithAnError()

        /// <summary>
        /// An error stanza is not followed by an error - it is discarded all the
        /// same.
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 8.3.1. Without this exception two servers could
        /// push notifications back and forth at each other until one gives up:
        /// the one answers the impossible address, the other answers the answer.
        /// </remarks>
        [Test]
        public async Task AnErrorStanza_IsNotAnsweredWithAnError()
        {

            var alice   = await ConnectClientAsync();
            var session = Server.SessionOf(alice.FullJid)!;
            var before  = session.Sent.Count;

            await alice.SendRawAsync(
                      "<message to='@localhost' type='error'>" +
                      "<error type='cancel'><gone xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/></error>" +
                      "</message>");

            await WaitAgainst(() => SentSince(session, before).Any(f => f.Contains("jid-malformed", StringComparison.Ordinal)),
                              "an answer to an error stanza");

        }

        #endregion

        #region ARefusedStanza_IsNotDeliveredAnyway()

        /// <summary>
        /// The refused stanza really does end - it is not delivered in addition.
        /// </summary>
        /// <remarks>
        /// The address is chosen on purpose so that a passing-on would show:
        /// <c>bob@…/</c> is no JID (an empty resource does not exist), but the
        /// part before it belongs to a signed-on account. A check that answers
        /// and then delivers all the same would arrive at Bob by the route for
        /// bare JIDs - and without this test it would not be distinguishable
        /// from the right one.
        /// </remarks>
        [Test]
        public async Task ARefusedStanza_IsNotDeliveredAnyway()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync();
            var bob   = await ConnectClientAsync("bob", createAccount: false);

            var arrived = new List<String>();
            bob.OnMessage += (timestamp, sender, m, ct) => { lock (arrived) arrived.Add(m.Body);  return Task.CompletedTask; };

            await alice.SendRawAsync(
                      $"<message to='bob@{Server.Domain}/' type='chat'><body>All the same</body></message>");

            await WaitAgainst(() => { lock (arrived) return arrived.Contains("All the same"); },
                              "the delivery of a refused message");

        }

        #endregion

        #region AnUnusualButValidJid_IsDelivered()

        /// <summary>
        /// A JID that looks unusual and is one all the same comes through.
        /// </summary>
        /// <remarks>
        /// The counter-check, without which "refuse everything" would be a
        /// passing solution. It checks at the same time that RFC 7622 really
        /// works here and not a handful of special characters: the local part
        /// carries umlauts, the resource a space - in the local part it would be
        /// forbidden (IdentifierClass), in the resource permitted
        /// (FreeformClass).
        /// </remarks>
        [Test]
        public async Task AnUnusualButValidJid_IsDelivered()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync();
            var bob   = await ConnectClientAsync("bob", createAccount: false);

            Server.AddAccount("bäcker");

            var arrived = new List<String>();
            bob.OnMessage += (timestamp, sender, m, ct) => { lock (arrived) arrived.Add(m.Body);  return Task.CompletedTask; };

            var session = Server.SessionOf(alice.FullJid)!;
            var before  = session.Sent.Count;

            // First the unusual address: it must not count as impossible. It is
            // delivered to nobody - nobody sits there -, and the answer to it is
            // a different error from jid-malformed.
            await alice.SendRawAsync(
                      $"<message to='bäcker@{Server.Domain}/Büro 1' type='chat'><body>Rolls</body></message>");

            // And then an ordinary one, which has to arrive.
            await alice.SendMessageAsync($"bob@{Server.Domain}", "And coffee");

            await WaitFor(() => { lock (arrived) return arrived.Contains("And coffee"); },
                          "the ordinary message");

            Assert.That(SentSince(session, before).Any(f => f.Contains("jid-malformed", StringComparison.Ordinal)),
                        Is.False,
                        "A valid JID was refused as impossible.");

        }

        #endregion

    }

}
