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
    /// <b>The event is a handle; the listener is still the engine's, and that pin is not in this
    /// file.</b> Four firing paths reach this through five call sites: element and document dispatch
    /// (<c>Features/EventDispatchBinding.cs</c>), window dispatch (<c>DomBridge.WindowLoad.cs</c>),
    /// form submit (<c>Features/FormSubmitBinding.cs</c>), and the generic event-target dispatch in
    /// <c>Features/MessagingBinding.cs</c>, which calls it twice. Four of the five hand over an
    /// <c>EventListenerRegistration</c>'s listener field, a Broiler.JS value because that record is
    /// declared as one in <c>DomBridge/RuntimeStates.cs</c>. The fifth does not:
    /// <c>MessagingBinding.InvokeEventTargetHandler</c> reads the target's <c>on…</c> property and
    /// hands over what it read. This paragraph used to say every path only forwards the field, so the
    /// step that retypes the record has one caller more to answer for than it was told. Narrowing the
    /// listener parameter is still that step's, and not before.
    /// </para>
    /// <para>
    /// <b>The event parameter never had that pin, and it has moved.</b> All five callers held the
    /// event as a handle and unwrapped it to call this: once at the top of window dispatch, once per
    /// listener fired on the element path, once before the listener loop on form submit, and once for
    /// the <c>on…</c> handler plus once for the listener loop on the generic event-target path. The
    /// unwrap is below now, once per call on every path, and it is a cast over the object the handle
    /// carries rather than a conversion. The realm comes in beside it because this method is static
    /// and the trace label reads the event's <c>type</c> through it; it is also what calling the
    /// listener through <see cref="IJsCalls.Invoke"/> will need, so the step that retypes the record
    /// changes what this method does without changing the four call sites that forward the field.
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
    internal static void InvokeEventListener(IJsRealm realm, JSValue listener, JsValue evt, string logContext)
    {
        // The event crosses to the engine here for all five callers, and "here" means ahead of the
        // turn and outside the try: that is where each caller's own unwrap sat, so a handle carrying no
        // engine object still fails out to the dispatch that passed it instead of being logged as a
        // listener error.
        var engineEvent = Dom.Runtime.JsInterop.ToEngineObject(evt);

        // Every DOM listener the page runs passes through here, which makes this the one place a
        // listener turn can be bracketed. Inactive unless a run asked for it; see JsEntryTrace.
        using var turn = JsEntryTrace.Enter(JsEntryKind.Event, EventTurnLabel(realm, evt, logContext));

        try
        {
            if (listener is JSFunction fn)
            {
                fn.InvokeFunction(new Arguments(fn, engineEvent));
                return;
            }

            if (listener is JSObject listenerObject &&
                listenerObject[(KeyString)"handleEvent"] is JSFunction handleEvent)
            {
                handleEvent.InvokeFunction(new Arguments(listenerObject, engineEvent));
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
    /// <para>
    /// Reads the property only while the trace is active, and answers with the call site alone if the
    /// read throws: <c>type</c> is a data property on every event this bridge constructs, but a page
    /// may dispatch an object of its own through <c>dispatchEvent</c>, and a diagnostic does not get to
    /// turn that into a failure.
    /// </para>
    /// <para>
    /// <b>Through the realm, and it answers what the engine-typed read answered.</b> The realm's
    /// property read is the engine's own indexer on the same object, so a page-defined <c>type</c>
    /// accessor still runs, and the three "no type" answers the former test named — never
    /// installed, <c>null</c>, <c>undefined</c> — come back as exactly the three kinds
    /// <see cref="JsValue.IsNullish"/> tests. <see cref="IJsValues.ToJsString"/> rather than the
    /// handle's own rendering, because the label used to interpolate the engine's value, and that is
    /// the engine's <c>ToString</c>: the ECMAScript coercion, which may run a <c>toString</c> the page
    /// wrote. The handle would render an object as <c>[object]</c> and run nothing.
    /// </para>
    /// <para>
    /// <b>The one difference is the realm's scope</b>, which each of the two calls takes: page code
    /// either of them reaches runs with this document's context installed as the engine's current
    /// one, rather than with whatever the thread was carrying. That needs the trace to be active and a
    /// <c>type</c> the page supplied as an accessor or an object — the events this bridge builds carry
    /// a string, and reading one runs nothing — and on the window and generic event-target paths
    /// that same accessor and that same coercion have already run inside the realm's scope at the top
    /// of the same dispatch, to find the listeners.
    /// </para>
    /// </remarks>
    private static string EventTurnLabel(IJsRealm realm, JsValue evt, string logContext)
    {
        if (!JsEntryTrace.IsActive)
            return logContext;

        try
        {
            var type = realm.GetProperty(evt, "type");
            return type.IsNullish ? logContext : $"{realm.ToJsString(type)}@{logContext}";
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
    /// <b>The compile goes through the realm, and the store holds what the realm answered.</b>
    /// <see cref="IJsSource.EvaluateHostScript"/> is the right call and not merely the available one:
    /// an event-handler content attribute is source the <em>page</em> wrote, but the wrapper around it
    /// is this repository's, and HTML §8.1.5.1 makes the attribute subject to the
    /// <c>script-src</c>/<c>unsafe-inline</c> decision taken above rather than to <c>eval</c>'s — so
    /// the Content-Security-Policy check stays where it is and the evaluation is unconditional, which
    /// is exactly what the bare <c>Eval</c> it replaces did. The handle is stored as it is. This
    /// paragraph used to say it had to be unwrapped first because the map was typed over the engine's
    /// value, "declared in the unowned <c>DomBridge/RuntimeStates.cs</c> and read by the equally unowned
    /// dispatch path". The dispatch path is <c>Features/EventDispatchBinding.cs</c>, which already spoke
    /// JSEAL and was handed a handle minted back over the very object unwrapped here, so this unwrap
    /// and that wrap were one round trip. The function dispatch runs is still the one compiled here.
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
                GetInlineEventHandlers(element)[eventName] = fn;
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.CompileInlineEventAttribute",
                $"Failed to compile on{eventName} handler: {ex.Message}", ex);
        }
    }
}
