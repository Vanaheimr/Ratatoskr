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
/// What somebody is at a node (XEP-0060, section 4.1).
/// </summary>
/// <remarks>
/// <b>Four out of six, and every one of them decides something.</b> XEP-0060
/// knows <c>publish-only</c> besides — a publisher who may not read. The
/// difference to <see cref="Publisher"/> would be a third line in two checks
/// and an exotic role for a PEP node; that is why it is refused instead of
/// offered.
///
/// <b>The owner is not an entry but the account.</b> A PEP node belongs to the
/// human being in whose account it stands, and that cannot be transferred:
/// whoever could change the owner could take somebody's own account away from
/// them.
/// </remarks>
public enum PubSubAffiliation
{

    /// <summary>No role - the normal case for strangers.</summary>
    None,

    /// <summary>
    /// The owner: the account the node stands in. They may do everything and
    /// are not settable.
    /// </summary>
    Owner,

    /// <summary>
    /// May publish into the node but not configure it.
    /// </summary>
    Publisher,

    /// <summary>
    /// May read and subscribe, even when the node stands open only to its
    /// list.
    /// </summary>
    /// <remarks>
    /// That takes effect only with the access model <c>whitelist</c>; with
    /// <c>open</c> and <c>presence</c> a member may do no more than anybody
    /// else. A role that decides nothing anywhere would not exist here.
    /// </remarks>
    Member,

    /// <summary>
    /// Shut out: gets neither at the subscribing nor at the fetching,
    /// independently of the access model.
    /// </summary>
    /// <remarks>
    /// And they lose existing subscriptions (section 8.9.4). To hinder them
    /// only at new ones would mean making the exclusion depend on the chance
    /// whether they were there before.
    /// </remarks>
    Outcast

}
