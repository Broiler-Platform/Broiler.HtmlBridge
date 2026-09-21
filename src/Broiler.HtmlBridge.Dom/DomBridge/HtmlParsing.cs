using System.Collections.Generic;
using Broiler.Dom;
using Broiler.Dom.Html;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// Sibling partial peeled out of <c>DomBridge.cs</c> to keep the
/// facade under the 750-line guideline: initial HTML/doctype ingestion and inline-style parsing.
/// <see cref="ParseHtml"/> rebuilds the canonical document from an HTML string (clearing prior
/// runtime state, parsing the doctype, running the shared tree builder and reparenting into
/// <c>DocumentElement</c>); <see cref="DomBridgeUtils.ParseStyle"/> / <see cref="DomBridgeUtils.IsAcceptableInlineValue"/> apply
/// the shared CSS declaration-validation error recovery to inline <c>style</c> values. Pure
/// partial-class relocation — no signature, accessibility, or logic change.
/// </summary>
public sealed partial class DomBridge
{
    private void ParseHtml(string html)
    {
        // Multithreading roadmap item #17. Everything from here to the parse is CPU the
        // document's sub-resource fetches could have been overlapping with, and until now they did
        // not start until the parse had finished and the sheet list had been collected. Start the
        // speculative scan first, so its worker is issuing requests while this thread tears the old
        // document down and builds the new one.
        StartSpeculativePreloadScan(html);

        // Parse/re-parse tears down and rebuilds the live tree wholesale (ClearChildren + build);
        // these are document-construction mutations, not script mutations, so suppress observer
        // delivery for them (matching the prior explicit channel, which parse never drove).
        using var mutationSuppression = SuppressMutationDelivery();
        // One call clears both wrapper maps. Re-parse now also releases stale sub-document
        // wrappers (keyed by detached roots that no lookup can reach again) — observably
        // equivalent to before, but it stops them lingering until disposal.
        _jsObjects.Clear();
        ClearComputedPropsCache();
        // Clear the document first so the doctype/<html> re-append below satisfies canonical
        // DomDocument ordering (doctype must precede the document element).
        ClearChildren(_document);
        // A re-parse is a new document generation: drop the prior document's timers, listeners,
        // observers and message ports so re-attaching leaves no state from the previous document.
        ClearRuntimeSessionState();
        // A re-parsed document is a new generation: release the prior document's headless
        // layout view (and its renderer container) so geometry is document-scoped.
        DisposeLayoutView();

        // Publish the document's quirks mode for the render that follows on this
        // thread. Layout (which on the HTML-string path holds no back-reference to
        // the document) reads it while sizing the root/body boxes for the
        // quirks-mode fill-viewport behaviour. Every WPT render runs through this
        // parse before laying out, so the flag is set for the render that matters.
        Layout.DocumentModeContext.CurrentQuirksMode =
            Layout.DocumentModeContext.IsQuirksHtml(html);

        // The shared WHATWG-aligned tokenizer and tree builder parse into a document of their own. Its
        // <!DOCTYPE> node (name plus PUBLIC/SYSTEM identifiers) is moved across from there, and its <html>
        // element stays behind: that element's children and attributes are carried into the persistent
        // DocumentElement below. The doctype goes first, before DocumentElement is appended, because a
        // canonical DomDocument requires doctype-before-element.
        var parsed = HtmlDocumentParser.ParseDocument(html, document: null, NavigationParseOptions);
        var docElement = parsed.Document.DocumentElement ??
            throw new InvalidOperationException("The shared HTML parser did not produce a document element.");
        if (parsed.Document.DocumentType is { } doctype)
            _document.AppendChild(doctype);
        Title = parsed.Title;
        ClearChildren(DocumentElement);
        // Reparent ALL children (raw ChildNodes) so any
        // text/comment nodes directly under the parsed <html> survive — no-op on the old
        // homogeneous tree where every child was an element.
        foreach (var child in docElement.ChildNodes.ToArray())
        {
            DocumentElement.AppendChild(child);
        }

        // Copy attributes from the parsed <html> element to DocumentElement
        // so that attributes like lang="en", dir="rtl", etc. are preserved
        // during serialization.
        if (!string.IsNullOrEmpty(docElement.Id))
            DocumentElement.Id = docElement.Id;
        if (!string.IsNullOrEmpty(docElement.ClassName))
            DocumentElement.ClassName = docElement.ClassName;
        foreach (var attribute in docElement.Attributes.Values)
            SetAttr(DocumentElement, attribute.QualifiedName, attribute.Value);
        foreach (var kv in InlineStyle(docElement))
            InlineStyle(DocumentElement)[kv.Key] = kv.Value;


        // Connect DocumentElement to the canonical document (after the doctype) so that
        // document.firstChild works and structural pseudo-classes correctly detect the document
        // root boundary.
        if (!_document.ChildNodes.Contains(DocumentElement))
            _document.AppendChild(DocumentElement);

        // The shadow roots the tree builder attached are real ones on real hosts, and the pass that
        // confines a shadow tree's own style rules to that tree is skipped outright for a document
        // that has never attached one — a flag only attachShadow() used to set, and which the
        // post-parse pass this replaces set as it attached. Asking the finished tree once is
        // strictly less work than that walk-plus-attach was.
        _hasShadowRoots |= GetDescendantShadowRoots(DocumentElement).Any();

        // Stylesheet discovery is document-scoped and lazy through the shared
        // CssStyleEngine. A rebuilt document must not retain the prior engines.
        ResetComputedStyleEngines();
    }

