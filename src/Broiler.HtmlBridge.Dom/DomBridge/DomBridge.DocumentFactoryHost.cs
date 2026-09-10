using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IDocumentFactoryHost implementation for the DocumentFactoryBinding feature module
// (Phase 3): the bridge exposes the node-construction funnels, standalone Attr-node construction,
// the JS-wrapper factory and reverse lookup, and the two name validations via explicit interface
// members, so the module never reaches an arbitrary bridge private field and the public surface is
// unchanged.
//
// The contract is spelled in JSEAL and so is every bridge member behind it, so this file is no longer
// a seam: the wrapper factory answers a handle and the reverse lookup takes one. The wrappers the
// module receives and hands back are the instances the bridge's wrapper tables are keyed on, and those
// tables key on JsValue.ObjectIdentity — the reference the handle carries.
public sealed partial class DomBridge : Dom.Features.IDocumentFactoryHost
{
    // The bridge's own WrapNode, which answers the handle. This used to read
    // FromEngineObject(ToJSObject(node)), and ToJSObject is ToEngineObject(WrapNode(node)) — the same
    // wrapper converted down and back up, twice, to arrive where it started.
    JsValue Dom.Features.IDocumentFactoryHost.WrapNode(DomNode node) => WrapNode(node);

    // Missing rather than undefined for "nothing is defined for this name": the module tests it with
    // IsMissing and never hands it to script, which is what the null check it replaces did. The
    // IsObject filter is kept rather than delegating outright, so a non-object answer still reads as
    // "nothing matched" instead of reaching a caller that expects a wrapper.
    JsValue Dom.Features.IDocumentFactoryHost.CreateDefinedCustomElement(string tagName, string? isValue) =>
        CustomElements.CreateDefinedElement(tagName, isValue) is { IsObject: true } upgraded
            ? upgraded
            : JsValue.Missing;

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

    JsValue Dom.Features.IDocumentFactoryHost.BuildStandaloneAttrNode(string qualifiedName, string? namespaceUri)
        => _attributes.BuildStandaloneAttrNode(qualifiedName, namespaceUri);

    // A plain forward: the reverse lookup takes the same handle. There is no unwrap left to fail, and
    // a handle that is not an object answers null — which the module's own IsObject guard, at both of
    // its call sites, still means this never has to do.
    DomNode? Dom.Features.IDocumentFactoryHost.FindDomNode(JsValue wrapper)
        => FindDomNodeByJSObject(wrapper);

    DomNode Dom.Features.IDocumentFactoryHost.CloneDomNode(DomNode source, bool deep)
        => CloneDomElement(source, deep);

    // Both validations raise their DOMException against the realm, through IJsCalls.DomError. They
    // took a script context until the validators did.
    void Dom.Features.IDocumentFactoryHost.ValidateElementName(string name)
        => ValidateElementName(name, Realm);

    void Dom.Features.IDocumentFactoryHost.ValidateQualifiedName(string qualifiedName, string? ns)
        => ValidateQualifiedName(qualifiedName, ns, Realm);
}
