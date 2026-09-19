using Broiler.Dom;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// <c>EventTarget.prototype</c>'s three methods, routed to whichever listener store the receiver
/// actually uses — so a node, a document and the window are the <c>EventTarget</c>s they already
/// claim to be.
/// </summary>
/// <remarks>
/// <para>
/// The realm carries its own <c>EventTarget</c>, a JS-engine class whose <c>addEventListener</c>
/// keeps its listeners in fields on the C# instance. A DOM wrapper is an ordinary object and never
/// one of those, so borrowing the prototype method threw: <c>node instanceof EventTarget</c>
/// answered <see langword="true"/> — the interface graph says so — while
/// <c>EventTarget.prototype.addEventListener.call(node, 'x', fn)</c> was a
/// <c>TypeError: Failed to convert this to EventTarget</c>. The bridge's own
/// <c>addEventListener</c> was a separate function installed as an own property of every wrapper, so
/// <c>node.addEventListener === EventTarget.prototype.addEventListener</c> was
/// <see langword="false"/> where a browser says <see langword="true"/>.
/// </para>
/// <para>
/// That is not only a shape difference. Borrowing the prototype method is what a library does when
/// it cannot trust the instance's own — <c>EventTarget.prototype.addEventListener.call(el, …)</c> is
/// ordinary defensive code — and here it threw rather than registering, so the listener was silently
/// never added.
/// </para>
/// <para>
/// <b>Routing by receiver, with the engine's own behaviour preserved.</b> The replacement resolves
/// the receiver in three steps: the window object, then any registered node wrapper — which covers
/// elements, text, comments, fragments <em>and</em> the document, since the document's wrapper is
/// registered as its node's and its listener store is the same per-node one — and otherwise
/// delegates to the function the engine installed. So <c>new EventTarget()</c>, an
/// <c>AbortSignal</c> and every other engine-side target keep working exactly as before, and only a
/// receiver this bridge owns is taken over.
/// </para>
/// <para>
/// With one function serving every receiver, the per-wrapper copies are redundant and are gone: a
/// text or comment node now carries no own properties at all, and
/// <c>node.addEventListener === EventTarget.prototype.addEventListener</c> holds. The
/// <c>length</c> of each is Web IDL's — <c>2</c>, <c>2</c>, <c>1</c>, measured against Chromium —
/// where the copies advertised <c>3</c>, <c>3</c>, <c>1</c>.
/// </para>
/// <para>
/// <b>The routing table names no engine type, and the fallback is the part that had to be shown to be
/// expressible.</b> It used to read "JSEAL cannot say: the function that was there before I replaced
/// it, called with exactly these arguments and this receiver", and that is no longer true — a JSEAL
/// call frame exposes the arguments it was supplied as a span, and <c>Invoke(function, thisValue,
/// arguments)</c> takes one, so handing the call back to the engine's own function is a single line.
/// The other half was the callees: a routed method can only be realm-minted if every body it reaches
/// takes a JSEAL frame, so the three window operations and the three node ones had to migrate in the
/// same change as this file. They did.
/// </para>
/// <para>
/// <b>The receiver resolution moved as well, and this remark said it had not.</b> It said the
/// node-wrapper registry and the window wrapper field were keyed on the engine's own object, that the
/// receiver was unwrapped to ask them, and that the same pin kept an engine-framed twin of each of the
/// three below in <c>Dom.Features.EventTargetBinding</c>. The registry is asked with the handle and the
/// window test is handle equality (<see cref="RouteEventTargetMethod"/>). The per-wrapper copies
/// <c>DomBridge/JsObjects.cs</c> and <c>JsObjects.NonElementNodes.cs</c> still install are
/// realm-minted over the same <see cref="JsCall"/> bodies the routed methods call, and there is no
/// twin.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// Whether <c>EventTarget.prototype</c> carries the routed methods, which is what lets a wrapper
    /// stop installing its own. A wrapper minted before the realm is up still installs them.
    /// </summary>
    private bool _eventTargetRoutingReady;

    /// <summary>
    /// Replaces <c>EventTarget.prototype</c>'s <c>addEventListener</c>, <c>removeEventListener</c>
    /// and <c>dispatchEvent</c> with versions that route by receiver. A no-op when the realm has no
    /// <c>EventTarget</c>.
    /// </summary>
    /// <remarks>
    /// The three functions the engine installed are read out first and captured, because the very
    /// next thing this does is overwrite them; each routed method keeps its own so that a receiver
    /// this bridge does not own still reaches the one that was there.
    /// </remarks>
    internal void RegisterEventTargetRouting()
    {
        var proto = PrototypeHandleOfInterface("EventTarget");
        if (!proto.IsObject)
            return;

        var realm = Realm;
        var engineAdd = realm.GetProperty(proto, "addEventListener");
        var engineRemove = realm.GetProperty(proto, "removeEventListener");
        var engineDispatch = realm.GetProperty(proto, "dispatchEvent");

        RouteEventTargetMethod(proto, "addEventListener", 2, engineAdd,
            (in JsCall call, DomNode node) => Dom.Features.EventTargetBinding.AddEventListener(this, node, in call),
            (in JsCall call) => Dom.Features.WindowEventTargetBinding.AddEventListener(this, in call));

        RouteEventTargetMethod(proto, "removeEventListener", 2, engineRemove,
            (in JsCall call, DomNode node) => Dom.Features.EventTargetBinding.RemoveEventListener(this, node, in call),
            (in JsCall call) => Dom.Features.WindowEventTargetBinding.RemoveEventListener(this, in call));

        RouteEventTargetMethod(proto, "dispatchEvent", 1, engineDispatch,
            (in JsCall call, DomNode node) => Dom.Features.EventTargetBinding.DispatchEvent(this, node, in call),
            (in JsCall call) => Dom.Features.WindowEventTargetBinding.DispatchEvent(this, in call));

        _eventTargetRoutingReady = true;
    }

    /// <summary>A prototype method's body once the receiver has been resolved to a DOM node.</summary>
    private delegate JsValue NodeEventTargetOperation(in JsCall call, DomNode node);

    /// <summary>A prototype method's body for the window receiver, which is not a node.</summary>
    private delegate JsValue WindowEventTargetOperation(in JsCall call);

    /// <summary>
    /// Installs one routed method on <c>EventTarget.prototype</c>, keeping the engine's own as the
    /// fallback for a receiver this bridge does not own.
    /// </summary>
    /// <remarks>
    /// The property is enumerable, configurable and writable — the attributes the engine's own three
    /// carried and the ones Web IDL asks for on a prototype — so only the <em>body</em> of each
    /// changes, not its descriptor. The declared <c>length</c> is Web IDL's, measured against
    /// Chromium: 2, 2, 1.
    /// </remarks>
    private void RouteEventTargetMethod(JsValue proto, string name, int length, JsValue engineMethod,
        NodeEventTargetOperation onNode, WindowEventTargetOperation onWindow)
    {
        Realm.DefineValue(proto, name, Realm.NewMethod(name, (in call) =>
        {
            // A non-object receiver is neither the window nor a node, so neither is looked for. The
            // window test is handle equality, which for an object compares the kind and then the
            // reference the handle carries. The receiver and the window root come out of the same
            // provider wrapping the same engine object, so their kinds cannot differ, and this asks
            // exactly what comparing the two unwrapped references asked. (This used to say both
            // tables were keyed on the engine's own object; the node registry has keyed on the handle
            // since it was re-typed, and the unwrap that fed the window test is gone with the test.)
            if (call.This.IsObject)
            {
                if (call.This == WindowHandle)
                    return onWindow(in call);

                if (_jsObjects.TryGetNode(call.This, out var node))
                    return onNode(in call, node);
            }

            return InvokeEngineEventTargetMethod(engineMethod, name, in call);
        }, length));
    }
}

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
