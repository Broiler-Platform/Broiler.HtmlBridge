using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="EventTargetBinding"/> needs from the bridge: the realm the
/// synthetic <c>click</c>/<c>focus</c>/<c>blur</c> events are built in, the per-node listener store
/// (<c>addEventListener</c>/<c>removeEventListener</c> mutate it), the registration operations over it,
/// the propagation engine (<c>dispatchEvent</c>/<c>click</c>/<c>focus</c>/<c>blur</c> all run
/// capture→target→bubble via it), and the window JS object (the synthetic <c>focus</c>/<c>blur</c>
/// UIEvents expose it as <c>view</c>). The listener-registration semantics live in
/// <see cref="EventListenerBinding"/> and the propagation engine in <see cref="EventDispatchBinding"/>;
/// node-type/attribute/runtime-state helpers, the radio-group mutual-exclusion walk and the no-op
/// function factory are the bridge's <c>internal static</c> helpers.
/// </summary>
/// <remarks>
/// <para>
/// <b>The registration pair is the seam, and it is the same one the document and window contracts
/// use.</b> An <c>EventListenerRegistration</c> holds its listener as a Broiler.JS value — the record
/// is in the unowned <c>DomBridge/RuntimeStates.cs</c> and is shared with the window, form-submit and
/// messaging paths — and <see cref="EventListenerBinding"/> is written against that, so a handle
/// becomes an engine value in the implementation rather than in the module. See
/// <c>ToEngineListenerValue</c> in <c>DomBridge.WindowEventTargetHost.cs</c>, which all three
/// contracts share.
/// </para>
/// <para>
/// <b><see cref="GetEventListeners"/> is engine-typed for the same reason and cannot hide it</b>: the
/// dictionary it hands back is the store itself, and its element type is that record's — declared in
/// the unowned <c>DomBridge/RuntimeStates.cs</c>, which is where this contract's one remaining engine
/// claim comes from and where it goes when that record moves.
/// </para>
/// </remarks>
internal interface IEventTargetHost
{
    /// <summary>The realm the synthetic <c>click</c>/<c>submit</c>/<c>focus</c>/<c>blur</c> event
    /// objects are built in.</summary>
    IJsRealm Realm { get; }

    Dictionary<string, List<EventListenerRegistration>> GetEventListeners(DomNode element);

    /// <inheritdoc cref="EventListenerBinding.AddListener" />
    void AddListener(List<EventListenerRegistration> listeners, JsValue listener, JsValue options);

    /// <inheritdoc cref="EventListenerBinding.RemoveListener" />
    void RemoveListener(List<EventListenerRegistration>? listeners, JsValue listener, JsValue options);

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
