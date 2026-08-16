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
/// How this device stands towards a foreign one.
/// </summary>
public enum OmemoTrust
{

    /// <summary>Not decided yet - the human being has never looked at the fingerprint.</summary>
    Undecided,

    /// <summary>Confirmed.</summary>
    Trusted,

    /// <summary>Expressly refused - nothing goes to this device.</summary>
    Distrusted

}

/// <summary>
/// What comes out when a device is seen again.
/// </summary>
public enum OmemoIdentityCheck
{

    /// <summary>This device has never been here.</summary>
    New,

    /// <summary>Known, and the key is the same as last time.</summary>
    Known,

    /// <summary>
    /// Known, but with a <b>different</b> key from last time.
    /// </summary>
    Changed

}

/// <summary>
/// What this device knows about a foreign one.
/// </summary>
/// <param name="BareJid">Whom it belongs to.</param>
/// <param name="DeviceId">Which device.</param>
/// <param name="IdentityKey">Its identity key in Ed25519 form.</param>
/// <param name="Trust">The decision of the human being in front of it.</param>
/// <param name="FirstSeen">When it turned up for the first time.</param>
public sealed record OmemoDeviceRecord(String          BareJid,
                                       UInt32          DeviceId,
                                       Byte[]          IdentityKey,
                                       OmemoTrust      Trust,
                                       DateTimeOffset  FirstSeen)
{

    /// <summary>The fingerprint a human being compares.</summary>
    public String Fingerprint
        => Convert.ToHexString(IdentityKey).ToLowerInvariant();

}

/// <summary>
/// One's own key material as it is stored - <b>with the secret parts</b>.
/// </summary>
/// <param name="DeviceId">One's own device identifier.</param>
/// <param name="IdentityPrivateKey">The secret identity key.</param>
/// <param name="SignedPreKeyId">The identifier of the current signed prekey.</param>
/// <param name="SignedPreKeyPrivateKey">Its secret part.</param>
/// <param name="SignedPreKeySignature">Its signature.</param>
/// <param name="PreviousSignedPreKeyId">The identifier of the superseded one, or null.</param>
/// <param name="PreviousSignedPreKeyPrivateKey">Its secret part, or null.</param>
/// <param name="PreKeys">The prekeys in stock with their secret parts.</param>
/// <param name="SignedPreKeyCreatedAt">
/// When the current signed prekey came into being. Missing in files written
/// before the rotation had a schedule, and then null - which counts as due,
/// because an age nobody knows is not a young one.
/// </param>
public sealed record OmemoIdentityState(UInt32                                  DeviceId,
                                        Byte[]                                  IdentityPrivateKey,
                                        UInt32                                  SignedPreKeyId,
                                        Byte[]                                  SignedPreKeyPrivateKey,
                                        Byte[]                                  SignedPreKeySignature,
                                        UInt32?                                 PreviousSignedPreKeyId,
                                        Byte[]?                                 PreviousSignedPreKeyPrivateKey,
                                        IReadOnlyList<OmemoStoredPreKey>        PreKeys,
                                        DateTimeOffset?                         SignedPreKeyCreatedAt = null);

/// <summary>A prekey with its secret part.</summary>
public sealed record OmemoStoredPreKey(UInt32 Id, Byte[] PrivateKey);

/// <summary>
/// A stored session: the ratchet and the associated data from X3DH.
/// </summary>
/// <param name="Ratchet">The state of the two ratchets.</param>
/// <param name="AssociatedData">
/// <c>Encode(IK_A) ‖ Encode(IK_B)</c> - both identity keys, the initiator
/// first.
/// </param>
/// <remarks>
/// <b>The associated data belongs to the session and not to the ratchet</b>,
/// which is why it stands here beside it and not in the
/// <see cref="RatchetState"/>: the ratchet is handed it at every call and does
/// not own it.
///
/// It has to be stored all the same. It comes into being once at the key
/// exchange and goes into every checksum afterwards; without it a restored
/// session could indeed be continued, but not a single message in it could be
/// read - and the reason would stand nowhere.
/// </remarks>
public sealed record OmemoSessionState(RatchetState Ratchet, Byte[] AssociatedData);

/// <summary>
/// The storage that outlasts a restart.
/// </summary>
/// <remarks>
/// <b>Without it every reconnection is a breach of trust.</b> A new identity
/// key means a new fingerprint, and every comparison any human being has ever
/// made is thereby worthless. A client that produces new keys at every start
/// looks to its contacts like an attacker - every time.
///
/// <b>And the running sessions have to come along.</b> A newly begun session
/// would have a different root key; the counterpart would get messages whose
/// checksum is not right, and that in turn looks like an attack.
///
/// What this storage contains is without exception secret: the identity key,
/// the prekeys, every chain key. <b>Whoever reads it reads the conversations
/// along</b> - the past ones only as far as their keys are still there, the
/// future ones entirely.
/// </remarks>
public interface IOmemoStore
{

