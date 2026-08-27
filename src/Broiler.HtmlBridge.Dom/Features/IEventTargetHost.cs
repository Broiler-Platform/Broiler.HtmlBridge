using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="EventTargetBinding"/> needs from the bridge: the per-node listener
/// store (<c>addEventListener</c>/<c>removeEventListener</c> mutate it), the propagation engine
/// (<c>dispatchEvent</c>/<c>click</c>/<c>focus</c>/<c>blur</c> all run capture→target→bubble via it), and
/// the window JS object (the synthetic <c>focus</c>/<c>blur</c> UIEvents expose it as <c>view</c>). The
/// listener-registration semantics live in <see cref="EventListenerBinding"/> and the propagation engine
/// in <see cref="EventDispatchBinding"/>; node-type/attribute/runtime-state helpers, the radio-group
/// mutual-exclusion walk and the no-op function factory are the bridge's <c>internal static</c> helpers.
/// </summary>
internal interface IEventTargetHost
{
    Dictionary<string, List<EventListenerRegistration>> GetEventListeners(DomNode element);
    JSValue DispatchEventOnElement(DomNode element, JSObject evt);
    JSObject? WindowJSObject { get; }
    // Form-control state moved onto the host (Phase 2 item 4 de-globalization): the click checkbox/radio
    // toggle reads and writes the per-bridge FormControl runtime state (checkedness) and drives the
    // radio-group mutual-exclusion walk, all now bridge-instance rather than process-static.
    FormControlRuntimeState FormControlStateFor(DomElement element);
    void UncheckRadioSiblings(DomElement scope, DomElement except, string radioName);
}
