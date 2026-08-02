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
/// The four values the <c>type</c> attribute of an IQ stanza may take
/// (RFC 6120, section 8.2.3, rule 2).
/// </summary>
/// <remarks>
/// Here and not with the server or the client, because the rule concerns both:
/// it binds "the recipient <b>or an intermediate router</b>", and this project
/// has one of each. Two enumerations could drift apart, and the effect would be
/// silent - a value one side knows and the other does not would get through or
/// not, depending on the route.
/// </remarks>
public static class IqTypes
{

    /// <summary>
    /// Is that one of the foreseen values? <c>null</c> is not: the attribute is
    /// mandatory under rule 2.
    /// </summary>
    public static Boolean IsKnown(String? type)

        => type is "get" or "set" or "result" or "error";

}
