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
    /// XEP-0480: an account that only ever had SCRAM-SHA-1 material learns
    /// SCRAM-SHA-256, without anybody typing their password again.
    /// </summary>
    /// <remarks>
    /// The situation is not hypothetical. ejabberd and Prosody both store the
    /// material for one hash and only that one, so moving an account to
    /// SCRAM-SHA-256 means deriving afresh - which needs the password the
    /// server does not have. The usual answer is "set every password again",
    /// with everybody locked out in between.
    ///
    /// Building the case takes a deliberate step here, and that is worth
    /// knowing: XMPPCredentials.FromPassword derives *every* mechanism at once,
    /// so an account made the ordinary way never needs an upgrade and these
    /// tests would measure nothing. FromStored with a single mechanism is what
    /// an imported account looks like.
    /// </remarks>
    [TestFixture]
    public class ScramUpgradeTests : AXMPPTests
    {

        #region Helper

        /// <summary>
        /// Credentials as a server that only ever computed SHA-1 would hold
        /// them.
        /// </summary>
        private static XMPPCredentials Sha1Only(String password)
        {

            var whole = XMPPCredentials.FromPassword(password);

            return XMPPCredentials.FromStored(
                       whole.Salt,
                       whole.IterationCount,
                       new Dictionary<SCRAMMechanism, SCRAMKeys> {
                           [SCRAMMechanism.ScramSha1] = whole.KeysOf(SCRAMMechanism.ScramSha1)
                       });

        }

        /// <summary>
        /// The server as it stands mid-migration: SHA-1 material for the
        /// accounts, and only SCRAM-SHA-1 on offer because that is all it can
        /// actually serve.
        /// </summary>
        /// <remarks>
        /// This is the shape that matters, and getting it wrong is what the
        /// first attempt at these tests did. Leaving SCRAM-SHA-256 on offer
        /// makes the client take it - correctly, it is stronger - and the login
        /// then fails against an account with no SHA-256 keys, before any
        /// upgrade could happen. Which is also why the upgrade may name a
        /// mechanism the server does not currently offer: the material has to
        /// be collected before the offer can change, not after.
        /// </remarks>
        private XMPPAccount MidMigrationAccount(String localPart = "alice")
        {

            Server.OfferedSaslMechanisms.Clear();
            Server.OfferedSaslMechanisms.Add("SCRAM-SHA-1");

            return Server.AddAccount(localPart, Sha1Only("pw"));

        }

        #endregion


        #region TheTaskName_LeavesThePlusOff()

        /// <summary>
        /// "UPGR-" and the mechanism, without the <c>-PLUS</c> whatever the
        /// channel binding is doing.
        /// </summary>
        /// <remarks>
        /// The suffix says how an exchange is bound to its connection; what is
        /// being stored is key material, which is the same either way. A task
        /// called UPGR-SCRAM-SHA-256-PLUS would name something that does not
        /// exist, and a server following the XEP would not recognise it.
        /// </remarks>
        [Test]
        public void TheTaskName_LeavesThePlusOff()
        {

            Assert.Multiple(() =>
            {

                Assert.That(ScramUpgrade.TaskNameOf(SCRAMMechanism.ScramSha256),
                            Is.EqualTo("UPGR-SCRAM-SHA-256"));

                Assert.That(ScramUpgrade.MechanismOf("UPGR-SCRAM-SHA-256"),
                            Is.EqualTo(SCRAMMechanism.ScramSha256));

                Assert.That(ScramUpgrade.MechanismOf("UPGR-SCRAM-SHA-256-PLUS"), Is.Null);
                Assert.That(ScramUpgrade.MechanismOf("something else"),          Is.Null);

            });

        }

        #endregion

        #region AnAccountWithoutTheMechanism_IsUpgradedOnLogin()

        /// <summary>
        /// The whole point, end to end.
        /// </summary>
        /// <remarks>
        /// Note what the client had to do: nothing. It offered the upgrade
        /// along with its authentication, the server noticed the material was
        /// missing, and the exchange happened inside the login. The password
        /// was never sent and nobody was locked out for a moment.
        /// </remarks>
        [Test]
        public async Task AnAccountWithoutTheMechanism_IsUpgradedOnLogin()
        {

            var account = MidMigrationAccount();

            Assert.Multiple(() =>
            {
                Assert.That(account.Credentials.Has(SCRAMMechanism.ScramSha1),   Is.True);
                Assert.That(account.Credentials.Has(SCRAMMechanism.ScramSha256), Is.False,
                            "The premise: this account cannot answer a SHA-256 challenge.");
            });

            var alice = await ConnectClientAsync("alice", createAccount: false);

            Assert.Multiple(() =>
            {

                Assert.That(alice.IsConnected, Is.True);

                Assert.That(alice.Connection.UpgradedTo,
                            Is.EqualTo(SCRAMMechanism.ScramSha256),
                            "The client derived the new material during the login.");

                Assert.That(account.Credentials.Has(SCRAMMechanism.ScramSha256), Is.True,
                            "And the server kept it.");

                Assert.That(account.Credentials.Has(SCRAMMechanism.ScramSha1), Is.True,
                            "Without taking away what was there - every client that has " +
                            "not been upgraded yet still has to be able to log in.");

            });

        }

        #endregion

        #region TheUpgradedMaterial_ActuallyWorks()

        /// <summary>
        /// The half that would be easy to get wrong invisibly: the stored keys
        /// have to be the ones a real SCRAM-SHA-256 login needs.
        /// </summary>
        /// <remarks>
        /// A wrong derivation - the wrong salt, the SaltedPassword mistaken for
        /// the StoredKey, SASLprep applied on one side and not the other -
        /// produces material that is the right length and entirely useless. The
        /// upgrade would report success and the next login would fail as a
        /// wrong password, which is the least informative thing it could do.
        /// So the second connection is the assertion.
        /// </remarks>
        [Test]
        public async Task TheUpgradedMaterial_ActuallyWorks()
        {

            var account = MidMigrationAccount();

            var first = await ConnectClientAsync("alice", createAccount: false);
            Assert.That(first.Connection.UpgradedTo, Is.EqualTo(SCRAMMechanism.ScramSha256));
            await first.DisconnectAsync();

            // Nothing weaker is on offer now, so this login can only succeed on
            // the material the upgrade just wrote.
            Server.OfferedSaslMechanisms.Clear();
            Server.OfferedSaslMechanisms.Add("SCRAM-SHA-256");

            var second = await ConnectClientAsync("alice", createAccount: false);

            Assert.Multiple(() =>
            {

                Assert.That(second.IsConnected, Is.True);

                Assert.That(second.Connection.NegotiatedSaslMechanism,
                            Does.StartWith("SCRAM-SHA-256"));

                Assert.That(second.Connection.UpgradedTo, Is.Null,
                            "And nothing to upgrade the second time.");

            });

        }

        #endregion

        #region AnAccountThatNeedsNothing_IsNotAsked()

        /// <summary>
        /// The ordinary case, which is every account this server creates
        /// itself: the material is all there, so no task runs and the login
        /// keeps its round trips.
        /// </summary>
        [Test]
        public async Task AnAccountThatNeedsNothing_IsNotAsked()
        {

            var alice    = await ConnectClientAsync("alice");
            var session  = Server.SessionOf(alice.FullJid.ToString())!;

            Assert.Multiple(() =>
            {

                Assert.That(alice.Connection.UpgradedTo, Is.Null);

                Assert.That(session.Sent.Any(f => f.StartsWith("<continue", StringComparison.Ordinal)),
                            Is.False,
                            "Nothing to upgrade, so nothing to continue for.");

            });

        }

        #endregion

        #region AClientThatDeclines_IsNotUpgraded()

        /// <summary>
        /// The switch, and it is not decoration: what travels is
        /// password-equivalent for the mechanism it creates, so refusing to
        /// derive it at a server's asking is a legitimate position.
        /// </summary>
        [Test]
        public async Task AClientThatDeclines_IsNotUpgraded()
        {

            var account = MidMigrationAccount();

            var client = CreateClient("alice");
            client.Connection.PerformScramUpgrades = false;

            await client.ConnectAsync();

            Assert.Multiple(() =>
            {

                Assert.That(client.IsConnected, Is.True,
                            "Declining the upgrade must not cost the login.");

                Assert.That(client.Connection.UpgradedTo, Is.Null);

                Assert.That(account.Credentials.Has(SCRAMMechanism.ScramSha256), Is.False,
                            "The account keeps exactly what it had.");

            });

        }

        #endregion

        #region OverPlaintext_NothingIsDerived()

        /// <summary>
        /// A SaltedPassword is password-equivalent for its mechanism, so it
        /// belongs over TLS and nowhere else.
        /// </summary>
        /// <remarks>
        /// The client declines by never offering, rather than by refusing when
        /// asked. Refusing later would mean the server had already been told it
        /// could ask - and a server that asks anyway learns, from the refusal,
        /// exactly which accounts are worth attacking on the plaintext port.
        /// </remarks>
        [Test]
        public async Task OverPlaintext_NothingIsDerived()
        {

            await using var plain = new XMPPServer("localhost", useTLS: false);

            plain.Start();

            plain.OfferedSaslMechanisms.Clear();
            plain.OfferedSaslMechanisms.Add("SCRAM-SHA-1");

            var account = plain.AddAccount("alice", Sha1Only("pw"));

            await using var client = new XMPPClient(
                                         new XMPPConnection(
                                             JID.Parse($"alice@{plain.Domain}"),
                                             "pw",
                                             plain.Uri
                                         )
                                     );

            await client.ConnectAsync();

            Assert.Multiple(() =>
            {

                Assert.That(client.IsConnected, Is.True,
                            "The login still works - only the upgrade does not happen.");

                Assert.That(client.Connection.UpgradedTo, Is.Null);

                Assert.That(account.Credentials.Has(SCRAMMechanism.ScramSha256), Is.False);

            });

        }

        #endregion

    }

}
