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
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Threading;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;


#region (record) UploadSlotOutcome

/// <summary>
/// XEP-0363: the answer to a request for a slot.
/// </summary>
/// <param name="Slot">The slot, when one was given.</param>
/// <param name="Refusal">The error, when the service said no.</param>
/// <param name="MaxFileSize">
/// The limit the refusal named, when it named one.
/// </param>
/// <remarks>
/// All three can be absent at once, and that is the third case: the service
/// said nothing at all. Told apart because a caller decides something different
/// in each - try smaller, try later, try somewhere else.
/// </remarks>
public sealed record UploadSlotOutcome(UploadSlot?   Slot,
                                       StanzaError?  Refusal,
                                       Int64?        MaxFileSize)
{

    /// <summary>Is there a slot?</summary>
    public Boolean Granted   => Slot is not null;

    /// <summary>Did the service say nothing at all?</summary>
    public Boolean TimedOut  => Slot is null && Refusal is null;

    /// <summary>
    /// Was it refused for being too large - and is the real limit known now?
    /// </summary>
    public Boolean TooLarge  => Slot is null && MaxFileSize.HasValue;

}

#endregion

#region (record) UploadOutcome

/// <summary>
/// XEP-0363: how an upload ended, all the way through.
/// </summary>
/// <param name="Url">
/// Where the file can be fetched - the one thing worth sending to anybody.
/// </param>
/// <param name="Refusal">The XMPP side said no.</param>
/// <param name="MaxFileSize">The limit, when a refusal named one.</param>
/// <param name="HttpStatus">
/// The HTTP side said no, with this status. <b>Told apart from the XMPP
/// refusal on purpose</b>: a slot that was granted and then would not take the
/// file is a different fault from one that was never granted, and the two are
/// fixed in different places.
/// </param>
public sealed record UploadOutcome(Uri?           Url,
                                   StanzaError?   Refusal      = null,
                                   Int64?         MaxFileSize  = null,
                                   HttpStatusCode? HttpStatus  = null)
{

    /// <summary>Is the file up?</summary>
    public Boolean Uploaded    => Url is not null;

    /// <summary>
    /// Was there nothing to ask? Neither half refused, because neither half
    /// was ever reached.
    /// </summary>
    public Boolean NoService   => Url is null && Refusal is null && HttpStatus is null;

}

#endregion


/// <summary>
/// XEP-0363: finding the upload service, asking it for a slot, and putting the
/// file there.
/// </summary>
/// <remarks>
/// <b>Two protocols, and the seam between them is where this goes wrong.</b>
/// The IQ is easy and provable from one side; the PUT is neither. What makes an
/// upload an upload is that the bytes arrive at an address the service invented
/// and that somebody else can then fetch them - none of which a client can
/// arrange with itself.
///
/// Three decisions live here rather than in the caller:
///
/// <list type="bullet">
///   <item><b>Redirects are not followed.</b> The PUT carries a header the
///         service dictated, at an address the service invented. A 307 to
///         somewhere else would repeat both at an address nobody vouched for,
///         which is the one thing a capability must never do.</item>
///   <item><b>The certificate is judged the same way the stream is.</b>
///         Whoever trusts this server for the conversation trusts it for the
///         file; whoever pins it has to pin it in both places, or the pin is
///         a decoration around an unguarded door.</item>
///   <item><b>The size that was promised is the size that is sent.</b> The
///         service checked the slot against the number in the request and will
///         refuse anything else - so the Content-Length comes from that
///         number, not from whatever the stream happens to hold.</item>
/// </list>
/// </remarks>
public sealed class UploadManager : IDisposable
{

    #region Data

    private readonly JID                                                                _ownServer;
    private readonly DiscoManager                                                       _disco;
    private readonly Func<JID?, String, XElement, CancellationToken, Task<XElement?>>?   _ask;
    private readonly ILogger                                                            _logger;
    private readonly HttpClient                                                         _http;

    private UploadService?  _service;
    private Boolean         _looked;
    private readonly SemaphoreSlim _looking = new(1, 1);

