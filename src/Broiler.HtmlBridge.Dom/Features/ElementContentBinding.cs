using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The element-content IDL members, co-located as an HtmlBridge feature module: the HTML
/// serialization pair <c>innerHTML</c> / <c>outerHTML</c> (read serializes, write reparses a fragment) and
/// the text-content trio <c>textContent</c> / <c>innerText</c> / <c>outerText</c> (read returns the element's
/// descendant text; only <c>textContent</c> is writable, replacing all children with a single text node).
/// The serialization pair routes through the bridge's shared parser/serializer, reached through the
/// <see cref="IElementContentHost"/> contract; the text trio is the canonical
/// <see cref="DomNode.TextContent"/> read and written directly. The three entry points follow the
/// interfaces: the serialization pair is <c>Element</c>'s and goes on <c>Element.prototype</c>,
/// <c>textContent</c> (<c>Node</c>'s, shadowed here) stays on each wrapper, and the two
/// <c>HTMLElement</c> text members go on <c>HTMLElement.prototype</c>. A wrapper that cannot inherit
/// one of those prototypes (one minted before the realm carried it or, for <c>HTMLElement</c>'s, a
/// non-HTML element) carries those members itself. The <c>shadowRoot</c> accessor's position between
/// the serialization pair and the text members is decided in <c>DomBridge/ElementInterface.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>): the members are minted by the realm,
/// their bodies read a <see cref="JsCall"/>, and the two markup setters coerce with
/// <see cref="IJsValues.ToJsString"/> rather than with the handle's own <c>ToString</c>, which
/// deliberately does not coerce. <c>innerHTML = someObject</c> running that object's
/// <c>toString</c> is the whole of how a templating library hands over a fragment.
/// </para>
/// <para>
/// <b><c>textContent</c> is a nullable <c>DOMString</c> and the two markup members are not.</b> Its
/// setter is <see cref="NodeAccessorsBinding.SetTextContent"/>, the one every node kind uses, which
/// passes <c>null</c> and <c>undefined</c> through as IDL null, so <c>el.textContent = null</c> leaves no
/// children at all. Coercing it like the other two would leave one text node reading
/// <c>"null"</c>. The markup setters do coerce, so <c>innerHTML = null</c> parses the string
/// <c>"null"</c>, and that is a known difference from Chromium rather than parity: the two are
/// <c>[LegacyNullToEmptyString]</c>, so a browser reads <c>null</c> as the empty string and
/// <c>el.innerHTML = null</c> leaves the element empty.
/// </para>
/// <para>
/// <b><see cref="InstallTextContent"/> takes the wrapper handle and reads the realm off the host.</b>
/// It is reached from the wrapper factory (<c>DomBridge/JsObjects.cs</c>), which hands it the handle it
/// minted; the member it installs is built by the realm like the other four.
/// </para>
/// </remarks>
internal static class ElementContentBinding
{
    /// <summary>
    /// Installs the HTML-serialization members: <c>innerHTML</c> and <c>outerHTML</c> (read/write).
    /// Both are <c>Element</c>'s, so they go on its prototype.
    /// </summary>
    public static void InstallHtmlSerialization(IElementContentHost host, IJsRealm realm, JsValue target, JsElementSource element)
    {
        // innerHTML (read/write)
        realm.DefineAccessor(target, "innerHTML",
            (in call) => JsValue.String(host.SerializeChildrenToHtml(element(in call, "innerHTML"))),
            (in call) => SetInnerHtml(host, element(in call, "innerHTML"), in call));

        // outerHTML (read/write)
        realm.DefineAccessor(target, "outerHTML",
            (in call) => JsValue.String(host.SerializeElementToHtml(element(in call, "outerHTML"))),
            (in call) => SetOuterHtml(host, element(in call, "outerHTML"), in call));
    }

    /// <summary>
    /// <c>textContent</c> (read/write), which stays each element wrapper's own: it is <c>Node</c>'s
    /// member, and it shadows the one on <c>Node.prototype</c>. It was installed because an element's
    /// operation — read the descendants' text, and on write replace every child with one text node —
    /// differed from the character-data one there. Both are the canonical <see cref="DomNode.TextContent"/>
    /// now, so an element reads the same answer through either; this one never needs the document and
    /// doctype <c>null</c> the prototype getter maps, since an element is neither.
    /// </summary>
    /// <param name="target">The element's JS wrapper, as the factory that calls this now holds it.</param>
    public static void InstallTextContent(IElementContentHost host, JsValue target, DomElement element)
    {
        var realm = host.Realm;

        realm.DefineAccessor(target, "textContent",
            (in _) => JsValue.String(element.TextContent),
            (in call) => NodeAccessorsBinding.SetTextContent(element, in call));
    }

    /// <summary>
    /// <c>innerText</c> and <c>outerText</c> (read-only), which are <c>HTMLElement</c>'s and go on its
    /// prototype.
    /// </summary>
    public static void InstallHtmlElementMembers(IJsRealm realm, JsValue target, JsElementSource element)
    {
        realm.DefineAccessor(target, "innerText",
            (in call) => JsValue.String(element(in call, "innerText").TextContent), null);

        realm.DefineAccessor(target, "outerText",
            (in call) => JsValue.String(element(in call, "outerText").TextContent), null);
    }

    private static JsValue SetInnerHtml(IElementContentHost host, DomElement element, in JsCall call)
    {
        host.SetElementInnerHtml(element, StringArgument(in call));
        return JsValue.Undefined;
    }

    private static JsValue SetOuterHtml(IElementContentHost host, DomElement element, in JsCall call)
    {
        host.SetElementOuterHtml(element, StringArgument(in call));
        return JsValue.Undefined;
    }

    /// <summary>
    /// Argument zero as a string — the ECMAScript coercion, which may run a <c>toString</c> the page
    /// wrote — or the empty string when the setter was called with no argument at all.
    /// </summary>
    private static string StringArgument(in JsCall call)
        => call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
}
