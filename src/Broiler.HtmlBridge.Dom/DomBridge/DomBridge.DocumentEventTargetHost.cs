using Broiler.Dom;
using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IDocumentEventTargetHost implementation for the DocumentEventTargetBinding feature module
// (Phase 3): the bridge exposes the document node, its per-type listener store and the shared
// event-dispatch algorithm via explicit interface members, so the module never reaches an arbitrary
// bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IDocumentEventTargetHost
{
    DomNode Dom.Features.IDocumentEventTargetHost.DocumentNode => _document;

    Dictionary<string, List<EventListenerRegistration>> Dom.Features.IDocumentEventTargetHost.GetEventListeners(DomNode node)
        => GetEventListeners(node);

    JSValue Dom.Features.IDocumentEventTargetHost.DispatchEventOnElement(DomNode target, JSObject evt)
        => DispatchEventOnElement(target, evt);
}
