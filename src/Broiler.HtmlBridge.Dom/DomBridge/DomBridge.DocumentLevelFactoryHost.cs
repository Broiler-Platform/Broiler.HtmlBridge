using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IDocumentLevelFactoryHost implementation for the DocumentLevelFactoryBinding feature
// module (Phase 3): the bridge exposes the JS-wrapper factory and reverse lookup, the
// node-construction funnels, the browsing-context document-root factory, the sub-document builder and
// the two name validations via explicit interface members, so the module never reaches an arbitrary
// bridge private field and the public surface is unchanged.
//
// The contract is spelled in JSEAL and so is every bridge member behind it, so this file is no longer
// a seam: the wrapper factory answers a handle and the reverse lookup takes one. The wrapper the module
// receives is the same instance the bridge's wrapper tables are keyed on.
public sealed partial class DomBridge : Dom.Features.IDocumentLevelFactoryHost
{
    JsValue Dom.Features.IDocumentLevelFactoryHost.ToJsObject(DomNode node) =>
        WrapNode(node);

    // A plain forward: the reverse lookup takes the same handle. There is no unwrap left to fail, and
    // a handle that is not an object answers null — which the module's own IsObject guard still means
    // this never has to do.
    DomNode? Dom.Features.IDocumentLevelFactoryHost.FindDomNode(JsValue wrapper)
        => FindDomNodeByJSObject(wrapper);

    DomDocumentType Dom.Features.IDocumentLevelFactoryHost.CreateBridgeDocumentType(string name, string publicId, string systemId)
        => CreateBridgeDocumentType(name, publicId, systemId);

    DomElement Dom.Features.IDocumentLevelFactoryHost.CreateBridgeElement(string tagName)
        => CreateBridgeElement(tagName);

    DomElement Dom.Features.IDocumentLevelFactoryHost.CreateBridgeElementNS(string? namespaceUri, string tagName)
        => CreateBridgeElementNS(namespaceUri, tagName);

    DomText Dom.Features.IDocumentLevelFactoryHost.CreateBridgeTextNode(string data)
        => CreateBridgeTextNode(data);

    DomDocument Dom.Features.IDocumentLevelFactoryHost.CreateBrowsingContextDocument()
        => CreateBrowsingContextDocument();

    // Build answers the handle; this used to ask an adapter to convert it out and convert it back.
    JsValue Dom.Features.IDocumentLevelFactoryHost.BuildDocument(DomNode docRoot)
        => _subDocuments.Build(docRoot);

    // Both validations raise their DOMException against the realm, through IJsCalls.DomError. They
    // took a script context until the validators did.
    void Dom.Features.IDocumentLevelFactoryHost.ValidateElementName(string name)
        => ValidateElementName(name, Realm);

    void Dom.Features.IDocumentLevelFactoryHost.ValidateQualifiedName(string qualifiedName, string? ns)
        => ValidateQualifiedName(qualifiedName, ns, Realm);
}
