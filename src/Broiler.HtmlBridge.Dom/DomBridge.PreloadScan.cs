namespace Broiler.HtmlBridge;

/// <summary>
/// The document's speculative preload scan — multithreading roadmap item #17. A worker reads the
/// raw source for the sub-resources the document names and starts their requests while this thread
/// parses the same source into a tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it changes, and what it deliberately does not.</b> Item #2 already split every
/// sub-resource call site into prefetch and consume, so nothing here changes which request is made,
/// which key it is stored under, or what the consuming site does with the bytes. It changes only
/// <em>when the request starts</em>: the stylesheet set used to be handed over once the whole
/// document had been parsed and its <c>&lt;style&gt;</c>/<c>&lt;link&gt;</c> elements collected, and
/// is now handed over from the source text before the parse begins.
/// </para>
/// <para>
/// <b>Only stylesheets are wired to a sink, and the other three families are not an oversight.</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Scripts</b> already have a prefetcher of their own, created by
/// <c>ScriptExtractionService.CreateScriptPrefetcher</c> from the host's own pre-parse scan — the
/// host needs the script list itself, so it pays for that pass whether or not this scan runs.
/// Adding a second prefetcher keyed the same way would issue the same requests twice under two
/// caches. The scan carries the script URLs for item #16 (compile-ahead), which is the consumer
/// that does not exist yet.
/// </description></item>
/// <item><description>
/// <b>Sub-documents</b> consume through a policy chain that yields <c>(content, contentType)</c> —
/// local base path, WPT host mapping, <c>file://</c>, MIME sniffing — and the text prefetcher
/// stores a <c>string</c>. Wiring them means a prefetcher over that tuple, not a call added here,
/// and on the paths this repository measures a frame resolves to a disk read rather than a round
/// trip, so it would be machinery bought for nothing measurable.
/// </description></item>
/// <item><description>
/// <b>Images</b> are item #8, whose consume site is <c>ImageLoadHandler</c> in <c>Broiler.HTML</c>.
/// The roadmap's §6 says #8 needs this scan to supply its URL set, and
/// <see cref="SpeculativePreloadResult"/> is where it will read it.
/// </description></item>
/// </list>
/// </remarks>
public sealed partial class DomBridge
{
    private SpeculativePreloadScan? _preloadScan;

    /// <summary>
    /// The scan's findings, blocking until its worker finishes, or <c>null</c> when no scan ran —
    /// the feature is off, or the source named nothing fetchable. Nothing on the render path reads
    /// this: the sink does the useful work on the worker. It is the seam items #8 and #16 consume.
    /// </summary>
    internal PreloadScanResult? SpeculativePreloadResult => _preloadScan?.Result;

    /// <summary>
    /// Starts the scan for <paramref name="html"/> and returns at once. Called first thing in
    /// <c>ParseHtml</c>, so the worker overlaps the whole of the teardown-and-rebuild that follows.
    /// </summary>
    /// <remarks>
    /// A re-parse starts a second scan without waiting for the first. The two share one
    /// <c>ResourceLoader</c>, so the worst case is that the outgoing document's sheets are requested
    /// once more than they would otherwise have been; the prefetcher collapses duplicate URLs onto
    /// one request, so in practice the second scan's sink finds the entries already there.
    /// </remarks>
    private void StartSpeculativePreloadScan(string html) =>
        _preloadScan = SpeculativePreloadScan.Start(
            html,
            _pageUrl,
            Csp,
            // Runs on the worker: this is the call that puts the requests on the wire while the
            // parse is still running, and the only reason the scan is worth doing at all.
            //
            // The RESOLVED URL is what is handed over, because that is what the consuming site
            // (`GetStyleElementSourceText` → `ResolveStyleSheetLinkUrl` → `FetchExternalStylesheet`)
            // passes to the loader — a prefetch stored under a differently-keyed URL would never be
            // consumed and would double the requests instead of overlapping them. The scanner
            // resolves against the same document base URL (its `<base href>`, else the page URL), so
            // the two keys agree. Both sides were the raw href until the consuming path started
            // resolving it; they must keep moving together. `minimumToOverlap: 1` because these
            // requests overlap the parse rather than each other, so a document with one sheet still
            // gains.
            result =>
            {
                // Best-effort, and a racy read of `_disposed` is the right strength for it: losing
                // the race issues a request whose bytes are discarded, which is what a prefetch for
                // a resource the document never reaches already costs. A lock here would be
                // synchronising the main thread's teardown against a speculation.
                if (!_disposed)
                    _resources.Prefetch(result.ResolvedUrls(PreloadKind.StyleSheet), minimumToOverlap: 1);
            });
}
