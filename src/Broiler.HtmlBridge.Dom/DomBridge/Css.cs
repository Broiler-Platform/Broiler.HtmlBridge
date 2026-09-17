using System.Runtime.CompilerServices;
using System.Text;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Dom;
using Broiler.CSS;
using Broiler.CSS.Dom;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// CSS specificity calculation, style-block extraction, rule cascading,
/// computed-style building, and media-query evaluation.
/// </summary>
public sealed partial class DomBridge
{
    // Phase 2 item 4 (de-globalization, 2026-07-17): the per-style-element stylesheet state (fetched
    // CSS text, the live CSSOM rule list and its parse-source / mutated flags) was the StyleSheet slot
    // of the process-static ElementRuntimeState table; it is now a per-bridge instance table, owned by
    // the session's bridge. Still an element-keyed ConditionalWeakTable, so it GCs with the style
    // element and the cloneNode copy (see CloneDomElement) is preserved. All access is on the bridge
    // instance, so this needed no static-helper cascade.
    private readonly ConditionalWeakTable<DomElement, StyleSheetRuntimeState> _styleSheetRuntimeStates = [];

    private StyleSheetRuntimeState StyleSheetStateFor(DomElement element) =>
        _styleSheetRuntimeStates.GetValue(element, static _ => new StyleSheetRuntimeState());

    // P2.3: computed-style state (the GetComputedProps memo and the style-invalidation batch depth /
    // pending roots) moved to DocumentStyleContext (see _styleContext). The memo maps are concurrent
    // there for the same reason they were here — JS continuations on ThreadPool threads re-enter
    // computed-style/geometry work concurrently with the main-thread layout pass, and a plain
    // dictionary corrupts under that race and aborts the process/WPT shard (issue #1143).

    /// <summary>
    /// Clears the bridge's <c>GetComputedProps</c> memo <em>and</em> the per-document engines'
    /// cascade/computed-style caches together — the single computed-style invalidation route
    /// (see <see cref="DocumentStyleContext.InvalidateComputedStyle"/>). The two must invalidate as
    /// one because <c>GetComputedProps</c> routes through the engine's sparse projection, which reads
    /// inline style from the live <see cref="InlineStyleRuntimeState"/> table (a mutation there is
    /// invisible to the engine's own DOM-mutation subscription).
    /// </summary>
    private void ClearComputedPropsCache() => _styleContext.InvalidateComputedStyle();

    /// <summary>
    /// Records that a CSSOM edit changed a <c>&lt;style&gt;</c>/<c>&lt;link&gt;</c> sheet's rule list — an
    /// <c>insertRule</c>, a <c>deleteRule</c>, or a write through a style rule's <c>style</c> — and
    /// invalidates the computed style resolved from the list before the edit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this closes.</b> Setting <see cref="StyleSheetRuntimeState.RulesMutated"/> switches the
    /// text the style engine is handed from the author's source to the serialized model
    /// (<see cref="GetStyleElementCssText"/>), and the engine re-syncs whenever that text changes — which is
    /// why <c>getComputedStyle(el).color</c> followed every edit. But <c>GetComputedProps</c> memoises a map per
    /// element, and nothing on a CSSOM path cleared it. The memo is not keyed on the document version at all:
    /// a DOM edit clears it only where its binding calls <see cref="InvalidateStyleScope"/>, or, for an edit to
    /// a sheet's own text, through <see cref="OnStyleSheetSourceMutation"/> — and an edit to the rule list
    /// touches no DOM node, so it reaches neither. <c>getComputedStyle</c> takes <c>display</c> from that memo
    /// (<see cref="ApplyUserAgentDisplayToComputedStyle"/>), so <c>display</c> kept the value of the first
    /// read; so did every other reader of the memo — <c>clientTop</c>/<c>clientLeft</c>, hit testing,
    /// scrolling, the anchor resolver. It looked fixed whenever a layout pass ran in between, because
    /// rebuilding the geometry snapshot clears the memo on its own.
    /// </para>
    /// <para>
    /// <b>Why here.</b> This is the one report every edit of an element-backed sheet makes, and each makes it
    /// only after the list changed and only if it did (<c>JsStyleSheetsInsertRule005Core</c> after the parse
    /// succeeds, <c>JsStyleSheetsDeleteRule006Core</c> for a valid index, <c>StyleSheetRuleModel.Commit</c> for
    /// a rule still in the sheet), so a re-resolution it lets through always reads the edited list. Not in the
    /// <see cref="StyleSheetRuntimeState.RulesMutated"/> setter: that state has no way back to the bridge, and
    /// the flag is also written on read paths — the reparse in <see cref="EnsureStyleSheetRulesCurrent"/>,
    /// reached from inside <c>GetComputedProps</c>, and <c>StyleSheetRuntimeState.CopyTo</c> while a render
    /// projection is built — where dropping the memo would discard maps still being resolved.
    /// </para>
    /// <para>
    /// <b>Why <see cref="ClearComputedPropsCache"/> and not <see cref="InvalidateStyleScope"/>.</b> The memo
    /// and the engines' caches are the whole of what a sheet edit leaves stale, and that call clears them
    /// together, as <see cref="DocumentStyleContext.InvalidateComputedStyle"/> requires. The scope walk the
    /// other adds visits every element to prune inline-style keys neither the <c>style</c> attribute nor
    /// script set, which a sheet edit cannot have produced, and it would run per edit: pages building styles
    /// with CSS-in-JS insert rules by the thousand. The geometry snapshot needs nothing from here, because
    /// the <see cref="StyleSheetRuntimeState.RulesMutated"/> setter already moves
    /// <see cref="BridgeRuntimeStateEpoch"/>.
    /// </para>
    /// </remarks>
    private void OnStyleSheetRulesMutated(DomElement styleElement)
    {
        StyleSheetStateFor(styleElement).RulesMutated = true;
        ClearComputedPropsCache();
    }

