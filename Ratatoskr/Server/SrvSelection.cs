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

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    /// <summary>
    /// The order in which SRV targets are tried (RFC 2782).
    /// </summary>
    /// <remarks>
    /// The part one easily gets wrong and never notices. Working through the
    /// priorities in order is obvious; the weighting within a priority is not.
    /// It is <b>not</b> a sorting by weight but a weighted draw without
    /// replacement: from the remaining targets one is drawn with a probability
    /// proportional to its weight, then the next one from the rest. Whoever
    /// sorts descending instead sends all traffic to the strongest machine -
    /// and the load distribution the weights exist for never takes place. That
    /// would only show up in operation, and even there only to somebody who
    /// looks at the utilisation.
    ///
    /// The source of randomness can be substituted, so that the procedure stays
    /// checkable.
    /// </remarks>
    public static class SrvSelection
    {

        #region Data

        /// <summary>
        /// A target "." explicitly means, per RFC 2782: this service is
        /// <b>not</b> offered for this domain.
        /// </summary>
        public const String NoService = ".";

        #endregion

        #region Order(targets, pick = null)

        /// <summary>
        /// Brings the targets into the order in which they are to be tried.
        /// </summary>
        /// <param name="targets">The unsorted SRV targets.</param>
        /// <param name="pick">
        /// Delivers a random number in <c>[0, max]</c> <b>inclusive</b>. Null
        /// takes a real source of randomness.
        /// </param>
        /// <returns>
        /// The targets in the order of the attempt. Empty when the domain
        /// explicitly does not offer the service.
        /// </returns>
        public static IReadOnlyList<SrvTarget> Order(IEnumerable<SrvTarget>  targets,
                                                     Func<Int32, Int32>?     pick   = null)
        {

            var all = targets.ToList();

            // RFC 2782: a single "." ends the search. Heeding other entries
            // beside it would be wrong - the domain has said that the service
            // does not exist.
            if (all.Any(t => t.Host == NoService))
                return [];

            pick ??= max => Random.Shared.Next(max + 1);

            var result = new List<SrvTarget>(all.Count);

            foreach (var group in all.GroupBy(t => t.Priority).OrderBy(g => g.Key))
            {

                // "all those with weight 0 are placed at the beginning of the
                // list" - that is how it stands in the RFC, and it is the reason
                // why a weightless target is ever drawn at all.
                var rest = group.OrderBy(t => t.Weight == 0 ? 0 : 1).ToList();

                while (rest.Count > 0)
                {

                    var total = rest.Sum(t => (Int32) t.Weight);
                    var roll  = pick(total);

                    var running = 0;
                    var chosen  = rest.Count - 1;

                    for (var i = 0; i < rest.Count; i++)
                    {

                        running += rest[i].Weight;

                        // "select the RR whose running sum value is the first
                        // value greater than or equal to the random number"
                        if (running >= roll)
                        {
                            chosen = i;
                            break;
                        }

                    }

                    result.Add(rest[chosen]);
                    rest.RemoveAt(chosen);

                }

            }

            return result;

        }

        #endregion

    }

}
