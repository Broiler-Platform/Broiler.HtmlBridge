namespace Broiler.HtmlBridge;

// Explicit IElementReflectionHost implementation for the ElementReflectionBinding feature module
// (Phase 3): the bridge exposes only the current page URL (read at call time) so the URL-typed IDL
// getters resolve relative content attributes against the live document base; everything else the
// module needs is a neutral internal static bridge helper it calls directly.
public sealed partial class DomBridge : Dom.Features.IElementReflectionHost
{
    string Dom.Features.IElementReflectionHost.PageUrl => _pageUrl;
}
