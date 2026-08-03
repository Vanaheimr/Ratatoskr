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
/// Who gets at the entries of a node (XEP-0060, section 4.5).
/// </summary>
/// <remarks>
/// <b>All five.</b> What this server cannot enforce it does not offer, instead
/// of accepting it and passing over it - with an access model that would be
/// the most expensive place for a promise without cover: whoever configures
/// <c>whitelist</c> and gets <c>open</c> believes their entries protected and
/// has published them. That the list is complete now therefore also means:
/// every model here does something.
///
/// <see cref="Whitelist"/> came along with the roles (K13): it is the model
/// that gives <see cref="PubSubAffiliation.Member"/> a meaning at all.
/// <see cref="Roster"/> followed in D92 - and first needed a server that knows
/// roster groups at all (D91). <see cref="Authorize"/> in D93, with the state
/// <see cref="PubSubSubscriptionState.Pending"/>, which until then existed only
/// on paper.
/// </remarks>
public enum PubSubAccessModel
{

    /// <summary>
    /// Whoever asks, gets.
    /// </summary>
    /// <remarks>
    /// The default, and for OMEMO the only usable one: whoever wants to write
    /// to a human being in encrypted form has to be able to read their bundle -
    /// in case of doubt somebody who stands in no roster (XEP-0384,
    /// section 5.2).
    /// </remarks>
    Open,

    /// <summary>
    /// Only whoever may see the presence of the owner.
    /// </summary>
    Presence,

    /// <summary>
    /// Only whoever stands on the list: the owner, a
    /// <see cref="PubSubAffiliation.Publisher"/> and a
    /// <see cref="PubSubAffiliation.Member"/>.
    /// </summary>
    /// <remarks>
    /// <b>The strictest of the three models and the only one where the roster
    /// decides nothing.</b> Presence permission comes into being beside the
    /// point - somebody takes a contact on, and already they see more. A list
    /// does not come into being beside the point: on it stands only whom the
    /// owner has expressly put on it.
    /// </remarks>
    Whitelist,

    /// <summary>
    /// Only whoever stands in the roster of the owner - and, when groups are
    /// named, in one of them.
    /// </summary>
    /// <remarks>
    /// <b>The roster is the list of the owner</b>, and that is why one entry
    /// suffices: whoever stands in it stands there because the owner has
    /// entered them. A presence state is not demanded - that would be
    /// <see cref="Presence"/>, and that is another question: there it is about
    /// who <i>may see me</i>, here about whom <i>I carry</i>. The two can
    /// diverge, and then they are two different answers and not one imprecise
    /// one.
    ///
    /// <b>Without named groups the whole roster comes in.</b> To read an empty
    /// list as "nobody" would be the other possibility and the worse one: it
    /// would make the model in its basic setting equal in effect to an empty
    /// <see cref="Whitelist"/> - two names for the same thing, and one of them
    /// would lead astray.
    /// </remarks>
    Roster,

    /// <summary>
    /// Only whom the owner has let in one by one.
    /// </summary>
    /// <remarks>
    /// <b>The only model where subscribing and getting in are two things.</b>
    /// With all the others the same rule decides both: whoever may not get in
    /// may not subscribe either. Here everybody may <i>ask</i> - the asking is
    /// the procedure -, and what they get is a subscription in the state
    /// <see cref="PubSubSubscriptionState.Pending"/>: accepted, but not yet
    /// granted.
    ///
    /// The difference to <see cref="Whitelist"/> is the moment: there the owner
    /// has to enter somebody <i>before</i> they ask, and never learns that
    /// somebody knocked in vain. Here the question arrives at them.
    /// </remarks>
    Authorize

}
