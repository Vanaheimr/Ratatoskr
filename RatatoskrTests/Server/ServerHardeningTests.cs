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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// The server, for whoever runs it against something other than a test.
    /// </summary>
    /// <remarks>
    /// Everything here is about the same corner: what somebody learns, or can
    /// do, before they have proved who they are.
    /// </remarks>
    [TestFixture]
    public class ServerHardeningTests
    {

        #region Data & helper functions

        private String _directory = "";

        private String Path
            => System.IO.Path.Combine(_directory, "accounts.json");

        [SetUp]
        public void CreateDirectory()
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                "ratatoskr-hardening-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void RemoveDirectory()
        {
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            { }
        }

        #endregion


        #region TheDecoySecret_SurvivesARestart()

        /// <summary>
        /// The key the invented credentials are derived from is kept, so that
        /// the salt of a name without an account stands as firmly as that of a
        /// name with one.
        /// </summary>
        /// <remarks>
        /// It used to be drawn afresh at every start. The decoy answers the
        /// question "does this account exist" with a salt that looks like a
        /// real one - but a real salt does not change when the server is
        /// restarted, and an invented one did. Asking the same name twice
        /// across a restart therefore answered the question after all, which
        /// was the one thing the decoy was there to prevent.
        /// </remarks>
        [Test]
        public async Task TheDecoySecret_SurvivesARestart()
        {

            var first = new FileAccountStore(Path);

            Assert.That(first.LoadDecoySecret(), Is.Null, "Nothing is stored yet.");

            // Starting a server is what settles the key.
            await using (var server = new XMPPServer("localhost", accountStore: first))
            { }

            var settled = first.LoadDecoySecret();

            Assert.That(settled, Is.Not.Null, "The first start has to keep one.");

            // The restart: a new store on the same file, a new server.
            var second = new FileAccountStore(Path);

            await using (var server = new XMPPServer("localhost", accountStore: second))
            { }

            Assert.That(second.LoadDecoySecret(), Is.EqualTo(settled),
                        "A restart must not change the invented salts - the real ones do not " +
                        "change either, and the difference is the answer.");

        }

        #endregion

        #region TheDecoySecret_SurvivesAnAccountChange()

        /// <summary>
        /// And it survives the ordinary writing of the file, which is where a
        /// key kept beside the accounts is easiest to lose.
        /// </summary>
        /// <remarks>
        /// Every save rebuilds the file. One that built it out of the accounts
        /// alone would drop the key silently, and the next start would draw a
        /// new one - the enumeration would be back after the first roster
        /// change, with nothing to show for it.
        /// </remarks>
        [Test]
        public async Task TheDecoySecret_SurvivesAnAccountChange()
        {

            var store = new FileAccountStore(Path);

            await using var server = new XMPPServer("localhost", accountStore: store);

            var before = store.LoadDecoySecret();

            server.AddAccount("alice", "secret");

            Assert.Multiple(() =>
            {
                Assert.That(store.LoadDecoySecret(), Is.Not.Null);
                Assert.That(store.LoadDecoySecret(), Is.EqualTo(before));
            });

        }

        #endregion

        #region TheAccountFile_IsReadableByItsOwnerOnly()

        /// <summary>
        /// What stands in the file is not a password - but the StoredKey is
        /// what the server compares against, so whoever reads it can answer any
        /// SCRAM challenge for that account.
        /// </summary>
        [Test]
        public async Task TheAccountFile_IsReadableByItsOwnerOnly()
        {

            if (OperatingSystem.IsWindows())
                Assert.Ignore("Windows has no file mode; permissions there are ACLs " +
                              "and are inherited from the directory.");

            var store = new FileAccountStore(Path);

            await using var server = new XMPPServer("localhost", accountStore: store);

            server.AddAccount("alice", "secret");

            Assert.That(File.Exists(Path), Is.True);

            var mode = File.GetUnixFileMode(Path);

            Assert.Multiple(() =>
            {
                Assert.That(mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite),
                            Is.EqualTo(UnixFileMode.None),
                            "Nobody besides the owner has anything to look for in there.");

                Assert.That(mode.HasFlag(UnixFileMode.UserRead),  Is.True);
                Assert.That(mode.HasFlag(UnixFileMode.UserWrite), Is.True);
            });

        }

        #endregion

    }

}
