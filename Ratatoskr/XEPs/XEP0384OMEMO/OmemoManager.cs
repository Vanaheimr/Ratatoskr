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

using Microsoft.Extensions.Logging;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// What came out of the encryption for a single device.
/// </summary>
/// <param name="Jid">Whom it belongs to.</param>
/// <param name="DeviceId">Which device.</param>
/// <param name="Reason">Why it was skipped.</param>
public sealed record OmemoSkippedDevice(String Jid, UInt32 DeviceId, String Reason);

/// <summary>
/// A decrypted message.
/// </summary>
/// <param name="Content">The content of the SCE envelope.</param>
/// <param name="SenderDeviceId">Which device it came from.</param>
/// <param name="Trust">How this device is classified.</param>
/// <param name="IdentityCheck">Whether its key is new, known or a different one.</param>
/// <param name="EnvelopeFrom">
/// The sender <b>out of the encrypted envelope</b> - not the one from the
/// stanza.
/// </param>
/// <remarks>
/// Keeping the two senders apart is the point of the affix from XEP-0420: the
/// outer one can be changed by anybody, the inner one cannot. They are compared
/// when decrypting; here the inner one stands, so that a caller can also see
/// the check and does not merely have to trust it.
/// </remarks>
public sealed record OmemoDecrypted(IReadOnlyList<XElement>  Content,
                                    UInt32                   SenderDeviceId,
                                    OmemoTrust               Trust,
                                    OmemoIdentityCheck       IdentityCheck,
                                    String?                  EnvelopeFrom);

/// <summary>
/// Brings together what the stages before have built: key material, X3DH,
/// ratchets, wire format, PEP and storage.
/// </summary>
/// <remarks>
/// <b>The hardest question here is not the encrypting but what happens with a
/// device it does not work for.</b> A contact has four devices, one of them has
/// no fetchable bundle. Three answers are possible, and only one is usable:
///
/// <list type="bullet">
/// <item><b>Do not send at all.</b> Then a single broken device makes the human
///       being unreachable - and they never learn why nobody writes to them any
///       more.</item>
/// <item><b>Send unencrypted.</b> That is the worst one: the sender believes
///       they have encrypted. An attacker who makes a bundle unreachable
///       thereby gets the plaintext.</item>
/// <item><b>Encrypted to all the rest, and say who is missing.</b> That is what
///       this class does - <see cref="OmemoEncryptionResult.Skipped"/> names
///       every skipped device together with the reason.</item>
/// </list>
///
/// <b>One's own further devices belong with it</b>, otherwise one's own
/// computer does not see what one's own telephone has written. One's own
/// <i>device itself</i> does not: it would have to keep a session with itself.
/// </remarks>
public sealed class OmemoManager
{

    #region Data

    private readonly IOmemoStore                                _store;
    private readonly String                                     _ownBareJid;
    private readonly Func<String, Task<OmemoDeviceList?>>       _fetchDeviceList;
    private readonly Func<String, UInt32, Task<OmemoBundle?>>   _fetchBundle;
    private readonly ILogger?                                   _logger;
    private readonly Lock                                       _lock = new();

    #endregion

    #region Properties

    /// <summary>One's own key material.</summary>
    public OmemoIdentity Identity { get; }

    /// <summary>One's own fingerprint.</summary>
    public String Fingerprint => Identity.Fingerprint;

    /// <summary>
    /// Is anything written to a device nobody has decided about yet?
    /// </summary>
    /// <remarks>
    /// <b>Blind trust before verification.</b> On true - the default - a message
    /// goes to unconfirmed devices as well; on false only to expressly confirmed
    /// ones.
    ///
    /// The default is a trade-off and no convenience: a procedure that demands a
    /// fingerprint comparison before the first message does not get used - and
    /// unused encryption protects nobody. Whoever has compared once notices
    /// every later change; that is the gain, and it is kept even when the
    /// beginning was blind.
    /// </remarks>
    public Boolean TrustNewDevicesBlindly { get; set; } = true;

