using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IWindowEventTargetHost implementation for the WindowEventTargetBinding feature module
// (Phase 3): the bridge exposes the window's per-type listener store (from the P2.5 EventTargetRegistry)
// and the window-scoped dispatch via explicit interface members, so the module never reaches an
// arbitrary bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IWindowEventTargetHost
{
    List<EventListenerRegistration> Dom.Features.IWindowEventTargetHost.WindowListenersForAdd(string type)
        => _eventTargets.WindowListenersForAdd(type);

    bool Dom.Features.IWindowEventTargetHost.TryGetWindowListeners(string type, out List<EventListenerRegistration> listeners)
        => _eventTargets.TryGetWindowListeners(type, out listeners);

    JSValue Dom.Features.IWindowEventTargetHost.DispatchWindowEvent(JSObject evt)
        => DispatchWindowEvent(evt);
}
