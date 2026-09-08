using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="EventTargetBinding"/> needs from the bridge: the realm the
/// synthetic <c>click</c>/<c>focus</c>/<c>blur</c> events are built in, the per-node listener store
/// (<c>addEventListener</c>/<c>removeEventListener</c> mutate it), the propagation engine
/// (<c>dispatchEvent</c>/<c>click</c>/<c>focus</c>/<c>blur</c> all run capture→target→bubble via it), and
/// the window JS object (the synthetic <c>focus</c>/<c>blur</c> UIEvents expose it as <c>view</c>). The
/// listener-registration semantics live in <see cref="EventListenerBinding"/> and the propagation engine
/// in <see cref="EventDispatchBinding"/>; node-type/attribute/runtime-state helpers, the radio-group
/// mutual-exclusion walk and the no-op function factory are the bridge's <c>internal static</c> helpers.
/// </summary>
/// <remarks>
/// <b>Two members are still engine-typed, and both are pinned from outside this slice.</b>
/// <see cref="GetEventListeners"/> hands back <c>EventListenerRegistration</c>s, whose listener field
/// is a Broiler.JS value because the record lives in the unowned <c>DomBridge/RuntimeStates.cs</c> and
/// is shared with the window, form-submit and messaging paths. <see cref="DispatchEventOnElement"/>
/// keeps its engine-typed event because <c>EventTargetBinding.DispatchEvent</c> receives the page's
/// event object from an unmigrated call frame and has nowhere to convert it to; the synthetic events
/// this module builds itself go through <see cref="Realm"/> and are cast at the seam instead.
/// </remarks>
internal interface IEventTargetHost
{
    /// <summary>The realm the synthetic <c>click</c>/<c>submit</c>/<c>focus</c>/<c>blur</c> event
    /// objects are built in.</summary>
    IJsRealm Realm { get; }

    Dictionary<string, List<EventListenerRegistration>> GetEventListeners(DomNode element);
    JSValue DispatchEventOnElement(DomNode element, JSObject evt);

    /// <summary>The JS <c>window</c> wrapper the synthetic focus/blur UIEvents expose as <c>view</c>;
    /// not an object before the window global is installed.</summary>
    JsValue WindowWrapper { get; }

    // Form-control state moved onto the host (Phase 2 item 4 de-globalization): the click checkbox/radio
    // toggle reads and writes the per-bridge FormControl runtime state (checkedness) and drives the
    // radio-group mutual-exclusion walk, all now bridge-instance rather than process-static.
    FormControlRuntimeState FormControlStateFor(DomElement element);
    void UncheckRadioSiblings(DomElement scope, DomElement except, string radioName);
}
