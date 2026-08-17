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

using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Ratatoskr;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Tests
{

    /// <summary>
    /// The wire format of OMEMO: the three protobuf messages, the
    /// <c>&lt;encrypted/&gt;</c> element and the SCE envelope (XEP-0420).
    /// </summary>
    /// <remarks>
    /// <b>Here every byte counts, and that against the specification and not
    /// against itself.</b> A format that can read itself is none yet - that is
    /// the lesson from D62 to D64. The expected bytes therefore stand written
    /// out where it is possible.
    /// </remarks>
    [TestFixture]
    public class OmemoWireFormatTests
    {

        #region Helper functions

        private static String Hex(Byte[] bytes)
            => Convert.ToHexString(bytes).ToLowerInvariant();

        private static Byte[] Pattern(Int32 length, Byte start = 0)
        {

            var b = new Byte[length];

            for (var i = 0; i < length; i++)
                b[i] = (Byte) (start + i);

            return b;

        }

        #endregion


        #region TheMessage_IsEncodedFieldByField()

        /// <summary>
        /// <c>OMEMOMessage.proto</c> - recalculated field by field.
        /// </summary>
        /// <remarks>
        /// <c>08</c> is field 1 as a varint, <c>10</c> field 2, <c>1a</c>
        /// field 3 as length-delimited, <c>22</c> field 4. The ciphertext
        /// therefore stands <b>inside</b> the protobuf with a tag and a length
        /// - and precisely over that the HMAC runs.
        /// </remarks>
        [Test]
        public void TheMessage_IsEncodedFieldByField()
        {

            var dh          = Pattern(32);
            var ciphertext  = Pattern(16, 100);

            var header = new RatchetHeader(dh, 2, 1);

            Assert.Multiple(() =>
            {

                Assert.That(Hex(header.Encode()),
                            Is.EqualTo("0801" + "1002" + "1a20" + Hex(dh)),
                            "The header without a ciphertext");

                Assert.That(Hex(header.Encode(ciphertext)),
                            Is.EqualTo("0801" + "1002" + "1a20" + Hex(dh) + "2210" + Hex(ciphertext)),
                            "The header with a ciphertext");

            });

        }

        #endregion

        #region TheMac_CoversTheEncodedMessage()

        /// <summary>
        /// The HMAC runs over <c>ad ‖ OMEMOMessage.proto</c> - with the
        /// ciphertext inside the protobuf and not behind it.
        /// </summary>
        /// <remarks>
        /// <b>This test came about through a find while reading the
        /// specification, not through a mutation.</b> In D64 the ciphertext
        /// hung raw behind the header; the specification demands it as field 4
        /// of the encoded message. The difference is three bytes - tag and
        /// length -, and both sides of this house would never have noticed it.
        /// Against a foreign client not a single checksum would have held.
        ///
        /// That is why the calculation stands here by hand next to it.
        /// </remarks>
        [Test]
        public void TheMac_CoversTheEncodedMessage()
        {

            var authKey         = Pattern(32);
            var associatedData  = Encoding.UTF8.GetBytes("AD");
            var header          = new RatchetHeader(Pattern(32), 7, 3);
            var ciphertext      = Pattern(48, 200);

            Byte[] expected = [.. associatedData, .. header.Encode(ciphertext)];

            Assert.That(Hex(DoubleRatchet.Mac(authKey, associatedData, header, ciphertext)),
                        Is.EqualTo(Hex(HMACSHA256.HashData(authKey, expected)[..16])));

        }

        #endregion

        #region ARatchetMessage_SurvivesTheWire()

        /// <summary>
        /// A ratchet message survives the encoding as
        /// <c>OMEMOAuthenticatedMessage</c> - and can still be decrypted
        /// afterwards.
        /// </summary>
        /// <remarks>
        /// The last part is the important one: an encoding that can be read but
        /// makes the HMAC invalid would not stand out at a mere comparison of
        /// the fields.
        /// </remarks>
        [Test]
        public void ARatchetMessage_SurvivesTheWire()
        {

            var sharedSecret    = RandomNumberGenerator.GetBytes(32);
            var bobsKey         = Curve25519.GenerateKeyPair();
            var associatedData  = Encoding.UTF8.GetBytes("AD");

            var alice = DoubleRatchet.InitiateAsSender(sharedSecret, bobsKey.PublicKey);
            var bob   = DoubleRatchet.InitiateAsReceiver(sharedSecret, bobsKey);

            var message = alice.Encrypt(Encoding.UTF8.GetBytes("through the wire"), associatedData);

            var wire   = OmemoWireFormat.Encode(message);
            var back   = OmemoWireFormat.Decode(wire);

            Assert.Multiple(() =>
            {

                Assert.That(back.Header.MessageNumber,       Is.EqualTo(message.Header.MessageNumber));
                Assert.That(back.Header.PreviousChainLength, Is.EqualTo(message.Header.PreviousChainLength));
                Assert.That(Hex(back.Header.DhPublicKey),    Is.EqualTo(Hex(message.Header.DhPublicKey)));
                Assert.That(Hex(back.Ciphertext),            Is.EqualTo(Hex(message.Ciphertext)));
                Assert.That(Hex(back.Mac),                   Is.EqualTo(Hex(message.Mac)));

                Assert.That(bob.Decrypt(back, associatedData),
                            Is.EqualTo(Encoding.UTF8.GetBytes("through the wire")));

            });

        }

        #endregion

        #region AMissingField_IsAnError()

        /// <summary>
        /// A missing mandatory field is a format error and no default value.
        /// </summary>
        /// <remarks>
        /// Protocol Buffers knows the zero for <c>uint32</c> and the empty
        /// field for <c>bytes</c>. Both could be inserted silently - the
        /// message would then look like the first of a chain with an empty
        /// ratchet key, could not be decrypted, and nobody would know that a
        /// field was missing.
        /// </remarks>
        [Test]
        public void AMissingField_IsAnError()
        {

            Assert.Multiple(() =>
            {

                // An authenticated message without a MAC.
                var withoutMac = new List<Byte>();
                Protobuf.WriteBytes(withoutMac, 2, Pattern(20));

                Assert.That(() => OmemoWireFormat.Decode([.. withoutMac]),
                            Throws.TypeOf<FormatException>(), "MAC missing");

                // A MAC of the wrong length - and that around an otherwise
                // flawless message.
                //
                // The earlier version packed random bytes in here as the inner
                // message. Those failed at the protobuf reading already, and
                // the test therefore passed even when the length check was
                // missing - it checked the wrong reason. The mutation removing
                // precisely this check survived it.
                var innerMessage = new RatchetHeader(Pattern(32), 0, 0).Encode(Pattern(16));

                var shortMac = new List<Byte>();
                Protobuf.WriteBytes(shortMac, 1, Pattern(8));
                Protobuf.WriteBytes(shortMac, 2, innerMessage);

                Assert.That(() => OmemoWireFormat.Decode([.. shortMac]),
                            Throws.TypeOf<FormatException>(), "MAC too short");

                // As a counter-check: with a 16 byte MAC the same message gets
                // through.
                var correct = new List<Byte>();
                Protobuf.WriteBytes(correct, 1, Pattern(16));
                Protobuf.WriteBytes(correct, 2, innerMessage);

                Assert.That(() => OmemoWireFormat.Decode([.. correct]),
                            Throws.Nothing, "The counter-check fails - then the test checks something else.");

                // A message without a ratchet key.
                var inner = new List<Byte>();
                Protobuf.WriteUInt32(inner, 1, 0);
                Protobuf.WriteUInt32(inner, 2, 0);
                Protobuf.WriteBytes (inner, 4, Pattern(16));

                var outer = new List<Byte>();
                Protobuf.WriteBytes(outer, 1, Pattern(16));
                Protobuf.WriteBytes(outer, 2, [.. inner]);

                Assert.That(() => OmemoWireFormat.Decode([.. outer]),
                            Throws.TypeOf<FormatException>(), "Ratchet key missing");

            });

        }

        #endregion

        #region TheKeyExchange_RoundTrips()

        /// <summary>
        /// <c>OMEMOKeyExchange.proto</c> - there and back, and the field
        /// numbers recalculated.
        /// </summary>
        [Test]
        public void TheKeyExchange_RoundTrips()
        {

            var exchange = new OmemoKeyExchange(31, 2, Pattern(32), Pattern(32, 50), Pattern(70, 90));

            var encoded = exchange.Encode();
            var decoded = OmemoKeyExchange.Decode(encoded);

            Assert.Multiple(() =>
            {

                Assert.That(decoded.PreKeyId,        Is.EqualTo(31u));
                Assert.That(decoded.SignedPreKeyId,  Is.EqualTo(2u));
                Assert.That(Hex(decoded.IdentityKey),   Is.EqualTo(Hex(exchange.IdentityKey)));
                Assert.That(Hex(decoded.EphemeralKey),  Is.EqualTo(Hex(exchange.EphemeralKey)));
                Assert.That(Hex(decoded.Message),       Is.EqualTo(Hex(exchange.Message)));

                // pk_id = 1, spk_id = 2, ik = 3, ek = 4, message = 5
                Assert.That(Hex(encoded), Does.StartWith("081f" + "1002" + "1a20"));

            });

        }

        #endregion

        #region TheEncryptedElement_RoundTrips()

        /// <summary>
        /// The <c>&lt;encrypted/&gt;</c> element: built, read, and the shape
        /// checked.
        /// </summary>
        [Test]
        public void TheEncryptedElement_RoundTrips()
        {

            var element = new OmemoEncryptedElement(
                              12345,
                              new Dictionary<JID, IReadOnlyList<OmemoKey>> {
                                  [JID.Parse("bob@example.org")]    = [new OmemoKey(1, Pattern(20), false),
                                                            new OmemoKey(2, Pattern(20, 40), true)],
                                  [JID.Parse("alice@example.org")]  = [new OmemoKey(9, Pattern(20, 80), false)]
                              },
                              Pattern(64));

            var xml = element.ToXml();

            // Pack it into a message, as it would be on the wire.
            var stanza = XElement.Parse(
                             $"<message xmlns='jabber:client' from='bob@example.org/x' " +
                             $"to='alice@example.org/y' type='chat'>{xml}</message>");

            Assert.That(OmemoEncryptedElement.TryRead(stanza, out var decoded), Is.True);

            Assert.Multiple(() =>
            {

                Assert.That(decoded!.SenderDeviceId, Is.EqualTo(12345u));
                Assert.That(decoded.Keys, Has.Count.EqualTo(2));

                Assert.That(Hex(decoded.Payload!), Is.EqualTo(Hex(Pattern(64))));

                var forBob2 = decoded.KeyFor(JID.Parse("bob@example.org"), 2);
                Assert.That(forBob2,                Is.Not.Null);
                Assert.That(forBob2!.IsKeyExchange, Is.True);
                Assert.That(Hex(forBob2.Data),      Is.EqualTo(Hex(Pattern(20, 40))));

                var forBob1 = decoded.KeyFor(JID.Parse("bob@example.org"), 1);
                Assert.That(forBob1!.IsKeyExchange, Is.False,
                            "Without a kex attribute 'false' holds (section 4.5).");

                // The device id alone is not enough - it belongs to a JID.
                // Device 1 exists at Bob's, not at Alice's.
                Assert.That(decoded.KeyFor(JID.Parse("alice@example.org"), 1), Is.Null);
                Assert.That(decoded.KeyFor(JID.Parse("carol@example.org"), 1), Is.Null);

                // The default value does not stand in the stanza.
                //
                // Here stood a search for the character sequence "kex='false'"
                // in the emitted XML - and that could never hold:
                // XElement.ToString writes attributes with double quotation
                // marks. The test therefore always passed, even when the
                // mutation wrote the default value out. What is asked now is
                // the attribute itself.
                XNamespace ns = OmemoEncryptedElement.Namespace;

                var withoutKex = xml.Descendants(ns + "key")
                                 .Where(k => k.Attr("rid") is "1" or "9")
                                 .ToList();

                Assert.That(withoutKex, Has.Count.EqualTo(2), "The keys without a kex are missing.");

                foreach (var k in withoutKex)
                    Assert.That(k.Attribute("kex"), Is.Null,
                                $"At rid={k.Attr("rid")} a written-out default value stands.");

            });

        }

        #endregion

        #region AMessageWithoutPayload_IsValid()

        /// <summary>
        /// A message without a <c>&lt;payload/&gt;</c> is not a broken one but
        /// one without content.
        /// </summary>
        /// <remarks>
        /// It means "I have built the session up anew" and carries only the key
        /// exchange. That way a far end gets a session without a human being
        /// having to write anything.
        /// </remarks>
        [Test]
        public void AMessageWithoutPayload_IsValid()
        {

            var element = new OmemoEncryptedElement(
                              7,
                              new Dictionary<JID, IReadOnlyList<OmemoKey>> {
                                  [JID.Parse("bob@example.org")] = [new OmemoKey(1, Pattern(20), true)]
                              },
                              null);

            var stanza = XElement.Parse(
                             $"<message xmlns='jabber:client' from='a@b/c' type='chat'>{element.ToXml()}</message>");

            Assert.That(OmemoEncryptedElement.TryRead(stanza, out var decoded), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(decoded!.Payload, Is.Null);
                Assert.That(decoded.KeyFor(JID.Parse("bob@example.org"), 1)!.IsKeyExchange, Is.True);
            });

        }

        #endregion

        #region AnEncryptedElementInsideACarbon_IsNotTheOuterOne()

        /// <summary>
        /// The encryption of a packed-in message does not belong to the outer
        /// one.
        /// </summary>
        /// <remarks>
        /// The same trap as with the delay stamp (D59) and with the correction
        /// note (D60): a carbon brings a complete message of its own along in
        /// its <c>&lt;forwarded/&gt;</c>. Whoever searches the whole stanza
        /// takes the outer one for encrypted and decrypts a payload belonging
        /// to a different session.
        /// </remarks>
        [Test]
        public void AnEncryptedElementInsideACarbon_IsNotTheOuterOne()
        {

            var inner = new OmemoEncryptedElement(
                            7,
                            new Dictionary<JID, IReadOnlyList<OmemoKey>> {
                                [JID.Parse("bob@example.org")] = [new OmemoKey(1, Pattern(20), false)]
                            },
                            Pattern(32)).ToXml();

            var carbon = XElement.Parse(
                             "<message xmlns='jabber:client' from='alice@example.org' type='chat'>" +
                             "<received xmlns='urn:xmpp:carbons:2'>" +
                             "<forwarded xmlns='urn:xmpp:forward:0'>" +
                             $"<message xmlns='jabber:client'>{inner}</message>" +
                             "</forwarded></received></message>");

            Assert.That(OmemoEncryptedElement.TryRead(carbon, out _), Is.False,
                        "The outer message counts as encrypted.");

        }

        #endregion

        #region ABrokenElement_IsRefusedWithoutThrowing()

        /// <summary>
        /// What cannot be read yields <c>false</c> - and no exception.
        /// </summary>
        /// <remarks>
        /// An incomprehensible message is the same as none for the recipient. A
        /// crash would be the worse answer: it could be triggered by anyone
        /// sending a <c>&lt;key/&gt;</c> with crooked base64.
        /// </remarks>
        [Test]
        public void ABrokenElement_IsRefusedWithoutThrowing()
        {

            String[] broken = [

                // No device id
                "<encrypted xmlns='urn:xmpp:omemo:2'><header/></encrypted>",

                // The id is no number
                "<encrypted xmlns='urn:xmpp:omemo:2'><header sid='not-a-number'/></encrypted>",

                // Crooked base64 in the key
                "<encrypted xmlns='urn:xmpp:omemo:2'><header sid='1'>" +
                "<keys jid='bob@example.org'><key rid='1'>!!!not base64!!!</key></keys>" +
                "</header></encrypted>",

                // A key without a rid
                "<encrypted xmlns='urn:xmpp:omemo:2'><header sid='1'>" +
                "<keys jid='bob@example.org'><key>AAAA</key></keys></header></encrypted>",

                // Keys without a jid
                "<encrypted xmlns='urn:xmpp:omemo:2'><header sid='1'>" +
                "<keys><key rid='1'>AAAA</key></keys></header></encrypted>",

                // Crooked base64 in the payload
                "<encrypted xmlns='urn:xmpp:omemo:2'><header sid='1'/>" +
                "<payload>!!!</payload></encrypted>"

            ];

            Assert.Multiple(() =>
            {

                foreach (var text in broken)
                {

                    var stanza = XElement.Parse(
                                     $"<message xmlns='jabber:client' from='a@b/c'>{text}</message>");

                    Assert.That(OmemoEncryptedElement.TryRead(stanza, out _), Is.False, text);

                }

            });

        }

        #endregion

        #region TheEnvelope_CarriesContentAndAffixes()

        /// <summary>
        /// The SCE envelope (XEP-0420): content, sender, time and padding.
        /// </summary>
        [Test]
        public void TheEnvelope_CarriesContentAndAffixes()
        {

            XNamespace client = "jabber:client";

            var envelope = new SceEnvelope([new XElement(client + "body", "Shall we meet at eight?")],
                                           From: "alice@example.org",
                                         Time: new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));

            var xml = envelope.ToXml();

            Assert.Multiple(() =>
            {

                Assert.That(xml.Child(SceEnvelope.Namespace, "content"), Is.Not.Null);
                Assert.That(xml.Child(SceEnvelope.Namespace, "rpad"),    Is.Not.Null,
                            "XEP-0420 demands the padding.");

                Assert.That(xml.Child(SceEnvelope.Namespace, "time")?.Attr("stamp"),
                            Is.EqualTo("2026-08-01T12:00:00Z"));

                Assert.That(SceEnvelope.TryRead(xml, out var decoded, JID.Parse("alice@example.org/mobile")),
                            Is.True,
                            "The sender from the stanza belongs to the same human being.");

                Assert.That(decoded!.Content.Single().Value, Is.EqualTo("Shall we meet at eight?"));
                Assert.That(decoded.From, Is.EqualTo("alice@example.org"));

            });

        }

        #endregion

        #region AForwardedEnvelope_IsRefused()

        /// <summary>
        /// If the envelope names a different sender than the stanza, it is
        /// turned away.
        /// </summary>
        /// <remarks>
        /// <b>That is the attack the associated data stands against:</b>
        /// somebody catches a ciphertext and passes it on under their own name.
        /// The encryption stays valid - it was not touched after all -, and
        /// without this comparison the recipient would see a message that was
        /// never addressed to them, with a sender who never wrote it.
        /// </remarks>
        [Test]
        public void AForwardedEnvelope_IsRefused()
        {

            XNamespace client = "jabber:client";

            var envelope = new SceEnvelope([new XElement(client + "body", "confidential")],
                                           From: "alice@example.org").ToXml();

            Assert.Multiple(() =>
            {

                Assert.That(SceEnvelope.TryRead(envelope, out _, JID.Parse("mallory@example.org/x")), Is.False,
                            "An envelope passed on was accepted.");

                // Without an expectation no comparison happens - the caller
                // then knows themselves what they are doing.
                Assert.That(SceEnvelope.TryRead(envelope, out _), Is.True);

            });

        }

        #endregion

        #region ThePadding_IsRandomEveryTime()

        /// <summary>
        /// The padding is a different one at every call.
        /// </summary>
        /// <remarks>
        /// Without it the length of the ciphertext would give the length of the
        /// message away - with "yes" and "no" that is the whole content. Were
        /// it the same for the same content, two equal messages would be
        /// equally long again, and the measure would be without effect exactly
        /// as far as it was intended.
        /// </remarks>
        [Test]
        public void ThePadding_IsRandomEveryTime()
        {

            XNamespace client = "jabber:client";

            var envelope = new SceEnvelope([new XElement(client + "body", "yes")]);

            var lengths = new HashSet<Int32>();

            for (var i = 0; i < 30; i++)
                lengths.Add(envelope.ToXml().Child(SceEnvelope.Namespace, "rpad")!.Value.Length);

            Assert.Multiple(() =>
            {

                Assert.That(lengths, Has.Count.GreaterThan(1),
                            "The padding always has the same length.");

                Assert.That(lengths.Max(), Is.LessThanOrEqualTo(SceEnvelope.MaxPadding));

            });

        }

        #endregion

    }

}
