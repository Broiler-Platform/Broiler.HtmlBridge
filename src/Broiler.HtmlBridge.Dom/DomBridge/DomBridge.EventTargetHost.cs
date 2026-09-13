using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IEventTargetHost implementation for the EventTargetBinding feature module (Phase 3): the
// bridge exposes the realm, the per-node listener store, the propagation engine and the window JS
// object via explicit interface members, so the module reaches no arbitrary bridge private field and
// the public surface is unchanged.
//
// A registration pair used to sit here too, and it was the seam the document and window contracts
// shared: a registration's listener field was an engine value, so a handle became one here, through
// ToEngineListenerValue in DomBridge.WindowEventTargetHost.cs. The record holds a handle now, so the
// pair and the converter are deleted and EventTargetBinding calls Features/EventListenerBinding.cs
// with its call frame's realm. The store GetEventListeners hands out has that record as its element
// type, and the record no longer holds an engine value.
//
// An engine-typed DispatchEventOnElement sat beside the migrated one until this file said it was
// what "the pre-realm wrapper path in DomBridge/JsObjects.cs still needs". That path does not name
// this contract at all, and the member had no caller anywhere; it is deleted rather than ported.
public sealed partial class DomBridge : Dom.Features.IEventTargetHost
{
    IJsRealm Dom.Features.IEventTargetHost.Realm => Realm;

    Dictionary<string, List<EventListenerRegistration>> Dom.Features.IEventTargetHost.GetEventListeners(DomNode element)
        => GetEventListeners(element);

    // Answers the "not cancelled" boolean the DOM says dispatchEvent returns.
    JsValue Dom.Features.IEventTargetHost.DispatchEvent(DomNode element, JsValue evt)
        => JsValue.Boolean(_eventDispatch.DispatchEventOnElement(element, evt).AsBoolean);

    JsValue Dom.Features.IEventTargetHost.WindowWrapper => WindowHandle;

    FormControlRuntimeState Dom.Features.IEventTargetHost.FormControlStateFor(DomElement element)
        => FormControlStateFor(element);

    void Dom.Features.IEventTargetHost.UncheckRadioSiblings(DomElement scope, DomElement except, string radioName)
        => UncheckRadioSiblings(scope, except, radioName);
}
