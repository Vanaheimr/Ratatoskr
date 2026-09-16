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
using System.Security.Cryptography;
using System.Xml.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;


#region (record) AvatarInfo

/// <summary>
/// XEP-0084: what somebody says their picture is, without the picture.
/// </summary>
/// <param name="Id">
/// The SHA-1 of the image data, lower-case hex. <b>It is the item id as well</b>,
/// which is what makes an avatar cacheable: a client that already has this id
/// needs to fetch nothing.
/// </param>
/// <param name="Bytes">How large the image is.</param>
/// <param name="Type">Its media type, as claimed.</param>
/// <param name="Width">Pixels across, when given.</param>
/// <param name="Height">Pixels down, when given.</param>
/// <param name="Url">
/// Where it can be fetched instead of out of PEP, when the publisher offered
/// that. <b>Not followed here</b> - see the remarks on <see cref="UserAvatar"/>.
/// </param>
public sealed record AvatarInfo(String   Id,
                                Int64    Bytes,
                                String   Type,
                                Int32?   Width   = null,
                                Int32?   Height  = null,
                                Uri?     Url     = null)
{

    public override String ToString()
        => $"{Type}, {Bytes} bytes" +
           (Width.HasValue && Height.HasValue ? $", {Width}x{Height}" : "") +
           $" ({Id[..Math.Min(8, Id.Length)]}…)";

}

#endregion

#region (record) Avatar

/// <summary>
/// XEP-0084: the picture and what was said about it.
/// </summary>
public sealed record Avatar(AvatarInfo Info, Byte[] Data);

#endregion


/// <summary>
/// XEP-0084 over XEP-0163: somebody's picture, published to whoever is
/// subscribed.
/// </summary>
/// <remarks>
/// Two nodes and not one, and the split is the whole design. The
/// <b>metadata</b> node carries a few bytes saying what the picture is and is
/// what gets pushed to everybody subscribed; the <b>data</b> node carries the
/// picture and is only ever fetched, by id, by whoever does not have it yet.
/// A person with three hundred contacts and a new photograph therefore sends
/// three hundred short notices and not three hundred photographs.
///
/// <b>The id is the SHA-1 of the bytes, and checking it is not optional
/// here.</b> Not because it makes the picture trustworthy - the publisher
/// controls both nodes and can publish whatever they like, consistently - but
/// because the id is what everything downstream caches by. A client that files
/// bytes under an id they do not hash to will show the wrong person's face for
/// every later avatar that really has that id, and will go on doing it after
/// the mistake has been fixed everywhere else.
///
/// <b>What is deliberately not here: decoding.</b> This hands over bytes and a
/// claimed type. An avatar is a picture from whoever is in the roster, and
/// image parsers are where such pictures have historically been turned into
/// something else; which decoder sees them, in what process, with what limits,
/// is an application's decision. For the same reason the <c>url</c> of the
/// info element is read and never followed: a library that fetches an address
/// a contact put in a stanza has handed that contact a request from this
/// machine.
/// </remarks>
public static class UserAvatar
{

    #region Data

    /// <summary>
    /// The node the picture lives in.
    /// </summary>
    public const String DataNode      = "urn:xmpp:avatar:data";

    /// <summary>
    /// The node that says what the picture is - the one that is pushed.
    /// </summary>
    public const String MetadataNode  = "urn:xmpp:avatar:metadata";

    /// <summary>
    /// The largest image this will read out of a PEP item.
    /// </summary>
    /// <remarks>
    /// <b>Our number, not the specification's.</b> XEP-0084 recommends a small
    /// picture and says nothing binding about size; what this guards against is
    /// that the item comes from a contact and arrives base64-encoded in a
    /// stanza, so a client without a limit lets anybody in its roster decide
    /// how much it allocates. 256 KiB is far more than an avatar and far less
    /// than a problem.
    /// </remarks>
    public const Int32 MaxImageBytes  = 256 * 1024;

    #endregion


    #region IdOf(Image)

    /// <summary>
    /// The id an image has to be published under: SHA-1, lower-case hex.
    /// </summary>
    /// <remarks>
    /// SHA-1 because XEP-0084 says SHA-1, and it is not a signature: what it
    /// does here is name a picture so that two clients holding the same bytes
    /// agree on what to call them. A stronger hash would name it better and
    /// nobody else would recognise the name.
    /// </remarks>
    public static String IdOf(ReadOnlySpan<Byte> Image)

#pragma warning disable SCS0006 // Weak hashing function - the id is a name, not a signature.
        => Convert.ToHexString(SHA1.HashData(Image)).ToLowerInvariant();
#pragma warning restore SCS0006

    #endregion

    #region Describe(Image, Type, Width = null, Height = null)

    /// <summary>
    /// What to publish about an image, with its id worked out.
    /// </summary>
    public static AvatarInfo Describe(Byte[]  Image,
                                      String  Type,
                                      Int32?  Width   = null,
                                      Int32?  Height  = null)

        => new (IdOf(Image), Image.LongLength, Type, Width, Height);

    #endregion

    #region Matches(Info, Image)

    /// <summary>
    /// Do these bytes hash to the id they were fetched under?
    /// </summary>
    /// <remarks>
    /// Checked on the way in, always. See the remarks on the class for what it
    /// buys and what it does not.
    /// </remarks>
    public static Boolean Matches(AvatarInfo Info, ReadOnlySpan<Byte> Image)

        => String.Equals(Info.Id, IdOf(Image), StringComparison.OrdinalIgnoreCase);

