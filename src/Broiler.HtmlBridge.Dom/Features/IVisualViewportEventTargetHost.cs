using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="VisualViewportEventTargetBinding"/> needs from the bridge: the
/// visual-viewport <c>scroll</c> listener store (add / remove), owned by the P2.5 EventTargetRegistry.
/// </summary>
/// <remarks>
/// The listener crosses this seam as a <see cref="JsValue"/> handle and the registry keeps that handle as
/// it arrived, so neither the binding nor the store behind it names an engine type. The store recognises
/// a listener by <see cref="JsValue"/> equality, which compares kind and then the reference the handle
/// carries; every listener that reaches it is kind <c>Function</c>, so what is left is the reference
/// comparison <c>removeEventListener</c> depends on. (This said that turning the handle back into what
/// the registry held was the bridge's job, and that <c>DomBridge/Hosts.Window.cs</c> was
/// where "the remaining engine reference" lived until the registry migrated. There were three, one of
/// them in the registry, and they went together because each end was the other's reason.)
/// </remarks>
internal interface IVisualViewportEventTargetHost
{
    void AddVisualViewportScrollListener(JsValue listener);
    void RemoveVisualViewportScrollListener(JsValue listener);
}
