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

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    /// <summary>
    /// XEP-0363, the serving half: handing out slots and taking the files.
    /// </summary>
    /// <remarks>
    /// <b>The two halves have opposite rules, and this is where getting that
    /// wrong costs something.</b> Who may ask for a slot is decided on the XMPP
    /// stream, which is authenticated. What comes back is a capability: an
    /// address nobody can guess, good for one file, of one announced size, for a
    /// few minutes. The HTTP side has no idea who anybody is - and must not,
    /// because the person who later fetches the file is usually on another
    /// server and has no account here.
    ///
    /// So the download is anonymous by necessity and the upload must not be, or
    /// this is a file drop for whoever finds the port. Four questions decide it,
    /// and every one of them is asked by <c>AForeignPeerUploadTests</c> of the
    /// real services as well:
    ///
    /// <list type="bullet">
    ///   <item>a PUT at an address that was never handed out is refused;</item>
    ///   <item>a slot is good once - the second PUT is refused, or a file
    ///         somebody has already been sent the address of could be swapped
    ///         underneath them;</item>
    ///   <item>the size in the request is a promise and is checked, or the
    ///         announced limit binds nobody;</item>
    ///   <item>a slot goes stale, because a capability that never expires is a
    ///         password that was written down.</item>
    /// </list>
    ///
    /// <b>And what is served back is served defensively.</b> An upload service
    /// hands out whatever it was given, under its own name: without
    /// <c>nosniff</c> and a content policy, an HTML file uploaded here is a
    /// script running on this server's origin. Anything not obviously safe to
    /// show is served as a download instead - which is what Prosody's
    /// <c>safe_file_types</c> does and for the same reason.
    ///
    /// <b>In memory, and that is a decision rather than a shortcut.</b> This is
    /// the server the test suite drives; a slot outliving the process would
    /// leave uploaded files on somebody's disk with nothing to clear them away.
    /// </remarks>
    public sealed class ServerUploadService
    {

        #region (class) Slot

        private sealed class Slot
        {

            public required String          Id            { get; init; }
            public required String          Uploader      { get; init; }
            public required String          Filename      { get; init; }
            public required Int64           Size          { get; init; }
            public required String?         ContentType   { get; init; }
            public required DateTimeOffset  ExpiresAt     { get; init; }

            /// <summary>
            /// What was put there - null while the slot is still empty.
            /// </summary>
            public Byte[]? Content { get; set; }

        }

        #endregion

        #region Data

        /// <summary>
        /// The path the files live under.
        /// </summary>
        public const String Path = "/upload";

        private readonly ConcurrentDictionary<String, Slot> _slots = new(StringComparer.Ordinal);

        /// <summary>
        /// Content types served inline; everything else is a download.
        /// </summary>
        /// <remarks>
        /// The same set Prosody's <c>http_file_share_safe_file_types</c> holds by
        /// default. Pictures, sound, film and plain text are shown; anything
        /// else, and HTML above all, is handed over as a file - a page uploaded
        /// here would otherwise run as a script on this server's own origin, and
        /// every session cookie for it would be within its reach.
        /// </remarks>
        private static readonly String[] SafePrefixes = ["image/", "video/", "audio/"];

        #endregion

        #region Properties

        /// <summary>
        /// The address the service is asked at - a domain beside the host.
        /// </summary>
        public JID    Address        { get; }

        /// <summary>
        /// The base the addresses it hands out are built on.
        /// </summary>
        public URL    BaseUrl        { get; set; }

        /// <summary>
        /// The largest file this service will take, announced in its disco form.
        /// </summary>
        public Int64  MaxFileSize    { get; set; } = 1024 * 1024;

        /// <summary>
        /// How long a slot is good for.
        /// </summary>
        /// <remarks>
        /// Minutes, not hours: the slot is a capability, and one that lies about
        /// unused is one more thing that can be found. Long enough for a large
        /// file over a slow line, and no longer.
        /// </remarks>
        public TimeSpan SlotLifetime { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How many slots are known right now - empty ones included.
        /// </summary>
        public Int32  SlotCount      => _slots.Count;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Creates the service for a host.
        /// </summary>
        /// <param name="Domain">The host it belongs to; the service lives on <c>upload.</c> plus that.</param>
        /// <param name="BaseUrl">Where its files are reachable.</param>
        public ServerUploadService(JID Domain, URL BaseUrl)
        {
            this.Address  = JID.Parse($"upload.{Domain.Domainpart}");
            this.BaseUrl  = BaseUrl;
        }

        #endregion


        #region Answer(Frame, Id, Type, From)

        /// <summary>
        /// Answers an IQ addressed to this service, or null when it is none of
        /// its business.
        /// </summary>
        /// <remarks>
        /// disco#info, disco#items and the slot request - and nothing else gets
        /// a result. An unknown request to a service that exists is
        /// <c>&lt;service-unavailable/&gt;</c>, which is a different answer from
        /// the one an unknown <em>address</em> gets, and the difference is worth
        /// keeping: one says "not here", the other "not something I do".
        /// </remarks>
        public String? Answer(String   Frame,
                              String?  Id,
                              String?  Type,
                              String?  From)
        {

            if (Type is not ("get" or "set"))
                return null;

            if (Frame.Contains(DiscoManager.InfoNamespace, StringComparison.Ordinal))
                return DiscoInfoResult(Id);

            if (Frame.Contains(DiscoManager.ItemsNamespace, StringComparison.Ordinal))
                return $"<iq type='result' id='{Id}' from='{Address}'>" +
                       $"<query xmlns='{DiscoManager.ItemsNamespace}'/></iq>";

            if (Frame.Contains(HttpFileUpload.Namespace, StringComparison.Ordinal))
                return SlotResult(Frame, Id, From);

            return $"<iq type='error' id='{Id}' from='{Address}'><error type='cancel'>" +
                    "<service-unavailable xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/></error></iq>";

        }

        #endregion

        #region (private) DiscoInfoResult(Id)

        /// <summary>
        /// XEP-0363, section 4: what the service says it is.
        /// </summary>
        /// <remarks>
        /// The limit travels in a XEP-0128 form, and the form is the only place
        /// a client can learn it <em>before</em> reading a file off disk. The
        /// identity is <c>store/file</c>, which is what a client walking the
        /// item list looks at when it wants to know what it has found.
        /// </remarks>
        private String DiscoInfoResult(String? Id)

            => $"<iq type='result' id='{Id}' from='{Address}'>" +
               $"<query xmlns='{DiscoManager.InfoNamespace}'>" +
                "<identity category='store' type='file' name='HTTP File Upload'/>" +
               $"<feature var='{HttpFileUpload.Namespace}'/>" +
               $"<feature var='{DiscoManager.InfoNamespace}'/>" +
               $"<feature var='{DiscoManager.ItemsNamespace}'/>" +
                "<x xmlns='jabber:x:data' type='result'>" +
                "<field var='FORM_TYPE' type='hidden'>" +
               $"<value>{HttpFileUpload.Namespace}</value></field>" +
               $"<field var='{HttpFileUpload.MaxFileSizeField}'>" +
               $"<value>{MaxFileSize.ToString(CultureInfo.InvariantCulture)}</value></field>" +
                "</x></query></iq>";

        #endregion

        #region (private) SlotResult(Frame, Id, From)

        /// <summary>
        /// XEP-0363, section 5: hands out a slot, or says why not.
        /// </summary>
        private String SlotResult(String Frame, String? Id, String? From)
        {

            var request = System.Xml.Linq.XElement.Parse(Frame)
                                                  .Child(HttpFileUpload.Namespace, "request");

            var filename     = request?.Attr("filename");
            var contentType  = request?.Attr("content-type");

            if (!Int64.TryParse(request?.Attr("size"), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out var size) ||
                size < 0)
            {
                return Error(Id, "modify", "bad-request", "The size has to be a number of bytes.");
            }

            // A file name travels into the URL, so a path separator in it is a
            // way of writing somewhere else. Refused rather than sanitised: a
            // name that had to be changed is not the name that was asked for,
            // and the sender should know.
            if (String.IsNullOrEmpty(filename) ||
                filename.Contains('/') || filename.Contains('\\'))
            {
                return Error(Id, "modify", "bad-request", "That is not a usable file name.");
            }

            if (size > MaxFileSize)
                return $"<iq type='error' id='{Id}' from='{Address}'><error type='modify'>" +
                        "<not-acceptable xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
                        "<text xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'>File too large</text>" +
                       $"<file-too-large xmlns='{HttpFileUpload.Namespace}'>" +
                       $"<max-file-size>{MaxFileSize.ToString(CultureInfo.InvariantCulture)}</max-file-size>" +
                        "</file-too-large></error></iq>";

            // 32 bytes from the cryptographic generator, base64url. It is the
            // whole of the credential - there is nothing else between a stranger
            // and this slot - so it is drawn the way a key is drawn and not the
            // way an identifier is.
            var slot = new Slot {
                           Id           = Base64Url(RandomNumberGenerator.GetBytes(32)),
                           Uploader     = From ?? "",
                           Filename     = filename,
                           Size         = size,
                           ContentType  = contentType,
                           ExpiresAt    = Timestamp.Now + SlotLifetime
                       };

            _slots[slot.Id] = slot;

            var url = $"{BaseUrl}{Path}/{slot.Id}/{Uri.EscapeDataString(filename)}";

            return $"<iq type='result' id='{Id}' from='{Address}'>" +
                   $"<slot xmlns='{HttpFileUpload.Namespace}'>" +
                   $"<put url='{XmlEscaping.Escape(url)}'/>" +
                   $"<get url='{XmlEscaping.Escape(url)}'/>" +
                    "</slot></iq>";

        }

        private String Error(String? Id, String Type, String Condition, String Text)

            => $"<iq type='error' id='{Id}' from='{Address}'><error type='{Type}'>" +
               $"<{Condition} xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'/>" +
               $"<text xmlns='urn:ietf:params:xml:ns:xmpp-stanzas'>{XmlEscaping.Escape(Text)}</text>" +
                "</error></iq>";

        private static String Base64Url(Byte[] Bytes)
            => Convert.ToBase64String(Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        #endregion


        #region HandlePut(Request)

        /// <summary>
        /// Takes a file, if there is a slot for it.
        /// </summary>
        /// <remarks>
        /// <b>No account is asked for and none would help.</b> What authorises
        /// this request is that its address was handed out over an authenticated
        /// stream a few minutes ago, for a file of exactly this size, and has
        /// not been used. Every one of those four is checked here, and each of
        /// them is a way in if it is not.
        /// </remarks>
        public HTTPResponse HandlePut(HTTPRequest Request)
        {

            if (!TryFindSlot(Request, out var slot))
                return Refuse(Request, HTTPStatusCode.Forbidden,
                              "No such upload slot.");

            if (slot!.ExpiresAt < Timestamp.Now)
            {
                _slots.TryRemove(slot.Id, out _);
                return Refuse(Request, HTTPStatusCode.Forbidden,
                              "That upload slot has expired.");
            }

            // A slot is for one file. Letting a second one in would let whoever
            // saw the address replace a file after somebody had been sent it -
            // and the address is deliberately shareable.
            if (slot.Content is not null)
                return Refuse(Request, HTTPStatusCode.Conflict,
                              "That upload slot has been used.");

            var body = Request.HTTPBody ?? [];

            // The size in the slot request was a promise. Without this the
            // limit the service announces binds nobody: a slot for one byte
            // would take a gigabyte.
            if (body.LongLength != slot.Size)
                return Refuse(Request, HTTPStatusCode.BadRequest,
                              $"This slot was issued for {slot.Size} bytes.");

            slot.Content = body;

            return new HTTPResponse.Builder(Request) {
                       HTTPStatusCode  = HTTPStatusCode.Created,
                       Connection      = ConnectionType.Close
                   }.AsImmutable;

        }

        #endregion

        #region HandleGet(Request)

        /// <summary>
        /// Hands a file back to anybody who knows where it is.
        /// </summary>
        /// <remarks>
        /// <b>Anonymous on purpose</b> - see the remarks on the class. What is
        /// not casual is how it is served: the type the uploader claimed, with
        /// <c>nosniff</c> so a browser does not go looking for a better idea, a
        /// content policy that forbids the page everything, and a download
        /// disposition for anything outside the handful of types that are safe
        /// to show. An upload service without those serves an attacker's HTML
        /// from its own origin.
        /// </remarks>
        public HTTPResponse HandleGet(HTTPRequest Request)
        {

            if (!TryFindSlot(Request, out var slot) || slot!.Content is null)
                return Refuse(Request, HTTPStatusCode.NotFound,
                              "There is nothing here.");

            var contentType  = slot.ContentType ?? "application/octet-stream";
            var inline       = SafePrefixes.Any(prefix => contentType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                               contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);

            var response = new HTTPResponse.Builder(Request) {
                               HTTPStatusCode  = HTTPStatusCode.OK,
                               ContentType     = HTTPContentType.TryParse(contentType, out var parsed)
                                                     ? parsed
                                                     : HTTPContentType.Application.OCTETSTREAM,
                               Content         = slot.Content,
                               Connection      = ConnectionType.Close
                           };

            response.Set("X-Content-Type-Options",   "nosniff");
            response.Set("Content-Security-Policy",  "default-src 'none'; sandbox");
            response.Set("Content-Disposition",      inline
                                                         ? "inline"
                                                         : $"attachment; filename=\"{slot.Filename}\"");

            return response.AsImmutable;

        }

        #endregion

        #region (private) TryFindSlot(Request, out Slot)

        /// <summary>
        /// The slot named in the path, if it is one that was handed out.
        /// </summary>
        /// <remarks>
        /// The path is <c>/upload/&lt;slot&gt;/&lt;filename&gt;</c>. The file
        /// name is not looked at: the slot already says what the file is called,
        /// and a name that came in off the wire is the one thing here that
        /// somebody else wrote.
        /// </remarks>
        private Boolean TryFindSlot(HTTPRequest Request, out Slot? Found)
        {

            Found = null;

            var parts = Request.Path.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2)
                return false;

            return _slots.TryGetValue(parts[1], out Found);

        }

        private static HTTPResponse Refuse(HTTPRequest     Request,
                                           HTTPStatusCode  Status,
                                           String          Why)

            => new HTTPResponse.Builder(Request) {
                   HTTPStatusCode  = Status,
                   ContentType     = HTTPContentType.Text.PLAIN,
                   Content         = Why.ToUTF8Bytes(),
                   Connection      = ConnectionType.Close
               }.AsImmutable;

        #endregion

    }

}
