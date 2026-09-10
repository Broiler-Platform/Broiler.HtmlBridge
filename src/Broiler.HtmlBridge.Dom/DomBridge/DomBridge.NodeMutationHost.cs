using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit INodeMutationHost implementation for the NodeMutationBinding feature module (Phase 3):
// the bridge exposes the document node, the JS-wrapper factory and reverse lookup, and the
// mutation-observer / node-iterator notifications via explicit interface members, so the module never
// reaches an arbitrary bridge private field and the public surface is unchanged.
//
// The contract is spelled in JSEAL and so are the two wrapper members behind it, so this file is no
// longer a seam: the factory answers a handle and the reverse lookup takes one. The wrapper the module
// receives, and the one it hands back for a lookup, are the instances the bridge's wrapper tables are
// keyed on — those tables key on JsValue.ObjectIdentity, the reference the handle carries.
public sealed partial class DomBridge : Dom.Features.INodeMutationHost
{
    JsValue Dom.Features.INodeMutationHost.WrapNode(DomNode node) =>
        WrapNode(node);

    DomNode Dom.Features.INodeMutationHost.DocumentNode => _document;

    // A plain forward: the reverse lookup takes the same handle. There is no unwrap left to fail, and
    // a handle that is not an object answers null — which the module's own IsObject guard, at all four
    // of its call sites, still means this never has to do.
    DomNode? Dom.Features.INodeMutationHost.FindDomNode(JsValue wrapper)
        => FindDomNodeByJSObject(wrapper);

    // One reading, not two: the same migrated argument reading the tree-mutation contract forwards to,
    // which coerces each non-node argument with the realm's ToString exactly as the engine frame did.
    List<DomNode> Dom.Features.INodeMutationHost.BuildChildNodeArgumentNodes(ReadOnlySpan<JsValue> arguments)
        => ((Dom.Features.ISubDocumentHost)this).BuildChildNodeArgumentNodes(arguments);

    void Dom.Features.INodeMutationHost.NotifyNodeIteratorPreRemoval(DomNode node)
        => NotifyNodeIteratorPreRemoval(node);

    void Dom.Features.INodeMutationHost.NotifyChildRemoved(DomNode parent, DomNode child, int index)
        => NotifyChildRemoved(parent, child, index);

    void Dom.Features.INodeMutationHost.NotifyChildAdded(DomNode parent, DomNode child, int index)
        => NotifyChildAdded(parent, child, index);
}
