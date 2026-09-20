using Broiler.CSS;
using Broiler.CSS.Dom;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.Runtime;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// <c>getComputedStyle()</c> resolves through the shared
/// <see cref="CssStyleEngine"/> (cascade, inheritance, custom
/// properties, shorthands, initial values) instead of the bridge's legacy
/// <c>BuildComputedStyleMap</c> cascade. The bridge still owns stylesheet
/// discovery, <c>&lt;link&gt;</c> fetching, CSSOM rule text, sub-document scoping,
/// and the JavaScript <c>CSSStyleDeclaration</c> wrapper; only the cascade and
/// computed-style authority moves into <c>Broiler.CSS.Dom</c>.
/// </summary>
public sealed partial class DomBridge
{
    // The per-document engine scopes, the GetComputedProps memo and the style-invalidation
    // batch state live in DocumentStyleContext, the single computed-style authority.
    private readonly DocumentStyleContext _styleContext = new();

    /// <summary>
    /// Serializes an element's live inline-style map (InlineStyleRuntimeState.Style) to a CSS
    /// declaration string for the canonical engine's cascade — the bridge's authoritative
    /// inline source. JS <c>el.style.X=</c> writes land in it first, then in the DOM <c>style</c>
    /// attribute the engine would otherwise read; anchor-resolver bakes land in the baked overlay.
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
            // FormControl table.
            var engine = new CssStyleEngine(new BridgeSelectorStateProvider(this));
            // Feed the bridge's live InlineStyleRuntimeState.Style map as the cascade's inline
            // source (see SerializeInlineStyleForEngine), so the engine reads JS-set inline from the
            // map, not the style attribute it is synced to (anchor bakes stay in the baked overlay).
            engine.SetInlineStyleSource(SerializeInlineStyleForEngine);
            return new ComputedStyleEngineScope(new CssStyleScopeBuilder(engine, StyleSheetLoader), engine);
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
                GetAttr(styleEl, "media"),
                GetStyleElementBaseUrl(styleEl)));

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
    /// external sheets that finish loading reach the engine exactly as before (a CSSOM edit also
    /// clears the per-element <c>GetComputedProps</c> memo, through <c>OnStyleSheetRulesMutated</c>, and a DOM
    /// edit to a sheet's text through <c>OnStyleSheetSourceMutation</c>), and the
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
                    partRules, CSS.Dom.CssOrigin.Author, GetAttr(styleEl, "media"), GetStyleElementBaseUrl(styleEl)));
        }
    }

    /// <summary>
    /// Gets the base URL for a stylesheet element (<c>&lt;style&gt;</c> or <c>&lt;link&gt;</c>)
    /// for resolving relative <c>@import</c> URLs.
    /// </summary>
    private string GetStyleElementBaseUrl(DomElement styleEl)
    {
        if (string.Equals(styleEl.TagName, "link", StringComparison.OrdinalIgnoreCase) &&
            TryGetAttribute(styleEl, "href", out var href) &&
            !string.IsNullOrEmpty(href))
        {
            return ResolveStyleSheetLinkUrl(href);
        }

        return DocumentBaseUrl();
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

/// <summary>
/// The element's inline-style state — the script-observable dictionary, its read-only view and the
/// properties script set — and the parity hooks that compare the bridge's computed style with the
/// canonical engine's.
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// The element's authoritative in-memory inline style dictionary (CSS kebab-case), relocated off the
    /// <c>Broiler.Dom.DomElement</c> facade into <see cref="InlineStyleRuntimeState.Style"/>. Lazily
    /// seeded once from the element's <c>style=</c> attribute; thereafter it is the source
    /// of truth for script writes (JS <c>element.style</c>), and serialize-time bakes land beside it in the
    /// overlay that <see cref="EffectiveInlineStyle"/> merges when the attribute is synced at serialization.
    /// </summary>
    internal Dictionary<string, string> InlineStyle(DomElement element)
    {
        // Handing out the mutable dictionary is the only inline-style write seam there is, so it is
        // also where a retained geometry snapshot has to be given up: an inline declaration never
        // reaches the DOM `style=` attribute at script time (it is synced only when a projection is
        // built), so DomDocument.Version cannot see the change. Counting the handout rather than the
        // write is deliberately conservative — a caller that only reads costs a rebuild, a caller
        // that writes can never go unnoticed. Read-only consumers on the geometry path call
        // InlineStyleForRead instead; see CurrentLayoutSnapshotKey.
        BridgeRuntimeStateEpoch.Bump();
        return InlineStyleForRead(element);
    }

    /// <summary>
    /// <see cref="InlineStyle"/> without the write-epoch bump, for callers that only read the
    /// dictionary. Reserved for the hot read paths a geometry query runs *after* the snapshot was
    /// built (the cascade's inline-style source and <c>EffectiveInlineStyle</c>): counting those as
    /// writes would invalidate the snapshot on every read and defeat the cache. Never hand the
    /// result to code that mutates it.
    /// </summary>
    internal Dictionary<string, string> InlineStyleForRead(DomElement element)
    {
        var state = InlineStyleStateFor(element);
        if (!state.StyleSeeded)
        {
            state.StyleSeeded = true;
            var styleAttr = element.GetAttribute("style");
            if (!string.IsNullOrEmpty(styleAttr))
            {
                foreach (var kv in ParseStyle(styleAttr))
                    state.Style[kv.Key] = kv.Value;
            }
        }
        return state.Style;
    }

    // Named bookkeeping seams for the set of inline-style properties explicitly set via JS
    // (element.style.foo = …, setProperty, cssText). The StyleDeclarationBinding module
    // records/clears these through these helpers instead of touching the runtime-state object directly;
    // the bridge's own serialization/computed-style paths read InlineStyleStateFor(...).JsSetStyleProps.
    internal void MarkInlineStylePropSetByJs(DomElement element, string property) =>
        InlineStyleStateFor(element).JsSetStyleProps.Add(property);

    internal void UnmarkInlineStylePropSetByJs(DomElement element, string property) =>
        InlineStyleStateFor(element).JsSetStyleProps.Remove(property);

    internal void ClearInlineStylePropsSetByJs(DomElement element) =>
        InlineStyleStateFor(element).JsSetStyleProps.Clear();

    internal IReadOnlyCollection<string> InlineStylePropsSetByJs(DomElement element) =>
        InlineStyleStateFor(element).JsSetStyleProps;

    /// <summary>Read-only diagnostic view of an element's script-observable inline-style map
    /// (<see cref="InlineStyle"/>). Serialize-time bakes, the anchor resolver's included, are not in it:
    /// they land in the overlay <see cref="EffectiveInlineStyle"/> merges.
    /// Visible only to <c>InternalsVisibleTo</c> assemblies — not part of the public surface, so it
    /// does not re-open a public facade seam.</summary>
    internal IReadOnlyDictionary<string, string> GetInlineStyleView(DomElement element) =>
        InlineStyle(element);

    /// <summary>Parity-test hook (DOM/CSS promotion §2.1): the bridge's own sparse
    /// computed-style projection. Paired with <see cref="GetSparseComputedStyleForParity"/>
    /// (the canonical engine's candidate replacement over the SAME synced engine) so a
    /// differential test can measure how close the canonical projection is before the
    /// higher-risk swap of the ~98 <c>GetComputedProps</c> call sites. Visible only to
    /// <c>InternalsVisibleTo</c> assemblies — not a public seam.</summary>
    internal Dictionary<string, string> GetComputedPropsForParity(DomElement element) =>
        GetComputedProps(element);

    /// <summary>Parity-test hook (DOM/CSS promotion §2.1): the canonical engine's
    /// <c>CssStyleEngine.GetSparseComputedStyle</c> over the element's synced scoped engine —
    /// the candidate replacement for <see cref="GetComputedPropsForParity"/>.</summary>
    internal IReadOnlyDictionary<string, string> GetSparseComputedStyleForParity(DomElement element) =>
        GetSyncedScopedEngine(element).GetSparseComputedStyle(element, sparseInheritance: true);
}

