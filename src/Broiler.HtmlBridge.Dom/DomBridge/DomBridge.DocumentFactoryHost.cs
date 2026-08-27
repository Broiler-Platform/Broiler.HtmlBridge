using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IDocumentFactoryHost implementation for the DocumentFactoryBinding feature module
// (Phase 3): the bridge exposes the node-construction funnels, standalone Attr-node construction,
// and the JS-wrapper factory via explicit interface members, so the module never reaches an
// arbitrary bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IDocumentFactoryHost
{
    JSObject Dom.Features.IDocumentFactoryHost.ToJSObject(DomNode node) => ToJSObject(node);

    JSObject? Dom.Features.IDocumentFactoryHost.CreateDefinedCustomElement(string tagName, string? isValue) =>
        CustomElements.CreateDefined(tagName, isValue);

    void Dom.Features.IDocumentFactoryHost.RecordCustomElementIsValue(DomElement element, string isValue) =>
        CustomElements.RecordIsValue(element, isValue);

    DomNode Dom.Features.IDocumentFactoryHost.AdoptNode(DomNode node) => _document.AdoptNode(node);

    DomElement Dom.Features.IDocumentFactoryHost.CreateBridgeElement(string tagName)
        => NoteCreatedByScript(CreateBridgeElement(tagName));

    DomElement Dom.Features.IDocumentFactoryHost.CreateBridgeElementNS(string? namespaceUri, string tagName)
        => NoteCreatedByScript(CreateBridgeElementNS(namespaceUri, tagName));

    /// <summary>
    /// Tells the <see cref="Dom.Runtime.ScriptInsertionRunner"/> that <paramref name="element"/> was
    /// built by <c>document.createElement</c>/<c>createElementNS</c>, which is what makes a
    /// <c>&lt;script&gt;</c> eligible to run when it is inserted. This contract is the JS factory's
    /// only route into the bridge's construction funnels — every other caller reaches the private
    /// methods directly — so marking here marks script-created elements and nothing else: neither
    /// the parser's scripts (already run from the document source) nor an <c>innerHTML</c> parse's
    /// (never run, per spec) pass through it. Returns its argument so it can wrap the call.
    /// </summary>
    private DomElement NoteCreatedByScript(DomElement element)
    {
        _scriptInsertion.NoteCreatedByScript(element);
        return element;
    }

    DomText Dom.Features.IDocumentFactoryHost.CreateBridgeTextNode(string data)
        => CreateBridgeTextNode(data);

    DomDocumentFragment Dom.Features.IDocumentFactoryHost.CreateBridgeDocumentFragment()
        => CreateBridgeDocumentFragment();

    JSObject Dom.Features.IDocumentFactoryHost.BuildStandaloneAttrNode(string qualifiedName, string? namespaceUri)
        => _attributes.BuildStandaloneAttrNode(qualifiedName, namespaceUri);

    DomNode? Dom.Features.IDocumentFactoryHost.FindDomNodeByJSObject(JSObject obj)
        => FindDomNodeByJSObject(obj);

    DomNode Dom.Features.IDocumentFactoryHost.CloneDomNode(DomNode source, bool deep)
        => CloneDomElement(source, deep);
}
