using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="TreeMutationBinding"/> needs from the bridge for the DOM
/// <c>Node</c> child-mutation methods (<c>insertBefore</c>/<c>appendChild</c>/<c>append</c>/
/// <c>prepend</c>/<c>removeChild</c>/<c>replaceChild</c>): the wrapper→node resolver (each takes a
/// child wrapper), the node/string argument builder (<c>append</c>/<c>prepend</c>), the side-effecting
/// insertion primitive, style-scope invalidation and the node-iterator / mutation-observer
/// notifications. The neutral tree helpers the methods also use (<c>ParentEl</c>, <c>ChildAt</c>,
/// <c>ChildIndexOf</c>, <c>RemoveNthChild</c>, <c>RemoveChildFrom</c>, <c>SetParent</c>) stay the
/// bridge's <c>internal static</c> helpers, called directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>One vocabulary now, because both consumers speak it.</b> The three <c>ParentNode</c> members are
/// installed on <c>Element.prototype</c> by <c>DomBridge/ElementInterface.cs</c> and the five
/// <c>Node</c> members stay each wrapper's own property, installed by <c>DomBridge/JsObjects.cs</c>;
/// both mint through the realm, so every operation arrives on a <see cref="JsCall"/> and reads its
/// arguments through <see cref="BuildChildNodeArgumentNodes"/>. The three members that existed only
/// for the engine frame — a script context for the DOM-exception thrower, a wrapper resolver taking
/// the engine's object, and a second argument reading over the engine's own frame — are gone with it.
/// <see cref="FindNode"/> is the surviving resolver, named for what it answers rather than for the
/// engine type it used to take, the way <c>INodeRelationshipsHost</c> and <c>ITraversalHost</c> spell
/// it.
/// </para>
/// </remarks>
internal interface ITreeMutationHost
{
    /// <summary>Resolves the canonical node behind a JS wrapper, or null.</summary>
    DomNode? FindNode(JsValue wrapper);

    /// <summary>
    /// The nodes an <c>append</c>/<c>prepend</c>/<c>replaceChildren</c> argument list denotes: a node
    /// argument is its own wrapper's node (a <c>DocumentFragment</c> contributing its children), and
    /// anything else is coerced to a string and minted as a text node.
    /// </summary>
    List<DomNode> BuildChildNodeArgumentNodes(ReadOnlySpan<JsValue> arguments);

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
