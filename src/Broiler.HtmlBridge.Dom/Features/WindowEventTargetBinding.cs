using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>window</c> EventTarget methods — <c>window.addEventListener</c>,
/// <c>window.removeEventListener</c>, <c>window.dispatchEvent</c> — co-located as an HtmlBridge feature
/// module, the symmetric counterpart to <see cref="DocumentEventTargetBinding"/>. Each
/// resolves the window's per-type listener store and applies the add/remove via the
/// <see cref="EventListenerBinding"/> operations, or runs the window-scoped dispatch. The listener
/// store and dispatch are reached through the
/// <see cref="IWindowEventTargetHost"/> contract.
/// </summary>
/// <remarks>
/// The call frame is JSEAL's -- <c>DomBridge/Registration/Window.cs</c> and
/// <c>DomBridge/Events.cs</c> both mint these through the realm -- and nothing on the
/// registration side sits behind the contract: the listener record holds a
/// <see cref="JsValue"/>, and <see cref="EventListenerBinding"/> is called directly with the realm the
/// frame carries. The event-type coercion is the realm's <c>ToJsString</c>, the observable
/// ECMAScript <c>ToString</c>.
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
