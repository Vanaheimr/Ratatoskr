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
/// The settings of a single subscription (XEP-0060, section 6.3).
/// </summary>
/// <param name="Deliver">
/// Are notifications delivered? <c>pubsub#deliver</c>, section 12.18.
/// </param>
/// <remarks>
/// <b>One field, and that is the statement.</b> XEP-0060 knows a dozen more -
/// digests, expiry dates, depth, presence filters. What this server cannot do
/// it does not offer either: a form with <c>pubsub#digest</c> in it that then
/// brings about nothing would be a promise without cover, and one the
/// subscriber cannot check at that - a digest that does not come looks like
/// quiet.
///
/// <b>Only with this do two subscriptions differ.</b> Until then two on the
/// same node were two identical things, and the second brought in nothing but a
/// second delivery. Now the <c>subid</c> is not only an identifier but the
/// address of a setting.
/// </remarks>
public sealed record PubSubSubscriptionOptions(Boolean Deliver = true)
{

    /// <summary>The namespace of the data forms (XEP-0004).</summary>
    public const String DataFormNamespace = "jabber:x:data";

    /// <summary>The form type of these settings.</summary>
    public const String FormType = "http://jabber.org/protocol/pubsub#subscribe_options";

    /// <summary>The field for the delivery.</summary>
    public const String DeliverVariable = "pubsub#deliver";

    /// <summary>
    /// The offer of the service (<c>type='form'</c>) - what can be set and what
    /// holds just now.
    /// </summary>
    public XElement ToForm()
    {

        XNamespace ns = DataFormNamespace;

        return new XElement(ns + "x",
                   new XAttribute("type", "form"),
                   new XElement(ns + "field",
                       new XAttribute("var",  "FORM_TYPE"),
                       new XAttribute("type", "hidden"),
                       new XElement(ns + "value", FormType)),
                   new XElement(ns + "field",
                       new XAttribute("var",   DeliverVariable),
                       new XAttribute("type",  "boolean"),
                       new XAttribute("label", "Deliver notifications"),
                       new XElement(ns + "value", Deliver ? "1" : "0")));

    }

    /// <summary>
    /// The answer of the subscriber (<c>type='submit'</c>).
    /// </summary>
    public XElement ToSubmit()
    {

        XNamespace ns = DataFormNamespace;

        return new XElement(ns + "x",
                   new XAttribute("type", "submit"),
                   new XElement(ns + "field",
                       new XAttribute("var",  "FORM_TYPE"),
                       new XAttribute("type", "hidden"),
                       new XElement(ns + "value", FormType)),
                   new XElement(ns + "field",
                       new XAttribute("var", DeliverVariable),
                       new XElement(ns + "value", Deliver ? "1" : "0")));

    }

    /// <summary>
    /// Reads a submitted form.
    /// </summary>
    /// <returns>
    /// false when it is none, has the wrong purpose or contains a field nobody
    /// offered here.
    /// </returns>
    /// <remarks>
    /// <b>Unknown fields are refused and not passed over.</b> That is stricter
    /// than XEP-0004 demands, and deliberate: whoever silently swallows the
    /// unknown leaves the sender in the belief that their setting holds. A
    /// refusal can be read, an effect that does not come cannot.
    ///
    /// A missing field, by contrast, is no error: the submitted form is the
    /// complete setting, and what does not stand there stands on the default.
    /// </remarks>
    public static Boolean TryRead(XElement x, out PubSubSubscriptionOptions? options)
    {

        options = null;

        if (x.Name.NamespaceName != DataFormNamespace ||
            x.Name.LocalName     != "x" ||
            x.Attr("type")       != "submit")
        {
            return false;
        }

        var deliver = true;

        foreach (var field in x.Children(DataFormNamespace, "field"))
        {

            var value = field.Child(DataFormNamespace, "value")?.Value;

            switch (field.Attr("var"))
            {

                case "FORM_TYPE":
                    if (value != FormType)
                        return false;
                    break;

                case DeliverVariable:
                    if (!TryReadBoolean(value, out deliver))
                        return false;
                    break;

                default:
                    return false;

            }

        }

        options = new PubSubSubscriptionOptions(deliver);

        return true;

    }

    /// <summary>
    /// Reads the offer of a service (<c>type='form'</c>).
    /// </summary>
    /// <remarks>
    /// <b>Here unknown fields are passed over, in <see cref="TryRead"/>
    /// refused</b> - and that is no contradiction but the direction:
    ///
    /// An offer is a piece of information. A foreign service offers a dozen
    /// fields of which this client can set only one; whoever founders on that
    /// cannot speak with any real service. A submitted form, by contrast, is an
    /// instruction, and a field passed over in it is a discarded instruction the
    /// sender learns nothing of.
    /// </remarks>
    public static Boolean TryReadForm(XElement x, out PubSubSubscriptionOptions? options)
    {

        options = null;

        if (x.Name.NamespaceName != DataFormNamespace ||
            x.Name.LocalName     != "x" ||
            x.Attr("type")       != "form")
        {
            return false;
        }

        foreach (var field in x.Children(DataFormNamespace, "field"))
        {

            var value = field.Child(DataFormNamespace, "value")?.Value;

            if (field.Attr("var") == "FORM_TYPE" && value != FormType)
                return false;

            if (field.Attr("var") == DeliverVariable)
            {

                if (!TryReadBoolean(value, out var deliver))
                    return false;

                options = new PubSubSubscriptionOptions(deliver);

            }

        }

        // Without the field there is nothing to read: an offer that does not
        // name the delivery says nothing about it either - and to assume the
        // default would mean inventing it.
        return options is not null;

    }

    /// <summary>
    /// XEP-0004, section 3.3: a truth value stands as 0/1 or false/true.
    /// </summary>
    /// <remarks>
    /// To read both spellings and to write only one is no contradiction but the
    /// usual caution: what comes in was written by somebody else.
    /// </remarks>
    private static Boolean TryReadBoolean(String? value, out Boolean result)
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

}
