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
    /// The classic framing per RFC 6120: a single, never closed
    /// <c>&lt;stream:stream&gt;</c> element whose children are the stanzas.
    /// </summary>
    /// <remarks>
    /// The difference to RFC 7395 is bigger than it looks. The stream header is
    /// an <b>open</b> tag: taken by itself it is not well-formed XML, and
    /// everything that comes after it hangs on it for its namespaces. That is
    /// why three are declared here at once - <c>jabber:server</c> as the
    /// default for the stanzas, <c>stream</c> for the stream layer and
    /// <c>db</c> for dialback.
    ///
    /// It is exactly here that a decision from S4b-3 pays off: the dialback
    /// elements are read through a regular expression and not through an XML
    /// parser. A <c>&lt;db:result/&gt;</c> over TCP would not be well-formed
    /// taken on its own, because its prefix hangs on the root element - a
    /// parser that takes every frame by itself would have to fail on it.
    ///
    /// Where RFC 7395 gets finished frames from the transport, here they first
    /// have to be taken apart; that is done by
    /// <see cref="XmlStreamSplitter"/>.
    /// </remarks>
    public sealed class TcpStreamFraming : IS2SFraming
    {

        #region Properties

        /// <summary>
        /// There is nothing to tell apart - one instance suffices.
        /// </summary>
        public static readonly TcpStreamFraming Instance = new();

        /// <summary>
        /// The default namespace of the stanzas on an S2S link (RFC 6120, section 4.8.2).
        /// </summary>
        public const String ContentNamespace = "jabber:server";

        /// <summary>
        /// The namespace of the stream layer.
        /// </summary>
        public const String StreamNamespace = S2SStream.StreamNamespace;

        /// <summary>
        /// The default port for S2S (RFC 6120, section 3.2.1).
        /// </summary>
        public const Int32 DefaultPort = 5269;

        #endregion

        private TcpStreamFraming()
        { }


        #region IS2SFraming

        public String StreamOpen(String from, String? to, String? id)

            => "<stream:stream " +
               $"xmlns='{ContentNamespace}' " +
               $"xmlns:stream='{StreamNamespace}' " +
               $"xmlns:db='{DialbackKey.Namespace}' " +
               $"from='{XmlEscaping.Escape(from)}'" +
               (to is not null ? $" to='{XmlEscaping.Escape(to)}'" : "") +
               (id is not null ? $" id='{XmlEscaping.Escape(id)}'" : "") +
               " version='1.0'>";

        public String StreamClose()
            => "</stream:stream>";

        public Boolean IsStreamOpen(String frame)
            => frame.StartsWith("<stream:stream", StringComparison.Ordinal);

        public Boolean IsStreamClose(String frame)
            => frame.StartsWith("</stream:stream", StringComparison.Ordinal);

        #endregion

    }

}
