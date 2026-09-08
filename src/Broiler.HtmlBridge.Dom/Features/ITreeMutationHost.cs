using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="TreeMutationBinding"/> needs from the bridge for the DOM
/// <c>Node</c> child-mutation methods (<c>insertBefore</c>/<c>appendChild</c>/<c>append</c>/
/// <c>prepend</c>/<c>removeChild</c>/<c>replaceChild</c>): the wrapper→node resolver (each takes a
/// child wrapper), the node/string argument builder (<c>append</c>/<c>prepend</c>), the side-effecting
/// insertion primitive, style-scope invalidation, the node-iterator / mutation-observer notifications,
/// and the <see cref="JSContext"/> the DOM-exception thrower needs. The neutral tree helpers the
/// methods also use (<c>ParentEl</c>, <c>ChildAt</c>, <c>ChildIndexOf</c>, <c>RemoveNthChild</c>,
/// <c>RemoveChildFrom</c>, <c>SetParent</c>, <c>ThrowDOMException</c>) stay the bridge's
/// <c>internal static</c> helpers, called directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>The contract is split across the two vocabularies because its consumers are.</b> The three
/// <c>ParentNode</c> members — <c>append</c>, <c>prepend</c>, <c>replaceChildren</c> — are installed
/// on <c>Element.prototype</c> by <c>DomBridge/ElementInterface.cs</c>, which is migrated, so they run
/// on a <see cref="JsCall"/> and read their arguments through
/// <see cref="BuildChildNodeArgumentNodes(System.ReadOnlySpan{JsValue})"/>. The five <c>Node</c>
/// members stay each wrapper's own property and are installed by <c>DomBridge/JsObjects.cs</c>, which
/// is not; they arrive on an engine frame, and the <see cref="JsContext"/>,
/// <see cref="FindDomNodeByJSObject"/> and <see cref="BuildChildNodeArgumentNodes(in Arguments)"/>
/// members here are exactly what that pins. Those three go when <c>JsObjects.cs</c> migrates.
/// </para>
/// </remarks>
internal interface ITreeMutationHost
{
    JSContext? JsContext { get; }
    DomNode? FindDomNodeByJSObject(JSObject jsObj);

    /// <summary>
    /// The nodes an <c>append</c>/<c>prepend</c>/<c>replaceChildren</c> argument list denotes: a node
    /// argument is its own wrapper's node (a <c>DocumentFragment</c> contributing its children), and
    /// anything else is coerced to a string and minted as a text node.
    /// </summary>
    List<DomNode> BuildChildNodeArgumentNodes(ReadOnlySpan<JsValue> arguments);

    /// <inheritdoc cref="BuildChildNodeArgumentNodes(System.ReadOnlySpan{JsValue})"/>
    /// <remarks>The same reading over an engine argument frame, for the members whose installer has
    /// not migrated.</remarks>
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
