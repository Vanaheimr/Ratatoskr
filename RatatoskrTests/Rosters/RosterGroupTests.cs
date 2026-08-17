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
    /// Roster groups under RFC 6121, section 2.1.2.4.
    /// </summary>
    /// <remarks>
    /// <b>The client could always do them, the server never took them.</b>
    /// <c>RosterStanzaBuilder.SetItem</c> sends <c>&lt;group/&gt;</c> along,
    /// <c>RosterItem.Groups</c> keeps them, the console sorts by them when it
    /// displays - and the server read the <c>&lt;item/&gt;</c> only as far as
    /// its attributes. The group arrived, was discarded in silence, and the
    /// push brought the same entry back without it. Since a push
    /// <i>replaces</i> the groups of an entry, it thereby disappeared at the
    /// client too: what the person had set was gone the blink of an eye later,
    /// without anything looking like a fault.
    ///
    /// The comment in the roster handling claimed all along that a set changed
    /// "name and groups".
    /// </remarks>
    [TestFixture]
    public class RosterGroupTests : AXMPPTests
    {

        #region Helper functions

        // Both may ask before the entry exists: they stand in WaitFor
        // conditions, and a condition that throws instead of being false does
        // not wait - it fails at once.

        /// <summary>The groups the server keeps for a contact.</summary>
        private IReadOnlyList<String> ServerGroupsOf(XMPPClient client, String contact)
            => Server.GetAccount(client.BareJid.ToString())
                    ?.Roster.FirstOrDefault(e => e.Jid == $"{contact}@{Server.Domain}")
                    ?.Groups ?? [];

        /// <summary>The groups the client keeps for a contact.</summary>
        private static IReadOnlyList<String> ClientGroupsOf(XMPPClient client, String jid)
            => client.Connection.Roster.Items.FirstOrDefault(i => i.Jid == jid)?.Groups ?? [];

        #endregion


        #region AGroupSurvivesTheRoundTrip()

        /// <summary>
        /// A group the client sets stands at the server afterwards - and comes
        /// back in the push.
        /// </summary>
        [Test]
        public async Task AGroupSurvivesTheRoundTrip()
        {

            var alice = await ConnectClientAsync("alice");

            await alice.AddContactAsync(JID.Parse($"bob@{Server.Domain}"), "Bob", ["Friends"]);

            await WaitFor(() => ServerGroupsOf(alice, "bob").Count > 0,
                          "the group at the server");

            await WaitFor(() => ClientGroupsOf(alice, $"bob@{Server.Domain}").Count > 0,
                          "the group in the push");

            Assert.Multiple(() =>
            {
                Assert.That(ServerGroupsOf(alice, "bob"),                       Is.EqualTo(new[] { "Friends" }));
                Assert.That(ClientGroupsOf(alice, $"bob@{Server.Domain}"),      Is.EqualTo(new[] { "Friends" }));
            });

        }

        #endregion

        #region TwoGroups_BothSurvive()

        /// <summary>
        /// A contact may stand in several groups.
        /// </summary>
        [Test]
        public async Task TwoGroups_BothSurvive()
        {

            var alice = await ConnectClientAsync("alice");

            await alice.AddContactAsync(JID.Parse($"bob@{Server.Domain}"), "Bob", ["Friends", "Work"]);

            await WaitFor(() => ServerGroupsOf(alice, "bob").Count > 1, "both groups");

            Assert.That(ServerGroupsOf(alice, "bob"), Is.EquivalentTo(new[] { "Friends", "Work" }));

        }

        #endregion

        #region ASetWithoutGroups_TakesThemAway()

        /// <summary>
        /// RFC 6121, section 2.3.2: the groups of a set replace the previous
        /// ones in full.
        /// </summary>
        /// <remarks>
        /// A set without a <c>&lt;group/&gt;</c> is therefore no omission but
        /// the instruction that the contact stands in no group any more.
        /// Whoever read that as "nothing given, so nothing changed" could never
        /// be rid of a group again.
        /// </remarks>
        [Test]
        public async Task ASetWithoutGroups_TakesThemAway()
        {

            var alice = await ConnectClientAsync("alice");

            await alice.AddContactAsync(JID.Parse($"bob@{Server.Domain}"), "Bob", ["Friends"]);

            await WaitFor(() => ServerGroupsOf(alice, "bob").Count > 0, "the group");

            await alice.AddContactAsync(JID.Parse($"bob@{Server.Domain}"), "Bob");

            await WaitFor(() => ServerGroupsOf(alice, "bob").Count == 0 &&
                                ClientGroupsOf(alice, $"bob@{Server.Domain}").Count == 0,
                          "the emptied group list on both sides");

            Assert.That(ClientGroupsOf(alice, $"bob@{Server.Domain}"), Is.Empty,
                        "And the client hears the same.");

        }

        #endregion

        #region AGroupChange_ChangesTheRosterVersion()

        /// <summary>
        /// Regrouping changes the version of the roster.
        /// </summary>
        /// <remarks>
        /// <b>That is the part nothing else would show.</b> If the version
        /// stayed the same, a client that had cached it would get an empty
        /// result at the next login - and would keep the old arrangement for
        /// ever. The fault would show up only days later and on another device.
        /// </remarks>
        [Test]
        public async Task AGroupChange_ChangesTheRosterVersion()
        {

            var alice = await ConnectClientAsync("alice");
            var account = Server.GetAccount(alice.BareJid.ToString())!;

            await alice.AddContactAsync(JID.Parse($"bob@{Server.Domain}"), "Bob", ["Friends"]);

            await WaitFor(() => ServerGroupsOf(alice, "bob").Count > 0, "the first group");

            var before = account.RosterVersion;

            await alice.AddContactAsync(JID.Parse($"bob@{Server.Domain}"), "Bob", ["Work"]);

            await WaitFor(() => ServerGroupsOf(alice, "bob").Contains("Work"), "the second group");

            Assert.That(account.RosterVersion, Is.Not.EqualTo(before),
                        "A change that does not change the version never reaches the client again.");

        }

        #endregion

        #region TheRosterRequest_BringsTheGroups()

        /// <summary>
        /// The fetch carries the groups too and not only the push.
        /// </summary>
        /// <remarks>
        /// Both build the same place now. Two accounts of the same entry
        /// otherwise drift apart, and the versioning makes that a lasting
        /// matter: the client takes the state from the push for the whole and
        /// does not ask again.
        /// </remarks>
        [Test]
        public async Task TheRosterRequest_BringsTheGroups()
        {

            Server.AddAccount("alice").SetRosterEntry(
                new RosterEntry($"bob@{Server.Domain}", "Bob", "both", null, false, ["Friends"]));

            var alice = await ConnectClientAsync("alice", createAccount: false);

            await WaitFor(() => alice.Connection.Roster.Items.Count > 0, "the roster");

            Assert.That(ClientGroupsOf(alice, $"bob@{Server.Domain}"), Is.EqualTo(new[] { "Friends" }));

        }

        #endregion

        #region AGroupWithSpecialCharacters_ArrivesAsItWasWritten()

        /// <summary>
        /// A group name with XML special characters survives both directions.
        /// </summary>
        /// <remarks>
        /// The server reads the frame here with a pattern and not with an XML
        /// reader - then it has to undo the escaping itself. <b>The ampersand
        /// last:</b> whoever replaces it first turns a text that is about a
        /// character into the character itself.
        /// </remarks>
        [Test]
        public async Task AGroupWithSpecialCharacters_ArrivesAsItWasWritten()
        {

            var alice = await ConnectClientAsync("alice");

            // The second name is the real touchstone: it contains the text
            // "&lt;" and means it literally. Whoever replaces the ampersand
            // first while unescaping turns it into a "<" - a text that is about
            // a character becomes the character.
            await alice.AddContactAsync(JID.Parse($"bob@{Server.Domain}"), "Bob",
                                        ["Tom & Jerry <old>", "A&lt;B"]);

            // Wait for both and not only for the first: the push comes after
            // the storing, and a test that does not wait for it measures the
            // speed of the machine.
            await WaitFor(() => ServerGroupsOf(alice, "bob").Count > 1 &&
                                ClientGroupsOf(alice, $"bob@{Server.Domain}").Count > 1,
                          "both groups on both sides");

            Assert.Multiple(() =>
            {

                Assert.That(ServerGroupsOf(alice, "bob"),
                            Is.EqualTo(new[] { "Tom & Jerry <old>", "A&lt;B" }));

                Assert.That(ClientGroupsOf(alice, $"bob@{Server.Domain}"),
                            Is.EqualTo(new[] { "Tom & Jerry <old>", "A&lt;B" }),
                            "And the way back unescapes them just the same.");

            });

        }

        #endregion

    }

}
