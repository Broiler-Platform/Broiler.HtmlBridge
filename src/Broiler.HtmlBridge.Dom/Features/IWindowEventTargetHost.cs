using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="WindowEventTargetBinding"/> needs from the bridge: the window's
/// per-type listener store (add/lookup), the registration operations over it, and the window-scoped
/// event-dispatch algorithm.
/// </summary>
/// <remarks>
/// The contract names no engine type, and the two registration members are why: a registration is an
/// <c>EventListenerRegistration</c> whose listener field is a Broiler.JS value
/// (<c>DomBridge/RuntimeStates.cs</c>), and the semantics live in the engine-typed
/// <c>EventListenerBinding</c> — neither this slice's — so the handle becomes an engine value in the
/// implementation rather than in the module. The window listener lists are shared with the messaging
/// and sub-window modules through <c>EventTargetRegistry</c>, and <c>DispatchWindowEvent</c> is
/// implemented in <c>DomBridge.WindowLoad.cs</c>. See <see cref="WindowEventTargetBinding"/>.
/// </remarks>
internal interface IWindowEventTargetHost
{
    List<EventListenerRegistration> WindowListenersForAdd(string type);
    bool TryGetWindowListeners(string type, out List<EventListenerRegistration> listeners);

    /// <inheritdoc cref="EventListenerBinding.AddListener" />
    void AddListener(List<EventListenerRegistration> listeners, JsValue listener, JsValue options);

    /// <inheritdoc cref="EventListenerBinding.RemoveListener" />
    void RemoveListener(List<EventListenerRegistration>? listeners, JsValue listener, JsValue options);

    JsValue DispatchWindowEvent(JsValue evt);
}
