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

using System.Globalization;
using System.Xml.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;


#region (record) UploadService

/// <summary>
/// XEP-0363: the service that hands out upload slots.
/// </summary>
/// <param name="Address">Where to ask.</param>
/// <param name="MaxFileSize">
/// The largest file it will take, out of the disco form - or null when it
/// announced none.
/// </param>
/// <remarks>
/// <b>A null limit is not "no limit".</b> It says the service did not say, and
/// the only way left to find out is to ask for a slot and read the refusal. A
/// client that reads null as unlimited offers to send a film and learns
/// otherwise after the file has been read from disk.
/// </remarks>
public sealed record UploadService(JID Address, Int64? MaxFileSize)
{

    /// <summary>
    /// Would a file of this size be refused by what the service announced?
    /// </summary>
    /// <remarks>
    /// False when nothing was announced - not because it fits, but because
    /// nothing here knows. <see cref="MaxFileSize"/> says which of the two it
    /// is.
    /// </remarks>
    public Boolean IsTooLarge(Int64 Size)
        => MaxFileSize.HasValue && Size > MaxFileSize.Value;

    public override String ToString()
        => MaxFileSize.HasValue
               ? $"{Address} (up to {MaxFileSize.Value} bytes)"
               : $"{Address} (no announced limit)";

}

#endregion

#region (record) UploadHeader

/// <summary>
/// XEP-0363, section 5: a header the service demands for the PUT.
/// </summary>
public sealed record UploadHeader(String Name, String Value);

#endregion

#region (record) UploadSlot

/// <summary>
/// XEP-0363: permission to put one file at one address, once.
/// </summary>
/// <param name="PutUrl">Where the file goes.</param>
/// <param name="GetUrl">Where it can be fetched afterwards - the shareable one.</param>
/// <param name="Headers">
/// What has to travel with the PUT, already filtered down to what a client is
/// allowed to send.
/// </param>
/// <remarks>
/// <b>The slot is the authorisation, and it is not the account.</b> Whoever may
/// ask is decided on the XMPP stream, which is authenticated; what comes back
/// is a capability - an address nobody can guess, good for one file of one
/// announced size, and usually only for the next few minutes. The HTTP side has
/// no idea who anybody is, and is not supposed to have one: the person who
/// later fetches the file may well be on another server and have no account
/// here at all.
///
/// Which is why the two halves have opposite rules. The PUT is authorised or it
/// is an open drop box for the whole internet; the GET is anonymous because
/// there is nobody to ask.
/// </remarks>
public sealed record UploadSlot(Uri                          PutUrl,
                                Uri                          GetUrl,
                                IReadOnlyList<UploadHeader>  Headers);

#endregion

#region (record) FileSent

/// <summary>
/// XEP-0363 with XEP-0066: how sending a file ended.
/// </summary>
/// <param name="Upload">What the upload did.</param>
/// <param name="MessageId">
/// The message that named the address - null when there was nothing to name.
/// </param>
/// <remarks>
/// Both halves, because both can fail separately and a caller wanting to say
/// what went wrong needs to know which. A file that is up and unannounced sits
/// on a server nobody will ever ask for it.
/// </remarks>
public sealed record FileSent(UploadOutcome Upload, String? MessageId)
{

    /// <summary>Is the file up and has somebody been told?</summary>
    public Boolean Sent  => MessageId is not null;

    /// <summary>Where it can be fetched, when it got there.</summary>
    public Uri?    Url   => Upload.Url;

}

#endregion


/// <summary>
/// XEP-0363: asking for somewhere to put a file.
/// </summary>
/// <remarks>
/// The one extension in this library whose interesting half does not speak XMPP
/// at all. The slot is asked for over the stream, the bytes go over HTTPS, and
/// only both together are an upload - a client that gets the IQ right and the
/// PUT wrong has sent nothing.
///
/// It is also the only way to send anything that is not text. Everything else
/// here - a correction, a reply, a receipt - travels inside a stanza; a
/// photograph cannot, and XEP-0363 is the answer XMPP settled on.
/// </remarks>
public static class HttpFileUpload
{

