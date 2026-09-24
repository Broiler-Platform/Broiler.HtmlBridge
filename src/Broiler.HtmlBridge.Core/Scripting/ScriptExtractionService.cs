using Broiler.Dom.Html;
using Broiler.HtmlBridge.Core.Diagnostics;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;
using Broiler.HtmlBridge.Internal.Scripting;
using Broiler.Net.Http;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Broiler.HtmlBridge;

/// <summary>
/// Extracts the contents of <c>&lt;script&gt;</c> tags from HTML using the shared
/// <c>Broiler.Dom.Html</c> tokenizer.  Inline scripts and <c>data:</c> URI scripts are
/// returned; external <c>src</c> references (http/https/file) are skipped by <see cref="Extract"/> but
/// resolved and fetched by <see cref="ExtractAll(string, string?, ContentSecurityPolicy?, ScriptFetchContext?)"/>.
/// </summary>
/// <remarks>
/// Discovery is parser-backed: the tokenizer treats <c>&lt;script&gt;</c> as a raw-text element, so a
/// <c>&lt;script&gt;</c> literal inside a comment or another element's text is not discovered, a
/// <c>&gt;</c> inside a quoted attribute does not truncate the start tag, and attribute flags are read
/// from the parsed (lower-cased) attribute map rather than a per-tag regex. Script body text is taken
/// verbatim (raw text is never entity-decoded), so authorised inline/data-URI program text is unchanged.
/// </remarks>
public static partial class ScriptExtractionService
{
    private static readonly Regex WhitespacePattern = WhitespacePatternRegex();

