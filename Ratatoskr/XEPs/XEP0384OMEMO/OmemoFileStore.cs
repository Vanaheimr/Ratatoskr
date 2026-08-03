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

using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// An OMEMO storage in working memory - for tests and for clients that want to
/// keep nothing.
/// </summary>
/// <remarks>
/// <b>It keeps nothing past the end of the program, and that is no economy
/// version but a statement:</b> whoever uses it has a new fingerprint at every
/// start. For a test that is right; for a human being it would be the assurance
/// that every comparison is worthless.
/// </remarks>
public sealed class OmemoMemoryStore : IOmemoStore
{

    private readonly Dictionary<String, OmemoSessionState>  _sessions  = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<String, OmemoDeviceRecord>  _devices   = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    private OmemoIdentityState? _identity;

    private static String Key(String bareJid, UInt32 deviceId)
        => $"{bareJid.ToLowerInvariant()}/{deviceId}";

    public OmemoIdentityState? LoadIdentity()
    {
        lock (_lock) return _identity;
    }

    public void SaveIdentity(OmemoIdentityState state)
    {
        lock (_lock) _identity = state;
    }

    public OmemoSessionState? LoadSession(String bareJid, UInt32 deviceId)
    {
        lock (_lock) return _sessions.GetValueOrDefault(Key(bareJid, deviceId));
    }

    public void SaveSession(String bareJid, UInt32 deviceId, OmemoSessionState state)
    {
        lock (_lock) _sessions[Key(bareJid, deviceId)] = state;
    }

    public IReadOnlyList<OmemoDeviceRecord> KnownDevices()
    {
        lock (_lock) return [.. _devices.Values];
    }

    public void SaveDevice(OmemoDeviceRecord record)
    {
        lock (_lock) _devices[Key(record.BareJid, record.DeviceId)] = record;
    }

}

/// <summary>
/// An OMEMO storage in a JSON file.
/// </summary>
/// <remarks>
/// One file, written anew in full at every change - the same procedure as with
/// the <see cref="Server.FileAccountStore"/> and sufficient for the same
/// reason: this is about one device and its conversation partners, not about a
/// server.
///
/// It is written by way of a side file that is afterwards moved into its place.
/// If the process breaks off, the old version still stands there in full. That
/// is <b>more important here than with the account storage</b>: a half-written
/// session file costs not one attempt to sign on but every running session -
/// and with it the readability of everything under way.
///
/// <b>The file is not encrypted.</b> It contains the secret identity key, all
/// prekeys and every chain key; whoever reads it reads the conversations along.
/// An encryption with a key that lay beside it would be none - and one that a
/// human being types in does not exist in this application. That is why it
/// stands here expressly instead of being replaced by a reassuring procedure:
/// <b>the file belongs in a place only this user gets to.</b>
/// </remarks>
public sealed class OmemoFileStore : IOmemoStore
{

    #region Data

    private readonly String _path;
    private readonly Lock _lock = new();

    private static readonly JsonSerializerOptions _options = new() {
        WriteIndented           = true,
        DefaultIgnoreCondition  = JsonIgnoreCondition.WhenWritingNull
    };

    private Content _content = new();

    /// <summary>The shape of the file.</summary>
    private sealed class Content
    {
        public OmemoIdentityState?        Identity  { get; set; }
        public List<SessionEntry>      Sessions  { get; set; } = [];
        public List<OmemoDeviceRecord>    Devices   { get; set; } = [];
    }

    private sealed class SessionEntry
    {
        public String        BareJid   { get; set; } = "";
        public UInt32        DeviceId  { get; set; }
        public OmemoSessionState? State     { get; set; }
    }

    #endregion

    #region Properties

    /// <summary>The file the storage lies in.</summary>
    public String Path => _path;

    #endregion

    #region Constructor(s)

    /// <summary>
    /// Creates a storage at the given file and reads it when it already exists.
    /// </summary>
    /// <remarks>
    /// An unreadable file throws instead of going on with an empty storage.
    /// <b>The convenient way would be the dangerous one here:</b> a client that
    /// starts with new keys after a read error has changed its fingerprint
    /// without anybody having been asked - and the old file would be
    /// overwritten at the first storing.
    /// </remarks>
    public OmemoFileStore(String path)
    {

        _path = System.IO.Path.GetFullPath(path);

        if (File.Exists(_path))
            _content = JsonSerializer.Deserialize<Content>(File.ReadAllText(_path), _options)
                          ?? throw new InvalidDataException(
                                 $"The OMEMO storage {_path} is empty or unreadable. It is not " +
                                 "replaced by a fresh one - that would be a silent change of one's " +
                                 "own fingerprint.");

    }

    #endregion

    #region IOmemoStore

    public OmemoIdentityState? LoadIdentity()
    {
        lock (_lock) return _content.Identity;
    }

    public void SaveIdentity(OmemoIdentityState state)
    {

        lock (_lock)
        {
            _content.Identity = state;
            Write();
        }

    }

    public OmemoSessionState? LoadSession(String bareJid, UInt32 deviceId)
    {

        lock (_lock)
            return _content.Sessions
                          .FirstOrDefault(s => s.DeviceId == deviceId &&
                                               String.Equals(s.BareJid, bareJid,
                                                             StringComparison.OrdinalIgnoreCase))
                         ?.State;

    }

    public void SaveSession(String bareJid, UInt32 deviceId, OmemoSessionState state)
    {

        lock (_lock)
        {

            _content.Sessions.RemoveAll(s => s.DeviceId == deviceId &&
                                            String.Equals(s.BareJid, bareJid,
                                                          StringComparison.OrdinalIgnoreCase));

            _content.Sessions.Add(new SessionEntry {
                                     BareJid   = bareJid,
                                     DeviceId  = deviceId,
                                     State     = state
                                 });

            Write();

        }

    }

    public IReadOnlyList<OmemoDeviceRecord> KnownDevices()
    {
        lock (_lock) return [.. _content.Devices];
    }

    public void SaveDevice(OmemoDeviceRecord record)
    {

        lock (_lock)
        {

            _content.Devices.RemoveAll(d => d.DeviceId == record.DeviceId &&
                                           String.Equals(d.BareJid, record.BareJid,
                                                         StringComparison.OrdinalIgnoreCase));

            _content.Devices.Add(record);

            Write();

        }

    }

    #endregion

    #region Write()

    /// <summary>
    /// Writes by way of a side file and moves it into its place.
    /// </summary>
    private void Write()
    {

        var directory = System.IO.Path.GetDirectoryName(_path);

        if (!String.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var sideFile = _path + ".new";

        File.WriteAllText(sideFile, JsonSerializer.Serialize(_content, _options));
        File.Move(sideFile, _path, overwrite: true);

    }

    #endregion

}
