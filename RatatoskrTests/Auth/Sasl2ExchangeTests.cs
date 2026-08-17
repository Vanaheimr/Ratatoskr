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
    /// XEP-0388, the SASL2 profile: same mechanisms, different wrapping - and
    /// one round trip fewer.
    /// </summary>
    /// <remarks>
    /// The two profiles are announced side by side and a client picks. That
    /// makes almost every difference between them invisible from the outside:
    /// the login succeeds either way, with the same mechanism, against the same
    /// account. What is measured here is therefore mostly the wire itself.
    /// </remarks>
    [TestFixture]
    public class Sasl2ExchangeTests : AXMPPTests
    {

        #region Helper

        private static String? FrameStartingWith(IEnumerable<String> frames, String prefix)
            => frames.FirstOrDefault(f => f.StartsWith(prefix, StringComparison.Ordinal));

        #endregion


        #region TheServerAnnouncesBothProfiles()

        /// <summary>
        /// XEP-0388 is a replacement for the SASL profile of RFC 6120, not a
        /// break with it: during the transition a server offers both so that a
        /// client which knows only one still gets in.
        /// </summary>
        [Test]
        public async Task TheServerAnnouncesBothProfiles()
        {

            var client   = await ConnectClientAsync("alice");
            var session  = Server.SessionOf(client.FullJid.ToString())!;

            var features = FrameStartingWith(session.Sent, "<stream:features");

            Assert.Multiple(() =>
            {

                Assert.That(features, Does.Contain("urn:ietf:params:xml:ns:xmpp-sasl"),
                            "The RFC 6120 mechanisms have to stay for clients that know no SASL2.");

                Assert.That(features, Does.Contain("<authentication xmlns='urn:xmpp:sasl:2'>"));

            });

        }

        #endregion

        #region TheClientTakesTheNewerProfile()

        /// <summary>
        /// Offered both, the client takes SASL2 - and says so on the wire with
        /// <c>&lt;authenticate/&gt;</c> rather than <c>&lt;auth/&gt;</c>.
        /// </summary>
        [Test]
        public async Task TheClientTakesTheNewerProfile()
        {

            var client   = await ConnectClientAsync("alice");
            var session  = Server.SessionOf(client.FullJid.ToString())!;

            Assert.Multiple(() =>
            {

                Assert.That(client.Connection.UsedSasl2, Is.True);

                Assert.That(FrameStartingWith(session.Received, "<authenticate"), Is.Not.Null,
                            "SASL2 opens with <authenticate/>.");

                Assert.That(FrameStartingWith(session.Received, "<auth "), Is.Null,
                            "And not with the RFC 6120 element as well.");

            });

        }

        #endregion

        #region TheInitialResponseIsAChild_AndTheSuccessCarriesAdditionalData()

        /// <summary>
        /// The two places the SASL data moves to, and both are easy to get
        /// wrong in a way that still looks like base64 in the right place.
        /// </summary>
        /// <remarks>
        /// RFC 6120 carries the mechanism's data as the text of
        /// <c>&lt;auth/&gt;</c> and <c>&lt;success/&gt;</c>; XEP-0388 moves it
        /// into <c>&lt;initial-response/&gt;</c> and
        /// <c>&lt;additional-data/&gt;</c>. Read the element's text in the
        /// SASL2 case and you get the concatenation of every child instead -
        /// which decodes to nothing sensible and fails the server signature
        /// check against a server that did nothing wrong.
        /// </remarks>
        [Test]
        public async Task TheInitialResponseIsAChild_AndTheSuccessCarriesAdditionalData()
        {

            var client       = await ConnectClientAsync("alice");
            var session      = Server.SessionOf(client.FullJid.ToString())!;

            var authenticate = FrameStartingWith(session.Received, "<authenticate")!;
            var success      = FrameStartingWith(session.Sent,     "<success")!;

            Assert.Multiple(() =>
            {

                Assert.That(authenticate, Does.Contain("<initial-response>"),
                            "The client-first-message is a child element, not the text.");

                Assert.That(success, Does.StartWith("<success xmlns='urn:xmpp:sasl:2'>"));
                Assert.That(success, Does.Contain("<additional-data>"),
                            "The server-final-message travels as additional-data.");

                // The identity the exchange settled, which RFC 6120 never
                // stated in the success at all.
                Assert.That(success, Does.Contain($"<authorization-identifier>alice@{Server.Domain}"));

            });

        }

        #endregion

        #region TheStreamIsNotRestarted()

        /// <summary>
        /// The round trip XEP-0388 saves, and the one thing that would deadlock
        /// if either side got it wrong.
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 6.4.6 has the client open a new stream after the
        /// login; XEP-0388, section 3.6 has the server send the next features
        /// immediately instead. A client that restarts anyway begins a second
        /// negotiation over a stream that has moved on; a server that waits for
        /// a restart that never comes waits forever. Counting the
        /// <c>&lt;open/&gt;</c> frames is the cheapest way to state which of the
        /// two happened.
        /// </remarks>
        [Test]
        public async Task TheStreamIsNotRestarted()
        {

            var client   = await ConnectClientAsync("alice");
            var session  = Server.SessionOf(client.FullJid.ToString())!;

            var opens = session.Received.Count(f => f.StartsWith("<open", StringComparison.Ordinal));

            Assert.Multiple(() =>
            {

                Assert.That(opens, Is.EqualTo(1),
                            "One <open/> for the whole connection: SASL2 does not restart.");

                // And the login really did complete - otherwise "no restart"
                // would be true of a handshake that simply stopped.
                Assert.That(client.IsConnected,      Is.True);
                Assert.That(client.FullJid.ToString(),          Is.Not.Empty);

            });

        }

        #endregion

        #region TheOlderProfileStillWorks()

        /// <summary>
        /// A server that offers no SASL2 is not left behind - which is most of
        /// them, including the ejabberd 23.01 this was first pointed at.
        /// </summary>
        [Test]
        public async Task TheOlderProfileStillWorks()
        {

            Server.OfferSasl2 = false;

            var client   = await ConnectClientAsync("alice");
            var session  = Server.SessionOf(client.FullJid.ToString())!;

            Assert.Multiple(() =>
            {

                Assert.That(client.Connection.UsedSasl2, Is.False);
                Assert.That(client.IsConnected,          Is.True);

                Assert.That(FrameStartingWith(session.Received, "<auth "), Is.Not.Null,
                            "Without the newer offer the client falls back to <auth/>.");

                // Two opens here, and that is the difference the profile makes.
                Assert.That(session.Received.Count(f => f.StartsWith("<open", StringComparison.Ordinal)),
                            Is.EqualTo(2),
                            "RFC 6120 restarts the stream after the login.");

            });

        }

        #endregion

        #region AClientThatDeclinesSasl2_UsesTheOlderProfile()

        /// <summary>
        /// The other half of the same switch: the server offers both and the
        /// client declines the newer one.
        /// </summary>
        /// <remarks>
        /// Worth its own test because the choice is the client's and nothing
        /// about the offer changes. It is also what keeps the RFC 6120 path
        /// reachable against a server that has moved on.
        /// </remarks>
        [Test]
        public async Task AClientThatDeclinesSasl2_UsesTheOlderProfile()
        {

            Server.AddAccount("alice");

            var client = CreateClient("alice");
            client.Connection.UseSasl2 = false;

            await client.ConnectAsync();

            Assert.Multiple(() =>
            {
                Assert.That(client.Connection.UsedSasl2, Is.False);
                Assert.That(client.IsConnected,          Is.True);
            });

        }

        #endregion

        #region AFailure_ComesBackInTheSameProfile()

        /// <summary>
        /// The refusal has to arrive in the namespace the exchange was opened
        /// in, or it reaches a client that is not listening for it.
        /// </summary>
        /// <remarks>
        /// XEP-0388, section 3.5 keeps the RFC 6120 condition inside the new
        /// wrapper: the profile changed, not the vocabulary of what can go
        /// wrong. So the frame carries both namespaces, and that is correct
        /// rather than confused.
        /// </remarks>
        [Test]
        public async Task AFailure_ComesBackInTheSameProfile()
        {

            Server.AddAccount("alice");

            var client = CreateClient("alice", password: "the wrong one");

            try
            {
                await client.ConnectAsync();
            }
            catch (AuthenticationException)
            { }

            var session = Server.Sessions.LastOrDefault();

            Assert.That(session, Is.Not.Null, "The stream has to have been opened at all.");

            var failure = FrameStartingWith(session!.Sent, "<failure");

            Assert.Multiple(() =>
            {

                Assert.That(failure, Is.Not.Null, "A wrong password owes a <failure/>.");

                Assert.That(failure, Does.StartWith("<failure xmlns='urn:xmpp:sasl:2'>"));

                Assert.That(failure, Does.Contain("not-authorized"));

                Assert.That(failure, Does.Contain("urn:ietf:params:xml:ns:xmpp-sasl"),
                            "The condition stays an RFC 6120 one inside the newer wrapper.");

            });

        }

        #endregion

    }

}
