using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Logging;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The DOM event dispatch feature binding (HtmlBridge complexity-reduction roadmap Phase 3, P3.3) —
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
/// <b>One engine-typed strand survives, and it is not this module's to cut.</b> A registered
/// listener is an <c>EventListenerRegistration</c>, whose listener field is a Broiler.JS value
/// because the record lives in <c>DomBridge/RuntimeStates.cs</c> and is shared with the window,
/// form-submit and messaging dispatch paths; it is invoked through <c>DomBridge.InvokeEventListener</c>,
/// which those same paths share and which is where a listener turn is bracketed for the entry trace.
/// So the engine's event object is taken once per dispatch — a cast over the object this handle
/// already carries, not a conversion — and handed to that invoker. When the listener store moves,
/// both lines go with it.
/// </para>
/// </remarks>
internal sealed class EventDispatchBinding(IEventDispatchHost host)
{
    private readonly IEventDispatchHost _host = host;

    /// <summary>
    /// Dispatches a DOM event on the given element with full capture → target → bubble propagation
    /// (DOM Events Level 3).
    /// </summary>
    internal JsValue DispatchEventOnElement(DomNode target, JsValue evt)
    {
        var realm = _host.Realm;
        var documentNode = _host.DocumentNode;

        // The document/window globals are read once per dispatch, as they were: a wrapper that is not
        // an object is one that has not been installed yet, and the path substitutes JS null for it
        // exactly where the `?? JSNull.Value` coalesces did.
        var documentWrapper = _host.DocumentWrapper;
        var documentValue = documentWrapper.IsObject ? documentWrapper : JsValue.Null;

        var typeVal = realm.GetProperty(evt, "type");
        // Only a string type names the event; anything else — including an object with a toString —
        // was "unknown" before and stays "unknown", so no coercion runs here.
        var eventType = typeVal.IsString ? typeVal.AsString! : "unknown";

        // Build the path from the root to the target
        var path = new List<DomNode>();
        var visited = new HashSet<DomElement>();
        var node = DomBridge.ParentEl(target);
        while (node != null && visited.Add(node)) { path.Add(node); node = DomBridge.ParentEl(node); }
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

        realm.DefineValue(evt, "stopPropagation",
            realm.NewMethod("stopPropagation", (in _) => EventStopPropagation(ref legacyCancelBubble, ref stopped)));

        realm.DefineValue(evt, "stopImmediatePropagation",
            realm.NewMethod("stopImmediatePropagation", (in _) => EventStopImmediatePropagation(ref immediateStopped, ref legacyCancelBubble, ref stopped)));

        realm.DefineValue(evt, "preventDefault",
            realm.NewMethod("preventDefault", (in _) => EventPreventDefault(realm, currentListenerPassive, evt, ref prevented)));

        realm.DefineAccessor(evt, "cancelBubble",
            (in _) => JsValue.Boolean(legacyCancelBubble),
            (in setCall) => EventSetCancelBubble(ref legacyCancelBubble, ref stopped, in setCall));

        realm.DefineAccessor(evt, "returnValue",
            (in _) => JsValue.Boolean(!prevented),
            (in setCall) => EventSetReturnValue(realm, currentListenerPassive, evt, ref prevented, in setCall));

        realm.DefineValue(evt, "composedPath",
            realm.NewMethod("composedPath", (in _) => BuildComposedPathValue(target, path)));

        // Phase 1: Capture (root → parent of target)
        realm.SetProperty(evt, "eventPhase", JsValue.Number(1));
        foreach (var ancestor in path)
        {
            if (stopped) break;
            realm.SetProperty(evt, "currentTarget", WrapPathNode(ancestor));
            FireListeners(ancestor, eventType, evt, capturePhase: true, ref stopped, ref immediateStopped, ref currentListenerPassive);
        }

        // Phase 2: Target — fire capture listeners first, then non-capture listeners.
        if (!stopped)
        {
            realm.SetProperty(evt, "eventPhase", JsValue.Number(2));
            realm.SetProperty(evt, "currentTarget", WrapPathNode(target));
            FireListeners(target, eventType, evt, capturePhase: true, ref stopped, ref immediateStopped, ref currentListenerPassive);
            FireListeners(target, eventType, evt, capturePhase: false, ref stopped, ref immediateStopped, ref currentListenerPassive);
        }

        // Phase 3: Bubble (parent of target → root) — only if event.bubbles is true
        var eventBubbles = realm.GetProperty(evt, "bubbles").AsBoolean;
        if (!stopped && eventBubbles)
        {
            realm.SetProperty(evt, "eventPhase", JsValue.Number(3));
            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (stopped) break;
                realm.SetProperty(evt, "currentTarget", WrapPathNode(path[i]));
                FireListeners(path[i], eventType, evt, capturePhase: false, ref stopped, ref immediateStopped, ref currentListenerPassive);
            }
        }

