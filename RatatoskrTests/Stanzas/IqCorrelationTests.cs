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
    /// An IQ answer is assigned by its identifier <b>and</b> its sender.
    /// </summary>
    /// <remarks>
    /// The identifier alone never was an assignment. This side chooses it, it
    /// is short and countable - <c>pep-1</c>, <c>roster1</c>, <c>disco-info-2</c>
    /// - and it travels in the clear; the full JID it is addressed to goes out
    /// in every presence. So anybody who may write to this client can guess an
    /// identifier that is currently in flight.
    ///
    /// Where that costs something is the OMEMO bundle fetch. The keys of a peer
    /// are asked for over PEP, and a substituted answer carries substituted
    /// keys. For a peer already known the identity check catches it afterwards,
    /// because a changed key is refused - on the very first contact there is
    /// nothing to compare against, and the first contact is what the fetch is
    /// for.
    /// </remarks>
    [TestFixture]
    public class IqCorrelationTests : AXMPPTests
    {

        #region AForgedAnswer_IsNeitherBelievedNorAllowedToConsumeTheRequest()

        /// <summary>
        /// The whole finding in one sequence: a stranger answers a request that
        /// was addressed to somebody else, and afterwards the entity actually
        /// asked answers as well.
        /// </summary>
        /// <remarks>
        /// <b>Both halves are checked, and the second is the one easy to get
        /// wrong.</b> An implementation that recognises the forgery but takes
        /// the pending entry out along the way has only exchanged one damage for
        /// another: the genuine answer arrives afterwards and belongs to nobody,
        /// so whoever cannot be believed can at least make sure that nobody else
        /// is either. That is why the genuine answer is played in afterwards and
        /// has to arrive.
        ///
        /// The identifier is <c>pep-1</c> because it really is the first one -
        /// the counter sits on the connection and this client is fresh. That the
        /// test can know it is not a convenience here; it is the precondition of
        /// the attack, written down.
        /// </remarks>
        [Test]
        public async Task AForgedAnswer_IsNeitherBelievedNorAllowedToConsumeTheRequest()
        {

            var client = await ConnectClientAsync();

            String? spoofing = null;
            client.Connection.OnSpoofingAttempt += message => spoofing = message;

            // The request goes to a domain this server does not federate with,
            // so nothing real can answer and the sequence stays ours.
            var asked   = $"bob@far.example";
            var wentOut = false;

            client.Connection.OnRawXml += line => {
                if (line.StartsWith(">>>") && line.Contains("id='pep-1'"))
                    wentOut = true;
            };

            var fetch = client.Connection.FetchOmemoBundleAsync(asked, 4711);

            await WaitFor(() => wentOut, "the bundle request to go out");

            // 1. The stranger. Right identifier, right recipient, wrong sender.
            client.Connection.ProcessStanza(
                $"<iq type='result' id='pep-1' from='mallory@{Server.Domain}' " +
                $"to='{client.FullJid}'/>");

            await WaitFor(() => spoofing is not null, "the forgery to be reported");

            Assert.That(fetch.IsCompleted, Is.False,
                        "A forged answer must not finish the request - neither with its " +
                        "content nor by taking the waiting party's place away.");

            // 2. The entity that was actually asked. It carries no bundle, so
            //    the result is null - what is being measured is that it arrives
            //    at all.
            client.Connection.ProcessStanza(
                $"<iq type='result' id='pep-1' from='{asked}' to='{client.FullJid}'/>");

            await WaitFor(() => fetch.IsCompleted,
                          "the genuine answer to reach the request",
                          TimeSpan.FromSeconds(3));

            Assert.Multiple(() =>
            {
                Assert.That(spoofing,        Is.Not.Null);
                Assert.That(spoofing,        Does.Contain("mallory"));
                Assert.That(fetch.Result,    Is.Null, "There was no bundle in it.");
            });

        }

        #endregion

        #region ARequestToNobody_IsAnsweredByOnesOwnServer()

        /// <summary>
        /// Roster, session, carbons: those requests carry no <c>to</c> and go to
        /// one's own server by that very fact (RFC 6120, section 10.3.3). It
        /// answers without a <c>from</c> - permitted by section 8.1.1.1 - or
        /// under the account's bare JID, or under the domain. All three are the
        /// same party and all three have to pass, or the connection setup would
        /// stall on its own roster.
        /// </summary>
        [Test]
        public async Task ARequestToNobody_IsAnsweredByOnesOwnServer()
        {

            var connection = (await ConnectClientAsync()).Connection;

            Assert.Multiple(() =>
            {
                Assert.That(connection.AnswerBelongsHere(null, null),                Is.True,
                            "No 'from' is one's own server.");
                Assert.That(connection.AnswerBelongsHere(null, connection.BareJid),  Is.True,
                            "One's own bare JID is one's own server.");
                Assert.That(connection.AnswerBelongsHere(null, Server.Domain),       Is.True,
                            "The domain is one's own server.");
            });

        }

        #endregion

        #region ARequestToNobody_IsNotAnsweredByAStranger()

        /// <summary>
        /// The counter-check to the one above, and the one that carries the
        /// weight: the roster request has the fixed identifier <c>roster1</c>,
        /// and it goes out at every single connection setup.
        /// </summary>
        [Test]
        public async Task ARequestToNobody_IsNotAnsweredByAStranger()
        {

            var connection = (await ConnectClientAsync()).Connection;

            Assert.Multiple(() =>
            {
                Assert.That(connection.AnswerBelongsHere(null, $"mallory@{Server.Domain}"), Is.False);
                Assert.That(connection.AnswerBelongsHere(null, "evil.example"),             Is.False);
            });

        }

        #endregion

        #region ARequestToSomebody_TakesOnlyTheirAnswer()

        /// <summary>
        /// Compared bare, deliberately, the way this whole codebase compares
        /// JIDs. A request to a full JID may be answered by another resource of
        /// the same account - that is the same person, and refusing it would
        /// cost real answers to buy nothing.
        /// </summary>
        [Test]
        public async Task ARequestToSomebody_TakesOnlyTheirAnswer()
        {

            var connection = (await ConnectClientAsync()).Connection;

            Assert.Multiple(() =>
            {

                Assert.That(connection.AnswerBelongsHere("bob@far.example", "bob@far.example"),
                            Is.True);

                Assert.That(connection.AnswerBelongsHere("bob@far.example", "bob@far.example/phone"),
                            Is.True, "Another resource of the same account is the same account.");

                Assert.That(connection.AnswerBelongsHere("bob@far.example", "mallory@far.example"),
                            Is.False);

                Assert.That(connection.AnswerBelongsHere("bob@far.example", "bob@evil.example"),
                            Is.False, "The same local part on another domain is somebody else.");

            });

        }

        #endregion

        #region AnAnswerWithoutAFrom_IsOnesOwnServer_WhoeverWasAsked()

        /// <summary>
        /// An answer that names no sender passes, no matter who was addressed.
        /// </summary>
        /// <remarks>
        /// <b>That is a permission, and it has to be argued for rather than
        /// assumed.</b> It costs nothing, because a peer cannot make use of it:
        /// RFC 6120, section 8.1.2.1 obliges the server to write the sender's
        /// full JID onto every stanza it takes from a client and to override
        /// whatever the client put there. Whatever a stranger sends therefore
        /// arrives carrying their own address - and that is the case the
        /// comparison catches. A missing <c>from</c> means the server wrote the
        /// answer itself, and against the server this comparison never
        /// protected: it routes everything and may put any address on top.
        ///
        /// Refusing it also breaks things. A server may answer an addressed
        /// request without naming itself; the one in this repository does so on
        /// several paths, and demanding a <c>from</c> turned those answers into
        /// ten-second timeouts.
        /// </remarks>
        [Test]
        public async Task AnAnswerWithoutAFrom_IsOnesOwnServer_WhoeverWasAsked()
        {

            var connection = (await ConnectClientAsync()).Connection;

            Assert.Multiple(() =>
            {
                Assert.That(connection.AnswerBelongsHere(null,                  null), Is.True);
                Assert.That(connection.AnswerBelongsHere(connection.BareJid,    null), Is.True);
                Assert.That(connection.AnswerBelongsHere("bob@far.example",     null), Is.True);
            });

        }

        #endregion

    }

}
