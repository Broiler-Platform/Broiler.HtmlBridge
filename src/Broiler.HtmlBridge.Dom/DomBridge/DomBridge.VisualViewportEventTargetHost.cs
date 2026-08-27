using Broiler.JavaScript.BuiltIns.Function;

namespace Broiler.HtmlBridge;

// Explicit IVisualViewportEventTargetHost implementation for the VisualViewportEventTargetBinding
// feature module (Phase 3): the bridge exposes the visual-viewport scroll listener store (from the
// P2.5 EventTargetRegistry) via explicit interface members, so the module never reaches an arbitrary
// bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IVisualViewportEventTargetHost
{
    void Dom.Features.IVisualViewportEventTargetHost.AddVisualViewportScrollListener(JSFunction listener)
        => _eventTargets.AddVisualViewportScrollListener(listener);

    void Dom.Features.IVisualViewportEventTargetHost.RemoveVisualViewportScrollListener(JSFunction listener)
        => _eventTargets.RemoveVisualViewportScrollListener(listener);
}
