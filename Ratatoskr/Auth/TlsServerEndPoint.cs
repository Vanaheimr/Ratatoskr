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
using System.Security.Cryptography.X509Certificates;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// RFC 5929, section 4.1: the <c>tls-server-end-point</c> channel binding -
/// the server's certificate, hashed.
/// </summary>
/// <remarks>
/// What channel binding buys is the thing TLS alone does not give: proof that
/// the SASL exchange and the TLS session are the same conversation. A man in
/// the middle with a certificate a trusted CA issued him terminates the
/// client's TLS, opens his own to the real server, and passes SCRAM through
/// untouched - both sides authenticate perfectly and he sits in between. Bind
/// the exchange to the certificate and the relay stops working: the client
/// hashes *his* certificate into its proof, the server checks against *its
/// own*, and the two do not agree.
///
/// <b>Why this binding and not <c>tls-exporter</c>.</b> RFC 9266 is the better
/// one - it survives a certificate being replaced legitimately, and TLS 1.3
/// makes <c>tls-unique</c> unavailable - but it needs the TLS exporter
/// interface, and .NET does not expose it: there is no
/// <c>ExportKeyingMaterial</c> on <c>SslStream</c> in .NET 10 (checked against
/// the reference assembly, 10.0.11). This one needs nothing from the TLS stack
/// at all, only the certificate, which both ends already hold. That is the
/// whole reason it is reachable here and the other is not.
///
/// <b>The limit, stated plainly:</b> it binds to the certificate, not to the
/// session. A man in the middle who holds the server's actual private key -
/// or a certificate for the same key - is not caught by this. Neither is a
/// server that shares one certificate across a fleet behind a load balancer,
/// where the binding is real but not unique to the connection.
/// </remarks>
public static class TlsServerEndPoint
{

    #region Data

    /// <summary>
    /// The name this binding carries in SASL (RFC 5929, section 4.1).
    /// </summary>
    public const String Name = "tls-server-end-point";

    #endregion


    #region (static) HashAlgorithmFor(SignatureAlgorithmOid)

    /// <summary>
    /// Which hash the binding uses for a certificate signed with this
    /// algorithm, or null when RFC 5929 leaves it undefined.
    /// </summary>
    /// <remarks>
    /// The rule is "the hash of the certificate's signature algorithm, except
    /// that MD5 and SHA-1 are both replaced by SHA-256". The exception is the
    /// point: a binding computed with a broken hash would let an attacker who
    /// can produce a colliding certificate produce a colliding binding with it.
    ///
    /// Null is returned rather than a guess where the signature carries no hash
    /// this can read - Ed25519 and Ed448 have none to read, and RSASSA-PSS
    /// keeps it in the parameters rather than in the OID. RFC 5929 calls the
    /// binding undefined in that case, and inventing SHA-256 there would
    /// produce a value some other implementation computes differently, which
    /// fails a login that nothing was wrong with. Not offering the binding is
    /// the honest outcome; the SASL exchange then proceeds without it.
    /// </remarks>
    public static HashAlgorithmName? HashAlgorithmFor(String? SignatureAlgorithmOid)

        => SignatureAlgorithmOid switch {

               // MD5 and SHA-1, in RSA, DSA and ECDSA spellings - all promoted.
               "1.2.840.113549.1.1.4"   or        // md5WithRSAEncryption
               "1.2.840.113549.1.1.5"   or        // sha1WithRSAEncryption
               "1.2.840.10040.4.3"      or        // dsa-with-sha1
               "1.2.840.10045.4.1"      => HashAlgorithmName.SHA256,

               "1.2.840.113549.1.1.11"  or        // sha256WithRSAEncryption
               "2.16.840.1.101.3.4.3.2" or        // dsa-with-sha256
               "1.2.840.10045.4.3.2"    => HashAlgorithmName.SHA256,

               "1.2.840.113549.1.1.12"  or        // sha384WithRSAEncryption
               "1.2.840.10045.4.3.3"    => HashAlgorithmName.SHA384,

               "1.2.840.113549.1.1.13"  or        // sha512WithRSAEncryption
               "1.2.840.10045.4.3.4"    => HashAlgorithmName.SHA512,

               _                        => null

           };

    #endregion

    #region (static) For(Certificate)

    /// <summary>
    /// The binding data for this certificate, or null when RFC 5929 defines
    /// none for the way it was signed.
    /// </summary>
    /// <remarks>
    /// Over <c>RawData</c>, which is the DER encoding of the whole certificate
    /// - the same bytes that went across the wire, not a re-encoding of the
    /// parsed structure. Anything else would hash to a different value on the
    /// two ends whenever an encoder disagreed about an optional field.
    /// </remarks>
    public static Byte[]? For(X509Certificate2? Certificate)
    {

        if (Certificate is null)
            return null;

        var algorithm = HashAlgorithmFor(Certificate.SignatureAlgorithm?.Value);

        if (algorithm is null)
            return null;

        return algorithm.Value.Name switch {
                   nameof(HashAlgorithmName.SHA384)  => SHA384.HashData(Certificate.RawData),
                   nameof(HashAlgorithmName.SHA512)  => SHA512.HashData(Certificate.RawData),
                   _                                 => SHA256.HashData(Certificate.RawData)
               };

    }

    #endregion

}
