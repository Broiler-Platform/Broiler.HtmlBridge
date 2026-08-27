using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IElementTraversalHost implementation for the ElementTraversalBinding feature module (Phase 3):
// the bridge exposes only the JS-wrapper factory via an explicit interface member, so the module reaches
// no arbitrary bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IElementTraversalHost
{
    JSObject Dom.Features.IElementTraversalHost.ToJSObject(DomNode node) => ToJSObject(node);
}
