using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The host surface <see cref="ElementContentBinding"/> needs from the bridge for the element-content IDL
/// members: HTML serialization of the element and its children (<c>outerHTML</c>/<c>innerHTML</c> getters)
/// and the fragment-reparsing setters (<c>innerHTML</c>/<c>outerHTML</c>). These live on the bridge because
/// they route through the shared HTML parser/serializer and the canonical tree mutation with its
/// style-scope invalidation. The text-content members (<c>textContent</c>/<c>innerText</c>/<c>outerText</c>)
/// need nothing from it: they are the canonical <see cref="DomNode.TextContent"/>, which the binding reads
/// and writes on the element it already holds.
/// </summary>
/// <remarks>
/// The contract names no engine type. <see cref="Realm"/> is here only because
/// <see cref="ElementContentBinding.InstallTextContent"/> reads the realm off the host instead of taking
/// one like its two siblings; its caller, WrapNode, holds that same realm, as the factory before it did.
/// (This said the caller was an engine-typed wrapper factory with no realm to hand over.)
/// </remarks>
internal interface IElementContentHost
{
    /// <summary>The realm the content members are installed in and run against.</summary>
    IJsRealm Realm { get; }

    string SerializeChildrenToHtml(DomElement element);
    string SerializeElementToHtml(DomElement element);
    void SetElementInnerHtml(DomElement element, string html);
    void SetElementOuterHtml(DomElement element, string html);
}
