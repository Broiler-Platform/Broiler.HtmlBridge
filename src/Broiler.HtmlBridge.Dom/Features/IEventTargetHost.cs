using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="EventTargetBinding"/> needs from the bridge: the realm the
/// synthetic <c>click</c>/<c>focus</c>/<c>blur</c> events are built in, the per-node listener store
/// (<c>addEventListener</c>/<c>removeEventListener</c> mutate it),
/// the propagation engine (<c>dispatchEvent</c>/<c>click</c>/<c>focus</c>/<c>blur</c> all run
/// capture→target→bubble via it), and the window JS object (the synthetic <c>focus</c>/<c>blur</c>
/// UIEvents expose it as <c>view</c>). The listener-registration semantics live in
/// <see cref="EventListenerBinding"/> and the propagation engine in <see cref="EventDispatchBinding"/>;
/// node-type/attribute/runtime-state helpers, the radio-group mutual-exclusion walk and the no-op
/// function factory are the bridge's <c>internal static</c> helpers.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no registration pair on this contract any more, and it was the seam.</b> Two members
/// here took a listener and an <c>options</c> argument as handles and converted both into the engine
/// values an <c>EventListenerRegistration</c> held, through a converter the document and window
/// contracts shared and the messaging contract reached by forwarding to this one. The record holds a
/// <see cref="JsValue"/> now, so <see cref="EventTargetBinding"/> calls <see cref="EventListenerBinding"/>
/// itself with the realm its call frame carries, and the pair is deleted rather than left forwarding.
/// </para>
/// <para>
/// <b><see cref="GetEventListeners"/> hands back the store itself</b>, and its element type is that
/// record's. It was this contract's last engine-typed claim, and it is not one any more.
/// </para>
/// </remarks>
internal interface IEventTargetHost
{
    /// <summary>The realm the synthetic <c>click</c>/<c>submit</c>/<c>focus</c>/<c>blur</c> event
    /// objects are built in.</summary>
    IJsRealm Realm { get; }

    Dictionary<string, List<EventListenerRegistration>> GetEventListeners(DomNode element);

    /// <summary>
    /// Capture→target→bubble dispatch of <paramref name="evt"/> at <paramref name="element"/>,
    /// answering the "not cancelled" boolean the DOM says <c>dispatchEvent</c> returns.
    /// </summary>
    JsValue DispatchEvent(DomNode element, JsValue evt);


    /// <summary>The JS <c>window</c> wrapper the synthetic focus/blur UIEvents expose as <c>view</c>;
    /// not an object before the window global is installed.</summary>
    JsValue WindowWrapper { get; }

    // Form-control state moved onto the host (Phase 2 item 4 de-globalization): the click checkbox/radio
    // toggle reads and writes the per-bridge FormControl runtime state (checkedness) and drives the
    // radio-group mutual-exclusion walk, all now bridge-instance rather than process-static.
    FormControlRuntimeState FormControlStateFor(DomElement element);
    void UncheckRadioSiblings(DomElement scope, DomElement except, string radioName);
}
