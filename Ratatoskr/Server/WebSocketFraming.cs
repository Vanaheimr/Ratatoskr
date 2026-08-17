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
    /// The framing per RFC 7395: a WebSocket frame is exactly one element, the
    /// stream is opened with <c>&lt;open/&gt;</c> and closed with
    /// <c>&lt;close/&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Every frame stands for itself and carries its namespaces itself - there
    /// is no root element that could pass anything on.
    /// </remarks>
    public sealed class WebSocketFraming : IS2SFraming
    {

        #region Properties

        /// <summary>
        /// There is nothing to tell apart - one instance suffices.
        /// </summary>
        public static readonly WebSocketFraming Instance = new();

        /// <summary>
        /// The namespace of the framing (RFC 7395, section 3.1).
        /// </summary>
        public const String Namespace = "urn:ietf:params:xml:ns:xmpp-framing";

        #endregion

        private WebSocketFraming()
        { }


        #region IS2SFraming

        public String StreamOpen(String from, String? to, String? id)

            => $"<open xmlns='{Namespace}' " +
               $"from='{XmlEscaping.Escape(from)}'" +
               (to is not null ? $" to='{XmlEscaping.Escape(to)}'" : "") +
               (id is not null ? $" id='{XmlEscaping.Escape(id)}'" : "") +
               " version='1.0'/>";

        public String StreamClose()
            => $"<close xmlns='{Namespace}'/>";

        // By the element name and not by the prefix: <opencast/> is not a stream
        // opening, <closet/> not a farewell.
        public Boolean IsStreamOpen(String frame)
            => StanzaElement.Is(frame, "open");

        public Boolean IsStreamClose(String frame)
            => StanzaElement.Is(frame, "close");

        #endregion

    }

}