    #region Data

    /// <summary>
    /// The namespace of XEP-0363.
    /// </summary>
    public const String Namespace     = "urn:xmpp:http:upload:0";

    /// <summary>
    /// The field the disco form carries the limit in (section 4).
    /// </summary>
    public const String MaxFileSizeField = "max-file-size";

    /// <summary>
    /// The three header names a client may pass on, and no others.
    /// </summary>
    /// <remarks>
    /// <b>An allow-list, and section 5 makes it one on purpose.</b> The service
    /// names headers and the client sends them, which is one entity dictating
    /// what another puts into an HTTP request - so the list of what can be
    /// dictated has to be closed. Anything outside it is dropped rather than
    /// passed on, even though that may mean the upload is refused: an upload
    /// that fails is a great deal better than a client that can be talked into
    /// sending arbitrary headers to an arbitrary address.
    /// </remarks>
    public static readonly IReadOnlySet<String> AllowedHeaders =
        new HashSet<String>(StringComparer.OrdinalIgnoreCase) {
            "Authorization",
            "Cookie",
            "Expires"
        };

    #endregion


    #region Request(Filename, Size, ContentType = null)

    /// <summary>
    /// XEP-0363, section 5: the request for a slot.
    /// </summary>
    /// <param name="Filename">
    /// The name the file should keep. It travels into the URL, so a service
    /// will refuse one that carries a path separator.
    /// </param>
    /// <param name="Size">How many bytes, exactly.</param>
    /// <param name="ContentType">What it is, or null to say nothing.</param>
    /// <remarks>
    /// <b>The size is a promise, not an estimate.</b> The service writes it into
    /// the slot and checks the PUT against it; a file that grew between the
    /// question and the answer is refused, and rightly - otherwise the announced
    /// limit would be decoration and a slot for one byte would take a gigabyte.
    /// </remarks>
    public static XElement Request(String   Filename,
                                   Int64    Size,
                                   String?  ContentType   = null)
    {

        var request = new XElement(XName.Get("request", Namespace),
                          new XAttribute("filename", Filename),
                          // Invariant on principle rather than because anything
                          // here would go wrong without it - measured, and the
                          // mutation for it survives. Int64.ToString(provider)
                          // is ToString("G", provider), and "G" on an integer
                          // writes no group separators in any culture: a German
                          // one gives 1048576 and not "1.048.576". What the
                          // invariant culture buys is that this stays true if
                          // the type ever stops being an integer.
                          new XAttribute("size",     Size.ToString(CultureInfo.InvariantCulture))
                      );

        if (!String.IsNullOrEmpty(ContentType))
            request.Add(new XAttribute("content-type", ContentType));

        return request;

    }

    #endregion

    #region ReadSlot(IQ)

    /// <summary>
    /// The slot out of an IQ result, or null when there is none in it.
    /// </summary>
    /// <remarks>
    /// Both URLs have to be there and both have to be absolute. A slot missing
    /// one of them is not half usable: without the PUT there is nowhere to send
    /// the file, and without the GET nobody could ever be told about it.
    ///
    /// The headers are filtered here rather than at the moment of sending, so
    /// that there is one place where the rule lives and nothing downstream has
    /// to remember it.
    /// </remarks>
    public static UploadSlot? ReadSlot(XElement IQ)
    {

        var slot = IQ.Child(Namespace, "slot");

        if (slot is null)
            return null;

        var put = slot.Child(Namespace, "put")?.Attr("url");
        var get = slot.Child(Namespace, "get")?.Attr("url");

        if (put is null || get is null ||
            !Uri.TryCreate(put, UriKind.Absolute, out var putUrl) ||
            !Uri.TryCreate(get, UriKind.Absolute, out var getUrl))
        {
            return null;
        }

        var headers = new List<UploadHeader>();

        foreach (var header in slot.Child(Namespace, "put")!.Children(Namespace, "header"))
        {

            var name   = header.Attr("name");
            var value  = header.Value;

            if (name is null || !AllowedHeaders.Contains(name))
                continue;

            // Section 5, and it is the reason the allow-list is not enough on
            // its own: a value carrying a line break ends the header and starts
            // another one of the attacker's choosing. Whoever passes this on
            // unchecked lets the service write the whole request.
            if (value.Contains('\r') || value.Contains('\n'))
                continue;

            headers.Add(new UploadHeader(name, value));

        }

        return new UploadSlot(putUrl, getUrl, headers);

    }

