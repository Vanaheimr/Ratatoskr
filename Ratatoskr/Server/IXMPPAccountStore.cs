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

        /// <summary>
        /// The key the invented credentials of unknown accounts are derived
        /// from - or null when this store keeps none.
        /// </summary>
        /// <remarks>
        /// <b>Not about an account, and here all the same</b>, because it has
        /// to outlive the process for the same reason the accounts do. The
        /// decoy salt of a name without an account is derived from this key so
        /// that an unknown name looks exactly like a known one (RFC 6120,
        /// section 13.11). A key drawn afresh at every start makes the invented
        /// salts change across a restart while the real ones stand - and
        /// whoever asks for the same name before and after reads the
        /// difference, which is the one question the decoy exists to leave
        /// unanswered.
        ///
        /// Defaulted, so that a store which keeps nothing has to say nothing:
        /// an in-memory store has no restart to survive, and null leaves the
        /// server drawing a fresh key, which is right there.
        /// </remarks>
        Byte[]? LoadDecoySecret() => null;

        /// <summary>
        /// Keeps the key from <see cref="LoadDecoySecret"/>. Called once, when
        /// there was none yet.
        /// </summary>
        void SaveDecoySecret(Byte[] Secret) { }

    }

}
