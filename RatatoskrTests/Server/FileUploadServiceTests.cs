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
using System.Security.Cryptography;
using System.Text;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0363 against a server of our own, on one port.
    /// </summary>
    /// <remarks>
    /// <b>The first round trip in this repository that goes through both
    /// protocols without leaving the process.</b> The slot is asked for over
    /// the WebSocket at <c>/xmpp</c>, the file goes over HTTP to
    /// <c>/upload</c>, and both are the same listener and the same
    /// certificate - which is what the WebSocket had to move onto the HTTP
    /// server for.
    ///
    /// What is asked here is what <c>AForeignPeerUploadTests</c> asks of
    /// Prosody and ejabberd, turned around: there the question was whether a
    /// foreign service refuses what it should, here whether ours does. The four
    /// refusals are the interesting half - a service that only gets the happy
    /// path right is a drop box for whoever finds the port.
    /// </remarks>
    [TestFixture]
    public class FileUploadServiceTests
    {

        #region Data

        private XMPPServer   server   = null!;
        private XMPPClient?  client;
        private HttpClient   http     = null!;

        #endregion

        #region Setting up / tearing down

        [SetUp]
        public void Start()
        {

            // Switched on before the start, because the address the service
            // hands out has to name the port the bind settles.
            server = new XMPPServer { OfferFileUploads = true };

            server.Start();
            server.AddAccount("alice");

            // The same pin the stream uses. The test server signs its own
            // certificate and no machine trusts it - and it is the *same*
            // certificate for both halves now, which is the point of the whole
            // arrangement: one listener, one certificate, one port.
            var handler = new SocketsHttpHandler();

            handler.SslOptions.RemoteCertificateValidationCallback =
                (sender, certificate, chain, errors) => server.IsOwnCertificate(sender, certificate, chain, errors);

            // Ten seconds and not the default hundred: everything here is on
            // the loopback and in one process, so a request that takes longer
            // is not slow but stuck - and a test that reports that after a
            // hundred seconds has made a fault into a nuisance. It found one:
            // a second request after a "Connection: close" hung, because
            // nothing had closed the socket.
            http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        }

        [TearDown]
        public async Task Stop()
        {

            if (client is not null)
            {
                try { await client.DisposeAsync(); } catch { }
                client = null;
            }

            http.Dispose();

            await server.DisposeAsync();

        }

        private async Task<XMPPClient> ConnectAsync()
        {

            var connection = new XMPPConnection(
                                 JID.Parse($"alice@{server.Domain}"),
                                 "pw",
                                 server.Uri
                             ) {
                                 KeepaliveEnabled            = false,
                                 MaxReconnectAttempts        = 0,
                                 ServerCertificateValidator  = server.IsOwnCertificate
                             };

            client = new XMPPClient(connection);

            await client.ConnectAsync();

            return client;

        }

        private static Byte[] SomeBytes(Int32 howMany)
        {
            var bytes = new Byte[howMany];
            RandomNumberGenerator.Fill(bytes);
            return bytes;
        }

        #endregion


        #region 1. The service is found where a client is told to look

        /// <summary>
        /// XEP-0363, section 4: through disco#items and then disco#info.
        /// </summary>
        /// <remarks>
        /// <b>The server had no disco#items at all before this.</b> It
        /// announced the feature and then did not answer the query, which is
        /// the one combination there must not be - and it went unnoticed
        /// because nothing this server carried had ever needed listing. A
        /// component is the first thing that does.
        /// </remarks>
        [Test]
        public async Task TheServiceIsFoundWhereAClientIsToldToLook()
        {

            var alice    = await ConnectAsync();
            var service  = await alice.DiscoverUploadServiceAsync();

            Assert.That(service, Is.Not.Null,
                        "Our own server carries an upload service and a client walking the " +
                        "specification's path does not find it.");

            Assert.Multiple(() =>
            {
                Assert.That(service!.Address.ToString(), Is.EqualTo($"upload.{server.Domain}"));
                Assert.That(service.MaxFileSize,         Is.EqualTo(server.Upload!.MaxFileSize));
            });

        }

        /// <summary>
        /// And nothing is announced when the service is off.
        /// </summary>
        /// <remarks>
        /// A server that has no upload service must not list one. Worth a round
        /// of its own because the announcement and the service are two pieces of
        /// code, and it is the announcement that a client believes.
        /// </remarks>
        [Test]
        public async Task AServerWithoutTheServiceAnnouncesNone()
        {

            await server.DisposeAsync();

            server = new XMPPServer();
            server.Start();
            server.AddAccount("alice");

            var handler = new SocketsHttpHandler();
            handler.SslOptions.RemoteCertificateValidationCallback =
                (sender, certificate, chain, errors) => server.IsOwnCertificate(sender, certificate, chain, errors);
            http.Dispose();
            http = new HttpClient(handler);

            var alice = await ConnectAsync();

            Assert.That(await alice.DiscoverUploadServiceAsync(), Is.Null);

        }

        #endregion

        #region 2. What goes up comes down

        /// <summary>
        /// Both protocols, one port, one process.
        /// </summary>
        [Test]
        public async Task WhatGoesUpComesDown()
        {

            var alice    = await ConnectAsync();
            var content  = SomeBytes(4096);

            using var stream = new MemoryStream(content);

            var outcome = await alice.Connection.Upload!.UploadAsync(
                              stream, content.Length, "round2.bin", "application/octet-stream");

            Assert.That(outcome.Uploaded, Is.True,
                        $"The file did not go up: {outcome.Refusal}, HTTP {outcome.HttpStatus}");

            var fetched = await alice.DownloadFileAsync(outcome.Url!);

            Assert.That(fetched, Is.EqualTo(content), "What came back is not what went up.");

        }

        #endregion

        #region 3. A slot nobody issued is not a slot

        /// <summary>
        /// The question that decides what an upload service is.
        /// </summary>
        /// <remarks>
        /// Sent by hand, because the library cannot ask it: it only ever sends
        /// to an address it was handed. The download has to be anonymous - the
        /// person a file is sent to is usually on another server - and the
        /// upload must not be, or this is a file drop for whoever finds the
        /// port, and the port will be found.
        /// </remarks>
        [Test]
        public async Task ASlotNobodyIssuedIsNotASlot()
        {

            await ConnectAsync();

            var invented = $"{server.Upload!.BaseUrl}{ServerUploadService.Path}/" +
                           $"{Guid.NewGuid():N}/anything.bin";

            var answer = await http.PutAsync(invented, new ByteArrayContent(SomeBytes(16)));

            Assert.That((Int32) answer.StatusCode, Is.InRange(400, 499),
                        $"A file was taken at an address that was never handed out ({(Int32) answer.StatusCode}).");

        }

        #endregion

        #region 4. A slot is for one file, of the size it was asked for

        /// <summary>
        /// The two promises a slot makes, and both are checked.
        /// </summary>
        /// <remarks>
        /// <b>Once</b>, because the address is meant to be shared: a second PUT
        /// would let whoever saw it swap the file under somebody who had already
        /// been sent the link. And <b>that size</b>, because otherwise the limit
        /// the service announces binds nobody - a slot for ten bytes would take
        /// a gigabyte.
        /// </remarks>
        [Test]
        public async Task ASlotIsForOneFileOfTheSizeItWasAskedFor()
        {

            var alice = await ConnectAsync();

            var outcome = await alice.Connection.Upload!.RequestSlotAsync(
                              server.Upload!.Address, "round4.bin", 10, "application/octet-stream");

            Assert.That(outcome.Granted, Is.True, $"No slot: {outcome.Refusal}");

            var tooMuch = await http.PutAsync(outcome.Slot!.PutUrl,
                                              new ByteArrayContent(SomeBytes(10_000)));

            Assert.That((Int32) tooMuch.StatusCode, Is.InRange(400, 499),
                        "A thousand times the announced size was taken into a slot issued for ten bytes.");

            var right = await http.PutAsync(outcome.Slot.PutUrl,
                                            new ByteArrayContent(SomeBytes(10)));

            Assert.That(right.IsSuccessStatusCode, Is.True,
                        "The right number of bytes was refused.");

            var again = await http.PutAsync(outcome.Slot.PutUrl,
                                            new ByteArrayContent(SomeBytes(10)));

            Assert.That((Int32) again.StatusCode, Is.InRange(400, 499),
                        "The same slot took a second file, so a link that was shared can be swapped.");

        }

        #endregion

        #region 5. A file over the limit is refused, and the refusal names it

        [Test]
        public async Task AFileOverTheLimitIsRefusedAndTheLimitIsNamed()
        {

            var alice = await ConnectAsync();

            var outcome = await alice.Connection.Upload!.RequestSlotAsync(
                              server.Upload!.Address,
                              "huge.bin",
                              server.Upload.MaxFileSize + 1,
                              "application/octet-stream");

            Assert.Multiple(() =>
            {

                Assert.That(outcome.Granted,      Is.False,
                            "A slot was handed out for a file larger than the announced limit.");

                Assert.That(outcome.MaxFileSize,  Is.EqualTo(server.Upload.MaxFileSize),
                            "The refusal does not name the limit, so a client cannot try something smaller.");

            });

        }

        #endregion

        #region 6. A slot goes stale

        /// <summary>
        /// A capability that never expires is a password that was written down.
        /// </summary>
        [Test]
        public async Task ASlotGoesStale()
        {

            server.Upload!.SlotLifetime = TimeSpan.Zero;

            var alice = await ConnectAsync();

            var outcome = await alice.Connection.Upload!.RequestSlotAsync(
                              server.Upload.Address, "late.bin", 4, "application/octet-stream");

            Assert.That(outcome.Granted, Is.True, $"No slot: {outcome.Refusal}");

            var answer = await http.PutAsync(outcome.Slot!.PutUrl,
                                             new ByteArrayContent(SomeBytes(4)));

            Assert.That((Int32) answer.StatusCode, Is.InRange(400, 499),
                        "A slot that had expired still took a file.");

        }

        #endregion

        #region 7. What is served back is served defensively

        /// <summary>
        /// An upload service serves strangers' files under its own name.
        /// </summary>
        /// <remarks>
        /// <b>Which makes an uploaded page a script on this server's origin.</b>
        /// So: the type the uploader claimed and no sniffing around for a better
        /// idea, a content policy that forbids the page everything, and anything
        /// outside the handful of types that are safe to show handed over as a
        /// download. Prosody's <c>safe_file_types</c> is the same decision.
        /// </remarks>
        [Test]
        public async Task WhatIsServedBackIsServedDefensively()
        {

            var alice  = await ConnectAsync();
            var page   = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");

            using var stream = new MemoryStream(page);

            var outcome = await alice.Connection.Upload!.UploadAsync(
                              stream, page.Length, "page.html", "text/html");

            Assert.That(outcome.Uploaded, Is.True, $"HTTP {outcome.HttpStatus}");

            var answer = await http.GetAsync(outcome.Url!);

            Assert.Multiple(() =>
            {

                Assert.That(answer.Headers.TryGetValues("X-Content-Type-Options", out var nosniff) &&
                            nosniff.Contains("nosniff"), Is.True,
                            "Without nosniff a browser is free to decide this is something else.");

                Assert.That(answer.Headers.TryGetValues("Content-Security-Policy", out _), Is.True,
                            "An uploaded page is served with nothing forbidding it anything.");

                Assert.That(answer.Content.Headers.ContentDisposition?.DispositionType,
                            Is.EqualTo("attachment"),
                            "HTML uploaded by a stranger is shown rather than handed over, which makes " +
                            "it a script on this server's origin.");

            });

        }

        [Test]
        public async Task APictureIsStillShown()
        {

            var alice  = await ConnectAsync();
            var bytes  = SomeBytes(64);

            using var stream = new MemoryStream(bytes);

            var outcome = await alice.Connection.Upload!.UploadAsync(
                              stream, bytes.Length, "cat.png", "image/png");

            var answer = await http.GetAsync(outcome.Url!);

            Assert.That(answer.Content.Headers.ContentDisposition?.DispositionType,
                        Is.EqualTo("inline"),
                        "Being careful with HTML must not turn every picture into a download.");

        }

        #endregion

        #region 8. A file reaches somebody else as a file

        /// <summary>
        /// XEP-0363 with XEP-0066, end to end, in one process.
        /// </summary>
        [Test]
        public async Task AFileReachesSomebodyElseAsAFile()
        {

            var alice = await ConnectAsync();

            server.AddAccount("bob");

            var bobConnection = new XMPPConnection(
                                    JID.Parse($"bob@{server.Domain}"),
                                    "pw",
                                    server.Uri
                                ) {
                                    KeepaliveEnabled            = false,
                                    MaxReconnectAttempts        = 0,
                                    ServerCertificateValidator  = server.IsOwnCertificate
                                };

            var bob = new XMPPClient(bobConnection);

            try
            {

                await bob.ConnectAsync();

                XMPPMessage? heard = null;
                bob.OnMessage += (t, s, m, ct) => { if (m.IsFile) heard = m; return Task.CompletedTask; };

                var content = SomeBytes(2048);

                using var stream = new MemoryStream(content);

                var sent = await alice.SendFileAsync(bob.BareJid, stream, content.Length,
                                                     "round8.bin", "application/octet-stream");

                Assert.That(sent.Sent, Is.True,
                            $"Nothing was sent: {sent.Upload.Refusal}, HTTP {sent.Upload.HttpStatus}");

                var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (heard is null && DateTime.UtcNow < until)
                    await Task.Delay(50);

                Assert.That(heard, Is.Not.Null, "No message about a file reached the other side.");

                Assert.That(heard!.FileUrl, Is.EqualTo(sent.Url));

                Assert.That(await bob.DownloadFileAsync(heard.FileUrl!), Is.EqualTo(content),
                            "What the recipient fetched is not what was sent.");

            }
            finally
            {
                try { await bob.DisposeAsync(); } catch { }
            }

        }

        #endregion

        #region 9. A file the server cannot read, all the way to somebody else

        /// <summary>
        /// XEP-0454 over XEP-0363 over XEP-0066, end to end in one process.
        /// </summary>
        /// <remarks>
        /// <b>The encryption is against the storage and not against the
        /// conversation</b>, and this round is arranged so that the difference
        /// is visible. The server holds bytes it cannot read - checked by
        /// fetching them without the key - and the recipient reads them without
        /// asking the server anything, because the key came to them in the
        /// message.
        ///
        /// Which is also the limit, and it is worth being plain about: whoever
        /// can read the message can read the file. If that message went in the
        /// clear, so did the key. What <c>aesgcm://</c> buys is that the host
        /// holding the bytes is not among the readers - no more and no less.
        /// </remarks>
        [Test]
        public async Task AFileTheServerCannotReadReachesSomebodyElse()
        {

            var alice = await ConnectAsync();

            server.AddAccount("bob");

            var bobConnection = new XMPPConnection(
                                    JID.Parse($"bob@{server.Domain}"),
                                    "pw",
                                    server.Uri
                                ) {
                                    KeepaliveEnabled            = false,
                                    MaxReconnectAttempts        = 0,
                                    ServerCertificateValidator  = server.IsOwnCertificate
                                };

            var bob = new XMPPClient(bobConnection);

            try
            {

                await bob.ConnectAsync();

                XMPPMessage? heard = null;
                bob.OnMessage += (t, s, m, ct) => { if (m.IsFile) heard = m; return Task.CompletedTask; };

                var secret = Encoding.UTF8.GetBytes("Nicht für den Server: äöüß 🎺");

                using var stream = new MemoryStream(secret);

                var sent = await alice.SendEncryptedFileAsync(bob.BareJid, stream, "passport.png");

                Assert.That(sent.Sent, Is.True,
                            $"Nothing was sent: {sent.Upload.Refusal}, HTTP {sent.Upload.HttpStatus}");

                var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (heard is null && DateTime.UtcNow < until)
                    await Task.Delay(50);

                Assert.That(heard, Is.Not.Null, "No message about a file reached the other side.");

                Assert.That(AesGcmUrl.IsAesGcmUrl(heard!.FileUrl!), Is.True,
                            "What arrived is a plain address, so the recipient would fetch the " +
                            "ciphertext and show it as the file.");

                // What the server is holding, asked for without the key - and
                // from the service's own store rather than over HTTP, so there
                // is no chance of the check accidentally measuring the download.
                var stored = await bob.DownloadFileAsync(AesGcmUrl.ToHttps(heard.FileUrl!));

                Assert.Multiple(() =>
                {

                    Assert.That(stored, Is.Not.Null, "The ciphertext cannot be fetched at all.");

                    Assert.That(stored!, Is.Not.EqualTo(secret),
                                "The server is holding the plaintext, so nothing was encrypted.");

                });

                var read = await bob.DownloadEncryptedFileAsync(heard.FileUrl!);

                Assert.That(read, Is.EqualTo(secret),
                            "The recipient could not read the file that was sent to them.");

            }
            finally
            {
                try { await bob.DisposeAsync(); } catch { }
            }

        }

        /// <summary>
        /// A ciphertext that was changed on the way does not come back as a file.
        /// </summary>
        /// <remarks>
        /// <b>The storage host is the party the encryption is against</b>, so
        /// the interesting case is that host handing back something other than
        /// what it was given. Without the tag being checked the caller takes it
        /// for the received file; with it, the answer is null.
        ///
        /// Null and not an exception: a caller cannot be asked to tell a
        /// cryptographic failure from a timeout in a catch block, and the two
        /// mean entirely different things about what to do next.
        /// </remarks>
        [Test]
        public async Task ATamperedFileDoesNotComeBack()
        {

            var alice  = await ConnectAsync();
            var secret = SomeBytes(256);

            using var stream = new MemoryStream(secret);

            var encrypted = await alice.Connection.Upload!.UploadEncryptedAsync(stream, "x.bin");

            Assert.That(encrypted.Uploaded, Is.True);

            // The same address with one bit of the key turned over - which is
            // what a host handing back a different file looks like from here.
            var fragment = encrypted.Url!.Fragment.TrimStart('#');
            var material = Convert.FromHexString(fragment);

            material[^1] ^= 0x01;

            var forged = new UriBuilder(encrypted.Url) {
                             Fragment = Convert.ToHexString(material).ToLowerInvariant()
                         }.Uri;

            Assert.That(await alice.DownloadEncryptedFileAsync(forged), Is.Null,
                        "A file that did not authenticate was handed back as the file.");

        }

        #endregion

    }

}
