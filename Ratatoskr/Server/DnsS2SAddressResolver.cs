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

using org.GraphDefined.Vanaheimr.Hermod.DNS;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    /// <summary>
    /// Finds the S2S service of a domain through DNS (RFC 6120,
    /// section 3.2.1).
    /// </summary>
    /// <remarks>
    /// Asked for is <c>_xmpp-server._tcp.&lt;domain&gt;</c>. If the answer
    /// fails to come, the fallback from section 3.2.1 holds: the domain itself
    /// on the standard port 5269. An explicit <c>.</c> as a target, by
    /// contrast, is not silence but a rejection - then nothing is tried.
    ///
    /// <b>Not asked for is <c>_xmpps-server._tcp</c></b> (XEP-0368, direct TLS
    /// without STARTTLS). It could be added as soon as somebody needs it; the
    /// choice between the two services would then be a decision of its own and
    /// not an extension of this query.
    /// </remarks>
    public sealed class DnsS2SAddressResolver : IS2SAddressResolver
    {

        #region Data

        private readonly IDNSClient _dns;

        /// <summary>The service name from RFC 6120, section 3.2.1.</summary>
        public const String ServicePrefix = "_xmpp-server._tcp.";

        /// <summary>The standard port when there is no SRV record.</summary>
        public const Int32 DefaultPort = TcpStreamFraming.DefaultPort;

        #endregion

        #region Properties

        /// <summary>
        /// How long an answer is waited for.
        /// </summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Shall a missing SRV record fall back to the domain itself?
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 3.2.1 provides for it. Switchable off, because an
        /// operator who wants to permit exclusively targets published through
        /// SRV would otherwise be connected somewhere else silently.
        /// </remarks>
        public Boolean FallBackToDomain { get; init; } = true;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Creates the resolver over a DNS client.
        /// </summary>
        public DnsS2SAddressResolver(IDNSClient dnsClient)
        {
            _dns = dnsClient;
        }

        #endregion


        #region ResolveAsync(domain, cancellationToken)

        public async Task<IReadOnlyList<SrvTarget>> ResolveAsync(String             domain,
                                                                 CancellationToken  cancellationToken = default)
        {

            var entries = new List<SrvTarget>();

            try
            {

                var name = DNSServiceName.Parse($"{ServicePrefix}{domain}");

                var answer = await _dns.Query<SRV>(name,
                                                   Timeout:            Timeout,
                                                   CancellationToken:  cancellationToken);

                foreach (var srv in answer.FilteredAnswers)
                    entries.Add(new SrvTarget(srv.Priority,
                                              srv.Weight,
                                              srv.Target.ToString().TrimEnd('.') is { Length: > 0 } t
                                                  ? t
                                                  : SrvSelection.NoService,
                                              srv.Port.ToUInt16()));

            }
            catch (Exception)
            {
                // No DNS, no answer, a broken answer - for the caller that is
                // the same thing as "no SRV record".
                entries.Clear();
            }

            if (entries.Count > 0)
                return SrvSelection.Order(entries);

            // RFC 6120, section 3.2.1: without an SRV record the domain itself.
            return FallBackToDomain
                       ? [new SrvTarget(0, 0, domain, DefaultPort)]
                       : [];

        }

        #endregion

    }

}
