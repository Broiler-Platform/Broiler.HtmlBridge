using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit ITreeMutationHost implementation for the TreeMutationBinding feature module (Phase 3): the
// bridge exposes the wrapper→node resolver, the child-node argument builders, the side-effecting
// insertion primitive, style-scope invalidation, the JS context the DOM-exception thrower needs and the
// node-iterator / mutation-observer notifications via explicit interface members, so the module never
// reaches an arbitrary bridge private field and the public surface is unchanged.
//
// The JS context, the wrapper→node resolver and the engine-framed argument builder are here for the
// five Node members alone — they are installed by DomBridge/JsObjects.cs, which still hands them the
// engine's argument frame. The three migrated ParentNode members take the JSEAL builder beside it.
public sealed partial class DomBridge : Dom.Features.ITreeMutationHost
{
    JSContext? Dom.Features.ITreeMutationHost.JsContext => _jsContext;

    DomNode? Dom.Features.ITreeMutationHost.FindDomNodeByJSObject(JSObject jsObj)
        => FindDomNodeByJSObject(jsObj);

    List<DomNode> Dom.Features.ITreeMutationHost.BuildChildNodeArgumentNodes(in Arguments arguments)
        => BuildChildNodeArgumentNodes(arguments);

    // One reading, not two: the JSEAL-framed argument list is read by the bridge's own
    // ISubDocumentHost member, which is documented there as the migrated twin of
    // BuildChildNodeArgumentNodes(in Arguments) and coerces each non-node argument with the realm's
    // ToString exactly as the engine frame did. Forwarding is how the two contracts share that one
    // reading rather than each carrying a copy of it.
    List<DomNode> Dom.Features.ITreeMutationHost.BuildChildNodeArgumentNodes(ReadOnlySpan<JsValue> arguments)
        => ((Dom.Features.ISubDocumentHost)this).BuildChildNodeArgumentNodes(arguments);

    void Dom.Features.ITreeMutationHost.MoveNodeBefore(DomNode parent, DomNode node, DomNode? reference)
        => MoveNodeBefore(parent, node, reference);

    void Dom.Features.ITreeMutationHost.InsertNodeAt(DomNode parent, DomNode node, int index)
        => InsertNodeAt(parent, node, index);

    void Dom.Features.ITreeMutationHost.InvalidateStyleScope(DomElement anchor)
        => InvalidateStyleScope(anchor);

    void Dom.Features.ITreeMutationHost.NotifyNodeIteratorPreRemoval(DomNode node)
        => NotifyNodeIteratorPreRemoval(node);

    void Dom.Features.ITreeMutationHost.NotifyChildAdded(DomNode parent, DomNode child, int index)
        => NotifyChildAdded(parent, child, index);

    void Dom.Features.ITreeMutationHost.NotifyChildRemoved(DomNode parent, DomNode child, int index, DomNode? previousSibling, DomNode? nextSibling)
        => NotifyChildRemoved(parent, child, index, previousSibling, nextSibling);
}
