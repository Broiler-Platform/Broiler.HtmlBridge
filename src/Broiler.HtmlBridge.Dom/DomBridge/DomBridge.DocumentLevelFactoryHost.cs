using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IDocumentLevelFactoryHost implementation for the DocumentLevelFactoryBinding feature
// module (Phase 3): the bridge exposes the JS-wrapper factory and reverse lookup, the
// node-construction funnels, the browsing-context document-root factory, the sub-document builder and
// the two name validations via explicit interface members, so the module never reaches an arbitrary
// bridge private field and the public surface is unchanged.
//
// The contract is spelled in JSEAL; the bridge members behind it are not migrated yet, so this file is
// the seam. JsInterop is a cast rather than a conversion — a handle carries the engine's own object —
// so the wrapper the module receives is the same instance the bridge's wrapper tables are keyed on.
public sealed partial class DomBridge : Dom.Features.IDocumentLevelFactoryHost
{
    JsValue Dom.Features.IDocumentLevelFactoryHost.ToJsObject(DomNode node) =>
        Dom.Runtime.JsInterop.FromEngineObject(ToJSObject(node));

    // The module only asks this of a handle it has already established is an object, so unwrapping it
    // cannot fail here; a non-object would mean the module skipped its own guard.
    DomNode? Dom.Features.IDocumentLevelFactoryHost.FindDomNode(JsValue wrapper)
        => FindDomNodeByJSObject(Dom.Runtime.JsInterop.ToEngineObject(wrapper));

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

    JsValue Dom.Features.IDocumentLevelFactoryHost.BuildDocument(DomNode docRoot)
        => Dom.Runtime.JsInterop.FromEngineObject(_subDocuments.BuildDocument(docRoot));

    // Both validations raise their DOMException against the script context, which is what the module
    // used to be handed so that it could pass it back here.
    void Dom.Features.IDocumentLevelFactoryHost.ValidateElementName(string name)
        => ValidateElementName(name, Realm);

    void Dom.Features.IDocumentLevelFactoryHost.ValidateQualifiedName(string qualifiedName, string? ns)
        => ValidateQualifiedName(qualifiedName, ns, Realm);
}
