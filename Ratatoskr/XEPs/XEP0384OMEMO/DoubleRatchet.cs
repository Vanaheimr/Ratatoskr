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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// The header of a ratchet message (XEP-0384, <c>OMEMOMessage.proto</c>).
/// </summary>
/// <param name="DhPublicKey">The current public ratchet key of the sender.</param>
/// <param name="PreviousChainLength">How long the previous send chain was (<c>pn</c>).</param>
/// <param name="MessageNumber">The running number in the current chain (<c>n</c>).</param>
public sealed record RatchetHeader(Byte[] DhPublicKey,
                                   UInt32 PreviousChainLength,
                                   UInt32 MessageNumber)
{

    /// <summary>
    /// The header as <c>OMEMOMessage.proto</c> without ciphertext - exactly
    /// that way it goes into the associated data of the encryption
    /// (section 4.3).
    /// </summary>
    /// <remarks>
    /// Without the ciphertext, and it says so expressly: "the
    /// OMEMOMessage.proto initializes without the ciphertext, which is
    /// optional". It could not be otherwise either - the associated data is
    /// needed in order to produce the ciphertext in the first place.
    ///
    /// The field numbers come from the schema of the specification:
    /// <c>n = 1</c>, <c>pn = 2</c>, <c>dh_pub = 3</c>, <c>ciphertext = 4</c>.
    /// Always all three, always in this order: both sides have to form the same
    /// bytes from the same header, otherwise the check founders on the encoding
    /// instead of on the content.
    /// </remarks>
    public Byte[] Encode()
        => Encode(null);

    /// <summary>
    /// The header as <c>OMEMOMessage.proto</c>, optionally with ciphertext.
    /// </summary>
    /// <remarks>
    /// <b>Precisely these bytes are what the HMAC checks</b> - "the HMAC is
    /// computed over <c>ad ‖ OMEMOMessage.proto</c> (after ciphertext is added
    /// to the proto)". The ciphertext therefore stands in it as field 4 <b>with
    /// an identifier and a length</b> and not simply appended at the back.
    ///
    /// The difference is three bytes, and it slipped through in D64: there the
    /// ciphertext was appended raw. Both sides did the same thing, all tests
    /// stayed green - and against a foreign client not a single checksum would
    /// have been right. The same family of errors as the info string in D62,
    /// the associated data in D63 and the root chain in D64.
    /// </remarks>
    public Byte[] Encode(Byte[]? ciphertext)
    {

        var bytes = new List<Byte>();

        Protobuf.WriteUInt32 (bytes, 1, MessageNumber);
        Protobuf.WriteUInt32 (bytes, 2, PreviousChainLength);
        Protobuf.WriteBytes  (bytes, 3, DhPublicKey);

        if (ciphertext is not null)
            Protobuf.WriteBytes(bytes, 4, ciphertext);

        return [.. bytes];

    }

}

/// <summary>
/// An encrypted ratchet message: header, ciphertext and the HMAC truncated to
/// 16 bytes.
/// </summary>
/// <remarks>
/// The three parts stand separately because they stand separately on the wire:
/// header and ciphertext together form the <c>OMEMOMessage</c>, the HMAC
/// encloses them as the <c>OMEMOAuthenticatedMessage</c>.
/// </remarks>
public sealed record RatchetMessage(RatchetHeader Header, Byte[] Ciphertext, Byte[] Mac);

/// <summary>
/// A message key set aside, as it outlasts a restart.
/// </summary>
public sealed record SkippedMessageKey(String RatchetKey, UInt32 Number, Byte[] MessageKey);

/// <summary>
/// The complete state of a ratchet session.
/// </summary>
/// <remarks>
/// <b>Complete means: what is missing here is lost after a restart</b> - and in
/// such a way that the counterpart does not learn of it. If one's own ratchet
/// key were missing, nothing still under way could be decrypted any more; if
/// the keys set aside were missing, it would be the overtaken messages; if the
/// counters were missing, the chain would stand at the wrong place.
///
/// The secret part of the ratchet key is a key like any other in this: whoever
/// reads the stored session reads the conversation along.
/// </remarks>
public sealed record RatchetState(Byte[]?                            OwnRatchetPrivateKey,
                                  Byte[]?                            RemoteRatchetKey,
                                  Byte[]                             RootKey,
                                  Byte[]?                            SendChain,
                                  Byte[]?                            ReceiveChain,
                                  UInt32                             SendCount,
                                  UInt32                             ReceiveCount,
                                  UInt32                             PreviousSendCount,
                                  IReadOnlyList<SkippedMessageKey>   SkippedKeys);

