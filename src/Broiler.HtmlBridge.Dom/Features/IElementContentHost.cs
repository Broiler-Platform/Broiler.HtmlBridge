using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The host surface <see cref="ElementContentBinding"/> needs from the bridge for the element-content IDL
/// members: HTML serialization of the element and its children (<c>outerHTML</c>/<c>innerHTML</c> getters),
/// the fragment-reparsing setters (<c>innerHTML</c>/<c>outerHTML</c>), and the text-content read/write
/// (<c>textContent</c>/<c>innerText</c>/<c>outerText</c> read; <c>textContent</c> write). These all live on
/// the bridge because they route through the shared HTML parser/serializer and the canonical tree mutation
/// with its style-scope invalidation and mutation-observer notifications.
/// </summary>
/// <remarks>
/// The contract names no engine type. Its two changes of vocabulary are both the same move — say what is
/// wanted, not which engine produces it. <see cref="NodeTextValue"/> answers a CLR string instead of an
/// engine string value: the two answers the DOM algorithm actually has are "this text" and "no text at
/// all" (§4.4 gives a document and a doctype the latter), and a <see langword="null"/> string carries
/// both, since <see cref="JsValue.String(string?)"/> is defined to make one <c>null</c>. And
/// <see cref="Realm"/> is here because <see cref="ElementContentBinding.InstallTextContent"/> is still
/// called from the engine-typed wrapper factory and so has no realm of its own to install through; see
/// the remarks there.
/// </remarks>
internal interface IElementContentHost
{
    /// <summary>The realm the content members are installed in and run against.</summary>
    IJsRealm Realm { get; }

    string SerializeChildrenToHtml(DomElement element);
    string SerializeElementToHtml(DomElement element);
    void SetElementInnerHtml(DomElement element, string html);
    void SetElementOuterHtml(DomElement element, string html);

    /// <summary>
    /// The node's <c>textContent</c>, or <see langword="null"/> for the two node kinds DOM §4.4 gives
    /// no text — a document and a doctype.
    /// </summary>
    string? NodeTextValue(DomNode node);

    void SetElementTextContent(DomElement element, string? value);
}
