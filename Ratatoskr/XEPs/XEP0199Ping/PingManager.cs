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

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// XEP-0199: XMPP Ping - measures round-trip times and keeps the connection
/// open.
/// </summary>
public sealed class PingManager
{

    /// <summary>The namespace of XEP-0199.</summary>
    public const string Namespace = "urn:xmpp:ping";

    private readonly Func<string, Task> _sendStanza;
    private readonly Dictionary<string, (TaskCompletionSource<TimeSpan?> Tcs, DateTime Sent)> _pending = new();
    private readonly object _lock = new();
    private int _counter;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public event Action<string, TimeSpan>? OnPong;
    public event Action<string>? OnPingTimeout;

    /// <summary>
    /// The ping was answered with a stanza error. That is something other than
    /// a timeout: the other side was reachable but declined -
    /// <c>service-unavailable</c> simply means that it does not support
    /// XEP-0199.
    /// </summary>
    public event Action<string, StanzaError>? OnPingError;

    public PingManager(Func<string, Task> sendStanza)
    {
        _sendStanza = sendStanza;
    }

    /// <summary>
    /// Sends a ping and measures the response time
    /// </summary>
    public async Task<TimeSpan?> PingAsync(string? to = null, CancellationToken ct = default)
    {
        var id = $"ping-{Interlocked.Increment(ref _counter)}";
        // RunContinuationsAsynchronously: without it the continuations of the
        // caller run synchronously in the thread that delivers the answer - that
        // is, in the receive loop. Arbitrary user code would hold up the reading
        // of further stanzas there.
        var tcs = new TaskCompletionSource<TimeSpan?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = DateTime.UtcNow;

        lock (_lock)
        {
            _pending[id] = (tcs, sent);
        }

        var toAttr = to != null ? $" to='{XmlEscaping.Escape(to)}'" : "";
        await _sendStanza($"<iq type='get' id='{id}'{toAttr}><ping xmlns='urn:xmpp:ping'/></iq>");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            lock (_lock) _pending.Remove(id);
            OnPingTimeout?.Invoke(to ?? "server");
            return null;
        }
    }

    /// <summary>
    /// Processes a ping answer
    /// </summary>
    public bool ProcessPong(string id)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(id, out var entry))
                return false;

            _pending.Remove(id);
            var rtt = DateTime.UtcNow - entry.Sent;
            entry.Tcs.TrySetResult(rtt);
            OnPong?.Invoke(id, rtt);
            return true;
        }
    }

    /// <summary>
    /// Processes a stanza error on a pending ping.
    ///
    /// Without this handling an <c>iq type='error'</c> ran into ProcessPong and
    /// was counted as a valid answer - a declined request thereby looked like a
    /// measured round-trip time.
    /// </summary>
    public bool ProcessError(string id, StanzaError error)
    {

        (TaskCompletionSource<TimeSpan?> Tcs, DateTime Sent) entry;

        lock (_lock)
        {
            if (!_pending.TryGetValue(id, out entry))
                return false;

            _pending.Remove(id);
        }

        entry.Tcs.TrySetResult(null);
        OnPingError?.Invoke(id, error);

        return true;

    }

    /// <summary>
    /// Answers a ping.
    ///
    /// Without a 'from' the request came from one's own server (RFC 6120,
    /// section 8.1.1.1); the answer then goes back there implicitly, without a
    /// 'to'.
    /// </summary>
    public Task RespondAsync(string id, string? from = null)
    {
        var toAttr = from != null ? $" to='{XmlEscaping.Escape(from)}'" : "";
        return _sendStanza($"<iq type='result' id='{XmlEscaping.Escape(id)}'{toAttr}/>");
    }

    /// <summary>
    /// Checks whether an IQ is a ping
    /// </summary>
    /// <summary>
    /// Checks whether an IQ is a ping.
    ///
    /// The earlier check looked literally for <c>type='get'</c>, that is, only
    /// with single quotation marks; against a server with double ones the ping
    /// was not recognised. The type is checked by the caller anyway - here only
    /// the payload counts.
    /// </summary>
    public static bool IsPing(XElement iq)
        => iq.Child(Namespace, "ping") is not null;
}
