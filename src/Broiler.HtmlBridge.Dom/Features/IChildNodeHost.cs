using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
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
/// <remarks>
/// The argument builder is declared twice because the mixin's installers do not share a call frame:
/// two of the three mint through the realm and read a <see cref="JsCall"/>, while
/// <c>DomBridge/ElementInterface.cs</c> still hands over the engine's own <c>Arguments</c>. Both
/// overloads are one reading — the bridge forwards them to a single implementation — and the engine
/// one goes when that last installer moves.
/// </remarks>
internal interface IChildNodeHost
{
    /// <summary>
    /// The nodes a <c>before</c>/<c>after</c>/<c>replaceWith</c> argument list denotes: a node argument
    /// is its own wrapper's node (a <c>DocumentFragment</c> contributing its children), and anything
    /// else is coerced to a string and minted as a text node.
    /// </summary>
    List<DomNode> BuildChildNodeArgumentNodes(ReadOnlySpan<JsValue> arguments);

    /// <inheritdoc cref="BuildChildNodeArgumentNodes(System.ReadOnlySpan{JsValue})" />
    /// <remarks>The same reading over an engine argument frame, for the installer that has not
    /// migrated.</remarks>
    List<DomNode> BuildChildNodeArgumentNodes(in Arguments arguments);

    void InsertNodeAt(DomNode parent, DomNode node, int index);
    void InvalidateStyleScope(DomElement anchor);
    void NotifyNodeIteratorPreRemoval(DomNode node);
    void NotifyChildRemoved(DomNode parent, DomNode child, int index);
}
