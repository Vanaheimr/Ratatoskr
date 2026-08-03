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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// SCRAM against the official test vectors:
    /// RFC 5802 section 5 for SCRAM-SHA-1 and
    /// RFC 7677 section 3 for SCRAM-SHA-256.
    ///
    /// Both vectors use the user "user" and the password "pencil". The client
    /// nonce is nailed down through <c>FixedClientNonce</c>, otherwise the
    /// AuthMessage and the proof could not be reproduced.
    /// </summary>
    [TestFixture]
    public class SCRAMAuthenticatorTests
    {

        #region Data

        // ----- RFC 5802, section 5 (SCRAM-SHA-1) -----
        private const String Sha1_ClientNonce      = "fyko+d2lbbFgONRv9qkxdawL";
        private const String Sha1_ClientFirst      = "n,,n=user,r=fyko+d2lbbFgONRv9qkxdawL";
        private const String Sha1_ServerFirst      = "r=fyko+d2lbbFgONRv9qkxdawL3rfcNHYJY1ZVvWVs7j," +
                                                     "s=QSXCR+Q6sek8bf92,i=4096";
        private const String Sha1_ClientFinal      = "c=biws,r=fyko+d2lbbFgONRv9qkxdawL3rfcNHYJY1ZVvWVs7j," +
                                                     "p=v0X8v3Bz2T0CJGbJQyF0X+HI4Ts=";
        private const String Sha1_ServerFinal      = "v=rmF9pqV8S7suAoZWja4dJRkFsKQ=";

        // ----- RFC 7677, section 3 (SCRAM-SHA-256) -----
        private const String Sha256_ClientNonce    = "rOprNGfwEbeRWgbNEkqO";
        private const String Sha256_ClientFirst    = "n,,n=user,r=rOprNGfwEbeRWgbNEkqO";
        private const String Sha256_ServerFirst    = "r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0," +
                                                     "s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096";
        private const String Sha256_ClientFinal    = "c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0," +
                                                     "p=dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=";
        private const String Sha256_ServerFinal    = "v=6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=";

        #endregion

        #region Helper functions

        private static SCRAMAuthenticator Authenticator(SCRAMMechanism mechanism, String clientNonce)
            => new("user", "pencil", mechanism) { FixedClientNonce = clientNonce };

        private static String B64(String s)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

        private static String FromB64(String s)
            => Encoding.UTF8.GetString(Convert.FromBase64String(s));

        #endregion


        #region Rfc5802_Sha1_ClientFirstMessage_MatchesTestVector()

        /// <summary>
        /// The client-first-message must match the example from RFC 5802.
        /// </summary>
        [Test]
        public void Rfc5802_Sha1_ClientFirstMessage_MatchesTestVector()
        {

            var scram = Authenticator(SCRAMMechanism.ScramSha1, Sha1_ClientNonce);

            Assert.That(FromB64(scram.CreateClientFirstMessage()),
                        Is.EqualTo(Sha1_ClientFirst));

        }

        #endregion

        #region Rfc5802_Sha1_ClientFinalMessage_MatchesTestVector()

        /// <summary>
        /// The ClientProof must match the value from RFC 5802 exactly. That
        /// covers Hi/PBKDF2, ClientKey, StoredKey, AuthMessage, ClientSignature
        /// and the XOR all at once.
        /// </summary>
        [Test]
        public void Rfc5802_Sha1_ClientFinalMessage_MatchesTestVector()
        {

            var scram = Authenticator(SCRAMMechanism.ScramSha1, Sha1_ClientNonce);
            scram.CreateClientFirstMessage();

            var clientFinal = scram.ProcessServerFirstMessage(B64(Sha1_ServerFirst));

            Assert.That(FromB64(clientFinal), Is.EqualTo(Sha1_ClientFinal));

        }

        #endregion

        #region Rfc5802_Sha1_ServerSignature_IsAccepted()

        /// <summary>
        /// The server signature from RFC 5802 must be accepted.
        /// </summary>
        [Test]
        public void Rfc5802_Sha1_ServerSignature_IsAccepted()
        {

            var scram = Authenticator(SCRAMMechanism.ScramSha1, Sha1_ClientNonce);
            scram.CreateClientFirstMessage();
            scram.ProcessServerFirstMessage(B64(Sha1_ServerFirst));

            Assert.That(scram.VerifyServerFinalMessage(B64(Sha1_ServerFinal)), Is.True);

        }

        #endregion

        #region Rfc7677_Sha256_ClientFirstMessage_MatchesTestVector()

        /// <summary>
        /// The client-first-message must match the example from RFC 7677.
        /// </summary>
        [Test]
        public void Rfc7677_Sha256_ClientFirstMessage_MatchesTestVector()
        {

            var scram = Authenticator(SCRAMMechanism.ScramSha256, Sha256_ClientNonce);

            Assert.That(FromB64(scram.CreateClientFirstMessage()),
                        Is.EqualTo(Sha256_ClientFirst));

        }

        #endregion

        #region Rfc7677_Sha256_ClientFinalMessage_MatchesTestVector()

        /// <summary>
        /// The ClientProof must match the value from RFC 7677 exactly.
        /// </summary>
        [Test]
        public void Rfc7677_Sha256_ClientFinalMessage_MatchesTestVector()
        {

            var scram = Authenticator(SCRAMMechanism.ScramSha256, Sha256_ClientNonce);
            scram.CreateClientFirstMessage();

            var clientFinal = scram.ProcessServerFirstMessage(B64(Sha256_ServerFirst));

            Assert.That(FromB64(clientFinal), Is.EqualTo(Sha256_ClientFinal));

        }

        #endregion

        #region Rfc7677_Sha256_ServerSignature_IsAccepted()

        /// <summary>
        /// The server signature from RFC 7677 must be accepted.
        /// </summary>
        [Test]
        public void Rfc7677_Sha256_ServerSignature_IsAccepted()
        {

            var scram = Authenticator(SCRAMMechanism.ScramSha256, Sha256_ClientNonce);
            scram.CreateClientFirstMessage();
            scram.ProcessServerFirstMessage(B64(Sha256_ServerFirst));

            Assert.That(scram.VerifyServerFinalMessage(B64(Sha256_ServerFinal)), Is.True);

        }

        #endregion

        #region TamperedServerSignature_IsRejected()

        /// <summary>
        /// A falsified server signature must be rejected - otherwise the mutual
        /// authentication would be worthless.
        /// </summary>
        [Test]
        public void TamperedServerSignature_IsRejected()
        {

            var scram = Authenticator(SCRAMMechanism.ScramSha1, Sha1_ClientNonce);
            scram.CreateClientFirstMessage();
            scram.ProcessServerFirstMessage(B64(Sha1_ServerFirst));

            // Flip one bit in the signature
            var signature     = Convert.FromBase64String("rmF9pqV8S7suAoZWja4dJRkFsKQ=");
            signature[0]     ^= 0x01;
            var tampered      = $"v={Convert.ToBase64String(signature)}";

            Assert.That(scram.VerifyServerFinalMessage(B64(tampered)), Is.False);

        }

        #endregion

        #region ServerNonceWithoutClientNonce_IsRejected()

        /// <summary>
        /// If the combined nonce does not carry the client nonce as its prefix,
        /// a MITM is possible (RFC 5802, section 5.1).
        /// </summary>
        [Test]
        public void ServerNonceWithoutClientNonce_IsRejected()
        {

            var scram = Authenticator(SCRAMMechanism.ScramSha1, Sha1_ClientNonce);
            scram.CreateClientFirstMessage();

            var evil = "r=AAAAAAAAAAAAAAAAAAAAAAAA3rfcNHYJY1ZVvWVs7j,s=QSXCR+Q6sek8bf92,i=4096";

            Assert.That(() => scram.ProcessServerFirstMessage(B64(evil)),
                        Throws.TypeOf<AuthenticationException>());

        }

        #endregion

        #region ServerFinalWithError_ThrowsAuthenticationException()

        /// <summary>
        /// A server-final-message with e= is an error and not a signature.
        /// </summary>
        [Test]
        public void ServerFinalWithError_ThrowsAuthenticationException()
        {

            var scram = Authenticator(SCRAMMechanism.ScramSha1, Sha1_ClientNonce);
            scram.CreateClientFirstMessage();
            scram.ProcessServerFirstMessage(B64(Sha1_ServerFirst));

            Assert.That(() => scram.VerifyServerFinalMessage(B64("e=invalid-proof")),
                        Throws.TypeOf<AuthenticationException>());

        }

        #endregion

        #region MechanismNames_MatchIanaRegistry()

        /// <summary>
        /// The mechanism names must match the IANA-registered designations
        /// exactly, otherwise the server refuses the choice.
        /// </summary>
        [Test]
        public void MechanismNames_MatchIanaRegistry()
        {

            Assert.Multiple(() =>
            {
                Assert.That(new SCRAMAuthenticator("u", "p", SCRAMMechanism.ScramSha1).MechanismName,
                            Is.EqualTo("SCRAM-SHA-1"));

                Assert.That(new SCRAMAuthenticator("u", "p", SCRAMMechanism.ScramSha256).MechanismName,
                            Is.EqualTo("SCRAM-SHA-256"));
            });

        }

        #endregion

        #region IterationCountFollowingNonceWithPadding_IsParsedCorrectly()

        /// <summary>
        /// REGRESSION TEST - ExtractValue must anchor its search at the start or
        /// behind a comma.
        ///
        /// With the earlier, unanchored pattern <c>{key}=([^,]+)</c> the search
        /// for the iteration count hit an 'i=' inside the combined nonce and
        /// delivered "=", whereupon Int32.Parse threw a FormatException instead
        /// of a clean AuthenticationException.
        /// </summary>
        [Test]
        public void IterationCountFollowingNonceWithPadding_IsParsedCorrectly()
        {

            var scram = Authenticator(SCRAMMechanism.ScramSha1, "cnonce");
            scram.CreateClientFirstMessage();

            // The combined nonce ends in "i==" - valid under RFC 5802, since
            // every printable character except the comma is allowed.
            var serverFirst = "r=cnonceZZi==,s=QSXCR+Q6sek8bf92,i=4096";

            Assert.That(() => scram.ProcessServerFirstMessage(B64(serverFirst)),
                        Throws.Nothing);

        }

        #endregion

    }

}
