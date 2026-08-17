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

using System.Collections.Concurrent;

using NUnit.Framework;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// What a subscriber's exception may and may not cost.
    /// </summary>
    /// <remarks>
    /// A handler used to run inside the receive loop, so its exception was the
    /// loop's problem. How bad that was depended entirely on where it was
    /// raised, and the difference is worth stating because it decides what
    /// these tests may claim.
    ///
    /// On the stanza path it was contained: <c>ProcessStanza</c> has had a
    /// blanket try/catch around the whole dispatch for a long time, so a
    /// throwing subscriber came out as "Stanza processing failed" and the
    /// session lived. What it did cost was the rest of that stanza's handling -
    /// including <b>every handler behind the one that threw</b>.
    ///
    /// On the raw-XML path it was not contained. That event is raised in the
    /// receive loop itself, and an exception there ends the loop: the client
    /// reports a receive error and reconnects. One debug display tripping over
    /// a null was enough.
    ///
    /// And underneath both sat the worse half. A handler that wanted to do
    /// anything asynchronous had only <c>async void</c>, and an exception in an
    /// <c>async void</c> lambda has no caller left to catch it: it goes to the
    /// thread pool and ends the process.
    ///
    /// Each test below says which of these it is measuring, because three of
    /// the first four written here passed against the old behaviour as well.
    /// </remarks>
    [TestFixture]
    public class EventHandlerFailureTests : AXMPPTests
    {

        #region AThrowingHandler_DoesNotStopTheOthers()

        /// <summary>
        /// Three subscribers, the first of which throws. The other two still
        /// hear about it.
        /// </summary>
        /// <remarks>
        /// The order matters and is the reason this test has three handlers
        /// rather than two. A single <c>Task.WhenAll</c> in a single try/catch
        /// looks equivalent and is not: a handler that throws before its first
        /// <c>await</c> throws while the task list is still being built, so
        /// everything behind it is never called at all. That failure only shows
        /// with a synchronous thrower <b>in front of</b> somebody else.
        /// </remarks>
        [Test]
        public async Task AThrowingHandler_DoesNotStopTheOthers()
        {

            var alice   = await ConnectClientAsync("alice");
            var bob     = await ConnectClientAsync("bob");

            var second  = new ConcurrentQueue<String>();
            var third   = new ConcurrentQueue<String>();

            bob.OnMessage += (timestamp, sender, message, ct)
                => throw new InvalidOperationException("the subscriber is having a bad day");

            bob.OnMessage += (timestamp, sender, message, ct) => {
                second.Enqueue(message.Body ?? "");
                return Task.CompletedTask;
            };

            bob.OnMessage += (timestamp, sender, message, ct) => {
                third.Enqueue(message.Body ?? "");
                return Task.CompletedTask;
            };

            await alice.SendMessageAsync(bob.BareJid, "Hello Bob!");

            await WaitFor(() => !second.IsEmpty && !third.IsEmpty,
                          "the two handlers behind the one that threw");

            Assert.Multiple(() =>
            {
                Assert.That(second.Count, Is.EqualTo(1), "The handler right behind the thrower.");
                Assert.That(third.Count,  Is.EqualTo(1), "And the one behind that.");
            });

        }

        #endregion

        #region AThrowingRawXmlHandler_DoesNotEndTheSession()

        /// <summary>
        /// A debug display that throws does not cost the session.
        /// </summary>
        /// <remarks>
        /// <b>This is the containment that was actually missing.</b> Unlike the
        /// stanza path, <c>OnRawXml</c> is raised in the receive loop itself,
        /// with no try/catch of its own between the handler and the loop: the
        /// exception ended the loop, the client reported a receive error, and
        /// what the user saw was not a stack trace but a reconnect they had not
        /// asked for.
        ///
        /// Asserted through a second message rather than through
        /// <see cref="XMPPClient.IsConnected"/> alone, because the reconnect
        /// restores that property within a second or two - a client that died
        /// and came back looks identical to one that never died, unless
        /// something was supposed to arrive in between.
        /// </remarks>
        [Test]
        public async Task AThrowingRawXmlHandler_DoesNotEndTheSession()
        {

            var alice   = await ConnectClientAsync("alice");
            var bob     = await ConnectClientAsync("bob");

            var arrived = new ConcurrentQueue<String>();

            bob.OnMessage += (timestamp, sender, message, ct) => {
                arrived.Enqueue(message.Body ?? "");
                return Task.CompletedTask;
            };

            bob.Connection.OnRawXml += (timestamp, sender, xml, ct)
                => throw new InvalidOperationException("the debug display is having a bad day");

            await alice.SendMessageAsync(bob.BareJid, "the first one");
            await alice.SendMessageAsync(bob.BareJid, "and the one after it");

            await WaitFor(() => arrived.Contains("and the one after it"),
                          "the message after the one that made the raw-XML handler throw");

            Assert.That(bob.IsConnected, Is.True,
                        "A subscriber's exception is not a reason to end somebody's session.");

        }

        #endregion

        #region AFaultingAsyncRawXmlHandler_IsCaughtToo()

        /// <summary>
        /// The same, for a handler that fails after its first <c>await</c> -
        /// the case that used to be <c>async void</c>.
        /// </summary>
        /// <remarks>
        /// Not a repetition of the test above: a synchronous throw and a
        /// faulted Task travel by different routes. Whether the second is
        /// caught cannot be observed from inside the process at all - an
        /// unobserved task exception surfaces whenever the finaliser gets round
        /// to it, which is to say in some other test, or in production - so
        /// what is asserted here is the consequence rather than the catch.
        /// </remarks>
        [Test]
        public async Task AFaultingAsyncRawXmlHandler_IsCaughtToo()
        {

            var alice   = await ConnectClientAsync("alice");
            var bob     = await ConnectClientAsync("bob");

            var arrived = new ConcurrentQueue<String>();

            bob.OnMessage += (timestamp, sender, message, ct) => {
                arrived.Enqueue(message.Body ?? "");
                return Task.CompletedTask;
            };

            bob.Connection.OnRawXml += async (timestamp, sender, xml, ct) =>
            {

                // The yield is the whole point: after it the handler is a
                // continuation, and its exception has nobody left above it.
                await Task.Yield();

                throw new InvalidOperationException("the debug display failed asynchronously");

            };

            await alice.SendMessageAsync(bob.BareJid, "the first one");
            await alice.SendMessageAsync(bob.BareJid, "and the one after it");

            await WaitFor(() => arrived.Contains("and the one after it"),
                          "the message after the asynchronously failing raw-XML handler");

            Assert.That(bob.IsConnected, Is.True);

        }

        #endregion

        #region ThePendingSubscription_IsNotedEvenWhenTheHandlerThrows()

        /// <summary>
        /// The bookkeeping happens before the announcement, so a subscriber's
        /// exception cannot cost a contact request.
        /// </summary>
        /// <remarks>
        /// Not a hypothetical ordering worry: whoever answers a contact request
        /// from the event handler and fails while doing so would otherwise be
        /// left with a request the client has forgotten and the server has
        /// not - and no way to reach it, because
        /// <see cref="XMPPClient.AcceptSubscriptionAsync"/> without an argument
        /// takes the oldest one on the list.
        ///
        /// <b>This one held before the conversion as well</b> - the list was
        /// always filled before the event was raised. It is here to keep that
        /// ordering from being tidied away later, not as evidence for the
        /// change.
        /// </remarks>
        [Test]
        public async Task ThePendingSubscription_IsNotedEvenWhenTheHandlerThrows()
        {

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            bob.OnSubscriptionRequest += (timestamp, sender, from, status, ct)
                => throw new InvalidOperationException("the subscriber is having a bad day");

            await alice.AddContactAsync(bob.BareJid);

            await WaitFor(() => bob.PendingSubscriptions.Count > 0,
                          "the noted contact request at Bob");

            Assert.That(bob.PendingSubscriptions[0],
                        Is.EqualTo(alice.BareJid).IgnoreCase);

        }

        #endregion

    }

}