    #endregion

    #region Properties

    /// <summary>
    /// The service, once it has been looked for - null before that, and null
    /// when there is none.
    /// </summary>
    public UploadService? Service => _service;

    /// <summary>
    /// Has the search already happened? Tells "no service" apart from "not
    /// asked yet", which <see cref="Service"/> alone cannot.
    /// </summary>
    public Boolean Searched => _looked;

    #endregion

    #region Constructor(s)

    /// <summary>
    /// Creates the manager.
    /// </summary>
    /// <param name="ownServer">The server whose services are to be searched.</param>
    /// <param name="disco">XEP-0030, which is how a service is found at all.</param>
    /// <param name="ask">Sends an IQ and gives back the answer.</param>
    /// <param name="certificateValidator">
    /// The same judgement the stream uses, or null for the platform's.
    /// </param>
    /// <param name="httpHandler">
    /// What the HTTP half is sent through. Null builds the real one.
    /// </param>
    /// <remarks>
    /// <b>The handler is a parameter because two decisions in here cannot
    /// otherwise be asked about at all</b>: whether a refused PUT is reported
    /// as a failure, and whether a refused GET is reported as no file. Neither
    /// can be provoked from a well-behaved service - it grants the slot and
    /// then takes the file - and both are the kind of mistake that turns a
    /// failure into a silent success. A mutation for each survived the whole
    /// suite before this parameter existed.
    /// </remarks>
    public UploadManager(JID                                                               ownServer,
                         DiscoManager                                                      disco,
                         Func<JID?, String, XElement, CancellationToken, Task<XElement?>>?  ask                   = null,
                         RemoteCertificateValidationCallback?                              certificateValidator  = null,
                         ILogger?                                                          logger                = null,
                         HttpMessageHandler?                                               httpHandler           = null)
    {

        _ownServer  = ownServer;
        _disco      = disco;
        _ask        = ask;
        _logger     = logger ?? NullLogger.Instance;

        if (httpHandler is not null)
        {
            _http = new HttpClient(httpHandler) { Timeout = TimeSpan.FromMinutes(10) };
            return;
        }

        var handler = new SocketsHttpHandler {

            // See the remarks on the class: a capability URL may not be
            // repeated at an address that did not issue it.
            AllowAutoRedirect  = false,

            // An upload is not a request-response over a few hundred bytes.
            // The default would give up on a large file over a slow line
            // halfway through, and the service would be left holding a slot
            // that was used and not filled.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)

        };

        if (certificateValidator is not null)
            handler.SslOptions.RemoteCertificateValidationCallback =
                (sender, certificate, chain, errors) => certificateValidator(sender, certificate, chain, errors);

        _http = new HttpClient(handler) {
                    Timeout = TimeSpan.FromMinutes(10)
                };

    }

    #endregion


    #region DiscoverAsync(Force = false, CancellationToken = default)

