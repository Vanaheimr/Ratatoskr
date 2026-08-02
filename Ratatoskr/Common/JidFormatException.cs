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
/// The string is not a JID per RFC 7622.
/// </summary>
/// <remarks>
/// On the wire this corresponds to a <c>&lt;jid-malformed/&gt;</c> (RFC 6120,
/// section 8.3.3.8).
/// </remarks>
public class JidFormatException : Exception
{

    /// <summary>The string that was objected to.</summary>
    public String Jid { get; }

    public JidFormatException(String jid, String message)

        : base(message)

    {
        Jid = jid;
    }

}
