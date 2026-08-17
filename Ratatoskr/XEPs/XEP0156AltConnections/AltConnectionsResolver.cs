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

using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using System.Text.Json;
using System.Xml.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// XEP-0156: Finds the WebSocket endpoint of a domain by way of its
/// <c>host-meta</c>.
/// </summary>
/// <remarks>
/// Two sentences from the XEP determine everything else.
///
/// The first is an order of precedence: "HTTPS queries for host-meta
/// information MUST be used only as a fallback after the methods specified in
/// RFC 6120 have been exhausted." This class is therefore asked only when
/// nobody has named an endpoint.
///
/// The second is a security rule: "host-meta files MUST be fetched only over
/// HTTPS, and MUST only use connection URLs starting with 'https://' or
/// 'wss://'." The two halves belong together. Whoever fetches the information
/// in the clear lets every man in the middle determine where the client signs
/// on; whoever takes a <c>ws://</c> from information fetched securely sends
/// user and password openly through the net afterwards all the same.
///
/// The DNS route by way of <c>_xmppconnect</c> TXT records is not missing, it
/// is abolished: "A previous version of this XEP defined a DNS method to look
/// up this info using a TXT _xmppconnect record, this was insecure and has been
/// removed."
/// </remarks>
public sealed class AltConnectionsResolver
{

    #region Data

    /// <summary>
    /// The link type of the WebSocket endpoint.
    /// </summary>
    public const string WebSocketRel = "urn:xmpp:alt-connections:websocket";

    private const string XrdNamespace = "http://docs.oasis-open.org/ns/xri/xrd-1.0";

    /// <summary>
    /// One shared HttpClient - one per application, not one per query: a new one
    /// per call uses up sockets that stay occupied for minutes after the
    /// closing.
    /// </summary>
    private static readonly HttpClient _httpClient = new();

    private readonly Func<string, CancellationToken, Task<string?>> _fetch;

    #endregion

    #region Constructor(s)

    /// <summary>
    /// Creates a resolver.
    /// </summary>
    /// <param name="fetch">
    /// Fetches the content of an address, or <c>null</c> when it does not
    /// exist. Without a value it is loaded over HTTPS; it is used by the tests,
    /// which check the sequence without a net.
    /// </param>
    public AltConnectionsResolver(Func<string, CancellationToken, Task<string?>>? fetch = null)
    {
        _fetch = fetch ?? FetchOverHttpsAsync;
    }

    #endregion


    /// <summary>
    /// Queries the <c>host-meta</c> of the domain and gives back the first
    /// WebSocket endpoint - or <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// First the JSON version, then the XML version. The XEP knows both as
    /// equals; the order is a choice and not a prescription. The second is asked
    /// only when the first yields nothing - a domain that delivers both thereby
    /// costs one query instead of two.
    /// </remarks>
    public async Task<URL?> DiscoverWebSocketAsync(string             domain,
                                                   CancellationToken  ct   = default)
    {

        var jrd = await _fetch($"https://{domain}/.well-known/host-meta.json", ct);

        if (jrd is not null && WebSocketEndpointsFromJrd(jrd) is { Count: > 0 } fromJson)
            return URL.Parse(fromJson[0]);

        var xrd = await _fetch($"https://{domain}/.well-known/host-meta", ct);

        if (xrd is not null && WebSocketEndpointsFromXrd(xrd) is { Count: > 0 } fromXml)
            return URL.Parse(fromXml[0]);

        return null;

    }

    /// <summary>
    /// The WebSocket endpoints from an XRD document, in the order found.
    /// </summary>
    public static IReadOnlyList<string> WebSocketEndpointsFromXrd(string xrd)
    {

        try
        {

            var root = XDocument.Parse(xrd).Root;

            if (root is null)
                return [];

            return [.. root.Elements(XName.Get("Link", XrdNamespace))
                             .Where  (link => link.Attribute("rel")?.Value == WebSocketRel)
                             .Select (link => link.Attribute("href")?.Value)
                             .Where  (IsSecureWebSocket)
                             .Select (href => href!)];

        }

        // The content comes from a foreign web server and can be anything: an
        // error page, half a file, HTML. That is no error of this program but a
        // domain without a usable host-meta - and the right answer to it is "no
        // endpoint", not an abort of the connection setup.
        catch (System.Xml.XmlException)
        {
            return [];
        }

    }

    /// <summary>
    /// The WebSocket endpoints from a JRD document, in the order found.
    /// </summary>
    public static IReadOnlyList<string> WebSocketEndpointsFromJrd(string jrd)
    {

        try
        {

            using var document = JsonDocument.Parse(jrd);

            if (!document.RootElement.TryGetProperty("links", out var links) ||
                 links.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var endpoints = new List<string>();

            foreach (var link in links.EnumerateArray())
            {

                if (link.ValueKind != JsonValueKind.Object)
                    continue;

                if (link.TryGetProperty("rel",  out var rel)  && rel.ValueKind  == JsonValueKind.String &&
                    link.TryGetProperty("href", out var href) && href.ValueKind == JsonValueKind.String &&
                    rel.GetString() == WebSocketRel &&
                    IsSecureWebSocket(href.GetString()))
                {
                    endpoints.Add(href.GetString()!);
                }

            }

            return endpoints;

        }

        catch (JsonException)
        {
            return [];
        }

    }


    /// <summary>
    /// Does this address point at a TLS-protected WebSocket?
    /// </summary>
    /// <remarks>
    /// The XEP permits <c>https://</c> and <c>wss://</c>; <c>https://</c>
    /// belongs to BOSH (XEP-0124), which this client does not speak. If it
    /// stayed here, it would come back as a WebSocket endpoint and the
    /// connection setup would founder on an address that was not meant for it at
    /// all.
    /// </remarks>
    private static bool IsSecureWebSocket(string? href)

        => href is not null &&
           href.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);


    /// <summary>
    /// Loads an address over HTTPS. Everything that goes wrong in the process is
    /// a domain without a <c>host-meta</c> and no error.
    /// </summary>
    private static async Task<string?> FetchOverHttpsAsync(string             uri,
                                                           CancellationToken  ct)
    {

        // The security rule of the XEP, here and not only at the result: what
        // came over http:// must not even be read.
        if (!uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {

            var response = await _httpClient.GetAsync(uri, ct);

            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsStringAsync(ct);

        }

        catch (HttpRequestException)
        {
            return null;
        }

        catch (TaskCanceledException)
        {
            return null;
        }

    }

}
