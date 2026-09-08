using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IChildNodeHost implementation for the ChildNodeBinding feature module (Phase 3): the bridge
// exposes the child-node argument builder, the side-effecting insertion primitive, style-scope
// invalidation and the node-iterator / mutation notifications via explicit interface members, so the
// module never reaches an arbitrary bridge private field and the public surface is unchanged.
//
// The builder appears twice because the mixin's three installers do not share a call frame — two mint
// through the realm, DomBridge/ElementInterface.cs still hands over the engine's own. Both forward into
// ONE reading: the JSEAL-framed member is the bridge's ISubDocumentHost implementation, documented
// there as the migrated twin of BuildChildNodeArgumentNodes(in Arguments), which coerces each non-node
// argument with the realm's ToString exactly as the engine frame did. The engine overload goes when
// ElementInterface.cs moves.
public sealed partial class DomBridge : Dom.Features.IChildNodeHost
{
    List<DomNode> Dom.Features.IChildNodeHost.BuildChildNodeArgumentNodes(ReadOnlySpan<JsValue> arguments)
        => ((Dom.Features.ISubDocumentHost)this).BuildChildNodeArgumentNodes(arguments);

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
