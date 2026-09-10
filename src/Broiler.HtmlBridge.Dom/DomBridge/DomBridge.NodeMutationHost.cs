using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit INodeMutationHost implementation for the NodeMutationBinding feature module (Phase 3):
// the bridge exposes the document node, the JS-wrapper factory and reverse lookup, and the
// mutation-observer / node-iterator notifications via explicit interface members, so the module never
// reaches an arbitrary bridge private field and the public surface is unchanged.
//
// The contract is spelled in JSEAL and the two wrapper members behind it are not migrated, so this
// file is the seam. Dom.Runtime.JsInterop is a cast rather than a conversion — a handle carries the
// engine's own object — so the wrapper the module receives, and the one it hands back for a lookup,
// are the instances the bridge's wrapper tables are keyed on.
public sealed partial class DomBridge : Dom.Features.INodeMutationHost
{
    JsValue Dom.Features.INodeMutationHost.WrapNode(DomNode node) =>
        WrapNode(node);

    DomNode Dom.Features.INodeMutationHost.DocumentNode => _document;

    // The module only asks this of a handle it has already established is an object, so unwrapping it
    // cannot fail here; a non-object would mean the module skipped its own guard.
    DomNode? Dom.Features.INodeMutationHost.FindDomNode(JsValue wrapper)
        => FindDomNodeByJSObject(Dom.Runtime.JsInterop.ToEngineObject(wrapper));

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
