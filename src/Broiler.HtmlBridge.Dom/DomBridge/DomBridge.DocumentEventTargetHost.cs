using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IDocumentEventTargetHost implementation for the DocumentEventTargetBinding feature module
// (Phase 3): the bridge exposes the document node, its per-type listener store, the registration
// operations over it and the shared event-dispatch algorithm via explicit interface members, so the
// module never reaches an arbitrary bridge private field and the public surface is unchanged.
//
// The two registration members are the seam. EventListenerRegistration holds its listener as a
// Broiler.JS value and Features/EventListenerBinding.cs is written against that, so a handle becomes
// an engine value here rather than in the module — see ToEngineListenerValue in
// DomBridge.WindowEventTargetHost.cs, which the window contract shares.
public sealed partial class DomBridge : Dom.Features.IDocumentEventTargetHost
{
    DomNode Dom.Features.IDocumentEventTargetHost.DocumentNode => _document;

    Dictionary<string, List<EventListenerRegistration>> Dom.Features.IDocumentEventTargetHost.GetEventListeners(DomNode node)
        => GetEventListeners(node);

    void Dom.Features.IDocumentEventTargetHost.AddListener(
        List<EventListenerRegistration> listeners, JsValue listener, JsValue options)
        => Dom.Features.EventListenerBinding.AddListener(
            listeners, ToEngineListenerValue(listener), ToEngineListenerValue(options));

    void Dom.Features.IDocumentEventTargetHost.RemoveListener(
        List<EventListenerRegistration>? listeners, JsValue listener, JsValue options)
        => Dom.Features.EventListenerBinding.RemoveListener(
            listeners, ToEngineListenerValue(listener), ToEngineListenerValue(options));

    // The migrated dispatch answers the "not cancelled" boolean the DOM says dispatchEvent returns,
    // which is what the engine-typed adapter beside it re-materialised as a JSBoolean.
    JsValue Dom.Features.IDocumentEventTargetHost.DispatchEvent(DomNode target, JsValue evt)
        => JsValue.Boolean(_eventDispatch.DispatchEventOnElement(target, evt).AsBoolean);
}
