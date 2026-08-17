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
/// XEP-0030: The result of a disco#info query (identities + features).
/// </summary>
public sealed class DiscoInfo
{
    public string From { get; init; } = "";
    public string? Node { get; init; }
    public List<DiscoIdentity> Identities { get; } = [];
    public List<string> Features { get; } = [];

    /// <summary>
    /// XEP-0128: The data forms of the answer, unfiltered and in the order
    /// found.
    /// </summary>
    /// <remarks>
    /// They belong to the answer and are not merely decoration: XEP-0115,
    /// section 5.1 lets them go into the verification string. Whoever throws
    /// them away cannot recompute the hash of an entity that carries any - and
    /// then has either to believe it blindly or to distrust it without cause.
    /// </remarks>
    public List<DiscoForm> Forms { get; } = [];

    /// <summary>
    /// Did the answer carry a data form (XEP-0128)?
    /// </summary>
    public bool HasExtendedInfo => Forms.Count > 0;

    /// <summary>
    /// Does the answer list this feature?
    /// </summary>
    /// <remarks>
    /// Five abbreviations once stood beside this one - <c>SupportsCarbons</c>,
    /// <c>SupportsReceipts</c> and three more -, each a line above this one and
    /// each with a built-in namespace. Nobody called them, and they could not
    /// have done anything this method cannot do either: the namespace stands
    /// where the extension stands anyway, and a second copy of it goes stale on
    /// its own.
    /// </remarks>
    public bool HasFeature(string feature) => Features.Contains(feature);
}
