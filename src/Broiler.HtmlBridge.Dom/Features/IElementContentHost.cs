using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The host surface <see cref="ElementContentBinding"/> needs from the bridge for the element-content IDL
/// members: HTML serialization of the element and its children (<c>outerHTML</c>/<c>innerHTML</c> getters),
/// the fragment-reparsing setters (<c>innerHTML</c>/<c>outerHTML</c>), and the text-content read/write
/// (<c>textContent</c>/<c>innerText</c>/<c>outerText</c> read; <c>textContent</c> write). These all live on
/// the bridge because they route through the shared HTML parser/serializer and the canonical tree mutation
/// with its style-scope invalidation and mutation-observer notifications.
/// </summary>
internal interface IElementContentHost
{
    string SerializeChildrenToHtml(DomElement element);
    string SerializeElementToHtml(DomElement element);
    void SetElementInnerHtml(DomElement element, string html);
    void SetElementOuterHtml(DomElement element, string html);
    JSValue GetNodeTextValue(DomNode node);
    void SetElementTextContent(DomElement element, string? value);
}
