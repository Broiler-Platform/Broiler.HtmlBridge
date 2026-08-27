namespace Broiler.HtmlBridge;

// Explicit IMatchMediaHost implementation for the MatchMediaBinding feature module (Phase 3):
// the bridge exposes only the live viewport dimensions, via explicit interface members so the
// module never reaches an arbitrary bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IMatchMediaHost
{
    int Dom.Features.IMatchMediaHost.ViewportWidth => _viewportWidth;

    int Dom.Features.IMatchMediaHost.ViewportHeight => _viewportHeight;
}
