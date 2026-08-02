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
    /// How an S2S stream is wrapped - the only difference between the WebSocket
    /// and the TCP link that the protocol layer has to know about.
    /// </summary>
    /// <remarks>
    /// This interface is the price for a claim from S4b-1:
    /// <see cref="S2SStream"/> was supposed to stay unchanged for TCP. It did
    /// not. In five places the framing per RFC 7395 stood hard-wired in the
    /// code - <c>&lt;open/&gt;</c>, <c>&lt;close/&gt;</c> and the two
    /// detections belonging to them. The abstraction had thus taken on the
    /// shape of its first implementation, exactly as noted down as a risk in
    /// the work plan. What <i>did</i> hold is everything else: the handshake
    /// sequence, dialback, the sender check, the error handling, the life
    /// cycle.
    ///
    /// Deliberately kept small. Everything that does not stand here is common
    /// to both links - among other things that stanzas go out without a
    /// namespace of their own. Over TCP they thereby inherit the default
    /// namespace <c>jabber:server</c> of the stream root element, which is
    /// exactly right; over WebSocket every frame carries for itself anyway.
    /// </remarks>
    public interface IS2SFraming
    {

        /// <summary>
        /// The stream header.
        /// </summary>
        /// <param name="from">One's own domain.</param>
        /// <param name="to">The domain of the peer.</param>
        /// <param name="id">
        /// The stream ID handed out - only the answering server sets it.
        /// </param>
        String StreamOpen(String from, String? to, String? id);

        /// <summary>The end of the stream.</summary>
        String StreamClose();

        /// <summary>Is this frame a stream header?</summary>
        Boolean IsStreamOpen(String frame);

        /// <summary>Is this frame the end of the stream?</summary>
        Boolean IsStreamClose(String frame);

    }

}