    #endregion


    #region DataItem(Image)

    /// <summary>
    /// The item for the data node.
    /// </summary>
    public static XElement DataItem(Byte[] Image)

        => new (XName.Get("data", DataNode),
                Convert.ToBase64String(Image));

    #endregion

    #region MetadataItem(Info)

    /// <summary>
    /// The item for the metadata node - what everybody subscribed is told.
    /// </summary>
    public static XElement MetadataItem(AvatarInfo Info)
    {

        var info = new XElement(XName.Get("info", MetadataNode),
                       new XAttribute("id",    Info.Id),
                       new XAttribute("bytes", Info.Bytes.ToString(CultureInfo.InvariantCulture)),
                       new XAttribute("type",  Info.Type)
                   );

        if (Info.Width  is Int32 width)
            info.Add(new XAttribute("width",  width. ToString(CultureInfo.InvariantCulture)));

        if (Info.Height is Int32 height)
            info.Add(new XAttribute("height", height.ToString(CultureInfo.InvariantCulture)));

        if (Info.Url is not null)
            info.Add(new XAttribute("url", Info.Url.AbsoluteUri));

        return new XElement(XName.Get("metadata", MetadataNode), info);

    }

    #endregion

    #region NoAvatarItem()

    /// <summary>
    /// XEP-0084, section 4: an empty <c>&lt;metadata/&gt;</c> - there is no
    /// picture any more.
    /// </summary>
    /// <remarks>
    /// <b>Publishing nothing is not the same as publishing an absence.</b> A
    /// node that is simply left alone goes on announcing the old picture to
    /// everybody who subscribes later; this is how somebody takes their face
    /// down.
    /// </remarks>
    public static XElement NoAvatarItem()

        => new (XName.Get("metadata", MetadataNode));

    #endregion


    #region InfosIn(Metadata)

    /// <summary>
    /// What a metadata item says, or an empty list when it says there is
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <b>Several <c>&lt;info/&gt;</c> elements are allowed</b> and mean the
    /// same picture in several forms - a PEP copy and an HTTP one, say. They
    /// come back in the order they were written; a caller picks. Returning only
    /// the first would quietly drop the one a caller could use in favour of one
    /// it cannot.
    ///
    /// An entry without an id, a length or a type is left out rather than
    /// guessed at: the id is what the data is fetched by, and there is nothing
    /// to do with one that has none.
    /// </remarks>
    public static IReadOnlyList<AvatarInfo> InfosIn(XElement Metadata)
    {

        var found = new List<AvatarInfo>();

        foreach (var info in Metadata.Children(MetadataNode, "info"))
        {

            var id    = info.Attr("id");
            var type  = info.Attr("type");

            if (String.IsNullOrEmpty(id) ||
                String.IsNullOrEmpty(type) ||
                !Int64.TryParse(info.Attr("bytes"), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out var bytes) ||
                bytes < 0)
            {
                continue;
            }

            found.Add(new AvatarInfo(
                          id.ToLowerInvariant(),
                          bytes,
                          type,
                          Int32.TryParse(info.Attr("width"),  NumberStyles.Integer,
                                         CultureInfo.InvariantCulture, out var w) ? w : null,
                          Int32.TryParse(info.Attr("height"), NumberStyles.Integer,
                                         CultureInfo.InvariantCulture, out var h) ? h : null,
                          Uri.TryCreate(info.Attr("url"), UriKind.Absolute, out var url) ? url : null
                      ));

        }

        return found;

    }

    #endregion

    #region IsNoAvatar(Metadata)

    /// <summary>
    /// Does this metadata item say the picture is gone?
    /// </summary>
    /// <remarks>
    /// Told apart from "could not be read" on purpose. One means take the face
    /// down, the other means leave what is there alone - and a client that
    /// confuses them either keeps showing a picture somebody removed or blanks
    /// one because a stanza was malformed.
    /// </remarks>
    public static Boolean IsNoAvatar(XElement Metadata)

        => Metadata.Name.LocalName == "metadata" &&
           Metadata.Name.NamespaceName == MetadataNode &&
           !Metadata.Children(MetadataNode, "info").Any();

    #endregion

    #region DataIn(Item, Expected)

    /// <summary>
    /// The picture out of a data item - and null when it is not the one that
    /// was asked for.
    /// </summary>
    /// <param name="Item">The <c>&lt;data/&gt;</c> element.</param>
    /// <param name="Expected">
    /// What was announced. The bytes are checked against its id and its length.
    /// </param>
    /// <remarks>
    /// Three ways to come back empty, and they are one from the caller's point
    /// of view - there is no picture - so they are one here too. What differs is
    /// the log line, which is the only place the difference helps.
    /// </remarks>
    public static Byte[]? DataIn(XElement Item, AvatarInfo Expected)
    {

        if (Item.Name.LocalName     != "data" ||
            Item.Name.NamespaceName != DataNode)
        {
            return null;
        }

        // Before decoding, not after: base64 of a quarter of a megabyte is a
        // third again as long, and the string is already in memory by the time
        // this is reached - but the decoded copy need not be.
        if (Expected.Bytes > MaxImageBytes)
            return null;

        Byte[] image;

        try
        {
            image = Convert.FromBase64String(Item.Value);
        }
        catch (FormatException)
        {
            return null;
        }

        return image.LongLength == Expected.Bytes && Matches(Expected, image)
                   ? image
                   : null;

    }

    #endregion

}
