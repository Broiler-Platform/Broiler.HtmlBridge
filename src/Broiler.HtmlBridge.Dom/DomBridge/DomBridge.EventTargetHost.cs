using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IEventTargetHost implementation for the EventTargetBinding feature module (Phase 3): the
// bridge exposes the realm, the per-node listener store, the propagation engine (the thin
// DispatchEventOnElement delegator over EventDispatchBinding) and the window JS object via explicit
// interface members, so the module reaches no arbitrary bridge private field and the public surface
// is unchanged.
//
// Two members stay engine-typed and both are pinned from outside this slice: the listener
// registrations (their record lives in the unowned DomBridge/RuntimeStates.cs and is shared with the
// window, form-submit and messaging paths) and the dispatch entry point (dispatchEvent receives the
// page's own event object from an unmigrated call frame). See IEventTargetHost.
public sealed partial class DomBridge : Dom.Features.IEventTargetHost
{
    IJsRealm Dom.Features.IEventTargetHost.Realm => Realm;

    Dictionary<string, List<EventListenerRegistration>> Dom.Features.IEventTargetHost.GetEventListeners(DomNode element)
        => GetEventListeners(element);

    JSValue Dom.Features.IEventTargetHost.DispatchEventOnElement(DomNode element, JSObject evt)
        => DispatchEventOnElement(element, evt);

    JsValue Dom.Features.IEventTargetHost.WindowWrapper =>
        _windowJSObject is null ? JsValue.Missing : JsInterop.FromEngineObject(_windowJSObject);

    FormControlRuntimeState Dom.Features.IEventTargetHost.FormControlStateFor(DomElement element)
        => FormControlStateFor(element);

    void Dom.Features.IEventTargetHost.UncheckRadioSiblings(DomElement scope, DomElement except, string radioName)
        => UncheckRadioSiblings(scope, except, radioName);
}