    /// <summary>
    /// XEP-0363, section 4: finds the service that hands out slots.
    /// </summary>
    /// <param name="Force">Look again even when the answer is already known.</param>
    /// <remarks>
    /// <b>The server itself first, and only then its items.</b> Both orders are
    /// seen in the wild - Prosody and ejabberd both put the service on a
    /// component of their own, but nothing in the specification says they have
    /// to, and a server that answers for itself would never be found by a
    /// client that only ever walks the item list. One extra round trip buys
    /// that.
    ///
    /// The result is kept, because this costs one query plus one per item and
    /// the answer does not change between two photographs.
    /// </remarks>
    public async Task<UploadService?> DiscoverAsync(Boolean            Force              = false,
                                                    CancellationToken  CancellationToken  = default)
    {

        if (_looked && !Force)
            return _service;

        await _looking.WaitAsync(CancellationToken);

        try
        {

            if (_looked && !Force)
                return _service;

            var onTheServer = await _disco.QueryInfoAsync(_ownServer, ct: CancellationToken);

            if (onTheServer is not null && HttpFileUpload.Announces(onTheServer))
            {
                _service  = new UploadService(_ownServer, HttpFileUpload.MaxFileSizeIn(onTheServer));
                _looked   = true;
                _logger.LogDebug("The server {Server} hands out upload slots itself", _ownServer);
                return _service;
            }

            var items = await _disco.QueryItemsAsync(_ownServer, ct: CancellationToken);

            if (items is not null)
            {
                foreach (var item in items.Items)
                {

                    if (!JID.TryParse(item.Jid, out var address))
                        continue;

                    // Every item, one after the other, and a silent one does
                    // not stop the walk: a server carries services that have
                    // nothing to do with this and one of them being asleep is
                    // no reason to give up on the rest.
                    var info = await _disco.QueryInfoAsync(address, ct: CancellationToken);

                    if (info is not null && HttpFileUpload.Announces(info))
                    {
                        _service  = new UploadService(address, HttpFileUpload.MaxFileSizeIn(info));
                        _looked   = true;
                        _logger.LogDebug("Upload service found: {Service}", _service);
                        return _service;
                    }

                }
            }

            _service  = null;
            _looked   = true;

            _logger.LogDebug("{Server} announces no upload service", _ownServer);

            return null;

        }
        finally
        {
            _looking.Release();
        }

    }

    #endregion

    #region RequestSlotAsync(Service, Filename, Size, ContentType = null, ...)

    /// <summary>
    /// XEP-0363, section 5: asks for somewhere to put one file.
    /// </summary>
    public async Task<UploadSlotOutcome> RequestSlotAsync(JID                Service,
                                                          String             Filename,
                                                          Int64              Size,
                                                          String?            ContentType        = null,
                                                          CancellationToken  CancellationToken  = default)
    {

        if (_ask is null)
            return new UploadSlotOutcome(null, null, null);

        var answer = await _ask(Service,
                                "get",
                                HttpFileUpload.Request(Filename, Size, ContentType),
                                CancellationToken);

        if (answer is null)
        {
            _logger.LogDebug("The upload service {Service} did not answer", Service);
            return new UploadSlotOutcome(null, null, null);
        }

        if (answer.Attr("type") == "error")
        {

            StanzaError.TryParse(answer.ToString(), out var error);

            // Read before anything is decided about the error, because it is
            // the useful half: a limit that is named is a limit the next
            // attempt can keep to.
            var limit = HttpFileUpload.TooLargeIn(answer);

            _logger.LogDebug("The upload service {Service} refused a slot for {Size} bytes{Limit}",
                             Service, Size,
                             limit.HasValue ? $", its limit is {limit.Value}" : "");

            return new UploadSlotOutcome(null, error, limit);

        }

        var slot = HttpFileUpload.ReadSlot(answer);

        if (slot is null)
        {
            _logger.LogDebug("The upload service {Service} answered without a usable slot", Service);
            return new UploadSlotOutcome(null, null, null);
        }

        return new UploadSlotOutcome(slot, null, null);

    }

    #endregion

    #region PutAsync(Slot, Content, Size, ContentType = null, ...)

    /// <summary>
    /// Puts the bytes where the slot says, and gives back what HTTP answered.
    /// </summary>
    /// <remarks>
    /// <b>Anything but 2xx is a failure, and the status is handed back rather
    /// than swallowed.</b> 403 means the slot was not accepted - expired, used
    /// already, or never issued; 413 means the file is larger than the slot
    /// promised. A caller told only "it did not work" cannot tell the one that
    /// is worth retrying from the one that never will be.
    /// </remarks>
    public async Task<HttpStatusCode> PutAsync(UploadSlot         Slot,
                                               Stream             Content,
                                               Int64              Size,
                                               String?            ContentType        = null,
                                               CancellationToken  CancellationToken  = default)
    {

        using var request = new HttpRequestMessage(HttpMethod.Put, Slot.PutUrl) {
                                Content = new StreamContent(Content)
                            };

        request.Content.Headers.ContentLength = Size;

        if (!String.IsNullOrEmpty(ContentType) &&
            MediaTypeHeaderValue.TryParse(ContentType, out var mediaType))
        {
            request.Content.Headers.ContentType = mediaType;
        }

        foreach (var header in Slot.Headers)
        {
            // Authorization belongs on the request, Expires on the content, and
            // .NET refuses the wrong one of the two rather than guessing. The
            // slot does not say which it meant, so both are tried.
            if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value))
                request.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        using var response = await _http.SendAsync(request,
                                                   HttpCompletionOption.ResponseHeadersRead,
                                                   CancellationToken);

