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
    /// RFC 6121, section 3.1.5: the <c>subscribed</c> that arrives when the
    /// roster push which should have created the entry did not.
    /// </summary>
    /// <remarks>
    /// The presence path in <see cref="Roster"/> is the net under the push, and
    /// it had a hole in the one place where it was needed: with no entry for
    /// the sender there was nothing to update, so the method returned and the
    /// grant was lost to this side although the server had made it. The
    /// conformance suite caught it on 2026-10-24 - a handshake against ejabberd
    /// that waited out its ten seconds for a subscription that had long since
    /// been granted.
    ///
    /// Closing it takes one decision about trust: an entry is made from a
    /// <c>subscribed</c> only where it answers a <c>subscribe</c> of our own.
    /// Anything more would let anybody write themselves into the roster with a
    /// single presence stanza - which is what <c>RosterPushSecurityTests</c>
    /// keeps out of the push path, and what the second half of these tests
    /// keeps out of this one.
    /// </remarks>
    [TestFixture]
    public class LostRosterPushTests : AXMPPTests
    {

        #region Data

        private static readonly JID  Bob       = JID.Parse("bob@example.com");
        private static readonly JID  Stranger  = JID.Parse("stranger@example.com");

        #endregion

        #region Helper functions

        /// <summary>
        /// A roster that reports what it did, so that a test can tell an entry
        /// that came into being from one that was brought up to date.
        /// </summary>
        private static (Roster Roster, List<String> Added, List<String> Changed) WatchedRoster()
        {

            var roster   = new Roster();
            var added    = new List<String>();
            var changed  = new List<String>();

            roster.OnItemAdded   += (timestamp, sender, item, ct) => { added.  Add(item.Jid.ToString()); return Task.CompletedTask; };
            roster.OnItemUpdated += (timestamp, sender, item, ct) => { changed.Add(item.Jid.ToString()); return Task.CompletedTask; };

            return (roster, added, changed);

        }

        #endregion


        #region AnAnswerToOurOwnRequestBringsTheMissingEntryIntoBeing()

        /// <summary>
        /// The case from 2026-10-24: we asked, the push that would have created
        /// the entry never arrived, and the grant comes as a presence.
        /// </summary>
        [Test]
        public async Task AnAnswerToOurOwnRequestBringsTheMissingEntryIntoBeing()
        {

            var (roster, added, changed) = WatchedRoster();

            roster.NoteOwnSubscriptionRequest(Bob);

            // From one of their devices, because that is how it arrives: the
            // roster is kept per account, so the resourcepart must not keep the
            // answer from finding the request.
            await roster.ProcessSubscriptionChangeAsync(JID.Parse("bob@example.com/phone"), "subscribed");

            Assert.Multiple(() =>
            {

                Assert.That(roster.GetItem(Bob),                   Is.Not.Null,
                            "The answer to our own request left the roster empty - which is the " +
                            "hole this path is here to close.");

                Assert.That(roster.GetItem(Bob)?.Subscription,     Is.EqualTo(SubscriptionState.To),
                            "The entry was created without the grant it was created for.");

                Assert.That(added,                                 Is.EquivalentTo(new[] { "bob@example.com" }),
                            "A contact that comes into being is an addition, and whoever listens " +
                            "for one has to hear it.");

                Assert.That(changed,                               Is.Empty,
                            "The new contact was announced as a change to something that was not there.");

                Assert.That(roster.HasOwnSubscriptionRequest(Bob), Is.False,
                            "The note outlived the answer it let through.");

            });

        }

        #endregion

        #region ASubscribedFromAStrangerWritesNobodyIntoTheRoster()

        /// <summary>
        /// The other half, and the reason the first one is bounded: nobody puts
        /// themselves into the roster with a presence stanza.
        /// </summary>
        [Test]
        public async Task ASubscribedFromAStrangerWritesNobodyIntoTheRoster()
        {

            var (roster, added, changed) = WatchedRoster();

            await roster.ProcessSubscriptionChangeAsync(Stranger, "subscribed");

            Assert.Multiple(() =>
            {

                Assert.That(roster.GetItem(Stranger), Is.Null,
                            "A subscription grant from somebody we never asked made a roster " +
                            "entry - the forgery that the push path refuses and this one must " +
                            "refuse with it.");

                Assert.That(added,                    Is.Empty);
                Assert.That(changed,                  Is.Empty);

            });

        }

        #endregion

        #region ARevocationForSomebodyWhoIsNotThereCreatesNothing()

        /// <summary>
        /// The note lets an answer through, not a type. <c>unsubscribed</c> and
        /// <c>unsubscribe</c> take something away, and there is nothing there.
        /// </summary>
        [Test]
        public async Task ARevocationForSomebodyWhoIsNotThereCreatesNothing()
        {

            var (roster, added, changed) = WatchedRoster();

            roster.NoteOwnSubscriptionRequest(Bob);

            await roster.ProcessSubscriptionChangeAsync(Bob, "unsubscribed");
            await roster.ProcessSubscriptionChangeAsync(Bob, "unsubscribe");

            Assert.Multiple(() =>
            {

                Assert.That(roster.GetItem(Bob),                   Is.Null,
                            "A revocation created the contact it revokes.");

                Assert.That(added,                                 Is.Empty);
                Assert.That(changed,                               Is.Empty);

                Assert.That(roster.HasOwnSubscriptionRequest(Bob), Is.True,
                            "A refusal used the note up, so the answer that does come later " +
                            "would find nothing.");

            });

        }

        #endregion

        #region AContactThrownOutStaysOut()

        /// <summary>
        /// The note goes with the entry. Otherwise a <c>subscribed</c> arriving
        /// late would bring back somebody who was deliberately removed.
        /// </summary>
        [Test]
        public async Task AContactThrownOutStaysOut()
        {

            var roster = new Roster();

            roster.NoteOwnSubscriptionRequest(Bob);
            await roster.ProcessSubscriptionChangeAsync(Bob, "subscribed");

            await roster.RemoveItemAsync(Bob);

            await roster.ProcessSubscriptionChangeAsync(Bob, "subscribed");

            Assert.That(roster.GetItem(Bob), Is.Null,
                        "A late subscription grant brought a removed contact back.");

        }

        #endregion

        #region AStandingEntryIsStillBroughtUpToDate()

        /// <summary>
        /// The path this change was made inside has to keep doing what it did:
        /// where the entry stands, the grant is applied to it, and only to the
        /// half it is about.
        /// </summary>
        [Test]
        public async Task AStandingEntryIsStillBroughtUpToDate()
        {

            var (roster, added, changed) = WatchedRoster();

            await roster.ProcessRosterItemAsync(new RosterItem(Bob) { Subscription = SubscriptionState.From });

            await roster.ProcessSubscriptionChangeAsync(Bob, "subscribed");

            Assert.Multiple(() =>
            {

                Assert.That(roster.GetItem(Bob)?.Subscription, Is.EqualTo(SubscriptionState.Both),
                            "The grant was taken as the whole state instead of its own half, and " +
                            "the contact lost the right to see us.");

                Assert.That(added,                             Is.EquivalentTo(new[] { "bob@example.com" }),
                            "The push created the contact, and that is the addition.");

                Assert.That(changed,                           Is.EquivalentTo(new[] { "bob@example.com" }),
                            "A contact that was brought up to date was not announced as changed.");

            });

        }

        #endregion

        #region AddContactAsync_NotesTheRequest()

        /// <summary>
        /// The whole repair hangs on the note being taken where the
        /// <c>subscribe</c> goes out.
        /// </summary>
        /// <remarks>
        /// Against a real server, because the wiring is what is under test: the
        /// note is taken in <c>AddContactAsync</c> and nowhere else, and the
        /// roster push that follows does not clear it.
        /// </remarks>
        [Test]
        public async Task AddContactAsync_NotesTheRequest()
        {

            var client   = await ConnectClientAsync();
            var contact  = JID.Parse($"bob@{Server.Domain}");

            if (Server.GetAccount(contact.ToString()) is null)
                Server.AddAccount("bob");

            await client.AddContactAsync(contact);

            Assert.That(client.Roster.HasOwnSubscriptionRequest(contact), Is.True,
                        "A subscribe went out without a note, so an answer to it would be " +
                        "indistinguishable from a stranger writing into the roster.");

        }

        #endregion

    }

}
