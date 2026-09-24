using Broiler.Net.Http;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace Broiler.HtmlBridge;

/// <summary>
/// The request shapes and the blocking text read every bridge loader shares when it sends through
/// the profile's <see cref="IBrowserRequestTransport"/>.
/// </summary>
/// <remarks>
/// The transport owns cookies, redirects, CORS and tainting; what stays here is what HTML decides
/// before the request exists (destination, mode and credentials from the element) and the bridge's
/// own budgets, which a transport shared by the whole profile cannot know. A loader's timeout is a
/// linked token rather than the session's, so each keeps the budget it had over its old client.
/// </remarks>
internal static class BridgeTransport
{
    /// <summary>
    /// A script's request context. A classic script follows the element's CORS settings attribute
    /// (no attribute: no-cors with credentials). A module script, and everything it imports, is
    /// always a CORS request: a missing attribute means <c>anonymous</c> (credentials
    /// <c>same-origin</c>), and <c>use-credentials</c> means <c>include</c>.
    /// </summary>
    internal static RequestContext ScriptRequestContext(
        DocumentRequestContext client, bool isModule, string? crossOrigin, Func<Uri, int, bool>? hopPolicy)
    {
        var cors = CorsSettings.Parse(crossOrigin);
        if (isModule && cors == CorsSetting.None)
            cors = CorsSetting.Anonymous;

        return RequestContext.Subresource(client, RequestDestination.Script, cors) with { HopPolicy = hopPolicy };
    }

    /// <summary>
    /// Whether two requests would be sent the same way: same destination, mode, credentials and
    /// redirect mode, for the same client and container document. The host policy is not compared —
    /// both sides apply the same check to the same URL.
    /// </summary>
    internal static bool SameRequestShape(RequestContext a, RequestContext b) =>
        a.Destination == b.Destination &&
        a.Mode == b.Mode &&
        a.Credentials == b.Credentials &&
        a.Redirect == b.Redirect &&
        ReferenceEquals(a.Client, b.Client) &&
        ReferenceEquals(a.Container, b.Container);

    /// <summary>
    /// GETs <paramref name="url"/> through <paramref name="transport"/> and returns its body as text,
    /// blocking the caller. The contract is <see cref="HttpClient.GetStringAsync(string)"/>'s, which
    /// is what every caller used before: a status outside 2xx throws
    /// <see cref="HttpRequestException"/>, and so do network errors (a
    /// <see cref="TransportException"/> is one). The body is read whatever the response tainting,
    /// because the caller is the user agent executing or applying the resource, not a page reading
    /// it: a cross-origin no-cors script still runs.
    /// </summary>
    /// <param name="acceptResponse">
    /// The caller's check on the response head, run before the body is read: a response it refuses
    /// throws <see cref="InvalidDataException"/> and its body is never read. <see langword="null"/>
    /// accepts every successful response.
    /// </param>
    internal static string GetText(
        IBrowserRequestTransport transport,
        Uri url,
        RequestContext context,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<TransportResponse, bool>? acceptResponse = null)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = transport.Send(request, context, budget.Token);
        if (response.StatusCode is < 200 or > 299)
            throw new HttpRequestException(
                $"Response status code does not indicate success: {response.StatusCode}.",
                inner: null,
                (HttpStatusCode)response.StatusCode);

        if (acceptResponse is not null && !acceptResponse(response))
            throw new InvalidDataException(
                $"The response for {context.Destination} is not acceptable: " +
                $"{response.Message.Content.Headers.ContentType?.MediaType ?? "no Content-Type"}, {response.Tainting}.");

