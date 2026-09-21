using System.Collections.Generic;
using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="ChildNodeBinding"/> needs from the bridge for the DOM
/// <c>ChildNode</c> mixin (<c>remove</c>/<c>before</c>/<c>after</c>/<c>replaceWith</c>): building the
/// node/string argument list, the side-effecting insertion primitive and style-scope invalidation. The
/// neutral tree helpers the mixin also uses (<c>ParentEl</c>, <c>ChildIndexOf</c>,
/// <c>RemoveNthChild</c>, <c>SetParent</c>) stay the bridge's <c>internal static</c> helpers, called
/// directly.
/// </summary>
/// <remarks>
/// The argument builder is declared once: every caller reads the span below.
/// </remarks>
internal interface IChildNodeHost : INodeInsertionHost, IStyleInvalidationHost
{
    /// <summary>
    /// The nodes a <c>before</c>/<c>after</c>/<c>replaceWith</c> argument list denotes: a node argument
    /// is its own wrapper's node (a <c>DocumentFragment</c> contributing its children), and anything
    /// else is coerced to a string and minted as a text node.
    /// </summary>
    List<DomNode> BuildChildNodeArgumentNodes(ReadOnlySpan<JsValue> arguments);
}
