using System.Collections.Generic;
using Broiler.Dom;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="TreeMutationBinding"/> needs from the bridge for the DOM
/// <c>Node</c> child-mutation methods (<c>insertBefore</c>/<c>appendChild</c>/<c>append</c>/
/// <c>prepend</c>/<c>removeChild</c>/<c>replaceChild</c>): the JS-object→node resolver (each takes a
/// child wrapper), the node/string argument builder (<c>append</c>/<c>prepend</c>), the side-effecting
/// insertion primitive, style-scope invalidation, the node-iterator / mutation-observer notifications,
/// and the <see cref="JSContext"/> the DOM-exception thrower needs. The neutral tree helpers the
/// methods also use (<c>ParentEl</c>, <c>ChildAt</c>, <c>ChildIndexOf</c>, <c>RemoveNthChild</c>,
/// <c>RemoveChildFrom</c>, <c>SetParent</c>, <c>ThrowDOMException</c>) stay the bridge's
/// <c>internal static</c> helpers, called directly.
/// </summary>
internal interface ITreeMutationHost
{
    JSContext? JsContext { get; }
    DomNode? FindDomNodeByJSObject(JSObject jsObj);
    List<DomNode> BuildChildNodeArgumentNodes(in Arguments arguments);
    void InsertNodeAt(DomNode parent, DomNode node, int index);

    /// <summary>The state-preserving reposition behind <c>moveBefore</c> — unlike
    /// <see cref="InsertNodeAt"/> it never disconnects the node, so a moved iframe does not
    /// reload and a render-blocking element keeps blocking.</summary>
    void MoveNodeBefore(DomNode parent, DomNode node, DomNode? reference);
    void InvalidateStyleScope(DomElement anchor);
    void NotifyNodeIteratorPreRemoval(DomNode node);
    void NotifyChildAdded(DomNode parent, DomNode child, int index);
    void NotifyChildRemoved(DomNode parent, DomNode child, int index, DomNode? previousSibling, DomNode? nextSibling);
}
