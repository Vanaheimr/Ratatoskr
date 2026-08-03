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

using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// XEP-0198: Stream Management - counts stanzas, collects acks and makes stream
/// resumption possible (in principle).
/// </summary>
public sealed class StreamManagementManager
{

    /// <summary>The namespace of XEP-0198.</summary>
    public const string Namespace = "urn:xmpp:sm:3";

    private readonly Func<string, Task> _sendStanza;
    private readonly ILogger _logger;

    /// <summary>
    /// While a negotiation is running, <see cref="NegotiateAsync"/> waits here
    /// for <c>&lt;enabled/&gt;</c> or <c>&lt;failed/&gt;</c>.
    /// </summary>
    private TaskCompletionSource<bool>? _negotiation;

    private bool _enabled;
    private uint _inbound;       // stanzas received
    private uint _outbound;      // stanzas sent
    private uint _lastAcked;     // last acknowledged

    private readonly Queue<(uint Seq, string Stanza, DateTime Sent)> _unacked = new();
    private readonly object _lock = new();

    private string? _resumeId;
    private bool _resumable;

    public bool IsEnabled => _enabled;
    public bool CanResume => _resumable && _resumeId != null;
    public string? ResumeId => _resumeId;
    public uint InboundCount => _inbound;
    public uint OutboundCount => _outbound;
    public int UnackedCount { get { lock (_lock) return _unacked.Count; } }

    /// <summary>
    /// The counter reading (<c>h</c>) last reported by the other side.
    /// </summary>
    /// <remarks>
    /// That the queue runs empty only means that the reported <c>h</c> was at
    /// least as large as our sequence numbers. A side that counts too <i>few</i>
    /// would thereby stay undiscovered - its <c>h</c> would be too large, and
    /// everything would look in order. Only the comparison with
    /// <see cref="OutboundCount"/> separates agreement from mere toleration.
    /// </remarks>
    public uint LastAcknowledged { get { lock (_lock) return _lastAcked; } }

    public event Action<uint>? OnAckReceived;
    public event Action<List<string>>? OnStanzasLost;
    public event Action? OnResumed;

