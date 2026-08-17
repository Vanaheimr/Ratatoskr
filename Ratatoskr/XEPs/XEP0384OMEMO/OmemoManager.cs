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

using org.GraphDefined.Vanaheimr.Illias;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

#region (delegate) OnOmemoBundleChangedDelegate

/// <summary>
/// XEP-0384: our own bundle has changed and belongs published anew.
/// </summary>
public delegate Task OnOmemoBundleChangedDelegate(DateTimeOffset     Timestamp,
                                                  OmemoManager       Sender,
                                                  CancellationToken  CancellationToken);

#endregion


/// <summary>
/// What came out of the encryption for a single device.
/// </summary>
/// <param name="Jid">Whom it belongs to.</param>
/// <param name="DeviceId">Which device.</param>
/// <param name="Reason">Why it was skipped.</param>
public sealed record OmemoSkippedDevice(JID Jid, UInt32 DeviceId, String Reason);

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
                                    JID?                     EnvelopeFrom);

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
    private readonly JID                                        _ownBareJid;
    private readonly Func<JID, Task<OmemoDeviceList?>>          _fetchDeviceList;
    private readonly Func<JID, UInt32, Task<OmemoBundle?>>      _fetchBundle;
    private readonly ILogger?                                   _logger;
    private readonly Lock                                       _lock = new();

    /// <summary>
    /// One gate per session, so that a ratchet step is never begun twice at
    /// once. Keyed by bare JID and device, because that is the granularity a
    /// ratchet has - a single gate for everything would let one unreachable
    /// bundle hold up the messages to everybody else for ten seconds.
    /// </summary>
    private readonly Dictionary<String, SemaphoreSlim>          _sessionGates = new();

    #endregion

    #region Events

    /// <summary>
    /// One's own bundle has changed and belongs published anew.
    /// </summary>
    /// <remarks>
    /// Raised when an incoming key exchange has used up a prekey and the stock
    /// has been filled back up. Whoever listens has the connection and can
    /// publish; this class has a store and no server.
    /// </remarks>
    public event OnOmemoBundleChangedDelegate? OnBundleChanged;

    #endregion

    #region Properties

    /// <summary>
    /// One's own key material.
    /// </summary>
    public OmemoIdentity Identity { get; }

    /// <summary>
    /// One's own fingerprint.
    /// </summary>
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

    /// <summary>
    /// How old the signed prekey may become before it is replaced.
    /// </summary>
    /// <remarks>
    /// A week. XEP-0384 asks for a periodic rotation without naming a period,
    /// and the trade-off is plain in both directions: a shorter one costs a
    /// publication and buys a narrower window, a longer one the reverse.
    ///
    /// <b>What the rotation is for is narrower than it sounds.</b> It does not
    /// protect what has already been sent - that hangs on the ratchet, which
    /// moves on by itself. It bounds how far back a stolen signed prekey opens
    /// <i>new</i> sessions: without a rotation that is the whole life of the
    /// device, with one it is an interval.
    ///
    /// <see cref="TimeSpan.Zero"/> or less rotates at every start, which is for
    /// tests and for whoever means it.
    /// </remarks>
    public TimeSpan SignedPreKeyMaxAge { get; set; } = TimeSpan.FromDays(7);

    #endregion

    #region Constructor(s)

    /// <summary>
    /// Builds the manager on a storage - and creates fresh key material when
    /// there is none yet.
    /// </summary>
    public OmemoManager(IOmemoStore                               store,
                        JID                                       ownBareJid,
                        Func<JID, Task<OmemoDeviceList?>>         fetchDeviceList,
                        Func<JID, UInt32, Task<OmemoBundle?>>     fetchBundle,
                        ILogger?                                  logger = null)
    {

        _store            = store;
        _ownBareJid       = ownBareJid;
        _fetchDeviceList  = fetchDeviceList;
        _fetchBundle      = fetchBundle;
        _logger           = logger;

        Identity          = store.LoadOrCreateIdentity();

        // The rotation was buildable all along - RotateSignedPreKey with its
        // superseded key has been there - and never happened, because nothing
        // asked how old the current one was. This is that question, and it is
        // asked where the identity comes off the store.
        //
        // <b>At the switching-on and not on a clock.</b> Whoever leaves this
        // running for a month rotates once, when they start it again. That is
        // less than a timer would give and much more than never; a timer inside
        // a manager that has no thread of its own would be the wrong place for
        // it, and the caller who wants one can ask again.
        //
        // Nothing is published from here. Whoever switches OMEMO on publishes
        // the bundle right afterwards anyway, and that carries the new key.
        if (Identity.RotateSignedPreKeyIfDue(SignedPreKeyMaxAge))
        {
            _logger?.LogInformation("OMEMO: the signed prekey has been rotated; it is now {Id}",
                                    Identity.SignedPreKeyId);

            _store.SaveIdentity(Identity.Export());
        }

    }

    #endregion

    #region SessionGate(jid, deviceId)

    /// <summary>
    /// The gate of one session - created on first use and kept.
    /// </summary>
    /// <remarks>
    /// Kept and not thrown away when it becomes free, because throwing it away
    /// is the one thing that cannot be done safely here: whoever removed it
    /// while somebody was waiting on it would hand the next caller a second
    /// gate for the same session, and the two would pass each other. One
    /// semaphore per device one has ever written to is a handful of objects,
    /// and they last as long as the connection.
    /// </remarks>
    private SemaphoreSlim SessionGate(JID jid, UInt32 deviceId)
    {

        var key = $"{jid.Bare}/{deviceId}";

        lock (_lock)
        {

            if (!_sessionGates.TryGetValue(key, out var gate))
                _sessionGates[key] = gate = new SemaphoreSlim(1, 1);

            return gate;

        }

    }

    #endregion

    #region EncryptAsync(recipients, content)

    /// <summary>
    /// Encrypts a content for all devices of the recipients and one's own
    /// further ones.
    /// </summary>
    public async Task<OmemoEncryptionResult> EncryptAsync(IEnumerable<JID>         recipients,
                                                          IReadOnlyList<XElement>  content)
    {

        // The envelope per XEP-0420 - with one's own sender in it, so that the
        // message cannot be passed on under a foreign name.
        var envelope = new SceEnvelope(content,
                                     From: _ownBareJid.ToString(),
                                     Time: DateTimeOffset.UtcNow).ToXml();

        var payload = OmemoPayloadCipher.Encrypt(
                           System.Text.Encoding.UTF8.GetBytes(envelope.ToString(SaveOptions.DisableFormatting)));

        // No comparer any more: the JID brings its own, and it is the right
        // one - which the OrdinalIgnoreCase here was not, over a full address.
        var keys  = new Dictionary<JID, IReadOnlyList<OmemoKey>>();
        var skipped = new List<OmemoSkippedDevice>();

        // One's own further devices belong with it - otherwise one's own
        // computer does not see what one's own telephone has written.
        foreach (var jid in recipients.Append(_ownBareJid)
                                      .Select(recipient => recipient.Bare)
                                      .Distinct())
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
                    jid == _ownBareJid)
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
    /// <remarks>
    /// Under the gate of that session from the load to the save, and that is not
    /// tidiness. A ratchet step is a read-modify-write: the state is loaded,
    /// imported, advanced, written back. Two messages to the same device at the
    /// same time both read the same state and both produce the message with the
    /// same number - one of the two overwrites the other's saved state, and the
    /// recipient can read exactly one of them. The other is lost with a checksum
    /// error, and nothing on this side notices.
    ///
    /// The lock inside <see cref="DoubleRatchet"/> does not help against it. It
    /// guards one instance, and each of the two calls imports an instance of its
    /// own out of the same stored state.
    ///
    /// The bundle fetch lies inside the gate deliberately, although it may take
    /// seconds. Two first messages to the same device would otherwise both begin
    /// a session, and the second would throw away the first one's.
    /// </remarks>
    private async Task<(OmemoKey? Key, String? Reason)> EncryptForAsync(JID     jid,
                                                                                UInt32  deviceId,
                                                                                Byte[]  keyAndHmac)
    {

        var trust = _store.TrustOf(jid.ToString(), deviceId);

        if (trust == OmemoTrust.Distrusted)
            return (null, "expressly refused");

        if (trust == OmemoTrust.Undecided && !TrustNewDevicesBlindly)
            return (null, "not confirmed");

        var gate = SessionGate(jid, deviceId);
        await gate.WaitAsync();

        try
        {

            var stored = _store.LoadSession(jid.ToString(), deviceId);

            // An existing session.
            if (stored is not null)
            {

                var ratchet   = DoubleRatchet.Import(stored.Ratchet);
                var message = ratchet.Encrypt(keyAndHmac, stored.AssociatedData);

                _store.SaveSession(jid.ToString(), deviceId,
                                   new OmemoSessionState(ratchet.Export(), stored.AssociatedData));

                return (new OmemoKey(deviceId, OmemoWireFormat.Encode(message), false), null);

            }

            // No session - so begin one.
            var bundle = await _fetchBundle(jid, deviceId);

            if (bundle is null)
                return (null, "no fetchable bundle");

            // The identity key from the bundle is noted down before anything is
            // computed with it: a change belongs reported, not used silently.
            var check = _store.RecordIdentity(jid.ToString(), deviceId, bundle.IdentityKey);

            if (check == OmemoIdentityCheck.Changed)
                return (null, "the identity key has changed");

            if (!TrustNewDevicesBlindly && _store.TrustOf(jid.ToString(), deviceId) != OmemoTrust.Trusted)
                return (null, "not confirmed");

            var x3dh    = X3DH.Initiate(Identity, bundle);
            var fresh     = DoubleRatchet.InitiateAsSender(x3dh.SharedSecret, bundle.SignedPreKey);
            var content  = fresh.Encrypt(keyAndHmac, x3dh.AssociatedData);

            _store.SaveSession(jid.ToString(), deviceId, new OmemoSessionState(fresh.Export(), x3dh.AssociatedData));

            var exchange = new OmemoKeyExchange(x3dh.UsedPreKeyId ?? 0,
                                                 bundle.SignedPreKeyId,
                                                 Identity.PublicIdentityKey,
                                                 x3dh.EphemeralKey!,
                                                 OmemoWireFormat.Encode(content));

            return (new OmemoKey(deviceId, exchange.Encode(), true), null);

        }
        finally
        {
            gate.Release();
        }

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
                                                    JID                    senderBareJid)
    {

        var jid     = senderBareJid.Bare;
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
                                      _store.TrustOf(jid.ToString(), element.SenderDeviceId),
                                      check,
                                      JID.TryParse(envelope.From));

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
        JID jid, UInt32 deviceId, OmemoKey entry)
    {

        // Takes the gate itself - and must, because a semaphore is not
        // reentrant: taking it here as well would be this method waiting for
        // itself.
        if (entry.IsKeyExchange)
            return await BuildSessionAsync(jid, deviceId, entry);

        var gate = SessionGate(jid, deviceId);
        await gate.WaitAsync();

        try
        {

            var stored = _store.LoadSession(jid.ToString(), deviceId);

            if (stored is null)
            {
                _logger?.LogWarning("OMEMO: no session with {Jid}/{Device}, and the message brings " +
                                    "no key exchange along", jid, deviceId);
                return (null, OmemoIdentityCheck.New);
            }

            var ratchet   = DoubleRatchet.Import(stored.Ratchet);
            var plaintext  = ratchet.Decrypt(OmemoWireFormat.Decode(entry.Data), stored.AssociatedData);

            _store.SaveSession(jid.ToString(), deviceId, new OmemoSessionState(ratchet.Export(), stored.AssociatedData));

            return (plaintext, OmemoIdentityCheck.Known);

        }
        finally
        {
            gate.Release();
        }

    }

    /// <summary>
    /// Accepts a key exchange and creates the session.
    /// </summary>
    /// <remarks>
    /// This is where a prekey is used up, and where the stock is filled back
    /// up. Both belong together, and until now only the first half happened.
    ///
    /// <b>What was published stayed as it was.</b> The bundle in the PEP node
    /// went on advertising the prekey that had just been spent, and nothing new
    /// was ever added to it. That is not only the replenishment XEP-0384 asks
    /// for going missing; it fails in operation, and loudly: a second stranger
    /// takes the same prekey out of the old bundle, <see cref="X3DH.Accept"/>
    /// finds it used up and throws, and their first message cannot be read.
    /// After a hundred first contacts the bundle consists of nothing but spent
    /// keys, and every further one runs into it - until somebody switches OMEMO
    /// off and on again, which was the only thing that published a bundle.
    /// </remarks>
    private async Task<(Byte[]? KeyAndHmac, OmemoIdentityCheck Check)> BuildSessionAsync(
        JID jid, UInt32 deviceId, OmemoKey entry)
    {

        var exchange = OmemoKeyExchange.Decode(entry.Data);

        var gate = SessionGate(jid, deviceId);
        await gate.WaitAsync();

        try
        {

            // First note down the identity key, then compute. A change is reported
            // and the message is not accepted - from outside a newly set-up device
            // cannot be told apart from an attacker, and that is not a decision a
            // program can make.
            var check = _store.RecordIdentity(jid.ToString(), deviceId, exchange.IdentityKey);

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

            var spent = x3dh.UsedPreKeyId is not null;

            if (spent)
                Identity.ReplenishPreKeys();

            lock (_lock)
            {

                _store.SaveSession(jid.ToString(), deviceId, new OmemoSessionState(ratchet.Export(), x3dh.AssociatedData));

                // The prekey used up is gone - that belongs stored at once,
                // otherwise it would be back after a restart and the message
                // acceptable a second time. The refilled stock goes with it, and
                // for the mirror-image reason: a key that has been handed out
                // but not written down would be gone after a restart, and the
                // message naming it unreadable.
                _store.SaveIdentity(Identity.Export());

            }

            // Outside the lock and not awaited: publishing is a round trip to
            // the server, and a message being decrypted is not going to wait on
            // it. Whoever listens decides what it costs.
            //
            // The discarded task is deliberate and safe, which with a Task it
            // would otherwise not be: the invoker catches and logs every
            // handler itself, so this task cannot fault and there is no
            // unobserved exception to come back later.
            if (spent)
                _ = OnBundleChanged.InvokeAllAsync(handler => handler(Timestamp.Now, this, CancellationToken.None), _logger);

            return (plaintext, check);

        }
        finally
        {
            gate.Release();
        }

    }

    #endregion

    #region Fingerprints and trust

    /// <summary>
    /// All known devices together with fingerprint and classification.
    /// </summary>
    public IReadOnlyList<OmemoDeviceRecord> KnownDevices()
        => _store.KnownDevices();

    /// <summary>
    /// Decides about a device.
    /// </summary>
    public Boolean SetTrust(JID bareJid, UInt32 deviceId, OmemoTrust trust)
        => _store.SetTrust(bareJid.Bare.ToString(), deviceId, trust);

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
