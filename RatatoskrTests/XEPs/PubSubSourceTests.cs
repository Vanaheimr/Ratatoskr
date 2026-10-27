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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// Who may report a PubSub node, and who may not.
    /// </summary>
    /// <remarks>
    /// <b>The rule had no test, which is how it shipped wrong.</b> D71 widened
    /// it from the configured service to cover PEP as well, by way of the
    /// subscription table - and a PEP notification is never in that table,
    /// because PEP delivery follows presence (XEP-0163, section 4.2) and is not
    /// a subscription anybody applied for. So every avatar a contact published
    /// was written to the log as attempted forgery, which is what a security
    /// log is worth least.
    ///
    /// Nothing here reaches the network or a server: the manager takes a
    /// stanza, and the three answers are a pure function of the sender, the
    /// node and what has been subscribed.
    /// </remarks>
    [TestFixture]
    public class PubSubSourceTests
    {

        #region Data & helper functions

        private static readonly JID  service  = JID.Parse("pubsub.example.com");
        private static readonly JID  bob      = JID.Parse("bob@example.com");
        private static readonly JID  carol    = JID.Parse("carol@example.net");

        private const String Avatar  = "urn:xmpp:avatar:metadata";
        private const String Weather = "weather";

        /// <summary>
        /// A manager wired as the connection wires it: this client announces
        /// <c>+notify</c> for the avatar node and for nothing else, which is
        /// what <c>DiscoManager.LocalFeatures</c> says in earnest.
        /// </summary>
        private static PubSubManager Manager()
            => new (service) {
                   AsksToBeNotifiedOf = node => node == Avatar
               };

        /// <summary>
        /// XEP-0060, section 8.8.4: the report that a subscription has ended.
        /// </summary>
        private static XElement SubscriptionEnded(String node, String? subId = null)
            => XElement.Parse(
                   "<message xmlns='jabber:client' type='headline'>" +
                     $"<event xmlns='{PubSubManager.EventNamespace}'>" +
                       $"<subscription node='{node}' subscription='none'" +
                       (subId is not null ? $" subid='{subId}'" : "") + "/>" +
                     "</event>" +
                   "</message>");

        /// <summary>
        /// A notification as XEP-0060, section 4.3 builds one.
        /// </summary>
        private static XElement Notification(String node, String itemId = "i1")
            => XElement.Parse(
                   "<message xmlns='jabber:client' type='headline'>" +
                     $"<event xmlns='{PubSubManager.EventNamespace}'>" +
                       $"<items node='{node}'>" +
                         $"<item id='{itemId}'><payload/></item>" +
                       "</items>" +
                     "</event>" +
                   "</message>");

        /// <summary>
        /// What the manager hands on for this notification, or null when it
        /// hands on nothing.
        /// </summary>
        private static async Task<(Boolean Accepted, PubSubEvent? Event)> Reported(PubSubManager  Manager,
                                                                                   JID            From,
                                                                                   XElement       Notification)
        {

            PubSubEvent? heard = null;

            Manager.OnEvent += (timestamp, sender, pubSubEvent, ct) => {
                heard = pubSubEvent;
                return Task.CompletedTask;
            };

            var accepted = await Manager.ProcessEventAsync(Notification, From, Manager.PubSubService);

            return (accepted, heard);

        }

        #endregion


        #region APepNotification_IsNotAForgery()

        /// <summary>
        /// XEP-0163: a contact publishing their own avatar is a contact
        /// publishing their own avatar.
        /// </summary>
        /// <remarks>
        /// The case out of a real log, and the whole of the fault: the
        /// expectation the notification was measured against was the configured
        /// service, which is the one address a PEP notification can never come
        /// from. Under PEP every account is its own service.
        /// </remarks>
        [Test]
        public async Task APepNotification_IsNotAForgery()
        {

            var (accepted, reported) = await Reported(Manager(), bob, Notification(Avatar));

            Assert.Multiple(() =>
            {

                Assert.That(accepted,         Is.True,
                            "a contact's own avatar notification was discarded as a forgery");

                Assert.That(reported?.NodeId, Is.EqualTo(Avatar));

                Assert.That(reported?.From,   Is.EqualTo(bob),
                            "the report was passed on without saying whose node it is, which is " +
                            "worse than discarding it");

            });

        }

        #endregion

        #region TwoContactsNotifications_AreToldApartByTheirSender()

        /// <summary>
        /// The reason it is safe to pass a PEP notification on at all.
        /// </summary>
        /// <remarks>
        /// <b>A node name does not say whose node it is.</b> A service keeps
        /// its names to itself, so for one service the name was the whole
        /// address; <c>urn:xmpp:avatar:metadata</c> is the name of a node that
        /// every account has. Bob's picture and Carol's arrive under the same
        /// name and differ in the sender and in nothing else - so the sender
        /// has to be on the event, or a reader has no way to be right.
        /// </remarks>
        [Test]
        public async Task TwoContactsNotifications_AreToldApartByTheirSender()
        {

            var manager = Manager();

            var (_, his)  = await Reported(manager, bob,   Notification(Avatar, "bobs-face"));
            var (_, hers) = await Reported(manager, carol, Notification(Avatar, "carols-face"));

            Assert.Multiple(() =>
            {

                Assert.That(his?.From,   Is.EqualTo(bob));
                Assert.That(hers?.From,  Is.EqualTo(carol));

                Assert.That(his?.NodeId, Is.EqualTo(hers?.NodeId),
                            "the two notifications are supposed to share a node name - without " +
                            "that this test is not about anything");

            });

        }

        #endregion

        #region AForeignComponent_IsStillAForgery()

        /// <summary>
        /// The case the check was written for, and the one where "spoofing" is
        /// the right word.
        /// </summary>
        /// <remarks>
        /// A component nobody asked anything of, claiming to deliver a node
        /// this client is subscribed to somewhere else. It has no account
        /// behind it - no localpart - so it is not an entity reporting its own
        /// business; it is a stranger reporting somebody else's.
        /// </remarks>
        [Test]
        public async Task AForeignComponent_IsStillAForgery()
        {

            var manager = Manager();

            manager.AddSubscription(new PubSubSubscription(Weather,
                                                           service,
                                                           "sub-1",
                                                           PubSubSubscriptionState.Subscribed));

            var (accepted, reported) = await Reported(manager,
                                                      JID.Parse("pubsub.evil.example"),
                                                      Notification(Weather));

            Assert.Multiple(() =>
            {

                Assert.That(accepted, Is.False,
                            "a strange component was allowed to report a node this client is " +
                            "subscribed to elsewhere");

                Assert.That(reported, Is.Null,
                            "and it reached the application anyway");

            });

        }

        #endregion

        #region ASubscriptionIsOnlyEndedByWhoeverHoldsIt()

        /// <summary>
        /// A report that a subscription has ended strikes what the sender
        /// holds, and nothing else.
        /// </summary>
        /// <remarks>
        /// <b>Found while widening the rule above, and older than it.</b> The
        /// branch struck every subscription on the node when the report named
        /// no identifier - which XEP-0060, section 12.19 permits a service to
        /// leave out while it keeps only one, so leaving it out is the ordinary
        /// case and not a trick. Unbound, "every subscription on the node"
        /// means whoever it was taken out with: the node name alone is not an
        /// address, as <c>RemoveSubscriptionsOf</c> says next door, and this
        /// was the caller that was not listening.
        ///
        /// Two services on one node name is all it takes, and that needs
        /// neither PEP nor a stranger - <c>urn:xmpp:omemo:2:bundles</c> is
        /// called that at every account one subscribes to. Letting accounts in
        /// for their own nodes would have widened the reach of it, which is how
        /// it was noticed; the fault was already there.
        ///
        /// The damage is quiet, which is the worst of it: the subscription is
        /// gone from the books, its reports are no longer expected, and it can
        /// no longer be unsubscribed either, because that takes the identifier
        /// that went with it.
        /// </remarks>
        [Test]
        public async Task ASubscriptionIsOnlyEndedByWhoeverHoldsIt()
        {

            var elsewhere = JID.Parse("pubsub.example.net");

            var manager = Manager();

            // The same node name at two services, which is the ordinary state
            // of affairs for a node name that belongs to a protocol rather
            // than to a service.
            manager.AddSubscription(new PubSubSubscription(Weather, service,   "sub-1",
                                                           PubSubSubscriptionState.Subscribed));

            manager.AddSubscription(new PubSubSubscription(Weather, elsewhere, "sub-2",
                                                           PubSubSubscriptionState.Subscribed));

            // One of them ends its own, naming no identifier - the shape that
            // struck everything.
            var (accepted, _) = await Reported(manager, elsewhere, SubscriptionEnded(Weather));

            Assert.Multiple(() =>
            {

                Assert.That(accepted, Is.True,
                            "a service may end the subscription it holds, or this test is " +
                            "measuring the wrong refusal");

                Assert.That(manager.SubscriptionsOf(Weather).Select(sub => sub.SubId),
                            Is.EqualTo(new[] { "sub-1" }),
                            "ending one service's subscription took the other service's with it");

            });

            // And the one that remains can still be ended by whoever holds it.
            await Reported(manager, service, SubscriptionEnded(Weather));

            Assert.That(manager.SubscriptionsOf(Weather), Is.Empty,
                        "the service that holds the subscription could not end it any more, which " +
                        "would leave this client waiting for reports that never come");

        }

        #endregion

        #region AnAccountReportingANodeNobodyAskedFor_IsAForgery()

        /// <summary>
        /// An account is its own service, and that is not a licence to report
        /// whatever it likes.
        /// </summary>
        /// <remarks>
        /// <b>What keeps the third permission from being "anybody may report
        /// anything".</b> Under PEP a client subscribes by announcing
        /// <c>+notify</c> for a node in its own caps (XEP-0163, section 4.2),
        /// so a report about a node it never announced is a report it never
        /// asked for - whether the sender invented the node or merely hopes
        /// something here will act on it.
        ///
        /// The wiring is <c>DiscoManager.LocalFeatures</c>, read through rather
        /// than copied, so a payload handler registered while running widens
        /// this of its own accord and there are not two lists to keep in step.
        /// </remarks>
        [Test]
        public async Task AnAccountReportingANodeNobodyAskedFor_IsAForgery()
        {

            var (accepted, reported) = await Reported(Manager(), bob,
                                                      Notification("urn:example:invented"));

            Assert.Multiple(() =>
            {

                Assert.That(accepted, Is.False,
                            "an account reported a node this client never announced any interest in");

                Assert.That(reported, Is.Null,
                            "and it reached the application anyway");

            });

        }

        #endregion

        #region TheConfiguredServiceAndASubscribedOne_AreAccepted()

        /// <summary>
        /// The two permissions that were already there, so that widening the
        /// rule is not quietly narrowing it.
        /// </summary>
        /// <remarks>
        /// The second one stays <b>bound to the node</b>: a subscription with a
        /// service is not a licence for that service to report every node one
        /// can think up. That is still worth something for a component, which
        /// is the only sender the binding now applies to - an account is let in
        /// for its own nodes whatever anybody is subscribed to.
        /// </remarks>
        [Test]
        public async Task TheConfiguredServiceAndASubscribedOne_AreAccepted()
        {

            var elsewhere = JID.Parse("pubsub.example.net");

            var manager = Manager();

            manager.AddSubscription(new PubSubSubscription(Weather,
                                                           elsewhere,
                                                           "sub-1",
                                                           PubSubSubscriptionState.Subscribed));

            var (configured, _)  = await Reported(Manager(), service,   Notification(Avatar));
            var (subscribed, _)  = await Reported(manager,   elsewhere, Notification(Weather));
            var (otherNode,  _)  = await Reported(manager,   elsewhere, Notification("stocks"));

            Assert.Multiple(() =>
            {

                Assert.That(configured, Is.True,
                            "the configured service may report its own nodes");

                Assert.That(subscribed, Is.True,
                            "a service this client is subscribed with may report that node");

                Assert.That(otherNode,  Is.False,
                            "a subscription on one node let the same service report another");

            });

        }

        #endregion

    }

}