    /// <summary>
    /// Invalidates computed style when a DOM mutation changed which sheets there are or what one says: the
    /// children of a <c>&lt;style&gt;</c>/<c>&lt;link&gt;</c> (its text), a character-data edit to one of them, or
    /// a sheet owner anywhere in a subtree that was added or removed. Subscribed to every document this bridge
    /// owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same stale memo as <see cref="OnStyleSheetRulesMutated"/>, reached from the DOM.</b> The engine
    /// reads a sheet's text again per resolution, so <c>color</c> followed <c>style.textContent = …</c>, but
    /// <c>display</c> is answered from the <c>GetComputedProps</c> memo, and the mutations that change a sheet's
    /// text or remove a sheet with its container do not go through a binding that calls
    /// <see cref="InvalidateStyleScope"/>: <c>textContent</c> is the canonical <see cref="DomNode.TextContent"/>
    /// setter, and <c>data</c>/<c>nodeValue</c>/<c>appendData</c> the canonical character-data one. Those
    /// publish to <see cref="DomDocument.Mutated"/> and nowhere else, which makes the mutation stream the one
    /// place every such edit is seen.
    /// </para>
    /// <para>
    /// <b>Why only sheet edits.</b> A <c>textContent</c> write that removes plain elements can make a map stale
    /// too (<c>:empty</c>, <c>:has()</c>). That gap is older than this handler and not closed by it: clearing on
    /// every child-list record would make this a second, general invalidation route beside the bindings' own.
    /// This one is scoped to the sheet source the engine re-reads, so a record that is not about a sheet costs a
    /// type test, and an added or removed subtree one walk of it.
    /// </para>
    /// <para>
    /// <b>Only the memo, as for a CSSOM edit</b>, because that and the engines' caches are all a sheet edit
    /// leaves stale. The render projection is a <see cref="DomDocument"/> of its own that nothing subscribes
    /// to, so the serialization transforms that rewrite sheet text there never reach this.
    /// </para>
    /// </remarks>
    private void OnStyleSheetSourceMutation(DomMutationRecord record)
    {
        var changesSheets = record.Type switch
        {
            DomMutationType.CharacterData => record.Target.ParentNode is DomElement owner && IsStyleSheetOwner(owner),
            DomMutationType.ChildList => (record.Target is DomElement target && IsStyleSheetOwner(target)) ||
                                         HoldsStyleSheetOwner(record.AddedNodes) || HoldsStyleSheetOwner(record.RemovedNodes),
            _ => false,
        };

        if (changesSheets)
            ClearComputedPropsCache();

        static bool HoldsStyleSheetOwner(IReadOnlyList<DomNode>? nodes) =>
            nodes is not null &&
            nodes.OfType<DomElement>().Any(node => IsStyleSheetOwner(node) || node.Descendants().OfType<DomElement>().Any(IsStyleSheetOwner));
    }

