using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>window</c> EventTarget methods — <c>window.addEventListener</c>,
/// <c>window.removeEventListener</c>, <c>window.dispatchEvent</c> — co-located as an HtmlBridge feature
/// module (Phase 3), the symmetric counterpart to <see cref="DocumentEventTargetBinding"/>. Each
/// resolves the window's per-type listener store and applies the add/remove via the P3.4
/// <see cref="EventListenerBinding"/> operations, or runs the window-scoped dispatch. The listener
/// store and dispatch are reached through the
/// <see cref="IWindowEventTargetHost"/> contract. Previously the bridge's
/// <c>JsRegistrationAddEventListener136Core</c>/<c>RemoveEventListener137Core</c>/
/// <c>DispatchEvent138Core</c> in the shared JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
/// <remarks>
/// The call frame is JSEAL's -- <c>DomBridge/Registration/Window.cs</c> and
/// <c>DomBridge/EventTargetInterface.cs</c> both mint these through the realm -- and nothing on the
/// registration side is behind the contract any more: the listener record holds a
/// <see cref="JsValue"/>, and <see cref="EventListenerBinding"/> is called directly with the realm the
/// frame carries. The event-type coercion is the realm's <c>ToJsString</c>, the same observable
/// ECMAScript <c>ToString</c> as before.
/// </remarks>
internal static class WindowEventTargetBinding
{
    public static JsValue AddEventListener(IWindowEventTargetHost host, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Undefined;
        var type = call.Realm.ToJsString(call[0]);
        EventListenerBinding.AddListener(
            call.Realm, host.WindowListenersForAdd(type), call[1], call.Length > 2 ? call[2] : JsValue.Undefined);
        return JsValue.Undefined;
    }

    public static JsValue RemoveEventListener(IWindowEventTargetHost host, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Undefined;
        var type = call.Realm.ToJsString(call[0]);
        EventListenerBinding.RemoveListener(
            call.Realm,
            host.TryGetWindowListeners(type, out var listeners) ? listeners : null,
            call[1], call.Length > 2 ? call[2] : JsValue.Undefined);
        return JsValue.Undefined;
    }

    public static JsValue DispatchEvent(IWindowEventTargetHost host, in JsCall call)
    {
        if (!call[0].IsObject)
            return JsValue.True;
        return host.DispatchWindowEvent(call[0]);
    }
}
