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
/// XEP-0060: Builds PubSub IQ stanzas.
/// </summary>
public static class PubSubBuilder
{
    /// <summary>
    /// Subscribe to a node
    /// </summary>
    public static string Subscribe(JID pubsubJid, string nodeId, JID myJid, string id = "pubsub-sub")
    {
        return $"<iq type='set' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='http://jabber.org/protocol/pubsub'>" +
               $"<subscribe node='{XmlEscaping.Escape(nodeId)}' jid='{XmlEscaping.Escape(myJid.ToString())}'/>" +
               $"</pubsub></iq>";
    }

    /// <summary>
    /// Unsubscribe from a node
    /// </summary>
    /// <param name="subId">
    /// The identifier of the subscription from the grant of the service, or
    /// null. Prescribed as soon as one JID holds several subscriptions on the
    /// same node (XEP-0060, section 6.2.3.1).
    /// </param>
    public static string Unsubscribe(JID pubsubJid, string nodeId, JID myJid, string id = "pubsub-unsub", string? subId = null)
    {
        return $"<iq type='set' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='http://jabber.org/protocol/pubsub'>" +
               $"<unsubscribe node='{XmlEscaping.Escape(nodeId)}' jid='{XmlEscaping.Escape(myJid.ToString())}'" +
               (subId is not null ? $" subid='{XmlEscaping.Escape(subId)}'" : "") +
               "/></pubsub></iq>";
    }

    /// <summary>
    /// XEP-0060, section 5.6: Query one's own subscriptions.
    /// </summary>
    /// <param name="nodeId">
    /// Which node it is narrowed to, or null for all.
    /// </param>
    public static string GetSubscriptions(JID pubsubJid, string id = "pubsub-subs", string? nodeId = null)
    {
        return $"<iq type='get' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='http://jabber.org/protocol/pubsub'>" +
               "<subscriptions" +
               (nodeId is not null ? $" node='{XmlEscaping.Escape(nodeId)}'" : "") +
               "/></pubsub></iq>";
    }

    /// <summary>
    /// XEP-0060, section 5.7: Query one's own roles.
    /// </summary>
    public static string GetAffiliations(JID pubsubJid, string id = "pubsub-affs")
    {
        return $"<iq type='get' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='http://jabber.org/protocol/pubsub'><affiliations/></pubsub></iq>";
    }

    /// <summary>
    /// XEP-0060, section 8.9.1: Query the roles at a node of one's own.
    /// </summary>
    public static string GetNodeAffiliations(JID pubsubJid, string nodeId, string id = "pubsub-nodeaffs")
    {
        return $"<iq type='get' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='{OwnerNamespace}'>" +
               $"<affiliations node='{XmlEscaping.Escape(nodeId)}'/>" +
               "</pubsub></iq>";
    }

    /// <summary>
    /// XEP-0060, section 8.9.2: Set a role.
    /// </summary>
    public static string SetAffiliation(JID pubsubJid, string nodeId, string id, JID jid, string affiliation)
    {
        return $"<iq type='set' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='{OwnerNamespace}'>" +
               $"<affiliations node='{XmlEscaping.Escape(nodeId)}'>" +
               $"<affiliation jid='{XmlEscaping.Escape(jid.ToString())}' affiliation='{affiliation}'/>" +
               "</affiliations></pubsub></iq>";
    }

    /// <summary>
    /// XEP-0060, section 8.8.1: Query the subscribers of a node of one's own.
    /// </summary>
    /// <remarks>
    /// Looks like the collection query from section 5.6 and asks the opposite:
    /// not "where am I hanging everywhere", but "who hangs on my node". The two
    /// are to be told apart by the namespace alone.
    /// </remarks>
    public static string GetNodeSubscriptions(JID pubsubJid, string nodeId, string id = "pubsub-nodesubs")
    {
        return $"<iq type='get' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='{OwnerNamespace}'>" +
               $"<subscriptions node='{XmlEscaping.Escape(nodeId)}'/>" +
               "</pubsub></iq>";
    }

    /// <summary>
    /// XEP-0060, section 8.8.2: End a subscription of one's own node.
    /// </summary>
    /// <param name="subId">
    /// A particular subscription, or null for all of this JID at this node.
    /// </param>
    /// <remarks>
    /// <b>Only ending and not signing up</b>, although the same section permits
    /// that too. A client that can sign another one up unasked needs no name in
    /// this file for it: whoever wants that says what they are doing. And the
    /// test server of this project refuses it anyway.
    /// </remarks>
    public static string RemoveSubscriber(JID pubsubJid, string nodeId, string id, JID jid, string? subId = null)
    {
        return $"<iq type='set' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='{OwnerNamespace}'>" +
               $"<subscriptions node='{XmlEscaping.Escape(nodeId)}'>" +
               $"<subscription jid='{XmlEscaping.Escape(jid.ToString())}' subscription='none'" +
               (subId is not null ? $" subid='{XmlEscaping.Escape(subId)}'" : "") +
               "/></subscriptions></pubsub></iq>";
    }

    /// <summary>
    /// XEP-0060, section 6.3.1: Query the settings of a subscription.
    /// </summary>
    public static string GetOptions(JID pubsubJid, string nodeId, JID myJid, string id = "pubsub-opts", string? subId = null)
    {
        return $"<iq type='get' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='http://jabber.org/protocol/pubsub'>" +
               $"<options node='{XmlEscaping.Escape(nodeId)}' jid='{XmlEscaping.Escape(myJid.ToString())}'" +
               (subId is not null ? $" subid='{XmlEscaping.Escape(subId)}'" : "") +
               "/></pubsub></iq>";
    }

