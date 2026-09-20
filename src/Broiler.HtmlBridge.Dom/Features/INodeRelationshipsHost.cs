using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="NodeRelationshipsBinding"/> needs from the bridge: the
/// wrapper→node resolver (<c>contains</c>/<c>compareDocumentPosition</c>/<c>isSameNode</c>/
/// <c>isEqualNode</c> all take another wrapper), the tree-root walk (<c>getRootNode</c>), the
/// character-data-aware <c>normalize()</c>, the root-node wrapper factory, the deep/shallow clone and
/// the plain JS-wrapper factory. Pure tree operations (<c>IsDescendantOf</c>, <c>IsEqualNode</c>,
/// <c>CompareDocumentPosition</c>) live on <see cref="DomNode"/>, and so does the composed tree-root
/// walk <c>GetRootNode(composed)</c>; only its wrapper comes from <see cref="WrapRootNode"/>.
/// </summary>
/// <remarks>
/// <para>
/// The contract names no engine type, and it carries no script context: the <c>DOMException</c> a
/// document clone must throw is raised through <see cref="IJsCalls.DomError"/>, reached from the call
/// frame the operation already has.
/// </para>
/// <para>
/// The three wrapper members are <see cref="WrapNode"/>, <see cref="WrapRootNode"/> and
/// <see cref="FindNode"/>. Same wrappers and the same
/// identity: a JSEAL object handle carries the engine's own object.
/// </para>
/// </remarks>
internal interface INodeRelationshipsHost
{
    /// <summary>Resolves the canonical node behind a JS wrapper, or null.</summary>
    DomNode? FindNode(JsValue wrapper);

    DomNode GetTreeRoot(DomNode node);
    void NormalizeNode(DomElement element);

    /// <summary>
    /// The wrapper <c>getRootNode()</c> answers for a tree root: the <c>document</c> object for the
    /// main document, and the ordinary wrapper for anything else.
    /// </summary>
    JsValue WrapRootNode(DomNode root);

    DomNode CloneDomElement(DomNode source, bool deep);

    /// <summary>The single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);
}
