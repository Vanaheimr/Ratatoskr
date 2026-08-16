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

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// SCRAM-SHA-1 and SCRAM-SHA-256 authentication (RFC 5802)
///
/// Sequence:
/// 1. Client → Server: client-first-message (username, client nonce)
/// 2. Server → Client: server-first-message (combined nonce, salt, iterations)
/// 3. Client → Server: client-final-message (channel binding, proof)
/// 4. Server → Client: server-final-message (server signature)
/// </summary>
public sealed class SCRAMAuthenticator
{

    /// <summary>
    /// The fewest iterations this client will compute with.
    /// </summary>
    /// <remarks>
    /// RFC 7677, section 4 asks for at least 4096 for SCRAM-SHA-256, and RFC
    /// 5802 names the same number for SCRAM-SHA-1. Both say SHOULD; here it is
    /// a MUST, and the reason is that the value arrives from the party this
    /// mechanism exists to distrust. Nothing signs the server-first-message.
    /// </remarks>
    public const Int32 MinimumIterations = 4096;

    /// <summary>
    /// The most.
    /// </summary>
    /// <remarks>
    /// No specification names an upper bound, because no specification worried
    /// about a server attacking its own client. One frame carrying
    /// <c>i=2147483647</c> keeps this process in PBKDF2 for hours, and it costs
    /// whoever sends it nothing at all - they only have to write the number
    /// down. A million is far above what any real deployment uses and far below
    /// where the wait stops being a wait.
    /// </remarks>
    public const Int32 MaximumIterations = 1_000_000;

    private readonly string _username;
    private readonly string _password;
    private readonly SCRAMMechanism _mechanism;

    private string? _clientNonce;
    private string? _clientFirstMessageBare;
    private string? _serverFirstMessage;
    private byte[]? _saltedPassword;

    /// <summary>
    /// The announcement this client received, for XEP-0474. Null when the
    /// caller did not record it, in which case an <c>h</c> from the server
    /// cannot be checked against anything.
    /// </summary>
    private readonly string[]? _offeredMechanisms;
    private readonly string[]? _offeredChannelBindings;

    /// <summary>
    /// What became of the downgrade protection - readable once
    /// <see cref="ProcessServerFirstMessage"/> has run.
    /// </summary>
    /// <remarks>
    /// <see cref="SaslDowngradeProtectionResult.Mismatch"/> never survives to
    /// be read here: it throws. The property exists for the other two, and
    /// telling them apart is the point - a login that verified the announcement
    /// and one that never asked look identical from the outside.
    /// </remarks>
    public SaslDowngradeProtectionResult DowngradeProtection { get; private set; }
        = SaslDowngradeProtectionResult.NotOffered;

    public SCRAMAuthenticator(string username, string password, SCRAMMechanism mechanism = SCRAMMechanism.ScramSha1)

        : this(username, password, mechanism, null, null)

    { }

    /// <summary>
    /// As above, and told what the server announced, so that XEP-0474 can be
    /// checked.
    /// </summary>
    /// <param name="offeredMechanisms">
    /// Every mechanism out of <c>&lt;mechanisms/&gt;</c>, including the ones
    /// this implementation cannot use - the hash covers the offer, not the
    /// choice.
    /// </param>
    /// <param name="offeredChannelBindings">
    /// The types out of XEP-0440, or null when the server announced none.
    /// </param>
    public SCRAMAuthenticator(string                username,
                              string                password,
                              SCRAMMechanism        mechanism,
                              IEnumerable<string>?  offeredMechanisms,
                              IEnumerable<string>?  offeredChannelBindings)
    {
        _username                = SaslPrep(username);
        _password                = SaslPrep(password);
        _mechanism               = mechanism;
        _offeredMechanisms       = offeredMechanisms?.     ToArray();
        _offeredChannelBindings  = offeredChannelBindings?.ToArray();
    }

    /// <summary>
    /// For tests only: forces a fixed client nonce, so that the test vectors
    /// from RFC 5802 section 5 and RFC 7677 section 3 can be recomputed. In
    /// operation the value stays null and the nonce comes from
    /// <see cref="RandomNumberGenerator"/>.
    /// </summary>
    internal string? FixedClientNonce { get; set; }

