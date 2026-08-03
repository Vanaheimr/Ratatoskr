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
    /// Accounts and rosters that outlive a server start.
    ///
    /// They lived in the memory of an <c>XMPPServer</c> instance and were gone
    /// when it ended. That pinned the server to tests - in service it would
    /// have had an empty account list after every restart.
    /// </summary>
    [TestFixture]
    public class AccountStoreTests
    {

        #region Data

        private String _directory = null!;
        private String _file = null!;
        private readonly InternalErrorGuard _guard = new();

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void OwnDirectory()
        {

            _directory = Path.Combine(Path.GetTempPath(),
                                        $"jabber-accounts-{Guid.NewGuid():N}");

            _file = Path.Combine(_directory, "accounts.json");

            _guard.Reset();

        }

        [TearDown]
        public void CleanUp()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch { /* never mind in the teardown */ }

            _guard.AssertClean();

        }

        #endregion


        #region MissingFile_IsAnEmptyStore()

        /// <summary>
        /// A store onto a file that does not exist yet is empty and not an
        /// error - otherwise every first start would have to be prepared by
        /// hand.
        /// </summary>
        [Test]
        public void MissingFile_IsAnEmptyStore()
        {

            var store = new FileAccountStore(_file);

            Assert.Multiple(() =>
            {
                Assert.That(store.Load(),          Is.Empty);
                Assert.That(File.Exists(_file),   Is.False,
                            "Merely reading must not create the file.");
            });

        }

        #endregion

        #region SavedAccount_ComesBack()

        /// <summary>
        /// The heart of it: a saved account can be read back in, and the login
        /// still works afterwards.
        /// </summary>
        [Test]
        public void SavedAccount_ComesBack()
        {

            new FileAccountStore(_file).Save(new XMPPAccount("alice@localhost", "secret"));

            var loaded = new FileAccountStore(_file).Load().ToList();

            Assert.That(loaded, Has.Count.EqualTo(1));

            Assert.Multiple(() =>
            {
                Assert.That(loaded[0].BareJid,                       Is.EqualTo("alice@localhost"));
                Assert.That(loaded[0].Credentials.Verify("secret"),  Is.True,
                            "After reading back in the login must still work.");
                Assert.That(loaded[0].Credentials.Verify("wrong"),  Is.False);
            });

        }

        #endregion

        #region ScramKeys_SurviveTheRoundTrip()

        /// <summary>
        /// The SCRAM keys too must come back unchanged - otherwise PLAIN would
        /// carry on working, but every SCRAM login would fail after a restart.
        /// </summary>
        /// <remarks>
        /// The case would be easy to miss: the suite checks logins mostly
        /// against freshly created accounts, and the salt round-tripping alone
        /// is not enough, because the keys are stored and not derived afresh.
        /// </remarks>
        [Test]
        public void ScramKeys_SurviveTheRoundTrip()
        {

            var original = new XMPPAccount("alice@localhost", "secret");

            new FileAccountStore(_file).Save(original);

            var loaded = new FileAccountStore(_file).Load().Single();

            Assert.Multiple(() =>
            {

                Assert.That(loaded.Credentials.Salt,            Is.EqualTo(original.Credentials.Salt));
                Assert.That(loaded.Credentials.IterationCount,  Is.EqualTo(original.Credentials.IterationCount));

                foreach (var mechanism in Enum.GetValues<SCRAMMechanism>())
                {
                    Assert.That(loaded.Credentials.KeysOf(mechanism).StoredKey,
                                Is.EqualTo(original.Credentials.KeysOf(mechanism).StoredKey),
                                $"StoredKey for {mechanism}.");
                    Assert.That(loaded.Credentials.KeysOf(mechanism).ServerKey,
                                Is.EqualTo(original.Credentials.KeysOf(mechanism).ServerKey),
                                $"ServerKey for {mechanism}.");
                }

            });

        }

        #endregion

        #region Roster_SurvivesTheRoundTrip()

        /// <summary>
        /// The roster belongs to the account and has to come along - subscription
        /// state and open request included.
        /// </summary>
        [Test]
        public void Roster_SurvivesTheRoundTrip()
        {

            var account = new XMPPAccount("alice@localhost", "secret");

            account.SetRosterEntry(new RosterEntry("bob@localhost",     "Bob",  "both"));
            account.SetRosterEntry(new RosterEntry("carol@localhost",   null,   "none", "subscribe"));

            new FileAccountStore(_file).Save(account);

            var loaded = new FileAccountStore(_file).Load().Single();

            Assert.Multiple(() =>
            {

                Assert.That(loaded.Roster, Has.Count.EqualTo(2));

                var bob = loaded.Roster.Single(e => e.Jid == "bob@localhost");
                Assert.That(bob.Name,          Is.EqualTo("Bob"));
                Assert.That(bob.Subscription,  Is.EqualTo("both"));
                Assert.That(bob.Ask,           Is.Null);

                var carol = loaded.Roster.Single(e => e.Jid == "carol@localhost");
                Assert.That(carol.Name,          Is.Null);
                Assert.That(carol.Subscription,  Is.EqualTo("none"));
                Assert.That(carol.Ask,           Is.EqualTo("subscribe"),
                            "An open request must not be lost across a restart.");

            });

        }

        #endregion

        #region PendingRequestsAndPreApprovals_SurviveTheRoundTrip()

        /// <summary>
        /// Kept requests (RFC 6121, section 3.1.3) and pre-approvals
        /// (section 3.4) belong to the account just as much.
        /// </summary>
        /// <remarks>
        /// The section demands that a request be delivered as soon as the
        /// contact logs in the next time - without a server restart in between
        /// being allowed to change anything. Were it lost in the process,
        /// "kept" would mean only "until the next restart", and the applicant
        /// would go on waiting for an answer nobody can give any more.
        ///
        /// What is kept is the complete stanza, not just the sender - which is
        /// why one with extended content is written here.
        /// </remarks>
        [Test]
        public void PendingRequestsAndPreApprovals_SurviveTheRoundTrip()
        {

            var account = new XMPPAccount("alice@localhost", "secret");

            account.SetRosterEntry(new RosterEntry("dave@localhost", null, "none",
                                                   Approved: true));

            account.RememberSubscriptionRequest(
                "carol@localhost",
                "<presence from='carol@localhost' to='alice@localhost' type='subscribe'>" +
                "<status>We know each other from the platform</status></presence>");

            new FileAccountStore(_file).Save(account);

            var loaded = new FileAccountStore(_file).Load().Single();

            Assert.Multiple(() =>
            {

                Assert.That(loaded.Roster.Single(e => e.Jid == "dave@localhost").Approved,
                            Is.True,
                            "A pre-approval must not be lost across a restart.");

                Assert.That(loaded.PendingSubscriptionRequests.Keys,
                            Is.EquivalentTo(new[] { "carol@localhost" }));

                Assert.That(loaded.PendingSubscriptionRequests["carol@localhost"],
                            Does.Contain("We know each other from the platform"),
                            "What is kept is the complete stanza, extended content and all.");

            });

        }

        #endregion

        #region TheOfflineStore_SurvivesTheRoundTrip()

        /// <summary>
        /// The offline store (RFC 6121, section 8.5.2.2.1) belongs to the
        /// account just as much - order and time of arrival included.
        /// </summary>
        /// <remarks>
        /// A sender whose message the server accepted, instead of refusing it
        /// with <c>&lt;service-unavailable/&gt;</c>, may rely on it arriving.
        /// Were it lost across a restart, the acceptance would be an empty
        /// promise - and nobody could notice the loss, because the sender
        /// already has their acknowledgement.
        ///
        /// The time belongs to it: without it the message handed on after a
        /// restart would carry a wrong XEP-0203 stamp or none at all, and would
        /// thereby claim to be from now.
        /// </remarks>
        [Test]
        public void TheOfflineStore_SurvivesTheRoundTrip()
        {

            var account    = new XMPPAccount("alice@localhost", "secret");
            var arrivedAt  = new DateTimeOffset(2026, 7, 29, 14, 5, 9, TimeSpan.Zero);

            account.StoreOfflineMessage("<message from='bob@localhost' to='alice@localhost' type='chat'>" +
                                        "<body>First</body></message>",
                                        arrivedAt);

            account.StoreOfflineMessage("<message from='bob@localhost' to='alice@localhost' type='chat'>" +
                                        "<body>Second</body></message>",
                                        arrivedAt.AddMinutes(3));

            new FileAccountStore(_file).Save(account);

            var loaded = new FileAccountStore(_file).Load().Single().OfflineMessages;

            Assert.Multiple(() =>
            {

                Assert.That(loaded,                Has.Count.EqualTo(2));
                Assert.That(loaded[0].Stanza,      Does.Contain("First"));
                Assert.That(loaded[1].Stanza,      Does.Contain("Second"),
                            "The order of arrival survives the restart.");
                Assert.That(loaded[0].StoredAt,    Is.EqualTo(arrivedAt));

            });

        }

        #endregion

        #region SavingTwice_DoesNotDuplicate()

        /// <summary>
        /// Saving the same account twice makes one entry, not a second -
        /// <c>Save</c> means create <b>or</b> carry forward.
        /// </summary>
        [Test]
        public void SavingTwice_DoesNotDuplicate()
        {

            var store    = new FileAccountStore(_file);
            var account  = new XMPPAccount("alice@localhost", "secret");

            store.Save(account);

            account.SetRosterEntry(new RosterEntry("bob@localhost"));
            store.Save(account);

            var loaded = store.Load().ToList();

            Assert.Multiple(() =>
            {
                Assert.That(loaded,             Has.Count.EqualTo(1));
                Assert.That(loaded[0].Roster,   Has.Count.EqualTo(1));
            });

        }

        #endregion

        #region DeletedAccount_IsGone()

        /// <summary>
        /// Deleting removes exactly one account, and an unknown JID is not an
        /// error.
        /// </summary>
        [Test]
        public void DeletedAccount_IsGone()
        {

            var store = new FileAccountStore(_file);

            store.Save(new XMPPAccount("alice@localhost", "secret"));
            store.Save(new XMPPAccount("bob@localhost",   "secret"));

            store.Delete("alice@localhost");
            store.Delete("doesnotexist@localhost");

            Assert.That(store.Load().Select(a => a.BareJid),
                        Is.EqualTo(new[] { "bob@localhost" }));

        }

        #endregion

        #region TheFile_ContainsNoPassword()

        /// <summary>
        /// The promise everything hangs on: the password is not in the file -
        /// neither in the clear nor as base64.
        /// </summary>
        [Test]
        public void TheFile_ContainsNoPassword()
        {

            const String password = "Pilcrow-Coelacanth-42";

            new FileAccountStore(_file).Save(new XMPPAccount("alice@localhost", password));

            var content = File.ReadAllText(_file);

            var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(password));

            Assert.Multiple(() =>
            {
                Assert.That(content, Does.Not.Contain(password), "Password in the clear in the file.");
                Assert.That(content, Does.Not.Contain(base64),   "Password as base64 in the file.");
            });

        }

        #endregion

        #region Server_LoadsExistingAccountsOnStart()

        /// <summary>
        /// And the server makes use of it: an account from an earlier instance
        /// is there again after the restart.
        /// </summary>
        [Test]
        public async Task Server_LoadsExistingAccountsOnStart()
        {

            await using (var first = _guard.Watched(new XMPPServer(accountStore: new FileAccountStore(_file),
                                                            useTLS:       false)))
            {
                first.AddAccount("alice", "secret");
            }

            await using var second = _guard.Watched(new XMPPServer(accountStore: new FileAccountStore(_file),
                                                            useTLS:       false));

            var account = second.GetAccount("alice@localhost");

            Assert.Multiple(() =>
            {
                Assert.That(account,                               Is.Not.Null);
                Assert.That(account!.Credentials.Verify("secret"), Is.True);
            });

        }

        #endregion

        #region Server_PersistsRosterChanges()

        /// <summary>
        /// Roster changes on the running server land in the store, without
        /// anyone having to save them expressly.
        /// </summary>
        /// <remarks>
        /// That is the real source of error in a change like this one: nobody
        /// forgets to save the creation of an account, but a roster change in
        /// the middle of the subscription handshake is another matter.
        /// </remarks>
        [Test]
        public async Task Server_PersistsRosterChanges()
        {

            await using var server = _guard.Watched(new XMPPServer(accountStore: new FileAccountStore(_file),
                                                           useTLS:       false));

            var account = server.AddAccount("alice", "secret");

            account.SetRosterEntry(new RosterEntry("bob@localhost", "Bob", "both"));

            var loaded = new FileAccountStore(_file).Load().Single();

            Assert.That(loaded.Roster.Select(e => e.Jid),
                        Is.EqualTo(new[] { "bob@localhost" }));

        }

        #endregion

        #region InMemoryStore_IsTheDefault()

        /// <summary>
        /// Without a store given, everything stays as it was: in memory, and
        /// gone when it ends.
        /// </summary>
        [Test]
        public async Task InMemoryStore_IsTheDefault()
        {

            await using var first = _guard.Watched(new XMPPServer(useTLS: false));
            first.AddAccount("alice", "secret");

            await using var second = _guard.Watched(new XMPPServer(useTLS: false));

            Assert.Multiple(() =>
            {
                Assert.That(first.GetAccount("alice@localhost"),  Is.Not.Null);
                Assert.That(second.GetAccount("alice@localhost"), Is.Null,
                            "A second server must not see the accounts of the first.");
            });

        }

        #endregion

    }

}
