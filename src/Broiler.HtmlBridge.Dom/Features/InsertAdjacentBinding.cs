using Broiler.Dom;
using Broiler.Dom.Html;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The DOM <c>insertAdjacentElement</c> / <c>insertAdjacentText</c> / <c>insertAdjacentHTML</c> methods,
/// co-located as an HtmlBridge feature module (Phase 3): each resolves the <c>beforebegin</c> /
/// <c>afterbegin</c> / <c>beforeend</c> / <c>afterend</c> position to a (parent, index) target and inserts
/// an element, a text node, or the parsed fragment there. Position parsing and target resolution
/// delegate to canonical <see cref="HtmlAdjacentPositionResolver"/> (Sprint 5.6 D6), raising the spec's
/// <c>SyntaxError</c> / <c>NoModificationAllowedError</c> through the realm.
/// </summary>
internal static class InsertAdjacentBinding
{
    /// <summary>
    /// Installs the insertAdjacent* methods on <paramref name="target"/>. All three are
    /// <c>Element</c>'s, so that is <c>Element.prototype</c>.
    /// </summary>
    public static void Install(IInsertAdjacentHost host, IJsRealm realm, JsValue target, JsElementSource element)
    {
        realm.DefineValue(target, "insertAdjacentElement",
            realm.NewMethod("insertAdjacentElement",
                (in call) => InsertAdjacentElement(host, element(in call, "insertAdjacentElement"), in call), 2));

        realm.DefineValue(target, "insertAdjacentText",
            realm.NewMethod("insertAdjacentText",
                (in call) => InsertAdjacentText(host, element(in call, "insertAdjacentText"), in call), 2));

        realm.DefineValue(target, "insertAdjacentHTML",
            realm.NewMethod("insertAdjacentHTML",
                (in call) => InsertAdjacentHtml(host, element(in call, "insertAdjacentHTML"), in call), 2));
    }

    private static JsValue InsertAdjacentElement(IInsertAdjacentHost host, DomElement element, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Null;
        var position = ParsePosition(call.Realm, call[0]);
        if (!call[1].IsObject)
            return JsValue.Null;
        var adjacentElement = host.FindElement(call[1]);
        if (adjacentElement == null)
            return JsValue.Null;
        var (parent, index) = GetInsertAdjacentTarget(call.Realm, element, position);
        host.InsertNodeAt(parent, adjacentElement, index);
        return call[1];
    }

    private static JsValue InsertAdjacentText(IInsertAdjacentHost host, DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;
        var position = ParsePosition(call.Realm, call[0]);
        var text = call.Length > 1 ? call.Realm.ToJsString(call[1]) : string.Empty;
        var (parent, index) = GetInsertAdjacentTarget(call.Realm, element, position);
        var textNode = host.CreateBridgeTextNode(text);
        host.InsertNodeAt(parent, textNode, index);
        return JsValue.Undefined;
    }

    private static JsValue InsertAdjacentHtml(IInsertAdjacentHost host, DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;
        var realm = call.Realm;
        var position = ParsePosition(realm, call[0]);
        var html = call.Length > 1 ? realm.ToJsString(call[1]) : string.Empty;
        if (string.IsNullOrEmpty(html))
            return JsValue.Undefined;

        DomElement parsingContext;
        try
        {
            parsingContext = HtmlAdjacentPositionResolver.ResolveParsingContext(element, position);
        }
        catch (DomException ex) when (ex.Name == "NoModificationAllowedError")
        {
            throw realm.DomError("NoModificationAllowedError", "Cannot insert adjacent HTML without a parent node.");
        }

        var (parent, index) = GetInsertAdjacentTarget(realm, element, position);
        var nodes = host.BuildAdjacentHtmlNodes(parsingContext, html);
        foreach (var node in nodes)
            host.InsertNodeAt(parent, node, index++);
        host.ResetComputedStyleEngines();
        return JsValue.Undefined;
    }

    private static HtmlAdjacentPosition ParsePosition(IJsRealm realm, JsValue value)
    {
        var raw = realm.ToJsString(value);
        if (HtmlAdjacentPositionResolver.TryParse(raw, out var position))
            return position;

        var positionStr = raw.Trim().ToLowerInvariant();
        throw realm.DomError("SyntaxError", $"'{positionStr}' is not a valid insertion position.");
    }

    private static (DomElement Parent, int Index) GetInsertAdjacentTarget(IJsRealm realm, DomElement element, HtmlAdjacentPosition position)
    {
        try
        {
            return HtmlAdjacentPositionResolver.ResolveTarget(element, position);
        }
        catch (DomException ex) when (ex.Name == "NoModificationAllowedError")
        {
            throw realm.DomError("NoModificationAllowedError", "Cannot insert adjacent content without a parent node.");
        }
        catch (DomException ex) when (ex.Name == "SyntaxError")
        {
            throw realm.DomError("SyntaxError", ex.Message);
        }
    }
}

