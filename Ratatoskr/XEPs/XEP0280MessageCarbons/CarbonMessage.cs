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
/// XEP-0280: A message mirrored by a message carbon.
/// </summary>
public sealed class CarbonMessage
{
    public bool IsSent { get; }          // true = sent by me, false = received
    public JID OriginalFrom { get; }
    public JID OriginalTo { get; }
    public string? Body { get; }
    public string? MessageId { get; }
    public DateTime ReceivedAt { get; }

    public CarbonMessage(bool isSent, JID originalFrom, JID originalTo, string? body, string? messageId)
    {
        IsSent = isSent;
        OriginalFrom = originalFrom;
        OriginalTo = originalTo;
        Body = body;
        MessageId = messageId;
        ReceivedAt = DateTime.UtcNow;
    }
}
