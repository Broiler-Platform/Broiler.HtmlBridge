using Broiler.JSeal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="WindowEventTargetBinding"/> needs from the bridge: the window's
/// per-type listener store (add/lookup) and the window-scoped
/// event-dispatch algorithm.
/// </summary>
/// <remarks>
/// The contract names no engine type: a registration holds its listener as a <see cref="JsValue"/>,
/// and there is no registration member here —
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
