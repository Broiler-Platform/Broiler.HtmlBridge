using Broiler.Dom;
using Broiler.CSS.Dom;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge;

/// <summary>
/// Phase 4 cutover: <c>getComputedStyle()</c> resolves through the shared
/// <see cref="CssStyleEngine"/> (cascade, inheritance, custom
/// properties, shorthands, initial values) instead of the bridge's legacy
/// <c>BuildComputedStyleMap</c> cascade. The bridge still owns stylesheet
/// discovery, <c>&lt;link&gt;</c> fetching, CSSOM rule text, sub-document scoping,
/// and the JavaScript <c>CSSStyleDeclaration</c> wrapper; only the cascade and
/// computed-style authority moves into <c>Broiler.CSS.Dom</c>.
/// </summary>
public sealed partial class DomBridge
{
    // P2.3: the per-document engine scopes, the GetComputedProps memo and the style-invalidation
    // batch state now live in DocumentStyleContext, the single computed-style authority (was the
    // scattered _computedStyleEngines/_computedPropsCache/_computedPropsInProgress/
    // _styleInvalidationBatchDepth/_pendingStyleInvalidationRoots fields).
    private readonly DocumentStyleContext _styleContext = new();

    /// <summary>
    /// Serializes an element's live inline-style map (ElementRuntimeState) to a CSS
    /// declaration string for the canonical engine's cascade — the bridge's authoritative
    /// inline source, which includes JS <c>el.style.X=</c> writes and anchor-resolver-written
    /// geometry that never reach the DOM <c>style</c> attribute the engine would otherwise read.
    /// Returns <c>null</c> when there is no inline style.
    /// </summary>
    private string? SerializeInlineStyleForEngine(DomElement element)
    {
        // Read-only, and on the hot path: the cascade calls this once per element, including on the
        // geometry queries that run against an already-built snapshot. InlineStyle's write-epoch bump
        // would invalidate that snapshot on every read, so take the non-bumping accessor.
        var inline = InlineStyleForRead(element);
        if (inline.Count == 0)
            return null;
        var sb = new System.Text.StringBuilder();
        foreach (var kv in inline)
        {
            if (sb.Length > 0)
                sb.Append(';');
            sb.Append(kv.Key).Append(':').Append(kv.Value);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resets the per-scope computed-style engines. Called when the document tree
    /// is rebuilt so stale document roots do not retain engines or subscriptions.
    /// </summary>
    private void ResetComputedStyleEngines() => _styleContext.ResetEngines();

    /// <summary>
    /// Returns the shared <see cref="CssStyleEngine"/> for
    /// <paramref name="element"/>'s document root, creating it on first use and
    /// re-syncing its scoped stylesheet set (the same <c>&lt;style&gt;</c>/<c>&lt;link&gt;</c>/
    /// inserted-CSSOM text the legacy cascade saw) whenever that text changes.
    /// </summary>
    private CssStyleEngine GetSyncedScopedEngine(DomElement element)
    {
        var docRoot = GetDocumentRootFor(element);
        var scope = _styleContext.GetOrCreateEngineScope(docRoot, () =>
        {
            // Non-static so the `:checked` state provider can read this bridge's per-instance
            // FormControl table (Phase 2 item 4 de-globalization).
            var engine = new CssStyleEngine(new BridgeSelectorStateProvider(this));
            // Feed the bridge's live ElementRuntimeState inline map as the cascade's inline
            // source (see SerializeInlineStyleForEngine) so the engine sees JS-set and
            // anchor-resolver-written inline that never reaches the DOM style attribute.
            engine.SetInlineStyleSource(SerializeInlineStyleForEngine);
            return new ComputedStyleEngineScope(new CssStyleScopeBuilder(engine), engine);
        });

        var styleElements = GetScopedStyleElements(docRoot, scope);

        // Hand the collected sheets to the canonical scope builder in document order; it
        // gates each on the element's `media` attribute against the viewport and re-syncs the
        // engine only when the effective set changes. Text extraction (canonical DomText children /
        // CSSOM rule text / external-sheet runtime state) stays here because it needs the DOM and loading.
        // Prefetch pass (multithreading roadmap item #2): the whole sheet set is known here, and the
        // loop below fetches each external one at the moment it reaches it — serially. Issuing them
        // all now lets the round trips overlap; the loop still consumes them in document order, so
        // the cascade sees the same sheets in the same order.
        PrefetchExternalStylesheets(styleElements);

        var sources = new List<CssStyleScopeBuilder.StyleSource>(styleElements.Count);
        foreach (var styleEl in styleElements)
            sources.Add(new CssStyleScopeBuilder.StyleSource(
                GetStyleElementCssText(styleEl),
                CSS.Dom.CssOrigin.Author,
                GetAttr(styleEl, "media")));

        AppendOuterPartRules(docRoot, sources);

        var (vpWidth, vpHeight) = GetViewportForDocRoot(docRoot);
        return scope.ScopeBuilder.Sync(sources, new CssEnvironment(vpWidth, vpHeight));
    }

    /// <summary>
    /// The scope's contributing <c>&lt;style&gt;</c>/<c>&lt;link&gt;</c> elements, in document
    /// order, reusing the cached tree walk when the document has not been mutated since.
    /// </summary>
    /// <remarks>
    /// <see cref="GetSyncedScopedEngine"/> runs once per element resolved, and the walk behind it
    /// is over the whole tree — so the discovery cost was O(elements x nodes). On WPT's
    /// legacy-multibyte <c>*_chars*.html</c> encoding tests, which are a single line of ~17 000
    /// sibling <c>&lt;span&gt;</c>s and not one stylesheet, the anchor-registry pass walked ~34 000
    /// nodes ~17 000 times — about 5.8x10^8 node visits, every one of them discarding an empty
    /// result — and took ~7.5 minutes against the runner's 30-second per-test budget. That was 28
    /// of the 65 timeouts in the 2026-08-16 WPT run, in five directories whose names all point at
    /// text encoding; none of it was encoding code.
    /// <para>
    /// The cache holds only the walk. Sheet text is still read per call, so CSSOM edits and
    /// external sheets that finish loading are picked up exactly as before, and the
    /// <c>disabled</c> filter — which honours a CSSOM override the DOM never sees — is applied
    /// here rather than baked in. <see cref="DomDocument.Version"/> covers the rest: it is bumped
    /// by every mutation, so a tree edit or an attribute write invalidates this on the next call.
    /// A scope whose root has no owner document (a severed sub-document root) simply walks every
    /// time, as before.
    /// </para>
    /// </remarks>
    private List<DomElement> GetScopedStyleElements(DomElement docRoot, ComputedStyleEngineScope scope)
    {
        if (docRoot.OwnerDocument is not { } document)
            return FilterEnabled(CollectStyleSheetCandidatesInTree(docRoot));

        var version = document.Version;
        var snapshot = scope.StyleSheetCandidates;
        if (snapshot is null || snapshot.Version != version)
        {
            snapshot = new StyleSheetCandidateSnapshot(version, CollectStyleSheetCandidatesInTree(docRoot));
            scope.StyleSheetCandidates = snapshot;
        }

        return FilterEnabled(snapshot.Elements);

        List<DomElement> FilterEnabled(List<DomElement> found)
        {
            var enabled = new List<DomElement>(found.Count);
            foreach (var element in found)
            {
                if (!IsStyleSheetDisabled(element))
                    enabled.Add(element);
            }

            return enabled;
        }
    }

    /// <summary>
    /// Adds the enclosing tree's <c>::part()</c> rules to a shadow tree's style scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A shadow tree gets its own scope holding only the sheets inside it, which is exactly the
    /// encapsulation the spec asks for — with one sanctioned exception. <c>::part()</c> (CSS Shadow
    /// Parts) is how the outer tree styles elements the shadow tree deliberately exposes, so those
    /// rules, and only those, have to cross the boundary. Without this a document-level
    /// <c>::part(name)</c> rule painted correctly (the renderer sees the tree already flattened) yet
    /// was invisible to <c>getComputedStyle</c> and to everything reading computed values — which is
    /// how WPT auto-name-from-id-shadow lost the <c>view-transition-name</c> its part rule sets, and
    /// with it the element's whole view-transition capture.
    /// </para>
    /// <para>
    /// Appended after the shadow tree's own sheets: per CSS Scoping, declarations from the outer
    /// tree win over the inner tree's at equal specificity.
    /// </para>
    /// </remarks>
    private void AppendOuterPartRules(DomElement docRoot, List<CssStyleScopeBuilder.StyleSource> sources)
    {
        if (!docRoot.TagName.StartsWith('#') || ParentEl(docRoot) is not { } shadowHost)
            return;

        var outerStyles = new List<DomElement>();
        CollectStyleElementsInTree(GetDocumentRootFor(shadowHost), outerStyles);

        foreach (var styleEl in outerStyles)
        {
            // Lifting the rule into this scope is only half of it: the selector matcher does not
            // model ::part, so a rule whose subject is still `::part(name)` matches nothing once it
            // gets here. Re-emit it against the shadow element's own `part` attribute — inside this
            // scope every candidate is already a member of this tree, so the attribute alone says
            // what the pseudo said. Without this the part's declarations stayed invisible to
            // getComputedStyle, and a `view-transition-name` set from ::part never reached the
            // capture (WPT auto-name-from-id-shadow).
            var partRules = ExtractPartRulesForShadowScope(GetStyleElementCssText(styleEl));
            if (partRules.Length > 0)
                sources.Add(new CssStyleScopeBuilder.StyleSource(
                    partRules, CSS.Dom.CssOrigin.Author, GetAttr(styleEl, "media")));
        }
    }

    /// <summary>
    /// The <c>::part()</c> rules of a stylesheet, returned as CSS text. A brace-depth scan rather
    /// than a parse: it keeps each qualifying rule's own text verbatim, so the cascade sees exactly
    /// what the author wrote. Rules nested in an at-rule (<c>@media</c> …) are not lifted out — a
    /// <c>::part()</c> inside one still will not cross into a shadow tree.
    /// </summary>
    private static string ExtractPartRules(string css)
    {
        if (string.IsNullOrEmpty(css) || css.IndexOf("::part(", StringComparison.OrdinalIgnoreCase) < 0)
            return string.Empty;

        var kept = new System.Text.StringBuilder();
        var depth = 0;
        var ruleStart = 0;
        var preludeEnd = -1;

        for (var index = 0; index < css.Length; index++)
        {
            var character = css[index];
            if (character == '{')
            {
                if (depth == 0)
                    preludeEnd = index;
                depth++;
            }
            else if (character == '}')
            {
                depth--;
                if (depth != 0)
                    continue;

                if (preludeEnd > ruleStart &&
                    css.AsSpan(ruleStart, preludeEnd - ruleStart)
                        .IndexOf("::part(", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    kept.Append(css, ruleStart, index - ruleStart + 1).Append('\n');
                }

                ruleStart = index + 1;
                preludeEnd = -1;
            }
        }

        return kept.ToString();
    }

    /// <summary>
    /// Builds the computed-style map for <paramref name="element"/> through the
    /// shared <see cref="CssStyleEngine"/>, scoped to the
    /// element's document root.
    /// </summary>
    private Dictionary<string, string> BuildComputedStyleMapViaEngine(DomElement element, string? pseudoElement)
    {
        var computed = GetSyncedScopedEngine(element).GetComputedStyle(element, pseudoElement: pseudoElement);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in computed.Properties)
            map[pair.Key] = pair.Value;

        return map;
    }

    /// <summary>
    /// Returns the cascade-winning <em>declared</em> CSS property values for
    /// <paramref name="element"/> from matching stylesheet rules (no inline styles,
    /// inheritance, or initial-value backfill), via the shared style engine. This
    /// replaces the legacy <c>foreach (… in CssRules) if (MatchesSelector(…))</c>
    /// collection loops; callers still merge <c>InlineStyle(element)</c> on top as before.
    /// </summary>
    private Dictionary<string, string> CollectMatchedRuleProperties(DomElement element) =>
        new(GetSyncedScopedEngine(element).GetCascadedDeclaredValues(element), StringComparer.OrdinalIgnoreCase);
}
