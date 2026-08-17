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
    /// What happens to a stanza that goes to a foreign domain.
    ///
    /// Up to now: nothing. The server looked for a session to the recipient,
    /// found none, and dropped the stanza. To a sender that looks like a
    /// delivered message - they never learn that it arrived nowhere.
    ///
    /// RFC 6120, section 10.4.3 demands a stanza error in this case. The
    /// condition <c>&lt;remote-server-not-found/&gt;</c> stands in section
    /// 8.3.3.
    /// </summary>
    [TestFixture]
    public class DomainRoutingTests : AXMPPTests
    {

        #region MessageToForeignDomain_IsAnsweredWithAnError()

        /// <summary>
        /// The heart of it: a message to an unreachable domain comes back as an
        /// error instead of disappearing without a trace.
        /// </summary>
        [Test]
        public async Task MessageToForeignDomain_IsAnsweredWithAnError()
        {

            var client  = await ConnectClientAsync();
            var errors  = new List<(JID? From, StanzaError Error)>();

            client.OnStanzaError += (timestamp, sender, from, error, ct) => { errors.Add((from, error)); return Task.CompletedTask; };

            await client.SendMessageAsync(JID.Parse("bob@elsewhere.example"), "Hello?");

            await WaitFor(() => errors.Count > 0, "the error report about the foreign domain");

            Assert.Multiple(() =>
            {
                Assert.That(errors[0].Error.Condition, Is.EqualTo("remote-server-not-found"));
                Assert.That(errors[0].From.ToString(), Is.EqualTo("bob@elsewhere.example"),
                            "The error has to appear to come from the original recipient.");
            });

        }

        #endregion

        #region IqToForeignDomain_IsAnsweredWithAnError()

        /// <summary>
        /// The same for an <c>iq</c> - there it weighs more, because the sender
        /// waits for an answer under RFC 6120, section 8.2.3.
        /// </summary>
        [Test]
        public async Task IqToForeignDomain_IsAnsweredWithAnError()
        {

            var client   = await ConnectClientAsync();
            var session  = Server.SessionOf(client.FullJid.ToString())!;

            await client.SendRawAsync(
                "<iq type='get' id='foreign-1' to='bob@elsewhere.example'>" +
                "<query xmlns='http://jabber.org/protocol/disco#info'/></iq>");

            await WaitFor(() => session.Sent.Any(f => f.Contains("id='foreign-1'", StringComparison.Ordinal)),
                          "the answer to the iq to the foreign domain");

            var reply = session.Sent.First(f => f.Contains("id='foreign-1'", StringComparison.Ordinal));

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='error'"));
                Assert.That(reply, Does.Contain("remote-server-not-found"));
            });

        }

        #endregion

        #region ErrorStanza_IsNotAnsweredAgain()

        /// <summary>
        /// An error stanza to a foreign domain must not trigger a further
        /// error.
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 8.3.1: an error is never followed by an error. If
        /// it were, two servers could push error reports back and forth at each
        /// other until one gives up.
        /// </remarks>
        [Test]
        public async Task ErrorStanza_IsNotAnsweredAgain()
        {

            var client   = await ConnectClientAsync();
            var session  = Server.SessionOf(client.FullJid.ToString())!;

            var before = session.Sent.Count;

            await client.SendRawAsync(
                "<message type='error' to='bob@elsewhere.example' id='already-an-error'>" +
                "<error type='cancel'><service-unavailable xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/></error>" +
                "</message>");

            await WaitAgainst(() => session.Sent.Skip(before).Any(f => f.Contains("already-an-error", StringComparison.Ordinal)),
                              "an answer to an error stanza");

        }

        #endregion

        #region LocalDelivery_IsUnaffected()

        /// <summary>
        /// The counter-check: to one's own domain delivery goes on as before
        /// and no error is produced.
        /// </summary>
        [Test]
        public async Task LocalDelivery_IsUnaffected()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var received  = new List<String>();
            var errors     = new List<StanzaError>();

            bob.OnMessage       += (timestamp, sender, m, ct) => { received.Add(m.Body); return Task.CompletedTask; };
            alice.OnStanzaError += (timestamp, sender, _, e, ct) => { errors.Add(e); return Task.CompletedTask; };

            await alice.SendMessageAsync(bob.BareJid, "Hello Bob!");

            await WaitFor(() => received.Count > 0, "the message delivered locally");

            Assert.That(errors, Is.Empty, "A local delivery must not produce an error.");

        }

        #endregion

        #region UnknownLocalAccount_IsStillDroppedSilently()

        /// <summary>
        /// An unknown account on one's <b>own</b> domain stays unanswered -
        /// that is another question than an unreachable domain and is not
        /// changed along here.
        /// </summary>
        /// <remarks>
        /// RFC 6121, section 8.1 demanded a <c>&lt;service-unavailable/&gt;</c>
        /// here. The test records today's state, so that the domain switch does
        /// not shift it along unnoticed; the gap itself stands in the work
        /// plan.
        /// </remarks>
        [Test]
        public async Task UnknownLocalAccount_IsStillDroppedSilently()
        {

            var client  = await ConnectClientAsync();
            var errors  = new List<StanzaError>();

            client.OnStanzaError += (timestamp, sender, _, e, ct) => { errors.Add(e); return Task.CompletedTask; };

            await client.SendMessageAsync(JID.Parse($"nobody@{Server.Domain}"), "Hello?");

            await WaitAgainst(() => errors.Count > 0, "an error for a local account");

        }

        #endregion

    }

}
