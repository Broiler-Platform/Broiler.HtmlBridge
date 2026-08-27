using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

// Explicit IObjectElementHost implementation for the ObjectElementBinding feature module (Phase 3): the
// <object>-element sub-document accessors reach the browsing-context machinery through this narrow seam —
// the live page URL plus the sub-document invalidation / load-failure / factory hooks — while the neutral
// content-attribute and same-origin helpers are called as internal statics.
public sealed partial class DomBridge : Dom.Features.IObjectElementHost
{
    string Dom.Features.IObjectElementHost.PageUrl => _pageUrl;
    void Dom.Features.IObjectElementHost.InvalidateCachedSubDocument(DomElement containerElement) => InvalidateCachedSubDocument(containerElement);
    bool Dom.Features.IObjectElementHost.IsObjectLoadFailed(DomElement objectElement) => IsObjectLoadFailed(objectElement);
    JSObject Dom.Features.IObjectElementHost.GetOrCreateSubDocument(DomElement containerElement) => GetOrCreateSubDocument(containerElement);
}
