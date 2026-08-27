using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="NodeAccessorsBinding"/> needs from the bridge: the JS-wrapper
/// factory, the document node (for <c>isConnected</c>/<c>ownerDocument</c>), the tree-root walk, the
/// notifying character-data setter (for <c>nodeValue</c>), the sub-document wrapper lookup and the main
/// document JS object (both for <c>ownerDocument</c>). Node-type tests, tree-order helpers, text reads
/// and the owning-document derivation are the bridge's <c>internal static</c> helpers, called directly.
/// </summary>
internal interface INodeAccessorsHost
{
    JSObject ToJSObject(DomNode node);

    /// <summary>
    /// The realm the wrappers belong to, or <see langword="null"/> before the bridge is attached.
    /// <c>childNodes</c> needs it to reach <c>NodeList.prototype</c>; a collection built without one
    /// still answers its contents and only lacks the shared methods.
    /// </summary>
    Broiler.JavaScript.Engine.JSContext? JsContext { get; }
    DomNode DocumentNode { get; }
    DomNode GetTreeRoot(DomNode node);
    void SetCharacterData(DomNode node, string? value);
    bool TryGetDocumentWrapper(DomNode documentRoot, out JSObject wrapper);
    JSObject? DocumentJSObject { get; }
}
