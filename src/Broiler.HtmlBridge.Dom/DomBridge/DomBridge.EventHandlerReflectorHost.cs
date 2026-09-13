using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IEventHandlerReflectorHost implementation for the EventHandlerReflectorBinding feature module
// (Phase 3): the bridge exposes the three things the reflector does to the live inline on* handler map
// via explicit interface members, so the reflector module never reaches an arbitrary bridge private
// field and the public surface is unchanged.
//
// The map holds JSEAL handles, and nothing here converts. This paragraph used to say the map held the
// engine's own value type because three unowned files decided it — the declaration in
// DomBridge/RuntimeStates.cs, the accessor in DomBridge.cs and the dispatch read in
// DomBridge.EventDispatchHost.cs — and that the two casts in this file would go "when the record
// moves". Those three, with DomBridge/Events.cs and this file, are every file that touches the map, so
// nothing outside the change was deciding anything. It also called DomBridge/Events.cs the only writer
// that went through the realm, when the setter below stored a realm-minted argument too. And the
// record it meant, EventListenerRegistration, is a different declaration that shares
// DomBridge/RuntimeStates.cs with the map; the casts did not wait for it.
public sealed partial class DomBridge : Dom.Features.IEventHandlerReflectorHost
{
    JsValue Dom.Features.IEventHandlerReflectorHost.GetInlineEventHandler(DomNode node, string eventName) =>
        // Only functions are ever put in this map. It has two writers: CompileInlineEventAttribute in
        // DomBridge/Events.cs, which stores a compiled handler only when it answered IsFunction, and the
        // setter below, whose one caller, EventHandlerReflectorBinding.SetOn, stores only a handle that
        // answered IsFunction and clears the entry otherwise. (This used to name the first writer as
        // CompileInlineEventAttributes, the loop that runs when an element is first wrapped. That is one
        // of three routes into it; the other two are the attribute-write paths in
        // Features/AttributesBinding.cs.) So the object test is what turns nothing stored into the IDL
        // attribute's null. The stored handle is returned as it is. The handle this used to mint over
        // the same object compared equal to it: the provider and the bridge's seam both give an engine
        // function kind Function, and a handle compares kind and then reference.
        GetInlineEventHandlers(node).TryGetValue(eventName, out var handler) && handler.IsObject
            ? handler
            : JsValue.Null;

    void Dom.Features.IEventHandlerReflectorHost.SetInlineEventHandler(DomNode node, string eventName, JsValue handler) =>
        GetInlineEventHandlers(node)[eventName] = handler;

    void Dom.Features.IEventHandlerReflectorHost.RemoveInlineEventHandler(DomNode node, string eventName) =>
        GetInlineEventHandlers(node).Remove(eventName);
}
