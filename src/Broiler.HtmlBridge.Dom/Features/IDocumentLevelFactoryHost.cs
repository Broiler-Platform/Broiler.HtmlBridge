using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The host surface <see cref="DocumentLevelFactoryBinding"/> needs from the bridge: the JS-wrapper
/// factory and reverse lookup, the node-construction funnels (doctype / element / namespaced element /
/// text), the browsing-context document-root factory, and the sub-document builder that wraps a
/// document root into its JS object. Name validation and the neutral tree helpers are the bridge's
/// <c>internal static</c> helpers, called directly.
/// </summary>
internal interface IDocumentLevelFactoryHost
{
    JSObject ToJSObject(DomNode node);
    DomNode? FindDomNodeByJSObject(JSObject jsObj);

    DomDocumentType CreateBridgeDocumentType(string name, string publicId, string systemId);
    DomElement CreateBridgeElement(string tagName);
    DomElement CreateBridgeElementNS(string? namespaceUri, string tagName);
    DomText CreateBridgeTextNode(string data);

    DomDocument CreateBrowsingContextDocument();
    JSObject BuildDocument(DomNode docRoot);
}
