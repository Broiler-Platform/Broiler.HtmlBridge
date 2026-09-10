using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.HtmlBridge.Core.Diagnostics;
using Broiler.HtmlBridge.Jseal;
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
    /// <para>
    /// <b>Still engine-typed, and the pin is not in this file.</b> Four firing paths reach it:
    /// element/document dispatch (<c>EventDispatchBinding</c>), window dispatch
    /// (<c>DomBridge.WindowLoad.cs</c>), form submit (<c>Features/FormSubmitBinding.cs</c>) and
    /// messaging (<c>Features/MessagingBinding.cs</c>). All four hand over an
    /// <c>EventListenerRegistration</c>'s listener field, which is a Broiler.JS value because that
    /// record is declared in <c>DomBridge/RuntimeStates.cs</c> — a file no migration round has owned
    /// yet. Narrowing the first parameter alone changes nothing: the three unowned firing paths would
    /// still compile, because they only forward the field, and the field would still be the engine's.
    /// So it migrates in the step that retypes the record, and not before.
    /// </para>
    /// <para>
    /// <b>There is a second reason to move it deliberately rather than in passing, and it has since
    /// been measured.</b> The call below enters the engine directly, taking the thread exactly as it
    /// finds it. A listener can be invoked from a thread-pool thread — the messaging path routes one
    /// into its owner window explicitly for that reason. <c>IJsCalls.Invoke</c> would bracket the
    /// call in the provider's realm scope, and for <em>this</em> realm that is one difference and not
    /// two: the bridge adopts the host's engine context rather than owning one, and the provider
    /// deliberately leaves an adopted realm's synchronization context alone, so no job pump is
    /// installed. What is installed is the engine's current-context slot — thread-static plus
    /// async-local — for the duration of the listener, which on a pool thread that has none is a
    /// change from "whatever the thread was carrying" to "this document's context". That is almost
    /// certainly the right thing and it is still not the same thing, so it belongs in the commit that
    /// moves the registration record and can be reasoned about across all four paths at once.
    /// </para>
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
    /// Compiles all <c>on*</c> HTML attributes (e.g. <c>onclick="code"</c>) on the given
    /// element into functions stored in the bridge-owned inline event handler state.
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
    /// Compiles a single <c>on*</c> attribute value into a function and stores it in the
    /// bridge-owned inline event handler state.
    /// </summary>
    /// <remarks>
    /// <b>The compile goes through the realm; the store it writes into does not.</b>
    /// <see cref="IJsSource.EvaluateHostScript"/> is the right call and not merely the available one:
    /// an event-handler content attribute is source the <em>page</em> wrote, but the wrapper around it
    /// is this repository's, and HTML §8.1.5.1 makes the attribute subject to the
    /// <c>script-src</c>/<c>unsafe-inline</c> decision taken above rather than to <c>eval</c>'s — so
    /// the Content-Security-Policy check stays where it is and the evaluation is unconditional, which
    /// is exactly what the bare <c>Eval</c> it replaces did. The compiled handler is unwrapped to the
    /// engine's own value because the map it lands in is keyed by name over the engine's value type,
    /// declared in the unowned <c>DomBridge/RuntimeStates.cs</c> and read by the equally unowned
    /// dispatch path; unwrapping is a cast over the object the handle already carries, so the function
    /// a listener runs is the one compiled here.
    /// </remarks>
    internal void CompileInlineEventAttribute(DomElement element, string attrName, string code)
    {
        if (_realm is not { } realm || string.IsNullOrEmpty(code) || attrName.Length <= 2) return;
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

            // One constant label rather than one per handler: it is the location a stack frame
            // reports, and a label that varied with the event name would give the engine's code
            // cache a different key for every attribute compiling the same wrapper shape.
            var fn = realm.EvaluateHostScript(
                $"(function(event) {{ {svgEventAlias}{code} }})", "broiler:inline-event-handler");
            if (fn.IsFunction)
                GetInlineEventHandlers(element)[eventName] = JsInterop.ToEngineObject(fn);
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.CompileInlineEventAttribute",
                $"Failed to compile on{eventName} handler: {ex.Message}", ex);
        }
    }
}
