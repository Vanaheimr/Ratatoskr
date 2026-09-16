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

using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0084: the picture, and the id that names it.
    /// </summary>
    /// <remarks>
    /// What is worth testing here is not the SHA-1. It is the handful of places
    /// where two things have to agree and nothing forces them to: the id against
    /// the bytes, the announced length against the bytes, an empty
    /// <c>&lt;metadata/&gt;</c> against a missing one. Every one of those is a
    /// convention, and a convention held differently by two clients is the shape
    /// of D62 to D65.
    /// </remarks>
    [TestFixture]
    public class UserAvatarTests
    {

        #region Data

        private static readonly Byte[] Picture =
            Encoding.UTF8.GetBytes("not really a PNG, but bytes are bytes");

        #endregion


        #region TheIdIsTheHashOfTheBytes()

        /// <summary>
        /// The item id is SHA-1 of the image, lower-case hex.
        /// </summary>
        /// <remarks>
        /// Against a hash computed here rather than against a constant: a
        /// constant would pin whatever this produced on the day it was written,
        /// which is the one thing already known.
        /// </remarks>
        [Test]
        public void TheIdIsTheHashOfTheBytes()
        {

#pragma warning disable SCS0006
            var expected = Convert.ToHexString(SHA1.HashData(Picture)).ToLowerInvariant();
#pragma warning restore SCS0006

            var info = UserAvatar.Describe(Picture, "image/png", 64, 64);

            Assert.Multiple(() =>
            {
                Assert.That(info.Id,     Is.EqualTo(expected));
                Assert.That(info.Id,     Is.EqualTo(info.Id.ToLowerInvariant()),
                            "An id in upper case is a different cache key for the same picture.");
                Assert.That(info.Bytes,  Is.EqualTo(Picture.Length));
                Assert.That(info.Type,   Is.EqualTo("image/png"));
            });

        }

        #endregion

        #region TheMetadataSaysWhatWasDescribed()

        [Test]
        public void TheMetadataSaysWhatWasDescribed()
        {

            var info      = UserAvatar.Describe(Picture, "image/png", 64, 48);
            var metadata  = UserAvatar.MetadataItem(info);

            var read      = UserAvatar.InfosIn(metadata);

            Assert.That(read, Has.Count.EqualTo(1));

            Assert.Multiple(() =>
            {
                Assert.That(read[0].Id,      Is.EqualTo(info.Id));
                Assert.That(read[0].Bytes,   Is.EqualTo(Picture.Length));
                Assert.That(read[0].Type,    Is.EqualTo("image/png"));
                Assert.That(read[0].Width,   Is.EqualTo(64));
                Assert.That(read[0].Height,  Is.EqualTo(48));
                Assert.That(UserAvatar.IsNoAvatar(metadata), Is.False);
            });

        }

        #endregion

        #region SeveralFormsAreAllKept()

        /// <summary>
        /// The same picture offered twice, and both come back.
        /// </summary>
        /// <remarks>
        /// XEP-0084 allows several <c>&lt;info/&gt;</c> elements for one
        /// picture - a PEP copy and an HTTP one, say. Returning only the first
        /// would quietly drop the one a caller could use in favour of one it
        /// cannot: this library does not follow a <c>url</c>, so a client that
        /// got only that one would show nothing.
        /// </remarks>
        [Test]
        public void SeveralFormsAreAllKept()
        {

            var metadata = XElement.Parse(
                $"<metadata xmlns='{UserAvatar.MetadataNode}'>" +
                 "<info id='aa' bytes='10' type='image/png'/>" +
                 "<info id='aa' bytes='10' type='image/png' url='https://example.org/a.png'/>" +
                 "</metadata>");

            var read = UserAvatar.InfosIn(metadata);

            Assert.Multiple(() =>
            {
                Assert.That(read,          Has.Count.EqualTo(2));
                Assert.That(read[0].Url,   Is.Null);
                Assert.That(read[1].Url?.AbsoluteUri, Is.EqualTo("https://example.org/a.png"));
            });

        }

        #endregion

        #region AnInfoWithoutWhatIsNeeded_IsLeftOut()

        /// <summary>
        /// No id, no length, no type - no entry.
        /// </summary>
        /// <remarks>
        /// The id is what the data is fetched by, so an entry without one has
        /// nothing that can be done with it. Left out rather than guessed at,
        /// and left out rather than making the whole item unreadable: an
        /// announcement with one broken form and one good one is still an
        /// announcement.
        /// </remarks>
        [Test]
        public void AnInfoWithoutWhatIsNeeded_IsLeftOut()
        {

            var metadata = XElement.Parse(
                $"<metadata xmlns='{UserAvatar.MetadataNode}'>" +
                 "<info bytes='10' type='image/png'/>" +
                 "<info id='bb' type='image/png'/>" +
                 "<info id='cc' bytes='10'/>" +
                 "<info id='dd' bytes='-1' type='image/png'/>" +
                 "<info id='ee' bytes='10' type='image/png'/>" +
                 "</metadata>");

            var read = UserAvatar.InfosIn(metadata);

            Assert.Multiple(() =>
            {
                Assert.That(read,        Has.Count.EqualTo(1));
                Assert.That(read[0].Id,  Is.EqualTo("ee"));

                Assert.That(UserAvatar.IsNoAvatar(metadata), Is.False,
                            "An item with unreadable entries is not an item saying the picture is gone.");
            });

        }

        #endregion

        #region AnEmptyMetadata_MeansTheFaceCameDown()

        /// <summary>
        /// The one way to say "there is no picture any more".
        /// </summary>
        /// <remarks>
        /// <b>Publishing nothing is not publishing an absence.</b> A node left
        /// alone goes on announcing the old picture to everybody who subscribes
        /// later, so removal has to arrive as something. And it has to be
        /// distinguishable from an item that could not be read: one means take
        /// the face down, the other means leave what is there alone.
        /// </remarks>
        [Test]
        public void AnEmptyMetadata_MeansTheFaceCameDown()
        {

            Assert.Multiple(() =>
            {

                Assert.That(UserAvatar.IsNoAvatar(UserAvatar.NoAvatarItem()), Is.True);

                Assert.That(UserAvatar.InfosIn(UserAvatar.NoAvatarItem()), Is.Empty);

                Assert.That(UserAvatar.IsNoAvatar(
                                XElement.Parse("<something xmlns='urn:x'/>")), Is.False,
                            "Anything that is not a <metadata/> was read as one saying the picture " +
                            "is gone.");

            });

        }

        #endregion

        #region TheDataIsCheckedAgainstWhatWasAnnounced()

        /// <summary>
        /// Bytes that do not hash to the id they were fetched under are refused.
        /// </summary>
        /// <remarks>
        /// <b>Not because it makes the picture trustworthy</b> - the publisher
        /// controls both nodes and can publish whatever they like, consistently.
        /// The id is what everything downstream caches by, and bytes filed under
        /// an id they do not hash to are shown for every later avatar that
        /// really has that id, long after the mistake has been fixed elsewhere.
        /// </remarks>
        [Test]
        public void TheDataIsCheckedAgainstWhatWasAnnounced()
        {

            var info  = UserAvatar.Describe(Picture, "image/png");
            var item  = UserAvatar.DataItem(Picture);

            Assert.That(UserAvatar.DataIn(item, info), Is.EqualTo(Picture),
                        "The ordinary case has to go on working.");

            var other = UserAvatar.DataItem(Encoding.UTF8.GetBytes("a different picture entirely"));

            Assert.Multiple(() =>
            {

                Assert.That(UserAvatar.DataIn(other, info), Is.Null,
                            "A picture that is not the one announced was handed over as if it were.");

                // The length is announced too, and checked: a client that only
                // compared hashes would do the expensive thing before the cheap
                // one, and on a byte count it was told not to expect.
                Assert.That(UserAvatar.DataIn(item, info with { Bytes = info.Bytes + 1 }), Is.Null,
                            "The announced length was not checked.");

                // The *right* bytes in the wrong namespace, because anything
                // else is refused by the length check before the namespace is
                // ever looked at - measured, with a mutation that dropped the
                // namespace check and survived.
                Assert.That(UserAvatar.DataIn(
                                new XElement(XName.Get("data", "urn:x"),
                                             Convert.ToBase64String(Picture)), info), Is.Null,
                            "An element from another namespace was read as avatar data.");

                Assert.That(UserAvatar.DataIn(
                                new XElement(XName.Get("data", UserAvatar.DataNode), "not base64 !!"),
                                info), Is.Null,
                            "Something that is not base64 came back as a picture.");

            });

        }

        #endregion

        #region APictureTooLarge_IsNotDecoded()

        /// <summary>
        /// An announced size beyond the limit is refused before decoding.
        /// </summary>
        /// <remarks>
        /// The item comes from a contact and arrives base64 in a stanza, so a
        /// client without a limit lets anybody in its roster decide how much it
        /// allocates. Checked against what was <em>announced</em>, which is the
        /// cheap number - the base64 is already in memory by then, but the
        /// decoded copy need not be.
        /// </remarks>
        [Test]
        public void APictureTooLarge_IsNotDecoded()
        {

            // A picture that really is too large, and whose announcement is
            // truthful about it.
            //
            // The first version of this round announced a huge size for a small
            // picture, and proved nothing: with the limit taken away the bytes
            // were decoded and then refused by the *length* check, so the round
            // stayed green either way. A mutation that removed the limit survived
            // it, which is how that came out.
            var huge = RandomNumberGenerator.GetBytes(UserAvatar.MaxImageBytes + 1);
            var info = UserAvatar.Describe(huge, "image/png");

            Assert.That(UserAvatar.DataIn(UserAvatar.DataItem(huge), info), Is.Null,
                        "A picture beyond the limit was decoded - and the limit is what stops " +
                        "a contact deciding how much this client allocates.");

            // And one just inside it still works, so the guard is a limit and not
            // a wall.
            var big     = RandomNumberGenerator.GetBytes(UserAvatar.MaxImageBytes);
            var bigInfo = UserAvatar.Describe(big, "image/png");

            Assert.That(UserAvatar.DataIn(UserAvatar.DataItem(big), bigInfo), Is.EqualTo(big));

        }

        #endregion

        #region TheRoundTripSurvivesTheWire()

        /// <summary>
        /// Published and read back through the XML, both nodes.
        /// </summary>
        [Test]
        public void TheRoundTripSurvivesTheWire()
        {

            var image  = RandomNumberGenerator.GetBytes(3000);
            var info   = UserAvatar.Describe(image, "image/jpeg", 96, 96);

            var meta   = XElement.Parse(UserAvatar.MetadataItem(info).ToString());
            var data   = XElement.Parse(UserAvatar.DataItem(image).  ToString());

            var read   = UserAvatar.InfosIn(meta);

            Assert.That(read, Has.Count.EqualTo(1));

            Assert.That(UserAvatar.DataIn(data, read[0]), Is.EqualTo(image));

        }

        #endregion

    }

}
