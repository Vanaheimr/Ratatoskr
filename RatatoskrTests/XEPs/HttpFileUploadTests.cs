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

using System.Net;
using System.Net.Http;
using System.Xml.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0363 and XEP-0066: the questions a fixture can ask honestly.
    /// </summary>
    /// <remarks>
    /// <b>What is here is what a well-behaved service will never provoke.</b>
    /// The rounds against Prosody and ejabberd answer whether an upload works;
    /// they cannot answer what happens when a service asks for something it is
    /// not allowed to ask for, because neither of them does. That is not a
    /// reason to leave it unchecked - a client that would pass on any header a
    /// service names is wrong the day it meets one that names a bad one, and by
    /// then it is somebody else's server.
    ///
    /// So the slots below are written by hand, with the things in them that no
    /// real service sends. The other direction - that an upload really arrives
    /// and can really be fetched - is not asked here at all, and could not be:
    /// see <c>AForeignPeerUploadTests</c>.
    /// </remarks>
    [TestFixture]
    public class HttpFileUploadTests
    {

        #region Data

        private const String NS = HttpFileUpload.Namespace;

        /// <summary>
        /// An IQ result carrying a slot, with whatever headers are handed in.
        /// </summary>
        private static XElement SlotIq(String headers = "")

            => XElement.Parse(
                   $"<iq xmlns='jabber:client' type='result' from='upload.example.org' id='x'>" +
                   $"<slot xmlns='{NS}'>" +
                    "<put url='https://upload.example.org/put/abc/f.txt'>" + headers + "</put>" +
                    "<get url='https://upload.example.org/get/abc/f.txt'/>" +
                    "</slot></iq>");

        #endregion


        #region The slot itself

        [Test]
        public void ASlotIsReadOutOfTheResult()
        {

            var slot = HttpFileUpload.ReadSlot(SlotIq());

            Assert.That(slot, Is.Not.Null);

            Assert.Multiple(() =>
            {
                Assert.That(slot!.PutUrl.AbsoluteUri, Is.EqualTo("https://upload.example.org/put/abc/f.txt"));
                Assert.That(slot!.GetUrl.AbsoluteUri, Is.EqualTo("https://upload.example.org/get/abc/f.txt"));
            });

        }

        /// <summary>
        /// Prosody puts the <c>&lt;get/&gt;</c> first and the specification's
        /// example puts the <c>&lt;put/&gt;</c> first.
        /// </summary>
        /// <remarks>
        /// Which means order is not something to rely on, and a reading that
        /// took the first child for the put would work against one service and
        /// silently upload to the download address against the other.
        /// </remarks>
        [Test]
        public void TheOrderOfPutAndGetDoesNotMatter()
        {

            var reversed = XElement.Parse(
                               $"<iq xmlns='jabber:client' type='result' id='x'>" +
                               $"<slot xmlns='{NS}'>" +
                                "<get url='https://example.org/g'/>" +
                                "<put url='https://example.org/p'/>" +
                                "</slot></iq>");

            var slot = HttpFileUpload.ReadSlot(reversed);

            Assert.That(slot, Is.Not.Null);

            Assert.Multiple(() =>
            {
                Assert.That(slot!.PutUrl.AbsoluteUri, Is.EqualTo("https://example.org/p"));
                Assert.That(slot!.GetUrl.AbsoluteUri, Is.EqualTo("https://example.org/g"));
            });

        }

        /// <summary>
        /// Half a slot is no slot.
        /// </summary>
        /// <remarks>
        /// Without the get there is nobody who could ever be told about the
        /// file, and a client that uploaded anyway would have spent the
        /// bandwidth for nothing and have nothing to show for it.
        /// </remarks>
        [Test]
        public void ASlotWithoutBothAddressesIsNotUsable()
        {

            var onlyPut = XElement.Parse(
                              $"<iq xmlns='jabber:client' type='result' id='x'>" +
                              $"<slot xmlns='{NS}'><put url='https://example.org/p'/></slot></iq>");

            Assert.Multiple(() =>
            {
                Assert.That(HttpFileUpload.ReadSlot(onlyPut), Is.Null);
                Assert.That(HttpFileUpload.ReadSlot(
                    XElement.Parse("<iq xmlns='jabber:client' type='result' id='x'/>")), Is.Null);
            });

        }

        /// <summary>
        /// A slot points at the web or it is not a slot.
        /// </summary>
        /// <remarks>
        /// <b>This one was found by running on a second platform, and the fault
        /// was in the code rather than the test.</b> The first version asked
        /// only whether the address was absolute.
        /// <c>Uri.TryCreate("/p", UriKind.Absolute, ...)</c> fails on Windows
        /// and <em>succeeds</em> on Linux, where a leading slash is an absolute
        /// path: the result is <c>file:///p</c>. Green on the machine it was
        /// written on, red in CI.
        ///
        /// What it would have cost is not a wrong error message. The PUT writes
        /// and the GET reads, so a service handing out <c>file:///</c> could
        /// have had the client write a file onto its own disk, or read one and
        /// pass the bytes to whoever asked for the upload.
        /// </remarks>
        [Test]
        public void AnAddressThatIsNotWebIsNotASlot()
        {

            static XElement Slot(String put, String get)
                => XElement.Parse($"<iq xmlns='jabber:client' type='result' id='x'>" +
                                  $"<slot xmlns='{NS}'>" +
                                  $"<put url='{put}'/><get url='{get}'/></slot></iq>");

            Assert.Multiple(() =>
            {

                Assert.That(HttpFileUpload.ReadSlot(Slot("/p", "/g")), Is.Null,
                            "A bare path is an absolute file:// address on Linux.");

                Assert.That(HttpFileUpload.ReadSlot(Slot("file:///tmp/p", "file:///tmp/g")), Is.Null,
                            "A slot pointing at the local disk was accepted.");

                Assert.That(HttpFileUpload.ReadSlot(Slot("https://example.org/p", "file:///tmp/g")), Is.Null,
                            "Half of a slot pointing at the local disk is still a slot pointing there.");

                Assert.That(HttpFileUpload.ReadSlot(Slot("https://example.org/p", "https://example.org/g")),
                            Is.Not.Null,
                            "The ordinary case has to go on working.");

                Assert.That(HttpFileUpload.ReadSlot(Slot("http://example.org/p", "http://example.org/g")),
                            Is.Not.Null,
                            "Plain http is a deployment decision and not this library's to refuse; " +
                            "whether the real services use it is asked of the real services.");

            });

        }

        #endregion

        #region The headers, which are the dangerous part

        /// <summary>
        /// XEP-0363, section 5: three names, and nothing else goes on the wire.
        /// </summary>
        /// <remarks>
        /// <b>No real service provokes this, which is exactly why it is here.</b>
        /// The service names headers and the client sends them - one entity
        /// deciding what another puts into an HTTP request - so the list of what
        /// may be dictated has to be closed. A client that passes anything on
        /// can be told to send an arbitrary header to an arbitrary address.
        /// </remarks>
        [Test]
        public void OnlyTheThreeAllowedHeadersSurvive()
        {

            var slot = HttpFileUpload.ReadSlot(SlotIq(
                           "<header name='Authorization'>Bearer token</header>" +
                           "<header name='Cookie'>a=b</header>" +
                           "<header name='Expires'>Wed, 21 Oct 2026 07:28:00 GMT</header>" +
                           "<header name='X-Anything'>please</header>" +
                           "<header name='Host'>somewhere.else</header>"));

            Assert.That(slot, Is.Not.Null);

            Assert.That(slot!.Headers.Select(h => h.Name),
                        Is.EquivalentTo(new[] { "Authorization", "Cookie", "Expires" }),
                        "A header outside the three of section 5 got through.");

        }

        /// <summary>
        /// A header value with a line break in it is a second header.
        /// </summary>
        /// <remarks>
        /// The whole of HTTP header injection in one line: whoever passes a
        /// value containing CR or LF on unchecked lets the other side write the
        /// rest of the request - another header, or a whole second request.
        /// The name being on the allow-list does not help at all here.
        /// </remarks>
        [Test]
        public void AHeaderValueWithALineBreakIsDropped()
        {

            var slot = HttpFileUpload.ReadSlot(SlotIq(
                           "<header name='Authorization'>Bearer good&#13;&#10;X-Evil: yes</header>" +
                           "<header name='Cookie'>fine=yes</header>"));

            Assert.That(slot, Is.Not.Null);

            Assert.That(slot!.Headers.Select(h => h.Name), Is.EquivalentTo(new[] { "Cookie" }),
                        "A header value carrying a line break was passed on.");

        }

        /// <summary>
        /// The names are matched without regard to case.
        /// </summary>
        /// <remarks>
        /// HTTP header names are case-insensitive, so a service writing
        /// <c>authorization</c> means the same thing - and a client that dropped
        /// it would refuse a perfectly correct slot and blame the service.
        /// </remarks>
        [Test]
        public void TheAllowedNamesAreNotCaseSensitive()
        {

            var slot = HttpFileUpload.ReadSlot(SlotIq(
                           "<header name='authorization'>Bearer t</header>"));

            Assert.That(slot!.Headers, Has.Count.EqualTo(1));

        }

        #endregion

        #region The limit

        /// <summary>
        /// XEP-0363, section 4: the limit out of the disco form.
        /// </summary>
        /// <remarks>
        /// <b>Only out of the form that says it is this one.</b> XEP-0128 puts
        /// no limit on how many forms an entity carries, and a
        /// <c>max-file-size</c> in somebody else's form is a number about
        /// something else entirely - here, deliberately, a much smaller one, so
        /// that reading the wrong form shows up as a wrong answer and not as no
        /// answer.
        /// </remarks>
        [Test]
        public void TheLimitComesOutOfTheFormThatSaysItIsTheOne()
        {

            var info = new DiscoInfo { From = "upload.example.org" };
            info.Features.Add(NS);

            info.Forms.Add(DiscoForm.Of("urn:xmpp:some:other:form",
                                        ("max-file-size", "17")));

            info.Forms.Add(DiscoForm.Of(NS,
                                        ("max-file-size", "1048576")));

            Assert.That(HttpFileUpload.MaxFileSizeIn(info), Is.EqualTo(1048576));

        }

        [Test]
        public void NoFormMeansNoAnnouncedLimit()
        {

            var info = new DiscoInfo { From = "upload.example.org" };
            info.Features.Add(NS);

            Assert.Multiple(() =>
            {

                Assert.That(HttpFileUpload.MaxFileSizeIn(info), Is.Null,
                            "A service that announced no limit must not look like one that announced a number.");

                Assert.That(new UploadService(JID.Parse("upload.example.org"), null)
                                .IsTooLarge(Int64.MaxValue), Is.False,
                            "Nothing is known about the limit, so nothing can be said about a size.");

            });

        }

        /// <summary>
        /// XEP-0363, section 5: the refusal carries the real limit.
        /// </summary>
        [Test]
        public void ARefusalNamesTheLimit()
        {

            var error = XElement.Parse(
                            $"<iq xmlns='jabber:client' type='error' id='x'>" +
                             "<error type='modify'>" +
                             "<not-acceptable xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                             "<text xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'>File too large</text>" +
                            $"<file-too-large xmlns='{NS}'><max-file-size>1048576</max-file-size></file-too-large>" +
                             "</error></iq>");

            Assert.Multiple(() =>
            {

                Assert.That(HttpFileUpload.TooLargeIn(error), Is.EqualTo(1048576));

                Assert.That(HttpFileUpload.TooLargeIn(
                                XElement.Parse("<iq xmlns='jabber:client' type='error' id='x'>" +
                                               "<error type='auth'><forbidden " +
                                               "xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/></error></iq>")),
                            Is.Null,
                            "A refusal for another reason must not look like a limit.");

            });

        }

        #endregion

        #region The request

        /// <summary>
        /// The request names the file, the size and the type.
        /// </summary>
        /// <remarks>
        /// This was written as a culture test - the fear being that a German
        /// machine writes 1048576 as "1.048.576" and the service reads a slot
        /// request for one byte. <b>It was asked, and the answer was no.</b>
        /// Under <c>de-DE</c> the mutation that swaps the invariant culture for
        /// the current one stays green, because <c>Int64.ToString(provider)</c>
        /// is <c>ToString("G", provider)</c> and "G" on an integer writes no
        /// group separators anywhere.
        ///
        /// So the culture is gone from the test rather than kept as a
        /// reassuring green that can never turn red. The invariant culture
        /// stays in the code, with the reason for it written beside it.
        /// </remarks>
        [Test]
        public void TheRequestNamesTheFileTheSizeAndTheType()
        {

            var request = HttpFileUpload.Request("f.bin", 1048576, "application/octet-stream");

            Assert.Multiple(() =>
            {
                Assert.That(request.Attr("size"),          Is.EqualTo("1048576"));
                Assert.That(request.Attr("filename"),      Is.EqualTo("f.bin"));
                Assert.That(request.Attr("content-type"),  Is.EqualTo("application/octet-stream"));
                Assert.That(request.Name.NamespaceName,    Is.EqualTo(NS));
            });

        }

        [Test]
        public void ARequestWithoutATypeSaysNothingRatherThanNothingAtAll()
        {

            var request = HttpFileUpload.Request("f.bin", 3);

            Assert.That(request.Attr("content-type"), Is.Null,
                        "An empty content-type is a claim about the file; leaving it out is not.");

        }

        #endregion

        #region What HTTP answered, and what that is reported as

        /// <summary>
        /// An HTTP far side that always says the same thing.
        /// </summary>
        private sealed class Answers(HttpStatusCode Status, Byte[]? Body = null) : HttpMessageHandler
        {

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage  request,
                                                                   CancellationToken   cancellationToken)

                => Task.FromResult(new HttpResponseMessage(Status) {
                       Content = new ByteArrayContent(Body ?? [])
                   });

        }

        private static UploadManager ManagerAnswering(HttpStatusCode Status, Byte[]? Body = null)

            => new (JID.Parse("example.org"),
                    new DiscoManager(_ => Task.CompletedTask, "me@example.org"),
                    (to, type, payload, ct) => Task.FromResult<XElement?>(SlotIq()),
                    httpHandler: new Answers(Status, Body));

        /// <summary>
        /// A slot that was granted and a PUT that was refused is not an upload.
        /// </summary>
        /// <remarks>
        /// <b>Written because a mutation survived the whole suite.</b> Removing
        /// the status check made every upload succeed, and nothing anywhere went
        /// red - because a well-behaved service grants the slot and then takes
        /// the file, so no round had ever seen the two come apart.
        ///
        /// They do come apart in the field: a slot expires, a slot is used
        /// twice, the disk behind the service is full. Reporting that as success
        /// hands the caller an address with nothing behind it, and the caller
        /// then sends that address to somebody.
        /// </remarks>
        [Test]
        public async Task ARefusedPutIsNotAnUpload()
        {

            using var manager = ManagerAnswering(HttpStatusCode.Forbidden);
            using var content = new MemoryStream([1, 2, 3]);

            var outcome = await manager.UploadAsync(content, 3, "f.bin", "application/octet-stream",
                                                    JID.Parse("upload.example.org"));

            Assert.Multiple(() =>
            {

                Assert.That(outcome.Uploaded,   Is.False,
                            "A PUT the service refused was reported as an upload.");

                Assert.That(outcome.Url,        Is.Null);

                Assert.That(outcome.HttpStatus, Is.EqualTo(HttpStatusCode.Forbidden),
                            "The caller is not told which half said no, so it cannot tell a slot that " +
                            "was never granted from one that would not take the file.");

            });

        }

        [Test]
        public async Task AnAcceptedPutGivesBackTheAddressToShare()
        {

            using var manager = ManagerAnswering(HttpStatusCode.Created);
            using var content = new MemoryStream([1, 2, 3]);

            var outcome = await manager.UploadAsync(content, 3, "f.bin", "application/octet-stream",
                                                    JID.Parse("upload.example.org"));

            Assert.Multiple(() =>
            {

                Assert.That(outcome.Uploaded, Is.True);

                Assert.That(outcome.Url?.AbsoluteUri,
                            Is.EqualTo("https://upload.example.org/get/abc/f.txt"),
                            "What comes back has to be the address a file can be fetched from - the one " +
                            "it was put at is nobody else's business.");

            });

        }

        /// <summary>
        /// A download that was refused is not a file.
        /// </summary>
        /// <remarks>
        /// The same shape one step further on, and the same mutation survived
        /// it. An error page is bytes, and bytes are what a caller asked for -
        /// so handing them over without looking at the status means a client
        /// shows somebody a picture made of the words "404 Not Found", or
        /// decrypts one under XEP-0454 and reports a broken file.
        /// </remarks>
        [Test]
        public async Task ARefusedDownloadIsNotAFile()
        {

            using var manager = ManagerAnswering(HttpStatusCode.NotFound,
                                                 System.Text.Encoding.UTF8.GetBytes("<html>gone</html>"));

            var fetched = await manager.DownloadAsync(new Uri("https://upload.example.org/get/abc/f.txt"));

            Assert.That(fetched, Is.Null,
                        "The body of an error answer was handed over as the file.");

        }

        [Test]
        public async Task ADownloadGivesBackExactlyWhatWasThere()
        {

            var bytes = new Byte[] { 7, 8, 9, 250, 0, 13, 10 };

            using var manager = ManagerAnswering(HttpStatusCode.OK, bytes);

            var fetched = await manager.DownloadAsync(new Uri("https://upload.example.org/get/abc/f.txt"));

            Assert.That(fetched, Is.EqualTo(bytes),
                        "A file is bytes and nothing is allowed to interpret them - a stray CR LF in " +
                        "there is data, not a line ending.");

        }

        #endregion

        #region XEP-0066: the message that names the file

        /// <summary>
        /// The address in the <c>&lt;x/&gt;</c> has to be the one in the body.
        /// </summary>
        /// <remarks>
        /// <b>Otherwise a message shows one address and opens another</b>, which
        /// is the oldest trick there is and needs no server's help at all - any
        /// contact can send such a stanza. XEP-0363, section 5 has the two
        /// agree; a disagreement has no reading that is safe to guess at.
        /// </remarks>
        [Test]
        public void AnAttachmentThatDisagreesWithTheBodyIsNotFollowed()
        {

            var honest = XElement.Parse(
                             "<message xmlns='jabber:client' from='a@example.org'>" +
                             "<body>https://upload.example.org/f.png</body>" +
                             "<x xmlns='jabber:x:oob'><url>https://upload.example.org/f.png</url></x>" +
                             "</message>");

            var lying  = XElement.Parse(
                             "<message xmlns='jabber:client' from='a@example.org'>" +
                             "<body>https://upload.example.org/kitten.png</body>" +
                             "<x xmlns='jabber:x:oob'><url>https://elsewhere.example/payload.exe</url></x>" +
                             "</message>");

            Assert.Multiple(() =>
            {

                Assert.That(OutOfBandData.UrlIn(honest, honest.ChildValue("body"))?.AbsoluteUri,
                            Is.EqualTo("https://upload.example.org/f.png"));

                Assert.That(OutOfBandData.UrlIn(lying, lying.ChildValue("body")), Is.Null,
                            "A message whose attachment points somewhere other than its body was followed.");

            });

        }

        /// <summary>
        /// Only a direct child counts.
        /// </summary>
        /// <remarks>
        /// The rule of D59, and here is what it buys: a message carrying a
        /// forwarded or archived one brings that message's
        /// <c>&lt;x/&gt;</c> along. Reading it would make the outer message
        /// about a file it has nothing to do with - and the body would not
        /// match, so it would look like the attack above rather than like the
        /// ordinary thing it is.
        /// </remarks>
        [Test]
        public void AnAttachmentInsideAForwardedMessageIsNotThisMessages()
        {

            var carrier = XElement.Parse(
                              "<message xmlns='jabber:client' from='a@example.org'>" +
                              "<body>see this</body>" +
                              "<forwarded xmlns='urn:xmpp:forward:0'>" +
                              "<message xmlns='jabber:client'>" +
                              "<body>https://upload.example.org/f.png</body>" +
                              "<x xmlns='jabber:x:oob'><url>https://upload.example.org/f.png</url></x>" +
                              "</message></forwarded></message>");

            Assert.That(OutOfBandData.UrlIn(carrier, "see this"), Is.Null,
                        "An attachment belonging to a forwarded message was read as this message's.");

        }

        /// <summary>
        /// The same question where the body cannot answer it.
        /// </summary>
        /// <remarks>
        /// <b>Written because the test above cannot tell two rules apart.</b>
        /// There the forwarded address and the outer body disagree, so the
        /// agreement check refuses it and the direct-child rule is never
        /// reached - and a mutation that replaced <c>Child</c> with
        /// <c>Descendants</c> stayed green through the whole suite.
        ///
        /// Here they agree: a message that carries somebody else's file message
        /// along and happens to write the same address in its own text. It has
        /// no attachment of its own, and must not borrow one. Two rules that
        /// each cover for the other are worth having; a test that cannot say
        /// which one is working is not.
        /// </remarks>
        [Test]
        public void AForwardedAttachmentIsNotBorrowedEvenWhenTheBodyAgrees()
        {

            const String url = "https://upload.example.org/f.png";

            var carrier = XElement.Parse(
                              "<message xmlns='jabber:client' from='a@example.org'>" +
                             $"<body>{url}</body>" +
                              "<forwarded xmlns='urn:xmpp:forward:0'>" +
                              "<message xmlns='jabber:client'>" +
                             $"<body>{url}</body>" +
                             $"<x xmlns='jabber:x:oob'><url>{url}</url></x>" +
                              "</message></forwarded></message>");

            Assert.That(OutOfBandData.UrlIn(carrier, url), Is.Null,
                        "A message with no attachment of its own was given the one belonging to the " +
                        "message it forwards.");

        }

        [Test]
        public void TheAttachmentIsWrittenBesideTheBody()
        {

            var x = OutOfBandData.Xml(new Uri("https://upload.example.org/f.png"));

            Assert.Multiple(() =>
            {
                Assert.That(x.Name.NamespaceName, Is.EqualTo(OutOfBandData.Namespace));
                Assert.That(x.Child(OutOfBandData.Namespace, "url")?.Value,
                            Is.EqualTo("https://upload.example.org/f.png"));
            });

        }

        #endregion

    }

}
