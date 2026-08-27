using Broiler.Dom.Html;
using Broiler.HtmlBridge.Core.Diagnostics;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;
using Broiler.HtmlBridge.Internal.Scripting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Broiler.HtmlBridge;

/// <summary>
/// Extracts the contents of <c>&lt;script&gt;</c> tags from HTML using the shared
/// <c>Broiler.Dom.Html</c> tokenizer (Phase 7 item 2).  Inline scripts and <c>data:</c> URI scripts are
/// returned; external <c>src</c> references (http/https/file) are skipped by <see cref="Extract"/> but
/// resolved and fetched by <see cref="ExtractAll"/>.
/// </summary>
/// <remarks>
/// Discovery is parser-backed: the tokenizer treats <c>&lt;script&gt;</c> as a raw-text element, so a
/// <c>&lt;script&gt;</c> literal inside a comment or another element's text is not discovered, a
/// <c>&gt;</c> inside a quoted attribute no longer truncates the start tag, and attribute flags are read
/// from the parsed (lower-cased) attribute map rather than a per-tag regex. Script body text is taken
/// verbatim (raw text is never entity-decoded), so authorised inline/data-URI program text is unchanged.
/// </remarks>
public static partial class ScriptExtractionService
{
    private static readonly Regex WhitespacePattern = WhitespacePatternRegex();

    /// <summary>
    /// Shared <see cref="HttpClient"/> for fetching external scripts.
    /// A static singleton is intentional — Microsoft recommends reusing
    /// <see cref="HttpClient"/> instances to benefit from connection pooling
    /// and avoid socket exhaustion.
    /// </summary>
    /// <remarks>
    /// Identified, like every other loader: <see cref="HttpClient"/> sends no <c>User-Agent</c> of its
    /// own, and a host whose policy rejects an unidentified request rejects the script rather than
    /// serving a plainer one. mediawiki.org's <c>load.php?modules=startup</c> — the bootstrap that
    /// loads every other module on the page — answered <c>403 Forbidden</c> for exactly that reason,
    /// after the document and its stylesheets had already been fixed.
    /// See <see cref="Broiler.Layout.Net.BroilerUserAgent"/>.
    /// </remarks>
    private static readonly HttpClient SharedHttpClient =
        Layout.Net.BroilerUserAgent.Apply(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

    private static string? GetNonce(IReadOnlyDictionary<string, string> attrs) =>
        attrs.TryGetValue("nonce", out var nonce) ? nonce : null;

    private static string? GetType(IReadOnlyDictionary<string, string> attrs) =>
        attrs.TryGetValue("type", out var type) ? type : null;

    private static bool IsModule(IReadOnlyDictionary<string, string> attrs) =>
        ScriptMimeType.IsModule(GetType(attrs));

    /// <summary>
    /// Whether this <c>&lt;script&gt;</c> is executed at all. A type that is neither a JavaScript
    /// MIME essence nor <c>module</c> marks a data block — JSON-LD, framework state, speculation
    /// rules, an import map, a client-side template — which a browser never executes. Extraction
    /// used to look only for <c>type="module"</c>, so every one of those was collected and handed
    /// to the engine, where it failed to compile.
    /// </summary>
    private static bool IsExecutable(IReadOnlyDictionary<string, string> attrs) =>
        ScriptMimeType.IsExecutable(GetType(attrs));

    /// <summary>The <c>src</c> value when present and non-empty (an empty <c>src</c> is treated as no src).</summary>
    private static string? GetSrc(IReadOnlyDictionary<string, string> attrs) =>
        attrs.TryGetValue("src", out var src) && !string.IsNullOrEmpty(src) ? src : null;

    /// <summary>
    /// Resolves an authorised module's program text (Phase 7 item 6), by the same rules a classic script
    /// uses: an inline body must pass the CSP inline check; a <c>data:</c>/external source must pass the CSP
    /// external check, then is decoded / fetched. Returns <c>null</c> when blocked, empty, or unresolvable.
    /// </summary>
    private static string? ResolveModuleSource(
        ScriptSourceKind kind, string? url, string rawContent, string? nonce, ContentSecurityPolicy? csp, string? pageUrl,
        SubResourcePrefetcher? prefetcher = null)
    {
        switch (kind)
        {
            case ScriptSourceKind.Inline:
                var body = rawContent.Trim();
                return !string.IsNullOrEmpty(body) && (csp == null || csp.AllowsInlineScript(nonce, body)) ? body : null;

            case ScriptSourceKind.DataUri:
                if (csp != null && !csp.AllowsExternalScript(url!, pageUrl, nonce))
                    return null;
                var decoded = DecodeDataUri(url!);
                return string.IsNullOrEmpty(decoded) ? null : decoded;

            case ScriptSourceKind.External:
                if (csp != null && !csp.AllowsExternalScript(url!, pageUrl, nonce))
                    return null;
                var fetched = FetchExternalScript(url!, pageUrl, prefetcher);
                return string.IsNullOrEmpty(fetched) ? null : fetched;

            default:
                return null;
        }
    }

    /// <inheritdoc />
    public static IReadOnlyList<string> Extract(string html)
    {
        var scripts = new List<string>();
        var csp = ContentSecurityPolicy.FromHtml(html);

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
                if (csp != null && !csp.AllowsExternalScript(src, pageUrl: null, nonce))
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
            if (!string.IsNullOrEmpty(content) && (csp == null || csp.AllowsInlineScript(nonce, content)))
            {
                scripts.Add(content);
            }
        }

        return scripts;
    }

