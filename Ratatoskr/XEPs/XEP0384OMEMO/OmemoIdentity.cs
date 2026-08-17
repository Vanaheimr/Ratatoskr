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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// The own key material of a device: identity key, signed prekey and the
/// prekeys usable once.
/// </summary>
/// <remarks>
/// <b>The three kinds of key differ in their lifetime, and precisely on that
/// hangs what they protect.</b>
///
/// The <b>identity key</b> lives as long as the device; its fingerprint is what
/// a human being compares. It is never exchanged - if it were, all previous
/// comparisons would be worthless.
///
/// The <b>signed prekey</b> is rotated regularly. It is the reason why a stolen
/// key does not retroactively open everything: whoever has today's does not get
/// at the sessions of the week before last, because the key from back then does
/// not exist any more. That is why the superseded one is kept for a while and
/// then <b>really</b> forgotten - a kept old key takes back precisely the
/// property the rotation exists for.
///
/// The <b>prekeys</b> hold once. They see to it that two first messages to the
/// same device do not yield the same session key. If the stock runs low, a
/// session can begin without one too - that is expressly provided for and costs
/// only this one property.
/// </remarks>
public sealed class OmemoIdentity
{

    #region Data

    private readonly Dictionary<UInt32, Curve25519KeyPair> _preKeys = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// The largest prekey identifier ever handed out - which is not the same as
    /// the largest one in stock, and the difference is the whole point.
    /// </summary>
    private UInt32 _highestPreKeyId;

    /// <summary>
    /// How many prekeys a fresh device publishes.
    /// </summary>
    public const Int32 PreKeyCount = 100;

    #endregion

    #region Properties

    /// <summary>
    /// The identity key - valid as long as this device.
    /// </summary>
    public Curve25519KeyPair IdentityKey { get; }

    /// <summary>
    /// The device identifier (XEP-0384, section 5.1): a positive number under
    /// which this device stands in the device list.
    /// </summary>
    public UInt32 DeviceId { get; }

    /// <summary>
    /// The current signed prekey.
    /// </summary>
    public Curve25519KeyPair SignedPreKey { get; private set; }

    /// <summary>
    /// Its identifier.
    /// </summary>
    public UInt32 SignedPreKeyId { get; private set; }

    /// <summary>
    /// The signature of the identity key over it.
    /// </summary>
    public Byte[] SignedPreKeySignature { get; private set; }

    /// <summary>
    /// The superseded signed prekey, as long as it is still needed - or null.
    /// </summary>
    /// <remarks>
    /// <b>Expressly postponed in D63, here it is.</b> A message that was sent
    /// off before the rotation names the old key; without it, it would not be
    /// readable, and the sender would learn nothing of that.
    ///
    /// Exactly <b>one</b> is kept, and that is the trade-off: every kept old key
    /// takes back a piece of what the rotation exists for - whoever steals it
    /// opens the sessions it opened. One covers the messages that were under way
    /// during the rotation; two would cover nothing further that would not be
    /// lost anyway.
    /// </remarks>
    public Curve25519KeyPair? PreviousSignedPreKey { get; private set; }

    /// <summary>
    /// The identifier of the superseded signed prekey, or null.
    /// </summary>
    public UInt32? PreviousSignedPreKeyId { get; private set; }

    /// <summary>
    /// When the current signed prekey came into being - or null for a device
    /// stored before this was written down.
    /// </summary>
    /// <remarks>
    /// The rotation was buildable all along and never happened, because nothing
    /// knew how old the key was. That is what this is for and it is all it is
    /// for.
    /// </remarks>
    public DateTimeOffset? SignedPreKeyCreatedAt { get; private set; }

    /// <summary>
    /// How many prekeys are still in stock.
    /// </summary>
    public Int32 AvailablePreKeys
    {
        get { lock (_lock) return _preKeys.Count; }
    }

    /// <summary>
    /// The identity key in Ed25519 form - that way and only that way it goes
    /// over the wire (section 5.3.2).
    /// </summary>
    public Byte[] PublicIdentityKey
        => Curve25519.MontgomeryToEdwards(IdentityKey.PublicKey);

    /// <summary>
    /// The fingerprint a human being compares: the public identity key in
    /// Ed25519 form, hexadecimal.
    /// </summary>
    /// <remarks>
    /// Over the Ed25519 form and not over the Montgomery form, for only the
    /// former goes over the wire - the counterpart could not make a comparison
    /// over the other one at all.
    /// </remarks>
    public String Fingerprint
        => Convert.ToHexString(PublicIdentityKey).ToLowerInvariant();

    #endregion

    #region Constructor(s)

    private OmemoIdentity(UInt32              deviceId,
                          Curve25519KeyPair   identityKey,
                          UInt32              signedPreKeyId,
                          Curve25519KeyPair   signedPreKey,
                          Byte[]              signature)
    {
        DeviceId               = deviceId;
        IdentityKey            = identityKey;
        SignedPreKeyId         = signedPreKeyId;
        SignedPreKey           = signedPreKey;
        SignedPreKeySignature  = signature;
    }

