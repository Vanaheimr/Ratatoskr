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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// XEP-0184: Follows sent messages up to the delivery receipt and checks
/// incoming receipts for spoofing.
/// </summary>
public sealed class ReceiptTracker
{
    private readonly Dictionary<string, PendingReceipt> _pending = new();
    private readonly object _lock = new();
    private readonly ILogger _logger;

    /// <summary>
    /// Whom a sent message went to, kept beside the pending receipts.
    /// </summary>
    /// <remarks>
    /// <b>Beside them and not in them</b>, because the two are consumed
    /// differently. A delivery receipt arrives once and takes its entry with
    /// it; chat markers keep coming for the same message - received, then
    /// displayed, then acknowledged - and the last of them may be minutes after
    /// the receipt. A single map would let the receipt erase what the markers
    /// still need.
    /// </remarks>
    private readonly Dictionary<string, string> _sentTo = new();

    /// <summary>The order they were remembered in, so the oldest can go first.</summary>
    private readonly Queue<string> _remembered = new();

    /// <summary>
    /// How many sent messages are remembered. Markers arrive within minutes;
    /// what is older than a thousand messages is not being marked any more.
    /// </summary>
    private const int MaxRemembered = 1000;

    public event Action<string, string>? OnReceiptReceived; // messageId, from

    public ReceiptTracker(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Registers a sent message for receipt tracking
    /// </summary>
    public void TrackMessage(string messageId, string to)
    {

        var bareTo = JidUtilities.Bare(to);

        lock (_lock)
        {

            _pending[messageId] = new PendingReceipt(messageId, bareTo, DateTime.UtcNow);

            if (_sentTo.TryAdd(messageId, bareTo))
                _remembered.Enqueue(messageId);

            while (_remembered.Count > MaxRemembered)
                _sentTo.Remove(_remembered.Dequeue());

        }

    }

    /// <summary>
    /// Was this message sent to this address?
    /// </summary>
    /// <remarks>
    /// The same question <see cref="ProcessReceipt"/> asks, and asked without
    /// consuming anything - a message can be marked several times over.
    ///
    /// <b>False for a message nobody remembers</b>, and that is the point: an
    /// answer about a message this side never sent is an answer to nothing.
    /// </remarks>
    public bool WasSentTo(string messageId, string from)
    {

        var bareFrom = JidUtilities.Bare(from);

        lock (_lock)
            return _sentTo.TryGetValue(messageId, out var bareTo) &&
                   string.Equals(bareTo, bareFrom, StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// Processes an incoming receipt with spoofing protection
    /// </summary>
    public bool ProcessReceipt(string receiptId, string from)
    {
        var bareFrom = JidUtilities.Bare(from);

        lock (_lock)
        {
            if (!_pending.TryGetValue(receiptId, out var pending))
            {
                // receipt for an unknown message - ignore
                return false;
            }

            // SPOOFING PROTECTION: the receipt has to come from the expected recipient
            if (!string.Equals(pending.ExpectedFrom, bareFrom, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Receipt spoofing detected! Expected: {Expected}, received: {Actual}",
                                   pending.ExpectedFrom, bareFrom);
                return false;
            }

            _pending.Remove(receiptId);
        }

        OnReceiptReceived?.Invoke(receiptId, from);
        return true;
    }

}
