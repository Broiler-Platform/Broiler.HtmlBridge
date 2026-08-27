using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IDocumentLevelFactoryHost implementation for the DocumentLevelFactoryBinding feature
// module (Phase 3): the bridge exposes the JS-wrapper factory and reverse lookup, the
// node-construction funnels, the browsing-context document-root factory, and the sub-document builder
// via explicit interface members, so the module never reaches an arbitrary bridge private field and
// the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IDocumentLevelFactoryHost
{
    JSObject Dom.Features.IDocumentLevelFactoryHost.ToJSObject(DomNode node) => ToJSObject(node);

    DomNode? Dom.Features.IDocumentLevelFactoryHost.FindDomNodeByJSObject(JSObject jsObj)
        => FindDomNodeByJSObject(jsObj);

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

    JSObject Dom.Features.IDocumentLevelFactoryHost.BuildDocument(DomNode docRoot)
        => _subDocuments.BuildDocument(docRoot);
}
