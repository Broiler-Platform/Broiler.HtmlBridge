using System.Runtime.CompilerServices;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
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
/// <para>
/// Node listeners use a <see cref="ConditionalWeakTable{TKey,TValue}"/> so a detached node's listeners
/// are collected with the node (matching the prior ElementRuntimeState semantics) while staying scoped
/// to this document rather than a static table. The dispatch algorithms (building the JS event object,
/// walking the tree, invoking listeners) stay in the bridge and read/write listeners through here.
/// Instance-scoped to the owning bridge/document; <see cref="Clear"/> runs on re-parse and disposal.
/// </para>
/// <para>
/// <b>The two object-keyed maps are keyed on <see cref="JsValue"/>, and the move was checked rather
/// than hoped for.</b> Both were keyed on the engine's own object type under
/// <see cref="ReferenceEqualityComparer"/>, which asks two questions of a key:
/// <c>ReferenceEquals</c> and <c>RuntimeHelpers.GetHashCode</c>.
/// <c>JsValue.Equals</c>'s arm for every object kind is <c>ReferenceEquals</c> over the engine object
/// the handle carries and <c>JsValue.GetHashCode</c>'s is <c>RuntimeHelpers.GetHashCode</c> of that
/// same reference, so the default comparer asks exactly those two questions about exactly those
/// instances — a port or sub-window is found under the handle it was filed under, and identity is
/// untouched. The one thing the handle adds is that its <em>kind</em> takes part in equality; every
/// key here is an ordinary object (a message port and a sub-window are both <c>NewObject</c>, and a
/// window crossing the seam wraps as one), so two handles over one key always agree on kind.
/// </para>
/// <para>
/// <b>What is still engine-typed, and what pins each one.</b> The listener lists hold
/// <c>EventListenerRegistration</c>, whose listener field is a Broiler.JS value and whose declaration
/// is in <c>DomBridge/RuntimeStates.cs</c> — outside this round — so the element type of every list
/// below is engine-typed however the maps are keyed. <see cref="TryGetOwnerWindow"/> keeps the engine
/// signature because <c>Runtime/WindowContextManager.cs</c> is its only caller and is not this round's;
/// it is an adapter over a store that no longer is, and the two <see cref="JsInterop"/> calls in it are
/// casts over the object the handle already carries. The visual-viewport list holds engine functions
/// because <c>DomBridge.VisualViewportEventTargetHost.cs</c> fills it and
/// <c>DomBridge/LayoutMetrics.Scrolling.cs</c> reads it back as engine functions to invoke — neither
/// belongs to this round either, and the second wants the list itself rather than a converted copy.
/// </para>
/// </remarks>
internal sealed class EventTargetRegistry
{
    private readonly ConditionalWeakTable<DomNode, Dictionary<string, List<EventListenerRegistration>>> _nodeListeners = [];
    private readonly Dictionary<string, List<EventListenerRegistration>> _windowListeners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<JsValue, Dictionary<string, List<EventListenerRegistration>>> _targetListeners = [];
    private readonly Dictionary<JsValue, JsValue> _ownerWindows = [];
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
    public Dictionary<string, List<EventListenerRegistration>> TargetListenersForAdd(JsValue target)
    {
        if (!_targetListeners.TryGetValue(target, out var byType))
        {
            byType = new Dictionary<string, List<EventListenerRegistration>>(StringComparer.OrdinalIgnoreCase);
            _targetListeners[target] = byType;
        }

        return byType;
    }

    /// <summary>The listener lists (by event type) for a generic JS <paramref name="target"/>, if any (for dispatch).</summary>
    public bool TryGetTargetListeners(JsValue target, out Dictionary<string, List<EventListenerRegistration>> byType) =>
        _targetListeners.TryGetValue(target, out byType!);

    // ------------------------------------------------------------------
    //  Generic-target owner windows (routes dispatch to the owning window)
    // ------------------------------------------------------------------

    public void SetOwnerWindow(JsValue target, JsValue window) => _ownerWindows[target] = window;

    /// <summary>The window that owns <paramref name="target"/>, if one was recorded.</summary>
    /// <remarks>
    /// The engine signature is an adapter over a store that is no longer engine-typed, kept because
    /// <c>Runtime/WindowContextManager.cs</c> — the only caller, and not this round's file — holds the
    /// engine's own object on both sides of the call. Both conversions are casts over the object the
    /// handle already carries, so the window this answers is the instance
    /// <see cref="SetOwnerWindow"/> filed, and a miss still leaves <paramref name="window"/> null for
    /// the caller that only reads it after a <see langword="true"/>.
    /// </remarks>
    public bool TryGetOwnerWindow(JSObject target, out JSObject window)
    {
        if (!_ownerWindows.TryGetValue(JsInterop.FromEngineObject(target), out var ownerWindow))
        {
            window = null!;
            return false;
        }

        window = JsInterop.ToEngineObject(ownerWindow);
        return true;
    }

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
