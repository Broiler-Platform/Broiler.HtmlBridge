using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit ITreeMutationHost implementation for the TreeMutationBinding feature module (Phase 3): the
// bridge exposes the wrapper→node resolver, the child-node argument builder, the side-effecting
// insertion primitive, style-scope invalidation and the node-iterator / mutation-observer notifications
// via explicit interface members, so the module never reaches an arbitrary bridge private field and the
// public surface is unchanged.
//
// The contract names no engine type any more. Both installers of these eight members —
// ElementInterface.cs for the ParentNode three, JsObjects.cs for the Node five — mint through the
// realm, so the script context the DOM-exception thrower needed, the engine-typed wrapper resolver and
// the second argument reading over the engine's own frame all went with them.
//
// FindNode's body is a plain forward now: the bridge's reverse lookup takes the same handle this
// member is handed, so there is no unwrap left in it. The lookup's name still carries the engine type
// it used to take; that rename is a separate change, and the reverse wrapper map it reads is keyed on
// JsValue.ObjectIdentity rather than on an engine object.
public sealed partial class DomBridge : Dom.Features.ITreeMutationHost
{
    DomNode? Dom.Features.ITreeMutationHost.FindNode(JsValue wrapper)
        => wrapper.IsObject ? FindDomNodeByJSObject(wrapper) : null;

    // One reading, not two: the argument list is read by the bridge's own ISubDocumentHost member,
    // which coerces each non-node argument with the realm's ToString exactly as the engine frame did.
    // Forwarding is how the two contracts share that one reading rather than each carrying a copy.
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
