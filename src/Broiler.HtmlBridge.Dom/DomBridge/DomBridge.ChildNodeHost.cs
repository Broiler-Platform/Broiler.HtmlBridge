using System.Collections.Generic;
using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IChildNodeHost implementation for the ChildNodeBinding feature module (Phase 3): the bridge
// exposes the child-node argument builder, the side-effecting insertion primitive, style-scope
// invalidation and the node-iterator / mutation notifications via explicit interface members, so the
// module never reaches an arbitrary bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IChildNodeHost
{
    List<DomNode> Dom.Features.IChildNodeHost.BuildChildNodeArgumentNodes(in Arguments arguments)
        => BuildChildNodeArgumentNodes(arguments);

    void Dom.Features.IChildNodeHost.InsertNodeAt(DomNode parent, DomNode node, int index)
        => InsertNodeAt(parent, node, index);

    void Dom.Features.IChildNodeHost.InvalidateStyleScope(DomElement anchor)
        => InvalidateStyleScope(anchor);

    void Dom.Features.IChildNodeHost.NotifyNodeIteratorPreRemoval(DomNode node)
        => NotifyNodeIteratorPreRemoval(node);

    void Dom.Features.IChildNodeHost.NotifyChildRemoved(DomNode parent, DomNode child, int index)
        => NotifyChildRemoved(parent, child, index);
}
