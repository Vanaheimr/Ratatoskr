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

using System.Text;
using System.Text.RegularExpressions;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// RFC 6120, section 13.11 ("Directory Harvesting"): the server shall not
    /// give away whether an account exists — "not reveal whether or not an
    /// account exists at a server when an entity attempts to authenticate".
    /// </summary>
    /// <remarks>
    /// The error value alone is not enough for that. <c>&lt;not-authorized/&gt;</c>
    /// expressly covers both cases (section 6.5.10: "this might include, but is
    /// not limited to, the case in which the user does not exist"), and the
    /// server did send exactly that in both cases before as well. What gave it
    /// away was the <b>course of events</b>: an existing account got a
    /// challenge to its first message and failed only at the second, an unknown
    /// one failed at once. One round of difference, and any list of names is
    /// sorted in a single pass.
    ///
    /// These tests therefore do not check <i>that</i> a login is refused - the
    /// tests in <see cref="ScramAuthenticationTests"/> do that - but that both
    /// refusals look alike.
    /// </remarks>
    [TestFixture]
    public class AccountEnumerationTests : AXMPPTests
    {

        #region Helper functions

        /// <summary>
        /// A client that makes exactly one attempt - the question is answered
        /// at the first, and every further one would open a second session.
        /// </summary>
        private XMPPClient SingleAttempt(String localPart, String password = "pw")
        {

            var client = CreateClient(localPart, password: password);
            client.Connection.MaxReconnectAttempts = 0;

            return client;

        }

        /// <summary>The names of the elements the server has sent.</summary>
        private static IReadOnlyList<String> ElementSequence(XMPPSession session)
            => [.. session.Sent.Select(f => Regex.Match(f, @"^\s*<([\w:.-]+)").Groups[1].Value)];

        /// <summary>
        /// The server-first-message from the <c>&lt;challenge/&gt;</c> of a
        /// session, in the clear.
        /// </summary>
        private static String ServerFirst(XMPPSession session)
        {

            var challenge = session.Sent.FirstOrDefault(f => f.StartsWith("<challenge", StringComparison.Ordinal));

            Assert.That(challenge, Is.Not.Null,
                        "The server did not even challenge.");

            var payload = Regex.Match(challenge!, @"<challenge[^>]*>([^<]*)</challenge>").Groups[1].Value;

            return Encoding.UTF8.GetString(Convert.FromBase64String(payload));

        }

        /// <summary>Reads an attribute of the server-first-message.</summary>
        private static String ValueOf(String message, String name)
            => message.Split(',')
                        .First(part => part.StartsWith($"{name}=", StringComparison.Ordinal))
                        [(name.Length + 1)..];

        #endregion


        #region AnUnknownAccount_LooksLikeAWrongPassword()

        /// <summary>
        /// A name without an account and an account with a wrong password
        /// produce the same course of events.
        /// </summary>
        /// <remarks>
        /// What is compared is the sequence of elements the server has sent,
        /// not their content: nonce and salt are different and are meant to be.
        /// What has to be the same is <b>how many</b> steps there were and
        /// <b>which</b> - because that, and not the error word, is what the
        /// question could be answered by until now.
        /// </remarks>
        [Test]
        public async Task AnUnknownAccount_LooksLikeAWrongPassword()
        {

            Server.AddAccount("alice");

            await FailingConnectAsync(SingleAttempt("alice", "wrong"));
            await FailingConnectAsync(SingleAttempt("nobody"));

            var sessions = Server.AllSessions;

            Assert.That(sessions, Has.Count.EqualTo(2),
                        "Exactly two runs are expected, otherwise the test compares the wrong thing.");

            var withAccount  = ElementSequence(sessions[0]);
            var withoutAccount = ElementSequence(sessions[1]);

            Assert.Multiple(() =>
            {

                Assert.That(withoutAccount, Is.EqualTo(withAccount),
                            $"The course of events gives away whether the account exists: {String.Join(", ", withoutAccount)} " +
                            $"instead of {String.Join(", ", withAccount)}");

                // Without this the test would pass even if both sides failed at
                // once - the course of events would be the same then too.
                Assert.That(withAccount, Does.Contain("challenge"),
                            "Without a challenge the comparison says nothing.");

                Assert.That(sessions[1].Sent.Any(f => f.Contains("not-authorized", StringComparison.Ordinal)),
                            Is.True,
                            "At the end stands the refusal, and the same one at that.");

            });

        }

        #endregion

        #region TheSaltOfAnUnknownAccount_StaysTheSame()

        /// <summary>
        /// Twice the same unknown name, twice the same salt.
        /// </summary>
        /// <remarks>
        /// The part a random salt would have spoiled: the salt of an existing
        /// account is fixed. An invented one that comes out differently at
        /// every attempt answers the question just as reliably as an immediate
        /// failure - one only has to ask twice.
        /// </remarks>
        [Test]
        public async Task TheSaltOfAnUnknownAccount_StaysTheSame()
        {

            await FailingConnectAsync(SingleAttempt("nobody"));
            await FailingConnectAsync(SingleAttempt("nobody"));

            var sessions = Server.AllSessions;

            Assert.That(sessions, Has.Count.EqualTo(2));

            var first  = ServerFirst(sessions[0]);
            var second = ServerFirst(sessions[1]);

            Assert.That(ValueOf(second, "s"), Is.EqualTo(ValueOf(first, "s")),
                        "A changing salt is itself the information.");

        }

        #endregion

        #region TwoUnknownAccounts_GetDifferentSalts()

        /// <summary>
        /// Two unknown names get different salts.
        /// </summary>
        /// <remarks>
        /// The counter-check to the previous test, and without it a fixed,
        /// built-in salt would be a passing solution. It would be the worst of
        /// all: two names with the same salt do not occur among real accounts,
        /// so a hit would be recognised as invented at once.
        /// </remarks>
        [Test]
        public async Task TwoUnknownAccounts_GetDifferentSalts()
        {

            await FailingConnectAsync(SingleAttempt("nobody"));
            await FailingConnectAsync(SingleAttempt("neither"));

            var sessions = Server.AllSessions;

            Assert.That(sessions, Has.Count.EqualTo(2));

            Assert.That(ValueOf(ServerFirst(sessions[1]), "s"),
                        Is.Not.EqualTo(ValueOf(ServerFirst(sessions[0]), "s")),
                        "One salt for all gives away just as much.");

        }

        #endregion

        #region WithoutTheStore_BothRecipientsAreTold()

        /// <summary>
        /// With the store switched off, the sender gets the same answer for an
        /// unknown account as for a known one that happens not to be looking.
        /// </summary>
        /// <remarks>
        /// RFC 6121, section 8.5.1 leaves the choice, for an account that does
        /// not exist, between <c>&lt;service-unavailable/&gt;</c> and silence.
        /// <b>But the choice is not a free one</b>: it has to be the same as
        /// for an existing, absent account - otherwise it answers the question
        /// "does this account exist?", and by the most convenient way there is:
        /// send a message and see whether anything comes back.
        ///
        /// That is exactly where the handling fell apart until now. The unknown
        /// account was discarded in silence, the existing one got an error when
        /// the store was switched off.
        /// </remarks>
        [Test]
        public async Task WithoutTheStore_BothRecipientsAreTold()
        {

            Server.StoreOfflineMessages = false;
            Server.AddAccount("bob");

            var alice   = await ConnectClientAsync();
            var errors  = new List<String>();

            alice.Connection.OnStanzaError += (timestamp, sender, from, e, ct) => { lock (errors) errors.Add($"{from}|{e.Condition}");  return Task.CompletedTask; };

            await alice.SendMessageAsync($"bob@{Server.Domain}",    "To an account that exists");
            await alice.SendMessageAsync($"nobody@{Server.Domain}", "To one that does not");

            await WaitFor(() => { lock (errors) return errors.Count == 2; },
                          "two refusals at the sender");

            Assert.Multiple(() =>
            {

                Assert.That(errors[0].Split('|')[1], Is.EqualTo("service-unavailable"));

                Assert.That(errors[1].Split('|')[1], Is.EqualTo(errors[0].Split('|')[1]),
                            "Two different answers sort the names.");

                Assert.That(Server.GetAccount($"nobody@{Server.Domain}"), Is.Null,
                            "An account was created for the sake of the answer.");

            });

        }

        #endregion

        #region WithTheStore_NeitherRecipientIsTold()

        /// <summary>
        /// With the store switched on, the server stays silent in both cases.
        /// </summary>
        /// <remarks>
        /// The counter-check, and it is the more important one: "just always
        /// answer with <c>&lt;service-unavailable/&gt;</c>" would be the
        /// obvious solution and would miss by exactly as much. With the store
        /// switched on - the default - the existing account would then get
        /// silence and the unknown one an error, and the question would be
        /// answered again, only the other way round.
        ///
        /// The silence here is not one of embarrassment: for the existing
        /// account the message lies in the store, and that can be checked.
        /// </remarks>
        [Test]
        public async Task WithTheStore_NeitherRecipientIsTold()
        {

            Server.AddAccount("bob");

            var alice   = await ConnectClientAsync();
            var errors  = new List<String>();

            alice.Connection.OnStanzaError += (timestamp, sender, from, e, ct) => { lock (errors) errors.Add(e.Condition);  return Task.CompletedTask; };

            await alice.SendMessageAsync($"bob@{Server.Domain}",    "Will be stored");
            await alice.SendMessageAsync($"nobody@{Server.Domain}", "Will be discarded");

            await WaitFor(() => Server.GetAccount($"bob@{Server.Domain}")!.OfflineMessages.Count == 1,
                          "the stored message for the existing account");

            await WaitAgainst(() => { lock (errors) return errors.Count > 0; },
                              "a refusal even though the store is on");

        }

        #endregion

        #region AFullStore_RefusesForBothAlike()

        /// <summary>
        /// If the store takes nothing more, that goes for the account that does
        /// not exist as well.
        /// </summary>
        /// <remarks>
        /// The case in which "pretend it was stored" differs from "ask whether
        /// it fits". With <c>MaxStoredOfflineMessages = 0</c> an <i>empty</i>
        /// store takes nothing - so an existing account gets an error, and an
        /// unknown one has to get it just the same. A handling that always
        /// reports "stored" for unknown accounts would fall apart exactly here.
        /// </remarks>
        [Test]
        public async Task AFullStore_RefusesForBothAlike()
        {

            Server.MaxStoredOfflineMessages = 0;
            Server.AddAccount("bob");

            var alice   = await ConnectClientAsync();
            var errors  = new List<String>();

            alice.Connection.OnStanzaError += (timestamp, sender, from, e, ct) => { lock (errors) errors.Add(e.Condition);  return Task.CompletedTask; };

            await alice.SendMessageAsync($"bob@{Server.Domain}",    "Does not fit any more");
            await alice.SendMessageAsync($"nobody@{Server.Domain}", "Does not fit either");

            await WaitFor(() => { lock (errors) return errors.Count == 2; },
                          "two refusals with a full store");

            Assert.That(errors, Is.All.EqualTo("service-unavailable"));

        }

        #endregion

        #region TheInventedSalt_LooksLikeARealOne()

        /// <summary>
        /// The length of the salt and the iteration count are the same as with
        /// an existing account.
        /// </summary>
        /// <remarks>
        /// Whatever was different about the invented salt would be a mark of
        /// recognition again - the iteration count stands openly in the
        /// server-first-message, and the length of the salt can be counted.
        /// </remarks>
        [Test]
        public async Task TheInventedSalt_LooksLikeARealOne()
        {

            Server.AddAccount("alice");

            await FailingConnectAsync(SingleAttempt("alice", "wrong"));
            await FailingConnectAsync(SingleAttempt("nobody"));

            var sessions = Server.AllSessions;

            Assert.That(sessions, Has.Count.EqualTo(2));

            var real      = ServerFirst(sessions[0]);
            var invented  = ServerFirst(sessions[1]);

            Assert.Multiple(() =>
            {

                Assert.That(ValueOf(invented, "i"), Is.EqualTo(ValueOf(real, "i")),
                            "The iteration count tells the two apart.");

                Assert.That(Convert.FromBase64String(ValueOf(invented, "s")).Length,
                            Is.EqualTo(Convert.FromBase64String(ValueOf(real, "s")).Length),
                            "The length of the salt tells the two apart.");

            });

        }

        #endregion

    }

}
