using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IEventTargetHost implementation for the EventTargetBinding feature module (Phase 3): the
// bridge exposes the per-node listener store, the propagation engine (the thin DispatchEventOnElement
// delegator over EventDispatchBinding) and the window JS object via explicit interface members, so the
// module reaches no arbitrary bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IEventTargetHost
{
    Dictionary<string, List<EventListenerRegistration>> Dom.Features.IEventTargetHost.GetEventListeners(DomNode element)
        => GetEventListeners(element);

    JSValue Dom.Features.IEventTargetHost.DispatchEventOnElement(DomNode element, JSObject evt)
        => DispatchEventOnElement(element, evt);

    JSObject? Dom.Features.IEventTargetHost.WindowJSObject => _windowJSObject;

    FormControlRuntimeState Dom.Features.IEventTargetHost.FormControlStateFor(DomElement element)
        => FormControlStateFor(element);

    void Dom.Features.IEventTargetHost.UncheckRadioSiblings(DomElement scope, DomElement except, string radioName)
        => UncheckRadioSiblings(scope, except, radioName);
}
