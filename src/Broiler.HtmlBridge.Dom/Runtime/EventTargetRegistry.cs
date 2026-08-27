using System.Runtime.CompilerServices;
using Broiler.Dom;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The single owner of a document's event-listener stores (HtmlBridge complexity-reduction roadmap
/// Phase 2, P2.5): the per-node <c>addEventListener</c> listeners, the window listeners, the generic
/// JS-target (message port / sub-window) listeners, the target→owner-window map used to route generic
/// dispatch, and the visual-viewport <c>scroll</c> listeners. It replaces the listener dictionaries
/// that were spread across the bridge and — for node listeners — moves them off the process-global
/// <c>ElementRuntimeState</c> table onto an instance-scoped store.
/// </summary>
/// <remarks>
/// Node listeners use a <see cref="ConditionalWeakTable{TKey,TValue}"/> so a detached node's listeners
/// are collected with the node (matching the prior ElementRuntimeState semantics) while staying scoped
/// to this document rather than a static table. The dispatch algorithms (building the JS event object,
/// walking the tree, invoking listeners) stay in the bridge and read/write listeners through here.
/// Instance-scoped to the owning bridge/document; <see cref="Clear"/> runs on re-parse and disposal.
/// </remarks>
internal sealed class EventTargetRegistry
{
    private readonly ConditionalWeakTable<DomNode, Dictionary<string, List<EventListenerRegistration>>> _nodeListeners = [];
    private readonly Dictionary<string, List<EventListenerRegistration>> _windowListeners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<JSObject, Dictionary<string, List<EventListenerRegistration>>> _targetListeners = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<JSObject, JSObject> _ownerWindows = new(ReferenceEqualityComparer.Instance);
    private readonly List<JSFunction> _visualViewportScrollListeners = [];

    // ------------------------------------------------------------------
    //  Node listeners (per DOM node, by event type)
    // ------------------------------------------------------------------

    /// <summary>The listener lists (by event type) for <paramref name="node"/>, created on first access.</summary>
    public Dictionary<string, List<EventListenerRegistration>> NodeListeners(DomNode node) =>
        _nodeListeners.GetValue(node, static _ => new Dictionary<string, List<EventListenerRegistration>>(StringComparer.OrdinalIgnoreCase));

    // ------------------------------------------------------------------
    //  Window listeners (by event type)
    // ------------------------------------------------------------------

    /// <summary>The window listener list for <paramref name="type"/>, created on first access (for add).</summary>
    public List<EventListenerRegistration> WindowListenersForAdd(string type)
    {
        if (!_windowListeners.TryGetValue(type, out var listeners))
        {
            listeners = [];
            _windowListeners[type] = listeners;
        }

        return listeners;
    }

    /// <summary>The window listener list for <paramref name="type"/>, if any (for remove/dispatch).</summary>
    public bool TryGetWindowListeners(string type, out List<EventListenerRegistration> listeners) =>
        _windowListeners.TryGetValue(type, out listeners!);

    // ------------------------------------------------------------------
    //  Generic JS-target listeners (message ports, sub-windows) by event type
    // ------------------------------------------------------------------

    /// <summary>The listener lists (by event type) for a generic JS <paramref name="target"/>, created on first access (for add).</summary>
    public Dictionary<string, List<EventListenerRegistration>> TargetListenersForAdd(JSObject target)
    {
        if (!_targetListeners.TryGetValue(target, out var byType))
        {
            byType = new Dictionary<string, List<EventListenerRegistration>>(StringComparer.OrdinalIgnoreCase);
            _targetListeners[target] = byType;
        }

        return byType;
    }

    /// <summary>The listener lists (by event type) for a generic JS <paramref name="target"/>, if any (for dispatch).</summary>
    public bool TryGetTargetListeners(JSObject target, out Dictionary<string, List<EventListenerRegistration>> byType) =>
        _targetListeners.TryGetValue(target, out byType!);

    // ------------------------------------------------------------------
    //  Generic-target owner windows (routes dispatch to the owning window)
    // ------------------------------------------------------------------

    public void SetOwnerWindow(JSObject target, JSObject window) => _ownerWindows[target] = window;

    public bool TryGetOwnerWindow(JSObject target, out JSObject window) => _ownerWindows.TryGetValue(target, out window!);

    // ------------------------------------------------------------------
    //  Visual-viewport scroll listeners
    // ------------------------------------------------------------------

    /// <summary>Registers a visual-viewport <c>scroll</c> listener (no-op if already registered).</summary>
    public void AddVisualViewportScrollListener(JSFunction listener)
    {
        if (!_visualViewportScrollListeners.Contains(listener))
            _visualViewportScrollListeners.Add(listener);
    }

    public void RemoveVisualViewportScrollListener(JSFunction listener) => _visualViewportScrollListeners.Remove(listener);

    /// <summary>The registered visual-viewport scroll listeners (snapshot the caller may iterate).</summary>
    public IReadOnlyList<JSFunction> VisualViewportScrollListeners => _visualViewportScrollListeners;

    // ------------------------------------------------------------------

    /// <summary>Drops every listener store — node, window, generic target, owner windows and viewport scroll.</summary>
    public void Clear()
    {
        _nodeListeners.Clear();
        _windowListeners.Clear();
        _targetListeners.Clear();
        _ownerWindows.Clear();
        _visualViewportScrollListeners.Clear();
    }
}
