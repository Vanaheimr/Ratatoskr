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
    /// How a TCP S2S link comes to TLS.
    /// </summary>
    /// <remarks>
    /// The difference between <see cref="Direct"/> and <see cref="StartTls"/>
    /// is not the security but who reaches whom. Both encrypt equally well;
    /// <see cref="StartTls"/> is, however, what RFC 6120, section 5.4 provides
    /// for and what ejabberd and Prosody expect on port 5269.
    /// <see cref="Direct"/> saves a round trip and is the simpler one between
    /// two instances of this server.
    /// </remarks>
    public enum TcpTlsMode
    {

        /// <summary>
        /// Plaintext. Only for fault-finding with a recording - RFC 6120,
        /// section 13.7 demands encryption for S2S.
        /// </summary>
        None,

        /// <summary>
        /// TLS from the first byte, without a negotiation in the stream.
        /// </summary>
        Direct,

        /// <summary>
        /// STARTTLS per RFC 6120, section 5.4: the stream begins in plaintext,
        /// negotiates TLS and starts over afterwards.
        /// </summary>
        StartTls

    }

}
