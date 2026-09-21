using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="DocumentFactoryBinding"/> needs from the bridge: the node
/// construction funnels (element / namespaced element / text / document fragment), standalone
/// <c>Attr</c>-node construction, the JS-wrapper factory and reverse lookup, and the two name
/// validations. ASCII-lowercasing is a neutral <c>internal static</c> bridge helper the module calls
/// directly, so it is not on this contract.
/// </summary>
/// <remarks>
/// The contract names no engine type, the two wrapper members' own names included: they are
/// <see cref="WrapNode"/> and <see cref="FindDomNode"/>, the signatures <c>IDocumentLevelFactoryHost</c>
/// gives <c>ToJsObject</c> and <c>FindDomNode</c>. The name validations are on the contract for the
/// same reason they are there — they raise a <c>DOMException</c> against the realm the bridge's side
/// hands them, so the module only asks to reject a name. This module is the one that puts the
/// inherited <see cref="INameValidationHost.ValidateElementName"/> to a second use: it guards doctype
/// names (XML) as well as element names.
/// </remarks>
internal interface IDocumentFactoryHost : INameValidationHost, INodeWrapperHost, ITextNodeFactoryHost
{
    /// <summary>The element a defined custom tag creates by running its own constructor, or
    /// <see cref="JsValue.Missing"/> when nothing is defined for it and the ordinary path applies.
    /// <paramref name="isValue"/> is <c>createElement</c>'s <c>is</c> option, which selects a
    /// customized built-in rather than an autonomous element.</summary>
    JsValue CreateDefinedCustomElement(string tagName, string? isValue);

    /// <summary>Notes an <c>is</c> option that named nothing defined, so a later <c>define</c> can
    /// still upgrade the element and serialization can report it.</summary>
    void RecordCustomElementIsValue(DomElement element, string isValue);

    /// <summary>Moves <paramref name="node"/> into this document (DOM §4.5), returning it.</summary>
    DomNode AdoptNode(DomNode node);

    DomElement CreateBridgeElement(string tagName);
    DomElement CreateBridgeElementNS(string? namespaceUri, string tagName);
    DomDocumentFragment CreateBridgeDocumentFragment();

    JsValue BuildStandaloneAttrNode(string qualifiedName, string? namespaceUri);

    /// <summary>Resolves a JS wrapper back to the node it wraps, or <see langword="null"/>.</summary>
    DomNode? FindDomNode(JsValue wrapper);

    /// <summary>Clones a node, deeply when asked — the copy <c>importNode</c> hands back.</summary>
    DomNode CloneDomNode(DomNode source, bool deep);
}
