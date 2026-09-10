using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The element-content IDL members, co-located as an HtmlBridge feature module (Phase 3): the HTML
/// serialization pair <c>innerHTML</c> / <c>outerHTML</c> (read serializes, write reparses a fragment) and
/// the text-content trio <c>textContent</c> / <c>innerText</c> / <c>outerText</c> (read returns the node's
/// text value; only <c>textContent</c> is writable, replacing all children with a single text node). Every
/// operation routes through the bridge's shared parser/serializer and canonical tree mutation, reached
/// through the <see cref="IElementContentHost"/> contract. The two entry points are now the two
/// interfaces: the serialization pair is <c>Element</c>'s and lives on its prototype, while
/// <c>textContent</c> (<c>Node</c>'s, deliberately shadowed here because an element's operation differs
/// from a character-data node's) and the two <c>HTMLElement</c> text members stay on each wrapper. The
/// split was originally made to keep the unrelated <c>shadowRoot</c> accessor in its position between
/// them. Was the bridge's inline <c>innerHTML</c>/<c>outerHTML</c>/<c>textContent</c>/
/// <c>innerText</c>/<c>outerText</c> registration plus the <c>JsJsObjectsSetInnerHTML016Core</c>/
/// <c>SetOuterHTML018Core</c>/<c>SetTextContent021Core</c> callbacks.
/// </summary>
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>): the members are minted by the realm,
/// their bodies read a <see cref="JsCall"/>, and the three setters coerce with
/// <see cref="IJsValues.ToJsString"/> — which is what the engine did before, and what the handle's own
/// <c>ToString</c> deliberately does not do. <c>innerHTML = someObject</c> running that object's
/// <c>toString</c> is the whole of how a templating library hands over a fragment.
/// </para>
/// <para>
/// <b><see cref="InstallTextContent"/> is the one member still shaped by its caller.</b> It is reached
/// from the wrapper factory (<c>DomBridge/JsObjects.cs</c>), which has not migrated and holds an engine
/// object, so the parameter is one — the seam unwraps it, and the realm comes off the host rather than
/// off a caller that has none to give. The member it installs is built by the realm like the other four.
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
    /// member, and an element's operation — read the descendants' text, and on write replace every
    /// child with one text node — differs from the character-data one already on
    /// <c>Node.prototype</c>, so it shadows that one until a single implementation serves both.
    /// </summary>
    /// <param name="target">The element's JS wrapper, as the factory that calls this now holds it.</param>
    public static void InstallTextContent(IElementContentHost host, JsValue target, DomElement element)
    {
        var realm = host.Realm;

        realm.DefineAccessor(target, "textContent",
            (in _) => JsValue.String(host.NodeTextValue(element)),
            (in call) => SetTextContent(host, element, in call));
    }

    /// <summary>
    /// <c>innerText</c> and <c>outerText</c> (read-only), which are <c>HTMLElement</c>'s and go on its
    /// prototype.
    /// </summary>
    public static void InstallHtmlElementMembers(IElementContentHost host, IJsRealm realm, JsValue target, JsElementSource element)
    {
        realm.DefineAccessor(target, "innerText",
            (in call) => JsValue.String(host.NodeTextValue(element(in call, "innerText"))), null);

        realm.DefineAccessor(target, "outerText",
            (in call) => JsValue.String(host.NodeTextValue(element(in call, "outerText"))), null);
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

    private static JsValue SetTextContent(IElementContentHost host, DomElement element, in JsCall call)
    {
        // Setting textContent replaces all children with a single text node per DOM spec.
        host.SetElementTextContent(element, StringArgument(in call));
        return JsValue.Undefined;
    }

    /// <summary>
    /// Argument zero as a string — the ECMAScript coercion, which may run a <c>toString</c> the page
    /// wrote — or the empty string when the setter was called with no argument at all.
    /// </summary>
    private static string StringArgument(in JsCall call)
        => call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
}
