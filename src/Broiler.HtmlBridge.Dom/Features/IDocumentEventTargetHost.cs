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
/// <remarks>
/// Still engine-typed, and pinned from outside this slice: the listener store's
/// <c>EventListenerRegistration</c> record lives in the unowned <c>DomBridge/RuntimeStates.cs</c>,
/// and <c>dispatchEvent</c> is handed the page's own event object by an unmigrated call frame in
/// <c>DomBridge/Registration/Document.cs</c>. Both move together; neither moves for this slice
/// alone. See <see cref="DocumentEventTargetBinding"/>.
/// </remarks>
internal interface IDocumentEventTargetHost
{
    DomNode DocumentNode { get; }
    Dictionary<string, List<EventListenerRegistration>> GetEventListeners(DomNode node);
    JSValue DispatchEventOnElement(DomNode target, JSObject evt);
}
