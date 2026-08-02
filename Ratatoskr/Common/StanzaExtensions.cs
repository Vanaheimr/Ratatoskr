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
/// Access to the parts of a stanza through the XML parser instead of through
/// text patterns.
///
/// The search deliberately goes by local name only, without checking the
/// namespace: servers bind <c>jabber:client</c> sometimes as the default
/// namespace, sometimes through a prefix, and some leave it out of the child
/// elements entirely. For the parts of a stanza - <c>from</c>, <c>body</c>,
/// <c>show</c> and so on - the local name is unambiguous enough.
/// </summary>
public static class StanzaExtensions
{

    /// <summary>
    /// The value of an attribute, independent of any prefix.
    /// </summary>
    public static string? Attr(this XElement element, string name)
        => element.Attributes()
                  .FirstOrDefault(attribute => attribute.Name.LocalName == name)
                  ?.Value;

    /// <summary>
    /// The first <b>direct</b> child element with this name.
    ///
    /// That only direct children count is the actual point: a message forwarded
    /// per XEP-0297 brings its own <c>&lt;body/&gt;</c> along, and that one must
    /// not displace the outer stanza's.
    /// </summary>
    public static XElement? Child(this XElement element, string name)
        => element.Elements()
                  .FirstOrDefault(child => child.Name.LocalName == name);

    /// <summary>
    /// The first direct child element with this name from this namespace.
    ///
    /// The right choice for payloads: which extension is meant is only said by
    /// the namespace. There is a <c>&lt;query/&gt;</c> in the roster, in
    /// disco#info and in disco#items, and a <c>&lt;received/&gt;</c> in XEP-0184
    /// and XEP-0333.
    /// </summary>
    public static XElement? Child(this XElement element, string namespaceName, string name)
        => element.Elements()
                  .FirstOrDefault(child => child.Name.NamespaceName == namespaceName &&
                                           child.Name.LocalName     == name);

    /// <summary>
    /// All direct child elements with this name from this namespace.
    /// </summary>
    public static IEnumerable<XElement> Children(this XElement element, string namespaceName, string name)
        => element.Elements()
                  .Where(child => child.Name.NamespaceName == namespaceName &&
                                  child.Name.LocalName     == name);

    /// <summary>
    /// The text content of the first direct child element with this name, with
    /// entities resolved. Null if there is no such element.
    /// </summary>
    public static string? ChildValue(this XElement element, string name)
        => element.Child(name)?.Value;

    /// <summary>
    /// Does the stanza carry an element from this namespace anywhere?
    /// </summary>
    public static bool HasNamespace(this XElement element, string namespaceName)
        => element.DescendantsAndSelf()
                  .Any(child => child.Name.NamespaceName == namespaceName);

}
