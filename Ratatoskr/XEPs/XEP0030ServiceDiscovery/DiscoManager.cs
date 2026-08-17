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

using System.Text;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using org.GraphDefined.Vanaheimr.Illias;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

#region (delegate) OnDiscoQueryErrorDelegate

/// <summary>
/// XEP-0030: a disco query was declined.
/// </summary>
public delegate Task OnDiscoQueryErrorDelegate(DateTimeOffset     Timestamp,
                                               DiscoManager       Sender,
                                               String             QueryId,
                                               StanzaError        Error,
                                               CancellationToken  CancellationToken);

#endregion


/// <summary>
/// XEP-0030: Service Discovery - queries features and sub-units of other
/// entities and answers incoming disco#info and disco#items requests.
/// </summary>
public sealed class DiscoManager
{

    /// <summary>The namespace of disco#info.</summary>
    public const string InfoNamespace = "http://jabber.org/protocol/disco#info";

    /// <summary>The namespace of disco#items.</summary>
    public const string ItemsNamespace = "http://jabber.org/protocol/disco#items";

    /// <summary>The namespace of the data forms (XEP-0004/XEP-0128).</summary>
    private const string DataFormNamespace = "jabber:x:data";

    private readonly Func<string, Task> _sendStanza;
    private readonly string? _ownBareJid;

    /// <summary>
    /// The open queries, each with the entity it was addressed to.
    /// </summary>
    /// <remarks>
    /// <b>The address is kept because the identifier alone assigns nothing.</b>
    /// <c>disco-info-2</c> is countable and stands openly in the stanza, so
    /// anybody who may write here can answer a question that was put to
    /// somebody else - and the answer used to be stored under the sender it
    /// carried, without anyone comparing it against the sender that was asked.
    /// </remarks>
    private readonly Dictionary<string, (TaskCompletionSource<DiscoInfo?> Tcs, string Target)> _infoQueries = new();
    private readonly Dictionary<string, (TaskCompletionSource<DiscoItems?> Tcs, string Target)> _itemsQueries = new();
    private readonly Lock _lock = new();
    private readonly ILogger _logger;
    private int _counter;

    /// <summary>
    /// A disco query was answered with a stanza error. The query belonging to it
    /// then delivers null - unlike with a timeout, however, it is known here
    /// why.
    /// </summary>
    public event OnDiscoQueryErrorDelegate? OnQueryError;

    // local features that we support
    public List<DiscoIdentity> LocalIdentities { get; } = [
        new("client", "console", "XMPP Console Client")
    ];

    public List<string> LocalFeatures { get; } = [
        "http://jabber.org/protocol/disco#info",
        "http://jabber.org/protocol/disco#items",
        "urn:xmpp:ping",
        "urn:xmpp:receipts",
        "urn:xmpp:carbons:2",
        "urn:xmpp:chat-markers:0",
        "http://jabber.org/protocol/chatstates",
        "http://jabber.org/protocol/caps",

        // XEP-0308, section 4: without this announcement another side does not
        // know whether a correction arrives - and has to assume, to be on the
        // safe side, that it appears as a second message.
        "urn:xmpp:message-correct:0"
    ];

    /// <summary>
    /// XEP-0128: One's own extended information, appended to every disco#info
    /// answer.
    /// </summary>
    /// <remarks>
    /// Empty by default, and deliberately so. What stands here every contact
    /// learns without asking - software, version and operating system are
    /// exactly the details a device can be recognised again by. Whoever wants to
    /// publish them does so; of itself it does not happen.
    ///
    /// The content goes into the verification string per XEP-0115 (see
    /// <see cref="EntityCapsManager"/>). It is therefore announced along with
    /// it, and the other side recomputes it - which is why it can only be
    /// changed together with a new presence.
    /// </remarks>
    public List<DiscoForm> LocalForms { get; } = [];

    /// <summary>
    /// XEP-0030, section 4: One's own sub-units, which a disco#items query
    /// enumerates.
    /// </summary>
    /// <remarks>
    /// Empty by default, for a client has none. Precisely for that reason the
    /// query has to be answered all the same: <c>LocalFeatures</c> announces
    /// <c>disco#items</c>, and announced and then refused is the one combination
    /// there must not be.
    ///
    /// <b>"I have none" and "do not ask me" are different pieces of
    /// information.</b> A <c>&lt;service-unavailable/&gt;</c> says the second;
    /// true is the first.
    /// </remarks>
    public List<DiscoItem> LocalItems { get; } = [];