    /// <summary>
    /// XEP-0060, section 6.3.5: Set the settings of a subscription.
    /// </summary>
    /// <param name="form">
    /// The submitted data form as finished XML - it is passed through like a
    /// payload and not escaped.
    /// </param>
    public static string SetOptions(JID pubsubJid, string nodeId, JID myJid, string id, string? subId, string form)
    {
        return $"<iq type='set' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='http://jabber.org/protocol/pubsub'>" +
               $"<options node='{XmlEscaping.Escape(nodeId)}' jid='{XmlEscaping.Escape(myJid.ToString())}'" +
               (subId is not null ? $" subid='{XmlEscaping.Escape(subId)}'" : "") +
               $">{form}</options></pubsub></iq>";
    }

    /// <summary>
    /// Publish an item to a node
    /// </summary>
    /// <remarks>
    /// <paramref name="payload"/> is deliberately NOT escaped - it is raw XML.
    /// Callers have to make sure that it is well-formed.
    /// </remarks>
    public static string Publish(JID pubsubJid, string nodeId, string itemId, string payload, string id = "pubsub-pub")
    {
        return $"<iq type='set' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='http://jabber.org/protocol/pubsub'>" +
               $"<publish node='{XmlEscaping.Escape(nodeId)}'>" +
               $"<item id='{XmlEscaping.Escape(itemId)}'>{payload}</item>" +
               $"</publish></pubsub></iq>";
    }

    /// <summary>
    /// XEP-0060, section 7.2: Retract a single entry.
    /// </summary>
    /// <remarks>
    /// In the ordinary namespace and not in that of the owner: whoever may
    /// publish may also retract. And with an identifier - "retract just
    /// anything" does not exist, that is what the purging is for.
    /// </remarks>
    public static string Retract(JID pubsubJid, string nodeId, string itemId, string id = "pubsub-retract")
    {
        return $"<iq type='set' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='http://jabber.org/protocol/pubsub'>" +
               $"<retract node='{XmlEscaping.Escape(nodeId)}'>" +
               $"<item id='{XmlEscaping.Escape(itemId)}'/>" +
               "</retract></pubsub></iq>";
    }

    /// <summary>
    /// Get items from a node
    /// </summary>
    public static string GetItems(JID pubsubJid, string nodeId, int? maxItems = null, string id = "pubsub-get")
    {
        var maxAttr = maxItems.HasValue ? $" max_items='{maxItems}'" : "";
        return $"<iq type='get' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='http://jabber.org/protocol/pubsub'>" +
               $"<items node='{XmlEscaping.Escape(nodeId)}'{maxAttr}/>" +
               $"</pubsub></iq>";
    }

    /// <summary>The namespace of the owner requests (XEP-0060, section 8).</summary>
    public const string OwnerNamespace = "http://jabber.org/protocol/pubsub#owner";

    /// <summary>
    /// Create a new node
    /// </summary>
    /// <param name="configuration">
    /// The submitted node form as finished XML, or null. Creating and
    /// configuring in one go (XEP-0060, section 8.1.3): two steps would have a
    /// gap in which the node stands open.
    /// </param>
    public static string CreateNode(JID pubsubJid, string nodeId, string id = "pubsub-create", string? configuration = null)
    {
        return $"<iq type='set' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='http://jabber.org/protocol/pubsub'>" +
               $"<create node='{XmlEscaping.Escape(nodeId)}'/>" +
               (configuration is not null ? $"<configure>{configuration}</configure>" : "") +
               $"</pubsub></iq>";
    }

    /// <summary>
    /// XEP-0060, section 8.2.1: Query the settings of a node.
    /// </summary>
    public static string GetNodeConfig(JID pubsubJid, string nodeId, string id = "pubsub-cfg")
    {
        return $"<iq type='get' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='{OwnerNamespace}'>" +
               $"<configure node='{XmlEscaping.Escape(nodeId)}'/>" +
               "</pubsub></iq>";
    }

    /// <summary>
    /// XEP-0060, section 8.2.4: Set the settings of a node.
    /// </summary>
    public static string SetNodeConfig(JID pubsubJid, string nodeId, string id, string form)
    {
        return $"<iq type='set' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='{OwnerNamespace}'>" +
               $"<configure node='{XmlEscaping.Escape(nodeId)}'>{form}</configure>" +
               "</pubsub></iq>";
    }

    /// <summary>
    /// XEP-0060, section 8.4: Delete a node.
    /// </summary>
    public static string DeleteNode(JID pubsubJid, string nodeId, string id = "pubsub-delete")
    {
        return $"<iq type='set' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='{OwnerNamespace}'>" +
               $"<delete node='{XmlEscaping.Escape(nodeId)}'/>" +
               $"</pubsub></iq>";
    }

    /// <summary>
    /// XEP-0060, section 8.5: Empty a node.
    /// </summary>
    /// <remarks>
    /// Looks confusingly like the deleting and means something else: the node
    /// stays, its subscribers stay, only the content goes.
    /// </remarks>
    public static string PurgeNode(JID pubsubJid, string nodeId, string id = "pubsub-purge")
    {
        return $"<iq type='set' to='{XmlEscaping.Escape(pubsubJid.ToString())}' id='{id}'>" +
               $"<pubsub xmlns='{OwnerNamespace}'>" +
               $"<purge node='{XmlEscaping.Escape(nodeId)}'/>" +
               $"</pubsub></iq>";
    }
}
