using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="VisualViewportEventTargetBinding"/> needs from the bridge: the
/// visual-viewport <c>scroll</c> listener store (add / remove), owned by <c>EventTargetRegistry</c>.
/// </summary>
/// <remarks>
/// The listener crosses this seam as a <see cref="JsValue"/> handle and the registry keeps that handle as
/// it arrived, so neither the binding nor the store behind it names an engine type. The store recognises
/// a listener by <see cref="JsValue"/> equality, which compares kind and then the reference the handle
/// carries; every listener that reaches it is kind <c>Function</c>, so what is left is the reference
/// comparison <c>removeEventListener</c> depends on.
/// </remarks>
internal interface IVisualViewportEventTargetHost
{
    void AddVisualViewportScrollListener(JsValue listener);
    void RemoveVisualViewportScrollListener(JsValue listener);
}