    /// <param name="ownBareJid">
    /// One's own account. Only needed so that an answer from one's own server
    /// is recognised as such; without it the comparison is narrower, never
    /// wider.
    /// </param>
    public DiscoManager(Func<string, Task>  sendStanza,
                        string?             ownBareJid   = null,
                        ILogger?            logger       = null)
    {
        _sendStanza  = sendStanza;
        _ownBareJid  = ownBareJid;
        _logger      = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// May this answer belong to the query with that identifier? Takes it out
    /// of the open queries when it may.
    /// </summary>
    /// <remarks>
    /// <b>Not taken out on a mismatch</b>, and that is deliberate. Removing it
    /// either way would hand the forgery a second prize: the genuine answer
    /// would arrive afterwards and belong to nobody, so whoever cannot be
    /// believed could at least see to it that nobody else is.
    /// </remarks>
    private bool TryClaim<T>(Dictionary<string, (TaskCompletionSource<T> Tcs, string Target)> open,
                             string                                                          id,
                             string?                                                         from,
                             out TaskCompletionSource<T>?                                    tcs)
    {

        tcs = null;

        lock (_lock)
        {

            if (!open.TryGetValue(id, out var entry))
                return false;

            if (!IqAnswerOrigin.MayBelongTo(entry.Target, from, _ownBareJid))
                return false;

            open.Remove(id);
            tcs = entry.Tcs;

            return true;

        }

    }

    /// <summary>
    /// Queries disco#info
    /// </summary>
    public async Task<DiscoInfo?> QueryInfoAsync(string jid, string? node = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var id = $"disco-info-{Interlocked.Increment(ref _counter)}";
        var tcs = new TaskCompletionSource<DiscoInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock) _infoQueries[id] = (tcs, jid);

        var nodeAttr = node != null ? $" node='{XmlEscaping.Escape(node)}'" : "";
        await _sendStanza(
            $"<iq type='get' to='{XmlEscaping.Escape(jid)}' id='{id}'>" +
            $"<query xmlns='http://jabber.org/protocol/disco#info'{nodeAttr}/></iq>");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            lock (_lock) _infoQueries.Remove(id);
            return null;
        }
    }

    /// <summary>
    /// Queries disco#items
    /// </summary>
    public async Task<DiscoItems?> QueryItemsAsync(string jid, string? node = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var id = $"disco-items-{Interlocked.Increment(ref _counter)}";
        var tcs = new TaskCompletionSource<DiscoItems?>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock) _itemsQueries[id] = (tcs, jid);

        var nodeAttr = node != null ? $" node='{XmlEscaping.Escape(node)}'" : "";
        await _sendStanza(
            $"<iq type='get' to='{XmlEscaping.Escape(jid)}' id='{id}'>" +
            $"<query xmlns='http://jabber.org/protocol/disco#items'{nodeAttr}/></iq>");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            lock (_lock) _itemsQueries.Remove(id);
            return null;
        }
    }

    /// <summary>
    /// Processes a stanza error on a pending disco request.
    ///
    /// Without this handling an <c>iq type='error'</c> ended up in
    /// ProcessInfoResult; there the parser, lacking a <c>&lt;query/&gt;</c>,
    /// finds nothing and delivered an empty but successful result - a declined
    /// query was not to be told apart from an entity without features.
    /// </summary>
    public async Task<bool> ProcessErrorAsync(string             id,
                                              StanzaError        error,
                                              string?            from                = null,
                                              CancellationToken  CancellationToken   = default)
    {

        TryClaim(_infoQueries,  id, from, out var infoTcs);
        TryClaim(_itemsQueries, id, from, out var itemsTcs);

        if (infoTcs is null && itemsTcs is null)
            return false;

        infoTcs?.TrySetResult(null);
        itemsTcs?.TrySetResult(null);

        await OnQueryError.InvokeAllAsync(handler => handler(Timestamp.Now, this, id, error, CancellationToken), _logger);

        return true;

    }

    /// <summary>
    /// Processes a disco#info answer.
    ///
    /// The earlier pattern for identities excluded the slash
    /// (<c>&lt;identity([^/&gt;]+)/?&gt;</c>) so that it would not eat the
    /// closing <c>/&gt;</c> along with it - a name with a slash therefore made
    /// the identity vanish entirely. With the feature pattern, <c>var</c> had to
    /// be the first attribute, otherwise the feature was missing from the list
    /// and the other side seemed less capable than it is.
    /// </summary>
    public bool ProcessInfoResult(string id, XElement iq, string from)
    {

        if (!TryClaim(_infoQueries, id, from, out var tcs))
            return false;

        var info  = new DiscoInfo { From = from };
        var query = iq.Child(InfoNamespace, "query");

        if (query is not null)
        {

            foreach (var identity in query.Children(InfoNamespace, "identity"))
                info.Identities.Add(new DiscoIdentity(identity.Attr("category") ?? "",
                                                      identity.Attr("type")     ?? "",
                                                      identity.Attr("name"),
                                                      identity.Attribute(XNamespace.Xml + "lang")?.Value));

            foreach (var feature in query.Children(InfoNamespace, "feature"))
            {
                var var = feature.Attr("var");
                if (var is not null)
                    info.Features.Add(var);
            }

            // XEP-0128: extended information as a data form. What is taken over
            // is what stands there - which forms count for the verification
            // string and which are to be passed over per XEP-0115, section 5.4
            // is decided by the EntityCapsManager. A parser that sorts things out
            // already takes the ground away from the check.
            foreach (var form in query.Elements()
                                      .Where(child => child.Name.NamespaceName == DataFormNamespace &&
                                                      child.Name.LocalName     == "x"))
            {

                var fields = new List<DiscoField>();

                foreach (var field in form.Elements()
                                          .Where(child => child.Name.LocalName == "field"))
                {

                    var var = field.Attr("var");

                    if (var is null)
                        continue;

                    fields.Add(new DiscoField(var,
                                              field.Attr("type"),
                                              [.. field.Elements()
                                                       .Where (v => v.Name.LocalName == "value")
                                                       .Select(v => v.Value)]));

                }

                info.Forms.Add(new DiscoForm(fields));

            }

        }

        tcs.TrySetResult(info);
        return true;
    }

    /// <summary>
    /// Processes a disco#items answer
    /// </summary>
    public bool ProcessItemsResult(string id, XElement iq, string from)
    {

        if (!TryClaim(_itemsQueries, id, from, out var tcs))
            return false;

        var items = new DiscoItems { From = from };
        var query = iq.Child(ItemsNamespace, "query");

        if (query is not null)
        {
            foreach (var item in query.Children(ItemsNamespace, "item"))
            {
                var jid = item.Attr("jid");
                if (jid is not null)
                    items.Items.Add(new DiscoItem(jid, item.Attr("node"), item.Attr("name")));
            }
        }

        tcs.TrySetResult(items);
        return true;
    }

    /// <summary>
    /// Answers a disco#info request
    /// </summary>
    public Task RespondInfoAsync(string id, string? from, string? node = null)
    {
        // Without a 'from' the request came from one's own server (RFC 6120,
        // section 8.1.1.1); the answer then goes back there without a 'to'.
        var toAttr = from != null ? $" to='{XmlEscaping.Escape(from)}'" : "";

        var sb = new StringBuilder();
        sb.Append($"<iq type='result' id='{XmlEscaping.Escape(id)}'{toAttr}>");

        var nodeAttr = node != null ? $" node='{XmlEscaping.Escape(node)}'" : "";
        sb.Append($"<query xmlns='http://jabber.org/protocol/disco#info'{nodeAttr}>");

        foreach (var identity in LocalIdentities)
        {
            sb.Append($"<identity category='{identity.Category}' type='{identity.Type}'");
            if (identity.Name != null)
                sb.Append($" name='{XmlEscaping.Escape(identity.Name)}'");
            // Without this attribute our answer would yield, at the other side, a
            // different hash from the one we announce.
            if (identity.Language != null)
                sb.Append($" xml:lang='{XmlEscaping.Escape(identity.Language)}'");
            sb.Append("/>");
        }

        foreach (var feature in LocalFeatures)
        {
            sb.Append($"<feature var='{feature}'/>");
        }

        // XEP-0128: the extended information, in case any is deposited.
        foreach (var form in LocalForms)
        {

            sb.Append($"<x xmlns='{DataFormNamespace}' type='result'>");

            foreach (var field in form.Fields)
            {

                sb.Append($"<field var='{XmlEscaping.Escape(field.Var)}'");

                if (field.Type is not null)
                    sb.Append($" type='{XmlEscaping.Escape(field.Type)}'");

                sb.Append('>');

                foreach (var value in field.Values)
                    sb.Append($"<value>{XmlEscaping.Escape(value)}</value>");

                sb.Append("</field>");

            }

            sb.Append("</x>");

        }

        sb.Append("</query></iq>");
        return _sendStanza(sb.ToString());
    }

    /// <summary>
    /// Answers a disco#items request with <see cref="LocalItems"/>.
    /// </summary>
    /// <remarks>
    /// <b>Without a <c>node</c> parameter, and that is deliberate.</b> A branch
    /// that does not exist here is not answered here but not at all - the caller
    /// decides about that, for an empty list would mean "this branch exists, it
    /// is empty". A parameter that never gets a value would look like a
    /// capability and would be none.
    /// </remarks>
    public Task RespondItemsAsync(string id, string? from)
    {

        // Without a 'from' the request came from one's own server (RFC 6120,
        // section 8.1.1.1); the answer then goes back there without a 'to'.
        var toAttr = from != null ? $" to='{XmlEscaping.Escape(from)}'" : "";

        var sb = new StringBuilder();
        sb.Append($"<iq type='result' id='{XmlEscaping.Escape(id)}'{toAttr}>");
        sb.Append($"<query xmlns='{ItemsNamespace}'>");

        foreach (var item in LocalItems)
        {

            sb.Append($"<item jid='{XmlEscaping.Escape(item.Jid)}'");

            if (item.Node != null)
                sb.Append($" node='{XmlEscaping.Escape(item.Node)}'");

            if (item.Name != null)
                sb.Append($" name='{XmlEscaping.Escape(item.Name)}'");

            sb.Append("/>");

        }

        sb.Append("</query></iq>");
        return _sendStanza(sb.ToString());

    }

}