    /// <inheritdoc />
    public static ScriptExtractionResult ExtractAll(string html, string? pageUrl = null)
    {
        var scripts = new List<string>();
        var deferredScripts = new List<string>();
        var asyncScripts = new List<string>();
        var descriptors = new List<ScriptDescriptor>();
        var moduleMap = new ModuleMap();
        var moduleRoots = new List<ModuleRoot>();
        var moduleEntryKeys = new HashSet<string>(StringComparer.Ordinal);
        var csp = ContentSecurityPolicy.FromHtml(html);

        // Prefetch pass (roadmap item #2): every external script this document will fetch is
        // requested now, concurrently and bounded per host. The walk below is untouched — it still
        // resolves each script in document order, it just no longer starts each round trip itself.
        var prefetcher = CreateScriptPrefetcher(html, pageUrl, csp);

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

            var src = GetSrc(tag.Attributes);
            var kind = src == null ? ScriptSourceKind.Inline
                : src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? ScriptSourceKind.DataUri
                : ScriptSourceKind.External;
            var url = kind == ScriptSourceKind.Inline ? null : src;

            // Phase 7 item 6: record every recognised module in the module map so it is not silently
            // dropped, and collect the authorised top-level modules as roots of the import graph. Inline
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
                    var moduleSource = ResolveModuleSource(kind, url, tag.RawContent, nonce, csp, pageUrl, prefetcher);
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
                            moduleRoots.Add(new ModuleRoot(graphKey, moduleSource, baseUrl));
                    }
                }
            }

            // Resolve the program text for the classic execution buckets. Module scripts are recorded in
            // the descriptor list but omitted from execution here (item 6 wires them into the event loop).
            string? scriptContent = null;
            if (!isModule)
            {
                if (kind == ScriptSourceKind.DataUri)
                {
                    if (csp == null || csp.AllowsExternalScript(url!, pageUrl, nonce))
                    {
                        var decoded = DecodeDataUri(url!);
                        if (!string.IsNullOrEmpty(decoded))
                            scriptContent = decoded;
                    }
                }
                else if (kind == ScriptSourceKind.External)
                {
                    if (csp == null || csp.AllowsExternalScript(url!, pageUrl, nonce))
                    {
                        var fetched = FetchExternalScript(url!, pageUrl, prefetcher);
                        if (!string.IsNullOrEmpty(fetched))
                            scriptContent = fetched;
                    }
                }
                else
                {
                    var content = tag.RawContent.Trim();
                    if (!string.IsNullOrEmpty(content) && (csp == null || csp.AllowsInlineScript(nonce, content)))
                        scriptContent = content;
                }
            }

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

        // Phase 7 item 6 / tail: the authorised top-level module roots are the sole module-execution input.
        // A consumer drives the JS engine's own module machinery (BridgeModuleContext) to run each root; the
        // engine resolves+fetches its transitive imports itself (CSP-gated). The string-rewriting
        // EsModuleLinker fallback was retired once every surface took the engine path.
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
    public static string? FetchExternalScript(string scriptUrl, string? pageUrl) =>
        FetchExternalScript(scriptUrl, pageUrl, prefetcher: null);

    /// <summary>
    /// The same fetch, but consuming <paramref name="prefetcher"/> when one is supplied. The URL
    /// resolution, the ordering, and the value the caller gets back are unchanged — the only
    /// difference is that the request may already have been in flight since the document was
    /// scanned. Multithreading roadmap item #2.
    /// </summary>
    internal static string? FetchExternalScript(string scriptUrl, string? pageUrl, SubResourcePrefetcher? prefetcher)
    {
        try
        {
            // Resolve relative URLs against the page URL via the shared resolver.
            if (UrlResolver.Resolve(scriptUrl, pageUrl) is not { } resolvedUri)
                return null;

            var resolvedUrl = resolvedUri.AbsoluteUri;
            return prefetcher is not null
                ? prefetcher.Consume(resolvedUrl)
                : FetchResolvedScript(resolvedUrl);
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
    private static string? FetchResolvedScript(string resolvedUrl)
    {
        if (!Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var uri))
            return null;

        // Traced here rather than at the two callers, because this is the one place both of them
        // reach: the inline consume path and the prefetch worker alike. Inactive by default, and the
        // attempt is what carries the timing, so a slow script is visible as a slow script.
        var attempt = ResourceTrace.Begin(ResourceTraceKind.Script, resolvedUrl);
        try
        {
            // Handle file:// URLs — read from local filesystem
            if (uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                var path = uri.LocalPath;
                var fileContent = File.Exists(path) ? File.ReadAllText(path) : null;
                attempt.Completed(fileContent);
                return fileContent;
            }

            // Synchronous HTTP fetch.  ConfigureAwait(false) prevents
            // deadlocks when the caller is on a UI dispatcher.
            var content = SharedHttpClient.GetStringAsync(resolvedUrl)
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
    /// document-order walk that consumes them one at a time. Multithreading roadmap item #2.
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
    /// </remarks>
    internal static SubResourcePrefetcher? CreateScriptPrefetcher(
        string html,
        string? pageUrl,
        ContentSecurityPolicy? csp)
    {
        var urls = new List<string>();
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

            if (csp != null && !csp.AllowsExternalScript(src, pageUrl, GetNonce(tag.Attributes)))
                continue;

            if (UrlResolver.Resolve(src, pageUrl) is { } resolved && seen.Add(resolved.AbsoluteUri))
                urls.Add(resolved.AbsoluteUri);
        }

        if (urls.Count < 2)
            return null;

        var prefetcher = new SubResourcePrefetcher(FetchResolvedScript);
        prefetcher.Prefetch(urls);
        return prefetcher;
    }

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespacePatternRegex();
}
