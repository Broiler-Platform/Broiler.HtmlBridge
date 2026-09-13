using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

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
/// <para><b>A sub-document and a sub-window are both a <see cref="JsValue"/>, and what kept the second
/// one engine-typed was this remark.</b> It said the sub-window maps were keyed on a JS object that
/// <c>Dom.Features.SubWindowBinding</c> and <see cref="WindowContextManager"/> hold engine-typed, so
/// narrowing them here would break each of them at the seam rather than at a boundary. Neither held
/// one. The binding mints its window through the realm and the manager takes and answers handles
/// throughout; between them they crossed the seam at seven places, and all seven were there to reach
/// this class. The header comment that stood above this file was wrong in the same direction: it said
/// the binding's one unwrap served both this cache and <c>RegisterWindowMessaging</c>, and that method
/// had been taking the handle since the commit before the header was written.</para>
///
/// <para><b>Keyed on the handle itself rather than on <see cref="JsValue.ObjectIdentity"/>, and the
/// difference is the lifecycle.</b> The tables that key on the identity are weak and need a reference
/// to be weak about. These are strong maps emptied on demand (<see cref="RemoveContainerCaches"/>,
/// <see cref="ResetSession"/>), so the handle both readers already hold is the key, as it is for
/// <see cref="EventTargetRegistry"/>'s owner map. The reverse map takes the default comparer: for an
/// object handle <see cref="JsValue.Equals(JsValue)"/> compares kind and then the carried reference by
/// <c>ReferenceEquals</c>, and <see cref="JsValue.GetHashCode"/> hashes that reference with
/// <c>RuntimeHelpers.GetHashCode</c> — the two questions the reference comparer it used to take asked,
/// plus kind. Every key filed here is a plain object minted by <see cref="IJsValues.NewObject"/>, and
/// <c>SubWindowMapKeyTests</c> looks one up again under the handle a later property read produces.</para>
///
/// <para><b>The struct key opens exactly one hole, and <see cref="SetSubWindow"/> closes it.</b>
/// <see cref="JsValue.Missing"/> is <c>default</c>: a valid dictionary key that every absent handle
/// shares and hashes alike, so a <see cref="SetSubWindow"/> that tolerated one would file every
/// sub-window minted from an absent handle under a single reverse entry, silently. It throws, and it
/// throws rather than asserting because <c>eng/Broiler.Configurations.props</c> defines <c>DEBUG</c>
/// only for the Debug base configuration while the three CI jobs that run the suite build Release,
/// Release and Release-VM: a <c>Debug.Assert</c> would compile out of every one of them.
/// <c>Runtime/JsObjectRegistry.cs</c>'s <c>IdentityOf</c> is the same guard for the same reason.</para>
///
/// <para>The sub-window maps have deliberately asymmetric lifecycles, preserved from the pre-consolidation
/// code: the container→sub-window map (<see cref="TryGetSubWindow"/>) is dropped per container when a
/// sub-document is invalidated (<see cref="RemoveContainerCaches"/>), while the reverse sub-window→container
/// map is bulk-cleared only on session reset (<see cref="ResetSession"/>). Both are set together by
/// <see cref="SetSubWindow"/>.</para>
/// </remarks>
internal sealed class BrowsingContextManager
{
    // Per-container JS-object identity for the sub-document and sub-window objects. Both maps hold
    // handles; JsValue's own equality is reference equality for an object, so the identity rules
    // (`frame.contentDocument === frame.contentDocument`, and the same of `contentWindow`) are decided
    // exactly as they were. Both are keyed on the container element, so neither key moved.
    private readonly Dictionary<DomElement, JsValue> _subDocuments = [];
    private readonly Dictionary<DomElement, JsValue> _subWindows = [];

    // Per-container location / base-URL caches.
    private readonly Dictionary<DomElement, string> _subDocumentLocations = [];
    private readonly Dictionary<DomElement, string> _subDocumentBaseUrls = [];

