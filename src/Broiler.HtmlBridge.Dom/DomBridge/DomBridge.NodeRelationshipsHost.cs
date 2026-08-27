using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit INodeRelationshipsHost implementation for the NodeRelationshipsBinding feature module
// (Phase 3): the bridge exposes the JS-object→node resolver, the tree-root walk, the character-data-aware
// normalize(), the root-node wrapper factory, the clone and the plain JS-wrapper factory via explicit
// interface members, so the module reaches no arbitrary bridge private field and the public surface is
// unchanged.
public sealed partial class DomBridge : Dom.Features.INodeRelationshipsHost
{
    DomNode? Dom.Features.INodeRelationshipsHost.FindDomNodeByJSObject(JSObject jsObj)
        => FindDomNodeByJSObject(jsObj);

    DomNode Dom.Features.INodeRelationshipsHost.GetTreeRoot(DomNode node) => GetTreeRoot(node);

    void Dom.Features.INodeRelationshipsHost.NormalizeNode(DomElement element) => NormalizeNode(element);

    JSValue Dom.Features.INodeRelationshipsHost.ToJSRootNode(DomNode root) => ToJSRootNode(root);

    DomNode Dom.Features.INodeRelationshipsHost.CloneDomElement(DomNode source, bool deep)
        => CloneDomElement(source, deep);

    JSObject Dom.Features.INodeRelationshipsHost.ToJSObject(DomNode node) => ToJSObject(node);

    Broiler.JavaScript.Engine.JSContext? Dom.Features.INodeRelationshipsHost.JsContext => _jsContext;
}
