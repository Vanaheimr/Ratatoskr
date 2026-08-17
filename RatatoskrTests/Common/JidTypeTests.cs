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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// What the type adds over the three strings it holds.
    /// </summary>
    /// <remarks>
    /// The splitting and preparation themselves are covered by
    /// <c>JidFormatTests</c> and <c>JidClassMembershipTests</c> and are not
    /// repeated here. What is tested here is the part that only exists because
    /// this is a type: that equality, hashing and ordering follow RFC 7622
    /// rather than following <see cref="String"/>, and that the default value
    /// of a struct - which no constructor can prevent - behaves.
    /// </remarks>
    [TestFixture]
    public class JidTypeTests
    {

        #region TheSpellingOfAnAccount_DoesNotMatter()

        /// <summary>
        /// Local and domain part are compared without regard to spelling.
        /// </summary>
        [Test]
        public void TheSpellingOfAnAccount_DoesNotMatter()
        {

            var lower = JID.Parse("alice@example.com");
            var upper = JID.Parse("ALICE@Example.COM");

            Assert.Multiple(() =>
            {

                Assert.That(lower == upper,                  Is.True);
                Assert.That(lower.GetHashCode(),             Is.EqualTo(upper.GetHashCode()),
                            "Equal addresses that hash differently would be two entries in every dictionary.");

                // The point of the type, stated as the test it exists for: on
                // the strings this comparison was false.
                Assert.That("alice@example.com" == "ALICE@Example.COM", Is.False,
                            "Which is exactly why these are no longer strings.");

            });

        }

        #endregion

        #region TheSpellingOfADevice_Does()

        /// <summary>
        /// The resourcepart is compared with regard to spelling - RFC 7622,
        /// section 3.4.
        /// </summary>
        /// <remarks>
        /// The asymmetry is the whole reason for the type. Before it, the
        /// comparison ran through <c>OrdinalIgnoreCase</c> over the whole
        /// string in most places, and so these two counted as the same address:
        /// two different devices of one account, and a message could end up on
        /// the wrong one.
        /// </remarks>
        [Test]
        public void TheSpellingOfADevice_Does()
        {

            var phone = JID.Parse("alice@example.com/Phone");
            var other = JID.Parse("alice@example.com/phone");

            Assert.Multiple(() =>
            {
                Assert.That(phone == other,                     Is.False);
                Assert.That(phone.Bare == other.Bare,           Is.True,
                            "Same account, though.");
                Assert.That(phone.CompareTo(other),             Is.Not.Zero,
                            "An ordering that called them equal would let a sort drop one of them.");
            });

        }

        #endregion

        #region ADictionaryKeyedByJid_FindsTheOtherSpelling()

        /// <summary>
        /// The consequence the rest of the library lives on: a JID may be used
        /// as a key without normalising it first.
        /// </summary>
        /// <remarks>
        /// This is where the old <c>StringComparer.OrdinalIgnoreCase</c>
        /// dictionaries were both wrong and right at once - right about the
        /// account, wrong about the device - and every one of them had to be
        /// remembered separately.
        /// </remarks>
        [Test]
        public void ADictionaryKeyedByJid_FindsTheOtherSpelling()
        {

            var contacts = new Dictionary<JID, String> {
                                { JID.Parse("Alice@Example.COM"), "the account" },
                                { JID.Parse("bob@example.com/Phone"), "one device" }
                            };

            Assert.Multiple(() =>
            {

                Assert.That(contacts[JID.Parse("alice@example.com")],       Is.EqualTo("the account"));
                Assert.That(contacts[JID.Parse("bob@example.com/Phone")],   Is.EqualTo("one device"));

                Assert.That(contacts.ContainsKey(JID.Parse("bob@example.com/phone")), Is.False,
                            "Another device, and not this entry.");

            });

        }

        #endregion

        #region TheDefault_NamesNothing()

        /// <summary>
        /// <c>default(JID)</c> cannot be forbidden, so it has to behave.
        /// </summary>
        /// <remarks>
        /// A struct has no way to insist on its constructor: an array of them,
        /// an uninitialised field, a <c>default</c> in a switch arm all produce
        /// one whose domainpart is null. What must not happen is that the first
        /// thing touching it throws from somewhere three layers down.
        /// </remarks>
        [Test]
        public void TheDefault_NamesNothing()
        {

            var nothing = default(JID);

            Assert.Multiple(() =>
            {

                Assert.That(nothing.IsNullOrEmpty,     Is.True);
                Assert.That(nothing.IsNotNullOrEmpty,  Is.False);
                Assert.That(nothing.ToString(),        Is.Empty);
                Assert.That(nothing == default(JID),   Is.True);
                Assert.That(nothing == JID.Parse("example.com"), Is.False);

                Assert.DoesNotThrow(() => nothing.GetHashCode());
                Assert.DoesNotThrow(() => { var _ = nothing.Bare; });

            });

        }

        #endregion

        #region TheParts_AreKeptApart()

        /// <summary>
        /// Bare, domain-only and full addresses, and what each of them says
        /// about itself.
        /// </summary>
        [Test]
        public void TheParts_AreKeptApart()
        {

            var full    = JID.Parse("alice@example.com/phone");
            var bare    = JID.Parse("alice@example.com");
            var domain  = JID.Parse("example.com");

            Assert.Multiple(() =>
            {

                Assert.That(full.Localpart,      Is.EqualTo("alice"));
                Assert.That(full.Domainpart,     Is.EqualTo("example.com"));
                Assert.That(full.Resourcepart,   Is.EqualTo("phone"));
                Assert.That(full.IsFull,         Is.True);
                Assert.That(full.IsBare,         Is.False);
                Assert.That(full.Bare,           Is.EqualTo(bare));
                Assert.That(full.Domain,         Is.EqualTo(domain));

                Assert.That(bare.IsBare,         Is.True);
                Assert.That(bare.IsDomainOnly,   Is.False);

                // A bare domain is a JID: 'example.com' addresses the server
                // itself, which is where every disco query goes.
                Assert.That(domain.Localpart,    Is.Null);
                Assert.That(domain.IsDomainOnly, Is.True);
                Assert.That(domain.ToString(),   Is.EqualTo("example.com"));

            });

        }

        #endregion

        #region TheOrdering_GroupsByDomainThenAccount()

        /// <summary>
        /// Sorting puts a domain's accounts together, and an account's devices
        /// together - which is what a person reading the list expects.
        /// </summary>
        [Test]
        public void TheOrdering_GroupsByDomainThenAccount()
        {

            var sorted = new[] {
                             JID.Parse("bob@example.com"),
                             JID.Parse("alice@zzz.example"),
                             JID.Parse("alice@example.com/phone"),
                             JID.Parse("alice@example.com")
                         }.
                         Order().
                         Select(jid => jid.ToString()).
                         ToArray();

            Assert.That(sorted, Is.EqualTo(new[] {
                                    "alice@example.com",
                                    "alice@example.com/phone",
                                    "bob@example.com",
                                    "alice@zzz.example"
                                }));

        }

        #endregion

        #region TryParse_RefusesWhatIsNotAnAddress()

        /// <summary>
        /// The strict route, for anything that has to be an address.
        /// </summary>
        [Test]
        public void TryParse_RefusesWhatIsNotAnAddress()
        {

            Assert.Multiple(() =>
            {

                Assert.That(JID.TryParse("juliet@",    out _), Is.False, "No domainpart.");
                Assert.That(JID.TryParse("/foobar",    out _), Is.False, "No domainpart.");
                Assert.That(JID.TryParse("",           out _), Is.False);
                Assert.That(JID.TryParse(null,         out _), Is.False);

                Assert.That(JID.TryParse("example.com"),       Is.Not.Null);
                Assert.That(JID.TryParse("juliet@"),           Is.Null);

                Assert.Throws<JidFormatException>(() => JID.Parse("juliet@"));

            });

        }

        #endregion

        #region BareTextOf_DoesNotThrowOnRubbish()

        /// <summary>
        /// The forgiving route, for whatever comes off the wire.
        /// </summary>
        /// <remarks>
        /// A stanza whose sender this side cannot parse must match nothing and
        /// go no further - it must not throw in the middle of stanza handling,
        /// which is why this path exists beside <see cref="JID.TryParse(String?)"/>
        /// at all.
        /// </remarks>
        [Test]
        public void BareTextOf_DoesNotThrowOnRubbish()
        {

            Assert.Multiple(() =>
            {

                Assert.That(JID.BareTextOf("Alice@Example.COM/Phone"), Is.EqualTo("alice@example.com"));

                // Not a JID at all - cut at the first slash, lowercased, and no
                // exception.
                Assert.That(JID.BareTextOf("juliet@/x"),               Is.EqualTo("juliet@"));
                Assert.That(JID.BareTextOf("nonsense"),                Is.EqualTo("nonsense"));

            });

        }

        #endregion

        #region ThePreparedForm_IsWhatIsKept()

        /// <summary>
        /// What went in is not what is stored: the parts are prepared per
        /// RFC 7622, and the address compares by what it means.
        /// </summary>
        [Test]
        public void ThePreparedForm_IsWhatIsKept()
        {

            var typed = JID.Parse("ALICE@EXAMPLE.COM/Phone");

            Assert.Multiple(() =>
            {

                Assert.That(typed.Localpart,     Is.EqualTo("alice"));
                Assert.That(typed.Domainpart,    Is.EqualTo("example.com"));

                // Not lowercased - a resourcepart is an OpaqueString.
                Assert.That(typed.Resourcepart,  Is.EqualTo("Phone"));

                Assert.That(typed.ToString(),    Is.EqualTo("alice@example.com/Phone"));

            });

        }

        #endregion

    }

}