    #endregion

    #region Constructor(s)

    /// <summary>
    /// Builds the manager on a storage - and creates fresh key material when
    /// there is none yet.
    /// </summary>
    public OmemoManager(IOmemoStore                               store,
                        String                                    ownBareJid,
                        Func<String, Task<OmemoDeviceList?>>      fetchDeviceList,
                        Func<String, UInt32, Task<OmemoBundle?>>  fetchBundle,
                        ILogger?                                  logger = null)
    {

        _store            = store;
        _ownBareJid       = ownBareJid;
        _fetchDeviceList  = fetchDeviceList;
        _fetchBundle      = fetchBundle;
        _logger           = logger;

        Identity          = store.LoadOrCreateIdentity();

    }

    #endregion

    #region EncryptAsync(recipients, content)

    /// <summary>
    /// Encrypts a content for all devices of the recipients and one's own
    /// further ones.
    /// </summary>
    public async Task<OmemoEncryptionResult> EncryptAsync(IEnumerable<String>      recipients,
                                                          IReadOnlyList<XElement>  content)
    {

        // The envelope per XEP-0420 - with one's own sender in it, so that the
        // message cannot be passed on under a foreign name.
        var envelope = new SceEnvelope(content,
                                     From: _ownBareJid,
                                     Time: DateTimeOffset.UtcNow).ToXml();

        var payload = OmemoPayloadCipher.Encrypt(
                           System.Text.Encoding.UTF8.GetBytes(envelope.ToString(SaveOptions.DisableFormatting)));

        var keys  = new Dictionary<String, IReadOnlyList<OmemoKey>>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<OmemoSkippedDevice>();

        // One's own further devices belong with it - otherwise one's own
        // computer does not see what one's own telephone has written.
        foreach (var jid in recipients.Append(_ownBareJid)
                                      .Select(JidUtilities.Bare)
                                      .Distinct(StringComparer.OrdinalIgnoreCase))
        {

            var list = await _fetchDeviceList(jid);

            if (list is null)
            {
                skipped.Add(new OmemoSkippedDevice(jid, 0, "no device list"));
                continue;
            }

            var forThisJid = new List<OmemoKey>();

            foreach (var device in list.Devices)
            {

                // One's own device would have to keep a session with itself.
                if (device.Id == Identity.DeviceId &&
                    String.Equals(jid, _ownBareJid, StringComparison.OrdinalIgnoreCase))
                    continue;

                var (entry, reason) = await EncryptForAsync(jid, device.Id, payload.KeyAndHmac);

                if (entry is not null)
                    forThisJid.Add(entry);
                else
                    skipped.Add(new OmemoSkippedDevice(jid, device.Id, reason!));

            }

            if (forThisJid.Count > 0)
                keys[jid] = forThisJid;

        }

        return new OmemoEncryptionResult(
                   new OmemoEncryptedElement(Identity.DeviceId, keys, payload.Ciphertext),
                   skipped);

    }

