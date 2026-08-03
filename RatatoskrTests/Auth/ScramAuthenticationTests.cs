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
    /// SCRAM between a real client and a real server (RFC 5802, RFC 7677).
    ///
    /// The client spoke SCRAM from the beginning, but the test server offered
    /// only PLAIN - the whole path was therefore checked only against the test
    /// vectors from the RFC, never in conversation. The second half in
    /// particular, in which the client checks the signature of the server, had
    /// not a single test that would have caught it failing.
    ///
    /// Now the server speaks SCRAM, and because the client picks the strongest
    /// mechanism offered of its own accord, the whole rest of the suite runs
    /// over it as well.
    /// </summary>
    [TestFixture]
    public class ScramAuthenticationTests : AXMPPTests
    {

        #region Helper functions

        /// <summary>
        /// A client that does not rebuild the connection twenty times after a
        /// failure - the question is answered at the first attempt.
        /// </summary>
        private XMPPClient SingleAttemptClient(String localPart = "alice",
                                               String password  = "pw")
        {

            if (Server.GetAccount($"{localPart}@{Server.Domain}") is null)
                Server.AddAccount(localPart);

            var client = CreateClient(localPart, password: password);
            client.Connection.MaxReconnectAttempts = 0;

            return client;

        }

        #endregion


        #region Client_ChoosesScramSha256()

        /// <summary>
        /// If the server offers everything, the client takes the strongest
        /// mechanism - and in particular sends no password any more.
        /// </summary>
        [Test]
        public async Task Client_ChoosesScramSha256()
        {

            var client   = await ConnectClientAsync();
            var session  = Server.SessionOf(client.FullJid)!;

            Assert.Multiple(() =>
            {

                Assert.That(session.Received.Any(f => f.Contains("mechanism='SCRAM-SHA-256'", StringComparison.Ordinal)),
                            Is.True,
                            "The client must choose SCRAM-SHA-256 when it is offered.");

                Assert.That(session.Received.Any(f => f.Contains("mechanism='PLAIN'", StringComparison.Ordinal)),
                            Is.False,
                            "Alongside SCRAM, PLAIN must not occur any more.");

            });

        }

        #endregion

        #region ScramSha1_IsUsedWhenItIsAllThereIs()

        /// <summary>
        /// The weaker mechanism has to work as well - a server that can do
        /// nothing but SCRAM-SHA-1 is the normal case out there.
        /// </summary>
        [Test]
        public async Task ScramSha1_IsUsedWhenItIsAllThereIs()
        {

            Server.OfferedSaslMechanisms.Clear();
            Server.OfferedSaslMechanisms.Add("SCRAM-SHA-1");

            var client   = await ConnectClientAsync();
            var session  = Server.SessionOf(client.FullJid)!;

            Assert.Multiple(() =>
            {
                Assert.That(client.IsConnected, Is.True);
                Assert.That(session.Received.Any(f => f.Contains("mechanism='SCRAM-SHA-1'", StringComparison.Ordinal)),
                            Is.True);
            });

        }

        #endregion

        #region PlainOnly_StillWorks()

        /// <summary>
        /// And PLAIN too, still - that path is the exception now and would
        /// otherwise go untested.
        /// </summary>
        [Test]
        public async Task PlainOnly_StillWorks()
        {

            Server.OfferedSaslMechanisms.Clear();
            Server.OfferedSaslMechanisms.Add("PLAIN");

            var client   = await ConnectClientAsync();
            var session  = Server.SessionOf(client.FullJid)!;

            Assert.Multiple(() =>
            {
                Assert.That(client.IsConnected, Is.True);
                Assert.That(session.Received.Any(f => f.Contains("mechanism='PLAIN'", StringComparison.Ordinal)),
                            Is.True);
            });

        }

        #endregion

        #region WrongPassword_IsRejected()

        /// <summary>
        /// The counter-check to the login: with a wrong password the client
        /// does not get through.
        /// </summary>
        /// <remarks>
        /// With SCRAM the server notices that only at the client-final-message
        /// - the password itself never goes over the wire, only a proof that
        /// the client knows it.
        /// </remarks>
        [Test]
        public async Task WrongPassword_IsRejected()
        {

            Server.AddAccount("alice");

            var client  = SingleAttemptClient(password: "wrong");
            var errors  = new List<String>();

            client.OnError += e => errors.Add(e);

            await FailingConnectAsync(client);

            Assert.Multiple(() =>
            {
                Assert.That(client.IsConnected, Is.False);
                Assert.That(errors,             Is.Not.Empty);
            });

        }

        #endregion

        #region UnknownAccount_IsRejected()

        /// <summary>
        /// An account that does not exist likewise.
        /// </summary>
        [Test]
        public async Task UnknownAccount_IsRejected()
        {

            var client = CreateClient("nobody");
            client.Connection.MaxReconnectAttempts = 0;

            var errors = new List<String>();
            client.OnError += e => errors.Add(e);

            await FailingConnectAsync(client);

            Assert.Multiple(() =>
            {
                Assert.That(client.IsConnected, Is.False);
                Assert.That(errors,             Is.Not.Empty);
            });

        }

        #endregion

        #region Success_CarriesTheServerSignature()

        /// <summary>
        /// The <c>&lt;success/&gt;</c> carries the server-final-message
        /// (RFC 5802, section 3) - without it the client would have nothing to
        /// check.
        /// </summary>
        [Test]
        public async Task Success_CarriesTheServerSignature()
        {

            var client   = await ConnectClientAsync();
            var session  = Server.SessionOf(client.FullJid)!;

            var success = session.Sent.FirstOrDefault(f => f.StartsWith("<success", StringComparison.Ordinal));

            Assert.That(success, Is.Not.Null, "No <success/> found.");

            var payload = success!.Replace("<success xmlns='urn:ietf:params:xml:ns:xmpp-sasl'>", "")
                                  .Replace("</success>", "");

            Assert.That(payload, Is.Not.Empty, "The <success/> came without a server-final-message.");

            var unpacked = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));

            Assert.That(unpacked, Does.StartWith("v="),
                        $"The server-final-message must begin with v=, but was: {unpacked}");

        }

        #endregion

        #region CorruptedServerSignature_IsRefused()

        /// <summary>
        /// The heart of it: a wrong server signature must make the client
        /// refuse the login.
        /// </summary>
        /// <remarks>
        /// That is exactly the second half of SCRAM. A man in the middle who
        /// does not know the password can indeed move the client to a
        /// client-final-message, but cannot produce this signature. Whoever
        /// does not check it has authenticated one-sidedly instead of
        /// mutually.
        /// </remarks>
        [Test]
        public async Task CorruptedServerSignature_IsRefused()
        {

            Server.CorruptScramSignature = true;

            var client  = SingleAttemptClient();
            var errors  = new List<String>();

            client.OnError += e => errors.Add(e);

            await FailingConnectAsync(client);

            Assert.Multiple(() =>
            {
                Assert.That(client.IsConnected, Is.False,
                            "With a wrong server signature no connection may come about.");

                Assert.That(errors.Any(e => e.Contains("signature", StringComparison.OrdinalIgnoreCase)),
                            Is.True,
                            $"The reason has to be named. Reported was: {String.Join(" | ", errors)}");
            });

        }

        #endregion

        #region MissingServerSignature_IsRefused()

        /// <summary>
        /// And a missing one likewise - the more tempting mistake, because an
        /// empty <c>&lt;success/&gt;</c> looks like a success.
        /// </summary>
        [Test]
        public async Task MissingServerSignature_IsRefused()
        {

            Server.OmitScramSignature = true;

            var client  = SingleAttemptClient();
            var errors  = new List<String>();

            client.OnError += e => errors.Add(e);

            await FailingConnectAsync(client);

            Assert.Multiple(() =>
            {
                Assert.That(client.IsConnected, Is.False,
                            "Without a server signature no connection may come about.");

                Assert.That(errors, Is.Not.Empty);
            });

        }

        #endregion

        #region ADifferentlyComposedPassword_StillMatches()

        /// <summary>
        /// The same password, put together differently, must give the same
        /// login — over SCRAM as over PLAIN.
        /// </summary>
        /// <remarks>
        /// A <c>u</c> with a diaeresis arrives, depending on keyboard and
        /// operating system, as one character or as <c>u</c> with two dots
        /// appended. For the person in front of it that is the same password;
        /// for a byte comparison it is not. That is exactly what SASLprep
        /// stands before the key derivation for — and as long as it consisted
        /// only of an NFKC, it hung on the mechanism as well: SCRAM normalised,
        /// PLAIN not at all.
        /// </remarks>
        [Test]
        public async Task ADifferentlyComposedPassword_StillMatches()
        {

            // Once put together, once taken apart (u + combining diaeresis).
            const String composed    = "Gr\u00FCße-42";
            const String decomposed  = "Gru\u0308ße-42";

            Server.AddAccount("alice", composed);

            var overScram = CreateClient("alice", password: decomposed);
            await overScram.ConnectAsync();

            Assert.That(overScram.IsConnected, Is.True,
                        "Over SCRAM the taken-apart spelling must fit.");

            // And the same again when the server offers PLAIN only.
            Server.OfferedSaslMechanisms.Clear();
            Server.OfferedSaslMechanisms.Add("PLAIN");

            var overPlain = CreateClient("alice", password: decomposed);
            overPlain.Connection.Resource = "second";
            await overPlain.ConnectAsync();

            Assert.That(overPlain.IsConnected, Is.True,
                        "Over PLAIN just the same - otherwise it would hang on the mechanism.");

            // And what went out is the prepared spelling.
            //
            // That the login succeeds does not vouch for it: the server
            // prepares what arrives at its end, and would therefore cope with
            // the taken-apart spelling too. What has to be checked is what
            // stands on the wire - otherwise the client half would stay
            // uncovered, and a server that does not prepare itself would no
            // longer let us in.
            var session   = Server.SessionOf(overPlain.FullJid)!;
            var expected  = Convert.ToBase64String(
                                System.Text.Encoding.UTF8.GetBytes($"\0alice\0{composed}"));

            Assert.That(session.Received.Any(f => f.Contains(expected, StringComparison.Ordinal)),
                        Is.True,
                        "The <auth/> must carry the password prepared by SASLprep.");

        }

        #endregion

        #region AnUnusablePassword_IsRejectedAndDoesNotThrow()

        /// <summary>
        /// A password that cannot be prepared by SASLprep is a failed attempt —
        /// and not a server error.
        /// </summary>
        /// <remarks>
        /// The way there leads over the wire: what stands in a PLAIN
        /// <c>&lt;auth/&gt;</c> is decided by the counterpart, and a control
        /// character in it must not knock the server over. The check therefore
        /// goes deliberately into a <c>false</c> instead of into an exception.
        /// </remarks>
        [Test]
        public async Task AnUnusablePassword_IsRejectedAndDoesNotThrow()
        {

            Server.AddAccount("alice");
            Server.OfferedSaslMechanisms.Clear();
            Server.OfferedSaslMechanisms.Add("PLAIN");

            var account = Server.GetAccount($"alice@{Server.Domain}")!;

            Assert.Multiple(() =>
            {

                Assert.That(() => account.Credentials.Verify("pw\u0007"), Throws.Nothing,
                            "An unusable password must not raise an exception.");

                Assert.That(account.Credentials.Verify("pw\u0007"), Is.False);

                // The right password still goes through.
                Assert.That(account.Credentials.Verify("pw"), Is.True);

            });

            await Task.CompletedTask;

        }

        #endregion

        #region ThePasswordNeverGoesOverTheWire()

        /// <summary>
        /// The promise SCRAM is there for at all: the password turns up in no
        /// sent frame.
        /// </summary>
        /// <remarks>
        /// Checked against a conspicuous password, so that a chance occurrence
        /// inside a base64 block is ruled out.
        /// </remarks>
        [Test]
        public async Task ThePasswordNeverGoesOverTheWire()
        {

            const String password = "Pilcrow-Coelacanth-42";

            Server.AddAccount("alice", password);

            var client = CreateClient("alice", password: password);
            await client.ConnectAsync();

            var session = Server.SessionOf(client.FullJid)!;

            var inPlainText = session.Received.Where(f => f.Contains(password, StringComparison.Ordinal)).ToList();

            // And the same for the base64 form, as PLAIN would send it.
            var base64 = Convert.ToBase64String(
                             System.Text.Encoding.UTF8.GetBytes($"\0alice\0{password}"));

            var encoded = session.Received.Where(f => f.Contains(base64, StringComparison.Ordinal)).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(inPlainText, Is.Empty, "The password stood in the clear in a frame.");
                Assert.That(encoded,     Is.Empty, "The password stood as a PLAIN payload in a frame.");
            });

        }

        #endregion

    }

}
