using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The DOM event dispatch feature binding —
/// the capture → target → bubble propagation algorithm (DOM Events Level 3), the event object's
/// propagation-control methods (<c>stopPropagation</c>/<c>stopImmediatePropagation</c>/
/// <c>preventDefault</c>/<c>cancelBubble</c>/<c>returnValue</c>) and <c>composedPath()</c>. It reads
/// the listener store and the inline handler through the narrow <see cref="IEventDispatchHost"/>
/// contract; listener registration and inline-handler compilation stay in the bridge, and the
/// shared <c>InvokeEventListener</c> helper (also used by window/submit/messaging firing paths)
/// stays a bridge static.
/// </summary>
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>): the event object is a
/// <see cref="JsValue"/> handle, its members are installed through the realm with the property
/// attributes they always had, and its propagation-control methods are realm methods closing over
/// the same dispatch-local flags they closed over before.
/// </para>
/// <para>
/// A registered listener is an <c>EventListenerRegistration</c>, whose listener field is a
/// <see cref="JsValue"/>, and it is invoked through <c>DomBridge.InvokeEventListener</c>, which the
/// window, form-submit and messaging firing paths share and which is where a listener turn is
/// bracketed for the entry trace. That invoker calls through the realm, in the same shape
/// <see cref="FireListeners"/> uses for the inline <c>on*</c> handler.
/// </para>
/// </remarks>
internal sealed class EventDispatchBinding(IEventDispatchHost host)
{
    private readonly IEventDispatchHost _host = host;

