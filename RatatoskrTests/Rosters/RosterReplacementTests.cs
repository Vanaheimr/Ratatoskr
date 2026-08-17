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
    /// The result of a roster request is the complete roster and not an
    /// addition (RFC 6121, section 2.1.4).
    /// </summary>
    /// <remarks>
    /// The difference becomes visible at exactly one place, and that one is
    /// common in everyday use: a contact is deleted on another device while
    /// this one is logged off. At the next login the server no longer sends
    /// them - but whoever merely works the result in does not take them out
    /// either. The contact comes back and cannot be got rid of from this device
    /// any more.
    ///
    /// In running use that never shows, because then a push with
    /// <c>subscription='remove'</c> comes and the entry disappears properly.
    /// </remarks>
    [TestFixture]
    public class RosterReplacementTests : AXMPPTests
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

        /// <summary>
        /// Tears the connection down, waits for the end of the session, carries
        /// out the change and waits for the new login.
        /// </summary>
        private async Task ReconnectAround(XMPPClient client, Action change)
        {

            var logins = CountConnects(client);

            client.KillConnection();

            // Wait for the end first: otherwise the reconnect can get ahead of
            // the change, and the test would check something other than what it
            // is meant to.
            await WaitFor(() => !Server.Sessions.Any(s => JID.AreEqual(s.BareJid, client.BareJid.ToString())),
                          "the end of the first session");

            change();

            await WaitFor(() => logins() >= 1, "the second login");

        }

        #endregion


        #region AContactRemovedWhileOffline_IsGoneAfterReconnect()

        /// <summary>
        /// The heart of it: what the server no longer keeps disappears at the
        /// client too.
        /// </summary>
        [Test]
        public async Task AContactRemovedWhileOffline_IsGoneAfterReconnect()
        {

            SetServerRoster("alice", "bob",   "both");
            SetServerRoster("alice", "carol", "both");

            var client = PlainClient();
            await client.ConnectAsync();

            Assert.That(client.Connection.Roster.Items, Has.Count.EqualTo(2),
                        "Precondition: both contacts are there.");

            var removed = new List<String>();
            client.Connection.Roster.OnItemRemoved += (timestamp, sender, jid, ct) => { removed.Add(jid.ToString()); return Task.CompletedTask; };

            await ReconnectAround(client,
                                  () => Server.GetAccount(client.BareJid.ToString())!
                                              .RemoveRosterEntry($"bob@{Server.Domain}"));

            await WaitFor(() => client.Connection.Roster.Items.Count == 1,
                          "the disappearance of the deleted contact");

            Assert.Multiple(() =>
            {

                Assert.That(client.Connection.Roster.GetItem(JID.Parse($"bob@{Server.Domain}")), Is.Null,
                            "The deleted contact must not come back.");

                Assert.That(client.Connection.Roster.GetItem(JID.Parse($"carol@{Server.Domain}")), Is.Not.Null,
                            "The remaining one has to stay.");

                Assert.That(removed, Does.Contain($"bob@{Server.Domain}"),
                            "Whoever keeps a display has to learn of the removal.");

            });

        }

        #endregion

        #region AnUnchangedContact_SurvivesTheReconnect()

        /// <summary>
        /// The counter-check: without a change everything stays put.
        /// </summary>
        /// <remarks>
        /// Without it the collection would pass even if everything were simply
        /// deleted at the login.
        /// </remarks>
        [Test]
        public async Task AnUnchangedContact_SurvivesTheReconnect()
        {

            SetServerRoster("alice", "bob", "both");

            var client = PlainClient();
            await client.ConnectAsync();

            // A change that touches the roster, so that the result really comes
            // a second time instead of being waved off as "unchanged" -
            // otherwise the test would only check the versioning.
            await ReconnectAround(client,
                                  () => SetServerRoster("alice", "carol", "both"));

            await WaitFor(() => client.Connection.Roster.Items.Count == 2,
                          "the second contact");

            Assert.That(client.Connection.Roster.GetItem(JID.Parse($"bob@{Server.Domain}")), Is.Not.Null,
                        "The unchanged contact must not be lost in the process.");

        }

        #endregion

        #region ARosterPush_DoesNotReplaceTheWholeRoster()

        /// <summary>
        /// A push carries only the changed entries and must not touch the rest
        /// of the roster.
        /// </summary>
        /// <remarks>
        /// That is the counter-check to the replacing, and it is the sharper
        /// one: whoever treats the push with the same procedure as the result
        /// deletes the whole rest of the roster at every single change. The
        /// fault would be an obvious simplification - both look the same on the
        /// wire, a <c>&lt;query/&gt;</c> with an <c>&lt;item/&gt;</c>.
        /// </remarks>
        [Test]
        public async Task ARosterPush_DoesNotReplaceTheWholeRoster()
        {

            SetServerRoster("alice", "bob",   "both");
            SetServerRoster("alice", "carol", "both");

            var client = PlainClient();
            await client.ConnectAsync();

            Assert.That(client.Connection.Roster.Items, Has.Count.EqualTo(2),
                        "Precondition: both contacts are there.");

            // A single entry changes - the server answers with a push carrying
            // exactly this one element.
            //
            // The change has to come from the client: an intervention past the
            // account triggers no push, and the test would then only check its
            // own patience.
            await client.Connection.SendRawAsync(
                      RosterStanzaBuilder.SetItem(JID.Parse($"bob@{Server.Domain}"), "Robert"));

            await WaitFor(() => client.Connection.Roster.GetItem(JID.Parse($"bob@{Server.Domain}"))?.Name == "Robert",
                          "the renamed contact");

            Assert.That(client.Connection.Roster.GetItem(JID.Parse($"carol@{Server.Domain}")), Is.Not.Null,
                        "A push about Bob must not delete Carol.");

        }

        #endregion

        #region ReplaceAll_UpdatesKeepsAndRemoves()

        /// <summary>
        /// The three cases one by one, without a server: take over, keep,
        /// remove.
        /// </summary>
        [Test]
        public async Task ReplaceAll_UpdatesKeepsAndRemoves()
        {

            var roster = new Roster();

            await roster.ProcessRosterItemAsync(new RosterItem("bob@example.com")   { Name = "Bob"   });
            await roster.ProcessRosterItemAsync(new RosterItem("carol@example.com") { Name = "Carol" });

            var removed   = new List<String>();
            var added   = new List<String>();
            var changed  = new List<String>();

            roster.OnItemRemoved += (timestamp, sender, jid, ct)  => { removed.Add(jid.ToString()); return Task.CompletedTask; };
            roster.OnItemAdded   += (timestamp, sender, item, ct) => { added.Add(item.Jid.ToString()); return Task.CompletedTask; };
            roster.OnItemUpdated += (timestamp, sender, item, ct) => { changed.Add(item.Jid.ToString()); return Task.CompletedTask; };

            // Bob stays (with a new name), Carol falls away, Dave comes along.
            await roster.ReplaceAllAsync([
                new RosterItem("bob@example.com")  { Name = "Robert" },
                new RosterItem("dave@example.com") { Name = "Dave"   }
            ]);

            Assert.Multiple(() =>
            {

                Assert.That(roster.GetItem(JID.Parse("bob@example.com"))?.Name, Is.EqualTo("Robert"));
                Assert.That(roster.GetItem(JID.Parse("dave@example.com")),      Is.Not.Null);
                Assert.That(roster.GetItem(JID.Parse("carol@example.com")),     Is.Null);

                Assert.That(roster.Items, Has.Count.EqualTo(2));

                Assert.That(removed,  Is.EqualTo(new[] { "carol@example.com" }));
                Assert.That(added,  Is.EqualTo(new[] { "dave@example.com"  }));
                Assert.That(changed, Is.EqualTo(new[] { "bob@example.com"   }));

            });

        }

        #endregion

        #region ReplaceAll_WithAnEmptyListClearsTheRoster()

        /// <summary>
        /// A roster that is really empty clears the cache as well.
        /// </summary>
        /// <remarks>
        /// Not to be confused with the empty result of the versioning: that one
        /// comes with no <c>&lt;query/&gt;</c> at all and never reaches this
        /// place. A <c>&lt;query/&gt;</c> <i>without children</i>, by contrast,
        /// really does mean "you have no contacts any more", and then they have
        /// to go.
        /// </remarks>
        [Test]
        public async Task ReplaceAll_WithAnEmptyListClearsTheRoster()
        {

            var roster = new Roster();

            await roster.ProcessRosterItemAsync(new RosterItem("bob@example.com"));

            await roster.ReplaceAllAsync([]);

            Assert.That(roster.Items, Is.Empty);

        }

        #endregion

    }

}
