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
        /// A stranger answers a request that was addressed to somebody else -
        /// and the request goes on waiting for the one it asked.
        /// </summary>
        /// <remarks>
        /// <b>Both halves are checked, and the second is the one easy to get
        /// wrong.</b> An implementation that recognises the forgery but takes
        /// the pending entry out along the way has only exchanged one damage
        /// for another: the genuine answer then belongs to nobody, so whoever
        /// cannot be believed could at least see to it that nobody else is.
        ///
        /// <b>The server is taken out of the exchange</b>, and that is what
        /// makes this a measurement rather than a coin toss. With
        /// <c>SwallowClientStanzas</c> nothing answers the request except the
        /// forgery, so the pending entry can only ever be touched by the thing
        /// under examination.
        ///
        /// It used to bet on winning a race instead, and lost - once in two
        /// full runs on Debian. The forgery is played in from an
        /// <c>OnRawXml</c> handler, and the remark here claimed that this
        /// happens early enough that "no real answer can have arrived". Half of
        /// that was true: the waiting entry does exist by then. But
        /// <c>SendAsync</c> writes the stanza to the socket, releases the send
        /// lock, and raises <c>OnRawXml</c> only afterwards - so in between, the
        /// server's refusal of the unreachable domain could arrive and the
        /// receive loop could take the entry. <c>TryCompleteIqAsync</c> then
        /// finds nothing under that id and returns at its first guard, without
        /// reporting anything, and the test failed while the code it examines
        /// had done everything right.
        ///
        /// So <b>both answers are now played in by this test</b>, at moments it
        /// decides: the forgery first, then the genuine one. The report is
        /// awaited rather than slept for, and whether the request survived the
        /// forgery is answered by the genuine answer still finding its entry.
        ///
        /// <b>Counter-checked, both halves.</b> With
        /// <c>AnswerBelongsHere</c> forced to <c>true</c> - the forgery
        /// believed - no report comes and the first await expires. With the
        /// pending entry removed whatever the sender - the second damage - the
        /// genuine answer finds nothing and the second await expires. An
        /// earlier version of this rewrite asked instead whether the fetch was
        /// still running at that point, and that question passed with the
        /// second fault in place: a request whose entry is gone does not
        /// complete either, it just waits for nothing.
        ///
        /// The identifier is <c>pep-1</c> because it really is the first one -
        /// the counter sits on the connection and this client is fresh. That
        /// the test can know it is no convenience here; it is the precondition
        /// of the attack, written down.
        /// </remarks>
        [Test]
        public async Task AForgedAnswer_IsNeitherBelievedNorAllowedToConsumeTheRequest()
        {

            var client = await ConnectClientAsync();

            // Nothing the client sends is answered from here on, so whatever
            // reaches the pending request is the forgery and nothing else.
            Server.SwallowClientStanzas = true;

            var asked    = "bob@far.example";

            var reported = new TaskCompletionSource<String>(TaskCreationOptions.RunContinuationsAsynchronously);

            client.Connection.OnSpoofingAttempt += (timestamp, sender, message, ct) => {
                reported.TrySetResult(message);
                return Task.CompletedTask;
            };

            client.Connection.OnRawXml += async (timestamp, sender, line, ct) => {
                if (line.StartsWith(">>>") && line.Contains("id='pep-1'"))
                    await client.Connection.ProcessStanzaAsync(
                        $"<iq type='result' id='pep-1' from='mallory@{Server.Domain}' " +
                        $"to='{client.FullJid}'/>", ct);
            };

            // Deliberately not awaited yet: what happens to it after the
            // forgery is the second half of what is being measured.
            var fetch     = client.Connection.FetchOmemoBundleAsync(JID.Parse(asked), 4711);

            var spoofing  = await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(spoofing, Does.Contain("mallory"),
                        "A forged answer has to be reported, not believed.");

            // And now the one that was actually asked, played in by hand for
            // the same reason as the forgery: so that it arrives at a moment
            // this test decides rather than the network does. If the forgery
            // took the entry along with it, there is nothing here for this
            // answer to belong to and the fetch sits out its full timeout - so
            // the second damage shows up as this await expiring.
            await client.Connection.ProcessStanzaAsync(
                      $"<iq type='result' id='pep-1' from='{asked}' to='{client.FullJid}'/>");

            var fetched = await fetch.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(fetched, Is.Null,
                        "And nothing a stranger sent may come back as a bundle.");

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
                Assert.That(connection.AnswerBelongsHere(null, connection.BareJid.ToString()),  Is.True,
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
                Assert.That(connection.AnswerBelongsHere(connection.BareJid.ToString(),    null), Is.True);
                Assert.That(connection.AnswerBelongsHere("bob@far.example",     null), Is.True);
            });

        }

        #endregion

    }

}
