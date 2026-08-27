using System.Collections.Generic;
using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="ChildNodeBinding"/> needs from the bridge for the DOM
/// <c>ChildNode</c> mixin (<c>remove</c>/<c>before</c>/<c>after</c>/<c>replaceWith</c>): building the
/// node/string argument list, the side-effecting insertion primitive, style-scope invalidation, and the
/// node-iterator / mutation-observer notifications. The neutral tree helpers the mixin also uses
/// (<c>ParentEl</c>, <c>ChildIndexOf</c>, <c>RemoveNthChild</c>, <c>SetParent</c>) stay the bridge's
/// <c>internal static</c> helpers, called directly.
/// </summary>
internal interface IChildNodeHost
{
    List<DomNode> BuildChildNodeArgumentNodes(in Arguments arguments);
    void InsertNodeAt(DomNode parent, DomNode node, int index);
    void InvalidateStyleScope(DomElement anchor);
    void NotifyNodeIteratorPreRemoval(DomNode node);
    void NotifyChildRemoved(DomNode parent, DomNode child, int index);
}
