using System.Collections.Concurrent;
using Broiler.CSS.Dom;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The single authority for a document's computed-style machinery (HtmlBridge complexity-reduction
/// roadmap Phase 2, P2.3): the per-document-root <see cref="CssStyleEngine"/> scopes, the bridge's
/// <c>GetComputedProps</c> memo (plus its re-entrancy in-progress map), and the style-invalidation
/// batch state. Consolidating these means there is one place that clears computed style and one
/// invalidation route — <see cref="InvalidateComputedStyle"/> — so an inline-style mutation and a
/// selector-affecting mutation cannot drift out of sync.
/// </summary>
/// <remarks>
/// This owns the <em>state</em>; the algorithms that need the DOM tree and resource loading
/// (collecting <c>&lt;style&gt;</c>/<c>&lt;link&gt;</c> text, walking the scope for recursive
/// invalidation, building an engine) stay in the bridge and call in. The computed-props maps are
/// concurrent for the same reason the timer maps are: JS continuations dispatched on ThreadPool
/// threads re-enter computed-style/geometry work concurrently with the main-thread layout pass, and
/// a plain dictionary corrupts under that race (issue #1143). Instance-scoped to the owning
/// bridge/document; <see cref="Reset"/> clears it on re-parse and disposal.
/// </remarks>
internal sealed class DocumentStyleContext
{
    // One engine scope per document root: keeps the engine's mutation-driven computed-style cache
    // and its single DomDocument.Mutated subscription intact across calls (rather than leaking a
    // subscription per getComputedStyle()).
    private readonly Dictionary<DomElement, ComputedStyleEngineScope> _engines = [];

    private readonly ConcurrentDictionary<DomElement, Dictionary<string, string>> _computedPropsCache = new();
    private readonly ConcurrentDictionary<DomElement, Dictionary<string, string>> _computedPropsInProgress = new();

    private int _batchDepth;
    private HashSet<DomElement>? _pendingRoots;

    // ------------------------------------------------------------------
    //  Per-document-root style engines
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the engine scope for <paramref name="documentRoot"/>, creating it via
    /// <paramref name="factory"/> on first use. The factory stays in the bridge because building an
    /// engine needs bridge-owned collaborators (the selector-state provider and the inline-style
    /// source).
    /// </summary>
    public ComputedStyleEngineScope GetOrCreateEngineScope(DomElement documentRoot, Func<ComputedStyleEngineScope> factory)
    {
        if (!_engines.TryGetValue(documentRoot, out var scope))
        {
            scope = factory();
            _engines[documentRoot] = scope;
        }

        return scope;
    }

    /// <summary>Drops every per-document engine scope so rebuilt document roots retain no engine or subscription.</summary>
    public void ResetEngines() => _engines.Clear();

    // ------------------------------------------------------------------
    //  GetComputedProps memo
    // ------------------------------------------------------------------

    public bool TryGetComputedProps(DomElement element, out Dictionary<string, string> props) =>
        _computedPropsCache.TryGetValue(element, out props!);

    public void SetComputedProps(DomElement element, Dictionary<string, string> props) =>
        _computedPropsCache[element] = props;

    public bool TryGetComputedPropsInProgress(DomElement element, out Dictionary<string, string> props) =>
        _computedPropsInProgress.TryGetValue(element, out props!);

    public void SetComputedPropsInProgress(DomElement element, Dictionary<string, string> props) =>
        _computedPropsInProgress[element] = props;

    public void RemoveComputedPropsInProgress(DomElement element) =>
        _computedPropsInProgress.TryRemove(element, out _);

    // ------------------------------------------------------------------
    //  The single computed-style invalidation route
    // ------------------------------------------------------------------

    /// <summary>
    /// Clears the <c>GetComputedProps</c> memo <em>and</em> every per-document engine's
    /// cascade/computed-style caches together. The two must invalidate as one because
    /// <c>GetComputedProps</c> routes through the engine's sparse projection, which reads inline
    /// style from the bridge's live ElementRuntimeState map — an ERS mutation is invisible to the
    /// engine's own DOM-mutation subscription, so clearing one without the other leaks stale values.
    /// </summary>
    public void InvalidateComputedStyle()
    {
        _computedPropsCache.Clear();
        foreach (var scope in _engines.Values)
            scope.Engine.InvalidateComputedStyleCaches();
    }

