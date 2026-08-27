using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

// Explicit IIframeElementHost implementation for the IframeElementBinding feature module (Phase 3): the
// <iframe> browsing-context accessors reach the frames machinery through this seam — the same-origin gate,
// the sub-document / sub-window factories, and the src/srcdoc reload hooks — forwarding to the existing
// bridge members (the sub-window map and the fired-onload latch live on the BrowsingContextManager /
// SubWindowBinding owners).
public sealed partial class DomBridge : Dom.Features.IIframeElementHost
{
    bool Dom.Features.IIframeElementHost.IsCurrentIframeCrossOrigin(DomElement element) => IsCurrentIframeCrossOrigin(element);
    JSObject Dom.Features.IIframeElementHost.GetOrCreateSubDocument(DomElement element) => GetOrCreateSubDocument(element);
    JSObject Dom.Features.IIframeElementHost.GetOrCreateSubWindow(DomElement element) => _subWindows.GetOrCreate(element);
    void Dom.Features.IIframeElementHost.InvalidateCachedSubDocument(DomElement element) => InvalidateCachedSubDocument(element);
    void Dom.Features.IIframeElementHost.ClearOnloadFired(DomElement element) => _browsingContexts.ClearOnloadFired(element);
    void Dom.Features.IIframeElementHost.FireSubDocumentOnload(DomElement element) => FireSubDocumentOnload(element);
}