    /// <summary>
    /// The switches the markup cannot answer for a <b>navigation</b>'s parse — the entry point
    /// <see cref="ParseHtml"/> is, in the Standard's terms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declarative shadow roots are on, because HTML §13.2.6.4.4 gates
    /// <c>&lt;template shadowrootmode&gt;</c> on the document's "allow declarative shadow roots" flag,
    /// and navigation is one of the entry points that sets it. The flag is the caller's to supply —
    /// the parser cannot see which entry point it is serving, and its conservative default is what
    /// keeps markup of unknown provenance from silently growing shadow trees.
    /// </para>
    /// <para>
    /// <b>The fragment paths deliberately do not take these options.</b> <c>innerHTML</c> is the entry
    /// point the Standard singles out as NOT setting the flag — that is what the "unsafe" in
    /// <c>setHTMLUnsafe</c> is about — so <c>TryBuildInnerHtmlFragmentContainer</c> keeps the overload
    /// without them. Nor do the two sub-document parses (a frame's resource, and <c>document.write</c>
    /// into one): the pass this replaces never reached those either, and turning the flag on there
    /// would be a behaviour change no finding asked for.
    /// </para>
    /// </remarks>
    private static readonly HtmlParseOptions NavigationParseOptions =
        new(AllowDeclarativeShadowRoots: true);
}

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
/// <em>when the request starts</em>: the stylesheet set is handed over from the source text before
/// the parse begins, rather than after the document has been parsed and its
/// <c>&lt;style&gt;</c>/<c>&lt;link&gt;</c> elements collected.
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

/// <summary>
/// The one place left that has to follow a <c>&lt;template&gt;</c>'s children into
/// <see cref="DomElement.TemplateContents"/> (HTML §4.12.3): the serialization walk.
/// </summary>
/// <remarks>
/// <para>
/// The fragment itself is the canonical element's, created with it by <c>Broiler.Dom</c> and filled
/// by the shared parser as the tree is built. What stood here before was a
/// <c>Dictionary&lt;DomElement, DomDocumentFragment&gt;</c> beside the tree plus a pass that moved
/// every parsed template's children into it afterwards — a workaround for a parser that did not
/// produce the contents, and the thing <c>Broiler.DOM</c> #22 was filed to retire.
/// </para>
/// <para>
/// A template built by script rather than parsed keeps its children on the <em>element</em>:
/// <c>t.appendChild(x)</c> appends to the element as it does in a browser, and only
/// <c>t.innerHTML</c> and <c>t.content</c> reach the fragment. Serialization reads the fragment
/// either way, which is why such a template serializes as empty — the answer the dependency's own
/// serializer gives.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>The node list serialization walks for <paramref name="node"/>: a template's contents
    /// fragment stands in for its (empty) own child list, and a textarea a script has written to
    /// stands in for its authored text.</summary>
    /// <remarks>
    /// A textarea's value <i>is</i> its child text (HTML §4.10.11), so a script's write has nowhere
    /// else to land at serialization time — there is no <c>value</c> attribute on one for the
    /// attribute path to carry. Document serialization already reflects it by rewriting the render
    /// projection; this is the same answer for the paths that do not go through the projection, of
    /// which <c>element.outerHTML</c> is the one pages read.
    /// <para>
    /// Textarea is not a raw-text element (only <c>script</c>, <c>style</c> and <c>noscript</c> are),
    /// so the node minted here is escaped on the way out exactly as an authored child would be.
    /// </para>
    /// </remarks>
    private IEnumerable<DomNode> SerializationChildrenOf(DomNode node)
    {
        if (node is not DomElement element)
            return node.ChildNodes;

        if (element.TemplateContents is { } contents)
            return contents.ChildNodes;

        if (element.TagName.Equals("textarea", StringComparison.OrdinalIgnoreCase) &&
            _formState.TryGetDirtyValue(element, out var dirty) && dirty is string raw)
        {
            return [CreateBridgeTextNode(raw)];
        }

        return element.ChildNodes;
    }
}
