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

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// The names of the roles on the wire (XEP-0060, section 12.16).
/// </summary>
public static class PubSubAffiliations
{

    /// <summary>The role as it is called in the protocol.</summary>
    public static String NameOf(PubSubAffiliation affiliation)
        => affiliation switch {
               PubSubAffiliation.Owner      => "owner",
               PubSubAffiliation.Publisher  => "publisher",
               PubSubAffiliation.Member     => "member",
               PubSubAffiliation.Outcast    => "outcast",
               _                            => "none"
           };

    /// <summary>
    /// Reads a role.
    /// </summary>
    /// <returns>
    /// false for everything this service does not know - including
    /// <c>publish-only</c>. <b>To read an unknown role as "none" would be
    /// especially expensive here:</b> whoever wants to shut somebody out and
    /// mistypes would otherwise get a <c>result</c> and take the exclusion for
    /// carried out.
    /// </returns>
    public static Boolean TryRead(String? name, out PubSubAffiliation affiliation)
    {

        switch (name)
        {

            case "owner":      affiliation = PubSubAffiliation.Owner;      return true;
            case "publisher":  affiliation = PubSubAffiliation.Publisher;  return true;
            case "member":     affiliation = PubSubAffiliation.Member;     return true;
            case "outcast":    affiliation = PubSubAffiliation.Outcast;    return true;
            case "none":       affiliation = PubSubAffiliation.None;       return true;

            default:           affiliation = PubSubAffiliation.None;       return false;

        }

    }

}
