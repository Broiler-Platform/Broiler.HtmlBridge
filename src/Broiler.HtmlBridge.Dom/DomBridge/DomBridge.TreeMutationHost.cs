using System.Collections.Generic;
using Broiler.Dom;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit ITreeMutationHost implementation for the TreeMutationBinding feature module (Phase 3): the
// bridge exposes the JS-object→node resolver, the child-node argument builder, the side-effecting
// insertion primitive, style-scope invalidation, the JS context the DOM-exception thrower needs and the
// node-iterator / mutation-observer notifications via explicit interface members, so the module never
// reaches an arbitrary bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.ITreeMutationHost
{
    JSContext? Dom.Features.ITreeMutationHost.JsContext => _jsContext;

    DomNode? Dom.Features.ITreeMutationHost.FindDomNodeByJSObject(JSObject jsObj)
        => FindDomNodeByJSObject(jsObj);

    List<DomNode> Dom.Features.ITreeMutationHost.BuildChildNodeArgumentNodes(in Arguments arguments)
        => BuildChildNodeArgumentNodes(arguments);

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
