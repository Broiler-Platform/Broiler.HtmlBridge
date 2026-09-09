using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IChildNodeHost implementation for the ChildNodeBinding feature module (Phase 3): the bridge
// exposes the child-node argument builder, the side-effecting insertion primitive, style-scope
// invalidation and the node-iterator / mutation notifications via explicit interface members, so the
// module never reaches an arbitrary bridge private field and the public surface is unchanged.
//
// The builder appears once: all three of the mixin's installers mint through the realm now, so the one
// reading is the bridge's ISubDocumentHost implementation, which coerces each non-node argument with
// the realm's ToString exactly as the engine frame it replaced did. The engine-framed overload that
// stood beside it went with DomBridge/ElementInterface.cs's migration.
public sealed partial class DomBridge : Dom.Features.IChildNodeHost
{
    List<DomNode> Dom.Features.IChildNodeHost.BuildChildNodeArgumentNodes(ReadOnlySpan<JsValue> arguments)
        => ((Dom.Features.ISubDocumentHost)this).BuildChildNodeArgumentNodes(arguments);

    void Dom.Features.IChildNodeHost.InsertNodeAt(DomNode parent, DomNode node, int index)
        => InsertNodeAt(parent, node, index);

    void Dom.Features.IChildNodeHost.InvalidateStyleScope(DomElement anchor)
        => InvalidateStyleScope(anchor);

    void Dom.Features.IChildNodeHost.NotifyNodeIteratorPreRemoval(DomNode node)
        => NotifyNodeIteratorPreRemoval(node);

    void Dom.Features.IChildNodeHost.NotifyChildRemoved(DomNode parent, DomNode child, int index)
        => NotifyChildRemoved(parent, child, index);
}