        return response.Message.Content.ReadAsStringAsync(budget.Token)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Whether a stylesheet's response may be applied — a <c>&lt;link rel="stylesheet"&gt;</c>'s
    /// (HTML "process the linked resource") or an <c>@import</c>'s (CSS Cascade "fetch an
    /// <c>@import</c>"), together with Fetch's nosniff check for destination <c>style</c>: its
    /// <c>Content-Type</c> must be <c>text/css</c>. The one exception is a document in quirks mode
    /// reading a CORS-same-origin response (basic or CORS tainting), whose type is ignored;
    /// <c>X-Content-Type-Options: nosniff</c> removes even that.
    /// </summary>
    /// <param name="response">The response head; the body is not read.</param>
    /// <param name="quirksMode">Whether the requesting document is in quirks mode.</param>
    /// <remarks>
    /// This is what keeps a page from reading another site's credentialed HTML or JSON through the
    /// CSSOM: a link or an import without <c>crossorigin</c> is a no-cors request that carries the
    /// user's cookies, and a body applied as a sheet leaks its text through the rules it happens to
    /// form, which <c>getComputedStyle</c> and <c>cssRules</c> then read back. It is the rule
    /// Broiler.HTML's own stylesheet loader applies, so what paints and what script sees agree.
    /// </remarks>
    internal static bool IsAcceptableStyleSheet(TransportResponse response, bool quirksMode)
    {
        ArgumentNullException.ThrowIfNull(response);

        var mediaType = response.Message.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType?.Trim(), "text/css", StringComparison.OrdinalIgnoreCase))
            return true;

        return quirksMode &&
               !IsNoSniff(response) &&
               response.Tainting is ResponseTainting.Basic or ResponseTainting.Cors;
    }

    /// <summary>
    /// Fetch's "determine nosniff": the first value of the response's <c>X-Content-Type-Options</c>
    /// fields, split on commas, is an ASCII case-insensitive match for <c>nosniff</c>.
    /// </summary>
    internal static bool IsNoSniff(TransportResponse response)
    {
        foreach (var header in response.Headers)
        {
            if (!string.Equals(header.Key, "X-Content-Type-Options", StringComparison.OrdinalIgnoreCase))
                continue;

            var comma = header.Value.IndexOf(',');
            var first = (comma < 0 ? header.Value : header.Value[..comma]).Trim(' ', '\t');
            return string.Equals(first, "nosniff", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}

/// <summary>
/// A <see cref="SubResourcePrefetcher"/> that remembers the request each URL was issued with, so a
/// prefetched response is only handed to a consumer that would have sent the same request.
/// </summary>
/// <remarks>
/// <para>
/// <b>The context is captured when the request is queued</b>, not when it runs on a worker: the
/// prefetch is the consumer's own request issued early, so it has to carry the document, mode and
/// credentials the consumer will ask for at that moment.
/// </para>
/// <para>
/// <b>Why a consumer can be refused.</b> The prefetcher keys by URL, but one URL can be requested two
/// ways — a classic script and a module of the same <c>src</c>, a stylesheet with and without
/// <c>crossorigin</c> — and the responses differ (credentials, CORS). A consumer whose request does not
/// match the one in flight fetches on its own instead of reading a response sent under different rules.
/// </para>
/// </remarks>
internal sealed class RequestPrefetcher<TRequest>
{
    private readonly ConcurrentDictionary<string, TRequest> _requests = new(StringComparer.Ordinal);
    private readonly SubResourcePrefetcher _inner;
    private readonly Func<TRequest, TRequest, bool> _sameRequest;

    /// <param name="fetch">The blocking fetch, given the URL and the request it was queued with.</param>
    /// <param name="sameRequest">Whether a queued request may serve a consumer's request.</param>
    public RequestPrefetcher(Func<string, TRequest, string?> fetch, Func<TRequest, TRequest, bool> sameRequest)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        _sameRequest = sameRequest ?? throw new ArgumentNullException(nameof(sameRequest));
        // The entry is always recorded before the URL reaches the inner prefetcher, so the lookup
        // cannot miss for a URL this instance issued.
        _inner = new SubResourcePrefetcher(url => fetch(url, _requests[url]));
    }

    /// <summary>Number of URLs a request has been issued for.</summary>
    public int RequestedCount => _inner.RequestedCount;

    /// <summary>
    /// Starts each request that has not been issued yet. A URL already issued keeps its first
    /// request: a later, different one is fetched by its consumer instead.
    /// </summary>
    public void Prefetch(IEnumerable<(string Url, TRequest Request)> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var urls = new List<string>();
        foreach (var (url, request) in requests)
        {
            if (string.IsNullOrWhiteSpace(url))
                continue;
            _requests.TryAdd(url, request);
            urls.Add(url);
        }

        _inner.Prefetch(urls);
    }

    /// <summary>
    /// The prefetched content for <paramref name="url"/> when a request for it is in flight and was
    /// issued the way <paramref name="request"/> would be; otherwise false, and the caller fetches.
    /// </summary>
    public bool TryConsume(string url, TRequest request, out string? content)
    {
        if (_inner.IsPending(url) && _requests.TryGetValue(url, out var issued) && _sameRequest(issued, request))
        {
            content = _inner.Consume(url);
            return true;
        }

        content = null;
        return false;
    }
}