    /// <summary>
    /// Dispatches a DOM event on the given element with full capture → target → bubble propagation
    /// (DOM Events Level 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The path ends at the target's own document, and then its window</b> (DOM §2.9, "get the
    /// parent": a document's parent is its window, for every event but <c>load</c>). It ended at the
    /// top document for every node, so an event inside a frame skipped the frame's document and
    /// reached the page's, and no window ever saw an element's event: a page delegating clicks from
    /// <c>window</c>, or a frame from its <c>document</c>, heard nothing.
    /// </para>
    /// <para>
    /// A node in no document -- detached, or in a shadow tree -- still gets the top document at the
    /// head of its path, as it always did, and no window.
    /// </para>
    /// </remarks>
    internal JsValue DispatchEventOnElement(DomNode target, JsValue evt)
    {
        var realm = _host.Realm;

        var typeVal = realm.GetProperty(evt, "type");
        // Only a string type names the event; anything else — including an object with a toString —
        // is "unknown", so no coercion runs here.
        var eventType = typeVal.IsString ? typeVal.AsString! : "unknown";

        // The document at the head of the path, the JS object that stands for it, and the window after
        // it. The document/window globals are read once per dispatch: a wrapper that is not an object is
        // one that has not been installed yet, and the path substitutes JS null for it.
        DomNode documentNode;
        JsValue documentWrapper;
        var window = JsValue.Missing;
        if (RootOf(target) is DomDocument connected &&
            _host.TryGetDocumentTargets(connected, out var connectedWrapper, out var connectedWindow))
        {
            documentNode = connected;
            documentWrapper = connectedWrapper;
            if (!string.Equals(eventType, "load", StringComparison.Ordinal))
                window = connectedWindow;
        }
        else
        {
            documentNode = _host.DocumentNode;
            documentWrapper = _host.DocumentWrapper;
        }

        var documentValue = documentWrapper.IsObject ? documentWrapper : JsValue.Null;

        // Build the path from the root to the target
        var path = new List<DomNode>();
        var visited = new HashSet<DomElement>();
        var node = DomBridgeUtils.ParentEl(target);
        while (node != null && visited.Add(node)) { path.Add(node); node = DomBridgeUtils.ParentEl(node); }
        path.Reverse();

        // Include the document node at the very beginning of the path
        // (first for capture, last for bubble) unless the target IS the document node.
        if (target != documentNode && !path.Contains(documentNode))
            path.Insert(0, documentNode);

        var stopped = false;
        var immediateStopped = false;
        var prevented = realm.GetProperty(evt, "defaultPrevented").AsBoolean;
        var currentListenerPassive = false;
        var legacyCancelBubble = false;

        JsValue WrapPathNode(DomNode pathNode) =>
            pathNode == documentNode ? documentValue : _host.WrapNode(pathNode);

        // Set up event object properties
        realm.SetProperty(evt, "target", WrapPathNode(target));
        realm.SetProperty(evt, "srcElement", realm.GetProperty(evt, "target"));
        realm.SetProperty(evt, "eventPhase", JsValue.Number(0));

        realm.DefineMethod(evt, "stopPropagation", (in _) => EventStopPropagation(ref legacyCancelBubble, ref stopped));

        realm.DefineMethod(evt, "stopImmediatePropagation",
            (in _) => EventStopImmediatePropagation(ref immediateStopped, ref legacyCancelBubble, ref stopped));

        realm.DefineMethod(evt, "preventDefault",
            (in _) => EventPreventDefault(realm, currentListenerPassive, evt, ref prevented));

        realm.DefineAccessor(evt, "cancelBubble",
            (in _) => JsValue.Boolean(legacyCancelBubble),
            (in setCall) => EventSetCancelBubble(ref legacyCancelBubble, ref stopped, in setCall));

        realm.DefineAccessor(evt, "returnValue",
            (in _) => JsValue.Boolean(!prevented),
            (in setCall) => EventSetReturnValue(realm, currentListenerPassive, evt, ref prevented, in setCall));

        realm.DefineMethod(evt, "composedPath", (in _) => BuildComposedPathValue(target, path, documentNode, documentValue, window));

        // Phase 1: Capture (window → root → parent of target)
        realm.SetProperty(evt, "eventPhase", JsValue.Number(1));
        if (window.IsObject)
        {
            realm.SetProperty(evt, "currentTarget", window);
            FireWindowListeners(window, eventType, evt, capturePhase: true, ref immediateStopped, ref currentListenerPassive, ref prevented);
        }

        foreach (var ancestor in path)
        {
            if (stopped) break;
            realm.SetProperty(evt, "currentTarget", WrapPathNode(ancestor));
            FireListeners(ancestor, eventType, evt, capturePhase: true, ref immediateStopped, ref currentListenerPassive, ref prevented);
        }

        // Phase 2: Target — fire capture listeners first, then non-capture listeners.
        if (!stopped)
        {
            realm.SetProperty(evt, "eventPhase", JsValue.Number(2));
            realm.SetProperty(evt, "currentTarget", WrapPathNode(target));
            FireListeners(target, eventType, evt, capturePhase: true, ref immediateStopped, ref currentListenerPassive, ref prevented);
            FireListeners(target, eventType, evt, capturePhase: false, ref immediateStopped, ref currentListenerPassive, ref prevented);
        }

        // Phase 3: Bubble (parent of target → root → window) — only if event.bubbles is true
        var eventBubbles = realm.GetProperty(evt, "bubbles").AsBoolean;
        if (!stopped && eventBubbles)
        {
            realm.SetProperty(evt, "eventPhase", JsValue.Number(3));
            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (stopped) break;
                realm.SetProperty(evt, "currentTarget", WrapPathNode(path[i]));
                FireListeners(path[i], eventType, evt, capturePhase: false, ref immediateStopped, ref currentListenerPassive, ref prevented);
            }

            if (!stopped && window.IsObject)
            {
                realm.SetProperty(evt, "currentTarget", window);
                FireWindowListeners(window, eventType, evt, capturePhase: false, ref immediateStopped, ref currentListenerPassive, ref prevented);
            }
        }

        realm.SetProperty(evt, "currentTarget", JsValue.Null);
        realm.SetProperty(evt, "eventPhase", JsValue.Number(0));

