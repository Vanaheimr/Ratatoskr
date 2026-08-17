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

using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// RFC 6120, section 6.4.4: if the client breaks off the SASL negotiation
    /// with <c>&lt;abort/&gt;</c>, the server answers with
    /// <c>&lt;failure&gt;&lt;aborted/&gt;&lt;/failure&gt;</c> — and the stream
    /// stays up.
    /// </summary>
    /// <remarks>
    /// Since D26 an <c>&lt;abort/&gt;</c> ended the stream with
    /// <c>&lt;unsupported-stanza-type/&gt;</c>. Word for word that was not
    /// wrong — the server did not support the element — but it is the worse of
    /// two answers: breaking off is an <b>intended</b> step of the negotiation,
    /// not a protocol violation. Whoever answers it with the end of the stream
    /// forces the client into a new connection for something the RFC expressly
    /// provides for within the existing one.
    ///
    /// Checked through a raw <see cref="ClientWebSocket"/> and not through
    /// <see cref="XMPPClient"/>: breaking off belongs <b>in the middle</b> of
    /// the negotiation, and there the real client holds a conversation of its
    /// own. Only by hand can a half-begun SCRAM exchange be brought about at
    /// all.
    /// </remarks>
    [TestFixture]
    public class SaslAbortTests : AXMPPTests
    {

        #region Raw client

        /// <summary>
        /// A client for the negotiation phase — with no opinion of its own
        /// about what to do next.
        /// </summary>
        private sealed class RawClient : IAsyncDisposable
        {

            private readonly ClientWebSocket _socket = new();

            public List<String> Received { get; } = [];

            public async Task ConnectAsync(XMPPServer server)
            {

                _socket.Options.AddSubProtocol("xmpp");
                _socket.Options.RemoteCertificateValidationCallback = server.IsOwnCertificate;

                await _socket.ConnectAsync(new Uri(server.Uri), CancellationToken.None);

                _ = ReadAsync();

            }

            public async Task SendAsync(String frame)
                => await _socket.SendAsync(Encoding.UTF8.GetBytes(frame),
                                           WebSocketMessageType.Text, true, CancellationToken.None);

            private async Task ReadAsync()
            {

                var buffer = new Byte[16384];

                try
                {
                    while (_socket.State == WebSocketState.Open)
                    {

                        var result = await _socket.ReceiveAsync(buffer, CancellationToken.None);

                        if (result.MessageType == WebSocketMessageType.Close)
                            break;

                        lock (Received)
                            Received.Add(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    }
                }
                catch (Exception)
                {
                    // Connection closed - depending on the test the expected outcome.
                }

            }

            public Boolean Saw(String text)
            {
                lock (Received)
                    return Received.Any(f => f.Contains(text, StringComparison.Ordinal));
            }

            public Int32 Count
            {
                get { lock (Received) return Received.Count; }
            }

            public Boolean IsOpen
                => _socket.State == WebSocketState.Open;

            public ValueTask DisposeAsync()
            {
                try { _socket.Dispose(); } catch { /* never mind */ }
                return ValueTask.CompletedTask;
            }

        }

        #endregion

        #region Helper functions

        private const String SaslNamespace = "urn:ietf:params:xml:ns:xmpp-sasl";

        private readonly List<RawClient> _clients = [];

        [TearDown]
        public async Task DisposeRawClients()
        {

            foreach (var c in _clients)
                await c.DisposeAsync();

            _clients.Clear();

        }

        /// <summary>
        /// A connected raw client with an opened stream.
        /// </summary>
        private async Task<RawClient> OpenedAsync()
        {

            Server.AddAccount("alice");

            var client = new RawClient();
            _clients.Add(client);

            await client.ConnectAsync(Server);

            await client.SendAsync(
                      "<open xmlns='urn:ietf:params:xml:ns:xmpp-framing' " +
                      $"to='{Server.Domain}' version='1.0'/>");

            await WaitFor(() => client.Saw("mechanisms"), "the features of the server");

            return client;

        }

        /// <summary>
        /// The content of the last received element of this name.
        /// </summary>
        private static String ContentOf(RawClient client, String element)
        {

            lock (client.Received)
            {

                var frame = client.Received.Last(f => f.Contains($"<{element}", StringComparison.Ordinal));

                return Regex.Match(frame, $@"<{element}[^>]*>([^<]*)</{element}>").Groups[1].Value;

            }

        }

        #endregion


        #region AnAbort_IsAnsweredWithAborted()

        /// <summary>
        /// The heart of it: <c>&lt;abort/&gt;</c> is followed by
        /// <c>&lt;failure&gt;&lt;aborted/&gt;&lt;/failure&gt;</c> and by no
        /// stream error.
        /// </summary>
        [Test]
        public async Task AnAbort_IsAnsweredWithAborted()
        {

            var client = await OpenedAsync();

            var scram = new SCRAMAuthenticator("alice", "pw", SCRAMMechanism.ScramSha256);

            await client.SendAsync(
                      $"<auth xmlns='{SaslNamespace}' mechanism='SCRAM-SHA-256'>" +
                      $"{scram.CreateClientFirstMessage()}</auth>");

            await WaitFor(() => client.Saw("<challenge"), "the challenge of the server");

            await client.SendAsync($"<abort xmlns='{SaslNamespace}'/>");

            await WaitFor(() => client.Saw("<aborted"), "the answer to the abort");

            Assert.Multiple(() =>
            {

                Assert.That(client.Saw("<failure"), Is.True,
                            "The abort is answered with a SASL failure.");

                Assert.That(client.Saw("unsupported-stanza-type"), Is.False,
                            "And expressly not with a stream error.");

                Assert.That(client.IsOpen, Is.True,
                            "The abort ends the negotiation, not the stream.");

            });

        }

        #endregion

        #region AnAbort_DiscardsTheHalfFinishedExchange()

        /// <summary>
        /// The broken-off SCRAM exchange is gone — a <c>&lt;response/&gt;</c>
        /// handed in afterwards belongs to nothing any more.
        /// </summary>
        /// <remarks>
        /// That is what breaking off actually amounts to. Were the half
        /// negotiation left lying about, it could still be carried through with
        /// an answer pushed in later — the abort would then be a courtesy and
        /// not a statement.
        ///
        /// The answer pushed in later is therefore a <b>valid</b> one, built
        /// with the client's real <see cref="SCRAMAuthenticator"/>. That is the
        /// heart of this test and was wrong at first: with a nonsensical answer
        /// <c>not-authorized</c> comes back whether the exchange was discarded
        /// or not — both worlds give the same answer, and the test checked
        /// nothing. Only an answer that <b>would go through</b> tells the cases
        /// apart: it leads either to <c>&lt;success/&gt;</c> or to a refusal.
        /// </remarks>
        [Test]
        public async Task AnAbort_DiscardsTheHalfFinishedExchange()
        {

            var client = await OpenedAsync();

            var scram = new SCRAMAuthenticator("alice", "pw", SCRAMMechanism.ScramSha256);

            await client.SendAsync(
                      $"<auth xmlns='{SaslNamespace}' mechanism='SCRAM-SHA-256'>" +
                      $"{scram.CreateClientFirstMessage()}</auth>");

            await WaitFor(() => client.Saw("<challenge"), "the challenge of the server");

            var challenge = ContentOf(client, "challenge");

            await client.SendAsync($"<abort xmlns='{SaslNamespace}'/>");

            await WaitFor(() => client.Saw("<aborted"), "the answer to the abort");

            var before = client.Count;

            // This answer would have been right - had the abort not come in
            // between.
            await client.SendAsync(
                      $"<response xmlns='{SaslNamespace}'>" +
                      $"{scram.ProcessServerFirstMessage(challenge)}</response>");

            await WaitFor(() => client.Count > before, "the answer to the belated response");

            Assert.Multiple(() =>
            {

                Assert.That(client.Saw("<success"), Is.False,
                            "The broken-off exchange must not be carried " +
                            "through after the fact.");

                Assert.That(client.Saw("not-authorized"), Is.True,
                            "A response without a running exchange belongs to no negotiation.");

            });

        }

        #endregion

        #region AfterAnAbort_ANewNegotiationStillWorks()

        /// <summary>
        /// And the stream is still good for something afterwards: a second run
        /// leads to success.
        /// </summary>
        /// <remarks>
        /// The counter-check to the heart of it. "No stream error" on its own
        /// would also be met if the server accepted nothing at all after the
        /// abort — the stream would then be formally open and practically dead.
        /// </remarks>
        [Test]
        public async Task AfterAnAbort_ANewNegotiationStillWorks()
        {

            Server.OfferedSaslMechanisms.Clear();
            Server.OfferedSaslMechanisms.Add("PLAIN");

            var client = await OpenedAsync();

            await client.SendAsync($"<abort xmlns='{SaslNamespace}'/>");

            await WaitFor(() => client.Saw("<aborted"), "the answer to the abort");

            var secret = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0alice\0pw"));

            await client.SendAsync(
                      $"<auth xmlns='{SaslNamespace}' mechanism='PLAIN'>{secret}</auth>");

            await WaitFor(() => client.Saw("<success"), "the login on the second run");

        }

        #endregion

    }

}