    #endregion

    #region Create(...)

    /// <summary>
    /// Creates a fresh device: identity key, signed prekey together with its
    /// signature and <see cref="PreKeyCount"/> prekeys.
    /// </summary>
    /// <param name="deviceId">
    /// The device identifier; without a value a random one. It is not a secret
    /// number but an ordinal - it stands in every device list.
    /// </param>
    public static OmemoIdentity Create(UInt32? deviceId = null)
    {

        var identity      = Curve25519.GenerateKeyPair();
        var signedPreKey  = Curve25519.GenerateKeyPair();

        var own = new OmemoIdentity(deviceId ?? RandomDeviceId(),
                                      identity,
                                      1,
                                      signedPreKey,
                                      Curve25519.Sign(identity.PrivateKey, signedPreKey.PublicKey));

        own.SignedPreKeyCreatedAt = DateTimeOffset.UtcNow;

        own.ReplenishPreKeys();

        return own;

    }

    /// <summary>
    /// An identifier from the range section 5.3.2 permits: 1 to 2³¹-1.
    /// </summary>
    /// <remarks>
    /// From the cryptographic random generator and not from a counter: a
    /// running number would betray how many devices of this account have been
    /// created so far, and the device list is public.
    /// </remarks>
    private static UInt32 RandomDeviceId()
        => (UInt32) RandomNumberGenerator.GetInt32(1, Int32.MaxValue);

    #endregion

    #region PreKeys

    /// <summary>
    /// Fills the stock back up to <see cref="PreKeyCount"/>.
    /// </summary>
    /// <remarks>
    /// The identifiers run on and are not reused. A reused identifier would no
    /// longer be an ordinal but a confusion: a message that was left lying under
    /// way and names the old prekey would find, on arriving, a new one under the
    /// same number and would yield a session that never existed.
    ///
    /// <b>Which is why the high-water mark is kept and not read off the
    /// stock.</b> Deriving the next identifier from the largest one in stock
    /// held as long as something was in stock - and fell over precisely when the
    /// stock ran empty, where it began again at 1 and handed the used-up numbers
    /// out a second time. That was the one case the sentence above forbids, and
    /// it was the one case this method was written for.
    ///
    /// One gap is left, and it cannot be closed from here: a device whose stock
    /// was already empty when it was stored has nothing to remember, because the
    /// mark lives in the identifiers themselves. Refilling on every use, the way
    /// <see cref="OmemoManager"/> now does, is what keeps it from arising.
    /// </remarks>
    public IReadOnlyList<OmemoPreKey> ReplenishPreKeys()
    {

        lock (_lock)
        {

            if (_preKeys.Count > 0)
                _highestPreKeyId = Math.Max(_highestPreKeyId, _preKeys.Keys.Max());

            while (_preKeys.Count < PreKeyCount)
                _preKeys[++_highestPreKeyId] = Curve25519.GenerateKeyPair();

            return PublicPreKeys();

        }

    }

    /// <summary>
    /// The public parts of all prekeys in stock.
    /// </summary>
    private IReadOnlyList<OmemoPreKey> PublicPreKeys()
        => [.. _preKeys.OrderBy(e => e.Key)
                       .Select(e => new OmemoPreKey(e.Key, e.Value.PublicKey))];

    /// <summary>
    /// Takes a prekey out - and for good.
    /// </summary>
    /// <returns>
    /// The prekey, or null when it does not (any longer) exist.
    /// </returns>
    /// <remarks>
    /// <b>Taking out and deleting are one step, and that is the heart of the
    /// matter.</b> A prekey that holds twice yields the same session key twice -
    /// and with that the session is replayable: whoever plays an old first
    /// message in once more gets an answer as though it were new. That is why
    /// there is no separate "look up" and "use up" here; whoever gets the key in
    /// hand has thereby already taken it out of the stock.
    /// </remarks>
    public Curve25519KeyPair? TakePreKey(UInt32 id)
    {

        lock (_lock)
        {

            if (!_preKeys.Remove(id, out var pair))
                return null;

            return pair;

        }

    }

    #endregion

    #region RotateSignedPreKey()

    /// <summary>
    /// Rotates the signed prekey and signs the new one.
    /// </summary>
    /// <remarks>
    /// The superseded one moves up to <see cref="PreviousSignedPreKey"/> -
    /// exactly one key far. What lay before it is thereby gone for good, and
    /// that is the point: a kept old key takes back a piece of what the rotation
    /// exists for at all.
    /// </remarks>
    public void RotateSignedPreKey()
    {

        var fresh = Curve25519.GenerateKeyPair();

        lock (_lock)
        {

            PreviousSignedPreKey    = SignedPreKey;
            PreviousSignedPreKeyId  = SignedPreKeyId;

            SignedPreKeyId++;
            SignedPreKey           = fresh;
            SignedPreKeySignature  = Curve25519.Sign(IdentityKey.PrivateKey, fresh.PublicKey);
            SignedPreKeyCreatedAt  = DateTimeOffset.UtcNow;

        }

    }

