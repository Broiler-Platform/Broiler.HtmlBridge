using Broiler.HtmlBridge.Core.Diagnostics;
using Broiler.HtmlBridge.Jseal;
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
    /// Calls one registered listener -- a function, or an object with a <c>handleEvent</c> -- and
    /// swallows what it throws into a warning, because a listener that fails must not abort the
    /// dispatch of the ones after it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The listener is a handle and the call goes through the realm.</b> Five call sites over four
    /// firing paths reach this: element and document dispatch (<c>Features/EventDispatchBinding.cs</c>),
    /// window dispatch (<c>DomBridge.WindowLoad.cs</c>), form submit (<c>Features/FormSubmitBinding.cs</c>)
    /// and messaging (<c>Features/MessagingBinding.cs</c>, once for a registration and once for an
    /// <c>on…</c> handler). Four hand over an <c>EventListenerRegistration</c>'s listener field, which
    /// holds a <see cref="JsValue"/> now, and the fifth reads its handler through the realm. Nothing on
    /// any of those paths converts a listener or an event, and nothing here names an engine type.
    /// </para>
    /// <para>
    /// <b>The receivers are the ones the engine-typed calls passed, including the odd one.</b> A function
    /// listener is its own <c>this</c>, as the engine argument frame built from the function made it --
    /// where DOM's inner invoke passes the event's <c>currentTarget</c> -- and an object's
    /// <c>handleEvent</c> is called with the object. <c>Features/EventDispatchBinding.cs</c> already fires
    /// the inline <c>on*</c> handler through the realm with itself as receiver; this is that shape.
    /// </para>
    /// <para>
    /// <b>Three things differ from the direct engine call, and all three were read rather than
    /// assumed.</b> <see cref="IJsCalls.Invoke"/> takes the provider's realm scope for the call, and so
    /// does the <c>handleEvent</c> lookup, which is <see cref="IJsMembers.GetProperty"/> -- the same
    /// indexer, so a getter for it still runs once. For the realm this bridge adopts, that scope makes the
    /// realm's context the engine's current context and restores the previous one after -- and nothing
    /// else, because the provider installs a job pump only for a context it created; a listener already
    /// running under that context sees nothing change. An exception the listener throws reaches the catch
    /// below as the provider's <see cref="JsEngineException"/> rather than the engine's own, constructed
    /// from the same message, so the warning reads the same. And the event is no longer unwrapped ahead
    /// of the turn, where a handle carrying no object used to fail out to the dispatch that passed it;
    /// none can arrive, because every <c>dispatchEvent</c> a page can call refuses a non-object, and every
    /// event the bridge dispatches itself is an object it minted or tested as one.
    /// </para>
    /// </remarks>
    internal static void InvokeEventListener(
        IJsRealm realm, JsValue listener, JsValue evt, string logContext)
    {
        // Every DOM listener the page runs passes through here, which makes this the one place a
        // listener turn can be bracketed. Inactive unless a run asked for it; see JsEntryTrace.
        using var turn = JsEntryTrace.Enter(JsEntryKind.Event, EventTurnLabel(realm, evt, logContext));

        try
        {
            if (listener.IsFunction)
            {
                realm.Invoke(listener, listener, [evt]);
                return;
            }

            if (!listener.IsObject)
                return;

            var handleEvent = realm.GetProperty(listener, "handleEvent");
            if (handleEvent.IsFunction)
                realm.Invoke(handleEvent, listener, [evt]);
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
    /// <para>
    /// <b>The compile goes through the realm, and the store holds what the realm answered.</b>
    /// <see cref="IJsSource.EvaluateClassicScript"/> is the right call and not merely the available
    /// one: HTML §8.1.5.1 makes an event-handler content attribute subject to the
    /// <c>script-src</c>/<c>unsafe-inline</c> decision taken above rather than to <c>eval</c>'s, so
    /// the Content-Security-Policy check stays where it is and the evaluation here is unconditional.
    /// </para>
    /// <para>
    /// <b>THIS USED TO SAY <see cref="IJsSource.EvaluateHostScript"/>, AND HALF OF WHY WAS WRONG.</b>
    /// The argument had two limbs: that the directive decides, which is right and is the reason the
    /// contract now has a member for exactly this; and that the wrapper around the page's statements
    /// is this repository's, which made the host member's promise — "JavaScript this repository
    /// authored" — read as literally true. It is not a promise about who typed the punctuation. The
    /// wrapper is a pair of parentheses and a parameter list; if that converted a page's program into
    /// this repository's, the promise would have no content at all, since any call site could satisfy
    /// it by wrapping. The statements inside are the page's and the page can tell.
    /// </para>
    /// <para>
    /// <b>It is not a cosmetic re-labelling on every engine.</b> On a provider whose only compiler is
    /// a registered artifact provider, the host-script mark is the permission to compile and is held
    /// for the whole evaluation; the classic-script permit is spent by the one compile it authorises
    /// and suspends that mark while it runs. So this moves a page's program off the permission
    /// reserved for source that never calls the page's code, which is the distinction the third
    /// member was added to make, and this was the last site still compiling a page's program under
    /// that permission.
    /// </para>
    /// <para>
    /// The handle is stored as it is. This remark used to say it had to be unwrapped first because the
    /// map was typed over the engine's value, "declared in the unowned <c>DomBridge/RuntimeStates.cs</c>
    /// and read by the equally unowned dispatch path". The dispatch path is
    /// <c>Features/EventDispatchBinding.cs</c>, which already spoke JSEAL and was handed a handle minted
    /// back over the very object unwrapped here, so this unwrap and that wrap were one round trip. The
    /// function dispatch runs is still the one compiled here.
    /// </para>
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
            //
            // It lost its `broiler:` prefix with the member. This repository labels some of the source
            // it authored that way -- `broiler:window-onload`, `broiler:dataset` -- and a stack frame
            // naming a page's own onclick that way pointed a reader at the wrong author on the one line
            // they had to go on.
            var fn = realm.EvaluateClassicScript(
                $"(function(event) {{ {svgEventAlias}{code} }})", "inline-event-handler");
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
