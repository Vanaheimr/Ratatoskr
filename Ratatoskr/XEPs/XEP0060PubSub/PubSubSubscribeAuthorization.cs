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
/// XEP-0060, section 8.6: The application for a subscription, as it is
/// presented to the owner and answered by them.
/// </summary>
/// <param name="NodeId">The node that is being asked for.</param>
/// <param name="SubscriberJid">Who asks.</param>
/// <param name="SubId">
/// The identifier of the subscription applied for. <b>It is the actual subject
/// of the answer</b> - the same JID can apply several times, and without it the
/// service would not know which application was decided.
/// </param>
/// <param name="Allow">The answer: grant or decline.</param>
/// <remarks>
/// <b>The second door to the same decision, and therefore no second
/// decision.</b> An application can also be approved by way of the subscriber
/// list (section 8.8.2), and the server of this project does the same thing
/// internally both times. Two doors are necessary all the same: the list is the
/// view of an administrator, the form that of a human being whose client shows
/// them a question. Whoever had only the list would demand of every client that
/// it can manage subscribers.
///
/// <b>A form nobody can answer would be worse than none.</b> That is why the
/// reading stands here beside the writing: whoever asks the question has to
/// accept the answer - otherwise a human being approves something and nothing
/// happens.
/// </remarks>
public sealed record PubSubSubscribeAuthorization(String   NodeId,
                                                  String   SubscriberJid,
                                                  String?  SubId,
                                                  Boolean  Allow = false)
{

    /// <summary>
    /// The form type of this application.
    /// </summary>
    public const String FormType = "http://jabber.org/protocol/pubsub#subscribe_authorization";

    /// <summary>
    /// The field for the node.
    /// </summary>
    public const String NodeVariable = "pubsub#node";

    /// <summary>
    /// The field for the identifier of the application.
    /// </summary>
    public const String SubIdVariable = "pubsub#subid";

    /// <summary>
    /// The field for the applicant.
    /// </summary>
    public const String SubscriberVariable = "pubsub#subscriber_jid";

    /// <summary>
    /// The field for the answer.
    /// </summary>
    public const String AllowVariable = "pubsub#allow";

    /// <summary>
    /// The question to the owner (<c>type='form'</c>).
    /// </summary>
    /// <remarks>
    /// The preset of <c>pubsub#allow</c> is <c>false</c>. A form that already
    /// stands on "yes" turns clicking it away into a grant.
    /// </remarks>
    public XElement ToForm()
        => DataForm.Form("form", FormType,
               DataForm.Field(NodeVariable,       "text-single", "Node",        NodeId),
               DataForm.Field(SubIdVariable,      "text-single", "Identifier",  SubId ?? ""),
               DataForm.Field(SubscriberVariable, "jid-single",  "Applicant",   SubscriberJid),
               DataForm.Field(AllowVariable,      "boolean",     "Grant?",      DataForm.Boolean(Allow)));

    /// <summary>
    /// The answer of the owner (<c>type='submit'</c>).
    /// </summary>
    public XElement ToSubmit()
        => DataForm.Form("submit", FormType,
               DataForm.Field(NodeVariable,       null, null, NodeId),
               DataForm.Field(SubIdVariable,      null, null, SubId ?? ""),
               DataForm.Field(SubscriberVariable, null, null, SubscriberJid),
               DataForm.Field(AllowVariable,      null, null, DataForm.Boolean(Allow)));

    /// <summary>
    /// Reads a submitted answer - strictly, like every instruction.
    /// </summary>
    /// <returns>
    /// false when it is no submitted form of this purpose, a field is missing
    /// or carries a value that is none.
    /// </returns>
    /// <remarks>
    /// <b>Without node, applicant and answer it is no answer.</b> The
    /// identifier may be missing - an applicant with only one pending
    /// application is unambiguous without it too, and a client that loses it
    /// shall not have to answer with an invented one.
    /// </remarks>
    public static Boolean TryRead(XElement x, out PubSubSubscribeAuthorization? authorization)
        => TryRead(x, "submit", allowRequired: true, out authorization);

    /// <summary>
    /// Reads the presented application (<c>type='form'</c>).
    /// </summary>
    /// <remarks>
    /// <b>Without <c>pubsub#allow</c>, and that is the difference.</b> In the
    /// presented form the field is the question; in the submitted answer it is
    /// the answer. An application without a preset is therefore complete, an
    /// answer without a decision is not.
    /// </remarks>
    public static Boolean TryReadRequest(XElement x, out PubSubSubscribeAuthorization? request)
        => TryRead(x, "form", allowRequired: false, out request);

    private static Boolean TryRead(XElement                          x,
                                   String                            kind,
                                   Boolean                           allowRequired,
                                   out PubSubSubscribeAuthorization?  authorization)
    {

        authorization = null;

        if (!DataForm.Is(x, kind))
            return false;

        String?   node       = null;
        String?   who        = null;
        String?   subId      = null;
        Boolean?  allow      = null;
        var       rightKind  = false;

        foreach (var field in DataForm.Fields(x))
        {

            var value = DataForm.ValueOf(field);

            switch (field.Attr("var"))
            {

                case "FORM_TYPE":
                    if (value != FormType)
                        return false;
                    rightKind = true;
                    break;

                case NodeVariable:        node    = value;  break;
                case SubscriberVariable:  who     = value;  break;

                // An empty field is no identifier: the applicant has one, or
                // they have none - an empty string would be a third possibility
                // that does not exist.
                case SubIdVariable:
                    subId = String.IsNullOrEmpty(value) ? null : value;
                    break;

                case AllowVariable:
                    if (!DataForm.TryBoolean(value, out var allowed))
                        return false;
                    allow = allowed;
                    break;

            }

        }

        if (!rightKind ||
            String.IsNullOrEmpty(node) ||
            String.IsNullOrEmpty(who)  ||
            (allowRequired && allow is null))
        {
            return false;
        }

        authorization = new PubSubSubscribeAuthorization(node, who, subId, allow ?? false);

        return true;

    }

}
