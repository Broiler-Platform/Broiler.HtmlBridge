using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The feature module for the DOM element-traversal accessors — <c>children</c>,
/// <c>firstElementChild</c>, <c>lastElementChild</c>, <c>nextElementSibling</c> and
/// <c>previousElementSibling</c>, the element-only siblings of the node accessors. Only
/// the realm and the JS-wrapper factory reach the bridge, through the two-member
/// <see cref="IElementTraversalHost"/> contract, while the views themselves are the canonical
/// <see cref="DomNode"/> members (<c>ChildElements</c>, <c>FirstElementChild</c>, <c>LastElementChild</c>,
/// <c>NextElementSibling</c>, <c>PreviousElementSibling</c>), read directly.
/// </summary>
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type: an absent sibling is <see cref="JsValue.Null"/> and <c>children</c>'s Array is minted by the
/// realm rather than constructed.
/// </para>
/// <para>
/// None of these five reads an argument — they are IDL attributes, and a getter's call frame carries
/// nothing they want — so each takes the node it operates on and no call at all. Their callers,
/// <c>DomBridge/ElementInterface.cs</c> and the fragment wrapper in
/// <c>DomBridge/JsObjects.NonElementNodes.cs</c>, mint the getters through the realm.
/// </para>
/// <para>
/// <b>The three <c>ParentNode</c> views take any node, and the two siblings an element.</b> A fragment
/// has <c>children</c> and its first and last element child exactly as an element does, and its wrapper
/// reads them here. The siblings are found through the canonical parent <em>node</em>, so an element
/// whose parent is a fragment has element siblings too; the bridge's own walk went through the parent
/// element and answered null both ways under a fragment. The root element still has none, since a
/// document holds at most one element.
/// </para>
/// </remarks>
internal static class ElementTraversalBinding
{
    public static JsValue GetChildren(IElementTraversalHost host, DomNode parent)
    {
        var children = parent.ChildElements;
        var result = new JsValue[children.Count];
        for (var index = 0; index < children.Count; index++)
            result[index] = host.ToWrapper(children[index]);

        return host.Realm.NewArray(result);
    }

    public static JsValue GetFirstElementChild(IElementTraversalHost host, DomNode parent) =>
        Wrap(host, parent.FirstElementChild);

    public static JsValue GetLastElementChild(IElementTraversalHost host, DomNode parent) =>
        Wrap(host, parent.LastElementChild);

    public static JsValue GetNextElementSibling(IElementTraversalHost host, DomElement element) =>
        Wrap(host, element.NextElementSibling);

    public static JsValue GetPreviousElementSibling(IElementTraversalHost host, DomElement element) =>
        Wrap(host, element.PreviousElementSibling);

    private static JsValue Wrap(IElementTraversalHost host, DomElement? element) =>
        element != null ? host.ToWrapper(element) : JsValue.Null;
}