    /// <summary>
    /// Encrypts the 48 bytes for a single device - and builds the session when
    /// there is none yet.
    /// </summary>
    private async Task<(OmemoKey? Key, String? Reason)> EncryptForAsync(String  jid,
                                                                                UInt32  deviceId,
                                                                                Byte[]  keyAndHmac)
    {

        var trust = _store.TrustOf(jid, deviceId);

        if (trust == OmemoTrust.Distrusted)
            return (null, "expressly refused");

        if (trust == OmemoTrust.Undecided && !TrustNewDevicesBlindly)
            return (null, "not confirmed");

        var stored = _store.LoadSession(jid, deviceId);

        // An existing session.
        if (stored is not null)
        {

            var ratchet   = DoubleRatchet.Import(stored.Ratchet);
            var message = ratchet.Encrypt(keyAndHmac, stored.AssociatedData);

            _store.SaveSession(jid, deviceId,
                               new OmemoSessionState(ratchet.Export(), stored.AssociatedData));

            return (new OmemoKey(deviceId, OmemoWireFormat.Encode(message), false), null);

        }

        // No session - so begin one.
        var bundle = await _fetchBundle(jid, deviceId);

        if (bundle is null)
            return (null, "no fetchable bundle");

        // The identity key from the bundle is noted down before anything is
        // computed with it: a change belongs reported, not used silently.
        var check = _store.RecordIdentity(jid, deviceId, bundle.IdentityKey);

        if (check == OmemoIdentityCheck.Changed)
            return (null, "the identity key has changed");

        if (!TrustNewDevicesBlindly && _store.TrustOf(jid, deviceId) != OmemoTrust.Trusted)
            return (null, "not confirmed");

        var x3dh    = X3DH.Initiate(Identity, bundle);
        var fresh     = DoubleRatchet.InitiateAsSender(x3dh.SharedSecret, bundle.SignedPreKey);
        var content  = fresh.Encrypt(keyAndHmac, x3dh.AssociatedData);

        _store.SaveSession(jid, deviceId, new OmemoSessionState(fresh.Export(), x3dh.AssociatedData));

        var exchange = new OmemoKeyExchange(x3dh.UsedPreKeyId ?? 0,
                                             bundle.SignedPreKeyId,
                                             Identity.PublicIdentityKey,
                                             x3dh.EphemeralKey!,
                                             OmemoWireFormat.Encode(content));

        return (new OmemoKey(deviceId, exchange.Encode(), true), null);

    }

    #endregion

    #region DecryptAsync(element, senderBareJid)

    /// <summary>
    /// Decrypts a message directed at this device.
    /// </summary>
    /// <returns>
    /// null when there was nothing for this device in it or it cannot be read.
    /// </returns>
    /// <remarks>
    /// <b>A failure does not throw but yields null.</b> An unreadable message is
    /// for the recipient the same as none, and a crash could be triggered by
    /// anybody who sends nonsense. The reason stands in the log - there, where
    /// somebody who is looking will look.
    /// </remarks>
    public async Task<OmemoDecrypted?> DecryptAsync(OmemoEncryptedElement  element,
                                                    String                 senderBareJid)
    {

        var jid     = JidUtilities.Bare(senderBareJid);
        var entry = element.KeyFor(_ownBareJid, Identity.DeviceId);

        if (entry is null)
        {
            _logger?.LogDebug("OMEMO: the message from {Jid} was not meant for this device", jid);
            return null;
        }

        if (element.Payload is null)
        {
            // A message without a payload only builds the session. It is
            // processed all the same - that is exactly what it exists for.
            _ = await BuildSessionAsync(jid, element.SenderDeviceId, entry);
            return null;
        }

        try
        {

            var (plaintext, check) = await DecryptEntryAsync(jid, element.SenderDeviceId, entry);

            if (plaintext is null)
                return null;

            var raw = OmemoPayloadCipher.Decrypt(element.Payload, plaintext);

            if (!SceEnvelope.TryRead(XElement.Parse(System.Text.Encoding.UTF8.GetString(raw)),
                                     out var envelope,
                                     senderBareJid))
            {
                _logger?.LogWarning("OMEMO: the envelope from {Jid} names another sender", jid);
                return null;
            }

            return new OmemoDecrypted(envelope!.Content,
                                      element.SenderDeviceId,
                                      _store.TrustOf(jid, element.SenderDeviceId),
                                      check,
                                      envelope.From);

        }
        catch (Exception e)
        {
            _logger?.LogWarning("OMEMO: the message from {Jid}/{Device} could not be read: {Reason}",
                                jid, element.SenderDeviceId, e.Message);
            return null;
        }

    }

