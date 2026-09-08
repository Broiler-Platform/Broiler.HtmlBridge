using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="NodeMutationBinding"/> needs from the bridge: the document
/// node (the mutation target), the JS-wrapper factory and reverse lookup, and the mutation-observer /
/// node-iterator notifications. The structural tree operations (index-of, insert, remove-nth,
/// set-parent, child enumeration) are the bridge's neutral <c>internal static</c> helpers, called
/// directly.
/// </summary>
/// <remarks>
/// The contract names no engine type, and that includes the two members whose old names did: the
/// wrapper factory is <see cref="WrapNode"/> and the reverse lookup <see cref="FindDomNode"/>, the
/// shape <c>ITraversalHost</c>, <c>ISubDocumentHost</c> and <c>IDocumentLevelFactoryHost</c> already
/// took. The script context is gone with them — the DOM exceptions the module raises go through the
/// call's own realm.
/// </remarks>
internal interface INodeMutationHost
{
    /// <summary>The single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);

    DomNode DocumentNode { get; }

    /// <summary>Resolves a JS wrapper back to the node it wraps, or <see langword="null"/>.</summary>
    DomNode? FindDomNode(JsValue wrapper);

    /// <summary>
    /// Coerces a <c>(Node or DOMString)...</c> argument list to nodes, wrapping each string
    /// as a text node — the conversion the ParentNode mixin's <c>append</c>/<c>prepend</c>/
    /// <c>replaceChildren</c> share with their element-level counterparts.
    /// </summary>
    List<DomNode> BuildChildNodeArgumentNodes(ReadOnlySpan<JsValue> arguments);

    void NotifyNodeIteratorPreRemoval(DomNode node);
    void NotifyChildRemoved(DomNode parent, DomNode child, int index);
    void NotifyChildAdded(DomNode parent, DomNode child, int index);
}
