using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IElementTraversalHost implementation for the ElementTraversalBinding feature module (Phase 3):
// the bridge exposes only its realm and the JS-wrapper factory via explicit interface members, so the
// module reaches no arbitrary bridge private field and the public surface is unchanged.
//
// The factory hands back a JSEAL handle over the very wrapper the bridge's cache holds — JsInterop is a
// cast rather than a conversion — so wrapper identity, and `el.firstElementChild === el.firstElementChild`
// with it, is untouched by the module having migrated.
public sealed partial class DomBridge : Dom.Features.IElementTraversalHost
{
    IJsRealm Dom.Features.IElementTraversalHost.Realm => Realm;

    JsValue Dom.Features.IElementTraversalHost.ToWrapper(DomNode node) =>
        WrapNode(node);
}
