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
    /// Where a server keeps its accounts and their rosters.
    /// </summary>
    /// <remarks>
    /// Deliberately small: loading at the start, saving on every change,
    /// deleting. No searching, no paging, no query language - the server holds
    /// its accounts in memory anyway, and anything further would be invented
    /// before anyone needs it.
    ///
    /// What is kept is never a plaintext password but only
    /// <see cref="XMPPCredentials"/>: salt, iteration count and the derived
    /// keys from RFC 5802.
    /// </remarks>
    public interface IXMPPAccountStore
    {

        /// <summary>
        /// Reads all existing accounts. Called once at the start.
        /// </summary>
        IEnumerable<XMPPAccount> Load();

        /// <summary>
        /// Creates an account or writes its changes on - roster changes run
        /// through here too.
        /// </summary>
        void Save(XMPPAccount account);

        /// <summary>
        /// Removes an account. An unknown JID is not an error.
        /// </summary>
        void Delete(String bareJid);

    }

}