    // Load-state marks.
    private readonly HashSet<DomElement> _objectLoadFailures = [];
    private readonly HashSet<DomElement> _onloadFired = [];

    // Reverse map: a sub-window handle → the container element it belongs to. Keyed on the handle with
    // the default comparer (a window's identity is the object the handle carries, and that is what
    // handle equality compares; see the class remarks). Bulk-cleared on session reset, never
    // per-container.
    private readonly Dictionary<JsValue, DomElement> _subWindowContainers = [];

    // P4.4b severed content documents: a nested-browsing-context container ↔ its canonical DomDocument.
    private readonly Dictionary<DomElement, DomDocument> _contentDocuments = [];
    private readonly Dictionary<DomDocument, DomElement> _documentContainers = [];

    /// <summary>The window whose context a nested-browsing-context script is currently running in
    /// (anything that is not an object, <see cref="JsValue.Missing"/> by default, = the main window).
    /// Owned here; the window-context switch saves/restores it.</summary>
    /// <remarks>
    /// Not nullable, and nothing normalises what is stored. Both readers are in
    /// <see cref="WindowContextManager"/>: <see cref="WindowContextManager.ResolveCurrentWindow"/> asks
    /// <see cref="JsValue.IsObject"/> of it, which treats a primitive exactly as the former write-side
    /// narrowing to <see langword="null"/> did, and <see cref="WindowContextManager.RunWithWindowContext"/>
    /// saves and restores it as it stands.
    /// </remarks>
    public JsValue CurrentWindowOverride { get; set; }

    // ── Sub-document JS-object identity ──────────────────────────────────────
    public bool TryGetSubDocument(DomElement container, out JsValue subDocument) =>
        _subDocuments.TryGetValue(container, out subDocument);
    public void SetSubDocument(DomElement container, JsValue subDocument) =>
        _subDocuments[container] = subDocument;

    // ── Sub-window JS-object identity (+ reverse container link) ─────────────
    public bool TryGetSubWindow(DomElement container, out JsValue subWindow) =>
        _subWindows.TryGetValue(container, out subWindow);
    /// <summary>Records the sub-window object for a container and its reverse container link.</summary>
    /// <remarks>
    /// <para>
    /// <b>A handle that is not an object is refused before either map is written.</b> The class remarks
    /// say why <see cref="JsValue.Missing"/> in particular would otherwise collapse every absent window
    /// onto one reverse entry, and why this is a throw rather than an assertion.
    /// </para>
    /// <para>
    /// The engine-typed signature refused the same case, but not here. Its one caller unwrapped the
    /// window on the line before, and that unwrap throws <see cref="InvalidOperationException"/> for a
    /// handle carrying no object, so no null ever reached this method. Had one arrived, the forward map
    /// was written first and the reverse map's null-key <see cref="ArgumentNullException"/> fired second,
    /// leaving the container half filed. A caller handing over a non-object sees the exception type it
    /// saw before, raised here and before any write rather than by the unwrap it ran before the call.
    /// </para>
    /// </remarks>
    public void SetSubWindow(DomElement container, JsValue subWindow)
    {
        if (!subWindow.IsObject)
            throw new InvalidOperationException(
                $"the sub-window map was given a handle that is not an object (kind {subWindow.Kind})");

        _subWindows[container] = subWindow;
        _subWindowContainers[subWindow] = container;
    }
    /// <summary>Every live sub-window object (used to canonicalise a window reference).</summary>
    public IEnumerable<JsValue> SubWindows => _subWindows.Values;
    /// <summary>Whether the handle is a known sub-window (has a reverse container link).</summary>
    /// <remarks>
    /// A handle that is not an object is a miss rather than a defect and needs no guard of its own:
    /// <see cref="SetSubWindow"/> never files one, so nothing that is not an object is in the map.
    /// </remarks>
    public bool IsSubWindow(JsValue window) => _subWindowContainers.ContainsKey(window);
    public bool TryGetSubWindowContainer(JsValue subWindow, out DomElement container) =>
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
        CurrentWindowOverride = JsValue.Missing;
    }
}
