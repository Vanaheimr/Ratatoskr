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
using Microsoft.Extensions.Logging.Abstractions;

using org.GraphDefined.Vanaheimr.Illias;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

#region (delegate) OnPing...Delegate

/// <summary>
/// XEP-0199: a ping came back, and how long it took.
/// </summary>
public delegate Task OnPongDelegate        (DateTimeOffset     Timestamp,
                                            PingManager        Sender,
                                            String             PingId,
                                            TimeSpan           RoundTripTime,
                                            CancellationToken  CancellationToken);

/// <summary>
/// XEP-0199: a ping was not answered in time.
/// </summary>
public delegate Task OnPingTimeoutDelegate (DateTimeOffset     Timestamp,
                                            PingManager        Sender,
                                            String             Target,
                                            CancellationToken  CancellationToken);

/// <summary>
/// XEP-0199: a ping was declined - which is not a timeout, the far end was
/// there.
/// </summary>
public delegate Task OnPingErrorDelegate   (DateTimeOffset     Timestamp,
                                            PingManager        Sender,
                                            String             PingId,
                                            StanzaError        Error,
                                            CancellationToken  CancellationToken);

#endregion


/// <summary>
/// XEP-0199: XMPP Ping - measures round-trip times and keeps the connection
/// open.
/// </summary>
public sealed class PingManager
{

    /// <summary>
    /// The namespace of XEP-0199.
    /// </summary>
    public const string Namespace = "urn:xmpp:ping";

    private readonly Func<string, Task> _sendStanza;
    private readonly string? _ownBareJid;

    /// <summary>
    /// The pings under way, each with whom it was sent to - null for one's own
    /// server, which is what a ping without a 'to' addresses.
    /// </summary>
    /// <remarks>
    /// The target is kept for the same reason as everywhere else here: the
    /// identifier assigns nothing. <c>ping-1</c> is countable, and an answer to
    /// it used to be believed whoever sent it. What that buys an attacker is
    /// admittedly small - a wrong round-trip time - but the keepalive runs on
    /// this, and a measurement that anybody may write is not a measurement.
    /// </remarks>
    private readonly Dictionary<string, (TaskCompletionSource<TimeSpan?> Tcs, DateTime Sent, string? Target)> _pending = new();
    private readonly Lock _lock = new();
    private readonly ILogger _logger;
    private int _counter;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public event OnPongDelegate? OnPong;
    public event OnPingTimeoutDelegate? OnPingTimeout;

    /// <summary>
    /// The ping was answered with a stanza error. That is something other than
    /// a timeout: the other side was reachable but declined -
    /// <c>service-unavailable</c> simply means that it does not support
    /// XEP-0199.
    /// </summary>
    public event OnPingErrorDelegate? OnPingError;

    /// <param name="ownBareJid">
    /// One's own account, so that an answer from one's own server is recognised
    /// as such. Without it the comparison is narrower, never wider.
    /// </param>
    public PingManager(Func<string, Task>  sendStanza,
                       string?             ownBareJid   = null,
                       ILogger?            logger       = null)
    {
        _sendStanza  = sendStanza;
        _ownBareJid  = ownBareJid;
        _logger      = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// May this answer belong to the ping with that identifier? Takes it out of
    /// the pending ones when it may - and leaves it there when it may not, so
    /// that a forgery cannot take the genuine answer's place away.
    /// </summary>
    private bool TryClaim(string                                                             id,
                          string?                                                            from,
                          out (TaskCompletionSource<TimeSpan?> Tcs, DateTime Sent, string? Target) entry)
    {

        lock (_lock)
        {

            if (!_pending.TryGetValue(id, out entry))
                return false;

            if (!IqAnswerOrigin.MayBelongTo(entry.Target, from, _ownBareJid))
                return false;

            _pending.Remove(id);

            return true;

        }

    }

    /// <summary>
    /// Sends a ping and measures the response time
    /// </summary>
    public async Task<TimeSpan?> PingAsync(JID? to = null, CancellationToken ct = default)
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
            _pending[id] = (tcs, sent, to?.ToString());
        }

        var toAttr = to is not null ? $" to='{XmlEscaping.Escape(to.ToString()!)}'" : "";
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
            await OnPingTimeout.InvokeAllAsync(handler => handler(Timestamp.Now, this, to?.ToString() ?? "server", ct), _logger);
            return null;
        }
    }

    /// <summary>
    /// Processes a ping answer
    /// </summary>
    public async Task<bool> ProcessPongAsync(string             id,
                                             string?            from                = null,
                                             CancellationToken  CancellationToken   = default)
    {

        if (!TryClaim(id, from, out var entry))
            return false;

        var rtt = DateTime.UtcNow - entry.Sent;

        entry.Tcs.TrySetResult(rtt);
        await OnPong.InvokeAllAsync(handler => handler(Timestamp.Now, this, id, rtt, CancellationToken), _logger);

        return true;

    }

    /// <summary>
    /// Processes a stanza error on a pending ping.
    ///
    /// Without this handling an <c>iq type='error'</c> ran into ProcessPong and
    /// was counted as a valid answer - a declined request thereby looked like a
    /// measured round-trip time.
    /// </summary>
    public async Task<bool> ProcessErrorAsync(string             id,
                                              StanzaError        error,
                                              string?            from                = null,
                                              CancellationToken  CancellationToken   = default)
    {

        if (!TryClaim(id, from, out var entry))
            return false;

        entry.Tcs.TrySetResult(null);
        await OnPingError.InvokeAllAsync(handler => handler(Timestamp.Now, this, id, error, CancellationToken), _logger);

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
