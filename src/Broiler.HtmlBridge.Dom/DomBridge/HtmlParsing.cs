using System.Collections.Generic;
using Broiler.Dom;
using Broiler.Dom.Html;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// Sibling partial peeled out of <c>DomBridge.cs</c> (Phase 3 ratchet, 2026-07-17) to keep the
/// facade under the 750-line guard: initial HTML/doctype ingestion and inline-style parsing.
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
        // P2.2: one call clears both wrapper maps. Re-parse now also releases stale sub-document
        // wrappers (keyed by detached roots that no lookup can reach again) — observably
        // equivalent to before, but it stops them lingering until disposal.
        _jsObjects.Clear();
        ClearComputedPropsCache();
        // Clear the document first so the doctype/<html> re-append below satisfies canonical
        // DomDocument ordering (doctype must precede the document element).
        ClearChildren(_document);
        // A re-parse is a new document generation: drop the prior document's timers, listeners,
        // observers and message ports so re-attaching leaves no state from the previous document
        // (HtmlBridge complexity-reduction roadmap Phase 2, P2.1).
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
        var parsed = HtmlDocumentParser.ParseDocument(html);
        var docElement = parsed.Document.DocumentElement ??
            throw new InvalidOperationException("The shared HTML parser did not produce a document element.");
        if (parsed.Document.DocumentType is { } doctype)
            _document.AppendChild(doctype);
        Title = parsed.Title;
        ClearChildren(DocumentElement);
        // RF-BRIDGE-1c Phase F (F3c part 2d): reparent ALL children (raw ChildNodes) so any
        // text/comment nodes directly under the parsed <html> survive — no-op on the old
        // homogeneous tree where every child was an element.
        foreach (var child in docElement.ChildNodes.ToArray())
        {
            SetParent(child, DocumentElement);
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

        AttachDeclarativeShadowRoots(DocumentElement);

        // After the shadow-root pass has consumed the templates that declare one: every
        // remaining <template>'s children move into its contents fragment, which is where
        // HTML §4.12.3 has the parser put them. Order matters — a declarative shadow root's
        // template is not inert and its children belong in the shadow tree, not in a fragment.
        DivertTemplateContents(DocumentElement);


        // Stylesheet discovery is document-scoped and lazy through the shared
        // CssStyleEngine. A rebuilt document must not retain the prior engines.
        ResetComputedStyleEngines();
    }

    /// <summary>
    /// HTML §4.12.3: turns every <c>&lt;template shadowrootmode="open|closed"&gt;</c> into a real
    /// shadow root on its parent — the declarative counterpart of <c>attachShadow()</c>. The template
    /// contributes its children to the new root and is then dropped from the tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The spec does this inside the tree builder, as the template's end tag is seen. Doing it as a
    /// pass over the finished tree is equivalent for a parsed document — nothing observes the
    /// intermediate state, because script has not run yet — and keeps the shared
    /// <c>HtmlDocumentParser</c> free of a bridge-only concept.
    /// </para>
    /// <para>
    /// Only the shadow root is new here: everything downstream of it (styling the shadow tree,
    /// flattening the <c>#shadow-root</c> wrapper for the renderer, capturing it in a view
    /// transition) is the same machinery <c>attachShadow()</c> already drove, which is why
    /// imperative shadow content rendered while declarative content did not.
    /// </para>
    /// </remarks>
    private void AttachDeclarativeShadowRoots(DomElement root)
    {
        // Depth-first with an explicit stack: a shadow tree may itself contain declarative shadow
        // roots, and those templates only become reachable once their content has been moved.
        var pending = new Stack<DomNode>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            for (var index = current.ChildNodes.Count - 1; index >= 0; index--)
            {
                if (current.ChildNodes[index] is not DomElement child)
                    continue;

                if (current is DomElement element &&
                    TryTakeDeclarativeShadowRoot(element, child, index, out var shadowRoot))
                {
                    pending.Push(shadowRoot);
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    /// <summary>
    /// When <paramref name="child"/> is a declarative shadow-root template of <paramref name="host"/>,
    /// moves its children into a freshly attached shadow root, removes the template, and returns the
    /// root. Returns <see langword="false"/> for anything else, leaving the tree untouched.
    /// </summary>
    private bool TryTakeDeclarativeShadowRoot(
        DomElement host, DomElement child, int childIndex, out DomShadowRoot shadowRoot)
    {
        shadowRoot = null!;

        if (!string.Equals(child.TagName, "template", StringComparison.OrdinalIgnoreCase))
            return false;

        var mode = child.GetAttribute("shadowrootmode")?.Trim();
        if (!string.Equals(mode, "open", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "closed", StringComparison.OrdinalIgnoreCase))
            return false;

        // "Only the first declarative shadow root wins": a second template on the same host is an
        // ordinary inert <template>, per the spec's already-has-a-shadow-root check.
        if (host.InternalShadowRoot is not null)
            return false;

        var shadowMode = string.Equals(mode, "closed", StringComparison.OrdinalIgnoreCase)
            ? DomShadowRootMode.Closed
            : DomShadowRootMode.Open;
        _hasShadowRoots = true;
        try
        {
            shadowRoot = host.AttachShadow(shadowMode);
        }
        catch (DomException)
        {
            return false;
        }

        foreach (var content in child.ChildNodes.ToArray())
        {
            shadowRoot.AppendChild(content);
        }

        RemoveNthChild(host, childIndex);
        return true;
    }
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

/// <summary>
/// <c>HTMLTemplateElement</c>'s contents fragment: who owns a template's children, when they move
/// there, and the two places that have to follow them (HTML §4.12.3).
/// </summary>
/// <remarks>
/// Split out of <c>Utilities.cs</c> and <c>DomBridge/Serialization.cs</c> rather than left in them.
/// The pieces are one concern — the fragment, the parse-time divert that fills it, and the
/// serialization walk that reaches through to it — and the two files they came from are both over
/// the architecture guard's line limit, which asks for a feature partial rather than a fatter god
/// object.
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>Per-template contents fragment, minted on first access and kept stable
    /// afterwards, so <c>t.content === t.content</c> and a mutation through it survives.</summary>
    private readonly Dictionary<DomElement, DomDocumentFragment> _templateContents = new();

    /// <summary>
    /// The fragment behind <c>HTMLTemplateElement.content</c> — the template's <b>own</b> children,
    /// held in the fragment the specification puts them in rather than copied out of the tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HTML §4.12.3 has the parser put a template's children straight into this fragment, leaving
    /// the element itself childless, and that is now what happens: <see cref="DivertTemplateContents"/>
    /// moves them at the end of the parse. The element is the owner of the fragment, not a second
    /// copy of it.
    /// </para>
    /// <para>
    /// It used to build the fragment from a deep <em>copy</em>, leaving the children in the tree,
    /// and the deviations that followed were not confined to the two sides disagreeing. A template's
    /// contents were reachable from the document — <c>t.querySelector('.row')</c> found them, where
    /// a browser answers <c>null</c> because they are not in the tree at all — so a page walking
    /// itself processed markup it was meant to stamp later. And writing <c>t.innerHTML</c> rewrote
    /// the element's children while <c>content</c> kept the cached copy, so building a template
    /// dynamically and then stamping it produced the <em>old</em> markup, silently.
    /// </para>
    /// <para>
    /// A template created by <c>createElement</c> starts empty and stays that way: only the parser
    /// diverts, so <c>t.appendChild(x)</c> appends to the element as it does in a browser, and only
    /// <c>t.innerHTML</c> and <c>t.content</c> reach the fragment.
    /// </para>
    /// </remarks>
    private DomDocumentFragment GetTemplateContent(DomElement template)
    {
        if (_templateContents.TryGetValue(template, out var existing))
            return existing;

        var fragment = CreateBridgeDocumentFragment();
        _templateContents[template] = fragment;
        return fragment;
    }

    /// <summary>
    /// Moves every parsed <c>&lt;template&gt;</c>'s children into its contents fragment, so the
    /// element is left childless as HTML §4.12.3 requires. Runs once, at the end of the parse.
    /// </summary>
    /// <remarks>
    /// Depth-first over the whole tree including inside templates, because a template may contain
    /// another: the inner one is diverted into the outer one's fragment first and must still be
    /// diverted itself. Declarative shadow-root templates are already gone by this point — that pass
    /// consumes them — so what is left here is the inert kind.
    /// </remarks>
    private void DivertTemplateContents(DomNode root)
    {
        foreach (var node in root.InclusiveDescendants().ToArray())
        {
            if (node is not DomElement element ||
                !string.Equals(element.TagName, "template", StringComparison.OrdinalIgnoreCase))
                continue;

            var fragment = GetTemplateContent(element);
            foreach (var child in element.ChildNodes.ToArray())
            {
                element.RemoveChild(child);
                fragment.AppendChild(child);
                DivertTemplateContents(child);
            }
        }
    }

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

        if (IsTemplateElement(element))
            return GetTemplateContent(element).ChildNodes;

        if (element.TagName.Equals("textarea", StringComparison.OrdinalIgnoreCase) &&
            _formState.TryGetDirtyValue(element, out var dirty) && dirty is string raw)
        {
            return [CreateBridgeTextNode(raw)];
        }

        return element.ChildNodes;
    }
}
