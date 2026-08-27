using System.Collections.Generic;
using System.Linq;
using Broiler.Dom;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Array;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Phase 3 feature module for the DOM element-traversal accessors — <c>children</c>,
/// <c>firstElementChild</c>, <c>lastElementChild</c>, <c>nextElementSibling</c> and
/// <c>previousElementSibling</c> (the element-only siblings of the P3.41 node accessors). These were the
/// bridge's <c>JsJsObjectsGetChildren081Core</c>..<c>GetPreviousElementSibling086Core</c> callbacks; only
/// the JS-wrapper factory reaches the bridge, through the one-member <see cref="IElementTraversalHost"/>
/// contract, while the element-child enumeration (<c>ChildElements</c>), the element-parent walk
/// (<c>ParentEl</c>) and the text-node test (<c>IsText</c>) are the bridge's <c>internal static</c>
/// helpers, called directly.
/// </summary>
internal static class ElementTraversalBinding
{
    public static JSValue GetChildren(IElementTraversalHost host, DomElement element, in Arguments a)
    {
        var result = new List<JSValue>();
        foreach (var child in DomBridge.ChildElements(element))
        {
            if (!DomBridge.IsText(child))
                result.Add(host.ToJSObject(child));
        }

        return new JSArray(result);
    }

    public static JSValue GetFirstElementChild(IElementTraversalHost host, DomElement element, in Arguments a)
    {
        var first = DomBridge.ChildElements(element).FirstOrDefault(c => !DomBridge.IsText(c));
        return first != null ? host.ToJSObject(first) : JSNull.Value;
    }

    public static JSValue GetLastElementChild(IElementTraversalHost host, DomElement element, in Arguments a)
    {
        var last = DomBridge.ChildElements(element).LastOrDefault(c => !DomBridge.IsText(c));
        return last != null ? host.ToJSObject(last) : JSNull.Value;
    }

    public static JSValue GetNextElementSibling(IElementTraversalHost host, DomElement element, in Arguments a)
    {
        if (DomBridge.ParentEl(element) == null)
            return JSNull.Value;
        var siblings = DomBridge.ChildElements(DomBridge.ParentEl(element)).ToList();
        var idx = siblings.IndexOf(element);
        for (var i = idx + 1; i < siblings.Count; i++)
        {
            if (!DomBridge.IsText(siblings[i]))
                return host.ToJSObject(siblings[i]);
        }

        return JSNull.Value;
    }

    public static JSValue GetPreviousElementSibling(IElementTraversalHost host, DomElement element, in Arguments a)
    {
        if (DomBridge.ParentEl(element) == null)
            return JSNull.Value;
        var siblings = DomBridge.ChildElements(DomBridge.ParentEl(element)).ToList();
        var idx = siblings.IndexOf(element);
        for (var i = idx - 1; i >= 0; i--)
        {
            if (!DomBridge.IsText(siblings[i]))
                return host.ToJSObject(siblings[i]);
        }

        return JSNull.Value;
    }
}
