using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

// Explicit IElementContentHost implementation for the ElementContentBinding feature module (Phase 3): the
// innerHTML/outerHTML/textContent members route through the bridge's shared HTML parser/serializer and
// canonical tree mutation, so each forwards to the existing private serialize/set helpers.
public sealed partial class DomBridge : Dom.Features.IElementContentHost
{
    string Dom.Features.IElementContentHost.SerializeChildrenToHtml(DomElement element) => SerializeChildrenToHtml(element);
    string Dom.Features.IElementContentHost.SerializeElementToHtml(DomElement element) => SerializeElementToHtml(element);
    void Dom.Features.IElementContentHost.SetElementInnerHtml(DomElement element, string html) => SetElementInnerHtml(element, html);
    void Dom.Features.IElementContentHost.SetElementOuterHtml(DomElement element, string html) => SetElementOuterHtml(element, html);
    JSValue Dom.Features.IElementContentHost.GetNodeTextValue(DomNode node) => GetNodeTextValue(node);
    void Dom.Features.IElementContentHost.SetElementTextContent(DomElement element, string? value) => SetElementTextContent(element, value);
}
