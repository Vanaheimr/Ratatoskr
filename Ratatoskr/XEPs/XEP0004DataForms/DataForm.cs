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
/// XEP-0004: the few handles on a data form that are needed here.
/// </summary>
/// <remarks>
/// <b>No form model, only the shared places.</b> There are two forms in this
/// house - the settings of a subscription and those of a node -, and both build
/// the same fields, read the same truth value and stumble over the same
/// spellings. To write the same thing twice means changing it once and
/// forgetting it once.
///
/// What does <b>not</b> stand here is a form model with field types and
/// validation rules. There would be one to build; it is not needed, and unused
/// surface is no asset in this building.
///
/// <b>Multiple values stood in the same line until D92</b> - they were not
/// needed either. With <c>pubsub#roster_groups_allowed</c> there is the first
/// field that carries several; a <c>list-multi</c> of which only the first
/// value were read would be exactly the silent shortening this house otherwise
/// writes against.
/// </remarks>
internal static class DataForm
{

    /// <summary>
    /// The namespace of the data forms.
    /// </summary>
    public const String Namespace = "jabber:x:data";

    /// <summary>
    /// Is this a form of this kind - <c>form</c>, <c>submit</c>?
    /// </summary>
    public static Boolean Is(XElement x, String type)
        => x.Name.NamespaceName == Namespace &&
           x.Name.LocalName     == "x" &&
           x.Attr("type")       == type;

    /// <summary>
    /// The fields of a form.
    /// </summary>
    public static IEnumerable<XElement> Fields(XElement x)
        => x.Children(Namespace, "field");

    /// <summary>
    /// The first value of a field, or null.
    /// </summary>
    public static String? ValueOf(XElement field)
        => field.Child(Namespace, "value")?.Value;

    /// <summary>
    /// All values of a field - for <c>list-multi</c>, where every value is a
    /// <c>&lt;value/&gt;</c> of its own.
    /// </summary>
    /// <remarks>
    /// A field without values gives an empty list. With a multi-field that is a
    /// statement and not a gap: <b>no selection</b>.
    /// </remarks>
    public static IReadOnlyList<String> ValuesOf(XElement field)
        => [.. field.Children(Namespace, "value").Select(v => v.Value)];

    /// <summary>
    /// XEP-0004, section 3.3: a truth value stands as 0/1 or false/true.
    /// </summary>
    /// <remarks>
    /// To read both spellings and to write only one is no contradiction but the
    /// usual caution: what comes in was written by somebody else.
    /// </remarks>
    public static Boolean TryBoolean(String? value, out Boolean result)
    {

        switch (value)
        {

            case "1" or "true":
                result = true;
                return true;

            case "0" or "false":
                result = false;
                return true;

            default:
                result = true;
                return false;

        }

    }

    /// <summary>
    /// A truth value as it is written.
    /// </summary>
    public static String Boolean(Boolean value)
        => value ? "1" : "0";

    /// <summary>
    /// A field with one value.
    /// </summary>
    public static XElement Field(String var, String? type, String? label, String value)
    {

        XNamespace ns = Namespace;

        var field = new XElement(ns + "field", new XAttribute("var", var));

        if (type is not null)
            field.Add(new XAttribute("type", type));

        if (label is not null)
            field.Add(new XAttribute("label", label));

        field.Add(new XElement(ns + "value", value));

        return field;

    }

    /// <summary>
    /// A field with any number of values - with none as well.
    /// </summary>
    /// <remarks>
    /// No value here means "nothing selected" and not "field missing": the
    /// field stands in the form, it is only empty. Whoever left it out instead
    /// would say "this setting does not exist" - something else entirely.
    /// </remarks>
    public static XElement MultiField(String var, String? type, String? label, IEnumerable<String> values)
    {

        XNamespace ns = Namespace;

        var field = new XElement(ns + "field", new XAttribute("var", var));

        if (type is not null)
            field.Add(new XAttribute("type", type));

        if (label is not null)
            field.Add(new XAttribute("label", label));

        foreach (var value in values)
            field.Add(new XElement(ns + "value", value));

        return field;

    }

    /// <summary>
    /// A form with its <c>FORM_TYPE</c> and the given fields.
    /// </summary>
    public static XElement Form(String type, String formType, params XElement[] fields)
    {

        XNamespace ns = Namespace;

        return new XElement(ns + "x",
                   new XAttribute("type", type),
                   Field("FORM_TYPE", "hidden", null, formType),
                   fields);

    }

}
