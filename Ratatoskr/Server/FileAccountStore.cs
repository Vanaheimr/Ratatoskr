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

using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    /// <summary>
    /// An account store in a JSON file.
    /// </summary>
    /// <remarks>
    /// One file for all accounts, rewritten completely on every change. That is
    /// O(n) per save and entirely sufficient for a few thousand accounts;
    /// whoever needs more needs a database anyway, and with it another
    /// implementation of this interface.
    ///
    /// Writing goes through a file beside it, which is afterwards moved into
    /// its place. If the operation breaks off, the old version still stands
    /// there complete - a target written directly would be truncated after a
    /// power cut in the middle of writing, and thereby unreadable.
    ///
    /// What does <b>not</b> happen here: the file is not encrypted and its
    /// access rights are not set. The keys stored are not passwords, but they
    /// allow an attacker to check logins. The file belongs in a place only the
    /// server process gets to.
    /// </remarks>
    public sealed class FileAccountStore : IXMPPAccountStore
    {

        #region Data

        private readonly String _path;
        private readonly Lock _lock = new();

        private static readonly JsonSerializerOptions _options = new() {
            WriteIndented         = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        #endregion

        #region Properties

        /// <summary>The file the accounts lie in.</summary>
        public String Path => _path;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Creates a store at the given file. The file does not have to exist
        /// yet.
        /// </summary>
        public FileAccountStore(String path)
        {
            _path = System.IO.Path.GetFullPath(path);
        }

        #endregion


        #region Load()

        public IEnumerable<XMPPAccount> Load()
        {

            lock (_lock)
                return ReadWithoutLock().Select(ToAccount).ToList();

        }

        #endregion

        #region Save(account)

        public void Save(XMPPAccount account)
        {

            lock (_lock)
            {

                var accounts = ReadWithoutLock()
                                   .Where(k => !String.Equals(k.BareJid, account.BareJid, StringComparison.OrdinalIgnoreCase))
                                   .ToList();

                accounts.Add(ToRecord(account));

                WriteWithoutLock(accounts);

            }

        }

        #endregion

        #region Delete(bareJid)

        public void Delete(String bareJid)
        {

            lock (_lock)
            {

                var accounts = ReadWithoutLock()
                                   .Where(k => !String.Equals(k.BareJid, bareJid, StringComparison.OrdinalIgnoreCase))
                                   .ToList();

                WriteWithoutLock(accounts);

            }

        }

        #endregion


        #region (private) Reading and writing the file

        private List<StoredAccount> ReadWithoutLock()
        {

            if (!File.Exists(_path))
                return [];

            var json = File.ReadAllText(_path);

            if (json.Length == 0)
                return [];

            return JsonSerializer.Deserialize<StoredAccounts>(json, _options)?.Accounts ?? [];

        }

        private void WriteWithoutLock(List<StoredAccount> accounts)
        {

            var directory = System.IO.Path.GetDirectoryName(_path);

            if (!String.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json    = JsonSerializer.Serialize(new StoredAccounts(1, accounts), _options);
            var beside  = _path + ".new";

            File.WriteAllText(beside, json);
            File.Move(beside, _path, overwrite: true);

        }

        #endregion

        #region (private) Conversion

        private static StoredAccount ToRecord(XMPPAccount account)
        {

            var credentials = account.Credentials;

            return new StoredAccount(

                       account.BareJid,

                       new StoredCredentials(
                           Convert.ToBase64String(credentials.Salt),
                           credentials.IterationCount,
                           credentials.Mechanisms.ToDictionary(
                               m => m.ToString(),
                               m => new StoredKeyPair(
                                        Convert.ToBase64String(credentials.KeysOf(m).StoredKey),
                                        Convert.ToBase64String(credentials.KeysOf(m).ServerKey)))),

                       [.. account.Roster.Select(e => new StoredContact(e.Jid, e.Name, e.Subscription,
                                                                       e.Ask, e.Approved,
                                                                       e.Groups.Count > 0
                                                                           ? [.. e.Groups]
                                                                           : null))],

                       account.PendingSubscriptionRequests.Count > 0
                           ? new Dictionary<String, String>(account.PendingSubscriptionRequests)
                           : null,

                       account.OfflineMessages.Count > 0
                           ? [.. account.OfflineMessages.Select(m => new StoredMessage(m.Stanza, m.StoredAt))]
                           : null

                   );

        }

        private static XMPPAccount ToAccount(StoredAccount stored)
        {

            var credentials = XMPPCredentials.FromStored(
                                  Convert.FromBase64String(stored.Credentials.Salt),
                                  stored.Credentials.IterationCount,
                                  stored.Credentials.Keys.ToDictionary(
                                      k => Enum.Parse<SCRAMMechanism>(k.Key),
                                      k => new SCRAMKeys(Convert.FromBase64String(k.Value.StoredKey),
                                                         Convert.FromBase64String(k.Value.ServerKey))));

            var account = new XMPPAccount(stored.BareJid, credentials);

            foreach (var contact in stored.Roster)
                account.SetRosterEntry(new RosterEntry(contact.Jid,
                                                       contact.Name,
                                                       contact.Subscription,
                                                       contact.Ask,
                                                       contact.Approved,
                                                       contact.Groups));

            // RFC 6121, section 3.1.3: a request that was kept shall be
            // delivered as soon as the contact logs in the next time - and
            // surviving a server restart belongs to that. Without it "kept"
            // would only be another word for "until the next restart".
            foreach (var request in stored.PendingSubscriptions ?? [])
                account.RememberSubscriptionRequest(request.Key, request.Value);

            // And the same for the offline storage. A sender whose message was
            // accepted may rely on it arriving - a restart of the server is no
            // reason to lose it, and the sender never learns anything about it.
            foreach (var message in stored.OfflineMessages ?? [])
                account.StoreOfflineMessage(message.Stanza, message.StoredAt);

            return account;

        }

        #endregion

        #region (private) The file format

        /// <param name="Version">
        /// So that a later format can recognise what it has in front of it.
        /// </param>
        private sealed record StoredAccounts(Int32                Version,
                                             List<StoredAccount>  Accounts);

        /// <param name="PendingSubscriptions">
        /// Subscription requests kept, by sender (RFC 6121, section 3.1.3).
        /// Missing in older files and then null.
        /// </param>
        /// <param name="OfflineMessages">
        /// The offline storage (RFC 6121, section 8.5.2.2.1). Missing in older
        /// files and then null.
        /// </param>
        private sealed record StoredAccount(String                        BareJid,
                                            StoredCredentials             Credentials,
                                            List<StoredContact>           Roster,
                                            Dictionary<String, String>?   PendingSubscriptions,
                                            List<StoredMessage>?          OfflineMessages);

        private sealed record StoredMessage(String          Stanza,
                                            DateTimeOffset  StoredAt);

        private sealed record StoredCredentials(String                             Salt,
                                                Int32                              IterationCount,
                                                Dictionary<String, StoredKeyPair>  Keys);

        private sealed record StoredKeyPair(String StoredKey,
                                            String ServerKey);

        /// <param name="Groups">
        /// The groups, or null in a file from before D91 - then the contact is
        /// in none. Treating a missing field as "unknown" would not exist here:
        /// the roster knows no third state between "in this group" and "not".
        /// </param>
        private sealed record StoredContact(String     Jid,
                                            String?    Name,
                                            String     Subscription,
                                            String?    Ask,
                                            Boolean    Approved,
                                            String[]?  Groups = null);

        #endregion

    }

}