        if (!response.IsSuccessStatusCode)
            _logger.LogDebug("The upload to {Url} was refused with {Status}",
                             Slot.PutUrl, (Int32) response.StatusCode);

        return response.StatusCode;

    }

    #endregion

    #region DownloadAsync(Url, CancellationToken = default)

    /// <summary>
    /// Fetches what is behind a GET url.
    /// </summary>
    /// <remarks>
    /// <b>Without any authentication, and that is the design and not an
    /// oversight.</b> The person this link is sent to is very often on another
    /// server and has no account here at all; there is nobody for this request
    /// to identify itself as. What keeps the file private is that the address
    /// cannot be guessed - which is also why it must never travel over
    /// anything but HTTPS, and why XEP-0454 puts its key in the fragment: the
    /// URL <em>is</em> the secret.
    /// </remarks>
    public async Task<Byte[]?> DownloadAsync(Uri                Url,
                                             CancellationToken  CancellationToken = default)
    {

        using var response = await _http.GetAsync(Url, CancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("{Url} answered {Status}", Url, (Int32) response.StatusCode);
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(CancellationToken);

    }

    #endregion

    #region UploadAsync(Content, Size, Filename, ContentType = null, ...)

    /// <summary>
    /// The whole way: find the service, ask for a slot, put the file there.
    /// </summary>
    /// <returns>
    /// The address the file can be fetched from, or what stood in the way.
    /// </returns>
    public async Task<UploadOutcome> UploadAsync(Stream             Content,
                                                 Int64              Size,
                                                 String             Filename,
                                                 String?            ContentType        = null,
                                                 JID?               Service            = null,
                                                 CancellationToken  CancellationToken  = default)
    {

        var service = Service ?? (await DiscoverAsync(false, CancellationToken))?.Address;

        if (service is null)
            return new UploadOutcome(null);

        var outcome = await RequestSlotAsync(service.Value, Filename, Size, ContentType, CancellationToken);

        if (outcome.Slot is null)
            return new UploadOutcome(null, outcome.Refusal, outcome.MaxFileSize);

        var status = await PutAsync(outcome.Slot, Content, Size, ContentType, CancellationToken);

        return (Int32) status is >= 200 and < 300
                   ? new UploadOutcome(outcome.Slot.GetUrl)
                   : new UploadOutcome(null, HttpStatus: status);

    }

    #endregion

    #region UploadEncryptedAsync(Content, Filename, ...)

    /// <summary>
    /// XEP-0454: encrypts a file, puts the ciphertext up and gives back the
    /// address with the key on it.
    /// </summary>
    /// <param name="Content">The file, in the clear.</param>
    /// <param name="Filename">
    /// What it is called here. <b>It does not travel</b> - see the remarks.
    /// </param>
    /// <returns>
    /// An <c>aesgcm://</c> address, or what stood in the way. The key is in its
    /// fragment, which is the one part of a URL that is never sent to a host.
    /// </returns>
    /// <remarks>
    /// <b>What the storage service is told, and what it is not.</b> It gets the
    /// ciphertext and its length, and that is nearly all there is to give away.
    /// Three things are deliberately withheld:
    ///
    /// <list type="bullet">
    ///   <item><b>the name.</b> A file called <c>scan-of-my-passport.png</c>
    ///         says most of what the encryption was for. What is uploaded is a
    ///         random name;</item>
    ///   <item><b>the type.</b> <c>application/octet-stream</c>, which is not a
    ///         polite fiction but the truth: what is being stored <em>is</em>
    ///         opaque bytes;</item>
    ///   <item><b>the extension.</b> Kept off the uploaded name for the same
    ///         reason as the type. The recipient does not need it - the message
    ///         carries the address and their client reads the file itself, and
    ///         a file type that has to be guessed from a name was never a
    ///         guess worth trusting.</item>
    /// </list>
    ///
    /// What is <em>not</em> hidden is the size, near enough. Padding it would be
    /// a decision with a cost, and one this library should not take on a
    /// caller's behalf without being asked.
    ///
    /// The plaintext is read into memory in one piece, because AES-GCM's tag
    /// covers the whole file: nothing may be handed on before the last byte has
    /// been read anyway.
    /// </remarks>
    public async Task<EncryptedUpload> UploadEncryptedAsync(Stream             Content,
                                                            String             Filename,
                                                            JID?               Service            = null,
                                                            CancellationToken  CancellationToken  = default)
    {

        using var buffer = new MemoryStream();

        await Content.CopyToAsync(buffer, CancellationToken);

        var encrypted = AesGcmUrl.Encrypt(buffer.ToArray());

        using var payload = new MemoryStream(encrypted.Payload);

        var outcome = await UploadAsync(
                          payload,
                          encrypted.Payload.LongLength,

                          // Sixteen random bytes and no extension: the service
                          // is storing bytes and has no business knowing whose
                          // or what.
                          Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),

                          "application/octet-stream",
                          Service,
                          CancellationToken
                      );

        if (outcome.Url is null)
            return new EncryptedUpload(null, outcome);

        try
        {
            return new EncryptedUpload(
                       AesGcmUrl.ToAesGcm(outcome.Url, encrypted.Key, encrypted.Nonce),
                       outcome
                   );
        }
        catch (ArgumentException e)
        {

            // The service handed out an address this cannot carry a key on -
            // in practice, plain http. Reported rather than thrown: the caller
            // asked to send a file, and "it could not be done" is an answer,
            // while an exception out of a send path becomes a 500 somewhere.
            //
            // The file is up by now and nobody will be told where. That is the
            // better of the two outcomes: the alternative is an address with a
            // key on it travelling over a transport that shows both.
            _logger.LogWarning(e, "The upload service answered with an address no encrypted " +
                                  "file can be published at");

            return new EncryptedUpload(null, outcome);

        }

    }

    #endregion

    #region DownloadEncryptedAsync(URL, CancellationToken = default)

    /// <summary>
    /// XEP-0454: fetches what is behind an <c>aesgcm://</c> address and
    /// decrypts it.
    /// </summary>
    /// <remarks>
    /// <b>The tag is checked, and the check is the throw.</b> Without it the
    /// storage host could hand back anything it liked and the caller would take
    /// it for the file that was sent - which is precisely the party the
    /// encryption is against. A file that does not authenticate comes back as
    /// null rather than as bytes.
    /// </remarks>
    public async Task<Byte[]?> DownloadEncryptedAsync(Uri                URL,
                                                      CancellationToken  CancellationToken = default)
    {

        if (!AesGcmUrl.TryParse(URL, out var key, out var nonce, out var problem))
        {
            _logger.LogDebug("{Url} cannot be read: {Problem}", URL, problem);
            return null;
        }

        var payload = await DownloadAsync(AesGcmUrl.ToHttps(URL), CancellationToken);

        if (payload is null)
            return null;

        try
        {
            return AesGcmUrl.Decrypt(payload, key!, nonce!);
        }
        catch (CryptographicException e)
        {
            // Not "the download failed". The bytes arrived and are not the ones
            // that were sent - which is either damage or somebody, and a caller
            // that cannot tell this from a timeout will retry a lie.
            _logger.LogWarning(e, "The file at {Url} did not authenticate", URL);
            return null;
        }

    }

    #endregion

    #region Dispose()

    public void Dispose()
    {
        _http.Dispose();
        _looking.Dispose();
    }

    #endregion

}
