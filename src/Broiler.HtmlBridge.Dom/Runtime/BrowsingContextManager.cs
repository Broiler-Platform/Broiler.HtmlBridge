using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The single owner of a document's nested-browsing-context state (HtmlBridge complexity-reduction
/// roadmap Phase 3, P3.16 — the browsing-context slice Phase 2/P2.6 deferred): the per-container
/// (<c>&lt;iframe&gt;</c>/<c>&lt;object&gt;</c>/<c>&lt;frame&gt;</c>) sub-document and sub-window JS-object
/// identity, their location/base-URL caches, the object load-failure and onload-fired marks, the
/// reverse sub-window→container map, the current-window override for the window-context switch, and the
/// P4.4b severed content-document maps. It replaces the ten fields that were scattered across
/// <c>SubDocuments.cs</c>, <c>DomBridge.WindowContext.cs</c> and <c>DomBridge.cs</c>.
/// </summary>
/// <remarks>
/// <para>The bridge keeps the browsing-context <em>algorithms</em> (sub-document/sub-window builders,
/// window resolution, resource loading, onload dispatch); they read and mutate this state through the
/// narrow surface here. Instance-scoped to the owning bridge/document.</para>
///
/// <para>The sub-window maps have deliberately asymmetric lifecycles, preserved from the pre-consolidation
/// code: the container→sub-window map (<see cref="TryGetSubWindow"/>) is dropped per container when a
/// sub-document is invalidated (<see cref="RemoveContainerCaches"/>), while the reverse sub-window→container
/// map is bulk-cleared only on session reset (<see cref="ResetSession"/>). Both are set together by
/// <see cref="SetSubWindow"/>.</para>
/// </remarks>
internal sealed class BrowsingContextManager
{
    // Per-container JS-object identity for the sub-document and sub-window objects.
    private readonly Dictionary<DomElement, JSObject> _subDocuments = [];
    private readonly Dictionary<DomElement, JSObject> _subWindows = [];

    // Per-container location / base-URL caches.
    private readonly Dictionary<DomElement, string> _subDocumentLocations = [];
    private readonly Dictionary<DomElement, string> _subDocumentBaseUrls = [];

    // Load-state marks.
    private readonly HashSet<DomElement> _objectLoadFailures = [];
    private readonly HashSet<DomElement> _onloadFired = [];

    // Reverse map: a sub-window JS object → the container element it belongs to. Reference-keyed
    // (a window's identity is its JS object). Bulk-cleared on session reset, never per-container.
    private readonly Dictionary<JSObject, DomElement> _subWindowContainers = new(ReferenceEqualityComparer.Instance);

    // P4.4b severed content documents: a nested-browsing-context container ↔ its canonical DomDocument.
    private readonly Dictionary<DomElement, DomDocument> _contentDocuments = [];
    private readonly Dictionary<DomDocument, DomElement> _documentContainers = [];

    /// <summary>The window whose context a nested-browsing-context script is currently running in
    /// (<c>null</c> = the main window). Owned here; the window-context switch saves/restores it.</summary>
    public JSObject? CurrentWindowOverride { get; set; }

    // ── Sub-document JS-object identity ──────────────────────────────────────
    public bool TryGetSubDocument(DomElement container, out JSObject subDocument) =>
        _subDocuments.TryGetValue(container, out subDocument!);
    public void SetSubDocument(DomElement container, JSObject subDocument) =>
        _subDocuments[container] = subDocument;

    // ── Sub-window JS-object identity (+ reverse container link) ─────────────
    public bool TryGetSubWindow(DomElement container, out JSObject subWindow) =>
        _subWindows.TryGetValue(container, out subWindow!);
    /// <summary>Records the sub-window object for a container and its reverse container link.</summary>
    public void SetSubWindow(DomElement container, JSObject subWindow)
    {
        _subWindows[container] = subWindow;
        _subWindowContainers[subWindow] = container;
    }
    /// <summary>Every live sub-window object (used to canonicalise a window reference).</summary>
    public IEnumerable<JSObject> SubWindows => _subWindows.Values;
    /// <summary>Whether the JS object is a known sub-window (has a reverse container link).</summary>
    public bool IsSubWindow(JSObject window) => _subWindowContainers.ContainsKey(window);
    public bool TryGetSubWindowContainer(JSObject subWindow, out DomElement container) =>
        _subWindowContainers.TryGetValue(subWindow, out container!);

    // ── Location / base-URL caches ───────────────────────────────────────────
    public bool TryGetLocation(DomElement container, out string location) =>
        _subDocumentLocations.TryGetValue(container, out location!);
    public void SetLocation(DomElement container, string location) =>
        _subDocumentLocations[container] = location;
    public bool TryGetBaseUrl(DomElement container, out string baseUrl) =>
        _subDocumentBaseUrls.TryGetValue(container, out baseUrl!);
    public void SetBaseUrl(DomElement container, string baseUrl) =>
        _subDocumentBaseUrls[container] = baseUrl;

    // ── Load-state marks ─────────────────────────────────────────────────────
    public bool HasObjectLoadFailed(DomElement objectElement) => _objectLoadFailures.Contains(objectElement);
    public void MarkObjectLoadFailed(DomElement objectElement) => _objectLoadFailures.Add(objectElement);
    public bool HasOnloadFired(DomElement element) => _onloadFired.Contains(element);
    public void MarkOnloadFired(DomElement element) => _onloadFired.Add(element);
    public void ClearOnloadFired(DomElement element) => _onloadFired.Remove(element);

    // ── Content documents (P4.4b) ────────────────────────────────────────────
    public DomDocument? GetContentDocument(DomElement container) =>
        _contentDocuments.TryGetValue(container, out var document) ? document : null;
    public DomElement? GetContainerForDocument(DomDocument document) =>
        _documentContainers.TryGetValue(document, out var container) ? container : null;
    /// <summary>Every nested browsing context's document this session has materialised — what a
    /// render-time pass has to walk to reach frames, which are severed from the main tree.</summary>
    public IEnumerable<DomDocument> ContentDocuments => _contentDocuments.Values;
    public void LinkContentDocument(DomElement container, DomDocument document)
    {
        _contentDocuments[container] = document;
        _documentContainers[document] = container;
    }
    /// <summary>Unlinks the container's content document (both directions) and returns the removed
    /// document so the caller can release its element runtime state; <c>null</c> if none.</summary>
    public DomDocument? UnlinkContentDocument(DomElement container)
    {
        if (!_contentDocuments.TryGetValue(container, out var document))
            return null;
        _documentContainers.Remove(document);
        _contentDocuments.Remove(container);
        return document;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────
    /// <summary>Drops the per-container caches when a sub-document is invalidated. Mirrors the
    /// pre-consolidation <c>InvalidateCachedSubDocument</c>: removes the container→sub-window entry but
    /// deliberately NOT the reverse sub-window→container entry (that is bulk-cleared on session reset).</summary>
    public void RemoveContainerCaches(DomElement container)
    {
        _subDocuments.Remove(container);
        _subWindows.Remove(container);
        _subDocumentLocations.Remove(container);
        _subDocumentBaseUrls.Remove(container);
    }

    /// <summary>Session reset (re-parse / disposal). Matches the pre-consolidation
    /// <c>ClearRuntimeSessionState</c>: bulk-clears the reverse sub-window→container map and the
    /// current-window override. The per-container caches keep their existing lifecycle (dropped via
    /// <see cref="RemoveContainerCaches"/>).</summary>
    public void ResetSession()
    {
        _subWindowContainers.Clear();
        CurrentWindowOverride = null;
    }
}