    public string MechanismName => _mechanism switch
    {
        SCRAMMechanism.ScramSha1 => "SCRAM-SHA-1",
        SCRAMMechanism.ScramSha256 => "SCRAM-SHA-256",
        _ => "SCRAM-SHA-1"
    };

    /// <summary>
    /// Step 1: Generates the client-first-message
    /// </summary>
    public string CreateClientFirstMessage()
    {
        _clientNonce = FixedClientNonce ?? GenerateNonce();

        // n=username,r=nonce
        _clientFirstMessageBare = $"n={EscapeUsername(_username)},r={_clientNonce}";

        // GS2 header: n,, (no channel binding, no authzid)
        // Complete message: n,,n=user,r=nonce
        var clientFirstMessage = $"n,,{_clientFirstMessageBare}";

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(clientFirstMessage));
    }

    /// <summary>
    /// Step 2: Processes the server-first-message and generates the client-final-message
    /// </summary>
    public string ProcessServerFirstMessage(string serverFirstMessageBase64)
    {
        _serverFirstMessage = Encoding.UTF8.GetString(Convert.FromBase64String(serverFirstMessageBase64));

        // Parse: r=combinedNonce,s=salt,i=iterations
        var serverNonce = ExtractValue(_serverFirstMessage, "r");
        var saltBase64 = ExtractValue(_serverFirstMessage, "s");
        var iterationsStr = ExtractValue(_serverFirstMessage, "i");

        if (serverNonce == null || saltBase64 == null || iterationsStr == null)
            throw new AuthenticationException($"Invalid server-first-message: {_serverFirstMessage}");

        // Verify nonce starts with client nonce
        if (!serverNonce.StartsWith(_clientNonce!))
            throw new AuthenticationException("The server nonce does not contain the client nonce - possible MITM attack!");

        // XEP-0474, and before the key derivation rather than after it: a
        // forged announcement is refused for the price of one hash, where
        // PBKDF2 over the iteration count the same server just named is the
        // most expensive thing in this exchange.
        //
        // No constant-time comparison, deliberately. Both sides of it are
        // public - the announcement arrived in the clear and the hash is
        // unkeyed - so there is no secret for a timing difference to leak, and
        // FixedTimeEquals here would only suggest to the next reader that there
        // is one.
        VerifyDowngradeProtection(ExtractValue(_serverFirstMessage, "h"));

        var salt = Convert.FromBase64String(saltBase64);
        var iterations = ReadIterationCount(iterationsStr);

        // Compute SaltedPassword = Hi(password, salt, iterations)
        _saltedPassword = Hi(_password, salt, iterations);

        // ClientKey = HMAC(SaltedPassword, "Client Key")
        var clientKey = HmacCompute(_saltedPassword, "Client Key");

        // StoredKey = H(ClientKey)
        var storedKey = HashCompute(clientKey);

        // channel-binding-data = base64("n,,")
        var channelBinding = Convert.ToBase64String(Encoding.UTF8.GetBytes("n,,"));

        // client-final-message-without-proof = c=channelBinding,r=serverNonce
        var clientFinalWithoutProof = $"c={channelBinding},r={serverNonce}";

        // AuthMessage = client-first-message-bare + "," + server-first-message + "," + client-final-without-proof
        var authMessage = $"{_clientFirstMessageBare},{_serverFirstMessage},{clientFinalWithoutProof}";

        // ClientSignature = HMAC(StoredKey, AuthMessage)
        var clientSignature = HmacCompute(storedKey, authMessage);

        // ClientProof = ClientKey XOR ClientSignature
        var clientProof = XOR(clientKey, clientSignature);

        // client-final-message = client-final-without-proof + ",p=" + base64(ClientProof)
        var clientFinalMessage = $"{clientFinalWithoutProof},p={Convert.ToBase64String(clientProof)}";

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(clientFinalMessage));
    }

    /// <summary>
    /// Step 3: Verifies the server-final-message
    /// </summary>
    public bool VerifyServerFinalMessage(string serverFinalMessageBase64)
    {
        var serverFinalMessage = Encoding.UTF8.GetString(Convert.FromBase64String(serverFinalMessageBase64));

        // Check for error
        if (serverFinalMessage.StartsWith("e="))
        {
            var error = ExtractValue(serverFinalMessage, "e");
            throw new AuthenticationException($"SCRAM error: {error}");
        }

        // Parse: v=serverSignature
        var serverSignatureBase64 = ExtractValue(serverFinalMessage, "v");
        if (serverSignatureBase64 == null)
            throw new AuthenticationException($"Invalid server-final-message: {serverFinalMessage}");

        var receivedSignature = Convert.FromBase64String(serverSignatureBase64);

        // Compute the expected ServerSignature
        // ServerKey = HMAC(SaltedPassword, "Server Key")
        var serverKey = HmacCompute(_saltedPassword!, "Server Key");

        // Reconstruct the AuthMessage
        var channelBinding = Convert.ToBase64String(Encoding.UTF8.GetBytes("n,,"));
        var serverNonce = ExtractValue(_serverFirstMessage!, "r");
        var clientFinalWithoutProof = $"c={channelBinding},r={serverNonce}";
        var authMessage = $"{_clientFirstMessageBare},{_serverFirstMessage},{clientFinalWithoutProof}";

        // ServerSignature = HMAC(ServerKey, AuthMessage)
        var expectedSignature = HmacCompute(serverKey, authMessage);

        // Constant-time comparison
        return CryptographicOperations.FixedTimeEquals(receivedSignature, expectedSignature);
    }

    // ===== Crypto Helpers =====

    /// <summary>
    /// The iteration count from the server-first-message, examined before
    /// anything is computed with it.
    /// </summary>
    /// <remarks>
    /// <b>This number comes from the party SCRAM is built against.</b> Nothing
    /// signs the server-first-message; whoever sits on the connection writes
    /// what they like into it, and it lands unchecked in a key derivation.
    /// Both directions hurt, and each in its own way:
    ///
    /// <c>i=1</c> makes the derivation cheap - and it is cheap for whoever
    /// afterwards guesses the password from the recorded handshake, not for the
    /// login, which succeeds either way. A downgrade nobody notices, because
    /// nothing about it looks wrong.
    ///
    /// <c>i=2147483647</c> costs nothing to send and hours to compute. One
    /// frame, and this process is busy.
    ///
    /// The parsing is strict for a reason of its own: RFC 5802 allows digits
    /// there and nothing else. <c>Int32.Parse</c> took a leading minus, and
    /// PBKDF2 then threw an ArgumentOutOfRangeException out of the middle of
    /// the handshake - an error about a parameter, where the truth was that the
    /// far side had sent nonsense.
    /// </remarks>
    /// <exception cref="AuthenticationException">
    /// When the count is no number, or lies outside the window.
    /// </exception>
    private static Int32 ReadIterationCount(String Value)
    {

        // NumberStyles.None: no sign, no space, no thousands separator. All
        // three would be a spelling variant of something the grammar does not
        // have.
        if (!Int32.TryParse(Value, NumberStyles.None, CultureInfo.InvariantCulture, out var iterations))
            throw new AuthenticationException(
                      $"The server names '{Value}' as its SCRAM iteration count. RFC 5802 has " +
                      $"digits there and nothing else.");

        if (iterations < MinimumIterations)
            throw new AuthenticationException(
                      $"The server names {iterations} SCRAM iterations; at least " +
                      $"{MinimumIterations} are required (RFC 7677, section 4). A count this low " +
                      $"is what somebody sets who wants to read the password out of a recorded " +
                      $"handshake afterwards - the login itself succeeds either way.");

        if (iterations > MaximumIterations)
            throw new AuthenticationException(
                      $"The server names {iterations} SCRAM iterations; at most " +
                      $"{MaximumIterations} are computed. Beyond that the login is no longer a " +
                      $"login but a wait that the far side sets the length of.");

        return iterations;

    }

    private byte[] Hi(string password, byte[] salt, int iterations)
    {
        // PBKDF2 with SHA-1 or SHA-256
        var hashName = _mechanism == SCRAMMechanism.ScramSha256
            ? HashAlgorithmName.SHA256
            : HashAlgorithmName.SHA1;

        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            hashName,
            hashName == HashAlgorithmName.SHA256 ? 32 : 20
        );
    }

    private byte[] HmacCompute(byte[] key, string data)
    {
        return HmacCompute(key, Encoding.UTF8.GetBytes(data));
    }

    private byte[] HmacCompute(byte[] key, byte[] data)
    {
        using var hmac = _mechanism == SCRAMMechanism.ScramSha256
            ? (HMAC)new HMACSHA256(key)
            : new HMACSHA1(key);

        return hmac.ComputeHash(data);
    }

    private byte[] HashCompute(byte[] data)
    {
        return _mechanism == SCRAMMechanism.ScramSha256
            ? SHA256.HashData(data)
            : SHA1.HashData(data);
    }

    private static byte[] XOR(byte[] a, byte[] b)
    {
        var result = new byte[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            result[i] = (byte)(a[i] ^ b[i]);
        }
        return result;
    }

    private static string GenerateNonce()
    {
        var bytes = new byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// XEP-0474: does the <c>h</c> the server sent describe the announcement
    /// that arrived here?
    /// </summary>
    /// <param name="offered">
    /// The value of <c>h</c>, or null when the server sent none.
    /// </param>
    /// <exception cref="SaslDowngradeException">
    /// When it describes a different announcement.
    /// </exception>
    /// <remarks>
    /// Two ways to end up unverified, and neither is an error:
    ///
    /// The server sent no <c>h</c> - which is every server that has not
    /// implemented an experimental XEP. Refusing those would be refusing almost
    /// all of them, so the lower bounds in <c>SaslMechanismPolicy</c> remain
    /// what covers this case; they need nothing from the far side.
    ///
    /// Or nobody told this authenticator what was announced. That is a caller
    /// which did not record the offer, not a server which did anything wrong,
    /// and inventing a list here to compare against would turn a missing input
    /// into a fabricated verdict.
    /// </remarks>
    private void VerifyDowngradeProtection(string? offered)
    {

        if (offered is null || _offeredMechanisms is null)
        {
            DowngradeProtection = SaslDowngradeProtectionResult.NotOffered;
            return;
        }

        var expected = SaslDowngradeProtection.Expected(_mechanism,
                                                        _offeredMechanisms,
                                                        _offeredChannelBindings);

        if (!String.Equals(offered, expected, StringComparison.Ordinal))
        {

            DowngradeProtection = SaslDowngradeProtectionResult.Mismatch;

            throw new SaslDowngradeException(
                      "SASL downgrade fended off (XEP-0474): the server signed a different " +
                      "list of mechanisms than the one that arrived here. What was announced " +
                      "to this client was " +
                      $"'{String.Join(", ", _offeredMechanisms)}' - somebody in between has " +
                      "taken something out of it.",
                      Offered:   String.Join(", ", _offeredMechanisms),
                      Demanded:  MechanismName,
                      Cause:     SaslDowngradeCause.ForgedAnnouncement);

        }

        DowngradeProtection = SaslDowngradeProtectionResult.Verified;

    }

    private static string? ExtractValue(string message, string key)
    {
        // Anchored at the start or behind a comma: otherwise the search for
        // "i=" also hits an 'i=' inside the nonce or the salt (RFC 5802 allows
        // every printable character except the comma in there).
        var match = Regex.Match(message, $@"(?:^|,){key}=([^,]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string EscapeUsername(string username)
    {
        // RFC 5802: = → =3D, , → =2C
        return username
            .Replace("=", "=3D")
            .Replace(",", "=2C");
    }

    /// <summary>
    /// RFC 5802, section 5.1: user name and password go through SASLprep
    /// before a key is derived from them.
    /// </summary>
    private static string SaslPrep(string input)
        => Ratatoskr.SaslPrep.Prepare(input);
}
