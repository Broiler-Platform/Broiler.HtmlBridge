using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Boolean;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>document</c> EventTarget methods — <c>document.addEventListener</c>,
/// <c>document.removeEventListener</c>, <c>document.dispatchEvent</c> — co-located as an HtmlBridge
/// feature module (Phase 3). Each resolves the document node's per-type listener store and applies the
/// add/remove via the P3.4 <see cref="EventListenerBinding"/> operations, or runs the capture→target→
/// bubble dispatch via the bridge's shared algorithm. The document node, listener store and dispatch
/// are reached through the <see cref="IDocumentEventTargetHost"/> contract. Previously the bridge's
/// <c>JsRegistrationAddEventListener060Core</c>/<c>RemoveEventListener061Core</c>/<c>DispatchEvent062Core</c>
/// in the shared JsFunctionCallbacks/Registration.cs grab-bag. (The window and visualViewport EventTarget
/// wiring, which use different listener stores and dispatch paths, are separate concerns.)
/// </summary>
internal static class DocumentEventTargetBinding
{
    public static JSValue AddEventListener(IDocumentEventTargetHost host, in Arguments a)
    {
        if (a.Length < 2)
            return JSUndefined.Value;
        var doc = host.DocumentNode;
        var type = a[0].ToString();
        if (!host.GetEventListeners(doc).TryGetValue(type, out var listeners))
        {
            listeners = [];
            host.GetEventListeners(doc)[type] = listeners;
        }

        EventListenerBinding.AddListener(listeners, a[1], a.Length > 2 ? a[2] : JSUndefined.Value);
        return JSUndefined.Value;
    }

    public static JSValue RemoveEventListener(IDocumentEventTargetHost host, in Arguments a)
    {
        if (a.Length < 2)
            return JSUndefined.Value;
        var type = a[0].ToString();
        EventListenerBinding.RemoveListener(
            host.GetEventListeners(host.DocumentNode).TryGetValue(type, out var listeners) ? listeners : null,
            a[1], a.Length > 2 ? a[2] : JSUndefined.Value);
        return JSUndefined.Value;
    }

    public static JSValue DispatchEvent(IDocumentEventTargetHost host, in Arguments a)
    {
        if (a.Length == 0)
            return JSBoolean.True;
        if (a[0] is not JSObject evt)
            return JSBoolean.True;
        return host.DispatchEventOnElement(host.DocumentNode, evt);
    }
}
