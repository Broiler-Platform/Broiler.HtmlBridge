using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IDocumentEventTargetHost implementation for the DocumentEventTargetBinding feature module
// (Phase 3): the bridge exposes the document node, its per-type listener store and the shared
// event-dispatch algorithm via explicit interface members, so the module never reaches an arbitrary
// bridge private field and the public surface is unchanged.
//
// Two registration members used to sit here and they were the seam: EventListenerRegistration held
// its listener as an engine value, so a handle became one here, through ToEngineListenerValue in
// DomBridge.WindowEventTargetHost.cs. The record holds a handle now; the members and the converter are
// deleted, and DocumentEventTargetBinding calls Features/EventListenerBinding.cs itself.
public sealed partial class DomBridge : Dom.Features.IDocumentEventTargetHost
{
    DomNode Dom.Features.IDocumentEventTargetHost.DocumentNode => _document;

    Dictionary<string, List<EventListenerRegistration>> Dom.Features.IDocumentEventTargetHost.GetEventListeners(DomNode node)
        => GetEventListeners(node);

    // The migrated dispatch answers the "not cancelled" boolean the DOM says dispatchEvent returns,
    // which the engine-typed adapter that stood beside it re-materialised as a JSBoolean.
    JsValue Dom.Features.IDocumentEventTargetHost.DispatchEvent(DomNode target, JsValue evt)
        => JsValue.Boolean(_eventDispatch.DispatchEventOnElement(target, evt).AsBoolean);
}
