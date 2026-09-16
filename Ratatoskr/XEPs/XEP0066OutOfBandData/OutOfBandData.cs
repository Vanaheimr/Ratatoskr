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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// XEP-0066: this message is about a file that lies somewhere else.
/// </summary>
/// <remarks>
/// <b>The half of XEP-0363 that reaches a person.</b> An upload that nobody is
/// told about is a file on a disk; what turns it into something shared is an
/// ordinary message carrying the address. XEP-0363, section 5 says how: the URL
/// in the body <em>and</em> in an <c>&lt;x xmlns='jabber:x:oob'/&gt;</c>.
///
/// Why both, when the body already holds it. The body is for whoever has no
/// idea what this extension is - every client in existence shows a link. The
/// <c>&lt;x/&gt;</c> is what turns the same message into a picture rather than
/// a line of text, and it is the only part a client can act on without guessing
/// from the shape of the text whether the whole body was meant as an address.
///
/// <b>The body is not decoration and must match.</b> A message whose
/// <c>&lt;x/&gt;</c> points somewhere other than its body is a link that shows
/// one destination and leads to another, which is the oldest trick there is.
/// <see cref="UrlIn"/> therefore gives nothing back when the two disagree.
/// </remarks>
public static class OutOfBandData
{

    /// <summary>
    /// The namespace of XEP-0066 in a message.
    /// </summary>
    public const String Namespace = "jabber:x:oob";


    #region Xml(Url, Description = null)

    /// <summary>
    /// The <c>&lt;x/&gt;</c> that goes beside the body.
    /// </summary>
    public static XElement Xml(Uri Url, String? Description = null)
    {

        var x = new XElement(XName.Get("x", Namespace),
                    new XElement(XName.Get("url", Namespace), Url.AbsoluteUri));

        if (!String.IsNullOrEmpty(Description))
            x.Add(new XElement(XName.Get("desc", Namespace), Description));

        return x;

    }

    #endregion

    #region UrlIn(Message, Body)

    /// <summary>
    /// The address this message is about, or null when it is about none.
    /// </summary>
    /// <param name="Message">The message stanza.</param>
    /// <param name="Body">
    /// Its body, as the rest of the client read it. Passed in rather than read
    /// again so that both halves are judging the same text - a body that was
    /// stripped of a fallback (XEP-0428) is no longer the body in the XML.
    /// </param>
    /// <remarks>
    /// <b>Only a direct child counts</b> (see <c>StanzaExtensions.Child</c>): a
    /// forwarded or archived message brings its own <c>&lt;x/&gt;</c> along, and
    /// that one is about the message inside, not about this one.
    ///
    /// And only when the body says the same thing. Section 5 of XEP-0363 has
    /// them agree, a client that shows the body and follows the <c>&lt;x/&gt;</c>
    /// would otherwise show one address and open another, and there is no
    /// reading of a disagreement that is safe to guess at.
    /// </remarks>
    public static Uri? UrlIn(XElement Message, String? Body)
    {

        var url = Message.Child(Namespace, "x")
                        ?.Child(Namespace, "url")
                        ?.Value
                        ?.Trim();

        if (String.IsNullOrEmpty(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return null;
        }

        return Body is not null && Body.Trim() == url
                   ? parsed
                   : null;

    }

    #endregion

}