/// <summary>
/// The double ratchet per XEP-0384, section 4.3.
/// </summary>
/// <remarks>
/// <b>Two ratchets, and they do different things.</b>
///
/// The <i>symmetric</i> ratchet runs with every message: out of the chain key
/// come a message key and a new chain key, and the old one is forgotten. That
/// gives <b>forward secrecy</b> - whoever steals today's state can no longer
/// read yesterday's messages, because their keys do not exist any more.
///
/// The <i>Diffie-Hellman</i> ratchet runs as soon as the counterpart sends a
/// new public key along: both sides compute a fresh shared value and begin new
/// chains. That gives <b>break-in recovery</b> - whoever has stolen the state
/// loses it again as soon as the two have written in both directions once.
///
/// <b>Together they yield the property this is about:</b> a state read along is
/// useful neither backwards nor lastingly forwards. Precisely for that reason
/// errors here are silent - a ratchet that does not run on goes on encrypting
/// flawlessly. It only does so again and again with the same key.
/// </remarks>
public sealed class DoubleRatchet
{

    #region Data

    /// <summary>The info string of the root chain (section 4.3).</summary>
    public const String RootChainInfo    = "OMEMO Root Chain";

    /// <summary>The info string for the material of a message key.</summary>
    public const String MessageKeyInfo   = "OMEMO Message Key Material";

    /// <summary>
    /// How many skipped message keys a session keeps.
    /// </summary>
    /// <remarks>
    /// The specification recommends a thousand. The number is a balance between
    /// two evils: too few, and a message that was under way for a day cannot be
    /// read any more. Too many - or no limit at all -, and an attacker sends a
    /// single message with <c>n = 4000000000</c> and the recipient computes four
    /// billion keys before noticing that it is not right.
    /// </remarks>
    public const Int32 MaxSkip = 1000;

    private readonly Dictionary<(String Dh, UInt32 N), Byte[]> _skipped = [];
    private readonly Lock _lock = new();

    private Curve25519KeyPair?  _ownRatchet;
    private Byte[]?             _remoteRatchet;
    private Byte[]              _root;
    private Byte[]?             _sendChain;
    private Byte[]?             _receiveChain;

    #endregion

    #region Properties

    /// <summary>The running number of the next message sent.</summary>
    public UInt32 SendCount { get; private set; }

    /// <summary>How many messages have been received in the current chain.</summary>
    public UInt32 ReceiveCount { get; private set; }

    /// <summary>The length of the previous send chain.</summary>
    public UInt32 PreviousSendCount { get; private set; }

    /// <summary>How many skipped keys are being kept just now.</summary>
    public Int32 SkippedKeys
    {
        get { lock (_lock) return _skipped.Count; }
    }

    /// <summary>Can this session send yet?</summary>
    /// <remarks>
    /// The party called can do so only after it has got the first message:
    /// before that it does not know the ratchet key of the counterpart and has
    /// nothing it could form a send chain from.
    /// </remarks>
    public Boolean CanSend => _sendChain is not null;

    #endregion

    #region Constructor(s)

    private DoubleRatchet(Byte[] root)
    {
        _root = root;
    }

    #endregion

    #region InitiateAsSender / InitiateAsReceiver

    /// <summary>
    /// The calling side: it knows the ratchet key of the counterpart from their
    /// bundle and can send at once.
    /// </summary>
    /// <param name="sharedSecret">The result of X3DH.</param>
    /// <param name="theirRatchetKey">
    /// The signed prekey of the counterpart - it is at the same time their first
    /// ratchet key.
    /// </param>
    public static DoubleRatchet InitiateAsSender(Byte[] sharedSecret, Byte[] theirRatchetKey)
    {

        var ratchet = new DoubleRatchet(sharedSecret)
        {
            _ownRatchet     = Curve25519.GenerateKeyPair(),
            _remoteRatchet  = theirRatchetKey
        };

        (ratchet._root, ratchet._sendChain) =
            ratchet.AdvanceRootChain(Curve25519.Agree(ratchet._ownRatchet.PrivateKey, theirRatchetKey));

        return ratchet;

    }