// Engine-typed for one thing: the adoptedStyleSheets assignment copies the array the page assigned in
// engine terms, because JSEAL has no way to enumerate an Array exotic's elements as the engine's own
// GetArrayElements does — holes included, byte for byte. See the adapter at the foot of this file.

/// <summary>
/// Constructable stylesheets (CSSOM) — <c>new CSSStyleSheet()</c>, its
/// <c>insertRule</c>/<c>deleteRule</c>/<c>replaceSync</c>/<c>replace</c> surface, and
/// <c>document.adoptedStyleSheets</c>. A constructed sheet is not tied to a
/// <c>&lt;style&gt;</c>/<c>&lt;link&gt;</c> element; it carries its own rule list and applies
/// to the document only while it is in <c>adoptedStyleSheets</c>. At serialization the adopted
/// sheets are emitted as synthetic <c>&lt;style&gt;</c> elements appended after the document's
/// own stylesheets, so the renderer applies them in the correct cascade order (the WPT
/// <c>css/cssom</c> constructable family, e.g.
/// <c>CSSStyleSheet-constructable-insertRule-base-uri</c>, which adopts a sheet whose rule sets
/// a <c>background-image</c>).
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>The live <c>document.adoptedStyleSheets</c> array (constructed sheets in
    /// application order). Lazily created; <see cref="JsValue.Missing"/> until first read.</summary>
    private JsValue _adoptedStyleSheets;

    /// <summary>Rule list backing each constructed <c>CSSStyleSheet</c> object, so the
    /// serialization pass can emit the adopted sheets' rules.</summary>
    /// <remarks>
    /// Keyed on the JSEAL handle rather than on the engine object, which is the same key: a handle over
    /// an object compares by the object's reference (<see cref="JsValue.Equals(JsValue)"/>), so a sheet
    /// looked up here is found by the identity it always was found by.
    /// </remarks>
    private readonly Dictionary<JsValue, List<CssRule>> _constructedSheetRules = [];

    private JsValue BuildConstructedStyleSheetObject(List<CssRule> rules)
    {
        var realm = Realm;
        var sheet = realm.NewObject();
        _constructedSheetRules[sheet] = rules;

        List<CssRule> CurrentRules() => rules;
        // A constructed sheet has no owner element and so no StyleSheetRuntimeState to mark — there
        // is nothing to reparse from, the list *is* the sheet. It still reaches the cascade through
        // adoptedStyleSheets, so an insertRule/deleteRule on it is a layout change no DOM mutation
        // records: move the epoch so a retained geometry snapshot is not answered from the pre-edit
        // rules. See BridgeRuntimeStateEpoch.
        static void MarkRulesMutated() => BridgeRuntimeStateEpoch.Bump();
        // A style rule's style writes through to this list too, reaching the adopted <style> the renderer is
        // handed; getComputedStyle never reads adopted sheets, so there is no computed style to invalidate.
        var ruleModel = new Dom.Features.StyleSheetRuleModel(CurrentRules, MarkRulesMutated);

        // ownerNode is null for a constructed sheet; href is null (no source URL).
        realm.DefineAccessor(sheet, "ownerNode", (in _) => JsValue.Null, null);
        realm.DefineAccessor(sheet, "href", (in _) => JsValue.Null, null);

        // disabled — a script flag on the sheet; a disabled adopted sheet does not apply.
        var disabled = false;
        realm.DefineAccessor(sheet, "disabled",
            (in _) => JsValue.Boolean(disabled),
            (in call) => { disabled = call.Length > 0 && call[0].AsBoolean; return JsValue.Undefined; });

        var liveCssRules = realm.NewObject();
        var lastSyncedRuleCount = 0;
        realm.DefineAccessor(liveCssRules, "length",
            (in _) => Dom.Features.StyleSheetBinding.JsStyleSheetsGetLength002Core(CurrentRules), null);
        realm.DefineValue(liveCssRules, "item",
            realm.NewMethod("item",
                (in call) => Dom.Features.StyleSheetBinding.JsStyleSheetsItem003Core(SyncLiveCssRulesIndices, liveCssRules, CurrentRules, in call), 1));

        void SyncLiveCssRulesIndices()
        {
            // The CSSOM view of the list: indices skip the rules a page does not see (see
            // StyleSheetBinding.IsCssomVisible), which stay in the list the cascade reads.
            var current = Dom.Features.StyleSheetBinding.CssomRules(CurrentRules());
            for (var i = 0; i < current.Count; i++)
                realm.DefineIndex(liveCssRules, (uint)i, Dom.Features.StyleSheetBinding.BuildCssRuleObject(realm, current[i], sheet, default, ruleModel));

            // Retiring an index is the one CSSOM operation JSEAL cannot express; see
            // StyleSheetBinding.RetireIndex, which is where the reasoning lives.
            for (var i = current.Count; i < lastSyncedRuleCount; i++)
                Dom.Features.StyleSheetBinding.RetireIndex(liveCssRules, (uint)i);

            lastSyncedRuleCount = current.Count;
        }

        realm.DefineAccessor(sheet, "cssRules",
            (in _) => Dom.Features.StyleSheetBinding.JsStyleSheetsGetCssRules004Core(SyncLiveCssRulesIndices, liveCssRules), null);
        realm.DefineValue(sheet, "insertRule",
            realm.NewMethod("insertRule",
                (in call) => Dom.Features.StyleSheetBinding.JsStyleSheetsInsertRule005Core(CurrentRules, MarkRulesMutated, SyncLiveCssRulesIndices, in call), 2));
        realm.DefineValue(sheet, "deleteRule",
            realm.NewMethod("deleteRule",
                (in call) => Dom.Features.StyleSheetBinding.JsStyleSheetsDeleteRule006Core(CurrentRules, MarkRulesMutated, SyncLiveCssRulesIndices, in call), 1));

        // replaceSync(text) — replace all rules from a CSS string (any @import is dropped per
        // spec). replace(text) does the same and returns an already-resolved promise of the sheet.
        void ReplaceFromText(string text)
        {
            rules.Clear();
            foreach (var rule in new CssParser().ParseStyleSheet(text).Rules)
            {
                if (rule is CssAtRule at && at.Name.Equals("import", StringComparison.OrdinalIgnoreCase))
                    continue;
                rules.Add(rule);
            }
            SyncLiveCssRulesIndices();
        }

        realm.DefineValue(sheet, "replaceSync",
            realm.NewMethod("replaceSync", (in call) =>
            {
                ReplaceFromText(call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
                return JsValue.Undefined;
            }, 1));
        realm.DefineValue(sheet, "replace",
            realm.NewMethod("replace", (in call) =>
            {
                ReplaceFromText(call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
                return ResolvedThenableWith(sheet);
            }, 1));

        return sheet;
    }

    /// <summary>An already-resolved thenable that yields <paramref name="value"/> — the
    /// promise <c>CSSStyleSheet.replace()</c> returns (resolving with the sheet itself).</summary>
    /// <remarks>
    /// A hand-built thenable rather than <see cref="IJsJobs.NewPromise"/>, deliberately and unchanged:
    /// the callback runs synchronously at <c>then()</c> rather than at a microtask checkpoint, which is
    /// what the render path this feeds depends on. Moving it to a real promise would be a behaviour
    /// change, not a migration.
    /// </remarks>
    private JsValue ResolvedThenableWith(JsValue value)
    {
        var realm = Realm;
        var thenable = realm.NewObject();

        JsValue Then(in JsCall call)
        {
            if (call.Length > 0 && call[0].IsFunction)
            {
                // The callback is its own receiver, which is what the engine-typed call frame passed.
                try { call.Realm.Invoke(call[0], call[0], [value]); }
                catch { /* a replace().then callback must not abort the render */ }
            }
            return thenable;
        }

        realm.DefineValue(thenable, "then", realm.NewMethod("then", Then, 1));
        realm.DefineValue(thenable, "catch", realm.NewMethod("catch", (in _) => thenable, 1));
        realm.DefineValue(thenable, "finally",
            realm.NewMethod("finally", (in call) =>
            {
                if (call.Length > 0 && call[0].IsFunction)
                {
                    try { call.Realm.Invoke(call[0], call[0]); }
                    catch { /* as above */ }
                }
                return thenable;
            }, 1));

        return thenable;
    }

    /// <summary>The live <c>document.adoptedStyleSheets</c> array, created on first access.</summary>
    private JsValue AdoptedStyleSheets() =>
        _adoptedStyleSheets.IsObject ? _adoptedStyleSheets : (_adoptedStyleSheets = Realm.NewArray());

    /// <summary>
    /// Emits each adopted stylesheet as a synthetic <c>&lt;style&gt;</c> appended after the
    /// document's own stylesheets, so the renderer applies the adopted rules in cascade order.
    /// A no-op when nothing is adopted. Runs from <see cref="ApplySerializationTransforms"/>.
    /// </summary>
    /// <remarks>
    /// The walk is by index rather than by the engine's hole-skipping enumerator, which reaches the same
    /// sheets: a hole reads as <c>undefined</c>, and the very next test — is this an object the
    /// constructed-sheet table knows — rejects it exactly as the enumerator's skip did.
    /// </remarks>
    private void ApplyAdoptedStyleSheets(DomElement root)
    {
        if (!_adoptedStyleSheets.IsObject)
            return;

        var realm = Realm;
        // Array length is a number by construction, so the handle carries it and no coercion is needed.
        var length = realm.GetProperty(_adoptedStyleSheets, "length").AsNumber;
        if (!(length > 0))
            return;

        var head = FindFirstElementByTagName(root, "head");
        var container = head ?? root;

        for (var i = 0u; i < length; i++)
        {
            var item = realm.GetIndex(_adoptedStyleSheets, i);
            if (!item.IsObject ||
                !_constructedSheetRules.TryGetValue(item, out var rules) ||
                rules.Count == 0)
                continue;

            if (realm.GetProperty(item, "disabled").AsBoolean)
                continue;

            var css = string.Join("\n", rules.Select(CssSerializer.Serialize));
            var styleElement = CreateBridgeElement("style");
            styleElement.TextContent = css;
            SetParent(styleElement, container);
            container.AppendChild(styleElement);
        }
    }

    // -------- the one engine-typed adapter (see the note above the constructable-stylesheets partial) --------

    /// <summary>Replaces <c>document.adoptedStyleSheets</c> with the assigned array's members
    /// (<c>document.adoptedStyleSheets = [sheet, …]</c>); <c>.push()</c> on the getter's array
    /// is handled directly by the returned array.</summary>
    /// <remarks>
    /// The signature is JSEAL's — its installer mints the accessor through the realm — but the copy
    /// itself is still made in engine terms, and that is a gap in the contract rather than an
    /// unmigrated caller. Every element of the assigned array, sheet objects and whatever else a page
    /// put there, has to survive the round trip byte for byte, and <see cref="IJsRealm"/> can mint an
    /// array from a span but cannot read one back the way the engine's own <c>GetArrayElements</c>
    /// does — with the hole treatment that decides whether <c>[a, , b]</c> copies as two members or
    /// three. Reconstructing that from <c>length</c> plus per-index reads would be a re-derivation of
    /// the answer the engine already has, so the unwrap stays until the contract can express it. A
    /// handle that carries no engine array — a primitive, or nothing assigned at all — empties the
    /// list, exactly as the narrowing cast this replaces did.
    /// </remarks>
    private void SetAdoptedStyleSheets(JsValue value)
    {
        var replacement = new JSArray();
        if (JsInterop.ToEngineValue(value) is JSArray array)
            foreach (var (_, item) in array.GetArrayElements(withHoles: false))
                replacement.Add(item);

        _adoptedStyleSheets = JsInterop.FromEngineObject(replacement);
    }
}