    // ------------------------------------------------------------------
    //  CSS specificity (Level 3) and <style> / <link> cascading
    // ------------------------------------------------------------------


    // htmlbridge-public-surface/v2 (declared 2026-07-10): the compatibility
    // `CssRules` tuple view and the `CalculateSpecificity` static delegation shim
    // were removed here (Milestone 1.1). They had no production callers; consumers
    // use the shared Broiler.CSS parser (`CssParser` / `CssStyleRule` /
    // `CssDeclarationBlock.GetPropertyValue`) and `CssSelectorParser.CalculateSpecificity`
    // directly. See docs/architecture/htmlbridge.md#canonical-owners-and-bridge-responsibilities.

    /// <summary>
    /// Clears any CSS-derived compatibility values left in the element's inline style
    /// (<see cref="InlineStyleRuntimeState.Style"/>, reached via <c>InlineStyle</c>)
    /// after a selector-affecting mutation. Stylesheet declarations are resolved lazily
    /// by the shared style engine; only inline declarations and JavaScript-set values
    /// remain in the bridge-owned declaration map.
    /// </summary>
    internal void InvalidateElementStyles(DomElement element)
    {
        // 1. Collect property names that come from the inline style attribute.
        //    These must never be cleared or overwritten by the cascade.
        var inlineStyleProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (TryGetAttribute(element, "style", out var inlineStyle) &&
            !string.IsNullOrEmpty(inlineStyle))
        {
            foreach (var kv in ParseStyle(inlineStyle))
                inlineStyleProps.Add(kv.Key);
        }

        // Remove all CSS-derived properties (keep inline ones AND JS-set ones).
        var keysToRemove = InlineStyle(element).Keys
            .Where(k => !inlineStyleProps.Contains(k) && !InlineStyleStateFor(element).JsSetStyleProps.Contains(k))
            .ToList();
        foreach (var key in keysToRemove)
            InlineStyle(element).Remove(key);
    }

    /// <summary>
    /// Recalculates CSS-derived inline styles for every element in the current
    /// document scope after a selector-affecting mutation such as a class,
    /// attribute, or sibling structure change.
    /// </summary>
    internal void BeginStyleInvalidationBatch() => _styleContext.BeginBatch();

    internal void EndStyleInvalidationBatch()
    {
        if (_styleContext.EndBatchShouldFlush())
            FlushPendingStyleInvalidations();
    }

    internal void InvalidateStyleScope(DomElement anchor)
    {
        ClearComputedPropsCache();
        var docRoot = GetDocumentRootFor(anchor);
        if (_styleContext.TryDeferRoot(docRoot))
            return;

        InvalidateStyleScopeRecursive(docRoot);
    }

    private void FlushPendingStyleInvalidations()
    {
        foreach (var root in _styleContext.DrainPendingRoots())
            InvalidateStyleScopeRecursive(root);
    }

    private void InvalidateStyleScopeRecursive(DomElement element)
    {
        if (!IsText(element) && !element.TagName.StartsWith('#'))
            InvalidateElementStyles(element);

        // Sub-documents keep their own style scope, but since P4.4b severed the #subdoc-root element
        // they are no longer in-tree children, so this walk never crosses a sub-document boundary.
        foreach (var child in ChildElements(element))
        {
            if (!IsText(child))
                InvalidateStyleScopeRecursive(child);
        }
    }

    /// <summary>
    /// Collects all <c>&lt;style&gt;</c> (and external-stylesheet <c>&lt;link&gt;</c>) elements from a
    /// document tree. Sub-documents keep their own style scope, but since P4.4b severed the
    /// <c>#subdoc-root</c> element they are no longer in-tree children, so this walk never crosses a
    /// sub-document boundary. Phase 4 item 4/5: reuses canonical <see cref="DomNode.Descendants"/>
    /// (document-order, level-snapshotted against the concurrent-mutation race the per-level
    /// <see cref="DomBridgeUtils.SnapshotChildren"/> walk guarded — Descendants snapshots the real child list, so the
    /// LegacyChildList projection overflow cannot occur here) instead of the hand-rolled recursion.
    /// </summary>
    private void CollectStyleElementsInTree(DomElement root, List<DomElement> styleElements)
    {
        foreach (var element in CollectStyleSheetCandidatesInTree(root))
        {
            // A disabled sheet (CSSOM CSSStyleSheet.disabled, or a <link disabled> content
            // attribute) does not contribute to the cascade — CSSOM §2.3.
            if (IsStyleSheetDisabled(element))
                continue;

            styleElements.Add(element);
        }
    }

