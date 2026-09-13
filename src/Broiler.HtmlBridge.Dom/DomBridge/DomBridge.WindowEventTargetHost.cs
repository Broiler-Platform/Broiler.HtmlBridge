using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IWindowEventTargetHost implementation for the WindowEventTargetBinding feature module
// (Phase 3): the bridge exposes the window's per-type listener store (from the P2.5 EventTargetRegistry)
// and the window-scoped dispatch via explicit interface members, so the module never reaches an
// arbitrary bridge private field and the public surface is unchanged.
//
// This file used to carry ToEngineListenerValue, the one place a JSEAL handle became the engine value
// the listener record held: an unwrap for an object, and a freshly minted engine primitive for
// anything else. The element, document and window contracts' registration members all called it. The
// record holds a handle now, so the converter went with those members rather than moving, and
// WindowEventTargetBinding calls Features/EventListenerBinding.cs itself with its call frame's realm.
public sealed partial class DomBridge : Dom.Features.IWindowEventTargetHost
{
    List<EventListenerRegistration> Dom.Features.IWindowEventTargetHost.WindowListenersForAdd(string type)
        => _eventTargets.WindowListenersForAdd(type);

    bool Dom.Features.IWindowEventTargetHost.TryGetWindowListeners(string type, out List<EventListenerRegistration> listeners)
        => _eventTargets.TryGetWindowListeners(type, out listeners);

    JsValue Dom.Features.IWindowEventTargetHost.DispatchWindowEvent(JsValue evt)
        => JsValue.Boolean(DispatchWindowEvent(evt));
}
