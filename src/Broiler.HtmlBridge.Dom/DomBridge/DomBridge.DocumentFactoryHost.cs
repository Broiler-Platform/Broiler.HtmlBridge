using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IDocumentFactoryHost implementation for the DocumentFactoryBinding feature module
// (Phase 3): the bridge exposes the node-construction funnels, standalone Attr-node construction,
// the JS-wrapper factory and reverse lookup, and the two name validations via explicit interface
// members, so the module never reaches an arbitrary bridge private field and the public surface is
// unchanged.
//
// The contract is spelled in JSEAL; the bridge members behind it are not migrated, so this file is
// the seam. Dom.Runtime.JsInterop is a cast rather than a conversion — a handle carries the engine's
// own object — so the wrappers the module receives and hands back are the instances the bridge's
// wrapper tables are keyed on.
public sealed partial class DomBridge : Dom.Features.IDocumentFactoryHost
{
    JsValue Dom.Features.IDocumentFactoryHost.WrapNode(DomNode node) =>
        Dom.Runtime.JsInterop.FromEngineObject(ToJSObject(node));

    // Missing rather than undefined for "nothing is defined for this name": the module tests it with
    // IsMissing and never hands it to script, which is what the null check it replaces did.
    JsValue Dom.Features.IDocumentFactoryHost.CreateDefinedCustomElement(string tagName, string? isValue) =>
        CustomElements.CreateDefined(tagName, isValue) is { } upgraded
            ? Dom.Runtime.JsInterop.FromEngineObject(upgraded)
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
        => Dom.Runtime.JsInterop.FromEngineObject(_attributes.BuildStandaloneAttrNode(qualifiedName, namespaceUri));

    // The module only asks this of a handle it has already established is an object, so unwrapping it
    // cannot fail here; a non-object would mean the module skipped its own guard.
    DomNode? Dom.Features.IDocumentFactoryHost.FindDomNode(JsValue wrapper)
        => FindDomNodeByJSObject(Dom.Runtime.JsInterop.ToEngineObject(wrapper));

    DomNode Dom.Features.IDocumentFactoryHost.CloneDomNode(DomNode source, bool deep)
        => CloneDomElement(source, deep);

    // Both validations raise their DOMException against the script context, which is what the module
    // used to be handed so that it could pass it back here.
    void Dom.Features.IDocumentFactoryHost.ValidateElementName(string name)
        => ValidateElementName(name, Realm);

    void Dom.Features.IDocumentFactoryHost.ValidateQualifiedName(string qualifiedName, string? ns)
        => ValidateQualifiedName(qualifiedName, ns, Realm);
}
