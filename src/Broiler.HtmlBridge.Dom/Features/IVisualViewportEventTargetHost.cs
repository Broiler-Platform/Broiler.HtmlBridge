using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="VisualViewportEventTargetBinding"/> needs from the bridge: the
/// visual-viewport <c>scroll</c> listener store (add / remove), owned by the P2.5 EventTargetRegistry.
/// </summary>
/// <remarks>
/// The listener crosses this seam as a <see cref="JsValue"/> handle rather than as an engine function
/// object, so the binding names no engine type. The handle carries the engine's own function, so the
/// store keeps recognising a listener by identity — which is what <c>removeEventListener</c> depends
/// on. Turning the handle back into what the registry holds is the bridge's job; see
/// <c>DomBridge.VisualViewportEventTargetHost.cs</c>, which is where the remaining engine reference
/// lives until the registry itself is migrated.
/// </remarks>
internal interface IVisualViewportEventTargetHost
{
    void AddVisualViewportScrollListener(JsValue listener);
    void RemoveVisualViewportScrollListener(JsValue listener);
}
