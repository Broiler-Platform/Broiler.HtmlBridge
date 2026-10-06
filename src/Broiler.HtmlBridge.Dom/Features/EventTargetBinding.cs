using System;
using Broiler.JSeal;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The feature module for the DOM <c>EventTarget</c> methods exposed on every node/element wrapper —
/// <c>addEventListener</c>, <c>removeEventListener</c>, <c>dispatchEvent</c>, and the
/// <c>click</c>, <c>focus</c> and <c>blur</c> an element has. The registration semantics
/// (option parsing, dedup, match-by-listener+capture) live in <see cref="EventListenerBinding"/> and the
/// capture→target→bubble engine in <see cref="EventDispatchBinding"/>; this module wires the JS-facing
/// methods to them, reaching the realm, the per-node listener store, the dispatch and the click
/// through <see cref="IEventTargetHost"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each <c>EventTarget</c> operation has one body, and five installers mint it through the
/// realm.</b> <c>DomBridge/Events.cs</c> routes <c>EventTarget.prototype</c>'s three by
/// receiver; <c>DomBridge/JsObjects.cs</c> once and <c>DomBridge/JsObjects.NonElementNodes.cs</c> three
/// times install per-wrapper copies. All five hand the body a JSEAL call frame, so one body serves
/// every installer.
/// </para>
/// <para>
/// <b>Registration is <see cref="EventListenerBinding"/>'s, called directly.</b> Its two operations
/// take the realm the call frame carries, which is what reading an <c>options</c> object's flags and
/// coercing anything else need; the listener record holds a handle, so nothing converts on the way
/// in. The event-type coercion is the realm's <c>ToJsString</c>, the observable ECMAScript
/// <c>ToString</c>.
/// </para>
/// <para>
/// <b>A click is the bridge's, as focus is.</b> <c>click()</c> built a plain object here, toggled a
/// checkbox without its <c>input</c> or <c>change</c>, and fired an untrusted <c>submit</c> of its own
/// for a submit button; what a click activates is decided where a user's click is
/// (<c>DomBridge/ScriptActivation.cs</c>), so the two agree. <c>DomBridge/ElementInterface.cs</c>
/// installs <c>click</c>, <c>focus</c> and <c>blur</c> with <c>DefineMethod</c> over a JSEAL call
/// frame, and their signatures below say so.
/// </para>
/// </remarks>
internal static class EventTargetBinding
{
    // ------------------------------------------------------------------
    //  EventTarget.prototype's three, routed by receiver — the JSEAL frame
    // ------------------------------------------------------------------

    // The arity guard comes first so that a one-argument call materialises no listener store: the
    // store is created on demand, and a page can reach this on every element it can name.
    public static JsValue AddEventListener(IEventTargetHost host, DomNode element, in JsCall call) =>
        call.Length < 2 ? JsValue.Undefined : EventListenerBinding.AddTo(host.GetEventListeners(element), in call);

    public static JsValue RemoveEventListener(IEventTargetHost host, DomNode element, in JsCall call) =>
        call.Length < 2 ? JsValue.Undefined : EventListenerBinding.RemoveFrom(host.GetEventListeners(element), in call);

    public static JsValue DispatchEvent(IEventTargetHost host, DomNode element, in JsCall call)
    {
        // A missing argument is not an object either, so this one test answers for an absent
        // argument and for a non-object one.
        if (!call[0].IsObject)
            return JsValue.True;
        return host.DispatchEvent(element, call[0]);
    }

    /// <remarks>
    /// The click a user's click would be, made by the bridge: untrusted, with the activation behaviour
    /// of the element it activates -- a checkbox changes, a submit button submits its form validated,
    /// a reset button resets it, a label clicks its control, a link is followed.
    /// </remarks>
    public static JsValue Click(IEventTargetHost host, DomElement element, in JsCall _)
    {
        host.Click(element);
        return JsValue.Undefined;
    }

    /// <remarks>
    /// The bridge's focus: the element is focused when it can be, with the events a browser fires for
    /// that. It used to fire an untrusted <c>focus</c> event at any element and focus nothing, so
    /// <c>document.activeElement</c> never followed.
    /// </remarks>
    public static JsValue Focus(IEventTargetHost host, DomElement element, in JsCall _)
    {
        host.FocusElement(element);
        return JsValue.Undefined;
    }

    /// <inheritdoc cref="Focus"/>
    public static JsValue Blur(IEventTargetHost host, DomElement element, in JsCall _)
    {
        host.BlurElement(element);
        return JsValue.Undefined;
    }

}
