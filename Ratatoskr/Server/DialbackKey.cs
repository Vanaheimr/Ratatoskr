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
    /// The dialback key from XEP-0220, section 2.1.1 (the procedure from
    /// XEP-0185).
    /// </summary>
    /// <remarks>
    /// <code>
    /// key = HMAC-SHA256(SHA256(Secret), { Target Domain, ' ', Sender Domain, ' ', Stream ID })
    /// </code>
    ///
    /// Two things about it are easy to get wrong, and neither would have been
    /// noticed without the published vector:
    ///
    /// <list type="number">
    ///   <item>
    ///     <b><c>SHA256(Secret)</c> goes into the HMAC as a hex string, not as
    ///     raw bytes.</b> The obvious reading - the digest as 32 bytes -
    ///     delivers a different result. Both versions are consistent in
    ///     themselves; two servers that decide on different ones would never
    ///     come together without either of them making a mistake.
    ///   </item>
    ///   <item>
    ///     <b>The order is target before sender domain</b>, that is, the
    ///     receiving one first. Swapped, it would likewise yield a
    ///     valid-looking key.
    ///   </item>
    /// </list>
    ///
    /// The terms are those of the XEP and are deliberately kept here:
    /// <b>sender domain</b> is the domain the establishing server wants to
    /// speak for, <b>target domain</b> that of the accepting one. From the
    /// point of view of the accepting server, target is therefore its own.
    ///
    /// The domains go in <b>unchanged</b>, without a normalisation of the
    /// upper/lower case. That is intentional: the checking server passes on the
    /// values the establishing one wrote into its addressing, and the
    /// authoritative one recomputes from exactly the same ones. Were it
    /// normalised here, the result would depend on whether both sides apply the
    /// same normalisation - one more way to miss each other without anybody
    /// gaining anything.
    /// </remarks>
    public static class DialbackKey
    {

        #region Properties

        /// <summary>
        /// The namespace of XEP-0220.
        /// </summary>
        public const String Namespace = "jabber:server:dialback";

        #endregion

        #region Generate(secret, targetDomain, senderDomain, streamId)

        /// <summary>
        /// Produces the dialback key.
        /// </summary>
        /// <param name="secret">
        /// The secret of the establishing server. Only it knows the secret;
        /// precisely for that reason only it can produce a key it later
        /// recognises again as the authoritative server.
        /// </param>
        /// <param name="targetDomain">The domain of the accepting server.</param>
        /// <param name="senderDomain">The domain that is to be spoken for.</param>
        /// <param name="streamId">
        /// The stream ID the accepting server handed out in its stream header.
        /// It binds the key to this one connection - without it a key recorded
        /// once could be reused at will.
        /// </param>
        public static String Generate(String  secret,
                                      String  targetDomain,
                                      String  senderDomain,
                                      String  streamId)
        {

            var hmacKey  = Encoding.UTF8.GetBytes(
                               Convert.ToHexStringLower(
                                   SHA256.HashData(Encoding.UTF8.GetBytes(secret))));

            var message  = Encoding.UTF8.GetBytes($"{targetDomain} {senderDomain} {streamId}");

            return Convert.ToHexStringLower(HMACSHA256.HashData(hmacKey, message));

        }

        #endregion

        #region Verify(secret, targetDomain, senderDomain, streamId, presentedKey)

        /// <summary>
        /// Checks a dialback key that was presented.
        /// </summary>
        /// <remarks>
        /// Compared through <see cref="CryptographicOperations.FixedTimeEquals"/>
        /// on the decoded bytes. The detour through
        /// <see cref="Convert.FromHexString(String)"/> takes the upper/lower
        /// case of the hex along in the process, without a character-by-character
        /// - and thereby temporally telltale - comparison being needed.
        /// </remarks>
        /// <returns>false also when the key is not valid hex at all.</returns>
        public static Boolean Verify(String  secret,
                                     String  targetDomain,
                                     String  senderDomain,
                                     String  streamId,
                                     String  presentedKey)
        {

            Byte[] presented;

            try
            {
                presented = Convert.FromHexString(presentedKey.Trim());
            }
            catch (FormatException)
            {
                return false;
            }

            var expected = Convert.FromHexString(
                               Generate(secret, targetDomain, senderDomain, streamId));

            return CryptographicOperations.FixedTimeEquals(expected, presented);

        }

        #endregion

        #region NewSecret()

        /// <summary>
        /// Produces a secret for a server that was not given one.
        /// </summary>
        /// <remarks>
        /// A random secret per process suffices for dialback: it only has to
        /// stay the same as long as a stream lives, and must be known to nobody
        /// except this server. Whoever runs several instances of the same
        /// domain has to share it, though - otherwise the instance answering
        /// the verification could not recompute the key of the instance that
        /// issued it.
        /// </remarks>
        public static String NewSecret()
            => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        #endregion

    }

}
