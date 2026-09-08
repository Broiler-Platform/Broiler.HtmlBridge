using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="WindowEventTargetBinding"/> needs from the bridge: the window's
/// per-type listener store (add/lookup) and the window-scoped event-dispatch algorithm. Listener
/// add/remove semantics themselves are the P3.4 <c>EventListenerBinding</c> module, called directly.
/// </summary>
/// <remarks>
/// Still engine-typed, and pinned from outside this slice: the listener store's
/// <c>EventListenerRegistration</c> record lives in the unowned <c>DomBridge/RuntimeStates.cs</c>,
/// the window listener lists are shared with the messaging and sub-window modules through
/// <c>EventTargetRegistry</c>, and <c>DispatchWindowEvent</c> is implemented in
/// <c>DomBridge.WindowLoad.cs</c>. See <see cref="WindowEventTargetBinding"/>.
/// </remarks>
internal interface IWindowEventTargetHost
{
    List<EventListenerRegistration> WindowListenersForAdd(string type);
    bool TryGetWindowListeners(string type, out List<EventListenerRegistration> listeners);
    JSValue DispatchWindowEvent(JSObject evt);
}
