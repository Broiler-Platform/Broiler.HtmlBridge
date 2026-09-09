using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.BuiltIns.String;

namespace Broiler.HtmlBridge;

// Explicit IElementContentHost implementation for the ElementContentBinding feature module (Phase 3): the
// innerHTML/outerHTML/textContent members route through the bridge's shared HTML parser/serializer and
// canonical tree mutation, so each forwards to the existing private serialize/set helpers.
//
// This file is the engine-typed half of the seam and one member is still on it. The bridge's own
// GetNodeTextValue (DomBridge/JsFunctionCallbacks/Common.cs) answers an engine string or engine null, and
// it is shared with the wrappers that have not migrated; the contract asks for the two answers rather
// than for the engine's rendering of them, so this is where the one is read as the other. It stops
// naming an engine type when that helper does.
public sealed partial class DomBridge : Dom.Features.IElementContentHost
{
    IJsRealm Dom.Features.IElementContentHost.Realm => Realm;

    string Dom.Features.IElementContentHost.SerializeChildrenToHtml(DomElement element) => SerializeChildrenToHtml(element);
    string Dom.Features.IElementContentHost.SerializeElementToHtml(DomElement element) => SerializeElementToHtml(element);
    void Dom.Features.IElementContentHost.SetElementInnerHtml(DomElement element, string html) => SetElementInnerHtml(element, html);
    void Dom.Features.IElementContentHost.SetElementOuterHtml(DomElement element, string html) => SetElementOuterHtml(element, html);

    // A JSString is the "this text" answer and anything else — which is only ever the engine's null,
    // for a document or a doctype — is the "no text at all" one. ToString() on a JSString is the
    // string it holds and enters nothing, so the trap that makes the coercion an engine operation
    // elsewhere does not apply.
    string? Dom.Features.IElementContentHost.NodeTextValue(DomNode node) =>
        GetNodeTextValue(node) is JSString text ? text.ToString() : null;

    void Dom.Features.IElementContentHost.SetElementTextContent(DomElement element, string? value) => SetElementTextContent(element, value);
}
