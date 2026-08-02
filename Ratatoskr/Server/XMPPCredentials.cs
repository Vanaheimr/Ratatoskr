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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    /// <summary>
    /// The credentials of an account in the form a server may keep them: salt,
    /// iteration count and, per mechanism, the two derived keys from RFC 5802,
    /// section 3.
    /// </summary>
    /// <remarks>
    /// The password itself does not appear here and cannot be computed back
    /// from what is stored. The PLAIN login does not need to keep it either: it
    /// derives anew from the plaintext offered, with the same salt, and
    /// compares the results.
    ///
    /// The server side is deliberately written independently of
    /// <see cref="SCRAMAuthenticator"/>, as is customary in this project. If
    /// both sides used the same helpers, the tests would check the handshake
    /// with the same logic that produces it, and a shared mistake in thinking
    /// would stay undetected.
    /// </remarks>
    public sealed class XMPPCredentials
    {

        #region Data

        private readonly Byte[] _salt;
        private readonly Dictionary<SCRAMMechanism, SCRAMKeys> _keys;

        #endregion

        #region Constants

        /// <summary>
        /// The iteration count for new accounts.
        /// </summary>
        /// <remarks>
        /// RFC 7677, section 4 names 4096 as the lower bound for
        /// SCRAM-SHA-256. By today's standards that is little - real operation
        /// should go considerably higher. The value stands here because every
        /// test account created runs through it twice and the suite would
        /// otherwise become noticeably slower; it can be overridden per
        /// account.
        /// </remarks>
        public const Int32 DefaultIterationCount = 4096;

        /// <summary>Length of the salt produced, in bytes.</summary>
        public const Int32 SaltLength = 16;

        #endregion

        #region Properties

        /// <summary>The salt of this account.</summary>
        public Byte[] Salt => [.. _salt];

        /// <summary>The iteration count that was derived with.</summary>
        public Int32 IterationCount { get; }

        /// <summary>For which mechanisms keys are on hand.</summary>
        public IEnumerable<SCRAMMechanism> Mechanisms => _keys.Keys;

        #endregion

        #region Constructor(s)

        private XMPPCredentials(Byte[]                                 salt,
                                Int32                                  iterationCount,
                                Dictionary<SCRAMMechanism, SCRAMKeys>  keys)
        {
            _salt           = salt;
            _keys           = keys;
            IterationCount  = iterationCount;
        }

        #endregion


        #region FromPassword(password, salt = null, iterationCount = DefaultIterationCount)

        /// <summary>
        /// Derives the credentials from a plaintext password. Afterwards the
        /// password is no longer needed.
        /// </summary>
        /// <param name="password">The plaintext password.</param>
        /// <param name="salt">A given salt; null produces a random one.</param>
        /// <param name="iterationCount">The iteration count for PBKDF2.</param>
        public static XMPPCredentials FromPassword(String   password,
                                                   Byte[]?  salt             = null,
                                                   Int32    iterationCount   = DefaultIterationCount)
        {

            ArgumentOutOfRangeException.ThrowIfLessThan(iterationCount, 1);

            salt ??= RandomNumberGenerator.GetBytes(SaltLength);

            var keys = new Dictionary<SCRAMMechanism, SCRAMKeys>();

            foreach (var mechanism in Enum.GetValues<SCRAMMechanism>())
                keys[mechanism] = DeriveKeys(password, salt, iterationCount, mechanism);

            return new XMPPCredentials([.. salt], iterationCount, keys);

        }

        #endregion

        #region FromStored(salt, iterationCount, keys)

        /// <summary>
        /// Puts credentials back together from what was stored - the way back
        /// for an <see cref="IXMPPAccountStore"/>.
        /// </summary>
        /// <remarks>
        /// Without derivation: the keys are already on hand, after all, and the
        /// password they stem from no longer exists.
        /// </remarks>
        public static XMPPCredentials FromStored(Byte[]                                          salt,
                                                 Int32                                           iterationCount,
                                                 IReadOnlyDictionary<SCRAMMechanism, SCRAMKeys>  keys)
        {

            ArgumentOutOfRangeException.ThrowIfLessThan(iterationCount, 1);

            if (keys.Count == 0)
                throw new ArgumentException("Without keys no login can be checked.", nameof(keys));

            return new XMPPCredentials([.. salt],
                                       iterationCount,
                                       keys.ToDictionary(k => k.Key, k => k.Value));

        }

        #endregion

        #region Decoy(user, secret)

        /// <summary>
        /// Invented credentials for an account that does not exist - so that an
        /// unknown user name looks just like a known one (RFC 6120,
        /// section 13.11).
        /// </summary>
        /// <remarks>
        /// "Not reveal whether or not an account exists at a server when an
        /// entity attempts to authenticate" - with SCRAM the same error does not
        /// suffice for that. Whoever refuses an unknown account right away
        /// answers the first message with a failure and that of an existing
        /// account with a challenge; the information then sits in the
        /// <b>sequence</b> and not in the error word.
        ///
        /// <b>Constant, not random.</b> A salt that changes on every attempt
        /// would itself be the information: that of an existing account stands
        /// fixed. It therefore arises from the user name and a server key - a
        /// different one for every name, always the same one for the same name,
        /// and none of them predictable without knowing the key. Exactly for
        /// that reason the iteration count is the ordinary one too: a differing
        /// one would again be a distinguishing mark.
        ///
        /// The keys arise the same way and fit no password. The exchange thus
        /// runs to its end and fails where it fails with a wrong password too -
        /// at the proof.
        ///
        /// <b>What this does not achieve:</b> across a restart the invented
        /// salts change, the real ones do not. Whoever tries the same name
        /// before and after sees the difference. A lasting server key would
        /// belong in the account store and is not part of this step.
        /// </remarks>
        /// <param name="user">The user name from the client-first-message.</param>
        /// <param name="secret">The server key that is derived from.</param>
        public static XMPPCredentials Decoy(String user, Byte[] secret)
        {

            var keys = new Dictionary<SCRAMMechanism, SCRAMKeys>();

            foreach (var mechanism in Enum.GetValues<SCRAMMechanism>())
            {

                var length = KeyLengthOf(mechanism);

                keys[mechanism] = new SCRAMKeys(
                                      StoredKey: Derived(secret, $"stored:{mechanism}:{user}", length),
                                      ServerKey: Derived(secret, $"server:{mechanism}:{user}", length));

            }

            return new XMPPCredentials(Derived(secret, $"salt:{user}", SaltLength),
                                       DefaultIterationCount,
                                       keys);

        }

        private static Byte[] Derived(Byte[] secret, String purpose, Int32 length)
            => HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(purpose))[..length];

        #endregion

        #region KeysOf(mechanism)

        /// <summary>
        /// The keys for a mechanism.
        /// </summary>
        public SCRAMKeys KeysOf(SCRAMMechanism mechanism)
            => _keys[mechanism];

        #endregion

        #region Verify(password)

        /// <summary>
        /// Checks a plaintext password, as SASL PLAIN delivers it.
        /// </summary>
        /// <remarks>
        /// Derived with the stored salt and the stored iteration count;
        /// compared is the <c>StoredKey</c>. The comparison runs through
        /// <see cref="CryptographicOperations.FixedTimeEquals"/> - a comparison
        /// that breaks off at the first differing byte would betray, through
        /// its running time, how far a guessing attempt got.
        /// </remarks>
        public Boolean Verify(String password)
        {

            var mechanism = SCRAMMechanism.ScramSha256;

            SCRAMKeys candidate;

            try
            {
                candidate = DeriveKeys(password, _salt, IterationCount, mechanism);
            }
            catch (AuthenticationException)
            {
                // A password that cannot be prepared per SASLprep can lead to no
                // stored key. That is a failed attempt and not a server error -
                // over the wire comes whatever the other side sends, and that
                // must not knock anything over here.
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(candidate.StoredKey,
                                                           _keys[mechanism].StoredKey);

        }

        #endregion


        #region (private, static) Derivation per RFC 5802, section 3

        private static SCRAMKeys DeriveKeys(String          password,
                                            Byte[]          salt,
                                            Int32           iterationCount,
                                            SCRAMMechanism  mechanism)
        {

            // SaltedPassword := Hi(Normalize(password), salt, i)
            var saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
                                     Encoding.UTF8.GetBytes(Normalize(password)),
                                     salt,
                                     iterationCount,
                                     HashOf(mechanism),
                                     KeyLengthOf(mechanism)
                                 );

            // ClientKey  := HMAC(SaltedPassword, "Client Key")
            // StoredKey  := H(ClientKey)
            // ServerKey  := HMAC(SaltedPassword, "Server Key")
            var clientKey = Hmac(mechanism, saltedPassword, "Client Key"u8.ToArray());

            return new SCRAMKeys(StoredKey: Hash(mechanism, clientKey),
                                 ServerKey: Hmac(mechanism, saltedPassword, "Server Key"u8.ToArray()));

        }

        /// <summary>
        /// SASLprep (RFC 4013) - the same preparation as on the client side.
        /// </summary>
        /// <remarks>
        /// That <see cref="SaslPrep"/> stands here and not a computation of its
        /// own is the point: server and client have to win the same key from
        /// the same input. Two versions of the same procedure would be two
        /// opportunities to drift apart, and it would only show up with a
        /// password outside of ASCII.
        /// </remarks>
        internal static String Normalize(String input)
            => SaslPrep.Prepare(input);

        internal static HashAlgorithmName HashOf(SCRAMMechanism mechanism)
            => mechanism == SCRAMMechanism.ScramSha256
                   ? HashAlgorithmName.SHA256
                   : HashAlgorithmName.SHA1;

        internal static Int32 KeyLengthOf(SCRAMMechanism mechanism)
            => mechanism == SCRAMMechanism.ScramSha256 ? 32 : 20;

        internal static Byte[] Hmac(SCRAMMechanism mechanism, Byte[] key, Byte[] data)
            => mechanism == SCRAMMechanism.ScramSha256
                   ? HMACSHA256.HashData(key, data)
                   : HMACSHA1.HashData(key, data);

        internal static Byte[] Hash(SCRAMMechanism mechanism, Byte[] data)
            => mechanism == SCRAMMechanism.ScramSha256
                   ? SHA256.HashData(data)
                   : SHA1.HashData(data);

        #endregion

    }

}
