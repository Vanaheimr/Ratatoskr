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
    /// The base for all XMPP client tests: starts an <see cref="XMPPServer"/>
    /// per test and clears server and clients away again.
    /// </summary>
    public abstract class AXMPPTests
    {

        #region Data

        private readonly List<XMPPClient> _clients = [];

        /// <summary>
        /// The guard against swallowed programming errors - it hangs on
        /// <b>every</b> test and not on one of its own. Otherwise it would be
        /// worthless: where such an error occurs one does not know beforehand,
        /// and a single test guards only the route it goes itself.
        /// </summary>
        private readonly InternalErrorGuard _guard = new();

        /// <summary>
        /// The test server of the running test.
        /// </summary>
        protected XMPPServer Server { get; private set; } = null!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void StartServer()
        {

            _guard.Reset();

            Server = new XMPPServer();

            _guard.Watch(Server);

            Server.Start();

        }

        [TearDown]
        public async Task StopServer()
        {

            foreach (var client in _clients)
            {
                try { await client.DisposeAsync(); }
                catch { /* all the same in the teardown */ }
            }

            _clients.Clear();

            await Server.DisposeAsync();

            _guard.AssertClean();

        }

        #endregion

        /// <summary>
        /// Puts a further server under the same guard and gives it back - for
        /// tests that run servers of their own beside <see cref="Server"/>.
        /// </summary>
        protected XMPPServer Watched(XMPPServer server)
            => _guard.Watched(server);

        /// <summary>
        /// Tells the guard that this test triggers an internal error on
        /// purpose.
        /// </summary>
        protected void ExpectInternalErrors()
            => _guard.Expect();

        /// <summary>
        /// The internal errors reported in this test.
        /// </summary>
        protected IReadOnlyList<String> InternalErrors
            => _guard.Errors;


        /// <summary>
        /// Creates an account and connects a real <see cref="XMPPClient"/> to
        /// it. The client is cleared away automatically at the end of the test.
        /// </summary>
        /// <param name="localPart">Local part of the JID, e.g. "alice".</param>
        /// <param name="createAccount">Create the account if it does not exist yet.</param>
        /// <param name="keepalive">Keepalive interval; null switches keepalive off.</param>
        /// <param name="reconnectDelay">Waiting time before the first reconnect attempt.</param>
        /// <param name="streamManagement">
        /// Negotiate XEP-0198 stream management? <c>null</c> leaves the default
        /// value standing.
        /// </param>
        protected async Task<XMPPClient> ConnectClientAsync(String     localPart             = "alice",
                                                            Boolean    createAccount         = true,
                                                            TimeSpan?  keepalive             = null,
                                                            TimeSpan?  reconnectDelay        = null,
                                                            Boolean?   streamManagement      = null,
                                                            Int32      maxReconnectAttempts  = 20)
        {

            if (createAccount && Server.GetAccount($"{localPart}@{Server.Domain}") is null)
                Server.AddAccount(localPart);

            var client = CreateClient(localPart, keepalive, reconnectDelay,
                                      streamManagement:     streamManagement,
                                      maxReconnectAttempts: maxReconnectAttempts);

            await client.ConnectAsync();

            return client;

        }

        /// <summary>
        /// Creates a client against the test server that is not connected yet.
        /// </summary>
        /// <remarks>
        /// <paramref name="streamManagement"/> is deliberately
        /// <see cref="Nullable{T}"/> and not <c>false</c>: <c>null</c> leaves
        /// the default value of <see cref="XMPPConnection"/> standing, and with
        /// that the whole collection runs with what a caller without an opinion
        /// of their own gets. If a hard <c>false</c> stood here, not a single
        /// test would check the default value - a change would go through
        /// noiselessly.
        /// </remarks>
        protected XMPPClient CreateClient(String     localPart             = "alice",
                                          TimeSpan?  keepalive             = null,
                                          TimeSpan?  reconnectDelay        = null,
                                          String     password              = "pw",
                                          Boolean?   streamManagement      = null,
                                          Int32      maxReconnectAttempts  = 20)
        {

            var connection = new XMPPConnection(
                                 JID.Parse($"{localPart}@{Server.Domain}"),
                                 password,
                                 Server.Uri
                             ) {
                KeepaliveEnabled         = keepalive.HasValue,
                KeepaliveInterval        = keepalive ?? TimeSpan.FromSeconds(25),
                InitialReconnectDelay    = reconnectDelay ?? TimeSpan.FromMilliseconds(200),
                MaxReconnectAttempts     = maxReconnectAttempts,

                // The test server signs its certificate itself; no machine
                // trusts it. What is pinned is the fingerprint of precisely this
                // server - a check that accepts everything would let the tests
                // pass against a foreign counterpart too.
                ServerCertificateValidator = Server.IsOwnCertificate
            };

            if (streamManagement.HasValue)
                connection.StreamManagementEnabled = streamManagement.Value;

            var client = new XMPPClient(connection);
            _clients.Add(client);

            return client;

        }

        /// <summary>
        /// Enters a contact into the server-side roster of an account. Both
        /// accounts are created if they do not exist yet.
        /// </summary>
        /// <param name="localPart">Whose roster.</param>
        /// <param name="contact">Who is entered.</param>
        /// <param name="subscription">none, to, from or both.</param>
        protected void SetServerRoster(String localPart, String contact, String subscription)
        {

            var account = Server.GetAccount($"{localPart}@{Server.Domain}") ?? Server.AddAccount(localPart);

            if (Server.GetAccount($"{contact}@{Server.Domain}") is null)
                Server.AddAccount(contact);

            account.SetRosterEntry(new RosterEntry($"{contact}@{Server.Domain}", null, subscription));

        }

        /// <summary>
        /// Establishes the two-sided presence permission as it would exist after
        /// a complete subscription handshake (RFC 6121, section 3.1).
        /// </summary>
        protected void MakeContacts(String localPartA, String localPartB)
        {
            SetServerRoster(localPartA, localPartB, "both");
            SetServerRoster(localPartB, localPartA, "both");
        }

        /// <summary>
        /// Waits until the condition holds, and otherwise lets the test fail
        /// with an intelligible message.
        /// </summary>
        protected static async Task WaitFor(Func<Boolean>  condition,
                                            String         what,
                                            TimeSpan?      timeout = null)
        {

            var ok = await XMPPServer.WaitUntilAsync(condition, timeout);

            Assert.That(ok, Is.True, $"Timeout while waiting for: {what}");

        }

        /// <summary>
        /// Checks that the condition does <b>not</b> come about within the
        /// waiting time. The waiting time is deliberately short - a negative
        /// proof costs it in full in every case.
        /// </summary>
        protected static async Task WaitAgainst(Func<Boolean>  condition,
                                                String         what,
                                                TimeSpan?      timeout = null)
        {

            var happened = await XMPPServer.WaitUntilAsync(condition,
                                                           timeout ?? TimeSpan.FromSeconds(2));

            Assert.That(happened, Is.False, $"Should not have come about: {what}");

        }

        /// <summary>
        /// A connection setup that <b>is supposed to</b> fail — and gives back
        /// the error instead of letting it through upwards.
        /// </summary>
        /// <remarks>
        /// Since D31 <c>ConnectAsync</c> throws on a failed setup. Eleven tests
        /// until then checked an expected failure with a mere <c>await</c> and
        /// the assertions after it; that worked only because the call came back
        /// silently.
        ///
        /// This helper makes the expectation express instead of packing it into
        /// a <c>try</c> in every test separately: <b>here it has to fail.</b>
        /// With that the eleven tests have since checked one assertion more than
        /// before — that the failure arrives at the caller at all.
        /// </remarks>
        protected static async Task<Exception> FailingConnectAsync(XMPPClient client)
        {

            try
            {
                await client.ConnectAsync();
            }
            catch (Exception e)
            {
                return e;
            }

            Assert.Fail("The connection setup should have failed, but came through.");

            return null!;

        }

    }

}
