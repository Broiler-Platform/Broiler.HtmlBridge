using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.HtmlBridge.Core.Diagnostics;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Logging;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// DOM event dispatch — capture → target → bubble propagation and
/// element validity checks used during form-submission events.
/// </summary>
public sealed partial class DomBridge
{
    // addEventListener/removeEventListener registration semantics (option parsing, the
    // duplicate-registration check and match-by-listener+capture removal) moved to the Phase 3
    // EventListenerBinding feature module (Broiler.HtmlBridge.Dom.Features).

    /// <summary>
    /// Calls one registered listener — a function, or an object with a <c>handleEvent</c> — and
    /// swallows what it throws into a warning, because a listener that fails must not abort the
    /// dispatch of the ones after it.
    /// </summary>
    /// <remarks>
    /// <b>Still engine-typed, and shared.</b> Four firing paths reach it: element/document dispatch
    /// (<c>EventDispatchBinding</c>), window dispatch (<c>DomBridge.WindowLoad.cs</c>), form submit
    /// (<c>Features/FormSubmitBinding.cs</c>) and messaging (<c>Features/MessagingBinding.cs</c>) —
    /// three of which are outside this migration round. The listener it is handed is an
    /// <c>EventListenerRegistration</c>'s, engine-typed for the same reason. It migrates when the
    /// registration record does, and not before, so that all four move together.
    /// </remarks>
    internal static void InvokeEventListener(JSValue listener, JSObject evt, string logContext)
    {
        // Every DOM listener the page runs passes through here, which makes this the one place a
        // listener turn can be bracketed. Inactive unless a run asked for it; see JsEntryTrace.
        using var turn = JsEntryTrace.Enter(JsEntryKind.Event, EventTurnLabel(evt, logContext));

        try
        {
            if (listener is JSFunction fn)
            {
                fn.InvokeFunction(new Arguments(fn, evt));
                return;
            }

            if (listener is JSObject listenerObject &&
                listenerObject[(KeyString)"handleEvent"] is JSFunction handleEvent)
            {
                handleEvent.InvokeFunction(new Arguments(listenerObject, evt));
            }
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, logContext, $"Event listener error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Names a listener turn as <c>type@context</c> for the trace. The event type is what makes a line
    /// actionable — "20 s idle before click@document" says which interaction ended the idle, where the
    /// call site alone says only that some listener ran.
    /// </summary>
    /// <remarks>
    /// Reads the property only while the trace is active, and answers with the call site alone if the
    /// read throws: <c>type</c> is a data property on every event this bridge constructs, but a page
    /// may dispatch an object of its own through <c>dispatchEvent</c>, and a diagnostic does not get to
    /// turn that into a failure.
    /// </remarks>
    private static string EventTurnLabel(JSObject evt, string logContext)
    {
        if (!JsEntryTrace.IsActive)
            return logContext;

        try
        {
            var type = evt[(KeyString)"type"];
            return type is null || type.IsNullOrUndefined ? logContext : $"{type}@{logContext}";
        }
        catch (Exception)
        {
            return logContext;
        }
    }

    // Constraint validation (checkValidity/reportValidity) moved to the Phase 3 FormBinding feature
    // module (Broiler.HtmlBridge.Dom.Features).

    /// <summary>
    /// Dispatches a DOM event on the given element with full capture → target → bubble propagation.
    /// The engine lives in the Phase 3 EventDispatchBinding feature module.
    /// </summary>
    /// <remarks>
    /// The module speaks JSEAL now, so this is the engine-typed adapter over it rather than a bare
    /// delegator: roughly a dozen call sites that have not migrated — form submit, the dialog and
    /// script-insertion hosts, the window-load sequence, sub-documents, layout-driven scroll events —
    /// still hold an engine object and none of their files belong to this round. The cast costs
    /// nothing — a handle carries the engine's own object — and this signature narrows to the
    /// module's own as those callers move.
    /// </remarks>
    private JSValue DispatchEventOnElement(DomNode target, JSObject evt) =>
        // The result is the "not cancelled" boolean the DOM says dispatchEvent answers, so it
        // re-materialises as one rather than round-tripping: a JSEAL handle carries no engine object
        // for a primitive, and there is nothing else this call can return.
        _eventDispatch.DispatchEventOnElement(target, JsInterop.FromEngineObject(evt)).AsBoolean
            ? JSBoolean.True
            : JSBoolean.False;

    /// <summary>
    /// Compiles all <c>on*</c> HTML attributes (e.g. <c>onclick="code"</c>) on the given
    /// element into <see cref="JSFunction"/> instances stored in <see cref="bridge-owned inline event handler state"/>.
    /// Only compiles attributes that have not already been compiled.
    /// </summary>
    private void CompileInlineEventAttributes(DomElement element)
    {
        foreach (var eventName in InlineEventNames)
        {
            var attrName = $"on{eventName}";
            if (TryGetAttribute(element, attrName, out var code) &&
                !string.IsNullOrEmpty(code) &&
                !GetInlineEventHandlers(element).ContainsKey(eventName))
            {
                CompileInlineEventAttribute(element, attrName, code);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="element"/> is SVG content — the <c>&lt;svg&gt;</c> element itself or
    /// anything inside one. Decided by walking ancestors rather than reading a namespace, so it
    /// holds for a fragment the HTML parser built as well as one script created with
    /// <c>createElementNS</c>.
    /// </summary>
    private static bool IsInSvgContent(DomElement element)
    {
        for (var node = element; node != null; node = ParentEl(node))
        {
            if (string.Equals(node.TagName, "svg", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Compiles a single <c>on*</c> attribute value into a <see cref="JSFunction"/>
    /// and stores it in <see cref="bridge-owned inline event handler state"/>.
    /// </summary>
    internal void CompileInlineEventAttribute(DomElement element, string attrName, string code)
    {
        if (_jsContext == null || string.IsNullOrEmpty(code) || attrName.Length <= 2) return;
        var eventName = attrName[2..].ToLowerInvariant();
        if (Csp != null && !Csp.AllowsInlineEventHandler(code))
        {
            GetInlineEventHandlers(element).Remove(eventName);
            return;
        }

        try
        {
            // SVG 1.1 §16.2.2 names the event object `evt` inside an event-handler attribute,
            // where HTML §8.1.5.1 names it `event`. Both are real: an element in SVG content is
            // reached through the SVG rule, and the SVG test suites are written to it —
            // `<svg onload="domTest(evt)">` calling a function defined in a <script> inside the
            // fragment is the entry point of every conformance-checkers/html-svg case. With only
            // `event` bound, `evt` was undefined, the handler threw before its first statement,
            // and the page kept the red "not supported" state the handler exists to clear.
            //
            // The alias is added for SVG content only. Binding `evt` on an HTML element would
            // shadow a page's own global of that name inside its handlers, which no browser does.
            var svgEventAlias = IsInSvgContent(element) ? "var evt = event; " : string.Empty;
            var fn = _jsContext.Eval($"(function(event) {{ {svgEventAlias}{code} }})") as JSFunction;
            if (fn != null)
                GetInlineEventHandlers(element)[eventName] = fn;
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.CompileInlineEventAttribute",
                $"Failed to compile on{eventName} handler: {ex.Message}", ex);
        }
    }
}
