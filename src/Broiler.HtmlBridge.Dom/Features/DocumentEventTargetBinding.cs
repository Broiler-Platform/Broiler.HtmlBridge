using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>document</c> EventTarget methods — <c>document.addEventListener</c>,
/// <c>document.removeEventListener</c>, <c>document.dispatchEvent</c> — co-located as an HtmlBridge
/// feature module (Phase 3). Each resolves the document node's per-type listener store and applies the
/// add/remove via the P3.4 <see cref="EventListenerBinding"/> operations, or runs the capture→target→
/// bubble dispatch via the bridge's shared algorithm. The document node, listener store, registration
/// operations and dispatch are reached through the <see cref="IDocumentEventTargetHost"/> contract.
/// Previously the bridge's <c>JsRegistrationAddEventListener060Core</c>/<c>RemoveEventListener061Core</c>/<c>DispatchEvent062Core</c>
/// in the shared JsFunctionCallbacks/Registration.cs grab-bag. (The window and visualViewport EventTarget
/// wiring, which use different listener stores and dispatch paths, are separate concerns.)
/// </summary>
/// <remarks>
/// The call frame is JSEAL's — <c>DomBridge/Registration/Document.cs</c> mints all three through the
/// realm — and so is everything this module itself says. What has not moved is behind the contract: a
/// registration's listener field is still a Broiler.JS value in the unowned
/// <c>DomBridge/RuntimeStates.cs</c>, and the add/remove semantics are still the engine-typed
/// <c>EventListenerBinding</c>, so the host implementation is where a handle becomes an engine value.
/// The event-type coercion below is the realm's <c>ToJsString</c>, which is the observable ECMAScript
/// <c>ToString</c> the engine's <c>a[0].ToString()</c> ran here before.
/// </remarks>
internal static class DocumentEventTargetBinding
{
    public static JsValue AddEventListener(IDocumentEventTargetHost host, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Undefined;
        var doc = host.DocumentNode;
        var type = call.Realm.ToJsString(call[0]);
        if (!host.GetEventListeners(doc).TryGetValue(type, out var listeners))
        {
            listeners = [];
            host.GetEventListeners(doc)[type] = listeners;
        }

        host.AddListener(listeners, call[1], call.Length > 2 ? call[2] : JsValue.Undefined);
        return JsValue.Undefined;
    }

    public static JsValue RemoveEventListener(IDocumentEventTargetHost host, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Undefined;
        var type = call.Realm.ToJsString(call[0]);
        host.RemoveListener(
            host.GetEventListeners(host.DocumentNode).TryGetValue(type, out var listeners) ? listeners : null,
            call[1], call.Length > 2 ? call[2] : JsValue.Undefined);
        return JsValue.Undefined;
    }

    public static JsValue DispatchEvent(IDocumentEventTargetHost host, in JsCall call)
    {
        if (!call[0].IsObject)
            return JsValue.True;
        return host.DispatchEvent(host.DocumentNode, call[0]);
    }
}
