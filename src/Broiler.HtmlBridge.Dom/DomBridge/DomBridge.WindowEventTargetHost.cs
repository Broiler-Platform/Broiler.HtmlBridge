using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IWindowEventTargetHost implementation for the WindowEventTargetBinding feature module
// (Phase 3): the bridge exposes the window's per-type listener store (from the P2.5 EventTargetRegistry),
// the registration operations over it and the window-scoped dispatch via explicit interface members, so
// the module never reaches an arbitrary bridge private field and the public surface is unchanged.
//
// This file carries the one place a JSEAL handle becomes the engine value the listener store still
// holds. Both EventTarget contracts share it, because both feed the same
// Features/EventListenerBinding.cs over the same EventListenerRegistration record.
public sealed partial class DomBridge : Dom.Features.IWindowEventTargetHost
{
    List<EventListenerRegistration> Dom.Features.IWindowEventTargetHost.WindowListenersForAdd(string type)
        => _eventTargets.WindowListenersForAdd(type);

    bool Dom.Features.IWindowEventTargetHost.TryGetWindowListeners(string type, out List<EventListenerRegistration> listeners)
        => _eventTargets.TryGetWindowListeners(type, out listeners);

    void Dom.Features.IWindowEventTargetHost.AddListener(
        List<EventListenerRegistration> listeners, JsValue listener, JsValue options)
        => Dom.Features.EventListenerBinding.AddListener(
            listeners, ToEngineListenerValue(listener), ToEngineListenerValue(options));

    void Dom.Features.IWindowEventTargetHost.RemoveListener(
        List<EventListenerRegistration>? listeners, JsValue listener, JsValue options)
        => Dom.Features.EventListenerBinding.RemoveListener(
            listeners, ToEngineListenerValue(listener), ToEngineListenerValue(options));

    JsValue Dom.Features.IWindowEventTargetHost.DispatchWindowEvent(JsValue evt)
        => JsValue.Boolean(DispatchWindowEvent(Dom.Runtime.JsInterop.ToEngineObject(evt)).BooleanValue);

    /// <summary>
    /// A handle as the engine value <c>EventListenerRegistration</c> holds — the listener a page
    /// registered, or the <c>options</c> argument beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An object handle carries the engine's own object, so this is a cast for every listener a page
    /// can usefully register: the instance stored by <c>addEventListener</c> is the instance
    /// <c>removeEventListener</c> compares against, and the reference equality the registration store
    /// is built on is untouched.
    /// </para>
    /// <para>
    /// A primitive has no engine value to unwrap and is re-materialised, which is what
    /// <c>options</c> needs — <c>addEventListener(t, f, true)</c> passes a boolean, and reading it as
    /// anything else would silently lose the capture flag. The same materialisation applied to a
    /// <em>listener</em> that is a primitive produces a fresh engine value each time, so
    /// <c>removeEventListener(t, "handler")</c> would not find a registration
    /// <c>addEventListener(t, "handler")</c> made. That is a shape WebIDL does not admit at all
    /// (<c>EventListener?</c> is an object or null) and one nothing in the bridge dispatches, so it
    /// stays reported rather than papered over; it goes when the record moves to
    /// <see cref="JsValue"/>, which compares primitives by value.
    /// </para>
    /// </remarks>
    private static JavaScript.Runtime.JSValue ToEngineListenerValue(JsValue value) =>
        Dom.Runtime.JsInterop.ToEngineValue(value) ?? value.Kind switch
        {
            JsValueKind.Null => JavaScript.BuiltIns.Null.JSNull.Value,
            JsValueKind.Boolean => value.AsBoolean
                ? JavaScript.BuiltIns.Boolean.JSBoolean.True
                : JavaScript.BuiltIns.Boolean.JSBoolean.False,
            JsValueKind.Number => new JavaScript.BuiltIns.Number.JSNumber(value.AsNumber),
            JsValueKind.String => new JavaScript.BuiltIns.String.JSString(value.AsString!),

            // Missing and Undefined alike: an argument the page did not pass is `undefined` to
            // everything downstream, which is what the engine frame handed over for the third slot.
            _ => JavaScript.Runtime.JSUndefined.Value,
        };
}
