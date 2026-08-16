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
    /// The server side of a SCRAM exchange (RFC 5802, RFC 7677) - one object
    /// per login in progress.
    /// </summary>
    /// <remarks>
    /// Deliberately written independently of <see cref="SCRAMAuthenticator"/>,
    /// not as its mirror image. If both sides shared the code, the tests would
    /// check the handshake with the same logic that produces it: a wrongly
    /// assembled <c>AuthMessage</c>, for example, would be equally wrong on
    /// both sides and would show up nowhere.
    ///
    /// Not implemented is channel binding (<c>-PLUS</c>). The server does
    /// check, though, that the client reports the same GS2 header it sent -
    /// otherwise a man in the middle could talk the client out of channel
    /// binding without it being noticed (RFC 5802, section 6).
    /// </remarks>
    internal sealed class SCRAMExchange
    {

        #region Data

        private readonly XMPPAccount? _account;
        private readonly XMPPCredentials _credentials;
        private readonly SCRAMMechanism _mechanism;
        private readonly String _gs2Header;
        private readonly String _clientFirstBare;
        private readonly String _combinedNonce;
        private readonly String _serverFirst;

        /// <summary>
        /// The <c>tls-server-end-point</c> data this server binds to, or null
        /// when it offers no channel binding. Only used when the client asked
        /// for the binding; kept regardless, because "the server could have
        /// bound" is what makes a <c>y</c> header a refusal rather than a
        /// shrug.
        /// </summary>
        private readonly Byte[]? _channelBinding;

        #endregion

        #region Properties

        /// <summary>
        /// The account whose login this is about - or null when the user name
        /// does not exist and the exchange only runs for show.
        /// </summary>
        public XMPPAccount? Account => _account;

        /// <summary>
        /// The server-first-message, ready for <c>&lt;challenge/&gt;</c>.
        /// </summary>
        public String Challenge => Convert.ToBase64String(Encoding.UTF8.GetBytes(_serverFirst));

        #endregion

        #region Constructor(s)

        private SCRAMExchange(XMPPAccount?     account,
                              XMPPCredentials  credentials,
                              SCRAMMechanism   mechanism,
                              String           gs2Header,
                              String           clientFirstBare,
                              String           combinedNonce,
                              String           serverFirst,
                              Byte[]?          channelBinding)
        {
            _account          = account;
            _credentials      = credentials;
            _mechanism        = mechanism;
            _gs2Header        = gs2Header;
            _clientFirstBare  = clientFirstBare;
            _combinedNonce    = combinedNonce;
            _serverFirst      = serverFirst;
            _channelBinding   = channelBinding;
        }

        #endregion


        #region (private) ExpectedChannelBindingInput()

        /// <summary>
        /// What <c>c=</c> has to contain: base64 of the GS2 header, followed by
        /// the binding data when the client bound (RFC 5802, section 6).
        /// </summary>
        /// <remarks>
        /// Only the <c>p=</c> header carries data after it. For <c>n</c> and
        /// <c>y</c> this is the header alone, which is what the literal
        /// comparison here did before channel binding existed.
        /// </remarks>
        private String ExpectedChannelBindingInput()
        {

            var header = Encoding.UTF8.GetBytes(_gs2Header);

            if (_channelBinding is null ||
                !_gs2Header.StartsWith("p=", StringComparison.Ordinal))
                return Convert.ToBase64String(header);

            var buffer = new Byte[header.Length + _channelBinding.Length];

            header.          CopyTo(buffer, 0);
            _channelBinding. CopyTo(buffer, header.Length);

            return Convert.ToBase64String(buffer);

        }

        #endregion

        #region Begin(clientFirstBase64, mechanism, lookup)

        /// <summary>
        /// Takes the client-first-message in and prepares the answer. Null
        /// means: unreadable.
        /// </summary>
        /// <remarks>
        /// An <b>unknown</b> user name is no reason to break off. The exchange
        /// then carries on with invented credentials and fails at the proof -
        /// where it fails with a wrong password too (RFC 6120, section 13.11).
        /// An immediate failure would be the information that the account does
        /// not exist, and that regardless of which error word stands with it.
        /// </remarks>
        /// <param name="clientFirstBase64">Payload of the <c>&lt;auth/&gt;</c>.</param>
        /// <param name="mechanism">The mechanism chosen by the client.</param>
        /// <param name="lookup">Searches for an account by the user name.</param>
        /// <param name="decoy">
        /// Delivers the invented credentials for a name without an account. No
        /// default value: whoever uses this exchange shall have to decide on
        /// the countermeasure and not slip past it.
        /// </param>
        /// <param name="announcedMechanisms">
        /// What this server put into <c>&lt;mechanisms/&gt;</c>. Given, it is
        /// hashed into the server-first-message as the attribute <c>h</c>
        /// (XEP-0474), which the client compares against the list that reached
        /// it. Omitted, the exchange is plain RFC 5802 and a client learns
        /// nothing about whether the announcement was tampered with.
        /// </param>
        public static SCRAMExchange? Begin(String                          clientFirstBase64,
                                           SCRAMMechanism                  mechanism,
                                           Func<String, XMPPAccount?>      lookup,
                                           Func<String, XMPPCredentials>   decoy,
                                           IEnumerable<String>?            announcedMechanisms   = null,
                                           Byte[]?                         channelBinding        = null)
        {

            String clientFirst;

            try
            {
                clientFirst = Encoding.UTF8.GetString(Convert.FromBase64String(clientFirstBase64));
            }
            catch (FormatException)
            {
                return null;
            }

            // GS2 header: "n,," without channel binding and without an authzid,
            // "y,," when the client can do channel binding and thinks the server
            // cannot. Both end after the second comma.
            var headerEnd = NthComma(clientFirst, 2);

            if (headerEnd < 0)
                return null;

            var gs2Header        = clientFirst[..(headerEnd + 1)];
            var clientFirstBare  = clientFirst[(headerEnd + 1)..];

            // RFC 5802, section 6: the first character of the GS2 header says
            // what the client did about channel binding, and each of the three
            // answers has a condition attached.
            //
            //   p=<type>  it bound. The server has to be able to bind too, and
            //             to the same type - anything else and the proof cannot
            //             agree, so refusing here is only saying so earlier.
            //
            //   y         it *can* bind and saw nothing offered. If this server
            //             does offer channel binding, that announcement was
            //             removed on the way: the client is describing a
            //             different <features/> than the one that was sent.
            //             This is the same downgrade XEP-0474 catches, caught a
            //             layer lower and without either end implementing an
            //             experimental XEP.
            //
            //   n         it cannot bind. Nothing to check; the exchange is
            //             plain SCRAM.
            if (gs2Header.StartsWith("p=", StringComparison.Ordinal))
            {

                var type = gs2Header[2..gs2Header.IndexOf(',')];

                if (channelBinding is null ||
                    !String.Equals(type, TlsServerEndPoint.Name, StringComparison.Ordinal))
                    return null;

            }

            else if (gs2Header.StartsWith("y,", StringComparison.Ordinal) &&
                     channelBinding is not null)
                return null;

            var user   = Attribute(clientFirstBare, "n");
            var nonce  = Attribute(clientFirstBare, "r");

            if (user is null || nonce is null || nonce.Length == 0)
                return null;

            var name           = Unescape(user);
            var account        = lookup(name);
            var credentials    = account?.Credentials ?? decoy(name);
            var combinedNonce  = nonce + Nonce();

            var serverFirst = $"r={combinedNonce}," +
                              $"s={Convert.ToBase64String(credentials.Salt)}," +
                              $"i={credentials.IterationCount}";

            // XEP-0474. Appended last and needing nothing else: RFC 5802 puts
            // the server-first-message into the AuthMessage verbatim, so this
            // attribute is already covered by the client proof and the server
            // signature. A man in the middle can shorten the announcement or he
            // can produce a proof, and he cannot do both without the password.
            //
            // The channel-binding types go in whenever this server has a
            // binding, because that is exactly when it announced one under
            // XEP-0440 - and the client hashes what it was announced. Leave
            // them out here and the two sides hash different strings, so every
            // channel-bound login fails looking like a forged announcement.
            if (announcedMechanisms is not null)
                serverFirst += ",h=" + SaslDowngradeProtection.Expected(
                                           mechanism,
                                           announcedMechanisms,
                                           channelBinding is not null
                                               ? [TlsServerEndPoint.Name]
                                               : null);

            return new SCRAMExchange(account,
                                     credentials,
                                     mechanism,
                                     gs2Header,
                                     clientFirstBare,
                                     combinedNonce,
                                     serverFirst,
                                     channelBinding);

        }

        #endregion

        #region Complete(clientFinalBase64)

        /// <summary>
        /// Checks the client-final-message. Back comes the server-final-message
        /// for the <c>&lt;success/&gt;</c>, or null when the proof is not
        /// right.
        /// </summary>
        /// <remarks>
        /// The server computes the <c>ClientKey</c> back out of the proof and
        /// checks whether its hash is the <c>StoredKey</c> kept. For that it
        /// needs neither the password nor the ClientKey itself - precisely for
        /// that reason it does not have to keep either.
        /// </remarks>
        public String? Complete(String clientFinalBase64)
        {

            String clientFinal;

            try
            {
                clientFinal = Encoding.UTF8.GetString(Convert.FromBase64String(clientFinalBase64));
            }
            catch (FormatException)
            {
                return null;
            }

            var proofStart = clientFinal.LastIndexOf(",p=", StringComparison.Ordinal);

            if (proofStart < 0)
                return null;

            var clientFinalWithoutProof  = clientFinal[..proofStart];
            var binding                  = Attribute(clientFinalWithoutProof, "c");
            var nonce                    = Attribute(clientFinalWithoutProof, "r");
            var proofBase64              = clientFinal[(proofStart + 3)..];

            if (binding is null || nonce is null)
                return null;

            // The client has to mirror the nonce of the server back. Without
            // this check an earlier exchange could be replayed.
            if (!String.Equals(nonce, _combinedNonce, StringComparison.Ordinal))
                return null;

            // And it has to report the same GS2 header it sent - together with
            // the binding data, when it bound. This is where channel binding
            // actually bites: a man in the middle relaying the exchange
            // presented his own certificate to the client, so the client hashed
            // *his* into this value, and it cannot equal what the real server
            // computes from its own. He has no way to correct it either - the
            // proof below covers this string.
            if (!String.Equals(binding, ExpectedChannelBindingInput(), StringComparison.Ordinal))
                return null;

            Byte[] proof;

            try
            {
                proof = Convert.FromBase64String(proofBase64);
            }
            catch (FormatException)
            {
                return null;
            }

            // An account can lack the mechanism entirely: credentials read back
            // from a store hold whatever was stored, and a server that only
            // ever computed SHA-1 material has no SHA-256 keys to compare
            // against. KeysOf threw a KeyNotFoundException for that until now -
            // out of a frame a stranger sends, which made it an unhandled
            // exception on the login path rather than a refusal.
            //
            // It is a refusal, and deliberately the same one a wrong password
            // gets: which mechanisms an account has key material for is not
            // something to tell whoever asks.
            if (!_credentials.Has(_mechanism))
                return null;

            var keys = _credentials.KeysOf(_mechanism);

            if (proof.Length != keys.StoredKey.Length)
                return null;

            var authMessage = $"{_clientFirstBare},{_serverFirst},{clientFinalWithoutProof}";
            var authBytes   = Encoding.UTF8.GetBytes(authMessage);

            // ClientSignature := HMAC(StoredKey, AuthMessage)
            // ClientKey       := ClientProof XOR ClientSignature
            var clientSignature = XMPPCredentials.Hmac(_mechanism, keys.StoredKey, authBytes);
            var clientKey       = XOR(proof, clientSignature);

            var correct = CryptographicOperations.FixedTimeEquals(
                              XMPPCredentials.Hash(_mechanism, clientKey),
                              keys.StoredKey);

            // The second condition is a safeguard and not a route: to an
            // invented account belongs a StoredKey from the server key, and
            // whoever does not know that key cannot produce a fitting proof. It
            // stands here all the same, because the price of an error at this
            // spot would be a login without an account.
            if (!correct || _account is null)
                return null;

            // ServerSignature := HMAC(ServerKey, AuthMessage)
            var serverSignature = XMPPCredentials.Hmac(_mechanism, keys.ServerKey, authBytes);

            return Convert.ToBase64String(
                       Encoding.UTF8.GetBytes($"v={Convert.ToBase64String(serverSignature)}"));

        }

        #endregion


        #region (private, static) Helpers

        /// <summary>
        /// Reads the value of an attribute out of a SCRAM message.
        /// </summary>
        /// <remarks>
        /// Anchored at the start or behind a comma. An unanchored search for
        /// <c>i=</c> would otherwise also hit an <c>i=</c> in the middle of the
        /// nonce or the salt - RFC 5802 allows every printable character except
        /// the comma there.
        /// </remarks>
        private static String? Attribute(String message, String name)
        {

            for (var i = 0; i <= message.Length - name.Length - 1; i++)
            {

                if (i > 0 && message[i - 1] != ',')
                    continue;

                if (String.CompareOrdinal(message, i, name, 0, name.Length) != 0)
                    continue;

                if (message[i + name.Length] != '=')
                    continue;

                var valueStart  = i + name.Length + 1;
                var valueEnd    = message.IndexOf(',', valueStart);

                return valueEnd < 0
                           ? message[valueStart..]
                           : message[valueStart..valueEnd];

            }

            return null;

        }

        /// <summary>The position of the n-th comma, or -1.</summary>
        private static Int32 NthComma(String text, Int32 n)
        {

            var position = -1;

            for (var i = 0; i < n; i++)
            {

                position = text.IndexOf(',', position + 1);

                if (position < 0)
                    return -1;

            }

            return position;

        }

        /// <summary>
        /// RFC 5802: in the user name <c>=2C</c> stands for a comma and
        /// <c>=3D</c> for an equals sign.
        /// </summary>
        /// <remarks>
        /// The order is not arbitrary: first the comma, then the equals sign.
        /// The other way round, a transmitted <c>=3D2C</c> - that is, the text
        /// "=2C" - would wrongly become a comma.
        /// </remarks>
        private static String Unescape(String user)
            => user.Replace("=2C", ",").Replace("=3D", "=");

        private static String Nonce()
            => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

        private static Byte[] XOR(Byte[] a, Byte[] b)
        {

            var result = new Byte[a.Length];

            for (var i = 0; i < a.Length; i++)
                result[i] = (Byte) (a[i] ^ b[i]);

            return result;

        }

        #endregion

    }

}
