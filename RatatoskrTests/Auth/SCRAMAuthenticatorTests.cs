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

        /// <summary>
        /// The RFC 7677 server-first-message with another iteration count.
        /// Derived from the real vector and not written afresh, so that nonce
        /// and salt stay the ones the authenticator expects - everything before
        /// the count has to pass, or the test would be measuring the nonce
        /// check.
        /// </summary>
        private static String Sha256_ServerFirstWith(String IterationCount)
            => Sha256_ServerFirst.Replace(",i=4096", $",i={IterationCount}");

        /// <summary>
        /// An authenticator that has said its client-first-message and is
        /// waiting for the answer.
        /// </summary>
        private static SCRAMAuthenticator AwaitingServerFirst()
        {
            var scram = Authenticator(SCRAMMechanism.ScramSha256, Sha256_ClientNonce);
            scram.CreateClientFirstMessage();
            return scram;
        }

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


        // ----- The iteration count comes from the untrusted side -----
        //
        // Nothing signs the server-first-message. Everything below is therefore
        // about a number that an attacker on the connection writes as they
        // please, and that used to reach the key derivation unexamined.

        #region TooFewIterations_AreRefused()

        /// <summary>
        /// <c>i=1</c> is the quiet attack of the two. The login succeeds, the
        /// password is right, nothing looks wrong - and whoever recorded the
        /// handshake can guess the password from it for almost nothing, because
        /// the derivation they have to repeat per guess has been made cheap.
        /// </summary>
        [Test]
        public void TooFewIterations_AreRefused()
        {

            var scram = AwaitingServerFirst();

            var thrown = Assert.Throws<AuthenticationException>(
                             () => scram.ProcessServerFirstMessage(B64(Sha256_ServerFirstWith("1"))));

            Assert.That(thrown!.Message, Does.Contain(SCRAMAuthenticator.MinimumIterations.ToString()));

        }

        #endregion

        #region TooManyIterations_AreRefused()

        /// <summary>
        /// The loud one, and the cheaper to mount: one frame with a large
        /// number, and this process computes PBKDF2 for hours. It costs the
        /// sender the writing of the number.
        /// </summary>
        [Test]
        public void TooManyIterations_AreRefused()
        {

            var scram = AwaitingServerFirst();

            Assert.Throws<AuthenticationException>(
                () => scram.ProcessServerFirstMessage(B64(Sha256_ServerFirstWith("2147483647"))));

        }

        #endregion

        #region TheBoundsThemselves_AreInside()

        /// <summary>
        /// Both ends of the window belong to it. 4096 is not an accident but
        /// the number both RFCs name and both test vectors use - a check that
        /// refused it would have failed every vector in this file.
        /// </summary>
        /// <remarks>
        /// The upper end really computes a million iterations, which takes about
        /// a second. That is the price of knowing the comparison is not off by
        /// one, and off-by-one is the only mistake this line can make.
        /// </remarks>
        [Test]
        public void TheBoundsThemselves_AreInside()
        {

            Assert.Multiple(() =>
            {

                Assert.That(() => AwaitingServerFirst().ProcessServerFirstMessage(
                                      B64(Sha256_ServerFirstWith(SCRAMAuthenticator.MinimumIterations.ToString()))),
                            Throws.Nothing);

                Assert.That(() => AwaitingServerFirst().ProcessServerFirstMessage(
                                      B64(Sha256_ServerFirstWith(SCRAMAuthenticator.MaximumIterations.ToString()))),
                            Throws.Nothing);

            });

        }

        #endregion

        #region AnIterationCountThatIsNoNumber_IsRefusedAsOne()

        /// <summary>
        /// Three of these used to leave through a door of their own:
        /// <c>Int32.Parse</c> threw a FormatException on "abc", an
        /// OverflowException past Int32, and it <b>accepted</b> the minus -
        /// whereupon PBKDF2 threw an ArgumentOutOfRangeException from inside the
        /// handshake. An error about a parameter, where the truth was that the
        /// far side had sent nonsense.
        ///
        /// The empty value is the fourth and was never one of them: with nothing
        /// behind the <c>i=</c> the extraction finds no value at all and the null
        /// check above throws before the parsing. It rides along to keep it that
        /// way, since a later reader has no way of telling the two apart by
        /// looking - and both are the same answer.
        ///
        /// They belong together and they belong to this mechanism, so they all
        /// arrive as one AuthenticationException that a caller can catch.
        /// </summary>
        [Test]
        public void AnIterationCountThatIsNoNumber_IsRefusedAsOne()
        {

            Assert.Multiple(() =>
            {
                foreach (var nonsense in new[] { "abc", "-1", "99999999999999999999", "" })
                    Assert.That(() => AwaitingServerFirst().ProcessServerFirstMessage(
                                          B64(Sha256_ServerFirstWith(nonsense))),
                                Throws.TypeOf<AuthenticationException>(),
                                $"iteration count '{nonsense}'");
            });

        }

        #endregion

    }

}
