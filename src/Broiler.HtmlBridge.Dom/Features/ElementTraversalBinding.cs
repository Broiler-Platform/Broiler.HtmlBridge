using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Phase 3 feature module for the DOM element-traversal accessors — <c>children</c>,
/// <c>firstElementChild</c>, <c>lastElementChild</c>, <c>nextElementSibling</c> and
/// <c>previousElementSibling</c> (the element-only siblings of the P3.41 node accessors). These were the
/// bridge's <c>JsJsObjectsGetChildren081Core</c>..<c>GetPreviousElementSibling086Core</c> callbacks; only
/// the realm and the JS-wrapper factory reach the bridge, through the two-member
/// <see cref="IElementTraversalHost"/> contract, while the element-child enumeration
/// (<c>ChildElements</c>), the element-parent walk (<c>ParentEl</c>) and the text-node test
/// (<c>IsText</c>) are the bridge's <c>internal static</c> helpers, called directly.
/// </summary>
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type: an absent sibling is <see cref="JsValue.Null"/> and <c>children</c>'s Array is minted by the
/// realm rather than constructed.
/// </para>
/// <para>
/// None of these five reads an argument — they are IDL attributes, and a getter's call frame carries
/// nothing they want — so each takes the element it operates on and no call at all. Their one caller,
/// <c>DomBridge/ElementInterface.cs</c>, mints the five getters through the realm and gets that element
/// from its <c>JsElementSource</c>. (This said that site had not migrated and passed an engine frame.)
/// </para>
/// </remarks>
internal static class ElementTraversalBinding
{
    public static JsValue GetChildren(IElementTraversalHost host, DomElement element)
    {
        var result = new List<JsValue>();
        foreach (var child in DomBridgeUtils.ChildElements(element))
        {
            if (!DomBridgeUtils.IsText(child))
                result.Add(host.ToWrapper(child));
        }

        return host.Realm.NewArray([.. result]);
    }

    public static JsValue GetFirstElementChild(IElementTraversalHost host, DomElement element)
    {
        var first = DomBridgeUtils.ChildElements(element).FirstOrDefault(c => !DomBridgeUtils.IsText(c));
        return first != null ? host.ToWrapper(first) : JsValue.Null;
    }

    public static JsValue GetLastElementChild(IElementTraversalHost host, DomElement element)
    {
        var last = DomBridgeUtils.ChildElements(element).LastOrDefault(c => !DomBridgeUtils.IsText(c));
        return last != null ? host.ToWrapper(last) : JsValue.Null;
    }

    public static JsValue GetNextElementSibling(IElementTraversalHost host, DomElement element)
    {
        if (DomBridgeUtils.ParentEl(element) == null)
            return JsValue.Null;
        var siblings = DomBridgeUtils.ChildElements(DomBridgeUtils.ParentEl(element)).ToList();
        var idx = siblings.IndexOf(element);
        for (var i = idx + 1; i < siblings.Count; i++)
        {
            if (!DomBridgeUtils.IsText(siblings[i]))
                return host.ToWrapper(siblings[i]);
        }

        return JsValue.Null;
    }

    public static JsValue GetPreviousElementSibling(IElementTraversalHost host, DomElement element)
    {
        if (DomBridgeUtils.ParentEl(element) == null)
            return JsValue.Null;
        var siblings = DomBridgeUtils.ChildElements(DomBridgeUtils.ParentEl(element)).ToList();
        var idx = siblings.IndexOf(element);
        for (var i = idx - 1; i >= 0; i--)
        {
            if (!DomBridgeUtils.IsText(siblings[i]))
                return host.ToWrapper(siblings[i]);
        }

        return JsValue.Null;
    }
}
