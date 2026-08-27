using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Logging;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The DOM event dispatch feature binding (HtmlBridge complexity-reduction roadmap Phase 3, P3.3) —
/// the capture → target → bubble propagation algorithm (DOM Events Level 3), the event object's
/// propagation-control methods (<c>stopPropagation</c>/<c>stopImmediatePropagation</c>/
/// <c>preventDefault</c>/<c>cancelBubble</c>/<c>returnValue</c>) and <c>composedPath()</c>. It reads
/// the listener store and inline-handler map through the narrow <see cref="IEventDispatchHost"/>
/// contract; listener registration and inline-handler compilation stay in the bridge, and the
/// shared <c>InvokeEventListener</c> helper (also used by window/submit/messaging firing paths)
/// stays a bridge static.
/// </summary>
internal sealed class EventDispatchBinding(IEventDispatchHost host)
{
    private readonly IEventDispatchHost _host = host;

    /// <summary>
    /// Dispatches a DOM event on the given element with full capture → target → bubble propagation
    /// (DOM Events Level 3).
    /// </summary>
    internal JSValue DispatchEventOnElement(DomNode target, JSObject evt)
    {
        var documentNode = _host.DocumentNode;
        var documentJSObject = _host.DocumentJSObject;

        var typeVal = evt[(KeyString)"type"];
        var eventType = typeVal != null && typeVal is JSString ? typeVal.ToString() : "unknown";

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
        var prevented = evt[(KeyString)"defaultPrevented"] is JSValue defaultPreventedValue &&
                        defaultPreventedValue.BooleanValue;
        var currentListenerPassive = false;
        var legacyCancelBubble = false;

        // Set up event object properties
        evt[(KeyString)"target"] = target == documentNode
            ? (documentJSObject ?? JSNull.Value)
            : _host.ToJSObject(target);
        evt[(KeyString)"srcElement"] = evt[(KeyString)"target"];
        evt[(KeyString)"eventPhase"] = new JSNumber(0);

        evt.FastAddValue("stopPropagation",
            new DomFunction((in _) => EventStopPropagation(ref legacyCancelBubble, ref stopped, in _), "stopPropagation", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        evt.FastAddValue("stopImmediatePropagation",
            new DomFunction((in _) => EventStopImmediatePropagation(ref immediateStopped, ref legacyCancelBubble, ref stopped, in _), "stopImmediatePropagation", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        evt.FastAddValue("preventDefault",
            new DomFunction((in _) => EventPreventDefault(currentListenerPassive, evt, ref prevented, in _), "preventDefault", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        evt.FastAddProperty(
            "cancelBubble",
            new DomFunction((in _) => legacyCancelBubble ? JSBoolean.True : JSBoolean.False, "get cancelBubble"),
            new DomFunction((in setArgs) => EventSetCancelBubble(ref legacyCancelBubble, ref stopped, in setArgs), "set cancelBubble"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        evt.FastAddProperty("returnValue",
            new DomFunction((in _) => prevented ? JSBoolean.False : JSBoolean.True, "get returnValue"),
            new DomFunction((in setArgs) => EventSetReturnValue(currentListenerPassive, evt, ref prevented, in setArgs), "set returnValue"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        evt.FastAddValue("composedPath",
            new DomFunction((in _) => BuildComposedPathValue(target, path), "composedPath", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // Phase 1: Capture (root → parent of target)
        evt[(KeyString)"eventPhase"] = new JSNumber(1);
        foreach (var ancestor in path)
        {
            if (stopped) break;
            evt[(KeyString)"currentTarget"] = ancestor == documentNode
                ? (documentJSObject ?? JSNull.Value)
                : _host.ToJSObject(ancestor);
            FireListeners(ancestor, eventType, evt, capturePhase: true, ref stopped, ref immediateStopped, ref currentListenerPassive);
        }

        // Phase 2: Target — fire capture listeners first, then non-capture listeners.
        if (!stopped)
        {
            evt[(KeyString)"eventPhase"] = new JSNumber(2);
            evt[(KeyString)"currentTarget"] = target == documentNode
                ? (documentJSObject ?? JSNull.Value)
                : _host.ToJSObject(target);
            FireListeners(target, eventType, evt, capturePhase: true, ref stopped, ref immediateStopped, ref currentListenerPassive);
            FireListeners(target, eventType, evt, capturePhase: false, ref stopped, ref immediateStopped, ref currentListenerPassive);
        }

        // Phase 3: Bubble (parent of target → root) — only if event.bubbles is true
        var bubblesVal = evt[(KeyString)"bubbles"];
        var eventBubbles = bubblesVal != null && bubblesVal.BooleanValue;
        if (!stopped && eventBubbles)
        {
            evt[(KeyString)"eventPhase"] = new JSNumber(3);
            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (stopped) break;
                evt[(KeyString)"currentTarget"] = path[i] == documentNode
                    ? (documentJSObject ?? JSNull.Value)
                    : _host.ToJSObject(path[i]);
                FireListeners(path[i], eventType, evt, capturePhase: false, ref stopped, ref immediateStopped, ref currentListenerPassive);
            }
        }

        evt[(KeyString)"currentTarget"] = JSNull.Value;
        evt[(KeyString)"eventPhase"] = new JSNumber(0);

        return prevented ? JSBoolean.False : JSBoolean.True;
    }

    /// <summary>
    /// Fires registered listeners for the given event type on a single element.
    /// When <paramref name="capturePhase"/> is <c>true</c>, only capture listeners fire.
    /// When <c>false</c>, only bubble listeners fire.
    /// When <c>null</c> (unused), all listeners fire in registration order plus the inline handler.
    /// </summary>
    private void FireListeners(DomNode el, string eventType, JSObject evt,
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
                DomBridge.InvokeEventListener(registration.Listener, evt, "DomBridge.dispatchEvent");
                currentListenerPassive = false;

                if (registration.Once)
                    listeners.Remove(registration);
            }
        }

        // Fire inline event handler (on* property) — fires after addEventListener listeners on the target,
        // and during bubble phase on ancestors (like a bubble listener).
        if (!immediateStopped && (capturePhase == null || capturePhase == false))
        {
            if (_host.GetInlineEventHandlers(el).TryGetValue(eventType, out var inlineHandler) && inlineHandler is JSFunction inlineFn)
            {
                // Inline on* handlers behave like regular non-passive listeners.
                currentListenerPassive = false;
                try { inlineFn.InvokeFunction(new Arguments(inlineFn, evt)); }
                catch (Exception ex) { RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.dispatchEvent", $"Inline handler error: {ex.Message}", ex); }
            }
        }
    }

    private JSValue BuildComposedPathValue(DomNode target, IReadOnlyList<DomNode> path)
    {
        var documentNode = _host.DocumentNode;
        var documentJSObject = _host.DocumentJSObject;

        JSValue ToEventPathObject(DomNode node)
            => node == documentNode ? (documentJSObject ?? JSNull.Value) : _host.ToJSObject(node);

        var values = new List<JSValue> { ToEventPathObject(target) };

        for (int i = path.Count - 1; i >= 0; i--)
            values.Add(ToEventPathObject(path[i]));

        if (_host.WindowJSObject != null)
            values.Add(_host.WindowJSObject);

        return new JSArray([.. values]);
    }

    // -------- Event object propagation-control methods --------

    private static JSValue EventStopPropagation(ref bool legacyCancelBubble, ref bool stopped, in Arguments _)
    {
        stopped = true;
        legacyCancelBubble = true;
        return JSUndefined.Value;
    }

    private static JSValue EventStopImmediatePropagation(ref bool immediateStopped, ref bool legacyCancelBubble, ref bool stopped, in Arguments _)
    {
        stopped = true;
        immediateStopped = true;
        legacyCancelBubble = true;
        return JSUndefined.Value;
    }

    private static JSValue EventPreventDefault(bool currentListenerPassive, JSObject evt, ref bool prevented, in Arguments _)
    {
        var cancelable = evt[(KeyString)"cancelable"];
        if (!currentListenerPassive && cancelable != null && cancelable.BooleanValue)
        {
            prevented = true;
            evt[(KeyString)"defaultPrevented"] = JSBoolean.True;
        }

        return JSUndefined.Value;
    }

    private static JSValue EventSetCancelBubble(ref bool legacyCancelBubble, ref bool stopped, in Arguments setArgs)
    {
        if (setArgs.Length > 0 && setArgs[0].BooleanValue)
        {
            legacyCancelBubble = true;
            stopped = true;
        }

        return JSUndefined.Value;
    }

    private static JSValue EventSetReturnValue(bool currentListenerPassive, JSObject evt, ref bool prevented, in Arguments setArgs)
    {
        var cancelable = evt[(KeyString)"cancelable"];
        if (setArgs.Length > 0 && !setArgs[0].BooleanValue && !currentListenerPassive && cancelable != null && cancelable.BooleanValue)
        {
            prevented = true;
            evt[(KeyString)"defaultPrevented"] = JSBoolean.True;
        }

        return JSUndefined.Value;
    }
}
