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
/// XEP-0359: the name a message keeps.
/// </summary>
/// <remarks>
/// The <c>id</c> of a stanza is the sender's, and only the sender's. Nothing
/// stops two people picking the same one, a room does not have to pass it on,
/// and an archive numbers what it stores itself. So whoever wants to point at a
/// message - a correction, a reaction, a reply - needs to know which of the
/// names on it the other side will recognise.
///
/// <list type="bullet">
///   <item><c>&lt;origin-id/&gt;</c> is the sender saying: this is my name for
///         it, whatever happens to the <c>id</c> on the way.</item>
///   <item><c>&lt;stanza-id/&gt;</c> is a server or a room saying: in my
///         archive it is called this. There can be several, one per archive,
///         which is why <c>by</c> is not decoration - a stanza-id without the
///         question <em>whose</em> is a number from somewhere.</item>
/// </list>
///
/// <b>Read here, not written.</b> This client assigns no stable id of its own:
/// what would go into an <c>&lt;origin-id/&gt;</c> is exactly the <c>id</c> it
/// just wrote, and a second copy of a number is not a second piece of
/// knowledge. Announcing <c>urn:xmpp:sid:0</c> would say more than that -
/// section 3 has it for entities that assign them - so it is not announced.
/// Reading them is not optional though: whoever ignores an
/// <c>&lt;origin-id/&gt;</c> from a client that sets one answers with the wrong
/// name as soon as anything on the way renames the stanza.
/// </remarks>
public static class StableIds
{

    /// <summary>
    /// The namespace of XEP-0359.
    /// </summary>
    public const string Namespace = "urn:xmpp:sid:0";

    /// <summary>
    /// The name the sender gave this message, or null.
    /// </summary>
    /// <remarks>
    /// Only direct children, as everywhere: a forwarded message carries its own
    /// and that one is not this one's. An empty id counts as none - it is not a
    /// name.
    /// </remarks>
    public static string? OriginId(XElement message)
    {

        var id = message.Child(Namespace, "origin-id")?.Attr("id");

        return string.IsNullOrEmpty(id) ? null : id;

    }

    /// <summary>
    /// The name the given archive gave this message, or null.
    /// </summary>
    /// <param name="by">Whose archive is being asked about - a bare JID.</param>
    /// <remarks>
    /// <b>The <c>by</c> has to match</b>, and taking the first stanza-id in the
    /// stanza instead would not be a shortcut but a different answer: a message
    /// out of a room carries the room's and the account archive's, and the two
    /// are different numbers for the same message. Which one is wanted depends
    /// on who is going to look it up.
    /// </remarks>
    public static string? StanzaId(XElement message, JID by)
    {

        var bare = by.Bare.ToString();

        foreach (var element in message.Children(Namespace, "stanza-id"))
        {

            if (element.Attr("by") != bare)
                continue;

            var id = element.Attr("id");

            if (!string.IsNullOrEmpty(id))
                return id;

        }

        return null;

    }

}
