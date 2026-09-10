using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.Runtime;

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
// which all three share. The listener store's own element type is that same record's, and the
// engine-typed DispatchEventOnElement is what the pre-realm wrapper path in DomBridge/JsObjects.cs
// still needs; both go when the record moves.
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

    // The migrated dispatch answers the "not cancelled" boolean the DOM says dispatchEvent returns,
    // which is what the engine-typed adapter beside it re-materialises as a JSBoolean.
    JsValue Dom.Features.IEventTargetHost.DispatchEvent(DomNode element, JsValue evt)
        => JsValue.Boolean(_eventDispatch.DispatchEventOnElement(element, evt).AsBoolean);

    // THE ONE PLACE THIS CONTRACT'S ENGINE SEAM ACTUALLY IS, now that the six callers who held a
    // handle stopped asking a private adapter to convert it for them. Both ends here are genuinely
    // the engine's: the JSObject was read out of an Arguments frame, and the JSValue answers back
    // into one. The boolean is re-materialised rather than round-tripped, because a handle carries
    // no engine object for a primitive and "not cancelled" is the only thing this can answer.
    JSValue Dom.Features.IEventTargetHost.DispatchEventOnElement(DomNode element, JSObject evt)
        => _eventDispatch.DispatchEventOnElement(element, JsInterop.FromEngineObject(evt)).AsBoolean
            ? JSBoolean.True
            : JSBoolean.False;

    JsValue Dom.Features.IEventTargetHost.WindowWrapper =>
        _windowJSObject is null ? JsValue.Missing : JsInterop.FromEngineObject(_windowJSObject);

    FormControlRuntimeState Dom.Features.IEventTargetHost.FormControlStateFor(DomElement element)
        => FormControlStateFor(element);

    void Dom.Features.IEventTargetHost.UncheckRadioSiblings(DomElement scope, DomElement except, string radioName)
        => UncheckRadioSiblings(scope, except, radioName);
}
