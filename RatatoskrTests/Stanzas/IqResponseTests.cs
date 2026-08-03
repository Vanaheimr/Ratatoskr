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

using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// RFC 6120, section 8.2.3: an <c>iq</c> of type <c>get</c> or <c>set</c>
    /// MUST be followed by a response - <c>result</c> or <c>error</c>.
    ///
    /// If it stays away, the counterpart waits into its timeout. A server reads
    /// that, depending on the implementation, as a dead session.
    /// </summary>
    [TestFixture]
    public class IqResponseTests : AXMPPTests
    {

        #region Helper functions

        /// <summary>
        /// Sends a stanza to the client and waits for a reply carrying the same
        /// id.
        /// </summary>
        private async Task<String> AskAsync(XMPPSession session, String id, String stanza)
        {

            await session.SendAsync(stanza);

            await WaitFor(() => session.Received.Any(f => f.Contains($"id='{id}'", StringComparison.Ordinal)),
                          $"the reply to IQ '{id}'");

            return session.Received.First(f => f.Contains($"id='{id}'", StringComparison.Ordinal));

        }

        /// <summary>
        /// Checks that no reply with this id arrives within the waiting time.
        /// </summary>
        private async Task AssertNoAnswerAsync(XMPPSession session, String id)
        {

            var answered = await XMPPServer.WaitUntilAsync(
                               () => session.Received.Any(f => f.Contains($"id='{id}'", StringComparison.Ordinal)),
                               TimeSpan.FromSeconds(1));

            Assert.That(answered, Is.False,
                        $"'{id}' should not have been answered at all.");

        }

        private async Task<XMPPSession> ConnectedSessionAsync()
        {

            await ConnectClientAsync();

            await WaitFor(() => Server.Sessions.Any(s => s.FullJid is not null),
                          "the bound session");

            return Server.Sessions.First(s => s.FullJid is not null);

        }

        #endregion


        #region UnknownIqGet_IsAnsweredWithServiceUnavailable()

        /// <summary>
        /// The heart of it: an <c>iq get</c> for which there is no handler used
        /// to be discarded in silence. Now an error has to come back.
        /// </summary>
        [Test]
        public async Task UnknownIqGet_IsAnsweredWithServiceUnavailable()
        {

            var session = await ConnectedSessionAsync();

            var reply = await AskAsync(session, "probe-get",
                            $"<iq type='get' id='probe-get' from='{Server.Domain}' to='{session.FullJid}'>" +
                            "<query xmlns='urn:example:does-not-exist'/></iq>");

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='error'"));
                Assert.That(reply, Does.Contain("<service-unavailable"));
                Assert.That(reply, Does.Contain("urn:ietf:params:xml:ns:xmpp-stanzas"));
                Assert.That(reply, Does.Contain("type='cancel'"));
                Assert.That(reply, Does.Contain($"to='{Server.Domain}'"));
            });

        }

        #endregion

        #region UnknownIqSet_IsAnsweredWithServiceUnavailable()

        /// <summary>
        /// The same for <c>set</c>.
        /// </summary>
        [Test]
        public async Task UnknownIqSet_IsAnsweredWithServiceUnavailable()
        {

            var session = await ConnectedSessionAsync();

            var reply = await AskAsync(session, "probe-set",
                            $"<iq type='set' id='probe-set' from='{Server.Domain}' to='{session.FullJid}'>" +
                            "<command xmlns='urn:example:does-not-exist'/></iq>");

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='error'"));
                Assert.That(reply, Does.Contain("<service-unavailable"));
            });

        }

        #endregion

        #region UnknownIqWithoutFrom_IsAnsweredWithoutToAttribute()

        /// <summary>
        /// Without a 'from' the request came from one's own server (RFC 6120,
        /// section 8.1.1.1). The reply has to come all the same, and without a
        /// 'to' - it is then delivered to the server implicitly.
        /// </summary>
        [Test]
        public async Task UnknownIqWithoutFrom_IsAnsweredWithoutToAttribute()
        {

            var session = await ConnectedSessionAsync();

            var reply = await AskAsync(session, "probe-nofrom",
                            "<iq type='get' id='probe-nofrom'>" +
                            "<query xmlns='urn:example:does-not-exist'/></iq>");

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='error'"));
                Assert.That(reply, Does.Not.Contain(" to='"),
                            "Without a 'from' the reply must not carry a 'to'.");
            });

        }

        #endregion

        #region IqResult_IsNotAnswered()

        /// <summary>
        /// A <c>result</c> and an <c>error</c> must never be answered -
        /// otherwise two counterparts wind each other up without end.
        /// </summary>
        [Test]
        [TestCase("result", TestName = "IqResult_IsNotAnswered")]
        [TestCase("error",  TestName = "IqError_IsNotAnswered")]
        public async Task IqResponse_IsNotAnswered(String type)
        {

            var session = await ConnectedSessionAsync();

            await session.SendAsync(
                $"<iq type='{type}' id='probe-{type}' from='{Server.Domain}' to='{session.FullJid}'/>");

            await AssertNoAnswerAsync(session, $"probe-{type}");

        }

        #endregion

        #region IqWithoutId_IsNotAnswered()

        /// <summary>
        /// Without an 'id' the reply could not be assigned to anything; the
        /// attribute is required by section 8.2.3. Rather than produce an
        /// unassignable reply, the client stays silent.
        /// </summary>
        [Test]
        public async Task IqWithoutId_IsNotAnswered()
        {

            var session = await ConnectedSessionAsync();

            // The IQ this is about: without an 'id' and with a namespace for
            // which there is no handler. It could only be answered with a
            // <service-unavailable/> - and that is exactly what the missing
            // attribute forbids.
            await session.SendAsync(
                $"<iq type='get' from='{Server.Domain}' to='{session.FullJid}'>" +
                "<query xmlns='urn:example:does-not-exist'/></iq>");

            // And right after it one that *must* be answered.
            //
            // That is the heart of the matter: on one stream things are worked
            // through in order. Once the reply to the second is there, the
            // client has already held the first in its hands and made up its
            // mind. So this test no longer needs a waiting time during which
            // nothing may happen.
            var reply = await AskAsync(session, "probe-after",
                              "<iq type='get' id='probe-after' " +
                              $"from='{Server.Domain}' to='{session.FullJid}'>" +
                              "<query xmlns='http://jabber.org/protocol/disco#info'/></iq>");

            Assert.Multiple(() =>
            {

                Assert.That(reply, Does.Contain("type='result'"),
                            "Precondition: the second IQ must have been answered.");

                // A <service-unavailable/> could only have come from the first -
                // the second is answerable and answered.
                Assert.That(session.Received.Any(f => f.Contains("type='error'", StringComparison.Ordinal)),
                            Is.False,
                            "An IQ without an 'id' is not answerable and must not trigger a reply.");

            });

        }

        #endregion

        #region KnownIqGet_IsAnsweredByItsHandlerNotWithAnError()

        /// <summary>
        /// The fallback must not get ahead of the real handlers: a ping has to
        /// keep getting a <c>result</c> and not an error.
        /// </summary>
        [Test]
        public async Task KnownIqGet_IsAnsweredByItsHandlerNotWithAnError()
        {

            var session = await ConnectedSessionAsync();

            var reply = await AskAsync(session, "probe-ping",
                            $"<iq type='get' id='probe-ping' from='{Server.Domain}' to='{session.FullJid}'>" +
                            "<ping xmlns='urn:xmpp:ping'/></iq>");

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='result'"));
                Assert.That(reply, Does.Not.Contain("service-unavailable"));
            });

        }

        #endregion

        #region DiscoInfoRequest_IsAnsweredWithFeatures()

        /// <summary>
        /// disco#info too must be answered by its own handler.
        /// </summary>
        [Test]
        public async Task DiscoInfoRequest_IsAnsweredWithFeatures()
        {

            var session = await ConnectedSessionAsync();

            var reply = await AskAsync(session, "probe-disco",
                            $"<iq type='get' id='probe-disco' from='{Server.Domain}' to='{session.FullJid}'>" +
                            "<query xmlns='http://jabber.org/protocol/disco#info'/></iq>");

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='result'"));
                Assert.That(reply, Does.Contain("<identity"));
                Assert.That(reply, Does.Not.Contain("service-unavailable"));
            });

        }

        #endregion

        #region SpoofedRosterPush_IsNotAnswered()

        /// <summary>
        /// The one permitted exception to section 8.2.3: RFC 6121 section 2.1.6
        /// expressly allows a roster push from an unauthorised sender not to be
        /// answered at all - a reply would confirm that the account is online.
        ///
        /// So the fallback must not take hold here.
        /// </summary>
        [Test]
        public async Task SpoofedRosterPush_IsNotAnswered()
        {

            var session = await ConnectedSessionAsync();

            await session.SendAsync(
                $"<iq type='set' id='probe-spoof' from='mallory@evil.example' to='{session.FullJid}'>" +
                "<query xmlns='jabber:iq:roster'>" +
                "<item jid='mallory@evil.example' subscription='both'/></query></iq>");

            await AssertNoAnswerAsync(session, "probe-spoof");

        }

        #endregion

        #region LegitimateRosterPush_IsAnsweredWithResult()

        /// <summary>
        /// A roster push from one's own account, by contrast, is acknowledged
        /// normally.
        /// </summary>
        [Test]
        public async Task LegitimateRosterPush_IsAnsweredWithResult()
        {

            var session = await ConnectedSessionAsync();

            var reply = await AskAsync(session, "probe-roster",
                            $"<iq type='set' id='probe-roster' to='{session.FullJid}'>" +
                            "<query xmlns='jabber:iq:roster'>" +
                            $"<item jid='carol@{Server.Domain}' subscription='both'/></query></iq>");

            Assert.That(reply, Does.Contain("type='result'"));

        }

        #endregion

        #region AnIqWithAnUnknownType_IsRefusedWithBadRequest()

        /// <summary>
        /// RFC 6120, section 8.2.3, rule 2: a <c>type</c> that is none of the
        /// four intended values gets <c>&lt;bad-request/&gt;</c> — from "the
        /// recipient", and that is the client here.
        /// </summary>
        /// <remarks>
        /// Not the same check as in the server, but the second role of the same
        /// rule. The server refuses what it is meant to pass on; the client
        /// refuses what arrives at its end. Both are needed: against this server
        /// such a stanza would never reach the client, against a foreign
        /// implementation without rule 2 it very much would.
        ///
        /// Before, it fell through here in silence: the fallback at the end of
        /// <c>ProcessIq</c> asks for <c>get</c> or <c>set</c>, and a fifth value
        /// is neither.
        /// </remarks>
        [Test]
        [TestCase("maybe", TestName = "AnIqWithAnUnknownType_IsRefusedWithBadRequest")]
        [TestCase(null,    TestName = "AnIqWithoutAType_IsRefusedWithBadRequest")]
        public async Task AnIqWithABrokenType_IsRefusedWithBadRequest(String? type)
        {

            var session = await ConnectedSessionAsync();

            var reply = await AskAsync(session, "probe-type",
                            "<iq" +
                            (type is not null ? $" type='{type}'" : "") +
                            $" id='probe-type' from='{Server.Domain}' to='{session.FullJid}'>" +
                            "<query xmlns='urn:example:does-not-exist'/></iq>");

            Assert.Multiple(() =>
            {

                Assert.That(reply, Does.Contain("type='error'"));
                Assert.That(reply, Does.Contain("<bad-request "));
                Assert.That(reply, Does.Contain("urn:ietf:params:xml:ns:xmpp-stanzas"));

                // Section 8.3.3.1: modify and not cancel - the sender can put it
                // right and try again.
                Assert.That(reply, Does.Contain("type='modify'"));

                Assert.That(reply, Does.Contain($"to='{Server.Domain}'"));

            });

        }

        #endregion

        #region TheRefusalComesEvenWithoutAnId()

        /// <summary>
        /// Without an <c>id</c> the refusal is sent all the same — unlike with
        /// an unhandled but well-formed request.
        /// </summary>
        /// <remarks>
        /// The difference lies in what the reply says. A
        /// <c>&lt;service-unavailable/&gt;</c> answers a question, and a reply
        /// without an <c>id</c> cannot be assigned to any question — it is of
        /// use to nobody, which is why the client stays silent there (see
        /// <see cref="IqWithoutId_IsNotAnswered"/>).
        ///
        /// <c>&lt;bad-request/&gt;</c> says something about the stanza itself:
        /// that its form is wrong. The sender can put that to use even when they
        /// cannot assign it to any open question — all the more so because the
        /// missing <c>id</c> is itself, under rule 1, part of what is wrong.
        ///
        /// An empty <c>id=''</c> would be the worst outcome of all: it belongs
        /// to no question and looks as though it belonged to one.
        /// </remarks>
        [Test]
        public async Task TheRefusalComesEvenWithoutAnId()
        {

            var session = await ConnectedSessionAsync();

            await session.SendAsync(
                      $"<iq type='maybe' from='{Server.Domain}' to='{session.FullJid}'>" +
                      "<query xmlns='urn:example:does-not-exist'/></iq>");

            await WaitFor(() => session.Received.Any(f => f.Contains("bad-request", StringComparison.Ordinal)),
                          "the refusal without an id");

            var reply = session.Received.First(f => f.Contains("bad-request", StringComparison.Ordinal));

            Assert.That(reply, Does.Not.Contain("id="),
                        "What had no id does not get an empty one back either.");

        }

        #endregion

        #region TheRefusalWithoutASenderCarriesNoTo()

        /// <summary>
        /// Without a <c>from</c> the stanza came from one's own server (RFC
        /// 6120, section 8.1.1.1) — the refusal then goes back without a
        /// <c>to</c>.
        /// </summary>
        /// <remarks>
        /// And that is the rule, not the exception: what a server sends to its
        /// own client often carries no <c>from</c>. A <c>to=''</c> would be an
        /// address here that does not exist.
        /// </remarks>
        [Test]
        public async Task TheRefusalWithoutASenderCarriesNoTo()
        {

            var session = await ConnectedSessionAsync();

            var reply = await AskAsync(session, "probe-without-sender",
                            "<iq type='maybe' id='probe-without-sender'>" +
                            "<query xmlns='urn:example:does-not-exist'/></iq>");

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("<bad-request "));
                Assert.That(reply, Does.Not.Contain(" to='"));
            });

        }

        #endregion

    }

}
