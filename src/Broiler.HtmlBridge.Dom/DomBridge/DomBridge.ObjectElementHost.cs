using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IObjectElementHost implementation for the ObjectElementBinding feature module (Phase 3): the
// <object>-element sub-document accessors reach the browsing-context machinery through this narrow seam —
// the live page URL plus the sub-document invalidation / load-failure / factory hooks — while the neutral
// content-attribute and same-origin helpers are called as internal statics.
//
// The sub-document factory is where this file is the engine-typed half of the seam: the browsing-context
// cache holds the engine's own document object, and Dom.Runtime.JsInterop mints a handle over it rather
// than converting it, so `obj.contentDocument === obj.contentDocument` is the same question it was.
public sealed partial class DomBridge : Dom.Features.IObjectElementHost
{
    string Dom.Features.IObjectElementHost.PageUrl => _pageUrl;
    void Dom.Features.IObjectElementHost.InvalidateCachedSubDocument(DomElement containerElement) => InvalidateCachedSubDocument(containerElement);
    bool Dom.Features.IObjectElementHost.IsObjectLoadFailed(DomElement objectElement) => IsObjectLoadFailed(objectElement);

    JsValue Dom.Features.IObjectElementHost.GetOrCreateSubDocument(DomElement containerElement)
        => Dom.Runtime.JsInterop.FromEngineObject(GetOrCreateSubDocument(containerElement));
}
