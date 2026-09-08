using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The host surface <see cref="ObjectElementBinding"/> needs from the bridge for the
/// <c>&lt;object&gt;</c>-element sub-document accessors: the current page URL (for the same-origin gate,
/// read at call time), the cached-sub-document invalidation hook fired when the <c>data</c> content
/// attribute changes, the load-failure probe that makes <c>contentDocument</c> report <c>null</c> so the
/// element's fallback content shows, and the lazy sub-document factory shared with the rest of the
/// browsing-context machinery. The same-origin test itself is the bridge's neutral <c>internal static</c>
/// <c>IsCrossOrigin</c>, called directly.
/// </summary>
/// <remarks>
/// The contract names no engine type — the sub-document is a <see cref="JsValue"/> handle. The bridge
/// member behind the factory still hands back an engine object, so that seam lives in the bridge's
/// implementation of this contract rather than in the module.
/// </remarks>
internal interface IObjectElementHost
{
    string PageUrl { get; }
    void InvalidateCachedSubDocument(DomElement containerElement);
    bool IsObjectLoadFailed(DomElement objectElement);
    JsValue GetOrCreateSubDocument(DomElement containerElement);
}
