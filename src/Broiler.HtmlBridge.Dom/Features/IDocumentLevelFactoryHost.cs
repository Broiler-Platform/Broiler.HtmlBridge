using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The host surface <see cref="DocumentLevelFactoryBinding"/> needs from the bridge: the JS-wrapper
/// factory and reverse lookup, the node-construction funnels (doctype / element / namespaced element /
/// text), the browsing-context document-root factory, the sub-document builder that wraps a document
/// root into its JS object, and the two name validations. The neutral tree helpers are the bridge's
/// <c>internal static</c> helpers, called directly.
/// </summary>
/// <remarks>
/// The validations are on the contract rather than called as statics. The bridge's helpers take the
/// realm to raise their <c>DOMException</c> against, and the bridge's side of this contract hands them
/// its own. What the module wants is "reject this name", which is what it asks for.
/// </remarks>
internal interface IDocumentLevelFactoryHost : IJsObjectHost, INameValidationHost, ITextNodeFactoryHost
{
    /// <summary>Reverse wrapper lookup: the node whose JS wrapper is <paramref name="wrapper"/>.</summary>
    DomNode? FindDomNode(JsValue wrapper);

    DomDocumentType CreateBridgeDocumentType(string name, string publicId, string systemId);
    DomElement CreateBridgeElement(string tagName);
    DomElement CreateBridgeElementNS(string? namespaceUri, string tagName);

    DomDocument CreateBrowsingContextDocument();
    JsValue BuildDocument(DomNode docRoot);
}
