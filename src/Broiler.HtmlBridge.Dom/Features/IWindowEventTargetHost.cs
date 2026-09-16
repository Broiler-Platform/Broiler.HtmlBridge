using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="WindowEventTargetBinding"/> needs from the bridge: the window's
/// per-type listener store (add/lookup) and the window-scoped
/// event-dispatch algorithm.
/// </summary>
/// <remarks>
/// The contract names no engine type. It used to say the two registration members it carried were why:
/// a registration held its listener as an engine value, and those members were where the implementation
/// made one. The record holds a <see cref="JsValue"/> now, so they are deleted and
/// <see cref="WindowEventTargetBinding"/> calls <see cref="EventListenerBinding"/> itself. The window
/// listener lists live in <c>EventTargetRegistry</c>, and <c>DispatchWindowEvent</c> is implemented in
/// <c>DomBridge/Lifecycle.cs</c>.
/// </remarks>
internal interface IWindowEventTargetHost
{
    List<EventListenerRegistration> WindowListenersForAdd(string type);
    bool TryGetWindowListeners(string type, out List<EventListenerRegistration> listeners);

    JsValue DispatchWindowEvent(JsValue evt);
}
