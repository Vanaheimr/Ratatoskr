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

using System.Xml.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0280 with XEP-0066: a file shared on another device of one's own.
    /// </summary>
    /// <remarks>
    /// A mirrored message used to come out of the carbon with its body and no
    /// word about what the body was - so a file received on the phone reached
    /// a client here as a line of text that happened to look like an address,
    /// and whoever showed it had to guess from the shape of the text whether
    /// it was one. The guess is what the element exists to replace.
    ///
    /// The reading is the inner message's, under the same rule a message of
    /// this connection's own gets, and these tests are mostly about that
    /// "inner": the wrapper around a carbon is built by the server, and what
    /// is inside it was once a stranger's.
    /// </remarks>
    [TestFixture]
    public class CarbonFileTests
    {

        #region Data & helper functions

        private const String Mine = "alice@example.org";
        private const String Url  = "https://upload.example.org/a1b2/IMG_1234.jpg";

        private static CarbonManager Manager()
            => new (JID.Parse(Mine));

        /// <summary>
        /// A carbon of a message about a file, as XEP-0363, section 5 has one
        /// built: the address in the body and in the <c>&lt;x/&gt;</c> beside
        /// it.
        /// </summary>
        private static XElement FileCarbon(String  body,
                                           String  url,
                                           String  kind        = "received",
                                           String? outerUrl    = null)

            => XElement.Parse(
                   $"<message from='{Mine}' to='{Mine}/laptop'>" +
                     (outerUrl is not null
                          ? $"<x xmlns='{OutOfBandData.Namespace}'><url>{outerUrl}</url></x>"
                          : "") +
                     $"<{kind} xmlns='{CarbonManager.Namespace}'>" +
                       "<forwarded xmlns='urn:xmpp:forward:0'>" +
                         $"<message from='bob@example.com/phone' to='{Mine}' type='chat'>" +
                           $"<body>{body}</body>" +
                           (url.Length > 0
                                ? $"<x xmlns='{OutOfBandData.Namespace}'><url>{url}</url></x>"
                                : "") +
                         "</message>" +
                       "</forwarded>" +
                     $"</{kind}>" +
                   "</message>");

        /// <summary>
        /// What the manager hands on for this stanza, or null when it hands on
        /// nothing.
        /// </summary>
        private static async Task<CarbonMessage?> Mirrored(XElement Carbon)
        {

            var              manager  = Manager();
            CarbonMessage?   heard    = null;

            manager.OnCarbonReceived += (timestamp, sender, carbon, ct) => {
                heard = carbon;
                return Task.CompletedTask;
            };

            var result = await manager.ProcessCarbonAsync(Carbon, JID.Parse(Mine));

            Assert.That(result, Is.EqualTo(CarbonResult.Success),
                        "The carbon was not unwrapped at all, so this test is measuring nothing.");

            return heard;

        }

        #endregion


        #region AMirroredFileMessage_NamesItsFile()

        /// <summary>
        /// The ordinary case, both ways: a file somebody sent to another device
        /// of ours, and one we shared from it.
        /// </summary>
        /// <remarks>
        /// <b>Both directions, because both are a file.</b> A
        /// <c>&lt;received/&gt;</c> is a file somebody handed over and a
        /// <c>&lt;sent/&gt;</c> is one handed over from the other device - and
        /// a client that keeps conversations has to put the file beside the
        /// line in either case, or the same conversation looks different
        /// depending on which device one reads it on.
        /// </remarks>
        [Test]
        public async Task AMirroredFileMessage_NamesItsFile()
        {

            var received  = await Mirrored(FileCarbon(Url, Url));
            var sent      = await Mirrored(FileCarbon(Url, Url, kind: "sent"));

            Assert.Multiple(() =>
            {

                Assert.That(received?.IsFile,            Is.True,
                            "A file received on another device arrived here as a line of text.");

                Assert.That(received?.FileUrl?.AbsoluteUri,  Is.EqualTo(Url));

                Assert.That(sent?.IsFile,                Is.True,
                            "A file shared from another device arrived here as a line of text.");

                Assert.That(sent?.FileUrl?.AbsoluteUri,  Is.EqualTo(Url));

            });

        }

        #endregion

        #region AMirroredAttachmentThatDisagreesWithTheBody_IsNotFollowed()

        /// <summary>
        /// The address in the <c>&lt;x/&gt;</c> has to be the one in the body
        /// here as well.
        /// </summary>
        /// <remarks>
        /// <b>The rule does not get easier for having travelled.</b> The
        /// wrapper comes from one's own server, which is what the spoofing
        /// check establishes - but the message inside it came from whoever
        /// wrote it, and they are free to write a body that shows one address
        /// and an attachment that leads to another. Exactly the stanza
        /// <see cref="OutOfBandData.UrlIn"/> refuses outside a carbon.
        /// </remarks>
        [Test]
        public async Task AMirroredAttachmentThatDisagreesWithTheBody_IsNotFollowed()
        {

            var lying = await Mirrored(FileCarbon("https://upload.example.org/a1b2/kitten.jpg",
                                                  "https://elsewhere.example/payload.exe"));

            Assert.Multiple(() =>
            {

                Assert.That(lying?.IsFile,  Is.False,
                            "A mirrored message whose attachment points somewhere other than its " +
                            "body was followed.");

                Assert.That(lying?.Body,    Is.EqualTo("https://upload.example.org/a1b2/kitten.jpg"),
                            "The line itself is still the line, and still worth showing.");

            });

        }

        #endregion

        #region AnAttachmentOnTheCarbonItself_IsNotTheMirroredMessages()

        /// <summary>
        /// Only the inner message's own <c>&lt;x/&gt;</c> counts.
        /// </summary>
        /// <remarks>
        /// <b>Written for the mutation, not for the specification.</b> Reading
        /// the <c>&lt;x/&gt;</c> off the outer stanza would pass the test above
        /// and this one is the only thing that notices: the body here is the
        /// address, the outer attachment names the same address, and the inner
        /// message carries none at all. A message that is about no file must
        /// not be given one because the envelope it arrived in mentioned one.
        ///
        /// It is also the one place where it would matter. The outer stanza of
        /// a carbon is addressed to this account, so anything on it got there
        /// past the spoofing check - and the check says who forwarded the
        /// message, not what the message is about.
        /// </remarks>
        [Test]
        public async Task AnAttachmentOnTheCarbonItself_IsNotTheMirroredMessages()
        {

            var borrowed = await Mirrored(FileCarbon(Url, "", outerUrl: Url));

            Assert.That(borrowed?.IsFile, Is.False,
                        "A mirrored message with no attachment of its own was given the one that " +
                        "stood on the carbon around it.");

        }

        #endregion

    }

}
