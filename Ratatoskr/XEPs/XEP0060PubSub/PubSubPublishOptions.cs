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
/// The conditions of a publication (XEP-0060, section 7.1.5).
/// </summary>
/// <param name="AccessModel">Which access model the node has to have.</param>
/// <param name="MaxItems">How many entries it has to keep.</param>
/// <param name="PersistItems">Whether it has to store.</param>
/// <remarks>
/// <b>Something other than a setting: a condition.</b> That is why every field
/// here is <c>null</c>-able, and <c>null</c> does not mean "default" but "this
/// is not asked about". Whoever mistakes a condition for a setting sets a whole
/// lot of fields when publishing that nobody wanted to name.
///
/// The point stands in XEP-0384, section 5.2: an OMEMO bundle has to be openly
/// fetchable, otherwise nobody who stands in no roster yet can write in
/// encrypted form. The client cannot know that without querying the node
/// beforehand - so it says it <i>with</i> the publication, and the service
/// either creates it accordingly or refuses.
/// </remarks>
public sealed record PubSubPublishOptions(PubSubAccessModel?  AccessModel   = null,
                                          Int32?              MaxItems      = null,
                                          Boolean?            PersistItems  = null)
{

    /// <summary>The form type of these conditions.</summary>
    public const String FormType = "http://jabber.org/protocol/pubsub#publish-options";

    /// <summary>
    /// Reads a submitted condition form - strictly, like every instruction.
    /// </summary>
    /// <returns>
    /// false when it is none, has the wrong purpose or contains a field about
    /// which this service can promise nothing. <b>Leniency would be wrong
    /// precisely here:</b> a condition that is passed over is one the sender
    /// takes for fulfilled.
    /// </returns>
    public static Boolean TryRead(XElement x, out PubSubPublishOptions? options)
    {

        options = null;

        if (!DataForm.Is(x, "submit"))
            return false;

        PubSubAccessModel?  access   = null;
        Int32?              count    = null;
        Boolean?            persist  = null;

        foreach (var field in DataForm.Fields(x))
        {

            var value = DataForm.ValueOf(field);

            switch (field.Attr("var"))
            {

                case "FORM_TYPE":
                    if (value != FormType)
                        return false;
                    break;

                case PubSubNodeConfiguration.AccessModelVariable:
                    if (!PubSubNodeConfiguration.TryReadAccessModel(value, out var demanded))
                        return false;
                    access = demanded;
                    break;

                case PubSubNodeConfiguration.MaxItemsVariable:
                    if (!Int32.TryParse(value, out var read) || read < 1)
                        return false;
                    count = read;
                    break;

                case PubSubNodeConfiguration.PersistItemsVariable:
                    if (!DataForm.TryBoolean(value, out var persisting))
                        return false;
                    persist = persisting;
                    break;

                default:
                    return false;

            }

        }

        options = new PubSubPublishOptions(access, count, persist);

        return true;

    }

    /// <summary>
    /// Does this node meet the conditions?
    /// </summary>
    public Boolean AreMetBy(PubSubNodeConfiguration configuration)

        => (AccessModel  is null || AccessModel  == configuration.AccessModel)  &&
           (MaxItems     is null || MaxItems     == configuration.MaxItems)     &&
           (PersistItems is null || PersistItems == configuration.PersistItems);

    /// <summary>
    /// The setting a new node is to be created with: the default, overwritten
    /// by what was demanded.
    /// </summary>
    public PubSubNodeConfiguration ApplyTo(PubSubNodeConfiguration configuration)

        => new(AccessModel  ?? configuration.AccessModel,
               MaxItems     ?? configuration.MaxItems,
               PersistItems ?? configuration.PersistItems);

}