    #endregion

    #region MaxFileSizeIn(Info)

    /// <summary>
    /// The announced limit out of the disco form (section 4), or null.
    /// </summary>
    /// <remarks>
    /// Only out of a form that says it is this one. A service may carry several
    /// - XEP-0128 puts no limit on them - and a <c>max-file-size</c> from
    /// somebody else's form is a number about something else.
    /// </remarks>
    public static Int64? MaxFileSizeIn(DiscoInfo Info)
    {

        foreach (var form in Info.Forms)
        {

            if (form.FormType != Namespace)
                continue;

            var value = form.Fields.FirstOrDefault(f => f.Var == MaxFileSizeField)
                                  ?.Values.FirstOrDefault();

            if (value is not null &&
                Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) &&
                size >= 0)
            {
                return size;
            }

        }

        return null;

    }

    #endregion

    #region TooLargeIn(IQ)

    /// <summary>
    /// The limit out of a <c>&lt;file-too-large/&gt;</c> refusal, or null when
    /// the error is a different one.
    /// </summary>
    /// <remarks>
    /// <b>The refusal carries the answer.</b> Section 5 has the service put its
    /// real limit into the error, which means a client that never read the disco
    /// form still learns it here - and a client that did read one learns that it
    /// has changed. Throwing the error away and reporting "upload failed" throws
    /// away the one number that would let the next attempt succeed.
    /// </remarks>
    public static Int64? TooLargeIn(XElement IQ)
    {

        var text = IQ.Child("error")
                    ?.Child(Namespace, "file-too-large")
                    ?.Child(Namespace, "max-file-size")
                    ?.Value;

        return text is not null &&
               Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) &&
               size >= 0
                   ? size
                   : null;

    }

    #endregion

    #region GuessContentType(Filename)

    /// <summary>
    /// What a file with this name probably is.
    /// </summary>
    /// <remarks>
    /// <b>A short table and not a complete one.</b> The type decides whether the
    /// other side shows a picture or offers a download, and it decides something
    /// on the way as well: a service is free to refuse types it will not serve
    /// safely, and Prosody by default serves only images, video, audio and plain
    /// text inline and forces everything else to be downloaded - which is the
    /// right answer to user-supplied HTML under the server's own domain.
    ///
    /// Everything unrecognised becomes <c>application/octet-stream</c>: that is
    /// "bytes, I make no claim", and it is the honest answer. Guessing wrongly
    /// is worse than not guessing, because the type travels into the slot and
    /// the service checks the upload against it.
    /// </remarks>
    public static String GuessContentType(String Filename)

        => Path.GetExtension(Filename).ToLowerInvariant() switch {
               ".png"   => "image/png",
               ".jpg"   => "image/jpeg",
               ".jpeg"  => "image/jpeg",
               ".gif"   => "image/gif",
               ".webp"  => "image/webp",
               ".svg"   => "image/svg+xml",
               ".pdf"   => "application/pdf",
               ".txt"   => "text/plain",
               ".mp3"   => "audio/mpeg",
               ".ogg"   => "audio/ogg",
               ".opus"  => "audio/ogg",
               ".mp4"   => "video/mp4",
               ".webm"  => "video/webm",
               ".zip"   => "application/zip",
               _        => "application/octet-stream"
           };

    #endregion

    #region Announces(Info)

    /// <summary>
    /// Does this entity say it hands out upload slots?
    /// </summary>
    public static Boolean Announces(DiscoInfo Info)
        => Info.HasFeature(Namespace);

    #endregion

}
