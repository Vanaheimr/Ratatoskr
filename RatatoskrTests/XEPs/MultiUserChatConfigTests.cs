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

using NUnit.Framework;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0045, section 10.2: changing a room's configuration without
    /// resetting it.
    /// </summary>
    /// <remarks>
    /// <b>A configuration form is a state and not a patch</b>, and that is the
    /// whole of what is checked here. A submit carrying only the field one
    /// meant to change tells the service that every other field is now unset -
    /// and the service answers <c>result</c>, because nothing about the request
    /// was malformed. The room quietly loses its password, its member list and
    /// whether it persists, and the only sign of it is that the room behaves
    /// differently afterwards.
    ///
    /// Which is why this has tests of its own rather than being covered in
    /// passing by the round that makes a room non-anonymous: that round would
    /// pass either way.
    /// </remarks>
    [TestFixture]
    public class MultiUserChatConfigTests
    {

        #region Data

        private static readonly XNamespace forms = DataForm.Namespace;

        /// <summary>
        /// A form of the shape a service sends: types and labels on the way out,
        /// several fields, one of them with more than one value.
        /// </summary>
        private static XElement AForm()

            => new (forms + "x",
                   new XAttribute("type", "form"),

                   new XElement(forms + "field",
                       new XAttribute("var",  "FORM_TYPE"),
                       new XAttribute("type", "hidden"),
                       new XElement(forms + "value", "http://jabber.org/protocol/muc#roomconfig")),

                   new XElement(forms + "field",
                       new XAttribute("var",   MultiUserChat.WhoIsField),
                       new XAttribute("type",  "list-single"),
                       new XAttribute("label", "Who may discover real JIDs?"),
                       new XElement(forms + "value", MultiUserChat.WhoIsModerators)),

                   new XElement(forms + "field",
                       new XAttribute("var",   "muc#roomconfig_roomsecret"),
                       new XAttribute("type",  "text-private"),
                       new XElement(forms + "value", "the password")),

                   new XElement(forms + "field",
                       new XAttribute("var",  MultiUserChat.PersistentField),
                       new XAttribute("type", "boolean"),
                       new XElement(forms + "value", "1")),

                   new XElement(forms + "field",
                       new XAttribute("var",  "muc#roomconfig_presencebroadcast"),
                       new XAttribute("type", "list-multi"),
                       new XElement(forms + "value", "moderator"),
                       new XElement(forms + "value", "participant")),

                   // A field this library has never heard of, which a service is
                   // free to have.
                   new XElement(forms + "field",
                       new XAttribute("var",  "muc#roomconfig_somethingelse"),
                       new XElement(forms + "value", "kept")));

        private static IEnumerable<string> ValuesOf(XElement form, string name)

            => form.Elements(forms + "field")
                   .First(field => field.Attr("var") == name)
                   .Elements(forms + "value")
                   .Select(value => value.Value);

        #endregion


        #region EverythingElseComesBackUnchanged()

        /// <summary>
        /// <b>The one that matters.</b> One field is changed and every other
        /// travels back exactly as it came.
        /// </summary>
        /// <remarks>
        /// Including the password, the multi-valued field and the one this
        /// library does not know. That last one is the point of the test as
        /// much as the password: a client that only carries back what it
        /// understands resets everything a service happens to offer beyond the
        /// registry, and different services offer different things.
        /// </remarks>
        [Test]
        public void EverythingElseComesBackUnchanged()
        {

            var submit = MultiUserChat.ConfigWith(
                             AForm(),
                             new Dictionary<string, string> {
                                 { MultiUserChat.WhoIsField, MultiUserChat.WhoIsAnyone }
                             },
                             out var missing);

            Assert.Multiple(() =>
            {

                Assert.That(missing, Is.Empty);

                Assert.That(submit.Attr("type"), Is.EqualTo("submit"),
                            "A form going back is a submit; a service ignores anything else.");

                Assert.That(ValuesOf(submit, MultiUserChat.WhoIsField),
                            Is.EqualTo(new[] { MultiUserChat.WhoIsAnyone }),
                            "The field that was meant to change did not.");

                Assert.That(ValuesOf(submit, "FORM_TYPE"),
                            Is.EqualTo(new[] { "http://jabber.org/protocol/muc#roomconfig" }),
                            "Without the FORM_TYPE a service does not know which form this is.");

                Assert.That(ValuesOf(submit, "muc#roomconfig_roomsecret"),
                            Is.EqualTo(new[] { "the password" }),
                            "The room's password was dropped, which unlocks the room and answers " +
                            "'result' while doing it.");

                Assert.That(ValuesOf(submit, MultiUserChat.PersistentField),
                            Is.EqualTo(new[] { "1" }),
                            "The room stopped being persistent, so it disappears when the last " +
                            "person leaves.");

                Assert.That(ValuesOf(submit, "muc#roomconfig_presencebroadcast"),
                            Is.EqualTo(new[] { "moderator", "participant" }),
                            "A field with several values came back with fewer.");

                Assert.That(ValuesOf(submit, "muc#roomconfig_somethingelse"),
                            Is.EqualTo(new[] { "kept" }),
                            "A setting this library has never heard of was dropped. What is not " +
                            "understood here is still part of the room's state.");

            });

        }

        #endregion

        #region TheSubmitCarriesNoTypesAndNoLabels()

        /// <summary>
        /// XEP-0004, section 3.2: a submit carries <c>var</c> and values.
        /// </summary>
        /// <remarks>
        /// Types and labels describe how a form is drawn and mean nothing on
        /// the way back. Sending them is not fatal - services ignore them - but
        /// a label is a string the service chose in whatever language it chose,
        /// and handing it back is a client claiming a setting is called that.
        /// </remarks>
        [Test]
        public void TheSubmitCarriesNoTypesAndNoLabels()
        {

            var submit = MultiUserChat.ConfigWith(AForm(),
                                                  new Dictionary<string, string>(),
                                                  out _);

            Assert.Multiple(() =>
            {

                Assert.That(submit.Descendants().Any(e => e.Attribute("type") is not null &&
                                                          e.Name.LocalName == "field"),
                            Is.False,
                            "A field went back carrying the type it is drawn with.");

                Assert.That(submit.Descendants().Any(e => e.Attribute("label") is not null),
                            Is.False,
                            "A label went back - a string the service chose, handed back as though " +
                            "this client had chosen it.");

            });

        }

        #endregion

        #region AFieldTheServiceDoesNotOfferIsNotInvented()

        /// <summary>
        /// Asking for a setting a service does not have.
        /// </summary>
        /// <remarks>
        /// <b>Not added, and reported.</b> Adding it would ask for something
        /// that does not exist; adding it silently would be worse - the submit
        /// succeeds, and the caller believes a room is non-anonymous when the
        /// room was never asked to be. That is the case
        /// <see cref="XMPPConnection.ConfigureRoomAsync"/> refuses to send at
        /// all.
        /// </remarks>
        [Test]
        public void AFieldTheServiceDoesNotOfferIsNotInvented()
        {

            var submit = MultiUserChat.ConfigWith(
                             AForm(),
                             new Dictionary<string, string> {
                                 { MultiUserChat.WhoIsField,       MultiUserChat.WhoIsAnyone },
                                 { MultiUserChat.MembersOnlyField, "1" }
                             },
                             out var missing);

            Assert.Multiple(() =>
            {

                Assert.That(missing, Is.EqualTo(new[] { MultiUserChat.MembersOnlyField }),
                            "A setting the service never offered was not reported as missing, so " +
                            "the caller would believe it had been set.");

                Assert.That(submit.Elements(forms + "field")
                                  .Any(field => field.Attr("var") == MultiUserChat.MembersOnlyField),
                            Is.False,
                            "A field the form does not have was invented.");

            });

        }

        #endregion

        #region AnEmptyValueIsAValue()

        /// <summary>
        /// Clearing a setting is setting it, and must not look like leaving it
        /// alone.
        /// </summary>
        [Test]
        public void AnEmptyValueIsAValue()
        {

            var submit = MultiUserChat.ConfigWith(
                             AForm(),
                             new Dictionary<string, string> {
                                 { "muc#roomconfig_roomsecret", "" }
                             },
                             out _);

            Assert.That(ValuesOf(submit, "muc#roomconfig_roomsecret"), Is.EqualTo(new[] { "" }),
                        "Removing a room's password was taken for not touching it.");

        }

        #endregion

        #region TheQueriesAreInTheOwnerNamespace()

        /// <summary>
        /// Both directions, and the namespace is what makes them the owner
        /// protocol rather than an ordinary disco.
        /// </summary>
        [Test]
        public void TheQueriesAreInTheOwnerNamespace()
        {

            var ask  = MultiUserChat.ConfigQuery();
            var back = MultiUserChat.ConfigSubmit(MultiUserChat.ConfigWith(AForm(),
                                                                          new Dictionary<string, string>(),
                                                                          out _));

            Assert.Multiple(() =>
            {

                Assert.That(ask.Name.NamespaceName,  Is.EqualTo(MultiUserChat.OwnerNamespace));
                Assert.That(ask.HasElements,         Is.False,
                            "The request for the form carries something, and an empty query is what " +
                            "asks for it.");

                Assert.That(back.Name.NamespaceName, Is.EqualTo(MultiUserChat.OwnerNamespace));
                Assert.That(back.Elements(forms + "x").Count(), Is.EqualTo(1),
                            "The form did not travel inside the query.");

            });

        }

        #endregion

    }

}
