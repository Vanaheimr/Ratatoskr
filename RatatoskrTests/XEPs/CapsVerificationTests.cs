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

using System.Text.RegularExpressions;
using System.Xml.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;
using org.GraphDefined.Vanaheimr.Ratatoskr.Server;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// XEP-0115, section 5.4: The cache takes a disco#info answer in only once
    /// its hash yields the announced <c>ver</c> value.
    /// </summary>
    /// <remarks>
    /// Without this check the cache was poisonable by everyone whose presence
    /// arrives here. The attacker announces the <c>node#ver</c> pair of a
    /// widespread client and answers the following query with a list of their
    /// choice; from then on their list lies under this pair, and it is handed
    /// out to every further contact announcing the same pair — without that one
    /// ever being asked.
    ///
    /// What is checked works without a server: <see cref="DiscoManager"/> gets
    /// a send function that only writes the query down, and the answer is fed
    /// in by hand. Only that way can an answer be built that does not fit the
    /// announced hash — an honest client would not even get to it.
    /// </remarks>
    [TestFixture]
    public class CapsVerificationTests
    {

        #region Data

        private const String NodeName   = "https://example.org/client";
        private const String Mallory  = "mallory@example.org/r";
        private const String Alice    = "alice@example.org/r";

        /// <summary>
        /// The features the widespread client really has.
        /// </summary>
        private static readonly String[] Real = [
            "http://jabber.org/protocol/caps",
            "http://jabber.org/protocol/disco#info"
        ];

        /// <summary>
        /// The list the attacker wants to substitute instead.
        /// </summary>
        private static readonly String[] Substituted = [
         "urn:xmpp:receipts"
        ];

        private static readonly DiscoIdentity Identity = new("client", "pc", "Exodus 0.9.1");

        private DiscoManager        disco       = null!;
        private EntityCapsManager   caps        = null!;
        private List<String>        sent    = null!;
        private List<String>        refused   = null!;
        private List<DiscoInfo>     reported    = null!;

        #endregion

        #region SetUp

        [SetUp]
        public void Setup()
        {

            sent      = [];
            refused   = [];
            reported  = [];

            disco = new DiscoManager(xml =>
            {
                lock (sent) sent.Add(xml);
                return Task.CompletedTask;
            });

            caps = new EntityCapsManager(disco) { Node = NodeName };

            caps.OnCapsRejected   += (timestamp, sender, from, reason, ct) => { lock (refused) refused.Add(reason);  return Task.CompletedTask; };
            caps.OnCapsDiscovered += (timestamp, sender, from, info, ct)  => { lock (reported)  reported.Add(info);    return Task.CompletedTask; };

        }

        #endregion

        #region Helper functions

        /// <summary>
        /// The verification string over these features.
        /// </summary>
        private static String VerOf(params String[] features)
            => EntityCapsManager.VerificationString([Identity], features);

        /// <summary>
        /// A disco#info answer with exactly these features.
        /// </summary>
        private static String Reply(params String[] features)
            => "<query xmlns='http://jabber.org/protocol/disco#info'>" +
               $"<identity category='{Identity.Category}' type='{Identity.Type}' " +
               $"name='{Identity.Name}'/>" +
               String.Concat(features.Select(f => $"<feature var='{f}'/>")) +
               "</query>";

        /// <summary>
        /// A disco#info answer with a softwareinfo form whose <c>os</c> field
        /// carries this value.
        /// </summary>
        private static String ReplyWithForm(String os)
            => "<query xmlns='http://jabber.org/protocol/disco#info'>" +
               $"<identity category='{Identity.Category}' type='{Identity.Type}' " +
               $"name='{Identity.Name}'/>" +
               String.Concat(Real.Select(f => $"<feature var='{f}'/>")) +
               "<x xmlns='jabber:x:data' type='result'>" +
               "<field var='FORM_TYPE' type='hidden'>" +
               "<value>urn:xmpp:dataforms:softwareinfo</value></field>" +
               $"<field var='os'><value>{os}</value></field>" +
               "</x></query>";

        /// <summary>
        /// The verification string that goes with it.
        /// </summary>
        private static String VerWithForm(String os)
            => EntityCapsManager.VerificationString(
                   [Identity],
                   Real,
                   [new DiscoForm([
                        new DiscoField("FORM_TYPE", "hidden", ["urn:xmpp:dataforms:softwareinfo"]),
                        new DiscoField("os",        null,     [os])
                    ])]);

        private Int32 Queries
        {
            get { lock (sent) return sent.Count; }
        }

        /// <summary>
        /// Waits until this many disco#info queries have been sent off.
        /// </summary>
        private async Task WaitForQueries(Int32 count)
        {

            var ok = await XMPPServer.WaitUntilAsync(() => Queries >= count);

            Assert.That(ok, Is.True,
                        $"Expected were {count} disco#info queries, sent off were {Queries}.");

        }

        /// <summary>
        /// Answers the query that was sent off last.
        /// </summary>
        private void Answer(String from, String query)
        {

            String last;
            lock (sent) last = sent[^1];

            var id = Regex.Match(last, @"id='([^']+)'").Groups[1].Value;

            disco.ProcessInfoResult(id,
                                    XElement.Parse($"<iq type='result' id='{id}'>{query}</iq>"),
                                    from);

        }

        #endregion


        #region AnAnswerThatDoesNotHashToTheAnnouncedVer_IsNotCached()

        /// <summary>
        /// The core: whoever announces a <c>ver</c> and answers something else
        /// does not get into the cache.
        /// </summary>
        [Test]
        public async Task AnAnswerThatDoesNotHashToTheAnnouncedVer_IsNotCached()
        {

            var ver      = VerOf(Real);
            var running  = caps.ProcessCapsAsync(JID.Parse(Mallory), NodeName, ver, EntityCapsManager.Sha1Algorithm);

            await WaitForQueries(1);
            Answer(Mallory, Reply(Substituted));
            await running;

            Assert.Multiple(() =>
            {

                Assert.That(caps.GetCachedInfo($"{NodeName}#{ver}"), Is.Null,
                            "The substituted answer must not stand in the cache.");

                Assert.That(refused, Is.Not.Empty, "The refusal has to be reported.");

                // It is reported nevertheless: it is what this entity says about
                // itself, and precisely that would come out of an ordinary
                // disco#info query as well. What is refused is only the
                // bundling.
                Assert.That(reported, Has.Count.EqualTo(1));

            });

        }

        #endregion

        #region AnAnswerThatHashesToTheAnnouncedVer_IsCached()

        /// <summary>
        /// The counter-check: the honest answer is stored.
        /// </summary>
        /// <remarks>
        /// Without it the collection would pass even if simply nothing got into
        /// the cache any more — and the whole purpose of XEP-0115, to save the
        /// second query, would have vanished silently.
        /// </remarks>
        [Test]
        public async Task AnAnswerThatHashesToTheAnnouncedVer_IsCached()
        {

            var ver      = VerOf(Real);
            var running  = caps.ProcessCapsAsync(JID.Parse(Alice), NodeName, ver, EntityCapsManager.Sha1Algorithm);

            await WaitForQueries(1);
            Answer(Alice, Reply(Real));
            await running;

            var stored = caps.GetCachedInfo($"{NodeName}#{ver}");

            Assert.Multiple(() =>
            {

                Assert.That(stored, Is.Not.Null, "The checked answer belongs into the cache.");
                Assert.That(stored!.Features, Is.EquivalentTo(Real));

                Assert.That(refused, Is.Empty,
                            $"Refused without a reason: {String.Join(" | ", refused)}");

            });

        }

        #endregion

        #region ThePoisonedEntryIsNotServedToTheNextContact()

        /// <summary>
        /// The actual damage, spelled out: what the attacker leaves behind must
        /// not be handed to the next contact as that one's information.
        /// </summary>
        /// <remarks>
        /// That is the test showing the poisoning as such. The others show only
        /// that an entry is missing — here it is missing at the place where it
        /// would have taken effect: Alice announces the same pair and is
        /// therefore asked a second time instead of getting Mallory's list
        /// substituted.
        /// </remarks>
        [Test]
        public async Task ThePoisonedEntryIsNotServedToTheNextContact()
        {

            var ver = VerOf(Real);

            // Mallory announces the pair of a widespread client and answers
            // with a list of their choice.
            var attack = caps.ProcessCapsAsync(JID.Parse(Mallory), NodeName, ver, EntityCapsManager.Sha1Algorithm);
            await WaitForQueries(1);
            Answer(Mallory, Reply(Substituted));
            await attack;

            // Alice announces the same pair - this time rightly.
            var honest = caps.ProcessCapsAsync(JID.Parse(Alice), NodeName, ver, EntityCapsManager.Sha1Algorithm);
            await WaitForQueries(2);
            Answer(Alice, Reply(Real));
            await honest;

            Assert.Multiple(() =>
            {

                Assert.That(Queries, Is.EqualTo(2),
                            "Alice has to be asked herself; to be served out of the cache " +
                            "would mean taking Mallory's list for hers.");

                Assert.That(reported[^1].Features, Is.EquivalentTo(Real));
                Assert.That(reported[^1].Features, Does.Not.Contain("urn:xmpp:receipts"));

            });

        }

        #endregion

        #region WithoutAHashAttribute_NothingIsAsked()

        /// <summary>
        /// The old form from XEP-0115 before 1.4: <c>ver</c> is a version
        /// number there and no hash. Nothing can be recalculated, so nothing is
        /// stored - and nothing is asked for either.
        /// </summary>
        /// <remarks>
        /// Without this rule the most convenient way would stay open: whoever
        /// wants to poison the cache simply leaves the <c>hash</c> attribute
        /// out.
        ///
        /// That nothing is *asked* is the younger half and the one worth
        /// checking. This used to send the query off and throw the answer away,
        /// which cost a round trip per presence forever, since the cache that
        /// would have ended it stays empty here by design. With a real far end
        /// it cost more than that: node#ver out of a version number is usually a
        /// node nobody announced, and Trillian answers its own with
        /// item-not-found - a stanza error arriving for a question nobody was
        /// waiting for.
        ///
        /// What is checked is the reason as well, and that not out of a love of
        /// order: a missing attribute would otherwise fall under "unknown
        /// algorithm" (<c>null</c> is after all not <c>sha-1</c>), and the
        /// branch of its own for it would be nothing but an ornament. The
        /// difference belongs in the protocol: the far end is not broken, it is
        /// old.
        /// </remarks>
        [Test]
        public async Task WithoutAHashAttribute_NothingIsAsked()
        {

            var ver = VerOf(Real);

            await caps.ProcessCapsAsync(JID.Parse(Mallory), NodeName, ver, hash: null);

            Assert.Multiple(() =>
            {

                Assert.That(Queries, Is.EqualTo(0),
                            "An answer that may not be stored need not be fetched. " +
                            $"Sent off were: {String.Join(" | ", sent)}");

                Assert.That(caps.GetCachedInfo($"{NodeName}#{ver}"), Is.Null);
                Assert.That(reported, Is.Empty,
                            "Nothing was asked, so there is nothing to report.");

                Assert.That(refused.Any(g => g.Contains("no hash attribute", StringComparison.Ordinal)),
                            Is.True,
                            $"The old form has to be named as such. What was reported: " +
                            $"{String.Join(" | ", refused)}");

            });

        }

        #endregion

        #region AnUnknownHashAlgorithm_IsNotCached()

        /// <summary>
        /// And an algorithm this client cannot calculate likewise — even if it
        /// is stronger than SHA-1.
        /// </summary>
        [Test]
        public async Task AnUnknownHashAlgorithm_IsNotCached()
        {

            var ver      = VerOf(Real);
            var running  = caps.ProcessCapsAsync(JID.Parse(Mallory), NodeName, ver, "sha-256");

            await WaitForQueries(1);
            Answer(Mallory, Reply(Real));
            await running;

            Assert.Multiple(() =>
            {
                Assert.That(caps.GetCachedInfo($"{NodeName}#{ver}"), Is.Null);
                Assert.That(refused, Is.Not.Empty);
            });

        }

        #endregion

        #region AnAnswerWithADataForm_IsVerifiedIncludingTheForm()

        /// <summary>
        /// An answer with an XEP-0128 data form is checked and stored — the
        /// form goes into the hash.
        /// </summary>
        /// <remarks>
        /// The frame is closed by the counterpart right below: here stands that
        /// a form is not in the way any more; there, that it is really
        /// calculated in. Without both together the test could also be passed
        /// by simply passing forms over.
        /// </remarks>
        [Test]
        public async Task AnAnswerWithADataForm_IsVerifiedIncludingTheForm()
        {

            var ver      = VerWithForm("Mac");
            var running  = caps.ProcessCapsAsync(JID.Parse(Alice), NodeName, ver, EntityCapsManager.Sha1Algorithm);

            await WaitForQueries(1);
            Answer(Alice, ReplyWithForm("Mac"));
            await running;

            var stored = caps.GetCachedInfo($"{NodeName}#{ver}");

            Assert.Multiple(() =>
            {

                Assert.That(refused, Is.Empty,
                            $"Refused without a reason: {String.Join(" | ", refused)}");

                Assert.That(stored, Is.Not.Null,
                            "An answer with a form belongs into the cache just like one without.");

                Assert.That(stored!.Forms, Has.Count.EqualTo(1),
                            "The form has to be preserved.");

                Assert.That(stored.Forms[0].FormType,
                            Is.EqualTo("urn:xmpp:dataforms:softwareinfo"));

            });

        }

        #endregion

        #region AnAnswerWhoseFormWasChanged_IsNotCached()

        /// <summary>
        /// And the counter-check: if something is changed in the form, the hash
        /// does not fit any more.
        /// </summary>
        /// <remarks>
        /// Without this test "simply pass forms over" would be a passing
        /// solution — and with that precisely the gap this is about would stand
        /// open again: two entities differing solely in their extended details
        /// would have the same <c>ver</c> value, and the answer of the one
        /// could be ascribed to the other.
        /// </remarks>
        [Test]
        public async Task AnAnswerWhoseFormWasChanged_IsNotCached()
        {

            var ver      = VerWithForm("Mac");
            var running  = caps.ProcessCapsAsync(JID.Parse(Mallory), NodeName, ver, EntityCapsManager.Sha1Algorithm);

            await WaitForQueries(1);
            Answer(Mallory, ReplyWithForm("Windows"));
            await running;

            Assert.Multiple(() =>
            {
                Assert.That(caps.GetCachedInfo($"{NodeName}#{ver}"), Is.Null);
                Assert.That(refused, Is.Not.Empty);
            });

        }

        #endregion

        #region XmlLangOfAnIdentity_GoesIntoTheHash()

        /// <summary>
        /// The <c>xml:lang</c> of an identity goes into the hash — and for that
        /// it first has to be preserved when the answer is taken apart.
        /// </summary>
        /// <remarks>
        /// An entity may carry the same name in several languages; in the
        /// verification string the language stands between type and name.
        /// Whoever loses it when taking the answer apart calculates a different
        /// value than every such far end does itself — and refuses it although
        /// it is honest.
        /// </remarks>
        [Test]
        public async Task XmlLangOfAnIdentity_GoesIntoTheHash()
        {

            var ver = EntityCapsManager.VerificationString(
                          [new DiscoIdentity("client", "pc", "Psi 0.11", "en")],
                          Real);

            var reply =
                "<query xmlns='http://jabber.org/protocol/disco#info'>" +
                "<identity xml:lang='en' category='client' type='pc' name='Psi 0.11'/>" +
                String.Concat(Real.Select(f => $"<feature var='{f}'/>")) +
                "</query>";

            var running = caps.ProcessCapsAsync(JID.Parse(Alice), NodeName, ver, EntityCapsManager.Sha1Algorithm);

            await WaitForQueries(1);
            Answer(Alice, reply);
            await running;

            var stored = caps.GetCachedInfo($"{NodeName}#{ver}");

            Assert.Multiple(() =>
            {

                Assert.That(refused, Is.Empty,
                            $"Refused without a reason: {String.Join(" | ", refused)}");

                Assert.That(stored, Is.Not.Null);
                Assert.That(stored!.Identities[0].Language, Is.EqualTo("en"));

            });

        }

        #endregion

        #region AnAmbiguousAnswer_IsNotCached()

        /// <summary>
        /// XEP-0115, section 5.4: An answer that cannot be turned
        /// unambiguously into a string is discarded as a whole.
        /// </summary>
        /// <remarks>
        /// That is no formal strictness. Where duplications stand there is more
        /// than one possible string for the same answer — and with that a
        /// second answer can be built for a given hash. To decide on one
        /// reading would mean leaving the attacker the choice of which one they
        /// mean.
        /// </remarks>
        [Test]
        public async Task AnAmbiguousAnswer_IsNotCached()
        {

            const String Me = "<identity category='client' type='pc' name='Exodus 0.9.1'/>";

            String Query(String content)
                => $"<query xmlns='http://jabber.org/protocol/disco#info'>{content}</query>";

            DiscoForm Form(params String[] types)
                => new([new DiscoField("FORM_TYPE", "hidden", types)]);

            const String FormXml =
                "<x xmlns='jabber:x:data' type='result'>" +
                "<field var='FORM_TYPE' type='hidden'><value>urn:test:form</value></field></x>";

            // What is decisive is that the announced ver value *fits* the
            // ambiguous answer: otherwise the hash comparison would strike
            // already, and these rules would be unchecked.
            var cases = new (String Name, String Reply, String Ver)[]
            {

                ("the same feature twice",
                 Query(Me + "<feature var='urn:test:a'/><feature var='urn:test:a'/>"),
                 EntityCapsManager.VerificationString([Identity], ["urn:test:a", "urn:test:a"])),

                ("the same identity twice",
                 Query(Me + Me),
                 EntityCapsManager.VerificationString([Identity, Identity], [])),

                ("two forms with the same FORM_TYPE",
                 Query(Me + FormXml + FormXml),
                 EntityCapsManager.VerificationString([Identity], [],
                                                      [Form("urn:test:form"),
                                                       Form("urn:test:form")])),

                // The second value vanishes without a trace out of the
                // calculation - the FORM_TYPE field itself is not appended
                // after all. Two different answers would thereby yield the same
                // hash.
                ("one FORM_TYPE with two values",
                 Query(Me + "<x xmlns='jabber:x:data' type='result'>" +
                             "<field var='FORM_TYPE' type='hidden'>" +
                             "<value>urn:test:a</value><value>urn:test:b</value></field></x>"),
                 EntityCapsManager.VerificationString([Identity], [],
                                                      [Form("urn:test:a", "urn:test:b")]))

            };

            foreach (var (name, reply, ver) in cases)
            {

                Setup();

                var running = caps.ProcessCapsAsync(JID.Parse(Mallory), NodeName, ver, EntityCapsManager.Sha1Algorithm);

                await WaitForQueries(1);
                Answer(Mallory, reply);
                await running;

                Assert.Multiple(() =>
                {

                    Assert.That(caps.GetCachedInfo($"{NodeName}#{ver}"), Is.Null,
                                $"Ambiguous, but the hash fitted - and stored: {name}.");

                    Assert.That(refused, Is.Not.Empty,
                                $"Let through without a report: {name}.");

                });

            }

        }

        #endregion

        #region ACachedEntryIsServedWithoutAsking()

        /// <summary>
        /// And what the whole thing is there for: a checked entry saves the
        /// next contact the query.
        /// </summary>
        [Test]
        public async Task ACachedEntryIsServedWithoutAsking()
        {

            var ver = VerOf(Real);

            var first = caps.ProcessCapsAsync(JID.Parse(Alice), NodeName, ver, EntityCapsManager.Sha1Algorithm);
            await WaitForQueries(1);
            Answer(Alice, Reply(Real));
            await first;

            await caps.ProcessCapsAsync(JID.Parse(Mallory), NodeName, ver, EntityCapsManager.Sha1Algorithm);

            Assert.Multiple(() =>
            {
                Assert.That(Queries, Is.EqualTo(1), "The cache has to save the second query.");
                Assert.That(reported, Has.Count.EqualTo(2));
            });

        }

        #endregion

    }

}