    #endregion

    #region RotateSignedPreKeyIfDue(MaxAge)

    /// <summary>
    /// Rotates the signed prekey when the current one has reached its age.
    /// </summary>
    /// <returns>true when it was rotated - the caller then has to store.</returns>
    /// <remarks>
    /// <b>A key of unknown age counts as due.</b> A device stored before the
    /// timestamp existed has no answer to the question, and the honest reading
    /// of "I do not know how old this is" is not "young enough". Rotating costs
    /// little: the superseded key stays reachable, so messages already under
    /// way naming it are still readable.
    ///
    /// What the rotation buys is bounded and worth naming precisely. It does
    /// not protect the messages already sent - those hang on the ratchet, which
    /// moves on by itself. It bounds how far back a stolen signed prekey opens
    /// <i>new</i> sessions: without rotation that is "as far as the device has
    /// existed", with it, one interval.
    /// </remarks>
    public Boolean RotateSignedPreKeyIfDue(TimeSpan MaxAge)
    {

        lock (_lock)
        {
            if (SignedPreKeyCreatedAt is DateTimeOffset created &&
                DateTimeOffset.UtcNow - created < MaxAge)
            {
                return false;
            }
        }

        // Outside the lock: RotateSignedPreKey takes it itself.
        RotateSignedPreKey();

        return true;

    }

    /// <summary>
    /// The signed prekey for this identifier - the current or the superseded
    /// one.
    /// </summary>
    /// <returns>null when the identifier belongs to neither of the two.</returns>
    public Curve25519KeyPair? SignedPreKeyFor(UInt32 id)
    {

        lock (_lock)
            return id == SignedPreKeyId          ? SignedPreKey
                 : id == PreviousSignedPreKeyId  ? PreviousSignedPreKey
                 : null;

    }

    #endregion

    #region Export() / Import(state)

    /// <summary>
    /// One's own key material as it is stored.
    /// </summary>
    public OmemoIdentityState Export()
    {

        lock (_lock)
            return new OmemoIdentityState(DeviceId,
                                          IdentityKey.PrivateKey,
                                          SignedPreKeyId,
                                          SignedPreKey.PrivateKey,
                                          SignedPreKeySignature,
                                          PreviousSignedPreKeyId,
                                          PreviousSignedPreKey?.PrivateKey,
                                          [.. _preKeys.OrderBy(e => e.Key)
                                                      .Select(e => new OmemoStoredPreKey(e.Key,
                                                                                          e.Value.PrivateKey))],
                                          SignedPreKeyCreatedAt);

    }

    /// <summary>
    /// Restores stored key material.
    /// </summary>
    /// <remarks>
    /// <b>The signature is taken along and not recomputed.</b> It could be
    /// renewed from the identity key at any time - but XEdDSA mixes randomness
    /// into every signature, so the new one would look different from the
    /// published one. The bundle in the PEP node and the device here would be at
    /// odds afterwards, and a sender comparing the two would take that for an
    /// exchange.
    /// </remarks>
    public static OmemoIdentity Import(OmemoIdentityState state)
    {

        var own = new OmemoIdentity(state.DeviceId,
                                      Curve25519.KeyPairFromPrivate(state.IdentityPrivateKey),
                                      state.SignedPreKeyId,
                                      Curve25519.KeyPairFromPrivate(state.SignedPreKeyPrivateKey),
                                      state.SignedPreKeySignature)
        {
            PreviousSignedPreKeyId  = state.PreviousSignedPreKeyId,
            PreviousSignedPreKey    = state.PreviousSignedPreKeyPrivateKey is not null
                                          ? Curve25519.KeyPairFromPrivate(state.PreviousSignedPreKeyPrivateKey)
                                          : null,

            // Null for a device stored before this was written down, and left
            // null on purpose: RotateSignedPreKeyIfDue reads that as "due", and
            // an unknown age is not a young one.
            SignedPreKeyCreatedAt   = state.SignedPreKeyCreatedAt
        };

        foreach (var pk in state.PreKeys)
            own._preKeys[pk.Id] = Curve25519.KeyPairFromPrivate(pk.PrivateKey);

        // The stock is all there is to read the mark off. What was handed out
        // and used up before is not written down anywhere - see
        // ReplenishPreKeys for what that leaves open and why refilling on every
        // use is what keeps it shut.
        if (state.PreKeys.Count > 0)
            own._highestPreKeyId = state.PreKeys.Max(pk => pk.Id);

        return own;

    }

    #endregion

    #region Bundle()

    /// <summary>
    /// One's own bundle as it is published.
    /// </summary>
    public OmemoBundle Bundle()
    {
        lock (_lock)
            return new OmemoBundle(PublicIdentityKey,
                                   SignedPreKeyId,
                                   SignedPreKey.PublicKey,
                                   SignedPreKeySignature,
                                   PublicPreKeys());
    }

    #endregion

}