    public StreamManagementManager(Func<string, Task> sendStanza, ILogger? logger = null)
    {
        _sendStanza = sendStanza;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Activates stream management after the bind
    /// </summary>
    public Task EnableAsync(bool requestResume = true)
    {
        var resume = requestResume ? " resume='true'" : "";
        return _sendStanza($"<enable xmlns='urn:xmpp:sm:3'{resume}/>");
    }

    /// <summary>
    /// Sends <c>&lt;enable/&gt;</c> and waits for the answer of the server.
    /// </summary>
    /// <remarks>
    /// The answer comes in through the normal receive loop, not through a
    /// reading of its own. The setup used to read from the socket itself and
    /// discarded up to ten frames in the process that did not look like stream
    /// management - among them messages and presence.
    /// </remarks>
    /// <returns>true when the server has answered with <c>&lt;enabled/&gt;</c>.</returns>
    public async Task<bool> NegotiateAsync(bool               requestResume  = false,
                                           TimeSpan?          timeout        = null,
                                           CancellationToken  ct             = default)
    {

        // RunContinuationsAsynchronously: the answer arrives in the thread of
        // the receive loop; without it everything waiting would run on there and
        // hold up the reading of the next stanzas.
        var negotiation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Interlocked.Exchange(ref _negotiation, negotiation);

        try
        {

            await EnableAsync(requestResume);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));

            return await negotiation.Task.WaitAsync(cts.Token);

        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("No answer to <enable/> - stream management stays off");
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(ref _negotiation, null, negotiation);
        }

    }

    /// <summary>
    /// Processes <c>&lt;enabled/&gt;</c> from the server
    /// </summary>
    public void ProcessEnabled(string xml)
    {
        _enabled = true;
        _inbound = 0;
        _outbound = 0;
        _lastAcked = 0;

        lock (_lock) _unacked.Clear();

        var idMatch = Regex.Match(xml, @"id=['""]([^'""]+)['""]");
        _resumeId = idMatch.Success ? idMatch.Groups[1].Value : null;
        _resumable = xml.Contains("resume='true'") || xml.Contains("resume=\"true\"");

        if (_resumable)
            _logger.LogInformation("Stream management activated (resume id: {ResumeIdPrefix}...)",
                                   _resumeId?[..Math.Min(8, _resumeId?.Length ?? 0)]);
        else
            _logger.LogInformation("Stream management activated (without resume)");

        _negotiation?.TrySetResult(true);
    }

    /// <summary>
    /// Tries to resume the stream
    /// </summary>
    public Task ResumeAsync()
    {
        if (!CanResume)
            throw new InvalidOperationException("This stream cannot be resumed");

        return _sendStanza($"<resume xmlns='urn:xmpp:sm:3' h='{_inbound}' previd='{_resumeId}'/>");
    }

    /// <summary>
    /// Processes <c>&lt;resumed/&gt;</c>
    /// </summary>
    public void ProcessResumed(string xml)
    {
        var hMatch = Regex.Match(xml, @"h=['""](\d+)['""]");
        if (hMatch.Success)
        {
            ProcessAck(uint.Parse(hMatch.Groups[1].Value));
        }

        _enabled = true;
        _logger.LogInformation("Stream resumed");
        OnResumed?.Invoke();
    }

    /// <summary>
    /// Processes <c>&lt;failed/&gt;</c>
    /// </summary>
    /// <param name="xml">
    /// The frame itself, if at hand - it can carry an <c>h</c>. Without it, it
    /// stays at "everything pending is lost".
    /// </param>
    public void ProcessFailed(string? xml = null)
    {
        // Holds for both: a refused negotiation and a failed resume.
        _negotiation?.TrySetResult(false);

        // XEP-0198 section 5: if the server names a state, the same holds up to
        // there as with every <a h='…'/> - processed is processed, and that
        // independently of the stream itself not going on.
        //
        // Without this step every unacknowledged stanza counts as lost, even the
        // one long since delivered: the server acknowledges only on request, and
        // whoever has just been cut off did not ask any more. Section 4
        // recommends sending what is lost again - on that basis this delivered
        // everything a second time.
        //
        // By way of ProcessAck and not by way of a comparison of its own: the
        // modulo arithmetic of the overflowing counter stands there, and two
        // conceptions of the same computation are one too many.
        if (xml is not null)
            ProcessAck(xml);

        List<string> lost;
        lock (_lock)
        {
            lost = _unacked.Select(x => x.Stanza).ToList();
            _unacked.Clear();
        }

        _resumable = false;
        _resumeId = null;

        if (lost.Count > 0)
        {
            _logger.LogWarning("Stream resume failed - {LostCount} stanzas lost", lost.Count);
            OnStanzasLost?.Invoke(lost);
        }
    }

    /// <summary>
    /// Checks whether a stanza has to be counted.
    ///
    /// XEP-0198 section 2 counts exclusively the three stanza types
    /// <c>message</c>, <c>presence</c> and <c>iq</c>. Nonzas - that is,
    /// <c>&lt;enable/&gt;</c>, <c>&lt;r/&gt;</c>, <c>&lt;a/&gt;</c>,
    /// <c>&lt;open/&gt;</c>, SASL elements and so on - do not count. If one side
    /// counts wrongly, the counters run apart and the other side takes the
    /// <c>h</c> for a protocol violation.
    ///
    /// The reading of the element name has stood in <see cref="StanzaElement"/>
    /// since D26 - it was right here first, but was guessed at elsewhere. One
    /// house, two conceptions of what an <c>&lt;iq&gt;</c> is: the counter did
    /// not take <c>&lt;iqbogus/&gt;</c> along, the switch did.
    /// </summary>
    public static bool IsCountableStanza(string xml)

        => StanzaElement.IsStanza(xml);

    /// <summary>
    /// Tracks an outgoing stanza.
    ///
    /// The caller has to do this only after the successful sending and has to
    /// keep to the send order in the process - the sequence numbers have to
    /// correspond to the order on the wire, otherwise an
    /// <c>&lt;a h='...'/&gt;</c> acknowledges the wrong stanzas.
    /// </summary>
    public void TrackOutgoing(string stanza)
    {
        if (!_enabled || !IsCountableStanza(stanza)) return;

        lock (_lock)
        {
            // 32-bit counter with overflow to 0 (XEP-0198, section 4).
            _outbound = unchecked(_outbound + 1);
            _unacked.Enqueue((_outbound, stanza, DateTime.UtcNow));
        }
    }

    /// <summary>
    /// Tracks an incoming stanza.
    ///
    /// Has to run on every receive path - also for stanzas that are still read
    /// directly during the setup phase. If they are missing,
    /// <c>&lt;a h='...'/&gt;</c> reports a value that is too small.
    /// </summary>
    public void TrackIncoming(string stanza)
    {
        if (!_enabled || !IsCountableStanza(stanza)) return;

        // 32-bit counter with overflow to 0 (XEP-0198, section 4).
        Interlocked.Increment(ref _inbound);
    }

    /// <summary>
    /// Requests an ack from the server
    /// </summary>
    public Task RequestAckAsync()
    {
        if (!_enabled) return Task.CompletedTask;
        return _sendStanza("<r xmlns='urn:xmpp:sm:3'/>");
    }

    /// <summary>
    /// Sends an ack to the server
    /// </summary>
    public Task SendAckAsync()
    {
        if (!_enabled) return Task.CompletedTask;
        return _sendStanza($"<a xmlns='urn:xmpp:sm:3' h='{_inbound}'/>");
    }

    /// <summary>
    /// Processes <c>&lt;a/&gt;</c> (ack) from the server
    /// </summary>
    public void ProcessAck(string xml)
    {
        var hMatch = Regex.Match(xml, @"h=['""](\d+)['""]");
        if (hMatch.Success)
        {
            ProcessAck(uint.Parse(hMatch.Groups[1].Value));
        }
    }

    /// <summary>
    /// Does the stanza with this sequence number count as acknowledged when the
    /// other side reports <c>h</c>?
    ///
    /// A plain <c>Seq &lt;= h</c> would be wrong: per XEP-0198 section 4 the
    /// counter is a 32-bit value that overflows to 0 after 2^32-1. Right after
    /// an overflow h is then smaller than the sequence numbers of the stanzas
    /// still pending, and these would lie unacknowledged in the queue for ever.
    /// What is compared is therefore the distance in modulo arithmetic.
    /// </summary>
    internal static bool IsAcknowledged(uint seq, uint h)
        => unchecked(h - seq) < 0x8000_0000u;

    private void ProcessAck(uint h)
    {
        uint acked = 0;

        lock (_lock)
        {
            while (_unacked.Count > 0 && IsAcknowledged(_unacked.Peek().Seq, h))
            {
                _unacked.Dequeue();
                acked++;
            }
            _lastAcked = h;
        }

        if (acked > 0)
        {
            OnAckReceived?.Invoke(acked);
        }
    }

    /// <summary>
    /// Processes <c>&lt;r/&gt;</c> (ack request) from the server
    /// </summary>
    public Task ProcessRequestAsync() => SendAckAsync();

    /// <summary>
    /// Gives back unacknowledged stanzas (for a resend after failed)
    /// </summary>
    public List<string> GetUnackedStanzas()
    {
        lock (_lock)
        {
            return _unacked.Select(x => x.Stanza).ToList();
        }
    }
}
