using Broiler.HtmlBridge.Jseal;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The DOM <c>insertAdjacentElement</c> / <c>insertAdjacentText</c> / <c>insertAdjacentHTML</c> methods,
/// co-located as an HtmlBridge feature module (Phase 3): each resolves the <c>beforebegin</c> /
/// <c>afterbegin</c> / <c>beforeend</c> / <c>afterend</c> position to a (parent, index) target and inserts
/// an element, a text node, or the parsed fragment there. The position-normalisation and target-resolution
/// helpers move here with the methods (they had no other consumer); they raise the spec's <c>SyntaxError</c>
/// / <c>NoModificationAllowedError</c> through the realm and navigate with the bridge's neutral
/// <c>internal static</c> <c>ParentEl</c>/<c>ChildIndexOf</c> helpers, while the reverse lookup, insertion
/// primitive, text-node factory, fragment parser and computed-style reset come through the
/// <see cref="IInsertAdjacentHost"/> contract. Was the bridge's
/// <c>JsJsObjectsInsertAdjacentElement130Core</c>/<c>InsertAdjacentText131Core</c>/<c>InsertAdjacentHTML132Core</c>.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>): the three members are minted through
/// the realm and installed on <c>Element.prototype</c> in the position <see cref="Install"/> is called
/// in, so <c>Object.getOwnPropertyNames</c> reports the order it always did. Every argument read goes
/// through the realm's <c>ToString</c> rather than the handle's, because that is the coercion a page
/// observes — <c>insertAdjacentText(pos, {toString(){…}})</c> has always run the object's own
/// <c>toString</c> here — and the two <c>DOMException</c>s the position resolution raises come from
/// <c>IJsCalls.DomError</c>, which is what the script context the contract used to carry was for.
/// </remarks>
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
        var position = NormalizeInsertAdjacentPosition(call.Realm, call[0]);
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
        var position = NormalizeInsertAdjacentPosition(call.Realm, call[0]);
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
        var position = NormalizeInsertAdjacentPosition(realm, call[0]);
        var html = call.Length > 1 ? realm.ToJsString(call[1]) : string.Empty;
        if (string.IsNullOrEmpty(html))
            return JsValue.Undefined;
        DomElement parsingContext;
        switch (position)
        {
            case "beforebegin":
            case "afterend":
                if (DomBridge.ParentEl(element) == null)
                    throw realm.DomError("NoModificationAllowedError", "Cannot insert adjacent HTML without a parent node.");
                parsingContext = DomBridge.ParentEl(element)!;
                break;
            default:
                parsingContext = element;
                break;
        }

        var (parent, index) = GetInsertAdjacentTarget(realm, element, position);
        var nodes = host.BuildAdjacentHtmlNodes(parsingContext, html);
        foreach (var node in nodes)
            host.InsertNodeAt(parent, node, index++);
        host.ResetComputedStyleEngines();
        return JsValue.Undefined;
    }

    // Validates and lower-cases the insertion position, raising SyntaxError for an unknown value. The
    // realm's ToString, not the handle's: the position argument is coerced the way the page sees it,
    // which is what the engine frame this replaces was doing.
    private static string NormalizeInsertAdjacentPosition(IJsRealm realm, JsValue value)
    {
        var position = realm.ToJsString(value).Trim().ToLowerInvariant();
        if (position is "beforebegin" or "afterbegin" or "beforeend" or "afterend")
            return position;

        throw realm.DomError("SyntaxError", $"'{position}' is not a valid insertion position.");
    }

    // Resolves the position keyword to the (parent, insertion index) pair, raising
    // NoModificationAllowedError when a beforebegin/afterend insertion has no parent.
    private static (DomElement Parent, int Index) GetInsertAdjacentTarget(IJsRealm realm, DomElement element, string position)
    {
        switch (position)
        {
            case "beforebegin":
                if (DomBridge.ParentEl(element) == null)
                    throw realm.DomError("NoModificationAllowedError", "Cannot insert adjacent content without a parent node.");
                return (DomBridge.ParentEl(element)!, DomBridge.ChildIndexOf(DomBridge.ParentEl(element)!, element));
            case "afterbegin":
                return (element, 0);
            case "beforeend":
                return (element, element.ChildNodes.Count);
            case "afterend":
                if (DomBridge.ParentEl(element) == null)
                    throw realm.DomError("NoModificationAllowedError", "Cannot insert adjacent content without a parent node.");
                return (DomBridge.ParentEl(element)!, DomBridge.ChildIndexOf(DomBridge.ParentEl(element)!, element) + 1);
            default:
                throw realm.DomError("SyntaxError", $"'{position}' is not a valid insertion position.");
        }
    }
}