    /// <summary>
    /// Fetches the 48 bytes out of the entry - by way of an existing session or
    /// by way of a key exchange.
    /// </summary>
    private async Task<(Byte[]? KeyAndHmac, OmemoIdentityCheck Check)> DecryptEntryAsync(
        String jid, UInt32 deviceId, OmemoKey entry)
    {

        if (entry.IsKeyExchange)
            return await BuildSessionAsync(jid, deviceId, entry);

        var stored = _store.LoadSession(jid, deviceId);

        if (stored is null)
        {
            _logger?.LogWarning("OMEMO: no session with {Jid}/{Device}, and the message brings " +
                                "no key exchange along", jid, deviceId);
            return (null, OmemoIdentityCheck.New);
        }

        var ratchet   = DoubleRatchet.Import(stored.Ratchet);
        var plaintext  = ratchet.Decrypt(OmemoWireFormat.Decode(entry.Data), stored.AssociatedData);

        _store.SaveSession(jid, deviceId, new OmemoSessionState(ratchet.Export(), stored.AssociatedData));

        return (plaintext, OmemoIdentityCheck.Known);

    }

    /// <summary>
    /// Accepts a key exchange and creates the session.
    /// </summary>
    private async Task<(Byte[]? KeyAndHmac, OmemoIdentityCheck Check)> BuildSessionAsync(
        String jid, UInt32 deviceId, OmemoKey entry)
    {

        await Task.CompletedTask;

        var exchange = OmemoKeyExchange.Decode(entry.Data);

        // First note down the identity key, then compute. A change is reported
        // and the message is not accepted - from outside a newly set-up device
        // cannot be told apart from an attacker, and that is not a decision a
        // program can make.
        var check = _store.RecordIdentity(jid, deviceId, exchange.IdentityKey);

        if (check == OmemoIdentityCheck.Changed)
        {
            _logger?.LogWarning("OMEMO: {Jid}/{Device} reports with a different identity key",
                                jid, deviceId);
            return (null, check);
        }

        var x3dh = X3DH.Accept(Identity,
                               exchange.IdentityKey,
                               exchange.EphemeralKey,
                               exchange.SignedPreKeyId,
                               exchange.PreKeyId == 0 ? null : exchange.PreKeyId);

        var ratchet   = DoubleRatchet.InitiateAsReceiver(x3dh.SharedSecret, Identity.SignedPreKey);
        var plaintext  = ratchet.Decrypt(OmemoWireFormat.Decode(exchange.Message), x3dh.AssociatedData);

        lock (_lock)
        {

            _store.SaveSession(jid, deviceId, new OmemoSessionState(ratchet.Export(), x3dh.AssociatedData));

            // The prekey used up is gone - that belongs stored at once,
            // otherwise it would be back after a restart and the message
            // acceptable a second time.
            _store.SaveIdentity(Identity.Export());

        }

        return (plaintext, check);

    }

    #endregion

    #region Fingerprints and trust

    /// <summary>All known devices together with fingerprint and classification.</summary>
    public IReadOnlyList<OmemoDeviceRecord> KnownDevices()
        => _store.KnownDevices();

    /// <summary>Decides about a device.</summary>
    public Boolean SetTrust(String bareJid, UInt32 deviceId, OmemoTrust trust)
        => _store.SetTrust(JidUtilities.Bare(bareJid), deviceId, trust);

    #endregion

}

/// <summary>
/// The result of the encryption: the stanza and who cannot read along.
/// </summary>
/// <param name="Element">The <c>&lt;encrypted/&gt;</c> element.</param>
/// <param name="Skipped">
/// The skipped devices together with the reason - <b>empty means: all are
/// included</b>.
/// </param>
/// <remarks>
/// The list is the reason why this method does not give back a mere
/// <c>XElement</c>. A sender who does not learn that three out of four devices
/// of their counterpart cannot read along takes their conversation for held -
/// and wonders about the answer that does not come.
/// </remarks>
public sealed record OmemoEncryptionResult(OmemoEncryptedElement                Element,
                                           IReadOnlyList<OmemoSkippedDevice>    Skipped);
