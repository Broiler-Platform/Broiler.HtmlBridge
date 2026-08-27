using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.Dom;

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
internal static class ElementContentBinding
{
    /// <summary>
    /// Installs the HTML-serialization members: <c>innerHTML</c> and <c>outerHTML</c> (read/write).
    /// Both are <c>Element</c>'s, so they go on its prototype.
    /// </summary>
    public static void InstallHtmlSerialization(IElementContentHost host, JSObject target, ElementSource element)
    {
        // innerHTML (read/write)
        target.FastAddProperty("innerHTML",
            new DomFunction((in a) => new JSString(host.SerializeChildrenToHtml(element(in a, "innerHTML"))), "get innerHTML"),
            new DomFunction((in a) => SetInnerHtml(host, element(in a, "innerHTML"), in a), "set innerHTML"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // outerHTML (read/write)
        target.FastAddProperty("outerHTML",
            new DomFunction((in a) => new JSString(host.SerializeElementToHtml(element(in a, "outerHTML"))), "get outerHTML"),
            new DomFunction((in a) => SetOuterHtml(host, element(in a, "outerHTML"), in a), "set outerHTML"),
            JSPropertyAttributes.EnumerableConfigurableProperty);
    }

    /// <summary>
    /// <c>textContent</c> (read/write), which stays each element wrapper's own: it is <c>Node</c>'s
    /// member, and an element's operation — read the descendants' text, and on write replace every
    /// child with one text node — differs from the character-data one already on
    /// <c>Node.prototype</c>, so it shadows that one until a single implementation serves both.
    /// </summary>
    public static void InstallTextContent(IElementContentHost host, JSObject obj, DomElement element)
    {
        obj.FastAddProperty("textContent",
            new DomFunction((in _) => host.GetNodeTextValue(element), "get textContent"),
            new DomFunction((in a) => SetTextContent(host, element, in a), "set textContent"),
            JSPropertyAttributes.EnumerableConfigurableProperty);
    }

    /// <summary>
    /// <c>innerText</c> and <c>outerText</c> (read-only), which are <c>HTMLElement</c>'s and go on its
    /// prototype.
    /// </summary>
    public static void InstallHtmlElementMembers(IElementContentHost host, JSObject target, ElementSource element)
    {
        target.FastAddProperty("innerText",
            new DomFunction((in a) => host.GetNodeTextValue(element(in a, "innerText")), "get innerText"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        target.FastAddProperty("outerText",
            new DomFunction((in a) => host.GetNodeTextValue(element(in a, "outerText")), "get outerText"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
    }

    private static JSValue SetInnerHtml(IElementContentHost host, DomElement element, in Arguments a)
    {
        host.SetElementInnerHtml(element, a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }

    private static JSValue SetOuterHtml(IElementContentHost host, DomElement element, in Arguments a)
    {
        host.SetElementOuterHtml(element, a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }

    private static JSValue SetTextContent(IElementContentHost host, DomElement element, in Arguments a)
    {
        // Setting textContent replaces all children with a single text node per DOM spec.
        host.SetElementTextContent(element, a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }
}
