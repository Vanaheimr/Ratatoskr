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

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// The peer did not keep to the protocol while the connection was being
/// established, or refused a step.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="AuthenticationException"/>: that one prevents
/// every reconnect, because a wrong password is just as wrong on the next
/// attempt. A refused resource binding, by contrast, can be due to an occupied
/// resource and may work on the next attempt.
/// </remarks>
public class XMPPProtocolException : Exception
{

    public XMPPProtocolException(string message)
        : base(message)
    { }

    public XMPPProtocolException(string message, Exception inner)
        : base(message, inner)
    { }

}
