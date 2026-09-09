using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="NodeRelationshipsBinding"/> needs from the bridge: the
/// wrapper→node resolver (<c>contains</c>/<c>compareDocumentPosition</c>/<c>isSameNode</c>/
/// <c>isEqualNode</c> all take another wrapper), the tree-root walk (document-position + <c>getRootNode</c>),
/// the character-data-aware <c>normalize()</c>, the root-node wrapper factory, the deep/shallow clone and
/// the plain JS-wrapper factory. Pure tree predicates (<c>IsDescendantOf</c>, <c>IsEqualNode</c>) live on
/// <see cref="DomNode"/>; document-order comparison (<c>CompareTreeOrder</c>) and the shadow-root walk
/// (<c>FindContainingShadowRoot</c>) are the bridge's <c>internal static</c> helpers, called directly.
/// </summary>
/// <remarks>
/// <para>
/// The contract names no engine type, and it no longer carries a script context: that seam existed
/// for exactly one purpose — raising the <c>DOMException</c> a document clone must throw — which
/// <see cref="IJsCalls.DomError"/> now owns, reached from the call frame the operation already has.
/// </para>
/// <para>
/// The three wrapper members are <see cref="WrapNode"/>, <see cref="WrapRootNode"/> and
/// <see cref="FindNode"/> now, the way <c>ITraversalHost</c> already spells them. They were named
/// after the engine type they took or answered, and a member named after an engine type is an engine
/// reference too — it could never be migrated away while the name survived. Same wrappers and the same
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
