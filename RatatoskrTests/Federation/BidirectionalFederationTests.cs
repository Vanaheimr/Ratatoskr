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
    /// XEP-0288 over real sockets - and that in the situation the extension
    /// exists for: the far end can<b>not</b> dial us.
    /// </summary>
    /// <remarks>
    /// The set-up is one-sided on purpose. <c>left</c> knows <c>right</c>,
    /// <c>right</c> does not know <c>left</c> - no entry, no resolver,
    /// nothing. With that the answer stands and falls with the return
    /// direction.
    ///
    /// The usual <see cref="TcpServerLinks.Connect"/> is no good here: it
    /// enters both sides mutually, and then the answer would arrive over a
    /// connection of its own - the test would pass without ever having made
    /// use of bidi. That is precisely why it checks
    /// <see cref="TcpServerLinks.BidirectionalDeliveryCount"/> as well and not
    /// only the arrival.
    /// </remarks>
    [TestFixture]
    public class BidirectionalFederationTests
    {

        #region Data

        private XMPPServer      _left    = null!;
        private XMPPServer      _right   = null!;
        private TcpServerLinks  _leftS   = null!;
        private TcpServerLinks  _rightS  = null!;

        private readonly List<XMPPClient> _clients = [];
        private readonly InternalErrorGuard _guard = new();

        #endregion

        #region SetUp / TearDown

        /// <summary>Arm the guard before every test.</summary>
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

            if (_left  is not null) await _left.DisposeAsync();
            if (_right is not null) await _right.DisposeAsync();

            _guard.AssertClean();

        }

        #endregion

        #region Helper functions

        /// <summary>
        /// Two servers, but only one direction is entered.
        /// </summary>
        private void ConnectOneSided(Boolean bidi)
        {

            _left   = _guard.Watched(new XMPPServer("left.example"));
            _right  = _guard.Watched(new XMPPServer("right.example"));

            _left.Start();
            _right.Start();

            // SASL-EXTERNAL and not dialback: its query would go in of all
            // directions the one that does not exist here. Right would have to
            // dial left in order to have the key checked - and could then just
            // as well send the answer straight away. The proof over the
            // certificate manages without a way back, and that is exactly what
            // this is about.
            _leftS                                       = new TcpServerLinks(_left) {
                            UseSaslExternal              = true,
                            OfferBidirectionalStreams    = bidi,
                            RequestBidirectionalStreams  = bidi
                        };

            _rightS                                      = new TcpServerLinks(_right) {
                            UseSaslExternal              = true,
                            OfferBidirectionalStreams    = bidi,
                            RequestBidirectionalStreams  = bidi
                        };

            // Only left knows right. Expressly the address and not
            // "localhost": the listener binds the IPv4 loopback, and a name
            // resolving to IPv6 first costs the fallback per connection.
            _leftS.AddPeer(_right.Domain,
                           System.Net.IPAddress.Loopback.ToString(),
                            _rightS.Port,
                            TcpTlsMode.StartTls,
                            _right.IsOwnCertificate);

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

        #endregion


        #region TheAnswerComesBackOverTheSameConnection()

        /// <summary>
        /// The core of XEP-0288: the answer takes the connection the question
        /// came over.
        /// </summary>
        [Test]
        public async Task TheAnswerComesBackOverTheSameConnection()
        {

            ConnectOneSided(bidi: true);

            var alice  = await ConnectAsync(_left,  "alice");
            var juliet = await ConnectAsync(_right, "juliet");

            _left.GetAccount(alice.BareJid)!.SetRosterEntry(new RosterEntry(juliet.BareJid, null, "both"));
            _right.GetAccount(juliet.BareJid)!.SetRosterEntry(new RosterEntry(alice.BareJid, null, "both"));

            var atJuliet = new List<String>();
            var atAlice  = new List<String>();

            juliet.OnMessage += m => atJuliet.Add(m.Body ?? "");
            alice.OnMessage  += m => atAlice.Add(m.Body ?? "");

            await alice.SendMessageAsync(juliet.BareJid, "There");
            await WaitFor(() => atJuliet.Count > 0, "the message at Juliet's");

            // And now the answer - for which right has no path to left.
            await juliet.SendMessageAsync(alice.BareJid, "Back");
            await WaitFor(() => atAlice.Any(b => b == "Back"), "the answer at Alice's");

            Assert.That(_rightS.BidirectionalDeliveryCount, Is.GreaterThan(0),
                        "The answer arrived, but not over the return direction.");

        }

        #endregion

        #region WithoutBidi_TheAnswerIsLost()

        /// <summary>
        /// The counter-check. Without the extension there is no way back, and
        /// the answer is lost.
        /// </summary>
        /// <remarks>
        /// Without this test the previous one would establish nothing: were the
        /// answer to arrive without bidi as well, it would have taken another
        /// path and the extension would have been uninvolved.
        ///
        /// The silent vanishing is the actual damage in this. Juliet's client
        /// considers the message sent off, her server has discarded it, and
        /// nobody learns of it - that is exactly how Prosody behaved in the run
        /// from S8.
        /// </remarks>
        [Test]
        public async Task WithoutBidi_TheAnswerIsLost()
        {

            ConnectOneSided(bidi: false);

            var alice  = await ConnectAsync(_left,  "alice");
            var juliet = await ConnectAsync(_right, "juliet");

            _left.GetAccount(alice.BareJid)!.SetRosterEntry(new RosterEntry(juliet.BareJid, null, "both"));
            _right.GetAccount(juliet.BareJid)!.SetRosterEntry(new RosterEntry(alice.BareJid, null, "both"));

            var atJuliet = new List<String>();
            var atAlice  = new List<String>();

            juliet.OnMessage += m => atJuliet.Add(m.Body ?? "");
            alice.OnMessage  += m => atAlice.Add(m.Body ?? "");

            await alice.SendMessageAsync(juliet.BareJid, "There");
            await WaitFor(() => atJuliet.Count > 0, "the message at Juliet's");

            await juliet.SendMessageAsync(alice.BareJid, "Back");

            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(atAlice.Any(b => b == "Back"), Is.False,
                            "Without bidi there must be no way back.");
                Assert.That(_rightS.BidirectionalDeliveryCount, Is.Zero);
            });

        }

        #endregion

        #region TheOutgoingDirection_StillWorksWithoutAReturnPath()

        /// <summary>
        /// Bidi must not touch the ordinary direction.
        /// </summary>
        /// <remarks>
        /// The return direction takes hold only where there is an identified
        /// inbound connection of the same domain. For everything else it stays
        /// with the dialling - otherwise the extension would have rebuilt the
        /// federation instead of adding to it.
        /// </remarks>
        [Test]
        public async Task TheOutgoingDirection_StillWorksWithoutAReturnPath()
        {

            ConnectOneSided(bidi: true);

            var alice  = await ConnectAsync(_left,  "alice");
            var juliet = await ConnectAsync(_right, "juliet");

            _left.GetAccount(alice.BareJid)!.SetRosterEntry(new RosterEntry(juliet.BareJid, null, "both"));

            var atJuliet = new List<String>();
            juliet.OnMessage += m => atJuliet.Add(m.Body ?? "");

            await alice.SendMessageAsync(juliet.BareJid, "There");

            await WaitFor(() => atJuliet.Count > 0, "the message at Juliet's");

            Assert.That(_leftS.BidirectionalDeliveryCount, Is.Zero,
                        "Left has dialled and used no return direction.");

        }

        #endregion

        #region TheReturnPath_GoesToTheRightDomain()

        /// <summary>
        /// If several far ends hang on with a return direction, a stanza has to
        /// take the connection of <b>its</b> domain.
        /// </summary>
        /// <remarks>
        /// The case a set-up with only one far end never shows - and precisely
        /// that is what a mutation got past that removes the domain comparison
        /// from the selection: with a single connection it changes nothing.
        ///
        /// In operation it would be a leak between two foreign servers. The
        /// stanza would go to the wrong far end, which does discard it (wrong
        /// recipient) but has read it beforehand - and the actual recipient
        /// would never get anything, without an error turning up anywhere.
        ///
        /// <c>farther</c> builds up first: a selection without a comparison
        /// would take the first connection to hand, and that would then be the
        /// wrong one.
        /// </remarks>
        [Test]
        public async Task TheReturnPath_GoesToTheRightDomain()
        {

            _left   = _guard.Watched(new XMPPServer("left.example"));
            _right  = _guard.Watched(new XMPPServer("right.example"));

            var farther = _guard.Watched(new XMPPServer("farther.example"));

            _left.Start();
            _right.Start();
            farther.Start();

            try
            {

                _leftS   = new TcpServerLinks(_left)  { UseSaslExternal = true, OfferBidirectionalStreams = true, RequestBidirectionalStreams = true };
                _rightS  = new TcpServerLinks(_right) { UseSaslExternal = true, OfferBidirectionalStreams = true, RequestBidirectionalStreams = true };

                var fartherS = new TcpServerLinks(farther) { UseSaslExternal = true, OfferBidirectionalStreams = true, RequestBidirectionalStreams = true };

                // Both dial right; right knows neither of them.
                fartherS.AddPeer(_right.Domain, System.Net.IPAddress.Loopback.ToString(),
                                 _rightS.Port, TcpTlsMode.StartTls, _right.IsOwnCertificate);

                _leftS.AddPeer(_right.Domain, System.Net.IPAddress.Loopback.ToString(),
                               _rightS.Port, TcpTlsMode.StartTls, _right.IsOwnCertificate);

                var alice  = await ConnectAsync(_left,  "alice");
                var juliet = await ConnectAsync(_right, "juliet");
                var third  = await ConnectAsync(farther,  "third");

                _left.GetAccount(alice.BareJid)!.SetRosterEntry(new RosterEntry(juliet.BareJid, null, "both"));
                farther.GetAccount(third.BareJid)!.SetRosterEntry(new RosterEntry(juliet.BareJid, null, "both"));
                _right.GetAccount(juliet.BareJid)!.SetRosterEntry(new RosterEntry(alice.BareJid, null, "both"));

                var atJuliet = new List<String>();
                var atAlice  = new List<String>();

                juliet.OnMessage += m => atJuliet.Add(m.Body ?? "");
                alice.OnMessage  += m => atAlice.Add(m.Body ?? "");

                // First farther, then left - the order is the core of the test.
                await third.SendMessageAsync(juliet.BareJid, "from farther");
                await WaitFor(() => atJuliet.Count > 0, "the message from farther");

                await alice.SendMessageAsync(juliet.BareJid, "from left");
                await WaitFor(() => atJuliet.Count > 1, "the message from left");

                // Now two return directions hang on right.
                await juliet.SendMessageAsync(alice.BareJid, "Back to left");

                await WaitFor(() => atAlice.Any(b => b == "Back to left"),
                              "the answer at Alice's and not at farther's");

                Assert.That(_rightS.BidirectionalDeliveryCount, Is.GreaterThan(0));

            }
            finally
            {
                await farther.DisposeAsync();
            }

        }

        #endregion

    }

}
