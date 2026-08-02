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
/// The kind of a message (RFC 6121, section 5.2.2).
/// </summary>
/// <remarks>
/// The difference is not decoration: it decides where a message belongs in the
/// user interface and whether a reply is expected at all. Up to here everything
/// arrived alike, and the recipient could not tell the shout of a news agency
/// from the line of an acquaintance.
/// </remarks>
public enum MessageType
{

    /// <summary>
    /// A single message outside of a conversation - and the default when the
    /// attribute is missing or unknown.
    /// </summary>
    Normal,

    /// <summary>Part of a one-on-one conversation.</summary>
    Chat,

    /// <summary>From a multi-user room (XEP-0045).</summary>
    GroupChat,

    /// <summary>
    /// A shout: a report, a notification, a stock price - "no reply is
    /// expected".
    /// </summary>
    Headline,

    /// <summary>
    /// The answer to a message the peer could not process. It carries no
    /// payload but the reason.
    /// </summary>
    Error

}
