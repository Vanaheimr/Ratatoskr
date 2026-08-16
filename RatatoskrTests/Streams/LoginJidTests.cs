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

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// The address a person types when they sign in.
    /// </summary>
    /// <remarks>
    /// The one boundary in this library where a JID comes from a human being -
    /// and the one place that did not use <c>JidUtilities</c>. It split at the
    /// <c>'@'</c> and took two pieces, which is right for exactly the shape
    /// people usually type and wrong for the rest.
    ///
    /// Nothing here goes near a network. The constructor decides the username,
    /// the domain and the fallback endpoint, and those are worth pinning down
    /// on their own: a mis-parsed domain shows up much later as a connection
    /// error that names nothing.
    /// </remarks>
    [TestFixture]
    public class LoginJidTests
    {

        #region Helper functions

        private static XMPPConnection Connect(String jid)
            => new(jid, "secret");

        #endregion


        #region APlainLoginJid_IsSplitTheOrdinaryWay()

        /// <summary>
        /// The shape almost everybody types, and it has to keep working.
        /// </summary>
        [Test]
        public void APlainLoginJid_IsSplitTheOrdinaryWay()
        {

            var connection = Connect("alice@example.com");

            Assert.Multiple(() =>
            {
                Assert.That(connection.BareJid, Is.EqualTo("alice@example.com"));
                Assert.That(connection.Resource, Does.StartWith("console-"),
                            "No resource was named, so the default stands.");
            });

        }

        #endregion

        #region AResourceInTheLoginJid_IsAWishAndNotADomain()

        /// <summary>
        /// The finding. <c>alice@example.com/phone</c> has one <c>@</c>, so the
        /// old split accepted it and made the domainpart
        /// <c>example.com/phone</c>.
        /// </summary>
        /// <remarks>
        /// Nothing objected at that point. The endpoint built from it read
        /// <c>wss://example.com/phone:5443/ws</c>, and what came back was a
        /// connection error naming a host nobody had typed. The address is
        /// taken apart correctly now, and the resource is used for what it says
        /// - which device this is.
        /// </remarks>
        [Test]
        public void AResourceInTheLoginJid_IsAWishAndNotADomain()
        {

            var connection = Connect("alice@example.com/phone");

            Assert.Multiple(() =>
            {
                Assert.That(connection.BareJid,  Is.EqualTo("alice@example.com"));
                Assert.That(connection.Resource, Is.EqualTo("phone"));
            });

        }

        #endregion

        #region TheSplitHappensAtTheSlashFirst()

        /// <summary>
        /// Example 15 of RFC 7622, and the reason section 3.2 prescribes the
        /// order: split at the first <c>/</c>, and only then at the first
        /// <c>@</c>.
        /// </summary>
        /// <remarks>
        /// The other way round this address gets the localpart
        /// <c>a.example.com/b</c> - and a localpart may not carry a <c>/</c> or
        /// an <c>@</c>, while a resourcepart may carry both. The old split did
        /// it the other way round and produced exactly that, without complaint.
        /// </remarks>
        [Test]
        public void TheSplitHappensAtTheSlashFirst()
        {

            // A JID without a localpart: a domain with a resource. Not a login,
            // so what is measured is the refusal - but the refusal has to be
            // about the missing account and not about the shape.
            var thrown = Assert.Throws<ArgumentException>(
                             () => Connect("a.example.com/b@example.net"));

            Assert.That(thrown!.Message, Does.Contain("no account"));

        }

        #endregion

        #region ADomainWithoutAnAccount_IsRefusedForWhatItIs()

        /// <summary>
        /// <c>example.com</c> is a perfectly good JID and no login. The
        /// objection is about the missing account, and it says so.
        /// </summary>
        [Test]
        public void ADomainWithoutAnAccount_IsRefusedForWhatItIs()
        {

            var thrown = Assert.Throws<ArgumentException>(() => Connect("example.com"));

            Assert.That(thrown!.Message, Does.Contain("no account"));

        }

        #endregion

        #region SomethingThatIsNoJid_IsRefusedWithTheReason()

        /// <summary>
        /// What is not an address at all comes back as an
        /// <c>ArgumentException</c> like everything else here - the reason from
        /// the parser travels along inside it, instead of a
        /// <c>JidFormatException</c> surfacing from a constructor nobody
        /// expected one from.
        /// </summary>
        [Test]
        public void SomethingThatIsNoJid_IsRefusedWithTheReason()
        {

            Assert.Multiple(() =>
            {
                Assert.That(() => Connect(""),                  Throws.TypeOf<ArgumentException>());
                Assert.That(() => Connect("alice@"),            Throws.TypeOf<ArgumentException>());
                Assert.That(() => Connect("@example.com"),      Throws.TypeOf<ArgumentException>());
                Assert.That(() => Connect("alice@@example.com"), Throws.TypeOf<ArgumentException>());
            });

        }

        #endregion

        #region TheAddressIsPrepared_NotJustSplit()

        /// <summary>
        /// RFC 7622 does not only say where to cut but what the pieces mean.
        /// Localpart and domainpart are independent of spelling, so the address
        /// reaches the server as what it is rather than as it was typed.
        /// </summary>
        /// <remarks>
        /// This is what <c>JidUtilities</c> was written for and what the
        /// constructor was missing entirely - it handed the localpart on
        /// exactly as typed, into SASL and into every later comparison.
        /// </remarks>
        [Test]
        public void TheAddressIsPrepared_NotJustSplit()
        {

            Assert.That(Connect("ALICE@Example.COM").BareJid, Is.EqualTo("alice@example.com"));

        }

        #endregion

    }

}
