using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Boolean;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>window</c> EventTarget methods — <c>window.addEventListener</c>,
/// <c>window.removeEventListener</c>, <c>window.dispatchEvent</c> — co-located as an HtmlBridge feature
/// module (Phase 3), the symmetric counterpart to <see cref="DocumentEventTargetBinding"/>. Each
/// resolves the window's per-type listener store and applies the add/remove via the P3.4
/// <see cref="EventListenerBinding"/> operations, or runs the window-scoped dispatch. The listener
/// store and dispatch are reached through the <see cref="IWindowEventTargetHost"/> contract.
/// Previously the bridge's <c>JsRegistrationAddEventListener136Core</c>/<c>RemoveEventListener137Core</c>/
/// <c>DispatchEvent138Core</c> in the shared JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
internal static class WindowEventTargetBinding
{
    public static JSValue AddEventListener(IWindowEventTargetHost host, in Arguments a)
    {
        if (a.Length < 2)
            return JSUndefined.Value;
        var type = a[0].ToString();
        EventListenerBinding.AddListener(
            host.WindowListenersForAdd(type), a[1], a.Length > 2 ? a[2] : JSUndefined.Value);
        return JSUndefined.Value;
    }

    public static JSValue RemoveEventListener(IWindowEventTargetHost host, in Arguments a)
    {
        if (a.Length < 2)
            return JSUndefined.Value;
        var type = a[0].ToString();
        EventListenerBinding.RemoveListener(
            host.TryGetWindowListeners(type, out var listeners) ? listeners : null,
            a[1], a.Length > 2 ? a[2] : JSUndefined.Value);
        return JSUndefined.Value;
    }

    public static JSValue DispatchEvent(IWindowEventTargetHost host, in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject evt)
            return JSBoolean.True;
        return host.DispatchWindowEvent(evt);
    }
}