    // ------------------------------------------------------------------
    //  Style-invalidation batching
    // ------------------------------------------------------------------

    public void BeginBatch() => _batchDepth++;

    /// <summary>
    /// Ends a batch. Returns <c>true</c> when the outermost batch just closed and its deferred roots
    /// should now be flushed; <c>false</c> otherwise (nested batch still open, or none was open).
    /// </summary>
    public bool EndBatchShouldFlush()
    {
        if (_batchDepth == 0)
            return false;

        _batchDepth--;
        return _batchDepth == 0;
    }

    /// <summary>
    /// If a batch is open, records <paramref name="documentRoot"/> for deferred invalidation and
    /// returns <c>true</c>; otherwise returns <c>false</c> and the caller invalidates immediately.
    /// </summary>
    public bool TryDeferRoot(DomElement documentRoot)
    {
        if (_batchDepth == 0)
            return false;

        (_pendingRoots ??= []).Add(documentRoot);
        return true;
    }

    /// <summary>Returns the deferred roots and clears the pending set.</summary>
    public IReadOnlyCollection<DomElement> DrainPendingRoots()
    {
        if (_pendingRoots is null || _pendingRoots.Count == 0)
            return [];

        var roots = _pendingRoots.ToArray();
        _pendingRoots.Clear();
        return roots;
    }
}

/// <summary>
/// A document root's shared <see cref="CssStyleEngine"/> and its <see cref="CssStyleScopeBuilder"/>.
/// The engine is held directly so ElementRuntimeState-inline mutations (which the engine's
/// DOM-mutation subscription does not observe) can invalidate its computed caches.
/// </summary>
internal sealed class ComputedStyleEngineScope(CssStyleScopeBuilder scopeBuilder, CssStyleEngine engine)
{
    public CssStyleScopeBuilder ScopeBuilder { get; } = scopeBuilder;
    public CssStyleEngine Engine { get; } = engine;

    /// <summary>
    /// The scope's <c>&lt;style&gt;</c>/<c>&lt;link&gt;</c> elements as of
    /// <see cref="StyleSheetCandidatesVersion"/>, or <c>null</c> when never collected.
    /// </summary>
    /// <remarks>
    /// Discovering these means walking the whole tree, and the bridge asks for them once per
    /// element resolved — so on a document with many elements the walk is repeated per element and
    /// the pass is quadratic. A 17 000-span WPT encoding test spent about seven and a half minutes
    /// here, against a 30-second per-test budget, walking 34 000 nodes 17 000 times to discover the
    /// same (empty) sheet set every time. Caching the walk makes the pass linear.
    /// <para>
    /// Validity is the owning document's <see cref="DomDocument.Version"/>, which every DOM
    /// mutation bumps. That is the whole dependency: which elements are stylesheets is a function
    /// of the tree and of element attributes. Sheet <em>text</em> is deliberately NOT cached — the
    /// bridge re-reads it per call — so CSSOM <c>insertRule</c> and a late-arriving external sheet
    /// are still seen, and neither needs to invalidate anything.
    /// </para>
    /// </remarks>
    /// <para>
    /// Held as one snapshot object rather than a list plus a version field so a reader always sees
    /// the two agreeing. Computed style is re-entered from ThreadPool threads (the same race that
    /// made the computed-props maps concurrent, issue #1143), and with separate fields a reader
    /// could pair a freshly published list with a not-yet-published version — or worse, the
    /// reverse, and treat a stale list as current. One reference assignment cannot be torn, so the
    /// worst a race can now cost is a redundant walk.
    /// </para>
    public StyleSheetCandidateSnapshot? StyleSheetCandidates { get; set; }
}

/// <summary>
/// A scope's <c>&lt;style&gt;</c>/<c>&lt;link&gt;</c> elements together with the
/// <see cref="DomDocument.Version"/> they were collected at, which is what makes them reusable.
/// </summary>
internal sealed record StyleSheetCandidateSnapshot(ulong Version, List<DomElement> Elements);