    /// <summary>One's own key material, or null at the first start.</summary>
    OmemoIdentityState? LoadIdentity();

    /// <summary>Stores one's own key material.</summary>
    void SaveIdentity(OmemoIdentityState state);

    /// <summary>A stored session, or null.</summary>
    OmemoSessionState? LoadSession(String bareJid, UInt32 deviceId);

    /// <summary>Stores a session and replaces an existing one.</summary>
    void SaveSession(String bareJid, UInt32 deviceId, OmemoSessionState state);

    /// <summary>All devices this one knows of.</summary>
    IReadOnlyList<OmemoDeviceRecord> KnownDevices();

    /// <summary>Stores a device record and replaces an existing one.</summary>
    void SaveDevice(OmemoDeviceRecord record);

}

/// <summary>
/// Behaviour shared by all storages - everything that does not depend on where
/// it is written to.
/// </summary>
public static class OmemoStoreExtensions
{

    #region Loading / storing the identity

    /// <summary>
    /// One's own key material - out of the storage, or freshly produced and
    /// stored.
    /// </summary>
    /// <remarks>
    /// Both in one call, and that is deliberate: whoever loads first and then
    /// produces it themselves on null forgets the storing sooner or later - and
    /// does not notice, because at the next start something is produced again.
    /// The error would look like a new client, and that is exactly the case
    /// this storage is meant to prevent.
    /// </remarks>
    public static OmemoIdentity LoadOrCreateIdentity(this IOmemoStore store)
    {

        if (store.LoadIdentity() is OmemoIdentityState stored)
            return OmemoIdentity.Import(stored);

        var fresh = OmemoIdentity.Create();

        store.SaveIdentity(fresh.Export());

        return fresh;

    }

    #endregion

    #region RecordIdentity(store, bareJid, deviceId, identityKey)

    /// <summary>
    /// Notes down the identity key of a foreign device and reports whether it
    /// is new, known or <b>a different one from last time</b>.
    /// </summary>
    /// <remarks>
    /// <b>A changed key is never taken over silently.</b> There are exactly two
    /// explanations for it: the human being has set up their device anew - or
    /// somebody is pushing in between. From outside the two cannot be told
    /// apart, and that is why it is not a decision a program can make.
    ///
    /// The old record stays standing in this case, together with its trust
    /// decision. Whoever overwrote it would turn a confirmed identity into an
    /// unconfirmed one without anybody noticing - and the warning would be gone
    /// after the first look.
    /// </remarks>
    public static OmemoIdentityCheck RecordIdentity(this IOmemoStore  store,
                                                    String            bareJid,
                                                    UInt32            deviceId,
                                                    Byte[]            identityKey)
    {

        var known = store.KnownDevices()
                           .FirstOrDefault(d => d.DeviceId == deviceId &&
                                                String.Equals(d.BareJid, bareJid,
                                                              StringComparison.OrdinalIgnoreCase));

        if (known is null)
        {

            store.SaveDevice(new OmemoDeviceRecord(bareJid,
                                                   deviceId,
                                                   identityKey,
                                                   OmemoTrust.Undecided,
                                                   DateTimeOffset.UtcNow));

            return OmemoIdentityCheck.New;

        }

        return known.IdentityKey.SequenceEqual(identityKey)
                   ? OmemoIdentityCheck.Known
                   : OmemoIdentityCheck.Changed;

    }

    #endregion

    #region TrustOf(store, bareJid, deviceId) / SetTrust(...)

    /// <summary>
    /// How this device stands towards a foreign one - undecided when it is
    /// unknown.
    /// </summary>
    public static OmemoTrust TrustOf(this IOmemoStore store, String bareJid, UInt32 deviceId)
        => store.KnownDevices()
                .FirstOrDefault(d => d.DeviceId == deviceId &&
                                     String.Equals(d.BareJid, bareJid, StringComparison.OrdinalIgnoreCase))
               ?.Trust
           ?? OmemoTrust.Undecided;

    /// <summary>
    /// Decides about a device.
    /// </summary>
    /// <returns>false when the device is unknown - then there is nothing to decide.</returns>
    /// <remarks>
    /// About an unknown device nothing can be decided, and that is no
    /// formality: a trust decision concerns a <i>key</i>, not a number. Whoever
    /// made it in advance for a device identifier would have made it for the
    /// first key that turns up under this number - and that can be anybody.
    /// </remarks>
    public static Boolean SetTrust(this IOmemoStore  store,
                                   String            bareJid,
                                   UInt32            deviceId,
                                   OmemoTrust        trust)
    {

        var known = store.KnownDevices()
                           .FirstOrDefault(d => d.DeviceId == deviceId &&
                                                String.Equals(d.BareJid, bareJid,
                                                              StringComparison.OrdinalIgnoreCase));

        if (known is null)
            return false;

        store.SaveDevice(known with { Trust = trust });

        return true;

    }

    #endregion

}
