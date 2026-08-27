using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The host surface <see cref="IframeElementBinding"/> needs from the bridge for the <c>&lt;iframe&gt;</c>
/// browsing-context accessors: the same-origin gate (<c>contentDocument</c>/<c>contentWindow</c>/
/// <c>getSVGDocument()</c> return <c>null</c> across origins), the lazy sub-document and sub-window factories,
/// and the <c>src</c>/<c>srcdoc</c> write hooks that reload the frame (invalidate the cached sub-document,
/// clear the fired-onload latch, then fire <c>onload</c> for the new resource). The content-attribute
/// reads/writes themselves use the bridge's neutral <c>internal static</c> <c>SetAttr</c>/<c>TryGetAttribute</c>
/// helpers directly.
/// </summary>
internal interface IIframeElementHost
{
    bool IsCurrentIframeCrossOrigin(DomElement element);
    JSObject GetOrCreateSubDocument(DomElement element);
    JSObject GetOrCreateSubWindow(DomElement element);
    void InvalidateCachedSubDocument(DomElement element);
    void ClearOnloadFired(DomElement element);
    void FireSubDocumentOnload(DomElement element);
}
