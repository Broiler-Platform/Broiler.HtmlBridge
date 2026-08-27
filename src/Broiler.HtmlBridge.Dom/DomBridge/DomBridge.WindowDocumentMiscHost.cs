namespace Broiler.HtmlBridge;

// Explicit IWindowDocumentMiscHost implementation for the WindowDocumentMiscBinding feature module
// (Phase 3): the bridge exposes the current page URL and the visual-viewport scale setter via explicit
// interface members, so the module never reaches an arbitrary bridge private field and the public
// surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IWindowDocumentMiscHost
{
    string Dom.Features.IWindowDocumentMiscHost.PageUrl => _pageUrl;

    void Dom.Features.IWindowDocumentMiscHost.SetVisualViewportScale(double scale)
        => SetVisualViewportScale(scale);
}
