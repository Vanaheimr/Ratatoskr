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

using NUnit.Framework;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// The derived key material, and the parameters it is only valid under.
    /// </summary>
    /// <remarks>
    /// That the derivation itself is right is settled elsewhere: the SCRAM test
    /// vectors from RFC 5802, section 5 and RFC 7677, section 3 are recomputed
    /// through the whole exchange in <c>ScramTestVectorTests</c>, and they run
    /// through this type now. What is tested here is what the type is for -
    /// that a kept value knows when it stops applying, and that the secret does
    /// not leak out of it by accident.
    /// </remarks>
    [TestFixture]
    public class SaltedPasswordTests
    {

        private static readonly Byte[] Salt = Convert.FromBase64String("W22ZaJ0SNY7soEsUEjb6gQ==");

        #region TheSameInputs_GiveTheSameKey()

        /// <summary>
        /// The premise of keeping it at all: it is the same value every time.
        /// </summary>
        [Test]
        public void TheSameInputs_GiveTheSameKey()
        {

            var first   = SaltedPassword.Derive(SCRAMMechanism.ScramSha256, "pencil", Salt, 4096);
            var second  = SaltedPassword.Derive(SCRAMMechanism.ScramSha256, "pencil", Salt, 4096);

            Assert.Multiple(() =>
            {
                Assert.That(first == second,             Is.True);
                Assert.That(first.ToArray(),             Is.EqualTo(second.ToArray()));
                Assert.That(first.GetHashCode(),         Is.EqualTo(second.GetHashCode()));
            });

        }

        #endregion

        #region AChangedParameter_StopsItFromApplying()

        /// <summary>
        /// Each of the three parameters on its own is enough to make a kept
        /// value inapplicable.
        /// </summary>
        /// <remarks>
        /// The salt is the one worth having a test for. A changed mechanism or
        /// iteration count is visible in the exchange; a changed salt is not,
        /// and reusing across it would fail as "wrong password" for a password
        /// that is right - which is the kind of report that sends somebody to
        /// reset a working account.
        /// </remarks>
        [Test]
        public void AChangedParameter_StopsItFromApplying()
        {

            var kept       = SaltedPassword.Derive(SCRAMMechanism.ScramSha256, "pencil", Salt, 4096);
            var otherSalt  = Convert.FromBase64String("QSXCR+Q6sek8bf92");

            Assert.Multiple(() =>
            {

                Assert.That(kept.Matches(SCRAMMechanism.ScramSha256, Salt,      4096), Is.True,
                            "Same three - this is the case the whole thing exists for.");

                Assert.That(kept.Matches(SCRAMMechanism.ScramSha256, otherSalt, 4096), Is.False,
                            "Another salt.");

                Assert.That(kept.Matches(SCRAMMechanism.ScramSha256, Salt,      8192), Is.False,
                            "Another iteration count.");

                Assert.That(kept.Matches(SCRAMMechanism.ScramSha1,   Salt,      4096), Is.False,
                            "Another mechanism - and another key length with it.");

            });

        }

        #endregion

        #region TheThreeDerivations_HangTogetherAsRFC5802Says()

        /// <summary>
        /// <c>StoredKey = H(ClientKey)</c>, and the server key is a different
        /// key from either.
        /// </summary>
        [Test]
        public void TheThreeDerivations_HangTogetherAsRFC5802Says()
        {

            var salted     = SaltedPassword.Derive(SCRAMMechanism.ScramSha256, "pencil", Salt, 4096);

            var clientKey  = salted.ClientKey();
            var storedKey  = salted.StoredKey();
            var serverKey  = salted.ServerKey();

            Assert.Multiple(() =>
            {

                Assert.That(storedKey, Is.EqualTo(SHA256.HashData(clientKey)),
                            "RFC 5802, section 3: StoredKey = H(ClientKey).");

                Assert.That(serverKey, Is.Not.EqualTo(clientKey),
                            "Two different keys, or the server could answer its own challenge.");

                // SCRAM-SHA-256 - so 32 bytes, and not whatever length happened
                // to be asked for.
                Assert.That(salted.ToArray(), Has.Length.EqualTo(32));

            });

        }

        #endregion

        #region TheMechanism_DecidesTheLength()

        /// <summary>
        /// SHA-1 gives 20 bytes, SHA-256 gives 32 - the hash length, not a free
        /// choice.
        /// </summary>
        /// <remarks>
        /// An HMAC over a longer key is an HMAC over a different key, so a
        /// length picked here rather than taken from the mechanism would
        /// produce a proof the server cannot reproduce - and it would look like
        /// a wrong password.
        /// </remarks>
        [Test]
        public void TheMechanism_DecidesTheLength()
        {

            Assert.Multiple(() =>
            {
                Assert.That(SaltedPassword.Derive(SCRAMMechanism.ScramSha1,   "pencil", Salt, 4096).ToArray(),
                            Has.Length.EqualTo(20));
                Assert.That(SaltedPassword.Derive(SCRAMMechanism.ScramSha256, "pencil", Salt, 4096).ToArray(),
                            Has.Length.EqualTo(32));
            });

        }

        #endregion

        #region WhatComesOut_IsACopy()

        /// <summary>
        /// A caller cannot reach into the key material through what it is
        /// handed.
        /// </summary>
        /// <remarks>
        /// <c>readonly</c> on a field holding an array protects the reference
        /// and not one byte of the contents, so handing the array out would
        /// make the struct mutable from the outside - and mutable in the one
        /// place where a changed byte turns into a failed login somewhere else
        /// entirely.
        /// </remarks>
        [Test]
        public void WhatComesOut_IsACopy()
        {

            var salted = SaltedPassword.Derive(SCRAMMechanism.ScramSha256, "pencil", Salt, 4096);

            var handedOut = salted.ToArray();
            var saltCopy  = salted.Salt;

            Array.Clear(handedOut);
            Array.Clear(saltCopy);

            Assert.Multiple(() =>
            {
                Assert.That(salted.ToArray(), Is.Not.All.Zero,
                            "Clearing the copy must not clear the key.");
                Assert.That(salted.Salt,      Is.EqualTo(Salt),
                            "Nor the salt.");
            });

        }

        #endregion

        #region TheDefault_HasNoKeyAndSaysSo()

        /// <summary>
        /// <c>default(SaltedPassword)</c> holds nothing, and asking it for a
        /// key says that rather than returning an HMAC over an empty array.
        /// </summary>
        /// <remarks>
        /// The empty-array answer is the dangerous one: it is a perfectly valid
        /// HMAC key, so the authentication would proceed and fail at the far
        /// end, with a message about the password.
        /// </remarks>
        [Test]
        public void TheDefault_HasNoKeyAndSaysSo()
        {

            var nothing = default(SaltedPassword);

            Assert.Multiple(() =>
            {

                Assert.That(nothing.IsNullOrEmpty,     Is.True);
                Assert.That(nothing.IsNotNullOrEmpty,  Is.False);
                Assert.That(nothing.ToArray(),         Is.Empty);
                Assert.That(nothing.Salt,              Is.Empty);
                Assert.That(nothing.Matches(SCRAMMechanism.ScramSha256, Salt, 4096), Is.False);

                Assert.Throws<InvalidOperationException>(() => nothing.ClientKey());
                Assert.Throws<InvalidOperationException>(() => nothing.ServerKey());

                Assert.DoesNotThrow(() => nothing.GetHashCode());

            });

        }

        #endregion

        #region ToString_DoesNotPrintTheKey()

        /// <summary>
        /// The parameters, and expressly not the secret.
        /// </summary>
        /// <remarks>
        /// Not pedantry: this is the type a debugger, a log statement or an
        /// exception message is most likely to call <c>ToString</c> on by
        /// accident, and the default for a struct would have printed the type
        /// name - harmless - while any hand-written "helpful" version tends
        /// towards printing the bytes.
        /// </remarks>
        [Test]
        public void ToString_DoesNotPrintTheKey()
        {

            var salted  = SaltedPassword.Derive(SCRAMMechanism.ScramSha256, "pencil", Salt, 4096);
            var printed = salted.ToString();

            Assert.Multiple(() =>
            {

                Assert.That(printed, Does.Contain("4096"));
                Assert.That(printed, Does.Contain("ScramSha256"));

                Assert.That(printed, Does.Not.Contain(Convert.ToBase64String(salted.ToArray())));
                Assert.That(printed, Does.Not.Contain(Convert.ToHexString  (salted.ToArray())));

            });

        }

        #endregion

        #region FromBytes_TakesWhatSomebodyElseDerived()

        /// <summary>
        /// XEP-0480's upgrade task hands over a salted password that the client
        /// computed; the server side takes it in this way.
        /// </summary>
        [Test]
        public void FromBytes_TakesWhatSomebodyElseDerived()
        {

            var derived = SaltedPassword.Derive  (SCRAMMechanism.ScramSha256, "pencil", Salt, 4096);
            var taken   = SaltedPassword.FromBytes(SCRAMMechanism.ScramSha256, derived.ToArray(), Salt, 4096);

            Assert.Multiple(() =>
            {
                Assert.That(taken == derived,       Is.True);
                Assert.That(taken.StoredKey(),      Is.EqualTo(derived.StoredKey()));
                Assert.That(taken.ServerKey(),      Is.EqualTo(derived.ServerKey()));
            });

        }

        #endregion

    }

}
