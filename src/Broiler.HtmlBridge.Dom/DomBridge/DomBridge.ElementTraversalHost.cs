using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IElementTraversalHost implementation for the ElementTraversalBinding feature module (Phase 3):
// the bridge exposes only its realm and the JS-wrapper factory via explicit interface members, so the
// module reaches no arbitrary bridge private field and the public surface is unchanged.
//
// The factory is a plain forward to WrapNode, which answers the handle the bridge's cache holds, so
// wrapper identity, and `el.firstElementChild === el.firstElementChild` with it, is untouched by the
// module having migrated. (This described the body bcce315 replaced, FromEngineObject(ToJSObject(node)),
// which by then unwrapped and rewrapped the same object, and said its JsInterop step was a cast.)
public sealed partial class DomBridge : Dom.Features.IElementTraversalHost
{
    IJsRealm Dom.Features.IElementTraversalHost.Realm => Realm;

    JsValue Dom.Features.IElementTraversalHost.ToWrapper(DomNode node) =>
        WrapNode(node);
}
