using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The host surface <see cref="DocumentLevelFactoryBinding"/> needs from the bridge: the JS-wrapper
/// factory and reverse lookup, the node-construction funnels (doctype / element / namespaced element /
/// text), the browsing-context document-root factory, the sub-document builder that wraps a document
/// root into its JS object, and the two name validations. The neutral tree helpers are the bridge's
/// <c>internal static</c> helpers, called directly.
/// </summary>
/// <remarks>
/// The validations are on the contract rather than called as statics because the bridge's helpers take
/// the script context to raise their <c>DOMException</c> against, and handing a feature module that
/// context so it can hand it straight back is the coupling this migration removes. What the module
/// wants is "reject this name", which is what it now asks for.
/// </remarks>
internal interface IDocumentLevelFactoryHost
{
    /// <summary>
    /// The single JS wrapper identity for <paramref name="node"/>, as a JSEAL handle over the same
    /// engine object the bridge's wrapper tables are keyed on.
    /// </summary>
    JsValue ToJsObject(DomNode node);

    /// <summary>Reverse wrapper lookup: the node whose JS wrapper is <paramref name="wrapper"/>.</summary>
    DomNode? FindDomNode(JsValue wrapper);

    DomDocumentType CreateBridgeDocumentType(string name, string publicId, string systemId);
    DomElement CreateBridgeElement(string tagName);
    DomElement CreateBridgeElementNS(string? namespaceUri, string tagName);
    DomText CreateBridgeTextNode(string data);

    DomDocument CreateBrowsingContextDocument();
    JsValue BuildDocument(DomNode docRoot);

    /// <summary>Throws an <c>InvalidCharacterError</c> when <paramref name="name"/> is not a valid
    /// element name (DOM's Name production).</summary>
    void ValidateElementName(string name);

    /// <summary>Throws an <c>InvalidCharacterError</c> or a <c>NamespaceError</c> when
    /// <paramref name="qualifiedName"/> is malformed, or is inconsistent with <paramref name="ns"/>.</summary>
    void ValidateQualifiedName(string qualifiedName, string? ns);
}
