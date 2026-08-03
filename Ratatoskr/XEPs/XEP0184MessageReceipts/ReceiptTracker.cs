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
        }
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