        realm.SetProperty(evt, "currentTarget", JsValue.Null);
        realm.SetProperty(evt, "eventPhase", JsValue.Number(0));

        return JsValue.Boolean(!prevented);
    }

    /// <summary>
    /// Fires registered listeners for the given event type on a single element.
    /// When <paramref name="capturePhase"/> is <c>true</c>, only capture listeners fire.
    /// When <c>false</c>, only bubble listeners fire.
    /// When <c>null</c> (unused), all listeners fire in registration order plus the inline handler.
    /// </summary>
    private void FireListeners(DomNode el, string eventType, JsValue evt,
        bool? capturePhase, ref bool stopped, ref bool immediateStopped, ref bool currentListenerPassive)
    {
        if (_host.GetEventListeners(el).TryGetValue(eventType, out var listeners))
        {
            foreach (var registration in listeners.ToList())
            {
                if (immediateStopped) break;
                // In capture/bubble phases, only fire matching listeners.
                // In target phase (capturePhase == null), fire all listeners.
                if (capturePhase.HasValue && registration.Capture != capturePhase.Value) continue;
                currentListenerPassive = registration.Passive;
                // The listener and the invoker are both still engine-typed (see the remarks on this
                // type); JsInterop.ToEngineObject is a cast over the object this handle already
                // carries, so the listener sees the same event object the page dispatched.
                DomBridge.InvokeEventListener(registration.Listener, JsInterop.ToEngineObject(evt), "DomBridge.dispatchEvent");
                currentListenerPassive = false;

                if (registration.Once)
                    listeners.Remove(registration);
            }
        }

        // Fire inline event handler (on* property) — fires after addEventListener listeners on the target,
        // and during bubble phase on ancestors (like a bubble listener).
        if (!immediateStopped && (capturePhase == null || capturePhase == false))
        {
            var inlineHandler = _host.InlineEventHandler(el, eventType);
            if (inlineHandler.IsObject)
            {
                // Inline on* handlers behave like regular non-passive listeners.
                currentListenerPassive = false;
                // The handler is its own receiver, as it was when the engine was invoked directly.
                try { _host.Realm.Invoke(inlineHandler, inlineHandler, [evt]); }
                catch (Exception ex) { RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.dispatchEvent", $"Inline handler error: {ex.Message}", ex); }
            }
        }
    }

    private JsValue BuildComposedPathValue(DomNode target, IReadOnlyList<DomNode> path)
    {
        var realm = _host.Realm;
        var documentNode = _host.DocumentNode;
        var documentWrapper = _host.DocumentWrapper;

        JsValue ToEventPathObject(DomNode node)
            => node == documentNode
                ? (documentWrapper.IsObject ? documentWrapper : JsValue.Null)
                : _host.WrapNode(node);

        var values = new List<JsValue> { ToEventPathObject(target) };

        for (int i = path.Count - 1; i >= 0; i--)
            values.Add(ToEventPathObject(path[i]));

        var windowWrapper = _host.WindowWrapper;
        if (windowWrapper.IsObject)
            values.Add(windowWrapper);

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
