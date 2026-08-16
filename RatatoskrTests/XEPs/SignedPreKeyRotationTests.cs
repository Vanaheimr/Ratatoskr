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

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// When the signed prekey is replaced.
    /// </summary>
    /// <remarks>
    /// The rotation was buildable all along - <c>RotateSignedPreKey</c> with its
    /// superseded key has been there for a while - and it never happened,
    /// because nothing knew how old the current key was. What was missing was
    /// not the mechanism but the question.
    ///
    /// <b>What it buys is narrower than the word suggests.</b> It does not
    /// protect what has already been sent; that hangs on the ratchet, which
    /// moves on by itself. It bounds how far back a stolen signed prekey opens
    /// <i>new</i> sessions - without a rotation, the whole life of the device.
    /// </remarks>
    [TestFixture]
    public class SignedPreKeyRotationTests
    {

        #region Helper functions

        /// <summary>A manager over a store already holding this identity.</summary>
        private static OmemoManager ManagerOver(OmemoIdentityState state)
        {

            var store = new OmemoMemoryStore();

            store.SaveIdentity(state);

            return new OmemoManager(store,
                                    "alice@example.org",
                                    _      => Task.FromResult<OmemoDeviceList?>(null),
                                    (_, _) => Task.FromResult<OmemoBundle?>(null));

        }

        private static OmemoIdentityState Aged(TimeSpan by)
        {

            var state = OmemoIdentity.Create().Export();

            return state with { SignedPreKeyCreatedAt = DateTimeOffset.UtcNow - by };

        }

        #endregion


        #region AFreshDevice_KeepsItsSignedPreKey()

        /// <summary>
        /// The counter-check first: a key made a moment ago is not replaced.
        /// A rotation at every start would publish a new bundle every time and
        /// buy nothing.
        /// </summary>
        [Test]
        public void AFreshDevice_KeepsItsSignedPreKey()
        {

            var state   = OmemoIdentity.Create().Export();
            var manager = ManagerOver(state);

            Assert.Multiple(() =>
            {
                Assert.That(manager.Identity.SignedPreKeyId,          Is.EqualTo(state.SignedPreKeyId));
                Assert.That(manager.Identity.PreviousSignedPreKeyId,  Is.Null);
            });

        }

        #endregion

        #region AnAgedSignedPreKey_IsReplacedWhenTheIdentityIsLoaded()

        /// <summary>
        /// Past the interval it is replaced - and the superseded one stays
        /// reachable, because a message sent before the rotation names it.
        /// </summary>
        [Test]
        public void AnAgedSignedPreKey_IsReplacedWhenTheIdentityIsLoaded()
        {

            var state   = Aged(TimeSpan.FromDays(30));
            var manager = ManagerOver(state);

            Assert.Multiple(() =>
            {

                Assert.That(manager.Identity.SignedPreKeyId,
                            Is.Not.EqualTo(state.SignedPreKeyId),
                            "A key a month old belongs replaced.");

                Assert.That(manager.Identity.PreviousSignedPreKeyId,
                            Is.EqualTo(state.SignedPreKeyId),
                            "The superseded one moves up, it does not vanish.");

                Assert.That(manager.Identity.SignedPreKeyFor(state.SignedPreKeyId),
                            Is.Not.Null,
                            "A message that was under way names the old key and has to stay " +
                            "readable.");

            });

        }

        #endregion

        #region AKeyOfUnknownAge_CountsAsDue()

        /// <summary>
        /// A device stored before the timestamp existed has no answer to the
        /// question - and "I do not know how old this is" does not read as
        /// "young enough".
        /// </summary>
        /// <remarks>
        /// It costs one rotation on the first start after the upgrade, and the
        /// superseded key stays reachable, so nothing under way is lost.
        /// </remarks>
        [Test]
        public void AKeyOfUnknownAge_CountsAsDue()
        {

            var state   = OmemoIdentity.Create().Export() with { SignedPreKeyCreatedAt = null };
            var manager = ManagerOver(state);

            Assert.That(manager.Identity.SignedPreKeyId, Is.Not.EqualTo(state.SignedPreKeyId));

        }

        #endregion

        #region TheRotation_IsWrittenDownAtOnce()

        /// <summary>
        /// Stored right away, and the new age with it. Without that the next
        /// start would find the old key again and rotate a second time - and a
        /// device that rotates at every start has a new bundle every start.
        /// </summary>
        [Test]
        public void TheRotation_IsWrittenDownAtOnce()
        {

            var store = new OmemoMemoryStore();

            store.SaveIdentity(Aged(TimeSpan.FromDays(30)));

            var first = new OmemoManager(store, "alice@example.org",
                                         _      => Task.FromResult<OmemoDeviceList?>(null),
                                         (_, _) => Task.FromResult<OmemoBundle?>(null));

            var rotated = first.Identity.SignedPreKeyId;

            // A second manager over the same store: the rotation is done and
            // stays done.
            var second = new OmemoManager(store, "alice@example.org",
                                          _      => Task.FromResult<OmemoDeviceList?>(null),
                                          (_, _) => Task.FromResult<OmemoBundle?>(null));

            Assert.That(second.Identity.SignedPreKeyId, Is.EqualTo(rotated),
                        "The age has to be stored along with the key, or every start rotates.");

        }

        #endregion

        #region TheOmemoStoreFile_IsReadableByItsOwnerOnly()

        /// <summary>
        /// The file holds the identity key and every chain key of every
        /// session. Whoever reads it reads the conversations, past ones
        /// included.
        /// </summary>
        /// <remarks>
        /// This is not encryption and does not pretend to be. Against another
        /// account on the same machine it is the difference between "may read"
        /// and "may not"; against whoever runs as this user, or takes the disk,
        /// it is nothing.
        /// </remarks>
        [Test]
        public void TheOmemoStoreFile_IsReadableByItsOwnerOnly()
        {

            if (OperatingSystem.IsWindows())
                Assert.Ignore("Windows has no file mode; permissions there are ACLs.");

            var directory = Path.Combine(Path.GetTempPath(),
                                         "ratatoskr-omemo-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(directory);

            try
            {

                var file  = Path.Combine(directory, "omemo.json");
                var store = new OmemoFileStore(file);

                store.SaveIdentity(OmemoIdentity.Create().Export());

                Assert.That(File.Exists(file), Is.True);

                var mode = File.GetUnixFileMode(file);

                Assert.That(mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite),
                            Is.EqualTo(UnixFileMode.None));

            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            }

        }

        #endregion

    }

}
