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

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    /// <summary>
    /// A dropped stream waiting for its returner (XEP-0198, section 5).
    /// </summary>
    /// <remarks>
    /// What is kept is the session itself and not a copy of its values: on it
    /// hang the full JID, the account, the counters, the buffer of the stanzas
    /// not yet acknowledged and the presence state. A copy would have to
    /// maintain every one of those separately, and whatever was forgotten in
    /// the process would only show up to the returner.
    ///
    /// Its connection is dead; nothing is sent over it any more
    /// (<c>SendAsync</c> aborts on a closed connection). It is a pure carrier
    /// of state here, until somebody takes it over or the deadline expires.
    /// </remarks>
    /// <param name="Session">The dropped session together with its state.</param>
    /// <param name="Deadline">When the promise expires.</param>
    internal sealed record ParkedStream(XMPPSession     Session,
                                        DateTimeOffset  Deadline);

}
