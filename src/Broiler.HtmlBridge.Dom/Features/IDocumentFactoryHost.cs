using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="DocumentFactoryBinding"/> needs from the bridge: the node
/// construction funnels (element / namespaced element / text / document fragment), standalone
/// <c>Attr</c>-node construction, and the JS-wrapper factory. Name validation and ASCII-lowercasing
/// are neutral <c>internal static</c> bridge helpers the module calls directly, so they are not on
/// this contract.
/// </summary>
internal interface IDocumentFactoryHost
{
    JSObject ToJSObject(DomNode node);

    /// <summary>The element a defined custom tag creates by running its own constructor, or
    /// <see langword="null"/> when nothing is defined for it and the ordinary path applies.
    /// <paramref name="isValue"/> is <c>createElement</c>'s <c>is</c> option, which selects a
    /// customized built-in rather than an autonomous element.</summary>
    JSObject? CreateDefinedCustomElement(string tagName, string? isValue);

    /// <summary>Notes an <c>is</c> option that named nothing defined, so a later <c>define</c> can
    /// still upgrade the element and serialization can report it.</summary>
    void RecordCustomElementIsValue(DomElement element, string isValue);

    /// <summary>Moves <paramref name="node"/> into this document (DOM §4.5), returning it.</summary>
    DomNode AdoptNode(DomNode node);

    DomElement CreateBridgeElement(string tagName);
    DomElement CreateBridgeElementNS(string? namespaceUri, string tagName);
    DomText CreateBridgeTextNode(string data);
    DomDocumentFragment CreateBridgeDocumentFragment();

    JSObject BuildStandaloneAttrNode(string qualifiedName, string? namespaceUri);

    /// <summary>Resolves a JS wrapper back to the node it wraps, or <see langword="null"/>.</summary>
    DomNode? FindDomNodeByJSObject(JSObject obj);

    /// <summary>Clones a node, deeply when asked — the copy <c>importNode</c> hands back.</summary>
    DomNode CloneDomNode(DomNode source, bool deep);
}
