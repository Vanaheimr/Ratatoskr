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

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    /// <summary>
    /// A target from an SRV record (RFC 2782): where a service of a domain can
    /// be reached.
    /// </summary>
    /// <param name="Priority">
    /// Smaller is better. Targets of a higher priority are only tried once all
    /// lower numbers are exhausted.
    /// </param>
    /// <param name="Weight">
    /// Distributes the load within the same priority. Zero does not mean
    /// "never" but "only if chance wills it" - RFC 2782 gives weightless
    /// targets a chance too.
    /// </param>
    /// <param name="Host">The host name that is connected to.</param>
    /// <param name="Port">The port.</param>
    /// <remarks>
    /// <b>An SRV record says where something lies - not who answers there.</b>
    /// DNS is not authenticated without DNSSEC; whoever can forge the
    /// resolution redirects the connection. That is why the identity of the
    /// peer stays bound to what it presents: the certificate is checked against
    /// the <i>domain sought</i> and not against the host name named here
    /// (RFC 6120, section 13.7.2.1). Otherwise a forged SRV record would
    /// suffice to pass every check - one would let the attacker bring along the
    /// yardstick they are measured by.
    /// </remarks>
    public sealed record SrvTarget(UInt16  Priority,
                                   UInt16  Weight,
                                   String  Host,
                                   Int32   Port)
    {

        public override String ToString()
            => $"{Host}:{Port} (priority {Priority}, weight {Weight})";

    }

}
