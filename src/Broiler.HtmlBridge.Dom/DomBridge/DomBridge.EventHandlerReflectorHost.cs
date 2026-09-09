using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IEventHandlerReflectorHost implementation for the EventHandlerReflectorBinding feature module
// (Phase 3): the bridge exposes the three things the reflector does to the live inline on* handler map
// via explicit interface members, so the reflector module never reaches an arbitrary bridge private
// field and the public surface is unchanged.
//
// The map itself still holds the engine's own value type, and three unowned files decide that: it is
// declared on InlineStyleRuntimeState in DomBridge/RuntimeStates.cs, handed out by
// GetInlineEventHandlers in DomBridge.cs, and read back in DomBridge.EventDispatchHost.cs, which
// tests each entry for the engine's function type before firing it. DomBridge/Events.cs (this group)
// is now the only writer that goes through the realm; it compiles the on* attribute with
// EvaluateHostScript and unwraps once at the store. So this file stays the seam where a JSEAL handle
// becomes the engine value the map holds and back again — the same job Runtime/JsInterop.cs does
// everywhere else in the half-migrated bridge — and the two casts here go when the record moves.
public sealed partial class DomBridge : Dom.Features.IEventHandlerReflectorHost
{
    JsValue Dom.Features.IEventHandlerReflectorHost.GetInlineEventHandler(DomNode node, string eventName) =>
        // Only functions are ever put in this map — by the setter below, and by
        // CompileInlineEventAttributes, which stores what it compiled or nothing — so the object test
        // is the whole of what can be there. A handle over a function is what the getter returned
        // before, and it is the same instance.
        GetInlineEventHandlers(node).TryGetValue(eventName, out var handler) && handler is JSObject stored
            ? Dom.Runtime.JsInterop.FromEngineObject(stored)
            : JsValue.Null;

    void Dom.Features.IEventHandlerReflectorHost.SetInlineEventHandler(DomNode node, string eventName, JsValue handler) =>
        GetInlineEventHandlers(node)[eventName] = Dom.Runtime.JsInterop.ToEngineObject(handler);

    void Dom.Features.IEventHandlerReflectorHost.RemoveInlineEventHandler(DomNode node, string eventName) =>
        GetInlineEventHandlers(node).Remove(eventName);
}
