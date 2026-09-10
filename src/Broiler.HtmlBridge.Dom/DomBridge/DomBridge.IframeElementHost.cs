using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IIframeElementHost implementation for the IframeElementBinding feature module (Phase 3): the
// <iframe> browsing-context accessors reach the frames machinery through this seam — the same-origin gate,
// the sub-document / sub-window factories, and the src/srcdoc reload hooks — forwarding to the existing
// bridge members (the sub-window map and the fired-onload latch live on the BrowsingContextManager /
// SubWindowBinding owners).
//
// This file is the engine-typed half of the seam. The module is written against JSEAL and receives
// JsValue handles; the two factories below still hand back the engine's own objects, and
// Dom.Runtime.JsInterop is a cast rather than a conversion — the frame document and window the module
// sees are the instances the browsing-context caches hold, so `frame.contentWindow === frame.contentWindow`
// is the same question it always was.
//
// Realm is implemented explicitly because DomBridge.Realm is internal: an implicit implementation of a
// public interface member cannot be satisfied by a non-public property (CS0737).
public sealed partial class DomBridge : Dom.Features.IIframeElementHost
{
    IJsRealm Dom.Features.IIframeElementHost.Realm => Realm;

    bool Dom.Features.IIframeElementHost.IsCurrentIframeCrossOrigin(DomElement element) => IsCurrentIframeCrossOrigin(element);

    JsValue Dom.Features.IIframeElementHost.GetOrCreateSubDocument(DomElement element)
        => GetOrCreateSubDocument(element);

    JsValue Dom.Features.IIframeElementHost.GetOrCreateSubWindow(DomElement element)
        => _subWindows.GetOrCreate(element);

    void Dom.Features.IIframeElementHost.InvalidateCachedSubDocument(DomElement element) => InvalidateCachedSubDocument(element);
    void Dom.Features.IIframeElementHost.ClearOnloadFired(DomElement element) => _browsingContexts.ClearOnloadFired(element);
    void Dom.Features.IIframeElementHost.FireSubDocumentOnload(DomElement element) => FireSubDocumentOnload(element);
}
