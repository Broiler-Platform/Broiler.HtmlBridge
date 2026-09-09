using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>window</c> EventTarget methods — <c>window.addEventListener</c>,
/// <c>window.removeEventListener</c>, <c>window.dispatchEvent</c> — co-located as an HtmlBridge feature
/// module (Phase 3), the symmetric counterpart to <see cref="DocumentEventTargetBinding"/>. Each
/// resolves the window's per-type listener store and applies the add/remove via the P3.4
/// <see cref="EventListenerBinding"/> operations, or runs the window-scoped dispatch. The listener
/// store, registration operations and dispatch are reached through the
/// <see cref="IWindowEventTargetHost"/> contract. Previously the bridge's
/// <c>JsRegistrationAddEventListener136Core</c>/<c>RemoveEventListener137Core</c>/
/// <c>DispatchEvent138Core</c> in the shared JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
/// <remarks>
/// The call frame is JSEAL's — <c>DomBridge/Registration/Window.cs</c> mints all three through the
/// realm — exactly as <see cref="DocumentEventTargetBinding"/>'s is, and what has not moved is behind
/// the contract for the same reason: the <c>EventListenerRegistration</c> record and the registration
/// semantics are engine-typed in files this round does not own, so the host implementation is where a
/// handle becomes an engine value. The event-type coercion is the realm's <c>ToJsString</c>, the same
/// observable ECMAScript <c>ToString</c> as before.
/// </remarks>
internal static class WindowEventTargetBinding
{
    public static JsValue AddEventListener(IWindowEventTargetHost host, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Undefined;
        var type = call.Realm.ToJsString(call[0]);
        host.AddListener(
            host.WindowListenersForAdd(type), call[1], call.Length > 2 ? call[2] : JsValue.Undefined);
        return JsValue.Undefined;
    }

    public static JsValue RemoveEventListener(IWindowEventTargetHost host, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Undefined;
        var type = call.Realm.ToJsString(call[0]);
        host.RemoveListener(
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