        return JsValue.Boolean(!prevented);
    }

    /// <summary>
    /// Dispatches <paramref name="evt"/> at a window itself -- its <c>focus</c> and <c>blur</c> --
    /// whose path is the window alone: its capture listeners, then the rest and its <c>on…</c>
    /// handler. Answers whether it was not cancelled.
    /// </summary>
    internal JsValue DispatchEventOnWindow(JsValue window, JsValue evt)
    {
        var realm = _host.Realm;
        var typeVal = realm.GetProperty(evt, "type");
        var eventType = typeVal.IsString ? typeVal.AsString! : "unknown";

        var stopped = false;
        var immediateStopped = false;
        var prevented = realm.GetProperty(evt, "defaultPrevented").AsBoolean;
        var currentListenerPassive = false;
        var legacyCancelBubble = false;

        realm.SetProperty(evt, "target", window);
        realm.SetProperty(evt, "srcElement", window);
        realm.DefineMethod(evt, "stopPropagation", (in _) => EventStopPropagation(ref legacyCancelBubble, ref stopped));
        realm.DefineMethod(evt, "stopImmediatePropagation",
            (in _) => EventStopImmediatePropagation(ref immediateStopped, ref legacyCancelBubble, ref stopped));
        realm.DefineMethod(evt, "preventDefault",
            (in _) => EventPreventDefault(realm, currentListenerPassive, evt, ref prevented));
        realm.DefineMethod(evt, "composedPath", (in _) => realm.NewArray([window]));

        realm.SetProperty(evt, "eventPhase", JsValue.Number(2));
        realm.SetProperty(evt, "currentTarget", window);
        FireWindowListeners(window, eventType, evt, capturePhase: true, ref immediateStopped, ref currentListenerPassive, ref prevented);
        if (!immediateStopped)
            FireWindowListeners(window, eventType, evt, capturePhase: false, ref immediateStopped, ref currentListenerPassive, ref prevented);

        realm.SetProperty(evt, "currentTarget", JsValue.Null);
        realm.SetProperty(evt, "eventPhase", JsValue.Number(0));
        return JsValue.Boolean(!prevented);
    }

    /// <summary>The node at the top of <paramref name="node"/>'s tree: its document when it is in one.</summary>
    private static DomNode RootOf(DomNode node)
    {
        var current = node;
        while (current.ParentNode is { } parent)
            current = parent;
        return current;
    }

    /// <summary>
    /// Fires registered listeners for the given event type on a single element.
    /// When <paramref name="capturePhase"/> is <c>true</c>, only capture listeners fire.
    /// When <c>false</c>, only bubble listeners fire.
    /// </summary>
    private void FireListeners(DomNode el, string eventType, JsValue evt,
        bool capturePhase, ref bool immediateStopped, ref bool currentListenerPassive, ref bool prevented)
    {
        if (_host.GetEventListeners(el).TryGetValue(eventType, out var listeners))
        {
            EventListenerBinding.InvokeListeners(listeners,
                listener => DomBridgeUtils.InvokeEventListener(_host.Realm, listener, evt, "DomBridge.dispatchEvent"),
                ref immediateStopped, ref currentListenerPassive, capturePhase);
        }

        // Fire inline event handler (on* property) — fires after addEventListener listeners on the target,
        // and during bubble phase on ancestors (like a bubble listener).
        if (!immediateStopped && !capturePhase)
            FireInlineHandler(_host.InlineEventHandler(el, eventType), evt, ref currentListenerPassive, ref prevented);
    }

    /// <summary>
    /// Fires <paramref name="window"/>'s listeners for the given event type -- its capture listeners
    /// before the path's, and the rest, and its <c>on…</c> handler, after it.
    /// </summary>
    private void FireWindowListeners(JsValue window, string eventType, JsValue evt,
        bool capturePhase, ref bool immediateStopped, ref bool currentListenerPassive, ref bool prevented)
    {
        if (_host.WindowListeners(window, eventType) is { } listeners)
        {
            EventListenerBinding.InvokeListeners(listeners,
                listener => DomBridgeUtils.InvokeEventListener(_host.Realm, listener, evt, "DomBridge.window.dispatchEvent"),
                ref immediateStopped, ref currentListenerPassive, capturePhase);
        }

        if (!immediateStopped && !capturePhase)
        {
            var handler = _host.Realm.GetProperty(window, "on" + eventType);
            FireInlineHandler(handler.IsFunction ? handler : JsValue.Missing, evt, ref currentListenerPassive, ref prevented);
        }
    }

    /// <summary>
    /// Calls an <c>on…</c> handler, with the object it is on as <c>this</c>, and cancels the event when
    /// it returns <see langword="false"/> (HTML §8.1.8.1, "the event handler processing algorithm").
    /// </summary>
    /// <remarks>
    /// It was called with itself as <c>this</c>, and what it returned was dropped: a link whose
    /// <c>onclick</c> ends in <c>return false</c> -- the commonest way an old page says "do not follow
    /// me" -- was followed anyway.
    /// </remarks>
    private void FireInlineHandler(JsValue inlineHandler, JsValue evt, ref bool currentListenerPassive, ref bool prevented)
    {
        if (!inlineHandler.IsObject)
            return;

        // Inline on* handlers behave like regular non-passive listeners.
        currentListenerPassive = false;
        var realm = _host.Realm;
        try
        {
            var receiver = realm.GetProperty(evt, "currentTarget");
            var returned = realm.Invoke(inlineHandler, receiver.IsObject ? receiver : inlineHandler, [evt]);
            if (returned.IsBoolean && !returned.AsBoolean)
                EventPreventDefault(realm, currentListenerPassive, evt, ref prevented);
        }
        catch (Exception ex) { RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.dispatchEvent", $"Inline handler error: {ex.Message}", ex); }
    }

    private JsValue BuildComposedPathValue(
        DomNode target, IReadOnlyList<DomNode> path, DomNode documentNode, JsValue documentValue, JsValue window)
    {
        var realm = _host.Realm;

        JsValue ToEventPathObject(DomNode node)
            => node == documentNode ? documentValue : _host.WrapNode(node);

        var values = new List<JsValue> { ToEventPathObject(target) };

        for (int i = path.Count - 1; i >= 0; i--)
            values.Add(ToEventPathObject(path[i]));

        // The window the path ends at; a node in no document still ends with the top window, as it did.
        var last = window.IsObject ? window : _host.WindowWrapper;
        if (last.IsObject)
            values.Add(last);

        return realm.NewArray([.. values]);
    }

    // -------- Event object propagation-control methods --------

    private static JsValue EventStopPropagation(ref bool legacyCancelBubble, ref bool stopped)
    {
        stopped = true;
        legacyCancelBubble = true;
        return JsValue.Undefined;
    }

    private static JsValue EventStopImmediatePropagation(ref bool immediateStopped, ref bool legacyCancelBubble, ref bool stopped)
    {
        stopped = true;
        immediateStopped = true;
        legacyCancelBubble = true;
        return JsValue.Undefined;
    }

    private static JsValue EventPreventDefault(IJsRealm realm, bool currentListenerPassive, JsValue evt, ref bool prevented)
    {
        if (!currentListenerPassive && realm.GetProperty(evt, "cancelable").AsBoolean)
        {
            prevented = true;
            realm.SetProperty(evt, "defaultPrevented", JsValue.True);
        }

        return JsValue.Undefined;
    }

    private static JsValue EventSetCancelBubble(ref bool legacyCancelBubble, ref bool stopped, in JsCall setCall)
    {
        if (setCall.Length > 0 && setCall[0].AsBoolean)
        {
            legacyCancelBubble = true;
            stopped = true;
        }

        return JsValue.Undefined;
    }

    private static JsValue EventSetReturnValue(IJsRealm realm, bool currentListenerPassive, JsValue evt, ref bool prevented, in JsCall setCall)
    {
        if (setCall.Length > 0 && !setCall[0].AsBoolean && !currentListenerPassive &&
            realm.GetProperty(evt, "cancelable").AsBoolean)
        {
            prevented = true;
            realm.SetProperty(evt, "defaultPrevented", JsValue.True);
        }

        return JsValue.Undefined;
    }
}
