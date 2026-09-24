using System.Net.Http;
using Broiler.HtmlBridge.Core.Diagnostics;
using Broiler.Net.Http;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The document's host resource loader: the
/// one place that performs sub-resource HTTP requests and knows the optional local base path for
/// resolving relative URLs to files. A feature callback does not construct or reference an
/// <c>HttpClient</c>; it asks the loader.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two ways out, one per host.</b> A bridge whose session options carry the profile's
/// <see cref="IBrowserRequestTransport"/> sends every request through it, with the
/// <see cref="RequestContext"/> the caller builds for the requesting document: the transport owns the
/// profile's cookies (sent and stored per redirect hop), CORS and response tainting. A bridge without
/// one uses the process-wide fallback client, which follows redirects as before but sends and keeps
/// no cookies. The context is ignored there.
/// </para>
/// <para>
/// <b>One loader per bridge.</b> The fallback <c>HttpClient</c> is shared across the process so many
/// documents do not each open a socket pool; the per-document instance carries the transport, the
/// local base path, the document's prefetches and its lifetime. Disposing it (the bridge's teardown)
/// cancels every request still in flight for the document, and re-attaching the bridge to another
/// document does the same for the previous one's (<see cref="BeginDocument"/>).
/// </para>
/// </remarks>
internal sealed class ResourceLoader : IDisposable
{
    // External fetches block the synchronous render/script pipeline; in the sandboxed WPT/headless
    // environment external hosts are unreachable, so a short timeout fails fast (several sequential
    // unreachable fetches still stay well under the per-test budget) instead of hanging the shard.
    // The transport path keeps the same budget through a linked token.
    private const int TimeoutSeconds = 5;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(TimeoutSeconds);

