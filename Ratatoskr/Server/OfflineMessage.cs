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
    /// A message kept for an account that currently has no reachable resource
    /// (RFC 6121, section 8.5.2.2.1).
    /// </summary>
    /// <param name="Stanza">
    /// The complete stanza as it would have been delivered - with the
    /// <c>from</c> set.
    /// </param>
    /// <param name="StoredAt">
    /// When it came in. The moment belongs to the message and not to the
    /// delivery: it is passed along when delivering late, as an XEP-0203
    /// <c>&lt;delay/&gt;</c>, so that the recipient does not take a message
    /// from yesterday for one from just now.
    /// </param>
    public sealed record OfflineMessage(String          Stanza,
                                        DateTimeOffset  StoredAt);

}
