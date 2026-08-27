using Broiler.JavaScript.BuiltIns.Function;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="VisualViewportEventTargetBinding"/> needs from the bridge: the
/// visual-viewport <c>scroll</c> listener store (add / remove), owned by the P2.5 EventTargetRegistry.
/// </summary>
internal interface IVisualViewportEventTargetHost
{
    void AddVisualViewportScrollListener(JSFunction listener);
    void RemoveVisualViewportScrollListener(JSFunction listener);
}
