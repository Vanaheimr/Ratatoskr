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

using System.Reflection;
using System.Text;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// How the server keeps passwords (RFC 5802, section 3).
    ///
    /// They used to lie in the clear in the memory of the <c>XMPPServer</c>
    /// instance. That was not merely unlovely: as soon as accounts were to
    /// outlive the instance - so with S2 -, the plaintext landed on the disk.
    ///
    /// What is kept now is what RFC 5802 provides for: salt, iteration count
    /// and, per mechanism, <c>StoredKey</c> and <c>ServerKey</c>. From none of
    /// those can the password be computed back.
    /// </summary>
    [TestFixture]
    public class CredentialStorageTests
    {

        #region CorrectPassword_IsAccepted()

        /// <summary>
        /// The obvious thing first: the right password is accepted.
        /// </summary>
        [Test]
        public void CorrectPassword_IsAccepted()
        {

            var credentials = XMPPCredentials.FromPassword("secret");

            Assert.That(credentials.Verify("secret"), Is.True);

        }

        #endregion

        #region WrongPassword_IsRejected()

        /// <summary>
        /// And the counter-check, without which the previous test says nothing.
        /// </summary>
        [Test]
        public void WrongPassword_IsRejected()
        {

            var credentials = XMPPCredentials.FromPassword("secret");

            Assert.Multiple(() =>
            {
                Assert.That(credentials.Verify("Secret"),  Is.False, "Upper and lower case.");
                Assert.That(credentials.Verify("secret "), Is.False, "An appended blank.");
                Assert.That(credentials.Verify(""),        Is.False, "An empty password.");
                Assert.That(credentials.Verify("secreT"),  Is.False, "One character different.");
            });

        }

        #endregion

        #region SamePassword_GetsDifferentKeys()

        /// <summary>
        /// Two accounts with the same password must not carry the same stored
        /// material - otherwise a glance at the account list would give away who
        /// uses the same password, and one rainbow table computed once would hit
        /// them all at a stroke.
        /// </summary>
        [Test]
        public void SamePassword_GetsDifferentKeys()
        {

            var one     = XMPPCredentials.FromPassword("secret");
            var other   = XMPPCredentials.FromPassword("secret");

            Assert.Multiple(() =>
            {

                Assert.That(one.Salt, Is.Not.EqualTo(other.Salt),
                            "Every account needs a salt of its own.");

                Assert.That(one.KeysOf(SCRAMMechanism.ScramSha256).StoredKey,
                            Is.Not.EqualTo(other.KeysOf(SCRAMMechanism.ScramSha256).StoredKey),
                            "Same password, different salt, therefore different keys.");

                // Both must work all the same.
                Assert.That(one.Verify("secret"),   Is.True);
                Assert.That(other.Verify("secret"), Is.True);

            });

        }

        #endregion

        #region Keys_DifferPerMechanism()

        /// <summary>
        /// SCRAM-SHA-1 and SCRAM-SHA-256 derive different keys from the same
        /// password; both are kept, because the client picks the mechanism.
        /// </summary>
        [Test]
        public void Keys_DifferPerMechanism()
        {

            var credentials = XMPPCredentials.FromPassword("secret");

            var sha1    = credentials.KeysOf(SCRAMMechanism.ScramSha1);
            var sha256  = credentials.KeysOf(SCRAMMechanism.ScramSha256);

            Assert.Multiple(() =>
            {
                Assert.That(sha1.StoredKey,   Has.Length.EqualTo(20));
                Assert.That(sha256.StoredKey, Has.Length.EqualTo(32));
                Assert.That(sha1.ServerKey,   Has.Length.EqualTo(20));
                Assert.That(sha256.ServerKey, Has.Length.EqualTo(32));

                Assert.That(sha1.StoredKey, Is.Not.EqualTo(sha1.ServerKey),
                            "StoredKey and ServerKey are derived differently.");
            });

        }

        #endregion

        #region StoredKey_IsTheHashOfTheClientKey()

        /// <summary>
        /// The heart of the construction, recomputed against RFC 5802,
        /// section 3: the key that is kept is the <b>hash</b> of the ClientKey,
        /// not the ClientKey itself.
        /// </summary>
        /// <remarks>
        /// The whole point hangs on exactly that: whoever captures the StoredKey
        /// can check a login with it, but cannot carry one out. Were the
        /// ClientKey itself stored, it would be a password substitute.
        ///
        /// The recomputation here is independent of the derivation, with the
        /// formulas from the RFC.
        /// </remarks>
        [Test]
        public void StoredKey_IsTheHashOfTheClientKey()
        {

            var salt         = new Byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            var credentials  = XMPPCredentials.FromPassword("secret", salt, 4096);

            // SaltedPassword := Hi(Normalize(password), salt, i)
            var saltedPassword = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                                     Encoding.UTF8.GetBytes("secret"),
                                     salt,
                                     4096,
                                     System.Security.Cryptography.HashAlgorithmName.SHA256,
                                     32);

            var clientKey  = System.Security.Cryptography.HMACSHA256.HashData(saltedPassword, "Client Key"u8.ToArray());
            var storedKey  = System.Security.Cryptography.SHA256.HashData(clientKey);
            var serverKey  = System.Security.Cryptography.HMACSHA256.HashData(saltedPassword, "Server Key"u8.ToArray());

            var keys = credentials.KeysOf(SCRAMMechanism.ScramSha256);

            Assert.Multiple(() =>
            {
                Assert.That(keys.StoredKey, Is.EqualTo(storedKey));
                Assert.That(keys.ServerKey, Is.EqualTo(serverKey));

                Assert.That(keys.StoredKey, Is.Not.EqualTo(clientKey),
                            "The ClientKey itself must not be stored.");
            });

        }

        #endregion

        #region GivenSalt_IsUsedUnchanged()

        /// <summary>
        /// A salt that is handed in is taken over - the way on which an account
        /// store reads stored credentials back in.
        /// </summary>
        [Test]
        public void GivenSalt_IsUsedUnchanged()
        {

            var salt         = new Byte[] { 42, 42, 42, 42, 42, 42, 42, 42 };
            var credentials  = XMPPCredentials.FromPassword("secret", salt, 1024);

            Assert.Multiple(() =>
            {
                Assert.That(credentials.Salt,            Is.EqualTo(salt));
                Assert.That(credentials.IterationCount,  Is.EqualTo(1024));
                Assert.That(credentials.Verify("secret"), Is.True);
            });

        }

        #endregion

        #region Salt_CannotBeChangedFromOutside()

        /// <summary>
        /// <c>Salt</c> hands out a copy. Were it to hand out the inner array, a
        /// caller could overwrite it and thereby make every further check of
        /// this account fail.
        /// </summary>
        [Test]
        public void Salt_CannotBeChangedFromOutside()
        {

            var credentials = XMPPCredentials.FromPassword("secret");

            var copy = credentials.Salt;
            Array.Clear(copy);

            Assert.Multiple(() =>
            {
                Assert.That(credentials.Salt,            Is.Not.EqualTo(copy));
                Assert.That(credentials.Verify("secret"), Is.True);
            });

        }

        #endregion

        #region Account_KeepsNoPlaintextPassword()

        /// <summary>
        /// The actual promise of this step: after an account has been created,
        /// the plaintext password no longer lies in any field.
        /// </summary>
        /// <remarks>
        /// By way of reflection and therefore coarse - but the promise is one
        /// about the state of the object, and only this way can it be checked
        /// without narrowing it down to the fields I happen to be thinking of.
        /// The <c>XMPPCredentials</c> object is deliberately searched as well.
        /// </remarks>
        [Test]
        public void Account_KeepsNoPlaintextPassword()
        {

            const String password = "a-very-peculiar-password";

            var account = new XMPPAccount("alice@localhost", password);

            var found = new List<String>();

            SearchPlaintext(account,             "XMPPAccount", password, found, 0);
            SearchPlaintext(account.Credentials, "Credentials", password, found, 0);

            Assert.That(found, Is.Empty,
                        $"The plaintext password still sits in: {String.Join(", ", found)}");

        }

        /// <summary>
        /// Searches the fields of an object flat for the plaintext - strings
        /// directly, byte arrays read as UTF-8.
        /// </summary>
        private static void SearchPlaintext(Object        target,
                                            String        path,
                                            String        password,
                                            List<String>  found,
                                            Int32         depth)
        {

            if (depth > 3)
                return;

            foreach (var field in target.GetType().GetFields(BindingFlags.Instance |
                                                             BindingFlags.Public   |
                                                             BindingFlags.NonPublic))
            {

                var value = field.GetValue(target);

                switch (value)
                {

                    case String text when text.Contains(password, StringComparison.Ordinal):
                        found.Add($"{path}.{field.Name}");
                        break;

                    case Byte[] bytes when Encoding.UTF8.GetString(bytes).Contains(password, StringComparison.Ordinal):
                        found.Add($"{path}.{field.Name}");
                        break;

                    case SCRAMKeys keys:
                        SearchPlaintext(keys, $"{path}.{field.Name}", password, found, depth + 1);
                        break;

                    case System.Collections.IDictionary entries:
                        foreach (var entry in entries.Values)
                            if (entry is not null)
                                SearchPlaintext(entry, $"{path}.{field.Name}[]", password, found, depth + 1);
                        break;

                }

            }

        }

        #endregion

    }

}
