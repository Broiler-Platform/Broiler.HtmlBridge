using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The host surface <see cref="IframeElementBinding"/> needs from the bridge for the <c>&lt;iframe&gt;</c>
/// browsing-context accessors: the realm the accessors are minted in, the same-origin gate
/// (<c>contentDocument</c>/<c>contentWindow</c>/<c>getSVGDocument()</c> return <c>null</c> across origins),
/// the lazy sub-document and sub-window factories, and the <c>src</c>/<c>srcdoc</c> write hooks that reload
/// the frame (invalidate the cached sub-document, clear the fired-onload latch, then fire <c>onload</c> for
/// the new resource). The content-attribute reads/writes themselves use the bridge's neutral
/// <c>internal static</c> <c>SetAttr</c>/<c>TryGetAttribute</c> helpers directly.
/// </summary>
/// <remarks>
/// The contract names no engine type: the two frame objects are <see cref="JsValue"/> handles, and
/// <see cref="Realm"/> is what the module mints its accessors and methods through. The bridge members
/// behind the two factories answer the handles the browsing-context caches hold, so neither side of
/// this contract converts.
/// </remarks>
internal interface IIframeElementHost : IRealmHost, ISubDocumentFactoryHost
{
    bool IsCurrentIframeCrossOrigin(DomElement element);
    JsValue GetOrCreateSubWindow(DomElement element);
    void InvalidateCachedSubDocument(DomElement element);
    void ClearOnloadFired(DomElement element);
    void FireSubDocumentOnload(DomElement element);
}
