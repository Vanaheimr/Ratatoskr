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
/// The three parts of a JID per RFC 7622, each prepared on its own.
/// </summary>
/// <remarks>
/// Only the domainpart is mandatory: <c>example.com</c> is a valid JID,
/// <c>juliet@</c> and <c>/foobar</c> are not.
///
/// The parts are treated differently, and that is precisely why they stand here
/// separately instead of as one string: local and domain part are lowercased
/// and are therefore independent of spelling, the resourcepart is not.
/// <c>alice@example.com/Phone</c> and <c>alice@example.com/phone</c> are two
/// different devices.
/// </remarks>
/// <param name="Localpart">The part before the <c>@</c>, or null.</param>
/// <param name="Domainpart">The part behind it - the only mandatory piece.</param>
/// <param name="Resourcepart">The part behind the first <c>/</c>, or null.</param>
public sealed record JidParts(String?  Localpart,
                              String   Domainpart,
                              String?  Resourcepart)
{

    /// <summary>The bare JID: <c>localpart@domainpart</c>, or just the domain.</summary>
    public String Bare

        => Localpart is null
               ? Domainpart
               : $"{Localpart}@{Domainpart}";

    /// <summary>The complete JID in its prepared form.</summary>
    public override String ToString()

        => Resourcepart is null
               ? Bare
               : $"{Bare}/{Resourcepart}";

}