    /// <summary>
    /// The side called: it has only the shared secret and its own signed
    /// prekey.
    /// </summary>
    /// <remarks>
    /// Nothing is derived here yet. The root <b>is</b> the shared secret, and
    /// the chains come into being only when the first message arrives - it
    /// brings the ratchet key of the counterpart along. Whoever formed a send
    /// chain here already would have one the counterpart does not know.
    /// </remarks>
    public static DoubleRatchet InitiateAsReceiver(Byte[] sharedSecret, Curve25519KeyPair ownRatchetKey)
        => new(sharedSecret)
           {
               _ownRatchet = ownRatchetKey
           };

    #endregion

    #region Export() / Import(state)

    /// <summary>
    /// The state of this session as it is stored.
    /// </summary>
    public RatchetState Export()
    {

        lock (_lock)
            return new RatchetState(_ownRatchet?.PrivateKey,
                                    _remoteRatchet,
                                    _root,
                                    _sendChain,
                                    _receiveChain,
                                    SendCount,
                                    ReceiveCount,
                                    PreviousSendCount,
                                    [.. _skipped.Select(e => new SkippedMessageKey(e.Key.Dh,
                                                                                          e.Key.N,
                                                                                          e.Value))]);

    }

    /// <summary>
    /// Restores a stored session.
    /// </summary>
    /// <remarks>
    /// <b>Restored and not begun anew</b> - the difference is the whole stage. A
    /// newly begun session would have a different root key, and the counterpart
    /// could no longer read anything that comes from here. It would see no error
    /// in the process, only messages that do not pass their checksum - that is,
    /// something that looks like an attack.
    /// </remarks>
    public static DoubleRatchet Import(RatchetState state)
    {

        var ratchet = new DoubleRatchet(state.RootKey)
        {
            _ownRatchet        = state.OwnRatchetPrivateKey is not null
                                     ? Curve25519.KeyPairFromPrivate(state.OwnRatchetPrivateKey)
                                     : null,
            _remoteRatchet     = state.RemoteRatchetKey,
            _sendChain         = state.SendChain,
            _receiveChain      = state.ReceiveChain,
            SendCount          = state.SendCount,
            ReceiveCount       = state.ReceiveCount,
            PreviousSendCount  = state.PreviousSendCount
        };

        foreach (var k in state.SkippedKeys)
            ratchet._skipped[(k.RatchetKey, k.Number)] = k.MessageKey;

        return ratchet;

    }

    #endregion

    #region Encrypt(plaintext, associatedData)

    /// <summary>
    /// Encrypts a message and pushes the symmetric ratchet one step on.
    /// </summary>
    /// <param name="plaintext">
    /// With OMEMO the 48 bytes of key and HMAC of the payload.
    /// </param>
    /// <param name="associatedData">
    /// The associated data from X3DH - both identity keys. The header of this
    /// message is appended here, not by the caller.
    /// </param>
    public RatchetMessage Encrypt(Byte[] plaintext, Byte[] associatedData)
    {

        lock (_lock)
        {

            if (_sendChain is null)
                throw new InvalidOperationException(
                          "This session cannot send yet - the ratchet key of the counterpart is " +
                          "unknown as long as nothing has come from them.");

            var (messageKey, next) = AdvanceChain(_sendChain);
            _sendChain = next;

            var header = new RatchetHeader(_ownRatchet!.PublicKey, PreviousSendCount, SendCount);

            SendCount++;

            var (ciphertext, mac) = Seal(messageKey, plaintext, associatedData, header);

            return new RatchetMessage(header, ciphertext, mac);

        }

    }

    #endregion

    #region Decrypt(message, associatedData)

