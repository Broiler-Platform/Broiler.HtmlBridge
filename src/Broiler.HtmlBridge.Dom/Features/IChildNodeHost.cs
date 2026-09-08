using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

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
/// The argument builder is declared once. It was briefly a pair, because the mixin's three installers
/// did not share a call frame — two minted through the realm and read a <see cref="JsCall"/> while
/// <c>DomBridge/ElementInterface.cs</c> handed over the engine's own argument frame — and both
/// overloads forwarded into one reading. That installer has migrated, so the engine overload is gone
/// and every caller reads the span below.
/// </remarks>
internal interface IChildNodeHost
{
    /// <summary>
    /// The nodes a <c>before</c>/<c>after</c>/<c>replaceWith</c> argument list denotes: a node argument
    /// is its own wrapper's node (a <c>DocumentFragment</c> contributing its children), and anything
    /// else is coerced to a string and minted as a text node.
    /// </summary>
    List<DomNode> BuildChildNodeArgumentNodes(ReadOnlySpan<JsValue> arguments);

    void InsertNodeAt(DomNode parent, DomNode node, int index);
    void InvalidateStyleScope(DomElement anchor);
    void NotifyNodeIteratorPreRemoval(DomNode node);
    void NotifyChildRemoved(DomNode parent, DomNode child, int index);
}
