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

using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// One way to raise the events of this library, so that a handler which throws
/// is a handler which throws - and not a connection which dies.
/// </summary>
/// <remarks>
/// The events used to be <c>Action</c>. That has two consequences, and the
/// second one is the expensive one.
///
/// A synchronous handler runs inside the read loop, so an exception in it
/// travels straight into the loop and takes the connection down - a display
/// routine that trips over a null can end a session.
///
/// And whoever wants to do something asynchronous - which is most of what one
/// does on receiving a message: write to a database, answer, forward - has only
/// <c>async void</c> left. An exception in an <c>async void</c> lambda is not
/// caught by the caller, because by then there is no caller any more: it lands
/// on the thread pool, and the process ends. That is not a hypothetical; it is
/// what an application built around this client hits on its first failed
/// database write.
///
/// Task-returning delegates remove the second problem, this class the first.
/// </remarks>
internal static class EventInvocation
{

    #region InvokeAllAsync(this Handlers, Invocation, Logger, EventName = ...)

    /// <summary>
    /// Calls every registered handler and waits for it.
    /// </summary>
    /// <param name="Handlers">The event; null when nobody has subscribed.</param>
    /// <param name="Invocation">Calls one handler with the arguments of this event.</param>
    /// <param name="Logger">Where a failing handler is reported; without one it stays silent.</param>
    /// <param name="EventName">
    /// The name of the event - supplied by the compiler from the call site, so
    /// that it cannot fall out of step with the event it names.
    /// </param>
    /// <remarks>
    /// <b>One after another, in the order subscribed</b>, and not
    /// <see cref="Task.WhenAll(IEnumerable{Task})"/>. That is what the
    /// <c>Action</c> events did, several handlers rely on it, and doing them at
    /// once would buy nothing: the raiser waits for all of them either way, so
    /// the only difference is whether two handlers may see each other's
    /// half-finished state.
    ///
    /// <b>Every handler in its own try/catch</b>, and this is the part that a
    /// single <c>WhenAll</c> in a try/catch gets wrong twice over. A handler
    /// that throws before its first <c>await</c> throws while the list is still
    /// being built, and the handlers behind it are then never called at all.
    /// And of those that do fail, <c>WhenAll</c> re-throws exactly one - the
    /// others are in the AggregateException nobody looks at.
    ///
    /// <b>Nothing comes back out.</b> The alternative would be to let it
    /// through to the read loop, and then a single subscriber decides whether
    /// the connection lives. Whoever wants their exception to have consequences
    /// has to say so in their own handler.
    /// </remarks>
    internal static async Task InvokeAllAsync<TDelegate>(this TDelegate?         Handlers,
                                                         Func<TDelegate, Task>   Invocation,
                                                         ILogger?                Logger,

                                                         [CallerArgumentExpression(nameof(Handlers))]
                                                         String?                 EventName   = null)

        where TDelegate : Delegate

    {

        if (Handlers is null)
            return;

        foreach (var handler in Handlers.GetInvocationList().OfType<TDelegate>())
        {
            try
            {
                await Invocation(handler).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down is not a fault. A handler that passes the
                // cancellation token on gets this the moment the connection
                // closes, and an error in the log for every subscriber at
                // every disconnect would teach the reader to skip the log.
            }
            catch (Exception e)
            {
                Logger?.LogError(e,
                                 "A handler of {EventName} threw - the event carries on to the remaining handlers",
                                 EventName ?? "an event");
            }
        }

    }

    #endregion

}