    /// <summary>
    /// The tree walk behind <see cref="CollectStyleElementsInTree"/>, without the disabled filter:
    /// every <c>&lt;style&gt;</c> and external-stylesheet <c>&lt;link&gt;</c> in the tree, in
    /// document order.
    /// </summary>
    /// <remarks>
    /// Split out so the walk can be cached per document root (see
    /// <c>DomBridge.GetStyleSheetCandidates</c>) while the disabled filter stays live. The split is
    /// exactly where the cache can be keyed on <see cref="DomDocument.Version"/>: what this returns
    /// depends only on the tree and on element attributes, both of which bump that counter.
    /// <c>disabled</c> does not — <see cref="IsStyleSheetDisabled"/> also honours the CSSOM
    /// <c>CSSStyleSheet.disabled</c> override, which is set on bridge-side state and never touches
    /// the DOM — so it must be re-evaluated on every call and cannot be baked into the cache.
    /// </remarks>
    private List<DomElement> CollectStyleSheetCandidatesInTree(DomElement root)
    {
        var candidates = new List<DomElement>();
        foreach (var element in root.Descendants().OfType<DomElement>())
        {
            if (IsStyleSheetOwner(element))
                candidates.Add(element);
        }

        return candidates;
    }

    // The getComputedStyle result object is built by the Phase 3 (P3.14) StyleDeclarationBinding
    // feature module; the bridge still produces the engine-cascaded computed map here. The name keeps
    // saying "object" because that is what it builds — the JSEAL handle is over the realm's own object,
    // and both host contracts that reach this (IComputedStyleHost, ISubWindowHost) name it that way.
    private JsValue BuildComputedStyleObject(DomElement? element, string? pseudoElement = null)
    {
        var map = BuildComputedStyleMap(element, pseudoElement);
        // `overlay` (CSS Position 4) is UA-controlled — it is not in the author cascade, so the
        // engine map never carries it. Surface its computed value for getComputedStyle here (copying
        // first so a memoised engine map is never mutated). Pseudo-elements never enter the top layer.
        if (element != null && pseudoElement == null)
        {
            map = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase)
            {
                ["overlay"] = ComputeOverlayValue(element),
            };

            ApplyUserAgentDisplayToComputedStyle(element, map);
        }

