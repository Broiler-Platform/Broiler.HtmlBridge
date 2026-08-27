using System.Net.Http;
using Broiler.HtmlBridge.Core.Diagnostics;
using Broiler.Layout.Net;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The document's host resource loader (HtmlBridge complexity-reduction roadmap Phase 2, P2.6): the
/// one place that performs sub-resource HTTP requests and knows the optional local base path for
/// resolving relative URLs to files. It replaces the process-static <c>HttpClient</c> that feature
/// callbacks reached into directly, so — per the roadmap's dependency rules — a feature callback no
/// longer constructs or references an <c>HttpClient</c>; it asks the loader.
/// </summary>
/// <remarks>
/// The <c>HttpClient</c> is shared across the process (as the bridge's static client was) so many
/// documents do not each open a socket pool; the per-document loader instance carries only the local
/// base path. This is the seam Phase 7 builds on to route scripts, stylesheets, fetch, XHR and frames
/// through one loader with explicit file/data/http policy, CSP and cancellation — none of which lives
/// here yet.
/// </remarks>
internal sealed class ResourceLoader
{
    // External fetches block the synchronous render/script pipeline; in the sandboxed WPT/headless
    // environment external hosts are unreachable, so a short timeout fails fast (several sequential
    // unreachable fetches still stay well under the per-test budget) instead of hanging the shard.
    private const int TimeoutSeconds = 5;

    // Identified, because an unidentified request is one some servers refuse outright rather than
    // serve differently: Wikimedia answers a request with no User-Agent 403 Forbidden, so every
    // stylesheet, script, fetch() and XHR a mediawiki.org page asked for here failed even once the
    // document itself had loaded. See Broiler.Layout.Net.BroilerUserAgent.
    private static readonly HttpClient SharedClient = BroilerHttpProtocol.Apply(
        BroilerUserAgent.Apply(new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) }));

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

    /// <summary>Fetches <paramref name="url"/> as a string (e.g. an external stylesheet).</summary>
    public Task<string> GetStringAsync(string url) => SharedClient.GetStringAsync(url);

    /// <summary>
    /// Loads an absolute <paramref name="url"/> as text, applying the file/http dispatch policy in one
    /// place (Phase 7 item 4): a <c>file://</c> URL is read from disk, <c>http(s)</c> is fetched via the
    /// shared client. Returns <c>null</c> for a non-absolute URL, an unsupported scheme, or a missing
    /// file. I/O exceptions propagate so the caller can log with its own context. This replaces the
    /// file/http switch that stylesheet/sub-resource feature callbacks used to inline.
    /// </summary>
    public string? LoadText(string url) =>
        // Consume side of the prefetch/consume split (multithreading roadmap item #2): identical
        // policy and identical result, but a URL that was prefetched has been in flight since the
        // document named it rather than starting its round trip here.
        _prefetcher is { } prefetcher && prefetcher.IsPending(url)
            ? prefetcher.Consume(url)
            : LoadTextDirect(url);

    private string? LoadTextDirect(string url)
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

                var content = GetStringAsync(url).ConfigureAwait(false).GetAwaiter().GetResult();
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
    /// Issues concurrent requests for text sub-resources this document is going to load — external
    /// stylesheets, above all — so <see cref="LoadText"/> blocks on a request already in flight.
    /// Multithreading roadmap item #2.
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
    /// nothing and the default is two. The speculative preload scan (item #17) overlaps them with
    /// the parse itself, which has not started when it calls, so there one URL is worth issuing and
    /// it passes 1.
    /// </para>
    /// </remarks>
    public void Prefetch(IReadOnlyCollection<string> urls, int minimumToOverlap = 2)
    {
        if (urls.Count < minimumToOverlap)
            return;

        // A URL the host will not let the consume path fetch must not be speculated on either: the
        // prefetch is the same request, issued earlier and on a worker, so leaving it unfiltered
        // would keep the whole cost the policy exists to remove — and pay it before the parse.
        Prefetcher().Prefetch(urls.Where(url =>
            Uri.TryCreate(url, UriKind.Absolute, out _) && MayFetchOverNetwork(url)));
    }

    /// <summary>
    /// The document's prefetcher, created on first use.
    /// </summary>
    /// <remarks>
    /// <b>Published atomically, because the callers are no longer all on one thread.</b> Item #17's
    /// scan calls <see cref="Prefetch"/> from a worker while the parse it overlaps may reach the
    /// post-parse stylesheet call on the main thread, so <c>??=</c> here would be the exact lazy-init
    /// race the Phase 2 findings record (§1): two prefetchers built, one published, and every
    /// request issued into the one that lost. The loser is discarded before it has issued anything —
    /// <see cref="SubResourcePrefetcher"/> starts requests in <c>Prefetch</c>, not in its
    /// constructor — so the cost of losing is an allocation.
    /// </remarks>
    private SubResourcePrefetcher Prefetcher() =>
        _prefetcher ??
        Interlocked.CompareExchange(ref _prefetcher, new SubResourcePrefetcher(LoadTextDirect), null) ??
        _prefetcher;

    /// <summary>
    /// Requests in flight for this document, created on the first <see cref="Prefetch"/>. Null
    /// until then, so a document that never prefetches carries no extra machinery at all.
    /// </summary>
    /// <remarks>
    /// <c>volatile</c> so the read in <see cref="LoadText"/> — a plain field read on the main
    /// thread — cannot observe the reference before the constructor's writes that
    /// <see cref="Prefetcher"/> published from a worker.
    /// </remarks>
    private volatile SubResourcePrefetcher? _prefetcher;

    /// <summary>
    /// Reads a local file <paramref name="path"/> as a sub-resource, applying the file read + MIME policy
    /// in one place (Phase 7 item 4) so a sub-document/object feature callback no longer inlines a
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

    /// <summary>Sends a prepared request (e.g. XMLHttpRequest).</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => SharedClient.SendAsync(request);

    /// <summary>Fetches <paramref name="url"/> (e.g. an iframe/object sub-document).</summary>
    public Task<HttpResponseMessage> GetAsync(string url) => SharedClient.GetAsync(url);
}
