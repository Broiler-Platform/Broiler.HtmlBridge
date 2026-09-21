using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>document</c> EventTarget methods — <c>document.addEventListener</c>,
/// <c>document.removeEventListener</c>, <c>document.dispatchEvent</c> — co-located as an HtmlBridge
/// feature module. Each resolves the document node's per-type listener store and applies the
/// add/remove via the <see cref="EventListenerBinding"/> operations, or runs the capture→target→
/// bubble dispatch via the bridge's shared algorithm. The document node, listener store and dispatch
/// are reached through the <see cref="IDocumentEventTargetHost"/> contract. (The window and
/// visualViewport EventTarget wiring, which use different listener stores and dispatch paths, are
/// separate concerns.)
/// </summary>
/// <remarks>
/// The call frame is JSEAL's -- <c>DomBridge/Registration/Document.cs</c> mints all three through the
/// realm -- and so is everything behind it. The add/remove semantics take the realm the frame
/// carries and a listener record that holds a <see cref="JsValue"/>. The event-type coercion is the
/// realm's <c>ToJsString</c>, the observable ECMAScript <c>ToString</c>.
/// </remarks>
internal static class DocumentEventTargetBinding
{
    // Guard before the store, as <see cref="EventTargetBinding"/> does: the document's store is
    // created on demand and a one-argument call must not file an empty one.
    public static JsValue AddEventListener(IDocumentEventTargetHost host, in JsCall call) =>
        call.Length < 2 ? JsValue.Undefined : EventListenerBinding.AddTo(host.GetEventListeners(host.DocumentNode), in call);

    public static JsValue RemoveEventListener(IDocumentEventTargetHost host, in JsCall call) =>
        call.Length < 2 ? JsValue.Undefined : EventListenerBinding.RemoveFrom(host.GetEventListeners(host.DocumentNode), in call);

    public static JsValue DispatchEvent(IDocumentEventTargetHost host, in JsCall call)
    {
        if (!call[0].IsObject)
            return JsValue.True;
        return host.DispatchEvent(host.DocumentNode, call[0]);
    }
}
