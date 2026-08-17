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
/// The key exchange at the beginning of a session
/// (<c>OMEMOKeyExchange.proto</c>).
/// </summary>
/// <param name="PreKeyId">Which prekey of the recipient was used.</param>
/// <param name="SignedPreKeyId">Which signed prekey.</param>
/// <param name="IdentityKey">One's own identity key, in Ed25519 form.</param>
/// <param name="EphemeralKey">The one-time key from X3DH.</param>
/// <param name="Message">The packed <c>OMEMOAuthenticatedMessage</c>.</param>
/// <remarks>
/// <b>It travels along with every message until the other side has
/// answered</b>, and not only with the first. The reason is unpleasantly
/// simple: the first message can get lost. If the exchange came only once, the
/// second message would stand before a counterpart that knows no session - and
/// would not be readable without anybody learning why.
/// </remarks>
public sealed record OmemoKeyExchange(UInt32  PreKeyId,
                                      UInt32  SignedPreKeyId,
                                      Byte[]  IdentityKey,
                                      Byte[]  EphemeralKey,
                                      Byte[]  Message)
{

    /// <summary>
    /// The encoding per the schema of the specification.
    /// </summary>
    public Byte[] Encode()
    {

        var bytes = new List<Byte>();

        Protobuf.WriteUInt32 (bytes, 1, PreKeyId);
        Protobuf.WriteUInt32 (bytes, 2, SignedPreKeyId);
        Protobuf.WriteBytes  (bytes, 3, IdentityKey);
        Protobuf.WriteBytes  (bytes, 4, EphemeralKey);
        Protobuf.WriteBytes  (bytes, 5, Message);

        return [.. bytes];

    }

    /// <summary>
    /// Reads a key exchange.
    /// </summary>
    public static OmemoKeyExchange Decode(Byte[] data)
    {

        UInt32  pk = 0, spk = 0;
        Byte[]  ik = [], ek = [], msg = [];

        foreach (var (field, _, number, content) in Protobuf.Read(data))
            switch (field)
            {
                case 1: pk   = (UInt32) number;  break;
                case 2: spk  = (UInt32) number;  break;
                case 3: ik   = content;          break;
                case 4: ek   = content;          break;
                case 5: msg  = content;          break;
            }

        if (ik.Length == 0 || ek.Length == 0 || msg.Length == 0)
            throw new FormatException("The key exchange is missing a mandatory field.");

        return new OmemoKeyExchange(pk, spk, ik, ek, msg);

    }

}

/// <summary>
/// The authenticated message (<c>OMEMOAuthenticatedMessage.proto</c>): the
/// truncated HMAC and the packed <c>OMEMOMessage</c>.
/// </summary>
/// <remarks>
/// <b>Why the HMAC does not stand in the message itself.</b> It is computed
/// over the encoded message - if it stood in it, it would check itself along
/// with it. Hence a shell: inside the message, outside its authentication.
/// </remarks>
public sealed record OmemoAuthenticatedMessage(Byte[] Mac, Byte[] Message)
{

    /// <summary>
    /// The encoding per the schema of the specification.
    /// </summary>
    public Byte[] Encode()
    {

        var bytes = new List<Byte>();

        Protobuf.WriteBytes(bytes, 1, Mac);
        Protobuf.WriteBytes(bytes, 2, Message);

        return [.. bytes];

    }

    /// <summary>
    /// Reads an authenticated message.
    /// </summary>
    public static OmemoAuthenticatedMessage Decode(Byte[] data)
    {

        Byte[] mac = [], msg = [];

        foreach (var (field, _, _, content) in Protobuf.Read(data))
            switch (field)
            {
                case 1: mac  = content;  break;
                case 2: msg  = content;  break;
            }

        if (mac.Length != 16)
            throw new FormatException(
                      $"The HMAC has {mac.Length} bytes instead of 16. Section 4.3 truncates it to 16 - " +
                      "another length is no message of this procedure.");

        if (msg.Length == 0)
            throw new FormatException("The authenticated message is empty.");

        return new OmemoAuthenticatedMessage(mac, msg);

    }

}

/// <summary>
/// The conversion between a <see cref="RatchetMessage"/> and its shape on the
/// wire.
/// </summary>
public static class OmemoWireFormat
{

    #region RatchetMessage <-> OMEMOAuthenticatedMessage

    /// <summary>
    /// Packs a ratchet message into an <c>OMEMOAuthenticatedMessage</c>.
    /// </summary>
    public static Byte[] Encode(RatchetMessage message)
        => new OmemoAuthenticatedMessage(
               message.Mac,
               message.Header.Encode(message.Ciphertext)).Encode();

    /// <summary>
    /// Reads an <c>OMEMOAuthenticatedMessage</c> back into a ratchet message.
    /// </summary>
    /// <remarks>
    /// <b>A missing mandatory field is a format error and not a default
    /// value.</b> Protocol Buffers knows the zero for <c>uint32</c> and the
    /// empty field for <c>bytes</c>, and both could be inserted here silently -
    /// the message would then look like the first of a chain with an empty
    /// ratchet key. It could not be decrypted, and nobody would know that a
    /// field was missing.
    /// </remarks>
    public static RatchetMessage Decode(Byte[] data)
    {

        var authenticated = OmemoAuthenticatedMessage.Decode(data);

        UInt32  n = 0, pn = 0;
        Byte[]  dh = [], ciphertext = [];
        var     hasN = false;
        var     hasPn = false;

        foreach (var (field, _, number, content) in Protobuf.Read(authenticated.Message))
            switch (field)
            {
                case 1: n           = (UInt32) number;  hasN  = true;  break;
                case 2: pn          = (UInt32) number;  hasPn = true;  break;
                case 3: dh          = content;                         break;
                case 4: ciphertext  = content;                         break;
            }

        if (!hasN || !hasPn || dh.Length != Curve25519.KeyLength || ciphertext.Length == 0)
            throw new FormatException(
                      "The OMEMOMessage is missing a mandatory field, or its ratchet key has the " +
                      "wrong length.");

        return new RatchetMessage(new RatchetHeader(dh, pn, n), ciphertext, authenticated.Mac);

    }

    #endregion

}
