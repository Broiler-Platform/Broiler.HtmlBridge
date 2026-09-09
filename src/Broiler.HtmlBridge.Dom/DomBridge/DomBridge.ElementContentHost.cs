using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IElementContentHost implementation for the ElementContentBinding feature module (Phase 3): the
// innerHTML/outerHTML/textContent members route through the bridge's shared HTML parser/serializer and
// canonical tree mutation, so each forwards to the existing private serialize/set helpers.
//
// This file used to be the engine-typed half of the seam, for one member. It asked the bridge's
// GetNodeTextValue for an engine string or engine null and unpicked it again into the string-or-null
// the contract wants — which was NodeTextOrNull spelled the long way round through two allocations,
// as that adapter's own remarks said. The adapter is gone and this asks NodeTextOrNull directly.
public sealed partial class DomBridge : Dom.Features.IElementContentHost
{
    IJsRealm Dom.Features.IElementContentHost.Realm => Realm;

    string Dom.Features.IElementContentHost.SerializeChildrenToHtml(DomElement element) => SerializeChildrenToHtml(element);
    string Dom.Features.IElementContentHost.SerializeElementToHtml(DomElement element) => SerializeElementToHtml(element);
    void Dom.Features.IElementContentHost.SetElementInnerHtml(DomElement element, string html) => SetElementInnerHtml(element, html);
    void Dom.Features.IElementContentHost.SetElementOuterHtml(DomElement element, string html) => SetElementOuterHtml(element, html);

    // The two answers the contract asks for, which is what NodeTextOrNull has always returned: the
    // text, or null for a node that has none — a document or a doctype.
    string? Dom.Features.IElementContentHost.NodeTextValue(DomNode node) => NodeTextOrNull(node);

    void Dom.Features.IElementContentHost.SetElementTextContent(DomElement element, string? value) => SetElementTextContent(element, value);
}
