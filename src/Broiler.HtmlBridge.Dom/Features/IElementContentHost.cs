using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The host surface <see cref="ElementContentBinding"/> needs from the bridge for the element-content IDL
/// members: HTML serialization of the element and its children (<c>outerHTML</c>/<c>innerHTML</c> getters)
/// and the mutation setters (<c>innerHTML</c>/<c>outerHTML</c>/<c>innerText</c>/<c>outerText</c>). These live on the bridge because
/// they route through the shared HTML parser/serializer or rendered text fragment generation and canonical tree mutation with
/// style-scope invalidation. <c>textContent</c> reads and writes canonical <see cref="DomNode.TextContent"/> directly.
/// </summary>
/// <remarks>
/// The contract names no engine type. <see cref="IRealmHost.Realm"/> is here only because
/// <see cref="ElementContentBinding.InstallTextContent"/> reads the realm off the host instead of taking
/// one like its two siblings; its caller, WrapNode, holds that same realm.
/// </remarks>
internal interface IElementContentHost : IRealmHost
{
    string SerializeChildrenToHtml(DomElement element);
    string SerializeElementToHtml(DomElement element);
    void SetElementInnerHtml(DomElement element, string html);
    void SetElementOuterHtml(DomElement element, string html);
    void SetElementInnerText(DomElement element, string text);
    void SetElementOuterText(DomElement element, string text);
}
