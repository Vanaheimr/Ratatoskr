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

using System.Threading;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

#region Delegates

/// <summary>
/// XEP-0313: one message out of an archive, as it arrives.
/// </summary>
/// <remarks>
/// For an interface that wants to show a long answer as it comes in rather than
/// after it. The whole page comes back from the query as well, so nothing is
/// lost by ignoring this.
/// </remarks>
public delegate Task OnArchivedMessageDelegate(DateTimeOffset     Timestamp,
                                               MamManager         Sender,
                                               String             QueryId,
                                               ArchivedMessage    Message,
                                               CancellationToken  CancellationToken);

#endregion


/// <summary>
/// XEP-0313: asking an archive what was said.
/// </summary>
/// <remarks>
/// <b>The results do not come back in the answer.</b> They arrive first, as
/// ordinary-looking messages carrying a <c>&lt;result/&gt;</c>, and the answer
/// to the query only says that they are all there now. Every one of those has
/// to reach this manager and go no further: a client that hands them on as
/// messages replays its own history as new arrivals every time somebody opens a
/// conversation - and does it convincingly, because each of them really is a
/// message that really was sent.
///
/// That is the same shape as the room presences of XEP-0045: a stanza that
/// looks exactly like news and is not. Both are told apart by one question
/// asked before anything else happens with them.
/// </remarks>
public sealed class MamManager
{

    #region Data

    private readonly Func<JID?, string, XElement, CancellationToken, Task<XElement?>>?  _ask;
    private readonly JID                                                                _ownJid;
    private readonly ILogger                                                            _logger;

    /// <summary>
    /// The queries that are still running, by the name they go by.
    /// </summary>
    /// <remarks>
    /// A dictionary and not a single field, because two queries may be running
    /// at once - two conversations opened in the same moment - and their
    /// results are interleaved on the connection. The <c>queryid</c> is the only
    /// thing that tells them apart.
    /// </remarks>
    private readonly Dictionary<string, List<ArchivedMessage>>  _open  = [];
    private readonly Lock                                       _lock  = new();

    private int _counter;

    #endregion

    #region Events

    public event OnArchivedMessageDelegate? OnArchivedMessage;

    #endregion

    #region Constructor

    public MamManager(JID                                                                ownJid,
                      Func<JID?, string, XElement, CancellationToken, Task<XElement?>>?  askArchive = null,
                      ILogger?                                                           logger     = null)
    {
        _ownJid  = ownJid;
        _ask     = askArchive;
        _logger  = logger ?? NullLogger.Instance;
    }

    #endregion

    #region Asking

    /// <summary>
    /// XEP-0313, section 3: asks an archive for a page of what it kept.
    /// </summary>
    /// <param name="archive">
    /// Whose archive. <b>Null is one's own</b> - a query with no address goes to
    /// one's own server, which is how XMPP says that everywhere. A room's
    /// archive is asked by naming the room, and that is how anybody sees what
    /// was said before they walked in.
    /// </param>
    /// <param name="with">Only messages with this address, or null for all.</param>
    /// <param name="start">Not before this moment.</param>
    /// <param name="end">Not after it.</param>
    /// <param name="max">How many at most.</param>
    /// <param name="before">
    /// Page backwards from this archive id. <b>An empty string is the usual way
    /// to open a conversation</b>: it means the last page, and the end of a
    /// conversation is what somebody wants to see first.
    /// </param>
    /// <param name="after">Page forwards from this archive id.</param>
    /// <returns>
    /// The page, or null when the archive refused or said nothing at all.
    /// <b>An empty page is not null</b>: an archive that kept nothing has
    /// answered, and the difference matters to whoever is deciding whether to
    /// ask again.
    /// </returns>
    public async Task<ArchivePage?> QueryAsync(JID?               archive            = null,
                                               JID?               with               = null,
                                               DateTimeOffset?    start              = null,
                                               DateTimeOffset?    end                = null,
                                               int?               max                = null,
                                               string?            before             = null,
                                               string?            after              = null,
                                               CancellationToken  cancellationToken  = default)
    {

        if (_ask is null)
            return null;

        var queryId    = $"mam-{Interlocked.Increment(ref _counter)}-{Guid.NewGuid():N}";
        var collected  = new List<ArchivedMessage>();

        // Registered before the query goes out, and that order is the whole
        // mechanism: the results arrive before the answer does, and one that
        // finds no query waiting for it has nowhere to go.
        lock (_lock)
            _open[queryId] = collected;

        try
        {

            var answer = await _ask(archive,
                                    "set",
                                    MessageArchive.Query(queryId, with, start, end, max, before, after),
                                    cancellationToken);

            if (answer is null)
            {
                _logger.LogDebug("The archive {Archive} did not answer", archive?.ToString() ?? "of our own");
                return null;
            }

            if (answer.Attr("type") != "result")
            {
                _logger.LogDebug("The archive {Archive} refused the query",
                                 archive?.ToString() ?? "of our own");
                return null;
            }

            var (complete, first, last, count) = MessageArchive.ReadEnd(answer);

            lock (_lock)
                return new ArchivePage([.. collected], complete, first, last, count);

        }
        finally
        {
            lock (_lock)
                _open.Remove(queryId);
        }

    }

    #endregion

    #region What comes back

    /// <summary>
    /// A message carrying a result. True when it was one - and then it goes no
    /// further.
    /// </summary>
    /// <remarks>
    /// <b>True even for a result nobody is waiting for.</b> A query that was
    /// given up on, or an answer that overtook its own end, still produces
    /// stanzas that are archive entries and not news. Letting one of those
    /// through as a message would be exactly the replay this branch exists to
    /// prevent - and it would arrive looking perfectly genuine, because it is a
    /// message that really was sent once.
    /// </remarks>
    public async Task<bool> ProcessMessageAsync(XElement           message,
                                                CancellationToken  cancellationToken = default)
    {

        var queryId = MessageArchive.QueryIdOf(message);

        if (queryId is null)
            return false;

        List<ArchivedMessage>? into;

        lock (_lock)
            _open.TryGetValue(queryId, out into);

        if (into is null)
        {
            _logger.LogDebug("A result for the query {QueryId}, which nobody is waiting for", queryId);
            return true;
        }

        var archived = MessageArchive.Read(message, _ownJid);

        if (archived is null)
        {
            // Most often a result without a stamp: its place in the
            // conversation cannot be known, and today is not an answer.
            _logger.LogDebug("A result of the query {QueryId} could not be read", queryId);
            return true;
        }

        lock (_lock)
            into.Add(archived);

        await OnArchivedMessage.InvokeAllAsync(handler => handler(Timestamp.Now, this, queryId, archived,
                                                                  cancellationToken), _logger);

        return true;

    }

    #endregion

}
