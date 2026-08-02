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
    /// An account store that outlasts nothing - the default case for tests and
    /// for a server that only runs as long as somebody is watching.
    /// </summary>
    /// <remarks>
    /// Holds the same objects the server uses, instead of copies. That is right
    /// here: there is no second truth that could drift apart, and a store that
    /// disappears when the process ends needs no demarcation against itself.
    /// </remarks>
    public sealed class InMemoryAccountStore : IXMPPAccountStore
    {

        #region Data

        private readonly Dictionary<String, XMPPAccount> _accounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _lock = new();

        #endregion


        public IEnumerable<XMPPAccount> Load()
        {
            lock (_lock)
                return _accounts.Values.ToList();
        }

        public void Save(XMPPAccount account)
        {
            lock (_lock)
                _accounts[account.BareJid] = account;
        }

        public void Delete(String bareJid)
        {
            lock (_lock)
                _accounts.Remove(bareJid);
        }

    }

}
