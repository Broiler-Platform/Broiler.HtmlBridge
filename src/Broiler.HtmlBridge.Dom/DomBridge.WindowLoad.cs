using System.Runtime.InteropServices;

// NO ENGINE NAMESPACE IS LEFT IN THIS FILE. The last one was the engine's array type, for
// `window.frames`, and the pin recorded for it was never real: the caller this file named as
// taking the engine's array -- DomBridge/Registration/Window.cs -- converted it straight back to
// a handle on the line it received it, so the reference bought a round trip and nothing else.
// The array is minted through the realm now and both halves of that pair are gone, along with
// the engine value list the builder collected into.
using Broiler.HtmlBridge.Jseal;
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

        if (_realm is null || _document == null)
            return;

        try
        {
            _eventDispatch.DispatchEventOnElement(_document, SimpleEvent("readystatechange", bubbles: false));
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
        if (_realm is null)
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
            _eventDispatch.DispatchEventOnElement(element, SimpleEvent("load", bubbles: false));
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
        if (_realm is not { } realm) return;

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
            realm.EvaluateHostScript(@"
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
})();", "broiler:window-onload");
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
        WrapNode(body);

        // Dispatch a 'load' event on the body element. This covers inline
        // attributes, property-assigned handlers (document.body.onload = fn),
        // and addEventListener registrations using the same event path.
        try
        {
            var evt = realm.EvaluateHostScript(
                "(function() { var e = document.createEvent('Event'); e.initEvent('load', false, false); return e; })()",
                "broiler:body-load-event");
            if (evt.IsObject)
                _eventDispatch.DispatchEventOnElement(body, evt);
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
            _eventDispatch.DispatchEventOnElement(_document, SimpleEvent("DOMContentLoaded", bubbles: true));
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

    /// <summary>
    /// A plain event object carrying only the two members a bridge-fired simple event needs.
    /// </summary>
    /// <remarks>
    /// Built and dispatched entirely in JSEAL: the three node-target sites above hand it straight to
    /// <c>EventDispatchBinding</c>, and <see cref="DispatchWindowEvent"/> now takes a handle too, so
    /// nothing unwraps this on the way in. The one unwrap left on that path is at the listener call
    /// itself, which belongs to <c>InvokeEventListener</c> rather than to anything built here.
    /// </remarks>
    private JsValue SimpleEvent(string type, bool bubbles)
    {
        var realm = Realm;
        var evt = realm.NewObject();
        realm.DefineValue(evt, "type", JsValue.String(type));
        realm.DefineValue(evt, "bubbles", JsValue.Boolean(bubbles));
        return evt;
    }

    private bool DispatchWindowEvent(string eventType, bool bubbles = false)
    {
        if (_realm is null)
            return true;

        return DispatchWindowEvent(SimpleEvent(eventType, bubbles));
    }

    /// <summary>
    /// Fires <paramref name="evt"/> at the window: the synthetic event object is completed with its
    /// target/phase members and the five propagation-control operations, then every registered window
    /// listener for its type runs. Answers whether the default action survived
    /// (<c>false</c> = <c>preventDefault()</c> was called), which is what <c>dispatchEvent</c> returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This signature was never pinned from outside, and the four names this paragraph used to
    /// give as its pins were all wrong.</b> <c>IWindowEventTargetHost</c>, <c>ILocationHost</c> and
    /// <c>IMessagingHost</c> each declare <c>DispatchWindowEvent(JsValue)</c> and have since they
    /// were extracted; the engine object was minted by the three bridge-side adapters that implement
    /// them, and this parameter was its only reader. <c>DomBridge/LayoutMetrics.Scrolling.cs</c>, the
    /// fourth, calls the string overload and discards what it answers. Of the seven call sites the two
    /// overloads have between them, exactly one reads the return value at all -- and it converted it
    /// to a handle on the same line.
    /// </para>
    /// <para>
    /// <b>One pin was real, and it is a call site rather than a signature.</b>
    /// <c>InvokeEventListener</c> in <c>DomBridge/Events.cs</c> takes the engine object, because the
    /// listener beside it comes out of an <c>EventListenerRegistration</c> whose record is
    /// engine-typed in the unowned <c>DomBridge/RuntimeStates.cs</c>. So the crossing is one
    /// conversion at the top of this method, in the shape <c>Features/EventDispatchBinding.cs</c>
    /// already uses for the same call, rather than one in each of three callers.
    /// </para>
    /// <para>
    /// <b>The five propagation-control operations are local functions now, and that is what let them
    /// move.</b> They were <c>DomBridge/JsFunctionCallbacks/Callback.cs</c>'s five
    /// <c>JsCallback…Core</c> methods, taking an engine argument frame because the lambdas installing
    /// them here were engine functions — the cycle described in the JSEAL migration notes, which only
    /// breaks when the installer and the body change together. Both are here, so both changed: the
    /// bodies close over the same four locals the <c>ref</c> parameters used to carry, in the shape
    /// <see cref="Dom.Features.LegacyEventBinding"/> already uses for the same five operations on a
    /// <c>createEvent</c> object. Nothing in this file calls <c>Callback.cs</c> any more.
    /// </para>
    /// <para>
    /// The event's <c>type</c> is read with the realm's <c>ToString</c> rather than the handle's own
    /// rendering, because that is what the engine-typed read it replaces did: a page that dispatches an
    /// object whose <c>type</c> has a <c>toString</c> gets that <c>toString</c> run, and the listener
    /// lookup keys on its result.
    /// </para>
    /// </remarks>
    private bool DispatchWindowEvent(JsValue evt)
    {
        // Taken before the guard because that is where it always happened: the three adapters
        // converted on the way in, so a handle carrying no engine object failed before this method's
        // first property write, and it still does. `_windowJSObject` is the engine's own field
        // (DomBridge.cs), so the null test beside it is a reference test and stays one.
        var engineEvent = Dom.Runtime.JsInterop.ToEngineObject(evt);

        if (_realm is not { } realm || _windowJSObject == null)
            return true;

        var window = Dom.Runtime.JsInterop.FromEngineObject(_windowJSObject);

        // A CLR-absent `type` is the only thing that reads as "unknown"; an explicit `undefined`
        // coerces to the string "undefined", exactly as the former ToString() did.
        var typeValue = realm.GetProperty(evt, "type");
        var eventType = typeValue.IsMissing ? "unknown" : realm.ToJsString(typeValue);
        realm.DefineValue(evt, "target", window);
        realm.SetProperty(evt, "srcElement", window);
        realm.DefineValue(evt, "currentTarget", window);
        realm.DefineValue(evt, "eventPhase", JsValue.Number(2));

        var immediateStopped = false;
        var prevented = realm.GetProperty(evt, "defaultPrevented").AsBoolean;
        var currentListenerPassive = false;
        var legacyCancelBubble = false;
        realm.SetProperty(evt, "defaultPrevented", JsValue.Boolean(prevented));

        // Installed in the order they always were: Object.getOwnPropertyNames on the event is
        // observable, so the sequence of these six is part of the behaviour, not a detail.
        realm.DefineValue(evt, "stopPropagation", realm.NewMethod("stopPropagation", StopPropagation, 0));
        realm.DefineValue(evt, "stopImmediatePropagation",
            realm.NewMethod("stopImmediatePropagation", StopImmediatePropagation, 0));
        realm.DefineValue(evt, "preventDefault", realm.NewMethod("preventDefault", PreventDefault, 0));
        realm.DefineAccessor(evt, "cancelBubble", GetCancelBubble, SetCancelBubble);
        realm.DefineAccessor(evt, "returnValue", GetReturnValue, SetReturnValue);
        realm.DefineValue(evt, "composedPath",
            realm.NewMethod("composedPath", (in _) => realm.NewArray([window]), 0));

        if (_eventTargets.TryGetWindowListeners(eventType, out var listeners))
        {
            foreach (var registration in listeners.ToList())
            {
                if (immediateStopped)
                    break;

                currentListenerPassive = registration.Passive;
                InvokeEventListener(registration.Listener, engineEvent, "DomBridge.window.dispatchEvent");
                currentListenerPassive = false;

                if (registration.Once)
                    listeners.Remove(registration);
            }
        }

        realm.SetProperty(evt, "currentTarget", JsValue.Null);
        realm.SetProperty(evt, "eventPhase", JsValue.Number(0));
        return !prevented;

        JsValue StopPropagation(in JsCall _)
        {
            legacyCancelBubble = true;
            return JsValue.Undefined;
        }

        JsValue StopImmediatePropagation(in JsCall _)
        {
            immediateStopped = true;
            legacyCancelBubble = true;
            return JsValue.Undefined;
        }

        JsValue PreventDefault(in JsCall _)
        {
            // An absent `cancelable` reads as an absent value and is not truthy, which is the same
            // answer the engine-typed `!= null && .BooleanValue` pair gave. A passive listener may
            // not cancel, and the flag is read at call time rather than captured, exactly as the
            // lambda that used to pass it did.
            if (!currentListenerPassive && realm.GetProperty(evt, "cancelable").AsBoolean)
            {
                prevented = true;
                realm.SetProperty(evt, "defaultPrevented", JsValue.True);
            }

            return JsValue.Undefined;
        }

        JsValue GetCancelBubble(in JsCall _) => JsValue.Boolean(legacyCancelBubble);

        JsValue SetCancelBubble(in JsCall setCall)
        {
            // Assigning a falsy value does not clear the flag — the legacy property's one-way
            // behaviour, and what this did before.
            if (setCall.Length > 0 && setCall[0].AsBoolean)
                legacyCancelBubble = true;

            return JsValue.Undefined;
        }

        // The dispatch loop's own `prevented`, not the event's `defaultPrevented` property: a page
        // that overwrote `defaultPrevented` by hand does not change what this answers, which is what
        // reading the local has always meant here.
        JsValue GetReturnValue(in JsCall _) => JsValue.Boolean(!prevented);

        JsValue SetReturnValue(in JsCall setCall)
        {
            // Short-circuit order preserved: `cancelable` is only read once the assignment is known
            // to be a falsy one from a non-passive listener, so a getter a page put there runs on
            // exactly the assignments it ran on before.
            if (setCall.Length > 0 && !setCall[0].AsBoolean && !currentListenerPassive &&
                realm.GetProperty(evt, "cancelable").AsBoolean)
            {
                prevented = true;
                realm.SetProperty(evt, "defaultPrevented", JsValue.True);
            }

            return JsValue.Undefined;
        }
    }

    /// <summary>
    /// A fresh <c>window.frames</c>: every same-origin nested browsing context's window, in
    /// document order. Minted through the realm, so the accessor in
    /// <c>DomBridge/Registration/Window.cs</c> returns what it is handed instead of converting it.
    /// </summary>
    /// <remarks>
    /// The span is the list's own storage rather than a copy of it — the copy the collection
    /// expression used to make on the way into the array constructor. The realm still materialises
    /// an array from it, in the one pass the engine's constructor made. Same shape as
    /// <c>BuildAnimationList</c> in <c>DomBridge/Registration/Animations.cs</c>.
    /// </remarks>
    private JsValue BuildWindowFramesArray()
    {
        var frames = new List<JsValue>();
        CollectWindowFrames(DocumentElement, frames);
        return Realm.NewArray(CollectionsMarshal.AsSpan(frames));
    }

    private void CollectWindowFrames(DomElement element, List<JsValue> frames)
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
