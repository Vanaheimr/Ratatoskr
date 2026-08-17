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
    /// The payloads inside <c>iq</c> stanzas in valid but unusual spelling:
    /// service discovery, PubSub and ping.
    ///
    /// The last part of the rebuild from regular expressions to an XML parser.
    /// </summary>
    [TestFixture]
    public class IqPayloadParsingTests : AXMPPTests
    {

        #region Helper functions

        private async Task<(XMPPClient Client, XMPPSession Session)> ConnectedPairAsync()
        {

            var client = await ConnectClientAsync();

            await WaitFor(() => Server.SessionOf(client.FullJid.ToString()) is not null,
                          "the server session for the client");

            return (client, Server.SessionOf(client.FullJid.ToString())!);

        }

        #endregion


        #region Ping_WithDoubleQuotedType_IsAnsweredWithAResult()

        /// <summary>
        /// The ping recognition looked literally for <c>type='get'</c>, that is,
        /// only with single quotation marks. Against a server that uses double
        /// ones the ping was not recognised - and, since the implementation of
        /// RFC 6120 §8.2.3, ended up in the fallback, so it got a
        /// <c>&lt;service-unavailable/&gt;</c> instead of an answer.
        /// </summary>
        [Test]
        public async Task Ping_WithDoubleQuotedType_IsAnsweredWithAResult()
        {

            var (_, session) = await ConnectedPairAsync();

            await session.SendAsync(
                $"<iq type=\"get\" id=\"p1\" from=\"{Server.Domain}\">" +
                "<ping xmlns=\"urn:xmpp:ping\"/></iq>");

            await WaitFor(() => session.Received.Any(f => f.Contains("id='p1'", StringComparison.Ordinal)),
                          "the answer to the ping");

            var reply = session.Received.First(f => f.Contains("id='p1'", StringComparison.Ordinal));

            Assert.Multiple(() =>
            {
                Assert.That(reply, Does.Contain("type='result'"));
                Assert.That(reply, Does.Not.Contain("service-unavailable"),
                            "A recognised ping must not end up in the fallback.");
            });

        }

        #endregion

        #region DiscoInfo_WithSlashInTheIdentityName_IsParsed()

        /// <summary>
        /// The pattern for identities excluded the slash
        /// (<c>&lt;identity([^/&gt;]+)/?&gt;</c>) so that it would not eat the
        /// closing <c>/&gt;</c> along with it. A name with a slash - our own
        /// client is called "XMPP Console Client" with the category
        /// <c>client/console</c>, so such a thing is anything but exotic - made
        /// the identity vanish completely.
        /// </summary>
        [Test]
        public async Task DiscoInfo_WithSlashInTheIdentityName_IsParsed()
        {

            var (client, session) = await ConnectedPairAsync();

            Server.OnStanzaReceived += (timestamp, sender, s, frame, ct) =>
            {
                if (frame.Contains("disco#info", StringComparison.Ordinal) &&
                    frame.Contains("type='get'", StringComparison.Ordinal))
                {

                    var id = System.Text.RegularExpressions.Regex.Match(frame, @"id='([^']+)'").Groups[1].Value;

                    _ = s.SendAsync(
                        $"<iq type='result' id='{id}' from='{Server.Domain}'>" +
                        "<query xmlns='http://jabber.org/protocol/disco#info'>" +
                        "<identity category='client' type='pc' name='Foo/Bar &amp; Co.'/>" +
                        "<feature var='urn:xmpp:ping'/>" +
                        "</query></iq>");

                }

                return Task.CompletedTask;

            };

            var info = await client.Connection.Disco!.QueryInfoAsync(JID.Parse(Server.Domain),
                                                                     timeout: TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(info, Is.Not.Null);
                Assert.That(info!.Identities, Has.Count.EqualTo(1));
                Assert.That(info!.Identities[0].Name, Is.EqualTo("Foo/Bar & Co."),
                            "Slash and entity in the name have to be kept.");
                Assert.That(info!.Features, Does.Contain("urn:xmpp:ping"));
            });

        }

        #endregion

        #region DiscoInfo_WithVarNotBeingTheFirstAttribute_IsParsed()

        /// <summary>
        /// The feature pattern demanded <c>var</c> immediately after
        /// <c>&lt;feature</c>. If another attribute stands in front of it, the
        /// feature vanished from the list - and the client took the counterpart
        /// for less capable than it is.
        /// </summary>
        [Test]
        public async Task DiscoInfo_WithVarNotBeingTheFirstAttribute_IsParsed()
        {

            var (client, session) = await ConnectedPairAsync();

            Server.OnStanzaReceived += (timestamp, sender, s, frame, ct) =>
            {
                if (frame.Contains("disco#info", StringComparison.Ordinal) &&
                    frame.Contains("type='get'", StringComparison.Ordinal))
                {

                    var id = System.Text.RegularExpressions.Regex.Match(frame, @"id='([^']+)'").Groups[1].Value;

                    _ = s.SendAsync(
                        $"<iq type='result' id='{id}' from='{Server.Domain}'>" +
                        "<query xmlns='http://jabber.org/protocol/disco#info'>" +
                        "<identity category='server' type='im'/>" +
                        "<feature xml:lang='de' var='urn:xmpp:carbons:2'/>" +
                        "</query></iq>");

                }

                return Task.CompletedTask;

            };

            var info = await client.Connection.Disco!.QueryInfoAsync(JID.Parse(Server.Domain),
                                                                     timeout: TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {

                Assert.That(info?.Features, Does.Contain("urn:xmpp:carbons:2"));

                // By way of HasFeature and not only by way of the list: that is
                // the question a caller asks. Until D57 it stood only behind
                // five abbreviations (SupportsCarbons and four more) that nobody
                // called - and with that HasFeature itself was touched by no
                // test either.
                Assert.That(info!.HasFeature("urn:xmpp:carbons:2"), Is.True);

                Assert.That(info.HasFeature("urn:xmpp:doesnotexist"), Is.False,
                            "HasFeature has to be able to deny as well.");

            });

        }

        #endregion

        #region PubSubEvent_WithDoubleQuotedNamespace_IsRecognised()

        /// <summary>
        /// The event pattern looked literally for
        /// <c>&lt;event xmlns='…pubsub#event'</c> - single quotation marks, and
        /// <c>xmlns</c> as the first attribute. XML prescribes neither.
        /// </summary>
        [Test]
        public async Task PubSubEvent_WithDoubleQuotedNamespace_IsRecognised()
        {

            var (client, session) = await ConnectedPairAsync();

            PubSubEvent? reported = null;
            client.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            await session.SendAsync(
                $"<iq type='set' id='ps1' from='pubsub.{Server.Domain}' to='{client.FullJid}'>" +
                "<event xmlns=\"http://jabber.org/protocol/pubsub#event\">" +
                "<items node=\"urn:example:news\">" +
                "<item id=\"1\"><payload xmlns='urn:example:x'>Content</payload></item>" +
                "</items></event></iq>");

            await WaitFor(() => reported is not null, "the reported PubSub event");

            Assert.Multiple(() =>
            {
                Assert.That(reported!.NodeId, Is.EqualTo("urn:example:news"));
                Assert.That(reported!.Type,   Is.EqualTo(PubSubEventType.Items));
                Assert.That(reported!.Items,  Has.Count.EqualTo(1));
            });

        }

        #endregion

        #region PubSubEvent_WithItemWithoutPayload_IsRecognised()

        /// <summary>
        /// An <c>&lt;item/&gt;</c> without a payload is permitted - XEP-0060
        /// allows pure notifications without content. The earlier pattern
        /// demanded a pair of an opening and a closing tag and overlooked
        /// self-closing items entirely.
        /// </summary>
        [Test]
        public async Task PubSubEvent_WithItemWithoutPayload_IsRecognised()
        {

            var (client, session) = await ConnectedPairAsync();

            PubSubEvent? reported = null;
            client.OnPubSubEvent += (timestamp, sender, e, ct) => { reported = e; return Task.CompletedTask; };

            await session.SendAsync(
                $"<iq type='set' id='ps2' from='pubsub.{Server.Domain}' to='{client.FullJid}'>" +
                "<event xmlns='http://jabber.org/protocol/pubsub#event'>" +
                "<items node='urn:example:signals'>" +
                "<item id='without-content'/>" +
                "</items></event></iq>");

            await WaitFor(() => reported is not null, "the reported PubSub event");

            Assert.Multiple(() =>
            {
                Assert.That(reported!.Items, Has.Count.EqualTo(1));
                Assert.That(reported!.Items[0].Id, Is.EqualTo("without-content"));
            });

        }

        #endregion

        #region RosterNamespaceInsideAForwardedMessage_IsNotTakenAsARosterPush()

        /// <summary>
        /// The case distinction in ProcessIq ran over
        /// <c>stanza.Contains("jabber:iq:roster")</c> - so the namespace only
        /// had to occur somewhere in the text. An embedded message that mentions
        /// it was thereby treated as a roster push.
        /// </summary>
        [Test]
        public async Task RosterNamespaceInsideAForwardedMessage_IsNotTakenAsARosterPush()
        {

            var (client, session) = await ConnectedPairAsync();

            await session.SendAsync(
                $"<iq type='set' id='fake-push' to='{client.FullJid}'>" +
                "<forwarded xmlns='urn:xmpp:forward:0'>" +
                "<message xmlns='jabber:client'>" +
                "<query xmlns='jabber:iq:roster'>" +
                $"<item jid='intruder@{Server.Domain}' subscription='both'/>" +
                "</query></message></forwarded></iq>");

            await WaitFor(() => session.Received.Any(f => f.Contains("id='fake-push'", StringComparison.Ordinal)),
                          "the answer to the IQ");

            Assert.That(client.GetContact(JID.Parse($"intruder@{Server.Domain}")), Is.Null,
                        "An embedded roster element is no roster push.");

        }

        #endregion

    }

}
