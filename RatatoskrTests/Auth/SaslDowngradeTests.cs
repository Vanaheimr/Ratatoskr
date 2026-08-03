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
    /// The SASL downgrade: the server suddenly offers less than it did last
    /// time.
    /// </summary>
    /// <remarks>
    /// The client used to take whatever was announced. That is convenient, and
    /// for an honest server it is also right - only the announcement is not
    /// authenticated. A man in the middle strikes the SCRAM offers out of the
    /// features, PLAIN is what remains, and the client sends the password
    /// itself instead of a proof that it knows it.
    ///
    /// The attack does not need the first connect for that: the client comes
    /// back of its own accord after every break, and a break can be brought
    /// about. It is precisely this second login that is covered here - the test
    /// server plays the man in the middle by changing its mechanisms between
    /// the two connections.
    /// </remarks>
    [TestFixture]
    public class SaslDowngradeTests : AXMPPTests
    {

        #region Helper functions

        /// <summary>
        /// Counts how often the connection has gone into
        /// <see cref="ConnectionState.Connected"/> since the login.
        /// </summary>
        /// <remarks>
        /// Counted rather than asked: a <c>WaitFor(() =&gt; client.IsConnected)</c>
        /// would already be met before the break was even noticed, and would
        /// then prove nothing about the second connect.
        /// </remarks>
        private static Func<Int32> CountReconnects(XMPPClient client)
        {

            var count = 0;

            client.Connection.OnStateChanged += (oldState, newState) =>
            {
                if (newState == ConnectionState.Connected)
                    Interlocked.Increment(ref count);
            };

            return () => Volatile.Read(ref count);

        }

        /// <summary>Everything the server has ever seen by way of <c>&lt;auth/&gt;</c>.</summary>
        private Boolean SawAuthWith(String mechanism)

            => Server.AllReceived.Any(f => f.Contains($"mechanism='{mechanism}'", StringComparison.Ordinal));

        #endregion


        #region AWeakerServerOnTheSecondConnect_IsRefused()

        /// <summary>
        /// The heart of it: what ran over SCRAM the first time must not run
        /// over PLAIN the second.
        /// </summary>
        [Test]
        public async Task AWeakerServerOnTheSecondConnect_IsRefused()
        {

            var client = await ConnectClientAsync();

            Assert.That(client.Connection.PinnedSaslMechanism, Is.EqualTo("SCRAM-SHA-256"),
                        "Precondition: the first login must have run over SCRAM-SHA-256.");

            var errors = new List<String>();
            client.OnError += e => errors.Add(e);

            // From now on the server offers nothing but PLAIN.
            Server.OfferedSaslMechanisms.Clear();
            Server.OfferedSaslMechanisms.Add("PLAIN");

            client.KillConnection();

            await WaitFor(() => errors.Count > 0, "the refusal of the downgrade");

            Assert.Multiple(() =>
            {

                Assert.That(client.IsConnected, Is.False,
                            "After a downgrade no connection may come about.");

                Assert.That(errors.Any(e => e.Contains("Downgrade", StringComparison.OrdinalIgnoreCase)),
                            Is.True,
                            $"The reason has to be named. Reported was: {String.Join(" | ", errors)}");

                Assert.That(SawAuthWith("PLAIN"), Is.False,
                            "No <auth/> at all may have gone out over PLAIN.");

            });

        }

        #endregion

        #region TheRefusalHappensBeforeThePasswordGoesOut()

        /// <summary>
        /// And before the first frame goes out, at that - with PLAIN the
        /// password sits in exactly this <c>&lt;auth/&gt;</c>.
        /// </summary>
        /// <remarks>
        /// A check that first looks at the server's answer would come too late:
        /// the man in the middle would have what he was after, and breaking off
        /// the login afterwards would not take it back off him.
        /// </remarks>
        [Test]
        public async Task TheRefusalHappensBeforeThePasswordGoesOut()
        {

            const String password = "Pilcrow-Coelacanth-42";

            Server.AddAccount("alice", password);

            var client = CreateClient("alice", password: password);
            await client.ConnectAsync();

            var errors = new List<String>();
            client.OnError += e => errors.Add(e);

            Server.OfferedSaslMechanisms.Clear();
            Server.OfferedSaslMechanisms.Add("PLAIN");

            client.KillConnection();

            await WaitFor(() => errors.Count > 0, "the refusal of the downgrade");

            var base64 = Convert.ToBase64String(
                             System.Text.Encoding.UTF8.GetBytes($"\0alice\0{password}"));

            Assert.Multiple(() =>
            {

                Assert.That(Server.AllReceived.Any(f => f.Contains(password, StringComparison.Ordinal)),
                            Is.False,
                            "The password stood in the clear in a frame.");

                Assert.That(Server.AllReceived.Any(f => f.Contains(base64, StringComparison.Ordinal)),
                            Is.False,
                            "The password went out as a PLAIN payload before the downgrade was noticed.");

            });

        }

        #endregion

        #region AnUnchangedServerOnTheSecondConnect_IsAccepted()

        /// <summary>
        /// The counter-check: if the offer stays the same, the client comes
        /// back quite normally.
        /// </summary>
        /// <remarks>
        /// Without it the collection would pass even if the lower bound refused
        /// every second connect.
        /// </remarks>
        [Test]
        public async Task AnUnchangedServerOnTheSecondConnect_IsAccepted()
        {

            var client       = await ConnectClientAsync();
            var reconnects   = CountReconnects(client);

            var errors = new List<String>();
            client.OnError += e => errors.Add(e);

            client.KillConnection();

            await WaitFor(() => reconnects() >= 1, "the second login");

            Assert.Multiple(() =>
            {

                Assert.That(client.IsConnected, Is.True);

                Assert.That(client.Connection.PinnedSaslMechanism, Is.EqualTo("SCRAM-SHA-256"));

                Assert.That(errors, Is.Empty,
                            $"Reported was: {String.Join(" | ", errors)}");

            });

        }

        #endregion

        #region AStrongerServerOnTheSecondConnect_IsAccepted()

        /// <summary>
        /// Upwards the lower bound is open: a server that adds SCRAM-SHA-256
        /// must not fail because SCRAM-SHA-1 was in use last time.
        /// </summary>
        /// <remarks>
        /// A pinning that checks for equality instead of for strength would be
        /// shorter to write and would fail here.
        /// </remarks>
        [Test]
        public async Task AStrongerServerOnTheSecondConnect_IsAccepted()
        {

            Server.OfferedSaslMechanisms.Clear();
            Server.OfferedSaslMechanisms.Add("SCRAM-SHA-1");

            var client       = await ConnectClientAsync();
            var reconnects   = CountReconnects(client);

            Assert.That(client.Connection.PinnedSaslMechanism, Is.EqualTo("SCRAM-SHA-1"),
                        "Precondition: the first login must have run over SCRAM-SHA-1.");

            Server.OfferedSaslMechanisms.Add("SCRAM-SHA-256");

            client.KillConnection();

            await WaitFor(() => reconnects() >= 1, "the second login");

            Assert.Multiple(() =>
            {

                Assert.That(client.IsConnected, Is.True);

                Assert.That(client.Connection.PinnedSaslMechanism, Is.EqualTo("SCRAM-SHA-256"),
                            "The pinning has to follow the stronger offer.");

                Assert.That(SawAuthWith("SCRAM-SHA-256"), Is.True);

            });

        }

        #endregion

        #region TheMinimumHoldsOnTheVeryFirstConnect()

        /// <summary>
        /// The lower bound that was set takes effect without any previous
        /// login.
        /// </summary>
        /// <remarks>
        /// The pinning is a trust on first use and by its nature does not
        /// protect the first connect. Whoever knows what their server can do
        /// says so - and needs no first time.
        /// </remarks>
        [Test]
        public async Task TheMinimumHoldsOnTheVeryFirstConnect()
        {

            Server.OfferedSaslMechanisms.Clear();
            Server.OfferedSaslMechanisms.Add("PLAIN");

            Server.AddAccount("alice");

            var client = CreateClient("alice");
            client.Connection.MaxReconnectAttempts   = 0;
            client.Connection.MinimumSaslMechanism   = "SCRAM-SHA-256";

            var errors = new List<String>();
            client.OnError += e => errors.Add(e);

            await FailingConnectAsync(client);

            Assert.Multiple(() =>
            {

                Assert.That(client.IsConnected, Is.False,
                            "Below the demanded lower bound no connection may come about.");

                Assert.That(SawAuthWith("PLAIN"), Is.False);

                Assert.That(errors, Is.Not.Empty);

            });

        }

        #endregion

        #region TheMinimumIsMetByAStrongerServer()

        /// <summary>
        /// And the counter-check to it: if the server meets it, it changes
        /// nothing.
        /// </summary>
        [Test]
        public async Task TheMinimumIsMetByAStrongerServer()
        {

            Server.AddAccount("alice");

            var client = CreateClient("alice");
            client.Connection.MinimumSaslMechanism = "SCRAM-SHA-1";

            await client.ConnectAsync();

            Assert.Multiple(() =>
            {
                Assert.That(client.IsConnected, Is.True);
                Assert.That(client.Connection.PinnedSaslMechanism, Is.EqualTo("SCRAM-SHA-256"));
            });

        }

        #endregion

        #region AnUnknownMinimum_IsRefusedAtTheSetter()

        /// <summary>
        /// A mechanism name the client does not know is refused when it is set.
        /// </summary>
        /// <remarks>
        /// Otherwise the typo would be the most dangerous input of all: an
        /// unknown name has strength 0, and a lower bound of 0 demands nothing
        /// at all. The caller would silently get the opposite of what they
        /// wrote down.
        /// </remarks>
        [Test]
        public void AnUnknownMinimum_IsRefusedAtTheSetter()
        {

            var client = CreateClient("alice");

            Assert.That(() => client.Connection.MinimumSaslMechanism = "SCRAM-SHA-512",
                        Throws.TypeOf<ArgumentException>());

        }

        #endregion

    }

}