    /// <summary>The budget for one external script, through the transport or the fallback client.</summary>
    private static readonly TimeSpan ScriptFetchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The fallback <see cref="HttpClient"/> for external scripts fetched without a
    /// <see cref="ScriptFetchContext"/> — a host with no profile network (tools, test runners).
    /// A static singleton is intentional — Microsoft recommends reusing
    /// <see cref="HttpClient"/> instances to benefit from connection pooling
    /// and avoid socket exhaustion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No cookies.</b> A default handler keeps an automatic, process-wide cookie jar: every
    /// <c>Set-Cookie</c> any page's script response carried was replayed to every later script request
    /// from any document, a jar no profile owned and nothing could clear. Cookies belong to the profile
    /// transport; without one there are none. Redirects are still followed automatically.
    /// </para>
    /// <para>
    /// Identified, like every other loader: <see cref="HttpClient"/> sends no <c>User-Agent</c> of its
    /// own, and a host whose policy rejects an unidentified request rejects the script rather than
    /// serving a plainer one. mediawiki.org's <c>load.php?modules=startup</c> — the bootstrap that
    /// loads every other module on the page — answered <c>403 Forbidden</c> for exactly that reason,
    /// after the document and its stylesheets had already been fixed.
    /// See <see cref="BroilerUserAgent"/>.
    /// </para>
    /// </remarks>
    private static readonly HttpClient SharedHttpClient =
        BroilerUserAgent.Apply(new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = ScriptFetchTimeout });

    private static string? GetNonce(IReadOnlyDictionary<string, string> attrs) =>
        attrs.TryGetValue("nonce", out var nonce) ? nonce : null;

    private static string? GetType(IReadOnlyDictionary<string, string> attrs) =>
        attrs.TryGetValue("type", out var type) ? type : null;

    private static bool IsModule(IReadOnlyDictionary<string, string> attrs) =>
        ScriptMimeType.IsModule(GetType(attrs));

    /// <summary>
    /// Whether this <c>&lt;script&gt;</c> is executed at all. A type that is neither a JavaScript
    /// MIME essence nor <c>module</c> marks a data block — JSON-LD, framework state, speculation
    /// rules, an import map, a client-side template — which a browser never executes. Looking only
    /// for <c>type="module"</c> instead would collect every one of those and hand it to the engine,
    /// where it fails to compile.
    /// </summary>
    private static bool IsExecutable(IReadOnlyDictionary<string, string> attrs) =>
        ScriptMimeType.IsExecutable(GetType(attrs));

    /// <summary>The <c>src</c> value when present and non-empty (an empty <c>src</c> is treated as no src).</summary>
    private static string? GetSrc(IReadOnlyDictionary<string, string> attrs) =>
        attrs.TryGetValue("src", out var src) && !string.IsNullOrEmpty(src) ? src : null;

    /// <summary>
    /// Resolves an authorised script's program text by the one set of rules classic scripts and
    /// modules share: an inline body must pass the CSP inline check; a <c>data:</c>/external source must
    /// pass the CSP external check, then is decoded / fetched. Returns <c>null</c> when blocked, empty,
    /// or unresolvable.
    /// </summary>
    /// <remarks>
    /// <paramref name="request"/> is how an external source is fetched: through the profile transport
    /// for the document, or, when <see langword="null"/>, through the fallback client.
    /// </remarks>
    private static string? ResolveScriptSource(
        ScriptSourceKind kind, string? url, string rawContent, string? nonce, ContentSecurityPolicySet csp, string? pageUrl,
        RequestPrefetcher<ScriptRequest?>? prefetcher = null, ScriptRequest? request = null)
    {
        switch (kind)
        {
            case ScriptSourceKind.Inline:
                var body = rawContent.Trim();
                return !string.IsNullOrEmpty(body) && csp.AllowsInlineScript(nonce, body) ? body : null;

            case ScriptSourceKind.DataUri:
                if (!csp.AllowsExternalScript(url!, pageUrl, nonce))
                    return null;
                var decoded = DecodeDataUri(url!);
                return string.IsNullOrEmpty(decoded) ? null : decoded;

            case ScriptSourceKind.External:
                if (!csp.AllowsExternalScript(url!, pageUrl, nonce))
                    return null;
                var fetched = FetchExternalScript(url!, pageUrl, prefetcher, request);
                return string.IsNullOrEmpty(fetched) ? null : fetched;

            default:
                return null;
        }
    }

    /// <summary>
    /// The authorised classic scripts in <paramref name="html"/> as program text, in document order:
    /// inline bodies and <c>data:</c> URI sources admitted by the policy the markup itself declares.
    /// Module scripts and external <c>src</c> references are skipped — <see cref="ExtractAll(string, string?, ContentSecurityPolicy?)"/> is the
    /// entry point that resolves those, separates the defer/async buckets and takes a delivered policy.
    /// </summary>
    public static IReadOnlyList<string> Extract(string html)
    {
        var scripts = new List<string>();
        var csp = new ContentSecurityPolicySet(ContentSecurityPolicy.FromHtml(html));

        foreach (var tag in HtmlScriptScanner.EnumerateScripts(html))
        {
            var nonce = GetNonce(tag.Attributes);

            // Skip data blocks: a type that is neither JavaScript nor `module` is not a script.
            if (!IsExecutable(tag.Attributes))
                continue;

            // Skip module scripts — they are extracted separately
            if (IsModule(tag.Attributes))
                continue;

            var src = GetSrc(tag.Attributes);

            // Check for data: URI src attribute
            if (src != null && src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (!csp.AllowsExternalScript(src, pageUrl: null, nonce))
                    continue;

                var decoded = DecodeDataUri(src);
                if (!string.IsNullOrEmpty(decoded))
                    scripts.Add(decoded);
                continue;
            }

            // Skip external (non-data:) src scripts
            if (src != null)
                continue;

            // Inline script
            var content = tag.RawContent.Trim();
            if (!string.IsNullOrEmpty(content) && csp.AllowsInlineScript(nonce, content))
            {
                scripts.Add(content);
            }
        }

        return scripts;
    }

    /// <summary>
    /// Walks every <c>&lt;script&gt;</c> in the document in document order and returns the classic
    /// execution buckets (regular, deferred, async), the per-script descriptors, the module map and
    /// the authorised top-level module roots.
    /// </summary>
    /// <param name="html">The document's markup.</param>
    /// <param name="pageUrl">The document's URL, which relative sources resolve against.</param>
    /// <param name="deliveredPolicy">
    /// A policy this document is bound by that its own markup did not declare, or
    /// <see langword="null"/>. Two things arrive this way: a LOCAL-SCHEME document —
    /// <c>about:srcdoc</c>, <c>about:blank</c>, <c>data:</c> — receives its embedder's policy,
    /// having no response of its own to carry one; and a document fetched over the network receives
    /// whatever its <c>Content-Security-Policy</c> response header delivered.
    /// <para>
    /// It is ADDITIONAL and never a replacement. A frame declaring a permissive <c>&lt;meta&gt;</c>
    /// cannot buy back what its embedder forbade, because both policies are enforced — which is the
    /// whole reason this is a <see cref="ContentSecurityPolicySet"/> below rather than a choice
    /// between two nullable policies.
    /// </para>
    /// </param>
    public static ScriptExtractionResult ExtractAll(
        string html,
        string? pageUrl = null,
        ContentSecurityPolicy? deliveredPolicy = null)
        => ExtractAll(html, pageUrl, deliveredPolicy, fetch: null);

    /// <summary>
    /// As <see cref="ExtractAll(string, string?, ContentSecurityPolicy?)"/>, fetching every external
    /// classic script and module root through the profile network in <paramref name="fetch"/>.
    /// </summary>
    /// <param name="html">The document's markup.</param>
    /// <param name="pageUrl">The document's URL, which relative sources resolve against.</param>
    /// <param name="deliveredPolicy">
    /// A policy this document is bound by that its own markup did not declare; see
    /// <see cref="ExtractAll(string, string?, ContentSecurityPolicy?)"/>.
    /// </param>
    /// <param name="fetch">
    /// The transport, the document the scripts belong to and its cancellation, or
    /// <see langword="null"/> for the fallback client (no cookies). With a context, a classic script is
    /// requested as HTML's <c>crossorigin</c> attribute says (no attribute: no-cors, credentials
    /// included); a module root is a CORS request with <c>same-origin</c> credentials, or
    /// <c>include</c> for <c>crossorigin="use-credentials"</c>. The Content-Security-Policy check a
    /// script passed is repeated for every redirect of its request. Speculative prefetches capture the
    /// request when they are queued.
    /// </param>
    public static ScriptExtractionResult ExtractAll(
        string html,
        string? pageUrl,
        ContentSecurityPolicy? deliveredPolicy,
        ScriptFetchContext? fetch)
    {
        var scripts = new List<string>();
        var deferredScripts = new List<string>();
        var asyncScripts = new List<string>();
        var descriptors = new List<ScriptDescriptor>();
        var moduleMap = new ModuleMap();
        var moduleRoots = new List<ModuleRoot>();
        var moduleEntryKeys = new HashSet<string>(StringComparer.Ordinal);
        var csp = new ContentSecurityPolicySet(deliveredPolicy, ContentSecurityPolicy.FromHtml(html));

        // Prefetch pass: every external script this document will fetch is requested now,
        // concurrently and bounded per host. The walk below resolves each script in document order
        // and does not start each round trip itself.
        var prefetcher = CreateScriptPrefetcher(html, pageUrl, csp, fetch);

        var documentOrder = 0;
        foreach (var tag in HtmlScriptScanner.EnumerateScripts(html))
        {
            // A data block (JSON-LD, framework state, speculation rules, an import map, a
            // client-side template) is not a script: it is skipped entirely — no descriptor, no
            // module-map entry, no execution bucket — rather than compiled and reported as a
            // syntax error in content that was never JavaScript.
            if (!IsExecutable(tag.Attributes))
                continue;

            var nonce = GetNonce(tag.Attributes);
            var isModule = IsModule(tag.Attributes);
            var isDefer = tag.Attributes.ContainsKey("defer");
            var isAsync = tag.Attributes.ContainsKey("async");
            var crossOrigin = GetCrossOrigin(tag.Attributes);

            var src = GetSrc(tag.Attributes);
            var kind = src == null ? ScriptSourceKind.Inline
                : src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? ScriptSourceKind.DataUri
                : ScriptSourceKind.External;
            var url = kind == ScriptSourceKind.Inline ? null : src;
            var request = kind == ScriptSourceKind.External
                ? ScriptRequestFor(fetch, isModule, crossOrigin, csp, pageUrl, nonce)
                : null;

            // Record every recognised module in the module map so it is not silently dropped, and
            // collect the authorised top-level modules as roots of the import graph. Inline
            // bodies plus data:/external sources are resolved through the same authorised decode/fetch path
            // as classic scripts; the graph loader (below) then resolves+fetches their transitive imports,
            // dedups, orders dependency-first, and links import/export. The classic buckets/descriptors
            // below are unchanged (modules stay out of them).
            if (isModule)
            {
                var moduleKey = kind == ScriptSourceKind.Inline ? $"inline:{documentOrder}" : url ?? $"module:{documentOrder}";

                // Module-map dedup: a module URL is fetched and evaluated once. Inline modules get a unique
                // per-occurrence key, so they never dedup; a repeated src module is recorded once.
                if (kind == ScriptSourceKind.Inline || !moduleMap.TryGet(moduleKey, out _))
                {
                    var moduleSource = ResolveScriptSource(kind, url, tag.RawContent, nonce, csp, pageUrl, prefetcher, request);
                    moduleMap.Add(new ModuleMapEntry(documentOrder, kind, moduleKey, url, moduleSource, IsExecutable: moduleSource != null));

                    if (moduleSource != null)
                    {
                        // The graph key must be the resolved absolute URL so a module's relative imports
                        // resolve against it and repeated modules dedup; inline/data keep a synthetic/data key.
                        var graphKey = kind switch
                        {
                            ScriptSourceKind.Inline => $"inline:{documentOrder}",
                            ScriptSourceKind.DataUri => url!,
                            _ => UrlResolver.Resolve(url!, pageUrl)?.AbsoluteUri ?? url!,
                        };
                        var baseUrl = kind == ScriptSourceKind.Inline ? pageUrl : graphKey;
                        if (moduleEntryKeys.Add(graphKey))
                            moduleRoots.Add(new ModuleRoot(graphKey, moduleSource, baseUrl) { CrossOrigin = crossOrigin });
                    }
                }
            }

            // Resolve the program text for the classic execution buckets. Module scripts are recorded in
            // the descriptor list but omitted from execution here; the module roots carry them instead.
            string? scriptContent = isModule
                ? null
                : ResolveScriptSource(kind, url, tag.RawContent, nonce, csp, pageUrl, prefetcher, request);

            descriptors.Add(new ScriptDescriptor(
                DocumentOrder: documentOrder++,
                Kind: kind,
                Url: url,
                Nonce: nonce,
                IsAsync: isAsync,
                IsDefer: isDefer,
                IsModule: isModule,
                Content: scriptContent ?? string.Empty));

            if (scriptContent == null) continue;

            if (isDefer)
                deferredScripts.Add(scriptContent);
            else if (isAsync)
                asyncScripts.Add(scriptContent);
            else
                scripts.Add(scriptContent);
        }

        // The authorised top-level module roots are the sole module-execution input. A consumer drives
        // the JS engine's own module machinery (BridgeModuleContext) to run each root; the engine
        // resolves+fetches its transitive imports itself (CSP-gated).
        return new ScriptExtractionResult(scripts, deferredScripts, asyncScripts, descriptors, moduleMap, moduleRoots);
    }

    /// <summary>
    /// Decodes a <c>data:</c> URI into its text content.
    /// Supports percent-encoding and base64 payloads.
    /// </summary>
    public static string DecodeDataUri(string dataUri)
    {
        if (!dataUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var rest = dataUri[5..]; // strip "data:"
        var commaIdx = rest.IndexOf(',');
        if (commaIdx < 0)
            return string.Empty;

        var meta = rest[..commaIdx];
        var payload = rest[(commaIdx + 1)..];

        if (meta.Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            // Percent-decode first (some Acid3 data URIs percent-encode the base64)
            var decoded = Uri.UnescapeDataString(payload);
            // Strip whitespace (RFC 2045 allows folding)
            decoded = WhitespacePattern.Replace(decoded, string.Empty);
            try
            {
                var bytes = Convert.FromBase64String(decoded);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException)
            {
                return string.Empty;
            }
        }
        else
        {
            return Uri.UnescapeDataString(payload);
        }
    }

    /// <summary>
    /// Resolves and downloads an external script from an HTTP/HTTPS/file URL.
    /// Relative URLs are resolved against the page <paramref name="pageUrl"/>.
    /// Returns the script text content, or <c>null</c> on failure.
    /// </summary>
    /// <remarks>
    /// This overload has no document and no profile, so it fetches through the fallback client, which
    /// sends and keeps no cookies.
    /// </remarks>
    public static string? FetchExternalScript(string scriptUrl, string? pageUrl) =>
        FetchExternalScript(scriptUrl, pageUrl, prefetcher: null, request: null);

    /// <summary>
    /// The same fetch for a script of a known document: through the profile transport when
    /// <paramref name="request"/> is set, otherwise through the fallback client.
    /// </summary>
    internal static string? FetchExternalScript(string scriptUrl, string? pageUrl, ScriptRequest? request) =>
        FetchExternalScript(scriptUrl, pageUrl, prefetcher: null, request);

    /// <summary>
    /// The same fetch, but consuming <paramref name="prefetcher"/> when one is supplied and its request
    /// for the URL was issued the way <paramref name="request"/> would be. The URL resolution, the
    /// ordering, and the value the caller gets back are unchanged — the only difference is that the
    /// request may already have been in flight since the document was scanned.
    /// </summary>
    internal static string? FetchExternalScript(
        string scriptUrl, string? pageUrl, RequestPrefetcher<ScriptRequest?>? prefetcher, ScriptRequest? request)
    {
        try
        {
            // Resolve relative URLs against the page URL via the shared resolver.
            if (UrlResolver.Resolve(scriptUrl, pageUrl) is not { } resolvedUri)
                return null;

            var resolvedUrl = resolvedUri.AbsoluteUri;
            return prefetcher is not null && prefetcher.TryConsume(resolvedUrl, request, out var prefetched)
                ? prefetched
                : FetchResolvedScript(resolvedUrl, request, pageUrl);
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "ScriptExtractor.FetchExternalScript",
                $"Failed to fetch external script '{scriptUrl}': {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// Fetches an already-resolved absolute script URL. This is the blocking primitive: it runs
    /// inline at the call site when there is no prefetcher, and on a prefetch worker when there is.
    /// </summary>
    /// <param name="resolvedUrl">The absolute script URL.</param>
    /// <param name="request">The request as the requesting document makes it, or null for the fallback client.</param>
    /// <param name="pageUrl">The requesting document's URL when there is no <paramref name="request"/>.</param>
    private static string? FetchResolvedScript(string resolvedUrl, ScriptRequest? request, string? pageUrl)
    {
        if (!Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var uri))
            return null;

        // Traced here rather than at the two callers, because this is the one place both of them
        // reach: the inline consume path and the prefetch worker alike. Inactive by default, and the
        // attempt is what carries the timing, so a slow script is visible as a slow script.
        var attempt = ResourceTrace.Begin(ResourceTraceKind.Script, resolvedUrl);
        try
        {
            // Handle file:// URLs — read from local filesystem, for a file: document only
            // (LocalFileAccess): an http(s) page must not run a local script as its own, nor name a
            // UNC path that Windows would open an SMB session to.
            if (uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                var allowed = request is { } fileRequest
                    ? LocalFileAccess.AllowedFor(fileRequest.Fetch.Document.DocumentUrl)
                    : LocalFileAccess.AllowedFor(pageUrl);
                if (!allowed)
                {
                    RenderLogger.LogWarning(LogCategory.JavaScript, "ScriptExtractor.FetchExternalScript",
                        $"Refused local script '{resolvedUrl}' for a document that is not a file: document.");
                    attempt.Completed(null);
                    return null;
                }

                var path = uri.LocalPath;
                var fileContent = File.Exists(path) ? File.ReadAllText(path) : null;
                attempt.Completed(fileContent);
                return fileContent;
            }

            // Synchronous HTTP fetch: through the profile transport for the document when there is
            // one, which sends and stores its cookies per hop, otherwise through the cookie-less
            // fallback client. ConfigureAwait(false) prevents deadlocks when the caller is on a UI
            // dispatcher; the transport's synchronous Send never captures a context either.
            var content = request is { } scriptRequest
                ? BridgeTransport.GetText(
                    scriptRequest.Fetch.Transport, uri, scriptRequest.Context, ScriptFetchTimeout,
                    scriptRequest.Fetch.CancellationToken)
                : SharedHttpClient.GetStringAsync(resolvedUrl)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            attempt.Completed(content);
            return content;
        }
        catch (Exception ex)
        {
            attempt.Failed(ex);
            throw;
        }
    }

    /// <summary>
    /// Issues concurrent requests for every external script the document will fetch, before the
    /// document-order walk that consumes them one at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scan applies the <em>same</em> CSP check the consuming walk applies. Prefetching a
    /// blocked script would put a request on the wire that the policy forbids — the request itself
    /// is the thing CSP is stopping, so "we fetched it but did not run it" is not a defensible
    /// reading of the policy.
    /// </para>
    /// <para>
    /// A document with fewer than two external scripts gets no prefetcher: there is no round trip
    /// to overlap, and the sequential path is then bit-for-bit the code that ran before.
    /// </para>
    /// <para>
    /// Each prefetch carries the request its script will be fetched with, built here from the same
    /// <paramref name="fetch"/> context, so the speculation sends the cookies and credentials mode the
    /// document-order fetch would. A script whose consuming request differs from the one in flight
    /// (the same URL as a classic script and as a module) is fetched again by its consumer.
    /// </para>
    /// </remarks>
    internal static RequestPrefetcher<ScriptRequest?>? CreateScriptPrefetcher(
        string html,
        string? pageUrl,
        ContentSecurityPolicySet csp,
        ScriptFetchContext? fetch = null)
    {
        var requests = new List<(string Url, ScriptRequest? Request)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tag in HtmlScriptScanner.EnumerateScripts(html))
        {
            // Prefetching what the document will fetch means what it will fetch *as a script*: a
            // data block's src is never requested by the walk below, so requesting it here would
            // be a round trip the page never makes.
            if (!IsExecutable(tag.Attributes))
                continue;

            var src = GetSrc(tag.Attributes);
            if (src is null || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var nonce = GetNonce(tag.Attributes);
            if (!csp.AllowsExternalScript(src, pageUrl, nonce))
                continue;

            if (UrlResolver.Resolve(src, pageUrl) is { } resolved && seen.Add(resolved.AbsoluteUri))
                requests.Add((resolved.AbsoluteUri,
                    ScriptRequestFor(fetch, IsModule(tag.Attributes), GetCrossOrigin(tag.Attributes), csp, pageUrl, nonce)));
        }

        if (requests.Count < 2)
            return null;

        var prefetcher = new RequestPrefetcher<ScriptRequest?>(
            (url, request) => FetchResolvedScript(url, request, pageUrl), ScriptRequest.Same);
        prefetcher.Prefetch(requests);
        return prefetcher;
    }

    /// <summary>
    /// The request an external script of this document is fetched with, or <see langword="null"/>
    /// without a fetch context. Its host policy is the Content-Security-Policy check the script's URL
    /// passed before the fetch, applied again to every URL a redirect leads to.
    /// </summary>
    private static ScriptRequest? ScriptRequestFor(
        ScriptFetchContext? fetch, bool isModule, string? crossOrigin, ContentSecurityPolicySet csp, string? pageUrl, string? nonce) =>
        fetch?.ForScript(isModule, crossOrigin, (url, _) => csp.AllowsExternalScript(url.AbsoluteUri, pageUrl, nonce));

    /// <summary>The <c>crossorigin</c> attribute's value, or <see langword="null"/> when absent.</summary>
    private static string? GetCrossOrigin(IReadOnlyDictionary<string, string> attrs) =>
        attrs.TryGetValue("crossorigin", out var value) ? value : null;

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespacePatternRegex();
}