    /// <summary>
    /// Decrypts a message - even when it arrives too early, too late or not in
    /// the current chain at all any more.
    /// </summary>
    /// <remarks>
    /// <b>The order of the three cases is the whole difficulty.</b> First it is
    /// looked up whether this is a message whose key has already been set aside
    /// - it came late, and the chains have long moved on. Then whether the
    /// counterpart brings a new ratchet key along; in that case the old receive
    /// chain is computed out to its end and set aside before the new one begins.
    /// Only then is the current chain wound forward.
    ///
    /// Whoever swaps the order loses messages that are still under way - and
    /// irretrievably at that, for their keys are then already forgotten.
    /// </remarks>
    public Byte[] Decrypt(RatchetMessage message, Byte[] associatedData)
    {

        lock (_lock)
        {

            // 1. A key set aside?
            var key = (message.Header.DhPublicKey, message.Header.MessageNumber);
            var id    = (Convert.ToHexString(key.DhPublicKey), key.MessageNumber);

            if (_skipped.TryGetValue(id, out var setAside))
            {

                var plaintext = Open(setAside, message, associatedData);

                // Remove only after the successful check. If the decryption
                // throws, it was not the expected message - and an attacker
                // would otherwise have deleted the key of the real one with a
                // forged one.
                _skipped.Remove(id);

                return plaintext;

            }

            // 2. A new ratchet key of the counterpart?
            if (_remoteRatchet is null ||
                !message.Header.DhPublicKey.SequenceEqual(_remoteRatchet))
            {
                SkipTo(message.Header.PreviousChainLength);
                TurnDhRatchet(message.Header.DhPublicKey);
            }

            // 3. Wind forward in the current chain.
            SkipTo(message.Header.MessageNumber);

            var (mk, next) = AdvanceChain(_receiveChain!);
            _receiveChain = next;

            ReceiveCount++;

            return Open(mk, message, associatedData);

        }

    }

    #endregion

    #region The two ratchets

    /// <summary>
    /// The root chain: out of the old root key and a Diffie-Hellman result come
    /// a new root key and a chain key.
    /// </summary>
    /// <remarks>
    /// The root key is the <b>salt</b> and the Diffie-Hellman value the input
    /// material - that way round it stands in section 4.3, and the swap would
    /// not be noticeable: both sides would still agree, only a foreign
    /// counterpart would not.
    /// </remarks>
    private (Byte[] Root, Byte[] Chain) AdvanceRootChain(Byte[] dhOutput)
        => DeriveRootChain(_root, dhOutput);

    /// <summary>
    /// The same derivation without state - so that a test can hold it against
    /// the prescription.
    /// </summary>
    /// <remarks>
    /// <b>Not pulled out for convenience.</b> While this was still a private
    /// method, four mutations survived: salt and input material swapped, info
    /// string gone, both halves out of the same one. None of them showed,
    /// <b>because both sides use the same function and went on agreeing</b> -
    /// and two of them are not merely questions of interoperability but
    /// abolitions of the security: if root key and chain key were the same
    /// bytes, the root and with it the whole session could be rolled up out of a
    /// single message read along.
    ///
    /// That only becomes checkable when the computation can be got at on its
    /// own.
    /// </remarks>
    internal static (Byte[] Root, Byte[] Chain) DeriveRootChain(Byte[] rootKey, Byte[] dhOutput)
    {

        var material = HKDF.DeriveKey(HashAlgorithmName.SHA256,
                                      ikm:           dhOutput,
                                      salt:          rootKey,
                                      info:          Encoding.UTF8.GetBytes(RootChainInfo),
                                      outputLength:  64);

        return (material[..32], material[32..]);

    }

    /// <summary>
    /// The symmetric chain: <c>HMAC(ck, 0x01)</c> is the message key,
    /// <c>HMAC(ck, 0x02)</c> the next chain key.
    /// </summary>
    /// <remarks>
    /// Two different constants, and that is the heart of it: were they the same,
    /// the message key would at the same time be the next chain key - and
    /// whoever reads along a single message could compute the whole rest of the
    /// chain. Forward secrecy would thereby become its opposite.
    /// </remarks>
    internal static (Byte[] MessageKey, Byte[] NextChain) AdvanceChain(Byte[] chainKey)
        => (HMACSHA256.HashData(chainKey, new Byte[] { 0x01 }),
            HMACSHA256.HashData(chainKey, new Byte[] { 0x02 }));

    /// <summary>
    /// The Diffie-Hellman ratchet: new foreign key, two steps of the root chain,
    /// a fresh key pair of one's own.
    /// </summary>
    /// <remarks>
    /// Two steps, because two chains come into being: first the receive chain
    /// out of the old own key against the new foreign one, then the send chain
    /// out of the new own one against the same foreign one. The intermediate
    /// state of the root goes into both in the process - that is why both sides
    /// agree although each forms its chains in the reverse order.
    /// </remarks>
    private void TurnDhRatchet(Byte[] theirRatchetKey)
    {

        PreviousSendCount  = SendCount;
        SendCount          = 0;
        ReceiveCount       = 0;
        _remoteRatchet     = theirRatchetKey;

        (_root, _receiveChain) = AdvanceRootChain(
                                        Curve25519.Agree(_ownRatchet!.PrivateKey, theirRatchetKey));

        _ownRatchet = Curve25519.GenerateKeyPair();

        (_root, _sendChain) = AdvanceRootChain(
                                     Curve25519.Agree(_ownRatchet.PrivateKey, theirRatchetKey));

    }

