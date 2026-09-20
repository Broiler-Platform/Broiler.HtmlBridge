using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="NodeAccessorsBinding"/> needs from the bridge: the JS-wrapper
/// factory, the live <c>childNodes</c> collection, the document node (for <c>ownerDocument</c>), the
/// notifying character-data setter (for <c>nodeValue</c>), the sub-document wrapper lookup and the main
/// document's wrapper (both for <c>ownerDocument</c>). <c>isConnected</c> needs none of it: it is the
/// canonical <c>DomNode.IsConnected</c>. Node-type tests, tree-order helpers, text reads and the
/// owning-document derivation are the bridge's <c>internal static</c> helpers, called directly.
/// </summary>
/// <remarks>
/// <para>
/// The contract names no engine type, and it carries no realm: <see cref="ChildNodeList"/> is the one
/// operation that needs one, so the contract names that operation instead. The bridge builds that live
/// collection: it hands the realm and a
/// contents function that wraps the node's children on every read to
/// <c>DomCollectionBinding.NodeList</c> and gets a handle back — the same split
/// <see cref="ISelectorsHost.ElementsByTagName"/> makes.
/// </para>
/// <para>
/// The two wrapper members are <see cref="WrapNode"/> and <see cref="DocumentWrapper"/>. Same wrapper,
/// same identity: a JSEAL object handle carries Broiler.JS's own object, or the VM's canonical identity
/// object under that provider, so <c>el === el</c>.
/// </para>
/// </remarks>
internal interface INodeAccessorsHost
{
    /// <summary>The single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);

    /// <summary>
    /// <paramref name="node"/>'s children as a <b>live</b> <c>NodeList</c> — the object
    /// <c>node.childNodes</c> answers. Live because DOM §4.4 says so: the collection holds the walk
    /// rather than its result, so every read sees the tree as it is now.
    /// </summary>
    JsValue ChildNodeList(DomNode node);

    DomNode DocumentNode { get; }
    void SetCharacterData(DomNode node, string? value);
    bool TryGetDocumentWrapper(DomNode documentRoot, out JsValue wrapper);

    /// <summary>
    /// The main document's own wrapper, or <see cref="JsValue.Null"/> when there is none yet — which
    /// is the value <c>ownerDocument</c> then answers.
    /// </summary>
    JsValue DocumentWrapper { get; }
}
