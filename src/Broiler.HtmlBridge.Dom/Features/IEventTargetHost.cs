using Broiler.Dom;
using Broiler.JSeal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="EventTargetBinding"/> needs from the bridge: the realm, the
/// per-node listener store (<c>addEventListener</c>/<c>removeEventListener</c> mutate it), the
/// dispatch a script's <c>dispatchEvent</c> runs, the click a script's <c>click()</c> makes, focus and
/// the window JS object. The listener-registration semantics live in <see cref="EventListenerBinding"/>
/// and the propagation engine in <see cref="EventDispatchBinding"/>; what a click activates is the
/// bridge's (<c>DomBridge/ScriptActivation.cs</c>), next to what a user's click activates.
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
    /// answering the "not cancelled" boolean the DOM says <c>dispatchEvent</c> returns. A
    /// <c>MouseEvent</c> named <c>click</c> activates the element it reaches, as a click does.
    /// </summary>
    JsValue DispatchEvent(DomNode element, JsValue evt);

    /// <summary>
    /// <c>element.click()</c>: an untrusted click at <paramref name="element"/>, with the activation
    /// behaviour of the element it activates -- nothing for a disabled control.
    /// </summary>
    void Click(DomElement element);


    /// <summary>The JS <c>window</c> wrapper; not an object before the window global is installed.</summary>
    JsValue WindowWrapper { get; }

    /// <summary><c>element.focus()</c>: moves focus to <paramref name="element"/> when it can be focused.</summary>
    void FocusElement(DomElement element);

    /// <summary><c>element.blur()</c>: takes focus from <paramref name="element"/> when it has it.</summary>
    void BlurElement(DomElement element);
}
