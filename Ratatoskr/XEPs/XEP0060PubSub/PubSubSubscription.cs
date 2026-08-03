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

#region Usings

using System.Xml.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// What the service has said about a subscription (XEP-0060, section 6.1.2).
/// </summary>
/// <param name="NodeId">The node.</param>
/// <param name="ServiceJid">
/// With whom the subscription was taken out - the address the request went to.
/// </param>
/// <param name="SubId">
/// The identifier of the subscription, or null: a service does not have to
/// hand out any as long as there is only one (section 12.19).
/// </param>
/// <param name="State">The state from the answer.</param>
/// <param name="Options">
/// The settings of this subscription, or null - <b>null means "not asked" and
/// not "default".</b> What holds is said by the service; another device of the
/// same account may have reconfigured the same subscription.
/// </param>
/// <remarks>
/// <b>This is the yield of the correlation.</b> A client can know none of it
/// before the answer is there - the identifier least of all, for it comes from
/// the service. Whoever sends off the request and enters their subscription
/// right away mistakes an intention for a fact.
/// </remarks>
public sealed record PubSubSubscription(String                      NodeId,
                                        String                      ServiceJid,
                                        String?                     SubId,
                                        PubSubSubscriptionState     State,
                                        PubSubSubscriptionOptions?  Options = null)
{

    /// <summary>The namespace of XEP-0060.</summary>
    public const String Namespace = "http://jabber.org/protocol/pubsub";

    /// <summary>
    /// Reads the grant out of an IQ answer.
    /// </summary>
    /// <param name="iq">The answer of the service.</param>
    /// <param name="serviceJid">The address that was asked.</param>
    /// <returns>
    /// false when the answer contains no grant - then it says nothing about the
    /// subscription, and that is something other than a refusal.
    /// </returns>
    public static Boolean TryRead(XElement              iq,
                                  String                serviceJid,
                                  out PubSubSubscription?  subscription)
    {

        subscription = null;

        var grant = iq.Child(Namespace, "pubsub")?.Child(Namespace, "subscription");

        if (grant?.Attr("node") is not String node || node.Length == 0)
            return false;

        // The address that was asked - not the 'from' of the answer.
        //
        // That is no detail: on this address hangs, later, the question from
        // whom notifications about this node are accepted. If it came from the
        // answer, another side could declare itself a source nobody asked for.
        subscription = new PubSubSubscription(node,
                                              serviceJid,
                                              grant.Attr("subid"),
                                              StateOf(grant.Attr("subscription")));

        return true;

    }

    /// <summary>
    /// The state behind its name - everything unknown counts as
    /// <see cref="PubSubSubscriptionState.None"/>.
    /// </summary>
    /// <remarks>
    /// A state this client does not know must not pass as a grant. The caution
    /// costs nothing here: whoever wrongly takes themselves for not subscribed
    /// asks once more - whoever wrongly takes themselves for subscribed waits
    /// for something that never comes.
    /// </remarks>
    public static PubSubSubscriptionState StateOf(String? name)
        => name switch {
               "subscribed"    => PubSubSubscriptionState.Subscribed,
               "pending"       => PubSubSubscriptionState.Pending,
               "unconfigured"  => PubSubSubscriptionState.Unconfigured,
               _               => PubSubSubscriptionState.None
           };

    /// <summary>
    /// The state as it stands in the protocol.
    /// </summary>
    /// <remarks>
    /// The opposite direction to <see cref="StateOf"/>, and in one place for
    /// the same reason: as long as there were only granted subscriptions,
    /// <c>subscribed</c> stood as a fixed string in three places in the server.
    /// With <c>authorize</c> every one of them became an assertion.
    /// </remarks>
    public static String NameOf(PubSubSubscriptionState state)
        => state switch {
               PubSubSubscriptionState.Subscribed    => "subscribed",
               PubSubSubscriptionState.Pending       => "pending",
               PubSubSubscriptionState.Unconfigured  => "unconfigured",
               _                                     => "none"
           };

    /// <summary>
    /// The same name, read strictly: false when it is no state name.
    /// </summary>
    /// <remarks>
    /// <b>The same distinction as with the forms.</b> An answer is read
    /// leniently - what this client does not know counts as "not subscribed",
    /// and that is the safe assumption. An <i>instruction</i> is read strictly:
    /// there <c>none</c> is the ending of a subscription, and a typo must not
    /// bring about the same thing.
    /// </remarks>
    public static Boolean TryReadState(String? name, out PubSubSubscriptionState state)
    {

        state = StateOf(name);

        return name is "none" or "subscribed" or "pending" or "unconfigured";

    }

}
