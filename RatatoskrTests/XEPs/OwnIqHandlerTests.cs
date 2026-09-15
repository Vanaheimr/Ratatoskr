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
    /// A protocol of somebody else's over the transport XMPP already has: an IQ
    /// with a payload this library knows nothing about, answered by a handler
    /// the caller registered.
    /// </summary>
    /// <remarks>
    /// Until this existed the library was closed to that. Every IQ ran through a
    /// chain of checks, one per implemented XEP, and whatever none of them
    /// claimed was refused with <c>&lt;service-unavailable/&gt;</c> — right by
    /// RFC 6120, section 8.4, and final. There was also no way to *send* one:
    /// <c>SendIqAsync</c> was private, so every request in the library belonged
    /// to a named XEP and nothing else could be asked.
    ///
    /// <b>Three things have to hold together, and the third is the one that gets
    /// forgotten.</b> The handler has to be reached; the request has to be
    /// sendable; and the namespace has to appear in the disco feature list,
    /// because XEP-0030 is how a peer finds out what may be asked here. A
    /// handler nobody is told about works perfectly and is never called — which
    /// looks like the feature working, since nothing fails.
    /// </remarks>
    [TestFixture]
    public class OwnIqHandlerTests : AXMPPTests
    {

        #region Data

        private const String Namespace  = "urn:example:measurements:1";
        private const String Element    = "measure";

        #endregion

        #region AHandlerAnswersAndItsAnswerComesBack()

        /// <summary>
        /// A registered handler answers, and what it returns arrives at the
        /// asker inside the result.
        /// </summary>
        [Test]
        public async Task AHandlerAnswersAndItsAnswerComesBack()
        {

            // Two accounts that may see each other. Without it the server
            // answers the request itself with <service-unavailable/> and
            // stamps the recipient as its sender - correct of it, and a
            // failure that looks exactly like a handler that was not found.
            MakeContacts("alice", "bob");

            var alice  = await ConnectClientAsync("alice");
            var bob    = await ConnectClientAsync("bob");

            XElement? seen  = null;
            JID?      asker = null;

            bob.RegisterIqHandler(Namespace, Element, (request, from, ct) =>
            {
                seen  = request;
                asker = from;
                return Task.FromResult<XElement?>(
                           new XElement(XName.Get("result", Namespace),
                                        new XAttribute("value", "42")));
            });

            var answer = await alice.SendIqAsync(
                             bob.FullJid,
                             "get",
                             new XElement(XName.Get(Element, Namespace),
                                          new XAttribute("what", "voltage"))
                         );

            Assert.That(answer,                    Is.Not.Null,   "No answer arrived at all.");

            Assert.That(seen,                      Is.Not.Null,
                        $"The handler was not reached. What came back instead:\n{answer}");

            Assert.That(answer!.Attr("type"),      Is.EqualTo("result"),
                        $"The handler ran and the answer is still not a result:\n{answer}");
            Assert.That(seen!.Attr("what"),        Is.EqualTo("voltage"),
                        "The handler was reached with something other than the payload that was sent.");

            Assert.That(asker?.Bare,               Is.EqualTo(alice.BareJid),
                        "The handler was not told who is asking - which for a protocol of one's own " +
                        "is usually the first thing it needs.");

            var payload = answer.Element(XName.Get("result", Namespace));

            Assert.That(payload,                   Is.Not.Null,
                        "What the handler returned did not end up in the result.");
            Assert.That(payload!.Attr("value"),    Is.EqualTo("42"));

        }

        #endregion

        #region TheNamespaceIsAnnouncedAndTakenBackAgain()

        /// <summary>
        /// The namespace lands in the disco feature list, and disappears from it
        /// again when the handler goes.
        /// </summary>
        /// <remarks>
        /// <b>This is the half that decides whether anybody ever asks.</b> A
        /// peer reads the feature list to find out what this entity speaks; a
        /// handler that is not in it is unreachable in practice while looking
        /// entirely healthy from inside.
        ///
        /// The feature names the namespace and not the element, so it may only
        /// be withdrawn once the last handler using it is gone - otherwise
        /// unregistering one of two would silently switch the other off.
        /// </remarks>
        [Test]
        public async Task TheNamespaceIsAnnouncedAndTakenBackAgain()
        {

            var alice = await ConnectClientAsync("alice");

            Assert.That(alice.AnnouncedFeatures, Does.Not.Contain(Namespace),
                        "The namespace is announced before anything was registered.");

            alice.RegisterIqHandler(Namespace, Element,    (r, f, ct) => Task.FromResult<XElement?>(null));
            alice.RegisterIqHandler(Namespace, "calibrate", (r, f, ct) => Task.FromResult<XElement?>(null));

            Assert.That(alice.AnnouncedFeatures, Does.Contain(Namespace),
                        "A registered handler is not announced, so no peer would ever send the request.");

            Assert.That(alice.AnnouncedFeatures.Count(feature => feature == Namespace), Is.EqualTo(1),
                        "The namespace stands twice in the feature list. The caps hash of XEP-0115 is " +
                        "computed over it, so a duplicate changes the hash without changing what is " +
                        "offered.");

            alice.UnregisterIqHandler(Namespace, Element);

            Assert.That(alice.AnnouncedFeatures, Does.Contain(Namespace),
                        "The feature was withdrawn although a second handler still uses the namespace - " +
                        "which would switch that one off without touching it.");

            alice.UnregisterIqHandler(Namespace, "calibrate");

            Assert.That(alice.AnnouncedFeatures, Does.Not.Contain(Namespace),
                        "The last handler is gone and the feature is still announced - this entity now " +
                        "promises something it refuses.");

        }

        #endregion

        #region AnUnregisteredNamespaceIsStillRefused()

        /// <summary>
        /// What nobody registered is still answered with
        /// <c>&lt;service-unavailable/&gt;</c>.
        /// </summary>
        /// <remarks>
        /// The control half. Without it a dispatch that accidentally answered
        /// everything would pass the test above and break RFC 6120, section 8.4
        /// everywhere else — and it would break it in the direction that is hard
        /// to notice, because a wrong result looks like an answer.
        /// </remarks>
        [Test]
        public async Task AnUnregisteredNamespaceIsStillRefused()
        {

            // Two accounts that may see each other. Without it the server
            // answers the request itself with <service-unavailable/> and
            // stamps the recipient as its sender - correct of it, and a
            // failure that looks exactly like a handler that was not found.
            MakeContacts("alice", "bob");

            var alice  = await ConnectClientAsync("alice");
            var bob    = await ConnectClientAsync("bob");

            bob.RegisterIqHandler(Namespace, Element, (r, f, ct) => Task.FromResult<XElement?>(null));

            var answer = await alice.SendIqAsync(
                             bob.FullJid,
                             "get",
                             new XElement(XName.Get("measure", "urn:example:something-else:1"))
                         );

            Assert.That(answer,               Is.Not.Null, "Not even an error came back.");
            Assert.That(answer!.Attr("type"), Is.EqualTo("error"),
                        "A payload nobody registered for was answered with something other than an " +
                        "error.");

            Assert.That(answer.Descendants()
                              .Any(e => e.Name.LocalName == "service-unavailable"), Is.True,
                        "Refused, but not with <service-unavailable/>, which is what RFC 6120 " +
                        $"section 8.4 asks for here:\n{answer}");

        }

        #endregion

        #region AHandlerThatThrowsStillAnswers()

        /// <summary>
        /// A handler that throws does not leave the asker waiting.
        /// </summary>
        /// <remarks>
        /// RFC 6120, section 8.2.3 wants a result or an error for every request.
        /// A handler written by somebody else will throw sooner or later, and
        /// the failure that costs most is not the exception but the silence
        /// after it: the peer waits into its timeout, and against a server that
        /// can take the session with it.
        /// </remarks>
        [Test]
        public async Task AHandlerThatThrowsStillAnswers()
        {

            // Two accounts that may see each other. Without it the server
            // answers the request itself with <service-unavailable/> and
            // stamps the recipient as its sender - correct of it, and a
            // failure that looks exactly like a handler that was not found.
            MakeContacts("alice", "bob");

            var alice  = await ConnectClientAsync("alice");
            var bob    = await ConnectClientAsync("bob");

            bob.RegisterIqHandler(Namespace, Element,
                                  (r, f, ct) => throw new InvalidOperationException("nope"));

            var answer = await alice.SendIqAsync(
                             bob.FullJid,
                             "set",
                             new XElement(XName.Get(Element, Namespace))
                         );

            Assert.That(answer,               Is.Not.Null,
                        "The handler threw and nothing was sent back, so the asker waits into its " +
                        "timeout for a request that was in fact received.");

            Assert.That(answer!.Attr("type"), Is.EqualTo("error"));

            Assert.That(answer.Descendants()
                              .Any(e => e.Name.LocalName == "internal-server-error"), Is.True,
                        $"Answered, but not as the failure it was:\n{answer}");

        }

        #endregion

        #region ASecondHandlerForTheSamePayloadIsRefused()

        /// <summary>
        /// Registering twice for the same payload fails rather than replacing.
        /// </summary>
        /// <remarks>
        /// Two parts of one program each believing they answer is worse than one
        /// of them failing to register: the first is found at runtime, by
        /// whichever request happens to arrive, and the second at the line that
        /// returns false.
        /// </remarks>
        [Test]
        public async Task ASecondHandlerForTheSamePayloadIsRefused()
        {

            var alice = await ConnectClientAsync("alice");

            Assert.That(alice.RegisterIqHandler(Namespace, Element, (r, f, ct) => Task.FromResult<XElement?>(null)),
                        Is.True);

            Assert.That(alice.RegisterIqHandler(Namespace, Element, (r, f, ct) => Task.FromResult<XElement?>(null)),
                        Is.False,
                        "A second handler for the same payload was accepted, so which of the two " +
                        "answers is now decided by the order they were registered in.");

        }

        #endregion

    }

}
