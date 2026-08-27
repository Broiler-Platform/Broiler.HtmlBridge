namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface the two state-touching methods of <see cref="WindowDocumentMiscBinding"/>
/// need from the bridge: the current page URL (for <c>document.contentType</c>) and the
/// visual-viewport scale setter (for <c>window.visualViewport.scale</c>). The other residual singletons
/// in that module are stateless (or take a by-ref store) and do not use this contract.
/// </summary>
internal interface IWindowDocumentMiscHost
{
    string PageUrl { get; }
    void SetVisualViewportScale(double scale);
}