    /// <summary>
    /// Winds the receive chain forward to the number named and sets aside every
    /// key coming into being in the process.
    /// </summary>
    /// <remarks>
    /// The upper limit is no convenience but a defence: without it <b>a
    /// single</b> message with a very large number suffices, and the recipient
    /// computes billions of keys before noticing that it is not right. That is
    /// why the check stands <b>before</b> the loop and not inside it.
    /// </remarks>
    private void SkipTo(UInt32 until)
    {

        if (_receiveChain is null)
            return;

        if (until < ReceiveCount)
            return;

        if (until - ReceiveCount > MaxSkip)
            throw new CryptographicException(
                      $"The message skips {until - ReceiveCount} keys; permitted are " +
                      $"{MaxSkip}. A single message must not trigger an unbounded computation.");

        while (ReceiveCount < until)
        {

            var (mk, next) = AdvanceChain(_receiveChain);

            _skipped[(Convert.ToHexString(_remoteRatchet!), ReceiveCount)] = mk;

            _receiveChain = next;
            ReceiveCount++;

        }

    }

    #endregion

    #region The encryption of a single message

    /// <summary>
    /// AES-256-CBC with HMAC-SHA-256, derived from the message key.
    /// </summary>
    internal static (Byte[] Key, Byte[] AuthKey, Byte[] Iv) Material(Byte[] messageKey)
    {

        var material = HKDF.DeriveKey(HashAlgorithmName.SHA256,
                                      ikm:           messageKey,
                                      salt:          new Byte[32],
                                      info:          Encoding.UTF8.GetBytes(MessageKeyInfo),
                                      outputLength:  80);

        return (material[..32], material[32..64], material[64..]);

    }

    /// <summary>
    /// Encrypts and appends the HMAC truncated to 16 bytes.
    /// </summary>
    /// <remarks>
    /// The HMAC runs over <b>associated data and ciphertext</b>. The associated
    /// data contains the two identity keys and the header of this message - with
    /// that it is checked along who is speaking with whom and at which place of
    /// the chain this message stands. Without it a valid message could be moved
    /// into another session or to another place of the chain.
    /// </remarks>
    private static (Byte[] Ciphertext, Byte[] Mac) Seal(Byte[]         messageKey,
                                                        Byte[]         plaintext,
                                                        Byte[]         associatedData,
                                                        RatchetHeader  header)
    {

        var (key, authKey, iv) = Material(messageKey);

        using var aes = Aes.Create();
        aes.Key = key;

        var ciphertext = aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);

        return (ciphertext, Mac(authKey, associatedData, header, ciphertext));

    }

    /// <summary>Checks the HMAC and decrypts.</summary>
    private static Byte[] Open(Byte[] messageKey, RatchetMessage message, Byte[] associatedData)
    {

        var (key, authKey, iv) = Material(messageKey);

        if (!CryptographicOperations.FixedTimeEquals(
                 Mac(authKey, associatedData, message.Header, message.Ciphertext),
                 message.Mac))
            throw new CryptographicException(
                      "The HMAC of the ratchet message is not right - it was altered, belongs to " +
                      "another session or stands at another place of the chain.");

        using var aes = Aes.Create();
        aes.Key = key;

        return aes.DecryptCbc(message.Ciphertext, iv, PaddingMode.PKCS7);

    }

    /// <summary>
    /// The truncated HMAC over <c>ad ‖ OMEMOMessage.proto</c> - with the
    /// ciphertext <b>in</b> the protobuf and not behind it.
    /// </summary>
    /// <remarks>
    /// The associated data thereby contains everything that makes up this
    /// message: the two identity keys from X3DH, the place in the chain and the
    /// ciphertext itself. Without the header a valid message could be moved to
    /// another place of the chain, without the identity keys into a foreign
    /// session.
    /// </remarks>
    internal static Byte[] Mac(Byte[] authKey, Byte[] associatedData, RatchetHeader header, Byte[] ciphertext)
    {

        Byte[] covered = [.. associatedData, .. header.Encode(ciphertext)];

        return HMACSHA256.HashData(authKey, covered)[..16];

    }

    #endregion

}
