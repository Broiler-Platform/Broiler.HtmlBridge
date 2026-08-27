using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
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

    internal static void InvokeEventListener(JSValue listener, JSObject evt, string logContext)
    {
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

    // Constraint validation (checkValidity/reportValidity) moved to the Phase 3 FormBinding feature
    // module (Broiler.HtmlBridge.Dom.Features).

    /// <summary>
    /// Dispatches a DOM event on the given element with full capture → target → bubble propagation.
    /// The engine lives in the Phase 3 EventDispatchBinding feature module; this thin delegator keeps
    /// the historical call sites (element/document dispatchEvent, form submit, XHR/layout-driven
    /// synthetic events) source-compatible.
    /// </summary>
    private JSValue DispatchEventOnElement(DomNode target, JSObject evt) =>
        _eventDispatch.DispatchEventOnElement(target, evt);

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
