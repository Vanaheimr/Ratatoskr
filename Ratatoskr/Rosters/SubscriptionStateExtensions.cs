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
/// The state transitions of the subscription handshake (RFC 6121, section 3)
/// from the point of view of the roster owner.
/// </summary>
/// <remarks>
/// <c>To</c> and <c>From</c> are two separate halves and not stages of a
/// scale: <c>To</c> means "I see the contact", <c>From</c> means "the contact
/// sees me". Every transition may therefore only touch its own half and has to
/// leave the other one standing - whoever understands this as a sequence
/// None → To → Both loses exactly the opposite direction on a revocation.
///
/// The server computes the same transitions with its own code, deliberately.
/// If both sides used the same helper, a shared mistake in thinking would stay
/// invisible.
/// </remarks>
public static class SubscriptionStateExtensions
{

    /// <summary>We may see the contact from now on: None→To, From→Both.</summary>
    public static SubscriptionState GrantTo(this SubscriptionState state)
        => state is SubscriptionState.From or SubscriptionState.Both
               ? SubscriptionState.Both
               : SubscriptionState.To;

    /// <summary>We may no longer see the contact: To→None, Both→From.</summary>
    public static SubscriptionState RevokeTo(this SubscriptionState state)
        => state is SubscriptionState.From or SubscriptionState.Both
               ? SubscriptionState.From
               : SubscriptionState.None;

    /// <summary>The contact may see us from now on: None→From, To→Both.</summary>
    public static SubscriptionState GrantFrom(this SubscriptionState state)
        => state is SubscriptionState.To or SubscriptionState.Both
               ? SubscriptionState.Both
               : SubscriptionState.From;

    /// <summary>The contact may no longer see us: From→None, Both→To.</summary>
    public static SubscriptionState RevokeFrom(this SubscriptionState state)
        => state is SubscriptionState.To or SubscriptionState.Both
               ? SubscriptionState.To
               : SubscriptionState.None;

}