    // The fallback client, for a bridge with no profile transport. Identified, because an
    // unidentified request is one some servers refuse outright rather than serve differently:
    // Wikimedia answers a request with no User-Agent 403 Forbidden, so every stylesheet, script,
    // fetch() and XHR a mediawiki.org page asked for here failed even once the document itself had
    // loaded. See Broiler.Net.Http.BroilerUserAgent. And cookie-less: a default handler keeps an
    // automatic process-wide jar, which replayed one page's Set-Cookie to every document's requests
    // (credentials modes and SameSite ignored). Cookies belong to the profile transport.
    private static readonly HttpClient SharedClient = BroilerHttpProtocol.Apply(
        BroilerUserAgent.Apply(new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = FetchTimeout }));

    private readonly IBrowserRequestTransport? _network;
    private CancellationTokenSource _lifetime = new();

    // The response tainting of each stylesheet the transport answered, by URL: what decides whether
    // the CSSOM may expose its rules (IsStyleSheetOriginClean). Written by the prefetch workers too.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ResponseTainting> _styleSheetTainting =
        new(StringComparer.Ordinal);
    private int _disposed;

    /// <param name="network">
    /// The profile's transport, or <see langword="null"/> for the cookie-less fallback client. The
    /// loader never disposes it.
    /// </param>
    public ResourceLoader(IBrowserRequestTransport? network = null) => _network = network;

    /// <summary>The profile's transport, or <see langword="null"/> when the loader uses the fallback client.</summary>
    public IBrowserRequestTransport? Network => _network;

    /// <summary>
    /// The current document's lifetime: cancelled when the loader is disposed or moves on to another
    /// document. Every request the loader sends is linked to it.
    /// </summary>
    public CancellationToken Lifetime => Volatile.Read(ref _lifetime).Token;

    /// <summary>
    /// Starts a new document on this loader: cancels what the previous document still had in flight
    /// and forgets its prefetches, whose requests were made for a different document.
    /// </summary>
    public void BeginDocument()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var previous = Interlocked.Exchange(ref _lifetime, new CancellationTokenSource());
        _prefetcher = null;
        Cancel(previous);
    }

    /// <summary>Cancels every request still in flight for the document. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _prefetcher = null;
        Cancel(Volatile.Read(ref _lifetime));
    }

    private static void Cancel(CancellationTokenSource lifetime)
    {
        // Cancelled, never disposed: a prefetch worker may still be linking a token source to it, and
        // disposal would turn that into an ObjectDisposedException on a thread nothing observes.
        try { lifetime.Cancel(); }
        catch (AggregateException) { /* a registration threw; the document is going away regardless */ }
    }

    /// <summary>
    /// Host policy on which absolute URLs may be fetched over the network at all, or
    /// <see langword="null"/> (the default) to permit every one — what a browser does. The engine
    /// side of the same question is <c>Broiler.Layout.Engine.OfflineSubresources</c>; a host that
    /// sets one normally sets both, since a document's sheets are requested from here and its
    /// images from there.
    /// </summary>
    /// <remarks>
    /// The timeout above bounds an unreachable host; it does not make one free. A host that knows
    /// in advance that a URL cannot resolve to anything — the conformance runner, whose whole
    /// corpus is a directory on disk — pays five seconds per such URL to be told what it already
    /// knew, and pays it inside a per-test budget of thirty. Declining here reports the resource as
    /// not loaded, which is where the fetch was going to end anyway.
    /// </remarks>
    internal static Func<string?, bool>? NetworkFetchPolicy;

    private static bool MayFetchOverNetwork(string url) =>
        NetworkFetchPolicy is null || NetworkFetchPolicy(url);

    /// <summary>
    /// Optional local base directory for resolving relative sub-resource URLs to files. When set,
    /// relative URLs are checked against this directory before an HTTP fetch is attempted.
    /// </summary>
    public string? LocalBasePath { get; set; }

    /// <summary>
    /// Loads an absolute stylesheet <paramref name="url"/> as text, applying the file/http dispatch
    /// policy in one place: a <c>file://</c> URL is read from disk, <c>http(s)</c> is fetched through
    /// the profile transport with the request's context (or the fallback client when there is none).
    /// Returns <c>null</c> for a non-absolute URL, an unsupported scheme, or a missing file. I/O
    /// exceptions, non-2xx statuses, network errors and a response that is not a stylesheet propagate
    /// so the caller can log with its own context.
    /// </summary>
    /// <param name="url">The absolute URL.</param>
    /// <param name="request">
    /// The request as the requesting document makes it (a linked stylesheet's
    /// <c>Subresource(document, Style, crossorigin)</c>, an <c>@import</c>'s no-cors style request),
    /// and whether that document is in quirks mode.
    /// </param>
    /// <remarks>
    /// A transport response is applied only when <see cref="BridgeTransport.IsAcceptableStyleSheet"/>
    /// admits it: <c>text/css</c>, or any type for a quirks-mode document reading a CORS-same-origin
    /// response without <c>nosniff</c>. The fallback client keeps its old behaviour: it sends no
    /// cookies, so what it reads is what any unauthenticated client could read.
    /// </remarks>
    public string? LoadText(string url, StyleSheetRequest request) =>
        // Consume side of the prefetch/consume split: identical policy and identical result, but a
        // URL that was prefetched has been in flight since the document named it rather than
        // starting its round trip here — provided it was requested the way this caller would.
        _prefetcher is { } prefetcher && prefetcher.TryConsume(url, request, out var prefetched)
            ? prefetched
            : LoadTextDirect(url, request, Lifetime);

    private string? LoadTextDirect(string url, StyleSheetRequest request, CancellationToken lifetime)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        // Traced on the direct path, which both the inline load and the prefetch worker end at, so a
        // stylesheet is recorded once however it was obtained. Off by default; see ResourceTrace.
        var attempt = ResourceTrace.Begin(ResourceTraceKind.Stylesheet, url);
        try
        {
            if (uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                // Only a file: document reads local files (LocalFileAccess): an http(s) page naming a
                // file: or UNC sheet must not have it read, or Windows open an SMB session for it.
                if (!LocalFileAccess.AllowedFor(request.Context.Client?.DocumentUrl))
                {
                    attempt.Completed(null);
                    return null;
                }

                var path = uri.LocalPath;
                var fileContent = File.Exists(path) ? File.ReadAllText(path) : null;
                attempt.Completed(fileContent);
                return fileContent;
            }

            if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                // Declined by the host: reported as not loaded, and traced as such, so it is
                // indistinguishable from the failed fetch it stands in for except in what it cost.
                if (!MayFetchOverNetwork(url))
                {
                    attempt.Completed(null);
                    return null;
                }

                var quirksMode = request.QuirksMode;
                string content;
                if (_network is { } network)
                {
                    var tainting = ResponseTainting.Opaque;
                    content = BridgeTransport.GetText(
                        network, uri, request.Context, FetchTimeout, lifetime,
                        response =>
                        {
                            tainting = response.Tainting;
                            return BridgeTransport.IsAcceptableStyleSheet(response, quirksMode);
                        });
                    _styleSheetTainting[url] = tainting;
                }
                else
                {
                    content = SharedClient.GetStringAsync(url, lifetime).ConfigureAwait(false).GetAwaiter().GetResult();
                }

                attempt.Completed(content);
                return content;
            }

            return null;
        }
        catch (Exception ex)
        {
            attempt.Failed(ex);
            throw;
        }
    }

    /// <summary>
    /// Whether the stylesheet last loaded from <paramref name="url"/> through the transport came back
    /// CORS-same-origin -- basic or CORS tainting -- which is CSSOM's origin-clean flag. Null when the
    /// transport did not answer it (no transport, or not loaded through this loader).
    /// </summary>
    internal bool? IsStyleSheetOriginClean(string url) =>
        _styleSheetTainting.TryGetValue(url, out var tainting)
            ? tainting is ResponseTainting.Basic or ResponseTainting.Cors
            : null;

    /// <summary>
    /// Issues concurrent requests for text sub-resources this document is going to load — external
    /// stylesheets, above all — so <see cref="LoadText"/> blocks on a request already in flight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The prefetcher lives on the loader, and the loader is per document, so a document's fetched
    /// bytes cannot be served to the next one. Prefetching is idempotent and additive: a URL that
    /// is never consumed costs one request that would have been made had the resource been
    /// reached, and a URL that is consumed without having been prefetched takes the direct path.
    /// </para>
    /// <para>
    /// <paramref name="minimumToOverlap"/> is how many URLs make the call worth making, and the
    /// answer depends on what the requests are being overlapped <em>with</em>. The post-parse
    /// caller (the collected sheet list) overlaps them only with each other, so one URL buys
    /// nothing and the default is two. The speculative preload scan overlaps them with
    /// the parse itself, which has not started when it calls, so there one URL is worth issuing and
    /// it passes 1.
    /// </para>
    /// <para>
    /// Each request is captured here, when it is queued: the prefetch is the consumer's own request
    /// issued early, so it is sent for the document and with the credentials mode the consumer will
    /// ask for, and its response is checked against the same document mode. A consumer asking
    /// differently fetches on its own.
    /// </para>
    /// </remarks>
    public void Prefetch(IReadOnlyCollection<(string Url, StyleSheetRequest Request)> requests, int minimumToOverlap = 2)
    {
        if (requests.Count < minimumToOverlap || Volatile.Read(ref _disposed) != 0)
            return;

        // A URL the host will not let the consume path fetch must not be speculated on either: the
        // prefetch is the same request, issued earlier and on a worker, so leaving it unfiltered
        // would keep the whole cost the policy exists to remove — and pay it before the parse.
        Prefetcher().Prefetch(requests.Where(request =>
            Uri.TryCreate(request.Url, UriKind.Absolute, out _) && MayFetchOverNetwork(request.Url)));
    }

    /// <summary>
    /// The document's prefetcher, created on first use.
    /// </summary>
    /// <remarks>
    /// <b>Published atomically, because the callers are not all on one thread.</b> The speculative
    /// scan calls <see cref="Prefetch"/> from a worker while the parse it overlaps may reach the
    /// post-parse stylesheet call on the main thread, so <c>??=</c> here would be a lazy-init
    /// race: two prefetchers built, one published, and every
    /// request issued into the one that lost. The loser is discarded before it has issued anything —
    /// <see cref="SubResourcePrefetcher"/> starts requests in <c>Prefetch</c>, not in its
    /// constructor — so the cost of losing is an allocation.
    /// <para>
    /// The prefetcher captures the document's lifetime when it is created, so a request it runs on a
    /// worker after <see cref="BeginDocument"/> is cancelled with the document it was issued for
    /// rather than running under the next one.
    /// </para>
    /// </remarks>
    private RequestPrefetcher<StyleSheetRequest> Prefetcher()
    {
        if (_prefetcher is { } existing)
            return existing;

        var lifetime = Lifetime;
        return Interlocked.CompareExchange(
                   ref _prefetcher,
                   new RequestPrefetcher<StyleSheetRequest>(
                       (url, request) => LoadTextDirect(url, request, lifetime), StyleSheetRequest.SameShape),
                   null) ??
               _prefetcher!;
    }

    /// <summary>
    /// Requests in flight for this document, created on the first <see cref="Prefetch"/>. Null
    /// until then, so a document that never prefetches carries no extra machinery at all.
    /// </summary>
    /// <remarks>
    /// <c>volatile</c> so the read in <see cref="LoadText"/> — a plain field read on the main
    /// thread — cannot observe the reference before the constructor's writes that
    /// <see cref="Prefetcher"/> published from a worker.
    /// </remarks>
    private volatile RequestPrefetcher<StyleSheetRequest>? _prefetcher;

    /// <summary>
    /// Reads a local file <paramref name="path"/> as a sub-resource, applying the file read + MIME policy
    /// in one place, so a sub-document/object feature callback does not inline a
    /// <c>File.Exists</c>/<c>File.ReadAllText</c> switch. Returns:
    /// <list type="bullet">
    /// <item><c>(null, "")</c> when the file is missing — the caller treats this as an empty document, not
    /// a fetch failure.</item>
    /// <item><c>(null, extensionMime)</c> for a binary content type (image/font/audio/video/pdf): the bytes
    /// are not decoded to text, but the detected MIME is preserved.</item>
    /// <item><c>(text, extensionMime)</c> otherwise, with <see cref="File.ReadAllText(string)"/> semantics
    /// (BOM/encoding detection).</item>
    /// </list>
    /// I/O exceptions from the read propagate so the caller can map them to its own failure contract.
    /// </summary>
    public (string? content, string contentType) LoadLocalResource(string path, string extensionMime)
    {
        if (!File.Exists(path))
            return (null, string.Empty);

        if (IsBinaryMime(extensionMime))
            return (null, extensionMime);

        return (File.ReadAllText(path), extensionMime);
    }

    /// <summary>
    /// True for content types whose bytes must not be decoded to text (images, fonts, media, PDF). Shared
    /// by the sub-resource file and local-base-path read paths so the binary-content policy lives once.
    /// </summary>
    public static bool IsBinaryMime(string mime) =>
        mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
        mime.StartsWith("font/", StringComparison.OrdinalIgnoreCase) ||
        mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
        mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mime, "application/pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sends a prepared request (<c>fetch()</c>, and XMLHttpRequest and <c>sendBeacon</c> through it)
    /// and answers the complete response, its body buffered within the loader's budget.
    /// </summary>
    /// <remarks>
    /// Through the transport the response carries the final URL, the redirect chain and the tainting
    /// the transport computed; its <see cref="TransportResponse.Headers"/> and status are privileged
    /// (Set-Cookie included), so what a page may see is the caller's to filter. Through the fallback
    /// client the response is wrapped as a basic one whose URL list is the request URL and, after an
    /// automatic redirect, the final one. Network errors throw, as <see cref="HttpClient"/> did.
    /// </remarks>
    public Task<TransportResponse> SendAsync(HttpRequestMessage request, RequestContext context, CancellationToken cancellationToken = default) =>
        SendCoreAsync(request, context, cancellationToken);

    /// <summary>
    /// GETs <paramref name="url"/> as a nested navigation (an iframe/frame/object sub-document) and
    /// answers the complete response. <see cref="TransportResponse.FinalUrl"/> is the URL the
    /// document was actually served from, after redirects.
    /// </summary>
    public async Task<TransportResponse> GetAsync(string url, RequestContext context, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendCoreAsync(request, context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransportResponse> SendCoreAsync(HttpRequestMessage request, RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime);
        budget.CancelAfter(FetchTimeout);

        if (_network is { } network)
        {
            var response = await network.SendAsync(request, context, budget.Token).ConfigureAwait(false);
            try
            {
                // Buffered within the same budget: the fallback client read the whole body before it
                // returned, and the callers read it synchronously afterwards with no token of their own.
                await response.Message.Content.LoadIntoBufferAsync(budget.Token).ConfigureAwait(false);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        var requestUrl = request.RequestUri;
        var message = await SharedClient.SendAsync(request, budget.Token).ConfigureAwait(false);
        var finalUrl = message.RequestMessage?.RequestUri;
        IReadOnlyList<Uri> urlList = finalUrl is null || finalUrl == requestUrl ? [requestUrl!] : [requestUrl!, finalUrl];
        return new TransportResponse(message, urlList, ResponseTainting.Basic, context.Credentials);
    }
}

/// <summary>
/// A stylesheet request as <see cref="ResourceLoader.LoadText"/> sends it: the request the requesting
/// document makes, and whether that document is in quirks mode, the one fact about it the response
/// check (<see cref="BridgeTransport.IsAcceptableStyleSheet"/>) needs.
/// </summary>
/// <param name="Context">The request: destination <c>style</c>, for the requesting document.</param>
/// <param name="QuirksMode">Whether the requesting document is in quirks mode.</param>
internal readonly record struct StyleSheetRequest(RequestContext Context, bool QuirksMode)
{
    /// <summary>
    /// Whether a prefetch issued as <paramref name="issued"/> may serve <paramref name="wanted"/>: the
    /// same request shape (<see cref="BridgeTransport.SameRequestShape"/>), checked against the same
    /// document mode.
    /// </summary>
    public static bool SameShape(StyleSheetRequest issued, StyleSheetRequest wanted) =>
        issued.QuirksMode == wanted.QuirksMode && BridgeTransport.SameRequestShape(issued.Context, wanted.Context);
}
