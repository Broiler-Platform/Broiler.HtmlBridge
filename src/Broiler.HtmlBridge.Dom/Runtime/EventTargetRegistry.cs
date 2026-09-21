using System.Runtime.CompilerServices;
using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The single owner of a document's event-listener stores: the per-node
/// <c>addEventListener</c> listeners, the window listeners, the generic
/// JS-target (message port / sub-window) listeners, the target→owner-window map used to route generic
/// dispatch, and the visual-viewport <c>scroll</c> listeners.
/// </summary>
/// <remarks>
/// <para>
/// Node listeners use a <see cref="ConditionalWeakTable{TKey,TValue}"/> so a detached node's listeners
/// are collected with the node, while staying scoped
/// to this document rather than a static table. The dispatch algorithms (building the JS event object,
/// walking the tree, invoking listeners) stay in the bridge and read/write listeners through here.
/// Instance-scoped to the owning bridge/document; <see cref="Clear"/> runs on re-parse and disposal.
/// </para>
/// <para>
/// <b>The two object-keyed maps are keyed on <see cref="JsValue"/> under the default comparer.</b>
/// <c>JsValue.Equals</c>'s arm for every object kind is <c>ReferenceEquals</c> over the engine object
/// the handle carries and <c>JsValue.GetHashCode</c>'s is <c>RuntimeHelpers.GetHashCode</c> of that
/// same reference, so a port or sub-window is found under the handle it was filed under and identity
/// is untouched. The one thing the handle adds is that its <em>kind</em> takes part in equality; every
/// key here is an ordinary object (a message port and a sub-window are both <c>NewObject</c>, and a
/// window crossing the seam wraps as one), so two handles over one key always agree on kind.
/// </para>
/// <para>
/// <b>Nothing in this store is engine-typed.</b> The listener lists hold
/// <c>EventListenerRegistration</c>, whose listener field is a <see cref="JsValue"/>, and the
/// visual-viewport list holds <see cref="JsValue"/> too, so no list here names an engine type however
/// its map is keyed.
/// </para>
/// </remarks>
internal sealed class EventTargetRegistry
{
    private readonly ConditionalWeakTable<DomNode, Dictionary<string, List<EventListenerRegistration>>> _nodeListeners = [];
    private readonly Dictionary<string, List<EventListenerRegistration>> _windowListeners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<JsValue, Dictionary<string, List<EventListenerRegistration>>> _targetListeners = [];
    private readonly Dictionary<JsValue, JsValue> _ownerWindows = [];
    private readonly List<JsValue> _visualViewportScrollListeners = [];

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
    public List<EventListenerRegistration> WindowListenersForAdd(string type) =>
        RegistryMaps.GetOrAdd(_windowListeners, type, static () => new List<EventListenerRegistration>());

    /// <summary>The window listener list for <paramref name="type"/>, if any (for remove/dispatch).</summary>
    public bool TryGetWindowListeners(string type, out List<EventListenerRegistration> listeners) =>
        _windowListeners.TryGetValue(type, out listeners!);

    // ------------------------------------------------------------------
    //  Generic JS-target listeners (message ports, sub-windows) by event type
    // ------------------------------------------------------------------

    /// <summary>The listener lists (by event type) for a generic JS <paramref name="target"/>, created on first access (for add).</summary>
    public Dictionary<string, List<EventListenerRegistration>> TargetListenersForAdd(JsValue target) =>
        RegistryMaps.GetOrAdd(
            _targetListeners,
            target,
            static () => new Dictionary<string, List<EventListenerRegistration>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The listener lists (by event type) for a generic JS <paramref name="target"/>, if any (for dispatch).</summary>
    public bool TryGetTargetListeners(JsValue target, out Dictionary<string, List<EventListenerRegistration>> byType) =>
        _targetListeners.TryGetValue(target, out byType!);

    // ------------------------------------------------------------------
    //  Generic-target owner windows (routes dispatch to the owning window)
    // ------------------------------------------------------------------

    public void SetOwnerWindow(JsValue target, JsValue window) => _ownerWindows[target] = window;

    /// <summary>The window that owns <paramref name="target"/>, if one was recorded.</summary>
    /// <remarks>
    /// A miss leaves <paramref name="window"/> at <see cref="JsValue.Missing"/>, which is
    /// <c>default</c>: the dictionary's own miss writes it, so the out parameter needs no
    /// null-forgiving operator, and every window <c>WindowContextManager</c> holds is tested with
    /// <see cref="JsValue.IsObject"/>, which <c>Missing</c> fails.
    /// </remarks>
    public bool TryGetOwnerWindow(JsValue target, out JsValue window) => _ownerWindows.TryGetValue(target, out window);

    // ------------------------------------------------------------------
    //  Visual-viewport scroll listeners
    // ------------------------------------------------------------------

    /// <summary>Registers a visual-viewport <c>scroll</c> listener (no-op if already registered).</summary>
    public void AddVisualViewportScrollListener(JsValue listener)
    {
        if (!_visualViewportScrollListeners.Contains(listener))
            _visualViewportScrollListeners.Add(listener);
    }

    public void RemoveVisualViewportScrollListener(JsValue listener) => _visualViewportScrollListeners.Remove(listener);

    /// <summary>The registered visual-viewport scroll listeners: the list itself, not a snapshot.</summary>
    /// <remarks>
    /// Taking a snapshot is the reader's job, and the only reader does it:
    /// <c>DispatchVisualViewportScrollEvent</c> in <c>DomBridge/LayoutMetrics.Scrolling.cs</c> copies
    /// the list before invoking anything, which is what lets a listener remove itself mid-dispatch
    /// without changing the list the loop is walking.
    /// </remarks>
    public IReadOnlyList<JsValue> VisualViewportScrollListeners => _visualViewportScrollListeners;

    // ------------------------------------------------------------------

    /// <summary>Drops every listener store — node, window, generic target, owner windows and viewport scroll.</summary>
    public void Clear()
    {
        // In-flight dispatch snapshots must observe a session reset too.
        foreach (var entry in _nodeListeners)
            RemoveListeners(entry.Value);
        RemoveListeners(_windowListeners);
        foreach (var byType in _targetListeners.Values)
            RemoveListeners(byType);

        _nodeListeners.Clear();
        _windowListeners.Clear();
        _targetListeners.Clear();
        _ownerWindows.Clear();
        _visualViewportScrollListeners.Clear();
    }

    private static void RemoveListeners(Dictionary<string, List<EventListenerRegistration>> byType)
    {
        foreach (var listeners in byType.Values)
        {
            foreach (var registration in listeners)
                registration.Removed = true;
            listeners.Clear();
        }
    }
}
