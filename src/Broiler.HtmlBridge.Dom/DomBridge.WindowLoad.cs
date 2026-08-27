using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Engine;
using Broiler.HtmlBridge.Logging;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// Sibling partial peeled out of <c>DomBridge.cs</c> (Phase 3 ratchet, 2026-07-17) to keep the
/// facade under the 750-line guard: the window <c>load</c> lifecycle and window-target event
/// dispatch. Fires <c>window.onload</c> / bare <c>onload</c>, the window <c>load</c> listeners,
/// and the <c>&lt;body&gt;</c> load event, and builds the <c>window.frames</c> array from the
/// document's same-origin iframes. Pure partial-class relocation — no signature, accessibility,
/// or logic change.
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Backs <c>document.readyState</c> (HTML §3.1.7). It starts at <c>loading</c> and is advanced
    /// by the load sequence below; a script that runs before that sequence — every script in the
    /// document — therefore sees the same value a browser would show it.
    /// </summary>
    private string _documentReadyState = "loading";

    /// <summary>
    /// Moves <c>document.readyState</c> on and fires <c>readystatechange</c> at the document, which
    /// is the event pages pair with the property (HTML §3.1.7: the state is set, then the event is
    /// fired at the document).
    /// </summary>
    private void SetDocumentReadyState(string state)
    {
        if (string.Equals(_documentReadyState, state, StringComparison.Ordinal))
            return;

        _documentReadyState = state;

        if (_jsContext == null || _document == null)
            return;

        try
        {
            var evt = new JSObject();
            evt.FastAddValue("type", new JSString("readystatechange"), JSPropertyAttributes.EnumerableConfigurableValue);
            evt.FastAddValue("bubbles", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
            DispatchEventOnElement(_document, evt);
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.SetDocumentReadyState",
                $"Error firing readystatechange listeners: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Fires the <c>load</c> event on every outermost inline <c>&lt;svg&gt;</c> beneath
    /// <paramref name="element"/> — the SVG counterpart of the stylesheet-link and
    /// nested-browsing-context passes either side of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SVG 1.1 §16.2 puts a <c>load</c> event on the outermost <c>&lt;svg&gt;</c> once the element
    /// and its children are parsed, and <c>onload</c> on that element is its handler. It is the
    /// entry point the SVG test suites are written against: they define a function in a
    /// <c>&lt;script&gt;</c> inside the fragment and call it from <c>&lt;svg onload="…"&gt;</c>.
    /// Nothing fired it here, so the handler never ran — and because those suites are written to
    /// paint a red "not supported" rectangle that the handler is supposed to clear, the page
    /// rendered its own failure state. Every <c>conformance-checkers/html-svg</c> case built that
    /// way was affected; <c>struct-dom-06-b-isvalid</c> is one, ranked at 16.5 % in the
    /// 2026-08-21 run.
    /// </para>
    /// <para>
    /// A <em>nested</em> <c>&lt;svg&gt;</c> is part of the fragment its outermost ancestor roots,
    /// not a root of its own, so the walk stops at the first <c>&lt;svg&gt;</c> it meets rather
    /// than dispatching again for every inner viewport.
    /// </para>
    /// </remarks>
    private void FireInlineSvgRootLoads(DomElement element)
    {
        if (_jsContext == null)
            return;

        if (string.Equals(element.TagName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            FireInlineSvgRootLoad(element);
            return;
        }

        // Snapshot before iterating: a load handler can structurally mutate the tree mid-walk —
        // the same hazard FireDescendantOnloads documents, and exactly what these tests do (the
        // SVG DOM cases remove the element that carries the failure text).
        foreach (var child in SnapshotChildren(element))
        {
            if (child is DomElement childElement)
                FireInlineSvgRootLoads(childElement);
        }
    }

    /// <summary>
    /// Dispatches <c>load</c> at one outermost inline <c>&lt;svg&gt;</c>, once. Runs the
    /// <c>onload</c> content attribute and any <c>addEventListener('load', …)</c> registered on
    /// the element, and gives the handler the <c>evt.target</c> the SVG suites read their
    /// <c>ownerDocument</c> from.
    /// </summary>
    private void FireInlineSvgRootLoad(DomElement element)
    {
        if (_browsingContexts.HasOnloadFired(element))
            return;
        _browsingContexts.MarkOnloadFired(element);

        try
        {
            var evt = new JSObject();
            evt.FastAddValue("type", new JSString("load"), JSPropertyAttributes.EnumerableConfigurableValue);
            evt.FastAddValue("bubbles", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
            DispatchEventOnElement(element, evt);
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.FireInlineSvgRootLoad",
                $"svg load handler error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Fires the <c>load</c> event on the <c>&lt;body&gt;</c> element, which
    /// triggers the inline <c>onload</c> attribute handler as well as any
    /// <c>addEventListener('load', …)</c> listeners registered on the body.
    /// In browsers, the body's <c>onload</c> fires after all synchronous
    /// scripts have executed. This is critical for test harnesses like Acid3,
    /// which use <c>&lt;body onload="update()"&gt;</c> to bootstrap the
    /// test runner.
    /// </summary>
    public void FireWindowLoadEvent()
    {
        ThrowIfDisposed();
        if (_jsContext == null) return;

        // Building the frames array is what mints each nested browsing context's window — and so
        // what runs that frame's scripts — so it is done eagerly here, before load fires, rather
        // than left until a page happens to read `frames`. The result is deliberately discarded:
        // `frames` is a live accessor on the window, which IS the global object, so assigning the
        // array here would replace that accessor with a snapshot frozen at load time.
        BuildWindowFramesArray();

        // Parsing is over by the time this runs — every synchronous script has executed — so the
        // document is "interactive" before DOMContentLoaded is dispatched, and "complete" once the
        // sub-resources are accounted for and `load` is about to fire.
        SetDocumentReadyState("interactive");
        _navigationTiming?.MarkDomInteractive();

        _navigationTiming?.MarkDomContentLoadedStart();
        FireDomContentLoadedEvent();
        _navigationTiming?.MarkDomContentLoadedEnd();

        var htmlEl = Elements.FirstOrDefault(e =>
            string.Equals(e.TagName, "html", StringComparison.OrdinalIgnoreCase));
        if (htmlEl != null)
        {
            // Stylesheet links first: HTML blocks the document's load event on its render-blocking
            // sheets, so by the time anything below runs their `load` events have already fired.
            FireDescendantStylesheetLinkLoads(htmlEl);
            FireDescendantOnloads(htmlEl);
            FireInlineSvgRootLoads(htmlEl);
        }

        SetDocumentReadyState("complete");
        _navigationTiming?.MarkDomComplete();
        _navigationTiming?.MarkLoadEventStart();

        // 1. Fire window.onload if it was set by script.
        //    In browsers, setting `window.onload = fn` registers a handler
        //    that fires when the page finishes loading.  This is distinct
        //    from the <body onload="…"> inline attribute handler.
        try
        {
            _jsContext.Eval(@"
(function() {
  // A page may register the load handler either as `window.onload = fn`
  // or as a bare `onload = fn` assignment. In a browser `window` IS the
  // global object so both are the same property; in this engine the global
  // object and `window` are distinct, so a bare `onload = fn` lands on the
  // global (globalThis.onload) and never on window.onload. Check both, with
  // window.onload winning when present.
  var h = null;
  if (typeof window.onload === 'function') h = window.onload;
  else if (typeof onload === 'function') h = onload;
  if (h) {
    try { h(); } catch(e) {}
  }
})();");
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.FireWindowLoadEvent",
                $"Error firing window.onload: {ex.Message}", ex);
        }

        try
        {
            DispatchWindowEvent("load");
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.FireWindowLoadEvent",
                $"Error firing window load listeners: {ex.Message}", ex);
        }

        // 2. Fire <body onload="…"> attribute handler and any load event
        //    listeners registered on the body element.
        // Find the <body> element by traversing the document tree.
        // The body is a child of <html> (documentElement), which is a
        // child of the document node.
        DomElement? body = null;
        if (htmlEl != null)
        {
            body = ChildElements(htmlEl).FirstOrDefault(c =>
                string.Equals(c.TagName, "body", StringComparison.OrdinalIgnoreCase));
        }
        if (body == null)
        {
            // No body to dispatch at: the load phase is over here, so close the mark on this path too.
            _navigationTiming?.MarkLoadEventEnd();
            return;
        }

        // Ensure the body's JS object is created so inline event attributes are compiled
        ToJSObject(body);

        // Dispatch a 'load' event on the body element. This covers inline
        // attributes, property-assigned handlers (document.body.onload = fn),
        // and addEventListener registrations using the same event path.
        try
        {
            if (_jsContext.Eval("(function() { var e = document.createEvent('Event'); e.initEvent('load', false, false); return e; })()") is JSObject evt)
                DispatchEventOnElement(body, evt);
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.FireWindowLoadEvent",
                $"Error firing window load event: {ex.Message}", ex);
        }

        _navigationTiming?.MarkLoadEventEnd();
    }

    /// <summary>
    /// Fires <c>DOMContentLoaded</c> at the document, then at the window. Nothing dispatched
    /// this event at all, so <c>document.addEventListener("DOMContentLoaded", …)</c> — one of
    /// the two idiomatic ways a page defers work until the DOM is ready — silently never ran.
    /// <para>
    /// It fires here, at the head of the load sequence, because the bridge has already run
    /// every synchronous script by the time this is called: that is the point at which parsing
    /// is finished, which is what the event means. Per DOM the event bubbles from the document
    /// to the window, but the window is a separate listener store with its own dispatch path
    /// here, so the two are dispatched explicitly rather than by propagation. Both are wrapped
    /// so a throwing listener cannot abort the rest of the load sequence, matching how the
    /// window <c>load</c> dispatch below is guarded.
    /// </para>
    /// </summary>
    private void FireDomContentLoadedEvent()
    {
        try
        {
            var evt = new JSObject();
            evt.FastAddValue("type", new JSString("DOMContentLoaded"), JSPropertyAttributes.EnumerableConfigurableValue);
            evt.FastAddValue("bubbles", JSBoolean.True, JSPropertyAttributes.EnumerableConfigurableValue);
            DispatchEventOnElement(_document, evt);
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.FireDomContentLoadedEvent",
                $"Error firing document DOMContentLoaded listeners: {ex.Message}", ex);
        }

        try
        {
            DispatchWindowEvent("DOMContentLoaded", bubbles: true);
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.FireDomContentLoadedEvent",
                $"Error firing window DOMContentLoaded listeners: {ex.Message}", ex);
        }
    }

    private JSBoolean DispatchWindowEvent(string eventType, bool bubbles = false)
    {
        var evt = new JSObject();
        evt.FastAddValue("type", new JSString(eventType), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("bubbles", bubbles ? JSBoolean.True : JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        return DispatchWindowEvent(evt);
    }

    private JSBoolean DispatchWindowEvent(JSObject evt)
    {
        if (_jsContext == null || _windowJSObject == null)
            return JSBoolean.True;

        var eventType = evt[(KeyString)"type"]?.ToString() ?? "unknown";
        evt.FastAddValue("target", _windowJSObject, JSPropertyAttributes.EnumerableConfigurableValue);
        evt[(KeyString)"srcElement"] = _windowJSObject;
        evt.FastAddValue("currentTarget", _windowJSObject, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("eventPhase", new JSNumber(2), JSPropertyAttributes.EnumerableConfigurableValue);

        var immediateStopped = false;
        var prevented = evt[(KeyString)"defaultPrevented"] is JSValue defaultPreventedValue &&
                        defaultPreventedValue.BooleanValue;
        var currentListenerPassive = false;
        var legacyCancelBubble = false;
        evt[(KeyString)"defaultPrevented"] = prevented ? JSBoolean.True : JSBoolean.False;
        evt.FastAddValue("stopPropagation",
            new DomFunction((in _) => JsCallbackStopPropagation001Core(ref legacyCancelBubble, in _), "stopPropagation", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("stopImmediatePropagation",
            new DomFunction((in _) => JsCallbackStopImmediatePropagation002Core(ref immediateStopped, ref legacyCancelBubble, in _), "stopImmediatePropagation", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("preventDefault",
            new DomFunction((in _) => JsCallbackPreventDefault003Core(currentListenerPassive, evt, ref prevented, in _), "preventDefault", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddProperty(
            "cancelBubble",
            new DomFunction((in _) => legacyCancelBubble ? JSBoolean.True : JSBoolean.False, "get cancelBubble"),
            new DomFunction((in setArgs) => JsCallbackSetCancelBubble005Core(ref legacyCancelBubble, in setArgs), "set cancelBubble"),
            JSPropertyAttributes.EnumerableConfigurableProperty);
        evt.FastAddProperty(
            "returnValue",
            new DomFunction((in _) => prevented ? JSBoolean.False : JSBoolean.True, "get returnValue"),
            new DomFunction((in setArgs) => JsCallbackSetReturnValue007Core(currentListenerPassive, evt, ref prevented, in setArgs), "set returnValue"),
            JSPropertyAttributes.EnumerableConfigurableProperty);
        evt.FastAddValue("composedPath",
            new DomFunction((in _) => new JSArray(_windowJSObject), "composedPath", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        if (_eventTargets.TryGetWindowListeners(eventType, out var listeners))
        {
            foreach (var registration in listeners.ToList())
            {
                if (immediateStopped)
                    break;

                currentListenerPassive = registration.Passive;
                InvokeEventListener(registration.Listener, evt, "DomBridge.window.dispatchEvent");
                currentListenerPassive = false;

                if (registration.Once)
                    listeners.Remove(registration);
            }
        }

        evt[(KeyString)"currentTarget"] = JSNull.Value;
        evt[(KeyString)"eventPhase"] = new JSNumber(0);
        return prevented ? JSBoolean.False : JSBoolean.True;
    }

    private JSArray BuildWindowFramesArray()
    {
        var frames = new List<JSValue>();
        CollectWindowFrames(DocumentElement, frames);
        return new JSArray([.. frames]);
    }

    private void CollectWindowFrames(DomElement element, List<JSValue> frames)
    {
        // Phase 4 item 4/5: reuse canonical Descendants() (public, document-order, level-snapshotted)
        // instead of a hand-rolled depth-first ChildElements recursion. Sub-documents are severed
        // (P4.4b) — never in-tree children — so the walk never crosses a frame boundary, and a nested
        // iframe's content (its own sub-document) is not a descendant here, matching the old walk.
        foreach (var child in element.Descendants().OfType<DomElement>())
        {
            // `window.frames` is the child browsing contexts, which includes a frameset's
            // <frame> cells and not just <iframe> (HTML §"nested browsing contexts").
            var childTag = child.TagName?.ToLowerInvariant();
            if (childTag is "iframe" or "frame")
            {
                var src = TryGetAttribute(child, "src", out var srcValue) ? srcValue : string.Empty;
                if (!IsCrossOrigin(src, _pageUrl))
                    frames.Add(_subWindows.GetOrCreate(child));
            }
        }
    }
}
