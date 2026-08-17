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

using System.Security.Cryptography;
using System.Text;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// The checks the server makes in the SCRAM exchange - straight against
    /// <c>SCRAMExchange</c> instead of over a connection.
    ///
    /// That is necessary because a real client does not trigger them: it always
    /// sends the right nonce and the right GS2 header, and a wrong proof it
    /// notices itself at the server signature. Checked over a connection, these
    /// cases would therefore pass for the wrong reason - which is exactly what
    /// happened to me with the first version: the server took every proof and
    /// the integration tests stayed green all the same.
    ///
    /// The client-final-message is built here from the formulas of RFC 5802,
    /// section 3, independently of both implementations.
    /// </summary>
    [TestFixture]
    public class ScramExchangeTests
    {

        #region Data

        private const String Password = "secret";

        private XMPPAccount _account = null!;

        #endregion

        #region SetUp

        [SetUp]
        public void CreateAccount()
        {
            _account = new XMPPAccount("alice@localhost", Password);
        }

        #endregion

        #region Helper functions

        /// <summary>
        /// The server secret for the invented credentials. Fixed, so that the
        /// test can recompute what the server would derive.
        /// </summary>
        private static readonly Byte[] ServerSecret =
            Encoding.UTF8.GetBytes("server secret for the test collection");

        /// <summary>
        /// Invented credentials for a name without an account - the same thing
        /// the server puts in (RFC 6120, section 13.11).
        /// </summary>
        private static XMPPCredentials Invented(String user)
            => XMPPCredentials.Decoy(user, ServerSecret);

        /// <summary>
        /// Begins an exchange with a fixed client nonce.
        /// </summary>
        private SCRAMExchange StartExchange(String clientNonce = "clientnonce")
        {

            var clientFirst = $"n,,n=alice,r={clientNonce}";

            var exchange = SCRAMExchange.Begin(
                               Convert.ToBase64String(Encoding.UTF8.GetBytes(clientFirst)),
                               SCRAMMechanism.ScramSha256,
                               user => user == "alice" ? _account : null,
                               Invented);

            Assert.That(exchange, Is.Not.Null, "The exchange should have begun.");

            return exchange!;

        }

        /// <summary>
        /// The server-first-message in the clear.
        /// </summary>
        private static String ServerFirst(SCRAMExchange exchange)
            => Encoding.UTF8.GetString(Convert.FromBase64String(exchange.Challenge));

        /// <summary>
        /// Builds a client-final-message after RFC 5802, section 3 - with all
        /// the adjusting screws the tests want to turn.
        /// </summary>
        private static String ClientFinal(String   clientFirstBare,
                                          String   serverFirst,
                                          String   password,
                                          String?  nonce        = null,
                                          String?  gs2Header    = null)
        {

            var salt        = Convert.FromBase64String(ValueOf(serverFirst, "s"));
            var iterations  = Int32.Parse(ValueOf(serverFirst, "i"));

            nonce      ??= ValueOf(serverFirst, "r");
            gs2Header  ??= "n,,";

            var saltedPassword = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password),
                                                           salt,
                                                           iterations,
                                                           HashAlgorithmName.SHA256,
                                                           32);

            var clientKey  = HMACSHA256.HashData(saltedPassword, "Client Key"u8.ToArray());
            var storedKey  = SHA256.HashData(clientKey);

            var withoutProof = $"c={Convert.ToBase64String(Encoding.UTF8.GetBytes(gs2Header))},r={nonce}";

            var authMessage      = $"{clientFirstBare},{serverFirst},{withoutProof}";
            var clientSignature  = HMACSHA256.HashData(storedKey, Encoding.UTF8.GetBytes(authMessage));

            var proof = new Byte[clientKey.Length];
            for (var i = 0; i < proof.Length; i++)
                proof[i] = (Byte) (clientKey[i] ^ clientSignature[i]);

            return Convert.ToBase64String(
                       Encoding.UTF8.GetBytes($"{withoutProof},p={Convert.ToBase64String(proof)}"));

        }

        /// <summary>
        /// Reads an attribute, anchored at the start or behind a comma.
        /// </summary>
        private static String ValueOf(String message, String name)
            => message.Split(',')
                        .First(part => part.StartsWith($"{name}=", StringComparison.Ordinal))
                        [(name.Length + 1)..];

        #endregion


        #region CorrectProof_IsAccepted()

        /// <summary>
        /// The right proof is accepted, and the answer is the
        /// server-final-message with the server signature.
        /// </summary>
        [Test]
        public void CorrectProof_IsAccepted()
        {

            var exchange     = StartExchange();
            var serverFirst  = ServerFirst(exchange);

            var result = exchange.Complete(
                               ClientFinal("n=alice,r=clientnonce", serverFirst, Password));

            Assert.That(result, Is.Not.Null, "The right proof has to go through.");

            var serverFinal = Encoding.UTF8.GetString(Convert.FromBase64String(result!));

            Assert.That(serverFinal, Does.StartWith("v="));

        }

        #endregion

        #region WrongPassword_IsRejectedByTheServer()

        /// <summary>
        /// The case the integration tests do <b>not</b> cover: the server
        /// itself has to turn a wrong proof away.
        /// </summary>
        /// <remarks>
        /// Over a real connection a login with a wrong password fails anyway,
        /// because the client does not get the server signature confirmed. If
        /// the server takes every proof, that goes unnoticed - not here.
        /// </remarks>
        [Test]
        public void WrongPassword_IsRejectedByTheServer()
        {

            var exchange     = StartExchange();
            var serverFirst  = ServerFirst(exchange);

            var result = exchange.Complete(
                               ClientFinal("n=alice,r=clientnonce", serverFirst, "wrong"));

            Assert.That(result, Is.Null, "A wrong proof must not be accepted.");

        }

        #endregion

        #region ForeignNonce_IsRejected()

        /// <summary>
        /// The nonce of the server has to be mirrored back. Without this check
        /// a recorded client-final-message could be replayed.
        /// </summary>
        [Test]
        public void ForeignNonce_IsRejected()
        {

            var exchange     = StartExchange();
            var serverFirst  = ServerFirst(exchange);

            // A completely valid proof - only to a different nonce.
            var result = exchange.Complete(
                               ClientFinal("n=alice,r=clientnonce",
                                           serverFirst,
                                           Password,
                                           nonce: "a-completely-different-nonce"));

            Assert.That(result, Is.Null, "A foreign nonce must not go through.");

        }

        #endregion

        #region ChangedGs2Header_IsRejected()

        /// <summary>
        /// The GS2 header reported has to be the one that was sent (RFC 5802,
        /// section 6).
        /// </summary>
        /// <remarks>
        /// Otherwise a man in the middle could make the client believe the
        /// server cannot do channel binding, and thereby haggle the connection
        /// down to the weaker variant without anyone noticing.
        /// </remarks>
        [Test]
        public void ChangedGs2Header_IsRejected()
        {

            var exchange     = StartExchange();
            var serverFirst  = ServerFirst(exchange);

            var result = exchange.Complete(
                               ClientFinal("n=alice,r=clientnonce",
                                           serverFirst,
                                           Password,
                                           gs2Header: "y,,"));

            Assert.That(result, Is.Null, "A deviating GS2 header must not go through.");

        }

        #endregion

        #region UnknownUser_DoesNotStart()

        /// <summary>
        /// An unknown account lets the exchange begin all the same - with
        /// invented but unchanging credentials.
        /// </summary>
        /// <remarks>
        /// The opposite stood here, reasons and all: "RFC 5802, section 7
        /// recommends carrying on with an invented salt instead … deliberately
        /// not done". Both halves were wrong. Section 7 of RFC 5802 is the
        /// formal syntax, and the RFC as a whole recommends nothing of the
        /// sort; on the contrary it lists an <c>unknown-user</c> as an error
        /// value. The recommendation stands in <b>RFC 6120, section 13.11</b>
        /// ("Directory Harvesting"): "not reveal whether or not an account
        /// exists at a server when an entity attempts to authenticate".
        ///
        /// An immediate failure gave that away regardless of the error word -
        /// the information sat in the course of events: one round instead of
        /// two.
        /// </remarks>
        [Test]
        public void UnknownUser_StartsAnyway()
        {

            SCRAMExchange? Attempt(String name)
                => SCRAMExchange.Begin(
                       Convert.ToBase64String(Encoding.UTF8.GetBytes($"n,,n={name},r=clientnonce")),
                       SCRAMMechanism.ScramSha256,
                       user => user == "alice" ? _account : null,
                       Invented);

            var firstAttempt   = Attempt("nobody");
            var secondAttempt  = Attempt("nobody");
            var otherName      = Attempt("neither");

            Assert.That(firstAttempt,  Is.Not.Null, "The exchange should have begun.");
            Assert.That(secondAttempt, Is.Not.Null);
            Assert.That(otherName,     Is.Not.Null);

            var first   = ServerFirst(firstAttempt!);
            var second  = ServerFirst(secondAttempt!);
            var other   = ServerFirst(otherName!);

            Assert.Multiple(() =>
            {

                Assert.That(firstAttempt!.Account, Is.Null,
                            "There is no account - the exchange runs for appearance only.");

                Assert.That(ValueOf(second, "s"), Is.EqualTo(ValueOf(first, "s")),
                            "A salt that changes at every attempt is itself the information.");

                Assert.That(ValueOf(other, "s"), Is.Not.EqualTo(ValueOf(first, "s")),
                            "One salt for all likewise.");

                Assert.That(ValueOf(first, "i"),
                            Is.EqualTo(_account.Credentials.IterationCount.ToString()),
                            "A deviating iteration count would be a mark of recognition again.");

                Assert.That(Convert.FromBase64String(ValueOf(first, "s")).Length,
                            Is.EqualTo(_account.Credentials.Salt.Length),
                            "And a deviating salt length too.");

            });

        }

        #endregion

        #region AValidProof_IsNotEnoughWithoutAnAccount()

        /// <summary>
        /// Even a proof that adds up logs nobody in if there is no account
        /// behind the name.
        /// </summary>
        /// <remarks>
        /// The case cannot be brought about over the wire: the invented keys
        /// come from the server secret, and whoever does not know it cannot
        /// produce a matching proof. Here the exchange is therefore slipped the
        /// <b>real</b> credentials as invented ones - the proof then adds up,
        /// and the exchange has to turn it away all the same.
        ///
        /// Without this test the safeguard in <c>Complete</c> would be an
        /// assertion: it shows up in no other test, and its price would be a
        /// login without an account.
        /// </remarks>
        [Test]
        public void AValidProof_IsNotEnoughWithoutAnAccount()
        {

            const String clientFirstBare = "n=nobody,r=clientnonce";

            var exchange = SCRAMExchange.Begin(
                               Convert.ToBase64String(Encoding.UTF8.GetBytes($"n,,{clientFirstBare}")),
                               SCRAMMechanism.ScramSha256,
                               _ => null,
                               _ => _account.Credentials);

            Assert.That(exchange, Is.Not.Null);

            var serverFirst  = ServerFirst(exchange!);
            var clientFinal  = ClientFinal(clientFirstBare, serverFirst, Password);

            Assert.Multiple(() =>
            {

                Assert.That(exchange!.Complete(clientFinal), Is.Null,
                            "A proof with no account behind it must not get through.");

                Assert.That(exchange.Account, Is.Null);

            });

        }

        #endregion

        #region EscapedUsername_IsUnescaped()

        /// <summary>
        /// RFC 5802: in the user name <c>=2C</c> stands for a comma and
        /// <c>=3D</c> for an equals sign.
        /// </summary>
        /// <remarks>
        /// The order of the unescaping is not free. Whoever replaces <c>=3D</c>
        /// first turns the transmitted <c>=3D2C</c> - that is, the text "=2C" -
        /// into "=2C" first and then wrongly into a comma.
        /// </remarks>
        [Test]
        public void EscapedUsername_IsUnescaped()
        {

            var account   = new XMPPAccount("a,b=c@localhost", Password);
            var lookedUp  = new List<String>();

            var exchange = SCRAMExchange.Begin(
                               Convert.ToBase64String(Encoding.UTF8.GetBytes("n,,n=a=2Cb=3Dc,r=nonce")),
                               SCRAMMechanism.ScramSha256,
                               user => { lookedUp.Add(user); return account; },
                               Invented);

            Assert.Multiple(() =>
            {
                Assert.That(exchange, Is.Not.Null);
                Assert.That(lookedUp,  Is.EqualTo(new[] { "a,b=c" }));
            });

        }

        #endregion

        #region MalformedMessages_AreRejected()

        /// <summary>
        /// Nonsense must not run into an exception, but into a refusal.
        /// </summary>
        [Test]
        public void MalformedMessages_AreRejected()
        {

            Assert.Multiple(() =>
            {

                Assert.That(SCRAMExchange.Begin("not-base64!!", SCRAMMechanism.ScramSha256, _ => _account, Invented),
                            Is.Null, "No base64.");

                Assert.That(SCRAMExchange.Begin(Base64("n,,"), SCRAMMechanism.ScramSha256, _ => _account, Invented),
                            Is.Null, "No user name and no nonce.");

                Assert.That(SCRAMExchange.Begin(Base64("n=alice,r=x"), SCRAMMechanism.ScramSha256, _ => _account, Invented),
                            Is.Null, "No GS2 header.");

                Assert.That(SCRAMExchange.Begin(Base64("n,,r=x"), SCRAMMechanism.ScramSha256, _ => _account, Invented),
                            Is.Null, "No user name.");

            });

            var exchange = StartExchange();

            Assert.Multiple(() =>
            {
                Assert.That(exchange.Complete("not-base64!!"), Is.Null, "No base64.");
                Assert.That(exchange.Complete(Base64("c=biws,r=nonce")), Is.Null, "No proof.");
                Assert.That(exchange.Complete(Base64("c=biws,p=too-short")), Is.Null, "No nonce.");
            });

        }

        private static String Base64(String text)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

        #endregion

    }

}
