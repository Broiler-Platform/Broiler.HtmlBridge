using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="WindowEventTargetBinding"/> needs from the bridge: the window's
/// per-type listener store (add/lookup) and the window-scoped event-dispatch algorithm. Listener
/// add/remove semantics themselves are the P3.4 <c>EventListenerBinding</c> module, called directly.
/// </summary>
internal interface IWindowEventTargetHost
{
    List<EventListenerRegistration> WindowListenersForAdd(string type);
    bool TryGetWindowListeners(string type, out List<EventListenerRegistration> listeners);
    JSValue DispatchWindowEvent(JSObject evt);
}
