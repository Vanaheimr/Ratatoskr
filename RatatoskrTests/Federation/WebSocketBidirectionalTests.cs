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
    /// XEP-0288 over the WebSocket transport.
    /// </summary>
    /// <remarks>
    /// The protocol layer is the same as over TCP and is checked there already
    /// (<see cref="BidirectionalStreamTests"/>). Here it is solely about this
    /// transport starting the negotiation off as well and really using the
    /// return direction - both of these are lines in
    /// <see cref="WebSocketServerLinks"/> and not in <see cref="S2SStream"/>.
    ///
    /// <b>One difference to the TCP set-up, and it is admitted:</b> there the
    /// far end does not know us, and without a return direction the answer is
    /// lost. That would not work here - the WebSocket path identifies itself
    /// exclusively over dialback (there is no SASL-EXTERNAL here), and its
    /// query needs precisely the direction that would then not exist. Both
    /// sides are therefore entered here, and the answer would arrive without
    /// bidi as well. That is why these tests check
    /// <see cref="WebSocketServerLinks.BidirectionalDeliveryCount"/> and not
    /// the arrival: only the number says which path it took.
    /// </remarks>
    [TestFixture]
    public class WebSocketBidirectionalTests
    {

        #region Data

        private XMPPServer            _left    = null!;
        private XMPPServer            _right   = null!;
        private WebSocketServerLinks  _leftS   = null!;
        private WebSocketServerLinks  _rightS  = null!;

        private readonly List<XMPPClient> _clients = [];
        private readonly InternalErrorGuard _guard = new();

        #endregion

        #region TearDown

        /// <summary>
        /// Arm the guard before every test.
        /// </summary>
        [SetUp]
        public void ArmTheGuard()
            => _guard.Reset();

        [TearDown]
        public async Task CleanUp()
        {

            foreach (var client in _clients)
            {
                try { await client.DisposeAsync(); } catch { /* does not matter in the teardown */ }
            }

            _clients.Clear();

            if (_leftS  is not null) await _leftS.DisposeAsync();
            if (_rightS is not null) await _rightS.DisposeAsync();

            if (_left   is not null) await _left.DisposeAsync();
            if (_right  is not null) await _right.DisposeAsync();

            _guard.AssertClean();

        }

        #endregion

        #region Helper functions

        private void Wire(Boolean bidi)
        {

            _left   = _guard.Watched(new XMPPServer("left.example"));
            _right  = _guard.Watched(new XMPPServer("right.example"));

            _left.Start();
            _right.Start();

            _leftS   = new WebSocketServerLinks(_left)  { OfferBidirectionalStreams = bidi, RequestBidirectionalStreams = bidi };
            _rightS  = new WebSocketServerLinks(_right) { OfferBidirectionalStreams = bidi, RequestBidirectionalStreams = bidi };

            _leftS.AddPeer(_right.Domain, _rightS.Uri, _right.IsOwnCertificate);
            _rightS.AddPeer(_left.Domain, _leftS.Uri,  _left.IsOwnCertificate);

        }

        private async Task<XMPPClient> ConnectAsync(XMPPServer server, String localPart)
        {

            if (server.GetAccount($"{localPart}@{server.Domain}") is null)
                server.AddAccount(localPart);

            var connection                                   = new XMPPConnection($"{localPart}@{server.Domain}", "pw", server.Uri) {
                                 KeepaliveEnabled            = false,
                                 MaxReconnectAttempts        = 0,
                                 ServerCertificateValidator  = server.IsOwnCertificate
                             };

            var client = new XMPPClient(connection);
            _clients.Add(client);

            await client.ConnectAsync();

            return client;

        }

        private static async Task WaitFor(Func<Boolean> condition, String what)
        {
            Assert.That(await XMPPServer.WaitUntilAsync(condition),
                        Is.True, $"Timeout while waiting for: {what}");
        }

        /// <summary>
        /// Sends there and back and delivers what arrived at Alice's.
        /// </summary>
        private async Task<List<String>> ThereAndBackAsync()
        {

            var alice  = await ConnectAsync(_left,  "alice");
            var juliet = await ConnectAsync(_right, "juliet");

            _left.GetAccount(alice.BareJid.ToString())!.SetRosterEntry(new RosterEntry(juliet.BareJid.ToString(), null, "both"));
            _right.GetAccount(juliet.BareJid.ToString())!.SetRosterEntry(new RosterEntry(alice.BareJid.ToString(), null, "both"));

            var atJuliet = new List<String>();
            var atAlice  = new List<String>();

            juliet.OnMessage += (timestamp, sender, m, ct) => { atJuliet.Add(m.Body ?? ""); return Task.CompletedTask; };
            alice.OnMessage  += (timestamp, sender, m, ct) => { atAlice.Add(m.Body ?? ""); return Task.CompletedTask; };

            await alice.SendMessageAsync(juliet.BareJid, "There");
            await WaitFor(() => atJuliet.Count > 0, "the message at Juliet's");

            await juliet.SendMessageAsync(alice.BareJid, "Back");
            await WaitFor(() => atAlice.Any(b => b == "Back"), "the answer at Alice's");

            return atAlice;

        }

        #endregion


        #region TheAnswerTakesTheReturnPath()

        /// <summary>
        /// With XEP-0288 the answer takes the existing connection.
        /// </summary>
        /// <remarks>
        /// What is checked is the side that <i>was</i> dialled. That the other
        /// side does not use its return direction would be the obvious
        /// counter-check - but it does not hold: as soon as even one stanza
        /// runs in the reverse direction (a delivery receipt according to
        /// XEP-0184 is already enough), the far end dials in turn, and then the
        /// first side has an inbound connection too, which it prefers from then
        /// on. Two servers knowing each other therefore collapse under bidi
        /// onto the connections they have anyway - precisely the purpose of the
        /// extension, but nothing a time-independent assurance could rest on.
        /// </remarks>
        [Test]
        public async Task TheAnswerTakesTheReturnPath()
        {

            Wire(bidi: true);

            await ThereAndBackAsync();

            Assert.That(_rightS.BidirectionalDeliveryCount, Is.GreaterThan(0),
                        "The answer arrived, but over a connection of its own.");

        }

        #endregion

        #region WithoutBidi_TheAnswerTakesItsOwnConnection()

        /// <summary>
        /// Without the extension the far end dials - the answer arrives, but on
        /// the other path.
        /// </summary>
        /// <remarks>
        /// The counter-check to the counter. Without it a number greater than
        /// zero would establish nothing, because it could come about without
        /// the extension as well - and the arrival alone does not tell the two
        /// paths apart at all.
        /// </remarks>
        [Test]
        public async Task WithoutBidi_TheAnswerTakesItsOwnConnection()
        {

            Wire(bidi: false);

            var atAlice = await ThereAndBackAsync();

            Assert.Multiple(() =>
            {
                Assert.That(atAlice, Does.Contain("Back"),
                            "Without bidi the ordinary path has to keep carrying.");
                Assert.That(_rightS.BidirectionalDeliveryCount, Is.Zero);
            });

        }

        #endregion

        #region AStanzaForAnUnknownDomain_DoesNotTakeSomeoneElsesReturnPath()

        /// <summary>
        /// A stanza to a third domain must not go out over the return direction
        /// of another one.
        /// </summary>
        /// <remarks>
        /// The comparison of the domain has stood in
        /// <c>S2SStream.TryDeliverOverBidiAsync</c> since S9b and thereby holds
        /// for both transports - but "holds jointly" is a claim about the
        /// build of the code, and no test of the other transport checks that.
        ///
        /// Here without a third server: a domain nobody knows has no connection
        /// and no return direction. Were the stanza to go out nevertheless, it
        /// would have taken the connection of <c>right</c>.
        /// </remarks>
        [Test]
        public async Task AStanzaForAnUnknownDomain_DoesNotTakeSomeoneElsesReturnPath()
        {

            Wire(bidi: true);

            await ThereAndBackAsync();

            Assert.That(_rightS.BidirectionalDeliveryCount, Is.GreaterThan(0),
                        "Set-up of the test: there is a used return direction.");

            var before = _rightS.BidirectionalDeliveryCount;

            var wentOut = await _rightS.DeliverAsync(
                             "elsewhere.example",
                           "<message from='juliet@right.example' to='romeo@elsewhere.example'/>");

            Assert.Multiple(() =>
            {
                Assert.That(wentOut, Is.False,
                            "For an unknown domain there is no path.");
                Assert.That(_rightS.BidirectionalDeliveryCount, Is.EqualTo(before),
                            "And certainly not the return direction of another one.");
            });

        }

        #endregion

    }

}
