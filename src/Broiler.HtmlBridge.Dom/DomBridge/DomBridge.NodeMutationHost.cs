using Broiler.Dom;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit INodeMutationHost implementation for the NodeMutationBinding feature module (Phase 3):
// the bridge exposes the document node, the JS-wrapper factory and reverse lookup, and the
// mutation-observer / node-iterator notifications via explicit interface members, so the module never
// reaches an arbitrary bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.INodeMutationHost
{
    JSObject Dom.Features.INodeMutationHost.ToJSObject(DomNode node) => ToJSObject(node);

    DomNode Dom.Features.INodeMutationHost.DocumentNode => _document;

    DomNode? Dom.Features.INodeMutationHost.FindDomNodeByJSObject(JSObject jsObj)
        => FindDomNodeByJSObject(jsObj);

    List<DomNode> Dom.Features.INodeMutationHost.BuildChildNodeArgumentNodes(in Arguments arguments)
        => BuildChildNodeArgumentNodes(arguments);

    JSContext? Dom.Features.INodeMutationHost.JsContext => _jsContext;

    void Dom.Features.INodeMutationHost.NotifyNodeIteratorPreRemoval(DomNode node)
        => NotifyNodeIteratorPreRemoval(node);

    void Dom.Features.INodeMutationHost.NotifyChildRemoved(DomNode parent, DomNode child, int index)
        => NotifyChildRemoved(parent, child, index);

    void Dom.Features.INodeMutationHost.NotifyChildAdded(DomNode parent, DomNode child, int index)
        => NotifyChildAdded(parent, child, index);
}
