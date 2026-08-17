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
/// The settings of a node (XEP-0060, section 8.2).
/// </summary>
/// <param name="AccessModel">Who gets at the entries.</param>
/// <param name="MaxItems">
/// How many entries the node keeps; once the limit is reached, the oldest gives
/// way.
/// </param>
/// <param name="PersistItems">
/// Are entries kept at all? A node without storage only reports - whoever was
/// not listening has missed it.
/// </param>
/// <param name="RosterGroups">
/// The roster groups that come in with the access model
/// <see cref="PubSubAccessModel.Roster"/> - empty means: the whole roster.
/// </param>
/// <remarks>
/// <b>Four fields, and every one of them does something.</b> XEP-0060 knows two
/// dozen more - title, language, notifications about configuration changes,
/// collection queries, publish models. What is offered here is only what also
/// takes effect; anything else would be a promise without cover, and at the very
/// place where an owner believes they have settled something.
///
/// The group list stands in the form even when another model holds. That is no
/// oversight: it is a setting of the node and not of the model - whoever
/// switches from <c>open</c> to <c>roster</c> shall be able to set the list
/// beforehand instead of letting the node stand open for a moment.
/// </remarks>
public sealed record PubSubNodeConfiguration(PubSubAccessModel       AccessModel   = PubSubAccessModel.Open,
                                             Int32                   MaxItems      = 256,
                                             Boolean                 PersistItems  = true,
                                             IReadOnlyList<String>?  RosterGroups  = null)
{

    /// <summary>
    /// The groups, never null.
    /// </summary>
    public IReadOnlyList<String> RosterGroups { get; init; } = RosterGroups ?? [];

    /// <summary>
    /// The form type of these settings.
    /// </summary>
    public const String FormType = "http://jabber.org/protocol/pubsub#node_config";

    /// <summary>
    /// The field for the access model.
    /// </summary>
    public const String AccessModelVariable = "pubsub#access_model";

    /// <summary>
    /// The field for the number of entries kept.
    /// </summary>
    public const String MaxItemsVariable = "pubsub#max_items";

    /// <summary>
    /// The field for the storage.
    /// </summary>
    public const String PersistItemsVariable = "pubsub#persist_items";

    /// <summary>
    /// The field for the permitted roster groups.
    /// </summary>
    public const String RosterGroupsVariable = "pubsub#roster_groups_allowed";

    /// <summary>
    /// The default: open, 256 entries, with storage.
    /// </summary>
    public static readonly PubSubNodeConfiguration Default = new();

    /// <summary>
    /// The access model as it stands in the form.
    /// </summary>
    public static String NameOf(PubSubAccessModel model)
        => model switch {
               PubSubAccessModel.Presence   => "presence",
               PubSubAccessModel.Whitelist  => "whitelist",
               PubSubAccessModel.Roster     => "roster",
               PubSubAccessModel.Authorize  => "authorize",
               _                            => "open"
           };

    /// <summary>
    /// Reads an access model.
    /// </summary>
    /// <returns>
    /// false for everything this server cannot enforce. Since D93 that is
    /// nothing any more - the check stays all the same: it distinguishes a name
    /// that exists from a typo.
    /// </returns>
    /// <remarks>
    /// <b>One place for all who ask about it</b>: the node form in both
    /// directions and the conditions of a publication. Four places that keep the
    /// same list keep it differently at some point - and the one that does not
    /// know a model lets it pass silently as <c>open</c>.
    /// </remarks>
    public static Boolean TryReadAccessModel(String? name, out PubSubAccessModel model)
    {

        switch (name)
        {

            case "open":       model = PubSubAccessModel.Open;       return true;
            case "presence":   model = PubSubAccessModel.Presence;   return true;
            case "whitelist":  model = PubSubAccessModel.Whitelist;  return true;
            case "roster":     model = PubSubAccessModel.Roster;     return true;
            case "authorize":  model = PubSubAccessModel.Authorize;  return true;

            default:           model = PubSubAccessModel.Open;       return false;

        }

    }

    /// <summary>
    /// The offer of the service (<c>type='form'</c>) - what can be set and what
    /// holds just now.
    /// </summary>
    public XElement ToForm()
        => DataForm.Form("form", FormType,
               DataForm.Field     (AccessModelVariable,  "list-single", "Who gets at the entries", NameOf(AccessModel)),
               DataForm.Field     (MaxItemsVariable,     "text-single", "Entries kept",            MaxItems.ToString()),
               DataForm.Field     (PersistItemsVariable, "boolean",     "Keep entries",            DataForm.Boolean(PersistItems)),
               DataForm.MultiField(RosterGroupsVariable, "list-multi",  "Permitted roster groups", RosterGroups));

    /// <summary>
    /// The answer of the owner (<c>type='submit'</c>).
    /// </summary>
    public XElement ToSubmit()
        => DataForm.Form("submit", FormType,
               DataForm.Field     (AccessModelVariable,  null, null, NameOf(AccessModel)),
               DataForm.Field     (MaxItemsVariable,     null, null, MaxItems.ToString()),
               DataForm.Field     (PersistItemsVariable, null, null, DataForm.Boolean(PersistItems)),
               DataForm.MultiField(RosterGroupsVariable, null, null, RosterGroups));

    /// <summary>
    /// Reads a submitted form - strictly, like every instruction.
    /// </summary>
    /// <param name="current">
    /// The state missing fields refer to. XEP-0060, section 8.2.4 permits
    /// partial forms; what does not stand there stays as it was.
    /// </param>
    /// <returns>
    /// false when it is no submitted form, has the wrong purpose, contains an
    /// unknown field or a value that is none.
    /// </returns>
    public static Boolean TryRead(XElement                  x,
                                  PubSubNodeConfiguration   current,
                                  out PubSubNodeConfiguration?  configuration)
    {

        configuration = null;

        if (!DataForm.Is(x, "submit"))
            return false;

        var access  = current.AccessModel;
        var count   = current.MaxItems;
        var persist = current.PersistItems;
        var groups  = current.RosterGroups;

        foreach (var field in DataForm.Fields(x))
        {

            var value = DataForm.ValueOf(field);

            switch (field.Attr("var"))
            {

                case "FORM_TYPE":
                    if (value != FormType)
                        return false;
                    break;

                case AccessModelVariable:
                    // authorize and roster do not stand in the offer. To accept
                    // them and stay open would be the most dangerous politeness
                    // of this server.
                    if (!TryReadAccessModel(value, out access))
                        return false;
                    break;

                case MaxItemsVariable:
                    if (!Int32.TryParse(value, out count) || count < 1)
                        return false;
                    break;

                case PersistItemsVariable:
                    if (!DataForm.TryBoolean(value, out persist))
                        return false;
                    break;

                // All values and not only the first: a field of which half is
                // read gives the owner back a list they never sent that way.
                case RosterGroupsVariable:
                    groups = DataForm.ValuesOf(field);
                    break;

                default:
                    return false;

            }

        }

        configuration = new PubSubNodeConfiguration(access, count, persist, groups);

        return true;

    }

    /// <summary>
    /// Reads the offer of a service (<c>type='form'</c>) - leniently, like
    /// every piece of information.
    /// </summary>
    /// <remarks>
    /// Unknown fields are passed over: a foreign service offers two dozen, of
    /// which this client understands three. An offer that names none of them is
    /// no offer all the same - then there is nothing to read.
    /// </remarks>
    public static Boolean TryReadForm(XElement x, out PubSubNodeConfiguration? configuration)
    {

        configuration = null;

        if (!DataForm.Is(x, "form"))
            return false;

        var found    = false;
        var access   = PubSubAccessModel.Open;
        var count    = Default.MaxItems;
        var persist  = Default.PersistItems;
        var groups   = Default.RosterGroups;

        foreach (var field in DataForm.Fields(x))
        {

            var value = DataForm.ValueOf(field);

            switch (field.Attr("var"))
            {

                case "FORM_TYPE":
                    if (value != FormType)
                        return false;
                    break;

                case AccessModelVariable:
                    // A foreign model is read as it is: a client that shortened
                    // 'authorize' to 'open' would show the human being the
                    // opposite of what holds. There is no value for that here -
                    // so the offer is not to be read.
                    if (!TryReadAccessModel(value, out access))
                        return false;
                    found = true;
                    break;

                case MaxItemsVariable:
                    if (!Int32.TryParse(value, out count))
                        return false;
                    found = true;
                    break;

                case PersistItemsVariable:
                    if (!DataForm.TryBoolean(value, out persist))
                        return false;
                    found = true;
                    break;

                // An empty multi-field is a read field: "no group named" is the
                // information and not its absence.
                case RosterGroupsVariable:
                    groups = DataForm.ValuesOf(field);
                    found  = true;
                    break;

            }

        }

        if (!found)
            return false;

        configuration = new PubSubNodeConfiguration(access, count, persist, groups);

        return true;

    }

}
