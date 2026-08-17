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
    /// Roster versioning under RFC 6121, section 2.6.
    /// </summary>
    /// <remarks>
    /// The roster is the largest thing that goes over the wire at the login,
    /// and it changes seldom. The versioning therefore spares it: the client
    /// names the version it has cached and gets an empty result if it still
    /// holds.
    ///
    /// The whole mechanism hangs on a fine point that easily comes out wrong:
    /// "unchanged" is a result with <b>no</b> <c>&lt;query/&gt;</c> at all. A
    /// <c>&lt;query/&gt;</c> without children, by contrast, means "your roster
    /// is empty" - whoever confuses the two deletes the user's contact list or
    /// shows them an outdated one.
    /// </remarks>
    [TestFixture]
    public class RosterVersioningTests : AXMPPTests
    {

        #region Helper functions

        /// <summary>
        /// A client without stream management - otherwise a reconnect would
        /// resume the old stream and would not fetch the roster afresh at all.
        /// </summary>
        private XMPPClient PlainClient(String localPart = "alice")
        {

            if (Server.GetAccount($"{localPart}@{Server.Domain}") is null)
                Server.AddAccount(localPart);

            return CreateClient(localPart, streamManagement: false);

        }

        /// <summary>Counts the logins of this client.</summary>
        private static Func<Int32> CountConnects(XMPPClient client)
        {

            var count = 0;

            client.Connection.OnStateChanged += (timestamp, sender, oldState, newState, ct) =>
            {
                if (newState == ConnectionState.Connected)
                    Interlocked.Increment(ref count);

                return Task.CompletedTask;

            };

            return () => Volatile.Read(ref count);

        }

        /// <summary>All roster requests the server has ever seen.</summary>
        private IEnumerable<String> RosterRequests
            => Server.AllReceived.Where(f => f.Contains("jabber:iq:roster", StringComparison.Ordinal) &&
                                             f.Contains("type='get'",       StringComparison.Ordinal));

        /// <summary>The roster results of the session opened last.</summary>
        private IEnumerable<String> RosterResults
            => Server.Sessions.Last().Sent
                     .Where(f => f.Contains("id='roster1'", StringComparison.Ordinal));

        #endregion


        #region TheFirstRequestBringsAVersion()

        /// <summary>
        /// The first request names an empty version, the result brings one
        /// along.
        /// </summary>
        /// <remarks>
        /// The empty <c>ver=''</c> is no placeholder but the announcement "I
        /// can do versioning but have nothing yet" (RFC 6121, section 2.6.1).
        /// Without it the server would not know that it should send a version
        /// along.
        /// </remarks>
        [Test]
        public async Task TheFirstRequestBringsAVersion()
        {

            SetServerRoster("alice", "bob", "both");

            var client = PlainClient();
            await client.ConnectAsync();

            Assert.Multiple(() =>
            {

                Assert.That(RosterRequests.Any(f => f.Contains("ver=''", StringComparison.Ordinal)),
                            Is.True,
                            "The first request has to carry an empty ver.");

                Assert.That(client.Connection.Roster.Version, Is.Not.Null.And.Not.Empty,
                            "The client has to take the version over from the result.");

                Assert.That(client.Connection.Roster.Version,
                            Is.EqualTo(Server.GetAccount(client.BareJid)!.RosterVersion));

                Assert.That(client.Connection.Roster.Items, Has.Count.EqualTo(1));

            });

        }

        #endregion

        #region AnUnchangedRoster_IsNotSentAgain()

        /// <summary>
        /// The heart of it: if the client knows the version already, the roster
        /// does not come a second time - and its cache stays filled all the
        /// same.
        /// </summary>
        /// <remarks>
        /// The second promise is the more important one. To read an empty
        /// result wrongly would mean showing the user an empty contact list at
        /// every second login.
        /// </remarks>
        [Test]
        public async Task AnUnchangedRoster_IsNotSentAgain()
        {

            SetServerRoster("alice", "bob", "both");

            var client = PlainClient();
            await client.ConnectAsync();

            var version     = client.Connection.Roster.Version;
            var logins = CountConnects(client);

            client.KillConnection();

            await WaitFor(() => logins() >= 1, "the second login");

            Assert.Multiple(() =>
            {

                Assert.That(RosterRequests.Any(f => f.Contains($"ver='{version}'", StringComparison.Ordinal)),
                            Is.True,
                            "The second request has to name the version it knows.");

                Assert.That(RosterResults.Any(f => f.Contains("jabber:iq:roster", StringComparison.Ordinal)),
                            Is.False,
                            "On a known version no <query/> may follow any more.");

                Assert.That(client.Connection.Roster.Items, Has.Count.EqualTo(1),
                            "The cache has to survive the empty notice.");

                Assert.That(client.Connection.Roster.Version, Is.EqualTo(version));

            });

        }

        #endregion

        #region AChangedRoster_ComesAgainWithANewVersion()

        /// <summary>
        /// The counter-check: if something has changed, the full roster comes
        /// and a new version.
        /// </summary>
        /// <remarks>
        /// Without it the collection would pass even if the server answered
        /// every second request empty - and the client would never get to see
        /// changes.
        /// </remarks>
        [Test]
        public async Task AChangedRoster_ComesAgainWithANewVersion()
        {

            SetServerRoster("alice", "bob", "both");

            var client = PlainClient();
            await client.ConnectAsync();

            var before      = client.Connection.Roster.Version;
            var logins = CountConnects(client);

            // While the client is away, a contact comes along.
            //
            // Wait for the tear first: otherwise the reconnect can get ahead of
            // the SetServerRoster, and then the client asks with the old
            // version after a roster that is still the old one - the test would
            // check something other than what it is meant to, and would fail
            // occasionally.
            client.KillConnection();

            await WaitFor(() => !Server.Sessions.Any(s => s.BareJid == client.BareJid),
                          "the end of the first session");

            SetServerRoster("alice", "carol", "both");

            await WaitFor(() => logins() >= 1, "the second login");
            await WaitFor(() => client.Connection.Roster.Items.Count == 2,
                          "the second contact in the roster");

            Assert.Multiple(() =>
            {

                Assert.That(client.Connection.Roster.Version, Is.Not.EqualTo(before),
                            "A change has to give a new version.");

                Assert.That(client.Connection.Roster.Version,
                            Is.EqualTo(Server.GetAccount(client.BareJid)!.RosterVersion));

            });

        }

        #endregion

        #region ARosterPush_CarriesTheNewVersion()

        /// <summary>
        /// The push carries the version too (RFC 6121, section 2.6.3).
        /// </summary>
        /// <remarks>
        /// Without it the client would stand on an outdated version again after
        /// every change and would fetch everything anew at the next login - the
        /// saving would be gone for exactly those who tend their roster.
        ///
        /// What is waited for is the <i>agreement</i> and not the first change.
        /// <c>AddContactAsync</c> is two things - a roster set and a
        /// <c>subscribe</c> - and both change the roster, so two pushes come.
        /// Whoever stops at the first and then compares against the server's
        /// state checks against a moving target and fails occasionally. The
        /// promise at issue is this one anyway: once it has settled, both sides
        /// agree.
        /// </remarks>
        [Test]
        public async Task ARosterPush_CarriesTheNewVersion()
        {

            var client = PlainClient();
            await client.ConnectAsync();

            var before = client.Connection.Roster.Version;

            await client.Connection.AddContactAsync($"carol@{Server.Domain}", "Carol");

            // Both conditions together, and that is no ornament: at the start
            // client and server both stand at the empty roster, so they already
            // agree. A waiting condition that looks only at agreement would be
            // met before anything has happened.
            await WaitFor(() => client.Connection.Roster.Version != before &&
                                client.Connection.Roster.Version ==
                                    Server.GetAccount(client.BareJid)!.RosterVersion,
                          "the version from the pushes");

            Assert.Multiple(() =>
            {

                Assert.That(client.Connection.Roster.Version, Is.Not.EqualTo(before),
                            "The new contact has to give a new version.");

                Assert.That(client.Connection.Roster.GetItem($"carol@{Server.Domain}"),
                            Is.Not.Null);

            });

        }

        #endregion

        #region WithoutTheFeature_NothingIsVersioned()

        /// <summary>
        /// If the server announces no versioning, the client does not ask after
        /// it either.
        /// </summary>
        /// <remarks>
        /// RFC 6121, section 2.6.1 demands exactly that. The reason lies in the
        /// other direction: a client that sends a <c>ver</c> unasked and then
        /// reads an empty result as "unchanged" would at some point take an
        /// empty roster for the current state with a server without versioning.
        /// </remarks>
        [Test]
        public async Task WithoutTheFeature_NothingIsVersioned()
        {

            Server.OfferRosterVersioning = false;

            SetServerRoster("alice", "bob", "both");

            var client = PlainClient();
            await client.ConnectAsync();

            Assert.Multiple(() =>
            {

                Assert.That(RosterRequests.Any(f => f.Contains("ver=", StringComparison.Ordinal)),
                            Is.False,
                            "Without the announcement no version may be asked for.");

                Assert.That(client.Connection.Roster.Version, Is.Null);

                Assert.That(client.Connection.Roster.Items, Has.Count.EqualTo(1),
                            "The roster comes in full all the same.");

            });

        }

        #endregion

        #region TheVersionFollowsTheContent()

        /// <summary>
        /// The version changes with every change - and only with one.
        /// </summary>
        /// <remarks>
        /// It is a hash over the content and no counter. Hence the last
        /// promise: if the roster goes back to A, the version is the old one
        /// again. That is as it should be - the cached state of a client that
        /// stored A does hold again.
        /// </remarks>
        [Test]
        public void TheVersionFollowsTheContent()
        {

            var account = Server.AddAccount("alice");

            var empty = account.RosterVersion;

            account.SetRosterEntry(new RosterEntry($"bob@{Server.Domain}", null, "both"));
            var withBob = account.RosterVersion;

            account.SetRosterEntry(new RosterEntry($"bob@{Server.Domain}", "Robert", "both"));
            var renamed = account.RosterVersion;

            account.SetRosterEntry(new RosterEntry($"bob@{Server.Domain}", "Robert", "to"));
            var otherPermission = account.RosterVersion;

            account.RemoveRosterEntry($"bob@{Server.Domain}");
            var emptyAgain = account.RosterVersion;

            Assert.Multiple(() =>
            {

                Assert.That(withBob,             Is.Not.EqualTo(empty),      "A new contact.");
                Assert.That(renamed,          Is.Not.EqualTo(withBob),    "A changed name.");
                Assert.That(otherPermission, Is.Not.EqualTo(renamed), "A changed permission.");

                Assert.That(emptyAgain, Is.EqualTo(empty),
                            "The same content gives the same version.");

            });

        }

        #endregion

    }

}
