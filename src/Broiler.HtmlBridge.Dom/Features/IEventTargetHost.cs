using Broiler.Dom;
using Broiler.JSeal;
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
/// node-type/attribute helpers are the bridge's <c>internal static</c> helpers, and the form-control
/// state and the radio-group mutual-exclusion walk are members of this contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no registration pair on this contract.</b> <see cref="EventTargetBinding"/> calls
/// <see cref="EventListenerBinding"/> itself, with the realm its call frame carries.
/// </para>
/// <para>
/// <b><see cref="GetEventListeners"/> hands back the store itself</b>, whose element type is
/// <c>EventListenerRegistration</c> — a record that holds a <see cref="JsValue"/>, so this contract
/// names no engine type.
/// </para>
/// </remarks>
internal interface IEventTargetHost : IRealmHost
{
    Dictionary<string, List<EventListenerRegistration>> GetEventListeners(DomNode element);

    /// <summary>
    /// Capture→target→bubble dispatch of <paramref name="evt"/> at <paramref name="element"/>,
    /// answering the "not cancelled" boolean the DOM says <c>dispatchEvent</c> returns.
    /// </summary>
    JsValue DispatchEvent(DomNode element, JsValue evt);


    /// <summary>The JS <c>window</c> wrapper the synthetic focus/blur UIEvents expose as <c>view</c>;
    /// not an object before the window global is installed.</summary>
    JsValue WindowWrapper { get; }

    // Form-control checkedness state for synthetic click event toggling.
    bool TryGetFormControlChecked(DomElement element, out bool value);
    void SetFormControlChecked(DomElement element, bool value);
}
