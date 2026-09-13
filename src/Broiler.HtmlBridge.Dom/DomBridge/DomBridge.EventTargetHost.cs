using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IEventTargetHost implementation for the EventTargetBinding feature module (Phase 3): the
// bridge exposes the realm, the per-node listener store, the registration operations over it, the
// propagation engine and the window JS object via explicit interface members, so the module reaches no
// arbitrary bridge private field and the public surface is unchanged.
//
// The registration pair is the seam, and it is shared with the document and window contracts: a
// registration's listener field is a Broiler.JS value in the unowned DomBridge/RuntimeStates.cs, and
// Features/EventListenerBinding.cs is written against that, so a handle becomes an engine value here
// rather than in the module — through ToEngineListenerValue in DomBridge.WindowEventTargetHost.cs,
// which all three share. The listener store's own element type is that same record's, and that is the
// whole of what is left here.
//
// An engine-typed DispatchEventOnElement sat beside the migrated one until this file said it was
// what "the pre-realm wrapper path in DomBridge/JsObjects.cs still needs". That path does not name
// this contract at all, and the member had no caller anywhere; it is deleted rather than ported.
public sealed partial class DomBridge : Dom.Features.IEventTargetHost
{
    IJsRealm Dom.Features.IEventTargetHost.Realm => Realm;

    Dictionary<string, List<EventListenerRegistration>> Dom.Features.IEventTargetHost.GetEventListeners(DomNode element)
        => GetEventListeners(element);

    void Dom.Features.IEventTargetHost.AddListener(
        List<EventListenerRegistration> listeners, JsValue listener, JsValue options)
        => Dom.Features.EventListenerBinding.AddListener(
            listeners, ToEngineListenerValue(listener), ToEngineListenerValue(options));

    void Dom.Features.IEventTargetHost.RemoveListener(
        List<EventListenerRegistration>? listeners, JsValue listener, JsValue options)
        => Dom.Features.EventListenerBinding.RemoveListener(
            listeners, ToEngineListenerValue(listener), ToEngineListenerValue(options));

    // Answers the "not cancelled" boolean the DOM says dispatchEvent returns.
    JsValue Dom.Features.IEventTargetHost.DispatchEvent(DomNode element, JsValue evt)
        => JsValue.Boolean(_eventDispatch.DispatchEventOnElement(element, evt).AsBoolean);

    JsValue Dom.Features.IEventTargetHost.WindowWrapper =>
        _windowJSObject is null ? JsValue.Missing : JsInterop.FromEngineObject(_windowJSObject);

    FormControlRuntimeState Dom.Features.IEventTargetHost.FormControlStateFor(DomElement element)
        => FormControlStateFor(element);

    void Dom.Features.IEventTargetHost.UncheckRadioSiblings(DomElement scope, DomElement except, string radioName)
        => UncheckRadioSiblings(scope, except, radioName);
}
