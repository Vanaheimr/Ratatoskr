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
/// XEP-0030: The identity of an entity (category/type/name).
/// </summary>
/// <param name="Language">
/// The <c>xml:lang</c> of the identity. An entity may carry the same name in
/// several languages; in the verification string the language stands between
/// type and name (XEP-0115, section 5.1), and without it such an answer yields
/// a different hash than at its originator.
///
/// Stands deliberately behind <c>Name</c> instead of at the place where it
/// belongs in the hash: that way every existing call stays valid.
/// </param>
public sealed record DiscoIdentity(string   Category,
                                   string   Type,
                                   string?  Name       = null,
                                   string?  Language   = null)
{
    public override string ToString() => Name != null ? $"{Category}/{Type} ({Name})" : $"{Category}/{Type}";
}
