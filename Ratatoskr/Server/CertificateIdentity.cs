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

using System.Security.Cryptography.X509Certificates;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    /// <summary>
    /// Which domains a certificate may speak for - the question SASL-EXTERNAL
    /// rests on (XEP-0178, section 3).
    /// </summary>
    /// <remarks>
    /// That is the difference between SASL-EXTERNAL and dialback. Dialback
    /// proves a domain by asking back at the address on record; SASL-EXTERNAL
    /// proves it by reading the certificate the peer presented in the TLS
    /// handshake. No second connection, no asking back - but then everything
    /// stands and falls with this check.
    ///
    /// <b>What is deliberately strict here:</b>
    ///
    /// <list type="bullet">
    ///   <item>
    ///     <b>If a SAN extension exists, the common name no longer counts.</b>
    ///     That is how RFC 6125, section 6.4.4 wants it, and the reason is
    ///     tangible: otherwise a certificate with a fitting CN and harmless
    ///     SANs would suffice to pass every check that still falls back on the
    ///     CN.
    ///   </item>
    ///   <item>
    ///     <b>No wildcards.</b> <c>*.example.com</c> counts here for not a
    ///     single domain. XEP-0178 leaves wildcards optional; handling them
    ///     correctly is surprisingly error-prone, and too generous a reading
    ///     gives away exactly the precision this class exists for.
    ///   </item>
    /// </list>
    ///
    /// <b>What is missing:</b> <c>id-on-xmppAddr</c> (OID 1.3.6.1.5.5.7.8.5) is
    /// not read, although XEP-0178 names it as the form actually intended. It
    /// sits as an <c>otherName</c> in the SAN extension, and the library only
    /// enumerates dNSName and IP addresses; reading it would mean taking ASN.1
    /// apart by hand. The consequence is to be named: a peer whose certificate
    /// identifies it <i>only</i> through <c>xmppAddr</c> is refused here
    /// although it is in the right. For it dialback remains.
    /// </remarks>
    public static class CertificateIdentity
    {

        #region Data

        /// <summary>OID of the subject alternative name extension.</summary>
        private const String SubjectAlternativeNameOid = "2.5.29.17";

        #endregion

        #region DomainsOf(certificate)

        /// <summary>
        /// The domains this certificate is issued for.
        /// </summary>
        /// <returns>
        /// The dNSName entries of the SAN extension; failing that the common
        /// name, but <b>only</b> when there is no SAN extension at all.
        /// </returns>
        public static IReadOnlyList<String> DomainsOf(X509Certificate2 certificate)
        {

            var san = certificate.Extensions
                                 .FirstOrDefault(e => e.Oid?.Value == SubjectAlternativeNameOid);

            if (san is not null)
            {

                try
                {

                    var names = new X509SubjectAlternativeNameExtension(san.RawData, san.Critical);

                    // An empty list is a result too: the extension exists, it
                    // just names no domain. Falling back on the common name
                    // would be exactly what RFC 6125 forbids.
                    return [.. names.EnumerateDnsNames()];

                }
                catch (Exception)
                {
                    // An unreadable extension - then no domain counts as proven.
                    return [];
                }

            }

            var commonName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

            return String.IsNullOrWhiteSpace(commonName)
                       ? []
                       : [commonName];

        }

        #endregion

        #region Authorises(certificate, domain)

        /// <summary>
        /// May this certificate speak for this domain?
        /// </summary>
        /// <remarks>
        /// Compared without regard to upper and lower case - domain names
        /// cannot be told apart by that - but otherwise exactly.
        /// </remarks>
        public static Boolean Authorises(X509Certificate2 certificate, String domain)

            => !String.IsNullOrWhiteSpace(domain) &&
               DomainsOf(certificate).Any(d => String.Equals(d, domain, StringComparison.OrdinalIgnoreCase));

        #endregion

    }

}
