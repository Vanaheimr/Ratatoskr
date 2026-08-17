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
    /// XEP-0115 between two real clients: announcement in the presence,
    /// disco#info query, check, cache.
    /// </summary>
    /// <remarks>
    /// The check itself is established on its own in
    /// <see cref="CapsVerificationTests"/> — there with answers an honest
    /// client could not give at all. Here it is about the other half: that the
    /// whole path leads into the cache under real conditions.
    ///
    /// That is no formality. The <c>hash</c> value is read out of the presence
    /// and handed on at exactly one place; were it to fall away there, every
    /// answer would be uncheckable and the cache would thereby stay empty for
    /// good. No test of the check itself would notice — the negotiation would
    /// carry on, only without the use XEP-0115 exists for.
    ///
    /// Along the way the test establishes that our own <c>ver</c> fits our own
    /// disco#info answer: what is announced comes out of
    /// <c>LocalIdentities</c>/<c>LocalFeatures</c>, what is answered likewise,
    /// and here the other side recalculates both against each other.
    /// </remarks>
    [TestFixture]
    public class CapsExchangeTests : AXMPPTests
    {

        #region CapsOfARealContact_AreVerifiedAndCached()

        /// <summary>
        /// Bob sees Alice's presence, asks, recalculates and stores.
        /// </summary>
        [Test]
        public async Task CapsOfARealContact_AreVerifiedAndCached()
        {

            MakeContacts("alice", "bob");

            var alice  = await ConnectClientAsync("alice");
            var bob    = await ConnectClientAsync("bob");

            var refused = new List<String>();
            bob.Connection.EntityCaps!.OnCapsRejected += (timestamp, sender, from, reason, ct) => { refused.Add(reason); return Task.CompletedTask; };

            var aliceNode = alice.Connection.EntityCaps!.Node;
            var aliceVer  = alice.Connection.EntityCaps!.CalculateVerificationString();
            var key       = $"{aliceNode}#{aliceVer}";

            await WaitFor(() => bob.Connection.EntityCaps!.GetCachedInfo(key) is not null,
                          "Alice's checked capabilities in Bob's cache");

            var stored = bob.Connection.EntityCaps!.GetCachedInfo(key)!;

            Assert.Multiple(() =>
            {

                Assert.That(refused, Is.Empty,
                            $"Our own announcement was refused: {String.Join(" | ", refused)}");

                // The counter-check to the check: what lies there yields the
                // hash it lies under.
                Assert.That(EntityCapsManager.VerificationString(stored.Identities,
                                                                 stored.Features),
                            Is.EqualTo(aliceVer));

                Assert.That(stored.Features, Does.Contain("urn:xmpp:receipts"));

            });

        }

        #endregion

        #region AnIdentityWithXmlLang_SurvivesTheRoundTrip()

        /// <summary>
        /// If an entity carries its name in a language, it has to say so in its
        /// own answer as well.
        /// </summary>
        /// <remarks>
        /// What is announced is a hash over <c>category/type/lang/name</c>.
        /// Were the <c>xml:lang</c> to stay out of the disco#info answer, the
        /// far end would calculate a different value from the announced one and
        /// would refuse us — we would be a liar for everybody checking
        /// according to XEP-0115 §5.4.
        ///
        /// The path there leads over two places in different files
        /// (announcement and answer); only together do they make sense, and
        /// only here do they run against each other.
        /// </remarks>
        [Test]
        public async Task AnIdentityWithXmlLang_SurvivesTheRoundTrip()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice");

            // The identity changes after the connecting - the managers come
            // about only there. The presence therefore has to go out once
            // more, otherwise the old ver value stands at Bob's.
            alice.Connection.Disco!.LocalIdentities.Clear();
            alice.Connection.Disco!.LocalIdentities.Add(
                new DiscoIdentity("client", "pc", "Psi 0.11", "en"));

            await alice.Connection.SendPresenceAsync();

            var newVer = alice.Connection.EntityCaps!.CalculateVerificationString();

            // See OwnDataForm_SurvivesTheRoundTrip: only once the new presence
            // stands at the server is the announcement in agreement with the
            // answer again.
            await WaitFor(() => Server.SessionOf(alice.FullJid.ToString())?.LastPresence?
                                      .Contains(newVer, StringComparison.Ordinal) == true,
                          "Alice's new presence at the server");

            var bob = await ConnectClientAsync("bob");

            var refused = new List<String>();
            bob.Connection.EntityCaps!.OnCapsRejected += (timestamp, sender, from, reason, ct) => { refused.Add(reason); return Task.CompletedTask; };

            var key = $"{alice.Connection.EntityCaps!.Node}#{newVer}";

            await WaitFor(() => bob.Connection.EntityCaps!.GetCachedInfo(key) is not null,
                          "Alice's checked capabilities in Bob's cache");

            var stored = bob.Connection.EntityCaps!.GetCachedInfo(key)!;

            Assert.Multiple(() =>
            {

                Assert.That(refused, Is.Empty,
                            $"Our own announcement was refused: {String.Join(" | ", refused)}");

                Assert.That(stored.Identities[0].Language, Is.EqualTo("en"),
                            "The language has to stand in our own answer.");

            });

        }

        #endregion

        #region OwnDataForm_SurvivesTheRoundTrip()

        /// <summary>
        /// XEP-0128: What stands in <c>LocalForms</c> goes into our own
        /// disco#info answer <b>and</b> into the announced hash.
        /// </summary>
        /// <remarks>
        /// The two belong together but lie in different files - the
        /// announcement in the <c>EntityCapsManager</c>, the answer in the
        /// <c>DiscoManager</c>. Were the form to go into only one of them, we
        /// would be a forger for every far end recalculating according to
        /// XEP-0115 §5.4: announced and calculated hash would come apart, and
        /// that with an entirely honest piece of information.
        ///
        /// The value with <c>&amp;</c> and <c>&lt;</c> is no high spirits: It
        /// goes through XML and has to come out undamaged. Were it to arrive
        /// changed, the hash would not hold any more either - the error would
        /// then look like a forgery.
        /// </remarks>
        [Test]
        public async Task OwnDataForm_SurvivesTheRoundTrip()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice");

            alice.Connection.Disco!.LocalForms.Add(
                DiscoForm.SoftwareInfo(Software:         "Jabber & Co <test edition>",
                                       SoftwareVersion:  "0.1",
                                       OperatingSystem:  "Windows"));

            await alice.Connection.SendPresenceAsync();

            var newVer = alice.Connection.EntityCaps!.CalculateVerificationString();

            // Bob may come only afterwards. Between the changed information and
            // the new presence lies a window in which Alice still announced the
            // old ver value and would already give the new answer - whoever
            // asks within it rightly gets a discrepancy reported. That is no
            // error but the price for capabilities being able to change; only
            // it has no business in this test.
            await WaitFor(() => Server.SessionOf(alice.FullJid.ToString())?.LastPresence?
                                      .Contains(newVer, StringComparison.Ordinal) == true,
                          "Alice's new presence at the server");

            var bob = await ConnectClientAsync("bob");

            var refused = new List<String>();
            bob.Connection.EntityCaps!.OnCapsRejected += (timestamp, sender, from, reason, ct) => { refused.Add(reason); return Task.CompletedTask; };

            var key = $"{alice.Connection.EntityCaps!.Node}#{newVer}";

            await WaitFor(() => bob.Connection.EntityCaps!.GetCachedInfo(key) is not null,
                          "Alice's checked capabilities in Bob's cache");

            var stored  = bob.Connection.EntityCaps!.GetCachedInfo(key)!;
            var form    = stored.Forms.SingleOrDefault();

            Assert.Multiple(() =>
            {

                Assert.That(refused, Is.Empty,
                            $"Our own announcement was refused: {String.Join(" | ", refused)}");

                Assert.That(form, Is.Not.Null, "The form is missing in the answer.");

                Assert.That(form!.FormType,
                            Is.EqualTo("urn:xmpp:dataforms:softwareinfo"));

                Assert.That(form.Fields.Single(f => f.Var == "software").Values.Single(),
                            Is.EqualTo("Jabber & Co <test edition>"),
                            "The value has to survive the XML round trip undamaged.");

                Assert.That(form.Fields.Any(f => f.Var == "os_version"), Is.False,
                            "A piece of information that was not given must not become an empty field.");

            });

        }

        #endregion

        #region WithoutOwnForms_NothingIsAnnounced()

        /// <summary>
        /// The counter-check to the setting: without one's own doing this
        /// client publishes no extended details.
        /// </summary>
        /// <remarks>
        /// Software, version and operating system are precisely the details a
        /// device can be recognised by again, and every contact gets them
        /// unasked. That the list begins empty is therefore a decision and no
        /// coincidence - and belongs secured like every other one.
        /// </remarks>
        [Test]
        public async Task WithoutOwnForms_NothingIsAnnounced()
        {

            MakeContacts("alice", "bob");

            var alice = await ConnectClientAsync("alice");
            var bob   = await ConnectClientAsync("bob");

            var key = $"{alice.Connection.EntityCaps!.Node}#" +
                             $"{alice.Connection.EntityCaps!.CalculateVerificationString()}";

            await WaitFor(() => bob.Connection.EntityCaps!.GetCachedInfo(key) is not null,
                          "Alice's checked capabilities in Bob's cache");

            Assert.Multiple(() =>
            {
                Assert.That(alice.Connection.Disco!.LocalForms, Is.Empty);
                Assert.That(bob.Connection.EntityCaps!.GetCachedInfo(key)!.Forms, Is.Empty);
            });

        }

        #endregion

    }

}
