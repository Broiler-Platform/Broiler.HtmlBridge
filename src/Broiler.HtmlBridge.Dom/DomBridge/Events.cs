using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Logging;
using Broiler.Dom;
using static Broiler.HtmlBridge.DomBridgeUtils;

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
    /// and suspends that mark while it runs. So this moves a page's program off the permission meant
    /// for source this repository authored, which is the distinction
    /// <see cref="IJsSource.EvaluateClassicScript"/> was added to make, and this was the only site
    /// handing that permission source text the page wrote. It is not the only way the page's code can
    /// run while the mark is held: host script that calls a function the page wrote or replaced lends
    /// it the mark, as <c>broiler:window-onload</c> does with the page's <c>onload</c>.
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
