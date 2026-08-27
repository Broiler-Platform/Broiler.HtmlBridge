using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The DOM <c>insertAdjacentElement</c> / <c>insertAdjacentText</c> / <c>insertAdjacentHTML</c> methods,
/// co-located as an HtmlBridge feature module (Phase 3): each resolves the <c>beforebegin</c> /
/// <c>afterbegin</c> / <c>beforeend</c> / <c>afterend</c> position to a (parent, index) target and inserts
/// an element, a text node, or the parsed fragment there. The position-normalisation and target-resolution
/// helpers move here with the methods (they had no other consumer); they raise the spec's <c>SyntaxError</c>
/// / <c>NoModificationAllowedError</c> via <c>DomBridge.ThrowDOMException</c> and navigate with the bridge's
/// neutral <c>internal static</c> <c>ParentEl</c>/<c>ChildIndexOf</c> helpers, while the JS context, reverse
/// lookup, insertion primitive, text-node factory, fragment parser and computed-style reset come through the
/// <see cref="IInsertAdjacentHost"/> contract. Was the bridge's
/// <c>JsJsObjectsInsertAdjacentElement130Core</c>/<c>InsertAdjacentText131Core</c>/<c>InsertAdjacentHTML132Core</c>.
/// </summary>
internal static class InsertAdjacentBinding
{
    /// <summary>
    /// Installs the insertAdjacent* methods on <paramref name="target"/>. All three are
    /// <c>Element</c>'s, so that is <c>Element.prototype</c>.
    /// </summary>
    public static void Install(IInsertAdjacentHost host, JSObject target, ElementSource element)
    {
        target.FastAddValue("insertAdjacentElement",
            new DomFunction((in a) => InsertAdjacentElement(host, element(in a, "insertAdjacentElement"), in a), "insertAdjacentElement", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        target.FastAddValue("insertAdjacentText",
            new DomFunction((in a) => InsertAdjacentText(host, element(in a, "insertAdjacentText"), in a), "insertAdjacentText", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        target.FastAddValue("insertAdjacentHTML",
            new DomFunction((in a) => InsertAdjacentHtml(host, element(in a, "insertAdjacentHTML"), in a), "insertAdjacentHTML", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);
    }

    private static JSValue InsertAdjacentElement(IInsertAdjacentHost host, DomElement element, in Arguments a)
    {
        if (a.Length < 2)
            return JSNull.Value;
        var position = NormalizeInsertAdjacentPosition(host, a[0]);
        if (a[1] is not JSObject adjacentObject)
            return JSNull.Value;
        var adjacentElement = host.FindDomElementByJSObject(adjacentObject);
        if (adjacentElement == null)
            return JSNull.Value;
        var (parent, index) = GetInsertAdjacentTarget(host, element, position);
        host.InsertNodeAt(parent, adjacentElement, index);
        return a[1];
    }

    private static JSValue InsertAdjacentText(IInsertAdjacentHost host, DomElement element, in Arguments a)
    {
        if (a.Length == 0)
            return JSUndefined.Value;
        var position = NormalizeInsertAdjacentPosition(host, a[0]);
        var text = a.Length > 1 ? a[1].ToString() : string.Empty;
        var (parent, index) = GetInsertAdjacentTarget(host, element, position);
        var textNode = host.CreateBridgeTextNode(text);
        host.InsertNodeAt(parent, textNode, index);
        return JSUndefined.Value;
    }

    private static JSValue InsertAdjacentHtml(IInsertAdjacentHost host, DomElement element, in Arguments a)
    {
        if (a.Length == 0)
            return JSUndefined.Value;
        var position = NormalizeInsertAdjacentPosition(host, a[0]);
        var html = a.Length > 1 ? a[1].ToString() : string.Empty;
        if (string.IsNullOrEmpty(html))
            return JSUndefined.Value;
        DomElement parsingContext;
        switch (position)
        {
            case "beforebegin":
            case "afterend":
                if (DomBridge.ParentEl(element) == null)
                    DomBridge.ThrowDOMException(host.JsContext!, "Cannot insert adjacent HTML without a parent node.", "NoModificationAllowedError");
                parsingContext = DomBridge.ParentEl(element)!;
                break;
            default:
                parsingContext = element;
                break;
        }

        var (parent, index) = GetInsertAdjacentTarget(host, element, position);
        var nodes = host.BuildAdjacentHtmlNodes(parsingContext, html);
        foreach (var node in nodes)
            host.InsertNodeAt(parent, node, index++);
        host.ResetComputedStyleEngines();
        return JSUndefined.Value;
    }

    // Validates and lower-cases the insertion position, raising SyntaxError for an unknown value.
    private static string NormalizeInsertAdjacentPosition(IInsertAdjacentHost host, JSValue? value)
    {
        var position = value?.ToString().Trim().ToLowerInvariant() ?? string.Empty;
        if (position is "beforebegin" or "afterbegin" or "beforeend" or "afterend")
            return position;

        DomBridge.ThrowDOMException(host.JsContext!, $"'{position}' is not a valid insertion position.", "SyntaxError");
        return string.Empty;
    }

    // Resolves the position keyword to the (parent, insertion index) pair, raising
    // NoModificationAllowedError when a beforebegin/afterend insertion has no parent.
    private static (DomElement Parent, int Index) GetInsertAdjacentTarget(IInsertAdjacentHost host, DomElement element, string position)
    {
        switch (position)
        {
            case "beforebegin":
                if (DomBridge.ParentEl(element) == null)
                    DomBridge.ThrowDOMException(host.JsContext!, "Cannot insert adjacent content without a parent node.", "NoModificationAllowedError");
                return (DomBridge.ParentEl(element)!, DomBridge.ChildIndexOf(DomBridge.ParentEl(element)!, element));
            case "afterbegin":
                return (element, 0);
            case "beforeend":
                return (element, element.ChildNodes.Count);
            case "afterend":
                if (DomBridge.ParentEl(element) == null)
                    DomBridge.ThrowDOMException(host.JsContext!, "Cannot insert adjacent content without a parent node.", "NoModificationAllowedError");
                return (DomBridge.ParentEl(element)!, DomBridge.ChildIndexOf(DomBridge.ParentEl(element)!, element) + 1);
            default:
                DomBridge.ThrowDOMException(host.JsContext!, $"'{position}' is not a valid insertion position.", "SyntaxError");
                return (element, element.ChildNodes.Count);
        }
    }
}
