using Broiler.Dom;
using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="DocumentEventTargetBinding"/> needs from the bridge: the
/// document node (the EventTarget), its per-type listener store, and the shared event-dispatch
/// algorithm. Listener add/remove semantics themselves are the P3.4 <c>EventListenerBinding</c> module,
/// called directly.
/// </summary>
internal interface IDocumentEventTargetHost
{
    DomNode DocumentNode { get; }
    Dictionary<string, List<EventListenerRegistration>> GetEventListeners(DomNode node);
    JSValue DispatchEventOnElement(DomNode target, JSObject evt);
}
