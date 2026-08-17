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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// The order of the SRV targets according to RFC 2782.
    /// </summary>
    /// <remarks>
    /// The source of randomness is given here, so that a checkable sequence
    /// comes out of a probabilistic procedure. That is more than convenience:
    /// a wrong weighting does not stand out at all with real randomness,
    /// because every result looks plausible somehow.
    /// </remarks>
    [TestFixture]
    public class SrvSelectionTests
    {

        #region Helper functions

        private static SrvTarget Target(UInt16 priority, UInt16 weight, String host)
            => new (priority, weight, host, 5269);

        /// <summary>
        /// A source of randomness delivering fixed values in turn.
        /// </summary>
        private static Func<Int32, Int32> Rolls(params Int32[] values)
        {

            var i = 0;

            return max =>
            {
                var value = i < values.Length ? values[i] : 0;
                i++;
                return Math.Min(value, max);
            };

        }

        private static String[] Names(IEnumerable<SrvTarget> targets)
            => [.. targets.Select(t => t.Host)];

        #endregion


        #region LowerPriorityGoesFirst()

        /// <summary>
        /// The priority beats everything - a high weight as well.
        /// </summary>
        [Test]
        public void LowerPriorityGoesFirst()
        {

            var ordered = SrvSelection.Order(
                              [Target(20, 100, "later.example"),
                               Target(10,   1, "first.example")],
                               Rolls(0, 0));

            Assert.That(Names(ordered), Is.EqualTo(new[] { "first.example", "later.example" }));

        }

        #endregion

        #region AllOfOnePriorityBeforeTheNext()

        /// <summary>
        /// Only when one priority level is exhausted does the next one come.
        /// </summary>
        [Test]
        public void AllOfOnePriorityBeforeTheNext()
        {

            var ordered = SrvSelection.Order(
                              [Target(20, 10, "c.example"),
                               Target(10, 10, "a.example"),
                                Target(20, 10, "d.example"),
                                Target(10, 10, "b.example")],
                               Rolls(0, 0, 0, 0));

            var names = Names(ordered);

            Assert.Multiple(() =>
            {
                Assert.That(names[..2], Is.EquivalentTo(new[] { "a.example", "b.example" }));
                Assert.That(names[2..], Is.EquivalentTo(new[] { "c.example", "d.example" }));
            });

        }

        #endregion

        #region TheWeightDecidesWithinAPriority()

        /// <summary>
        /// Within one priority the weight decides - over a weighted draw, not
        /// over a sorting.
        /// </summary>
        /// <remarks>
        /// The rolls are chosen so that they hit the weaker target first. Were
        /// the selection a descending sort by weight, the stronger one would
        /// always come first regardless of the roll - and that is precisely the
        /// error this test catches.
        /// </remarks>
        [Test]
        public void TheWeightDecidesWithinAPriority()
        {

            // Order in the remainder: heavy.example (90), light.example (10);
            // running sums 90 and 100. A roll of 100 hits the second one.
            var ordered = SrvSelection.Order(
                              [Target(10, 90, "heavy.example"),
                               Target(10, 10, "light.example")],
                               Rolls(100, 0));

            Assert.That(Names(ordered), Is.EqualTo(new[] { "light.example", "heavy.example" }));

        }

        #endregion

        #region ALowRollPicksTheFirstRunningSum()

        /// <summary>
        /// The counter-check: a small roll hits the first target of the running
        /// sum.
        /// </summary>
        [Test]
        public void ALowRollPicksTheFirstRunningSum()
        {

            var ordered = SrvSelection.Order(
                              [Target(10, 90, "heavy.example"),
                               Target(10, 10, "light.example")],
                               Rolls(1, 0));

            Assert.That(Names(ordered)[0], Is.EqualTo("heavy.example"));

        }

        #endregion

        #region WeightZeroIsNotExcluded()

        /// <summary>
        /// A weight of zero does not mean "never".
        /// </summary>
        /// <remarks>
        /// RFC 2782 puts weightless targets at the beginning of the list, with
        /// which a roll of 0 hits precisely them. Whoever sorts them to the end
        /// instead or leaves them out entirely makes a dead target out of a
        /// reserve one.
        /// </remarks>
        [Test]
        public void WeightZeroIsNotExcluded()
        {

            var ordered = SrvSelection.Order(
                              [Target(10, 100, "main.example"),
                               Target(10,   0, "reserve.example")],
                               Rolls(0, 0));

            Assert.Multiple(() =>
            {
                Assert.That(Names(ordered)[0], Is.EqualTo("reserve.example"));
                Assert.That(ordered,           Has.Count.EqualTo(2));
            });

        }

        #endregion

        #region EveryTargetAppearsExactlyOnce()

        /// <summary>
        /// The drawing happens without putting back - no target twice, none
        /// lost.
        /// </summary>
        [Test]
        public void EveryTargetAppearsExactlyOnce()
        {

            var targets = new[] {
                            Target(10, 5, "a.example"),
                            Target(10, 5, "b.example"),
                            Target(10, 5, "c.example"),
                            Target(20, 1, "d.example")
                        };

            var ordered = SrvSelection.Order(targets);

            Assert.That(Names(ordered), Is.EquivalentTo(Names(targets)));

        }

        #endregion

        #region ADotMeansTheServiceIsNotOffered()

        /// <summary>
        /// A target of "." means: this domain does not offer the service
        /// (RFC 2782).
        /// </summary>
        /// <remarks>
        /// That is a statement and not a missing entry. To pass it over and
        /// fall back on the default port would mean reading an express refusal
        /// as silence.
        /// </remarks>
        [Test]
        public void ADotMeansTheServiceIsNotOffered()
        {

            var ordered = SrvSelection.Order([new SrvTarget(0, 0, ".", 0)]);

            Assert.That(ordered, Is.Empty);

        }

        #endregion

        #region ADotAmongOthers_StillMeansNo()

        /// <summary>
        /// Next to other entries as well "." stays a refusal.
        /// </summary>
        [Test]
        public void ADotAmongOthers_StillMeansNo()
        {

            var ordered = SrvSelection.Order(
                              [Target(10, 10, "somewhere.example"),
                               new SrvTarget(0, 0, ".", 0)]);

            Assert.That(ordered, Is.Empty);

        }

        #endregion

        #region NothingAtAll_YieldsNothing()

        [Test]
        public void NothingAtAll_YieldsNothing()
        {
            Assert.That(SrvSelection.Order([]), Is.Empty);
        }

        #endregion

        #region OverManyRuns_TheDistributionFollowsTheWeights()

        /// <summary>
        /// Over many draws with real randomness the distribution follows the
        /// weights.
        /// </summary>
        /// <remarks>
        /// The previous tests hold the sequence fast, this one the effect. The
        /// bounds are generous - the test is meant to catch a swapped or
        /// ignored weighting, not to judge the quality of the source of
        /// randomness.
        /// </remarks>
        [Test]
        public void OverManyRuns_TheDistributionFollowsTheWeights()
        {

            var targets = new[] {
                            Target(10, 90, "heavy.example"),
                            Target(10, 10, "light.example")
                        };

            var heavyFirst = 0;

            for (var i = 0; i < 2000; i++)
                if (SrvSelection.Order(targets)[0].Host == "heavy.example")
                    heavyFirst++;

            Assert.That(heavyFirst, Is.InRange(1500, 1950),
                        "Around 90 percent should fall to the heavy target.");

        }

        #endregion

    }

}