        return Dom.Features.StyleDeclarationBinding.BuildComputedDeclaration(Realm, map);
    }

    private Dictionary<string, string> BuildComputedStyleMap(DomElement? element, string? pseudoElement = null)
    {
        // getComputedStyle() resolves through the shared Broiler.CSS.Dom.CssStyleEngine
        // (BuildComputedStyleMapViaEngine, see DomBridge/ComputedStyle.cs). The legacy
        // bridge computed-style cascade was retired in Phase 7 cleanup (RF-CSS-1); the engine
        // has been the sole getComputedStyle authority since the 2026-06-26 cutover, after it
        // gained the bridge's per-declaration value validation / error recovery and
        // border-shorthand reset semantics.
        if (element == null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return BuildComputedStyleMapViaEngine(element, pseudoElement);
    }

    private Dictionary<string, string> BuildSpecifiedStyleMap(DomElement element, string? pseudoElement = null)
    {
        pseudoElement = CssStyleEngine.NormalizePseudoElement(pseudoElement);
        var specified = new Dictionary<string, string>(
            GetSyncedScopedEngine(element).GetCascadedDeclaredValues(element, pseudoElement),
            StringComparer.OrdinalIgnoreCase);

        if (pseudoElement == null &&
            TryGetAttribute(element, "style", out var inlineStyleAttr) &&
            !string.IsNullOrEmpty(inlineStyleAttr))
        {
            foreach (var kv in ParseStyle(inlineStyleAttr))
                specified[kv.Key] = kv.Value;
        }

        return specified;
    }

    /// <summary>
    /// Raw author <em>source</em> text for a style element — its canonical text-node
    /// children, or a cached/fetched linked stylesheet — <em>without</em> any CSSOM
    /// <c>insertRule</c>/<c>deleteRule</c>
    /// mutations applied. This is the input from which the shared rule model is
    /// (re)parsed; <see cref="GetStyleElementCssText"/> applies mutations on top.
    /// </summary>
    private string GetStyleElementSourceText(DomElement styleEl)
    {
        var cssText = new StringBuilder();
        // RF-BRIDGE-1c Phase F (F3c part 2d): iterate raw ChildNodes — the <style> text is a
        // canonical DomText child, which ChildElements (OfType) would skip.
        foreach (var child in styleEl.ChildNodes)
        {
            if (IsText(child))
                cssText.Append(BridgeText(child));
        }

        if (string.Equals(styleEl.TagName, "link", StringComparison.OrdinalIgnoreCase) &&
            cssText.Length == 0 &&
            TryGetAttribute(styleEl, "href", out var href) &&
            !string.IsNullOrEmpty(href) &&
            IsExternalStyleAllowedByCsp(styleEl, href))
        {
            if (StyleSheetStateFor(styleEl).FetchedCss.TryGet(out var cachedCss) && cachedCss is string cachedStr)
            {
                cssText.Append(cachedStr);
            }
            else
            {
                try
                {
                    // Resolved against the document base URL first, then through the data:-aware
                    // seam rather than the loader directly.
                    //
                    // The resolution is what makes a linked sheet reach the CSSOM at all. The
                    // loader takes absolute URLs only (ResourceLoader.LoadTextDirect returns null
                    // for anything else), so passing the raw content attribute meant every
                    // *relative* href — the ordinary case — fetched nothing: the sheet's rules
                    // reached neither cssRules nor getComputedStyle, on file: and http(s) alike,
                    // while the renderer, which resolves the link itself, painted them. Paint and
                    // CSSOM had two different stylesheet sets and only paint had the linked one.
                    //
                    // The data: seam matters separately: a <link rel="stylesheet"
                    // href="data:text/css,…"> carries its own sheet and never goes on the wire.
                    // The loader only dispatches file/http(s), so a data: href fetched as an
                    // ordinary URL came back empty and the sheet — the whole sheet, for a link a
                    // script builds at run time — silently did not apply.
                    var fetchedCss = FetchStyleSheetText(ResolveStyleSheetLinkUrl(href));
                    if (!string.IsNullOrEmpty(fetchedCss))
                    {
                        StyleSheetStateFor(styleEl).FetchedCss.Set(fetchedCss);
                        cssText.Append(fetchedCss);
                    }
                }
                catch
                {
                    // Ignore stylesheet fetch failures in computed-style building.
                }
            }
        }

        return cssText.ToString();
    }

    /// <summary>
    /// Ensures the style element's live rule model
    /// (<see cref="StyleSheetRuntimeState.Rules"/>) reflects its current source text,
    /// reparsing when the source changed. Returns the shared mutable rule list — the
    /// single store behind the CSSOM (<c>cssRules</c>/<c>insertRule</c>/<c>deleteRule</c> and
    /// the writes a style rule's <c>style</c> passes through <c>StyleSheetRuleModel</c>),
    /// the renderer/legacy-cascade text, and the <c>getComputedStyle</c> engine sheet
    /// (Phase 6 store unification). Replacing the element's <c>textContent</c> changes
    /// the source text and thus discards prior CSSOM mutations, matching CSSOM semantics.
    /// </summary>
    private List<CssRule> EnsureStyleSheetRulesCurrent(DomElement styleEl)
    {
        var state = StyleSheetStateFor(styleEl);
        var sourceText = GetStyleElementSourceText(styleEl);
        if (state.Rules is null ||
            !string.Equals(state.RulesSourceText, sourceText, StringComparison.Ordinal))
        {
            state.Rules = [.. new CssParser().ParseStyleSheet(sourceText).Rules];
            state.RulesSourceText = sourceText;
            state.RulesMutated = false;
        }

        return state.Rules;
    }

    /// <summary>
    /// Enforces the Content Security Policy <c>style-src</c> family on the parsed
    /// DOM so blocked inline styles do not render: an inline <c>style="…"</c>
    /// attribute blocked by <c>style-src-attr</c> (→ <c>style-src</c> →
    /// <c>default-src</c>) is stripped, and a <c>&lt;style&gt;</c> element blocked
    /// by <c>style-src-elem</c> (same fallback chain) is removed. Only the style
    /// directives are consulted — script/event-handler enforcement is intentionally
    /// left to the script pipeline — so this is safe to call on any parsed document.
    /// </summary>
    public void ApplyStyleContentSecurityPolicy(ContentSecurityPolicy? csp)
    {
        if (csp == null || DocumentElement == null)
            return;

        // CSP §"Processing a `meta` element": a policy delivered by
        // <meta http-equiv="Content-Security-Policy"> is enforced from the point the parser reaches
        // the meta — markup already parsed is *not* retroactively blocked. Enforcing document-wide
        // stripped a style attribute that precedes the meta, including on the ancestors that
        // *contain* it: WPT content-security-policy/style-src/inline-style-attribute-on-html has
        // <html style="background-color: blue"> before a `style-src 'none'` meta, and rendered white
        // instead of blue.
        //
        // A pre-order walk visits an element's start tag in parse order, and visits ancestors before
        // descendants, so "not yet reached the meta" is exactly "this start tag was parsed first".
        // A policy with no meta in the document came from a header and applies document-wide.
        var policyMeta = FindCspMetaElement(DocumentElement);
        ApplyStyleCsp(
            DocumentElement, csp,
            blockStyleAttribute: !csp.AllowsInlineStyleAttribute(),
            policyMeta,
            enforcing: policyMeta == null);
    }

    /// <summary>
    /// The <c>&lt;meta http-equiv="Content-Security-Policy"&gt;</c> element that delivered the
    /// document's policy, in document order, or <c>null</c> when none is present (a header-delivered
    /// policy). Mirrors the acceptance rules of <c>CspMetaDiscovery.FindPolicyContent</c>, which
    /// parses the same meta out of the source text.
    /// </summary>
    private DomElement? FindCspMetaElement(DomElement element)
    {
        if (!IsText(element) &&
            element.TagName.Equals("meta", StringComparison.OrdinalIgnoreCase) &&
            TryGetAttribute(element, "http-equiv", out var httpEquiv) &&
            string.Equals(httpEquiv?.Trim(), "Content-Security-Policy", StringComparison.OrdinalIgnoreCase) &&
            TryGetAttribute(element, "content", out var content) &&
            !string.IsNullOrWhiteSpace(content))
        {
            return element;
        }

        foreach (var child in ChildElements(element))
        {
            var found = FindCspMetaElement(child);
            if (found != null)
                return found;
        }

        return null;
    }

    /// <summary>
    /// Walks the document in parse order applying the style-src family. <paramref name="enforcing"/>
    /// starts <c>false</c> for a meta-delivered policy and flips to <c>true</c> at
    /// <paramref name="policyMeta"/>; it is threaded through the walk (rather than being recomputed
    /// per element) so the flip is observed by every subsequent element in document order.
    /// </summary>
    private bool ApplyStyleCsp(
        DomElement element, ContentSecurityPolicy csp, bool blockStyleAttribute,
        DomElement? policyMeta, bool enforcing)
    {
        if (ReferenceEquals(element, policyMeta))
            enforcing = true;

        if (enforcing && !IsText(element))
        {
            if (element.TagName.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                var nonce = TryGetAttribute(element, "nonce", out var n) ? n : null;
                if (!csp.AllowsInlineStyleElement(nonce, GetStyleElementCssText(element)))
                {
                    element.Remove();
                    return enforcing;
                }
            }

            if (blockStyleAttribute && HasAttr(element, "style"))
            {
                RemoveAttr(element, "style");
                InlineStyle(element).Clear();
                InvalidateStyleScope(element);
            }
        }

        // Snapshot: a blocked <style> child removes itself from this collection.
        foreach (var child in ChildElements(element).ToArray())
            enforcing = ApplyStyleCsp(child, csp, blockStyleAttribute, policyMeta, enforcing);

        return enforcing;
    }

    /// <summary>
    /// The effective CSS text for a style element, as seen by the renderer/legacy
    /// cascade and the <c>getComputedStyle</c> engine. Returns the raw author source
    /// byte-for-byte while unmutated (so unchanged stylesheets are identical to
    /// pre-Phase-6), and the serialized live model once <c>insertRule</c>/<c>deleteRule</c>
    /// or a write to a style rule's <c>style</c> has mutated it — so script CSSOM mutations
    /// are observed downstream.
    /// </summary>
    private string GetStyleElementCssText(DomElement styleEl)
    {
        var rules = EnsureStyleSheetRulesCurrent(styleEl);
        var state = StyleSheetStateFor(styleEl);
        return state.RulesMutated
            ? string.Join("\n", rules.Select(CssSerializer.Serialize))
            : state.RulesSourceText ?? string.Empty;
    }

    /// <summary>
    /// Puts the user-agent stylesheet's <c>display</c> into a <c>getComputedStyle</c> map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="DomBridgeUtils.ApplyUserAgentDisplayDefaults"/> cannot be called on this map directly: it seeds
    /// only an <em>absent</em> <c>display</c>, and the engine's <c>GetComputedStyle</c> backfills
    /// initial values, so the key is always present — holding <c>inline</c>, the CSS initial value,
    /// for every element the UA sheet styles and no author rule touches. Nothing the UA sheet said
    /// about <c>display</c> therefore reached script: a plain <c>&lt;div&gt;</c> answered
    /// <c>inline</c> rather than <c>block</c>, and a <c>&lt;script&gt;</c> or <c>&lt;head&gt;</c>
    /// answered <c>inline</c> rather than <c>none</c>. Rendering was never affected — the renderer
    /// reads the box tree, and the bridge's own internal consumers read the sparse projection this
    /// borrows from — so it was a CSSOM gap alone.
    /// </para>
    /// <para>
    /// The value is taken from <c>GetComputedProps</c> rather than recomputed: that map is the
    /// engine's sparse projection (no initial-value backfill, so an undeclared property is absent)
    /// with the explicit-<c>inherit</c> fold and the UA seed already applied, and it is memoised per
    /// element. So an author or inline <c>display</c> still wins — the seed is non-clobbering — and
    /// the two paths cannot answer differently about what an element's display is.
    /// </para>
    /// </remarks>
    private void ApplyUserAgentDisplayToComputedStyle(DomElement element, Dictionary<string, string> map)
    {
        if (GetComputedProps(element).TryGetValue("display", out var display)
            && !string.IsNullOrWhiteSpace(display))
        {
            map["display"] = display;
        }
    }

    /// <summary>
    /// Determines the viewport width and height for media query evaluation
    /// based on the element's document root. For sub-documents inside iframes,
    /// the viewport is the iframe container's CSS dimensions. For the main
    /// document, the viewport is 0×0 (headless).
    /// </summary>
    private (int Width, int Height) GetViewportForDocRoot(DomElement docRoot)
    {
        if (ReferenceEquals(docRoot, DocumentElement) ||
            string.Equals(docRoot.TagName, "#document", StringComparison.OrdinalIgnoreCase))
            return (_viewportWidth, _viewportHeight);

        // docRoot is a severed sub-document's documentElement (<html>, post-P4.4b); its parent is
        // the content DomDocument. Recover the containing iframe/object via the reverse map to read
        // its CSS dimensions as the sub-viewport size (was ParentEl(#subdoc-root)).
        var parent = GetFrameForContentDocument(docRoot?.ParentNode);
        if (parent != null && !parent.TagName.StartsWith("#", StringComparison.Ordinal))
        {
            // parent is the iframe/object element — check its style for dimensions
            if (TryGetAttribute(parent, "style", out var style) && !string.IsNullOrEmpty(style))
            {
                var declarations = ParseStyle(style);
                var w = ExtractCssDimension(declarations, "width");
                var h = ExtractCssDimension(declarations, "height");
                if (w > 0 || h > 0)
                    return (w, h);
            }

            var attributeWidth = ParseViewportDimensionAttribute(GetAttr(parent, "width"));
            var attributeHeight = ParseViewportDimensionAttribute(GetAttr(parent, "height"));
            if (attributeWidth > 0 || attributeHeight > 0)
                return (attributeWidth, attributeHeight);

            // A stylesheet rule sizes the frame just as much as an inline style does, and only the
            // two shapes above were read — so `iframe { width: 50vw; height: 50vh }` left the frame
            // a 0×0 viewport. Everything resolved against it then collapsed: `dvw`/`vh` lengths
            // inside the frame, its media queries, and the pixel-sized backdrop the bridge
            // synthesizes for a modal <dialog> in it (WPT css-view-transitions/dialog-in-rtl-iframe,
            // whose scrim vanished at 0×0).
            if (CascadedFrameViewport(parent) is { } cascaded)
                return cascaded;
        }
        return (0, 0); // Default: headless 0×0 viewport
    }

    /// <summary>
    /// The frame element's cascaded <c>width</c>/<c>height</c> in pixels, or <c>null</c> when
    /// neither resolves to a length. Relative units are resolved against the viewport the frame
    /// itself lives in, which for a nested frame is its parent frame's — the same recursion the
    /// containing chain has.
    /// </summary>
    /// <remarks>
    /// Only lengths are read. A percentage needs the containing block, which this bridge does not
    /// measure here, and <c>auto</c> needs layout; both fall through to the caller's default rather
    /// than guessing a number the renderer would disagree with.
    /// </remarks>
    private (int Width, int Height)? CascadedFrameViewport(DomElement frame)
    {
        // The frame's own size is resolved through its containing document's engine, which resolves
        // that document's viewport in turn. Guard the walk: a cycle through the browsing-context
        // map (a frame reachable from its own document) would otherwise recur without end.
        if (!_frameViewportResolutions.Add(frame))
            return null;

        try
        {
            var (outerWidth, outerHeight) = GetViewportForDocRoot(GetDocumentRootFor(frame));
            var props = GetComputedProps(frame);
            var width = ResolveFrameLength("width");
            var height = ResolveFrameLength("height");
            return width > 0 || height > 0 ? (width, height) : null;

            int ResolveFrameLength(string property)
            {
                if (!props.TryGetValue(property, out var value) || string.IsNullOrWhiteSpace(value))
                    return 0;
                var px = ParseCssLengthToPixels(value.Trim(), outerWidth, outerHeight);
                return !double.IsNaN(px) && px > 0 ? (int)px : 0;
            }
        }
        finally
        {
            _frameViewportResolutions.Remove(frame);
        }
    }

    /// <summary>The frames whose viewport is being resolved right now — see
    /// <see cref="CascadedFrameViewport"/>.</summary>
    private readonly HashSet<DomElement> _frameViewportResolutions = new(ReferenceEqualityComparer.Instance);

    /// <summary>Author style rules (selector text + declarations) across every <c>&lt;style&gt;</c>
    /// and external-stylesheet <c>&lt;link rel="stylesheet"&gt;</c> in the tree, in document order.
    /// External links are included because the <c>::view-transition-*</c> pseudo rules and the
    /// <c>view-transition-group</c> values — which are read from the raw author rules here rather than
    /// through computed style — routinely live in a linked stylesheet (the WPT
    /// <c>css-view-transitions-2 nested</c> tests keep the entire pseudo tree styling in
    /// <c>resources/*.css</c>). A disabled sheet contributes nothing (CSSOM §2.3).</summary>
    private IEnumerable<(string SelectorText, CssDeclarationBlock Declarations)> EnumerateAuthorStyleRules(DomElement root)
    {
        foreach (var styleEl in root.Descendants().OfType<DomElement>())
        {
            if (!(styleEl.TagName.Equals("style", System.StringComparison.OrdinalIgnoreCase)
                    || IsExternalStylesheet(styleEl))
                || IsStyleSheetDisabled(styleEl))
                continue;

            var source = GetStyleElementSourceText(styleEl);
            if (string.IsNullOrEmpty(source))
                continue;

            CssStyleSheet sheet;
            try { sheet = new CssParser().ParseStyleSheet(source); }
            catch { continue; }

            foreach (var rule in sheet.Rules)
            {
                if (rule is not CssStyleRule styleRule)
                    continue;
                foreach (var selector in styleRule.Selectors.Selectors)
                    yield return (selector.Text, styleRule.Declarations);
            }
        }
    }
}
