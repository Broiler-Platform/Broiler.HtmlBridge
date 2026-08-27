using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The HTML/CSSOM element box-model and scrolling interface, co-located as an HtmlBridge feature module
/// (Phase 3): the box metrics (<c>clientTop</c>/<c>clientLeft</c>/<c>clientWidth</c>/<c>clientHeight</c>,
/// <c>offsetWidth</c>/<c>offsetHeight</c>, <c>scrollWidth</c>/<c>scrollHeight</c>, <c>offsetTop</c>/
/// <c>offsetLeft</c>, <c>offsetParent</c>, <c>getBoundingClientRect</c>/<c>getClientRects</c>) and the
/// imperative scrolling API (<c>scrollTop</c>/<c>scrollLeft</c> get/set, <c>scroll</c>/<c>scrollTo</c>/
/// <c>scrollBy</c>, <c>scrollIntoView</c>, <c>scrollParent</c>). Every value here reads the live layout, so
/// the module depends on the bridge through the deliberately wide <see cref="IElementGeometryHost"/>
/// contract (the Phase 3 "wide-explicit-host" template) rather than a one-member seam — the point is that
/// the exact geometry surface is now named instead of the callbacks reaching into arbitrary bridge
/// internals. Was the bridge's box-model block in <c>DomBridge/ElementInterfaces.cs</c> and the
/// <c>JsElementInterfacesGetScrollTop072Core</c>..<c>ScrollParent085Core</c> callbacks.
/// </summary>
internal static class ElementGeometryBinding
{
    /// <summary>
    /// The box metrics and scrolling members CSSOM View puts on <c>Element</c>, installed on
    /// <paramref name="target"/> — <c>Element.prototype</c>, or one wrapper before the realm is up.
    /// </summary>
    /// <remarks>
    /// The viewport test is asked per call rather than once at install, which is what a prototype
    /// member has to do and is also the more truthful answer: it walks the element's ancestors, so a
    /// <c>&lt;body&gt;</c> whose wrapper was built before it was attached under <c>&lt;html&gt;</c> used
    /// to keep the answer it had then for the rest of the document's life.
    /// </remarks>
    public static void InstallElementMembers(IElementGeometryHost host, JSObject target, ElementSource element)
    {
        // -- TODO-G4 / TODO-G19: Box model properties for all elements --
        // clientWidth/clientHeight, scrollWidth/scrollHeight, scrollTop/scrollLeft, and
        // getBoundingClientRect()
        target.FastAddProperty("clientTop",
            new DomFunction((in a) => new JSNumber(host.GetClientTopForDomElement(element(in a, "clientTop"))), "get clientTop"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        target.FastAddProperty("clientLeft",
            new DomFunction((in a) => new JSNumber(host.GetClientLeftForDomElement(element(in a, "clientLeft"))), "get clientLeft"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        target.FastAddProperty("clientWidth",
            new DomFunction((in a) => Metric(host, element(in a, "clientWidth"), host.GetClientWidthForDomElement), "get clientWidth"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        target.FastAddProperty("clientHeight",
            new DomFunction((in a) => Metric(host, element(in a, "clientHeight"), host.GetClientHeightForDomElement), "get clientHeight"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        target.FastAddProperty("scrollWidth",
            new DomFunction((in a) => Metric(host, element(in a, "scrollWidth"), host.GetScrollWidthForDomElement), "get scrollWidth"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        target.FastAddProperty("scrollHeight",
            new DomFunction((in a) => Metric(host, element(in a, "scrollHeight"), host.GetScrollHeightForDomElement), "get scrollHeight"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        target.FastAddProperty("scrollTop",
            new DomFunction((in a) => GetScrollTop(host, element(in a, "scrollTop")), "get scrollTop"),
            new DomFunction((in a) => SetScrollTop(host, element(in a, "scrollTop"), in a), "set scrollTop"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        target.FastAddProperty("scrollLeft",
            new DomFunction((in a) => GetScrollLeft(host, element(in a, "scrollLeft")), "get scrollLeft"),
            new DomFunction((in a) => SetScrollLeft(host, element(in a, "scrollLeft"), in a), "set scrollLeft"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // getBoundingClientRect() — returns DOMRect-like object
        target.FastAddValue("getBoundingClientRect",
            new DomFunction((in a) => Rect(host, element(in a, "getBoundingClientRect"), GetBoundingClientRect), "getBoundingClientRect", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // getClientRects() — returns array with one DOMRect for root elements
        target.FastAddValue("getClientRects",
            new DomFunction((in a) => Rect(host, element(in a, "getClientRects"), GetClientRects), "getClientRects", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        target.FastAddValue("scrollIntoView",
            new DomFunction((in a) => ScrollIntoView(host, element(in a, "scrollIntoView"), in a), "scrollIntoView", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        target.FastAddValue("scroll",
            new DomFunction((in a) => Scroll(host, element(in a, "scroll"), in a), "scroll", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        target.FastAddValue("scrollTo",
            new DomFunction((in a) => Scroll(host, element(in a, "scrollTo"), in a), "scrollTo", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        target.FastAddValue("scrollBy",
            new DomFunction((in a) => ScrollBy(host, element(in a, "scrollBy"), in a), "scrollBy", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);
    }

    /// <summary>
    /// The metrics <c>HTMLElement</c> owns — the <c>offset*</c> family — on its prototype. Like the
    /// <c>Element</c> half above, the viewport test is asked per call rather than snapshotted at
    /// install.
    /// </summary>
    public static void InstallHtmlElementMembers(IElementGeometryHost host, JSObject target, ElementSource element)
    {
        target.FastAddProperty("offsetWidth",
            new DomFunction((in a) => Metric(host, element(in a, "offsetWidth"), host.GetOffsetWidthForDomElement), "get offsetWidth"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        target.FastAddProperty("offsetHeight",
            new DomFunction((in a) => Metric(host, element(in a, "offsetHeight"), host.GetOffsetHeightForDomElement), "get offsetHeight"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        target.FastAddProperty("offsetTop",
            new DomFunction((in a) => new JSNumber(host.GetOffsetTopForDomElement(element(in a, "offsetTop"))), "get offsetTop"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        target.FastAddProperty("offsetLeft",
            new DomFunction((in a) => new JSNumber(host.GetOffsetLeftForDomElement(element(in a, "offsetLeft"))), "get offsetLeft"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        target.FastAddProperty("offsetParent",
            new DomFunction((in a) => GetOffsetParent(host, element(in a, "offsetParent")), "get offsetParent"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
    }

    /// <summary>
    /// <c>scrollParent</c>, the bridge's own — on no browser's prototype at all, so it stays an own
    /// property of each element wrapper rather than being smuggled onto one.
    /// </summary>
    public static void InstallBridgeMembers(IElementGeometryHost host, JSObject obj, DomElement element)
    {
        obj.FastAddValue("scrollParent",
            new DomFunction((in _) => GetScrollParent(host, element), "scrollParent", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
    }

    /// <summary>One metric read, with the viewport test the metrics take resolved for this element.</summary>
    private static JSValue Metric(IElementGeometryHost host, DomElement element, Func<DomElement, bool, double> read) =>
        new JSNumber(read(element, host.IsViewportElementForMetrics(element)));

    /// <summary>The same, for the two rect readers.</summary>
    private static JSValue Rect(IElementGeometryHost host, DomElement element,
        Func<IElementGeometryHost, DomElement, bool, JSValue> read) =>
        read(host, element, host.IsViewportElementForMetrics(element));

    private static JSValue GetScrollTop(IElementGeometryHost host, DomElement element)
    {
        if (host.GetElementScrollOffset(element, vertical: true) is double sv)
            return new JSNumber(sv);
        return new JSNumber(0);
    }

    private static JSValue SetScrollTop(IElementGeometryHost host, DomElement element, in Arguments a)
    {
        if (a.Length > 0)
            host.SetElementScrollOffsetsWithBehavior(element, top: a[0].DoubleValue);
        return JSUndefined.Value;
    }

    private static JSValue GetScrollLeft(IElementGeometryHost host, DomElement element)
    {
        if (host.GetElementScrollOffset(element, vertical: false) is double sv)
            return new JSNumber(sv);
        return new JSNumber(0);
    }

    private static JSValue SetScrollLeft(IElementGeometryHost host, DomElement element, in Arguments a)
    {
        if (a.Length > 0)
            host.SetElementScrollOffsetsWithBehavior(element, left: a[0].DoubleValue);
        return JSUndefined.Value;
    }

    private static JSValue GetOffsetParent(IElementGeometryHost host, DomElement element)
    {
        var offsetParent = host.GetOffsetParentForDomElement(element);
        return offsetParent != null ? host.ToJSObject(offsetParent) : JSNull.Value;
    }

    private static JSValue GetBoundingClientRect(IElementGeometryHost host, DomElement element, bool isViewportElement)
    {
        var (Left, Top, Width, Height) = host.GetBoundingClientRectForDomElement(element, isViewportElement);
        return BuildRect(Left, Top, Width, Height);
    }

    private static JSValue GetClientRects(IElementGeometryHost host, DomElement element, bool isViewportElement)
    {
        var (Left, Top, Width, Height) = host.GetBoundingClientRectForDomElement(element, isViewportElement);
        var rect = BuildRect(Left, Top, Width, Height);
        return Width > 0 || Height > 0 || isViewportElement ? new JSArray([rect]) : new JSArray();
    }

    // Builds the DOMRect-like object (x/y/top/left/right/bottom/width/height) shared by
    // getBoundingClientRect() and getClientRects().
    private static JSObject BuildRect(double left, double top, double width, double height)
    {
        var rect = new JSObject();
        rect.FastAddValue("x", new JSNumber(left), JSPropertyAttributes.EnumerableConfigurableValue);
        rect.FastAddValue("y", new JSNumber(top), JSPropertyAttributes.EnumerableConfigurableValue);
        rect.FastAddValue("top", new JSNumber(top), JSPropertyAttributes.EnumerableConfigurableValue);
        rect.FastAddValue("left", new JSNumber(left), JSPropertyAttributes.EnumerableConfigurableValue);
        rect.FastAddValue("right", new JSNumber(left + width), JSPropertyAttributes.EnumerableConfigurableValue);
        rect.FastAddValue("bottom", new JSNumber(top + height), JSPropertyAttributes.EnumerableConfigurableValue);
        rect.FastAddValue("width", new JSNumber(width), JSPropertyAttributes.EnumerableConfigurableValue);
        rect.FastAddValue("height", new JSNumber(height), JSPropertyAttributes.EnumerableConfigurableValue);
        return rect;
    }

    private static JSValue ScrollIntoView(IElementGeometryHost host, DomElement element, in Arguments a)
    {
        var (Block, Inline, Behavior) = host.GetScrollIntoViewOptions(a);
        host.ScrollElementIntoView(element, Block, Inline, Behavior);
        return JSUndefined.Value;
    }

    // scroll() / scrollTo() — absolute scroll to (left, top).
    private static JSValue Scroll(IElementGeometryHost host, DomElement element, in Arguments a)
    {
        var (left, top, behavior) = host.GetScrollArguments(a);
        // Clamped to the scrolling area — see the note in WindowScrollBinding.
        host.SetElementScrollOffsetsWithBehavior(element, left, top, clamp: true, behavior: behavior);
        return JSUndefined.Value;
    }

    // scrollBy() — relative scroll.
    private static JSValue ScrollBy(IElementGeometryHost host, DomElement element, in Arguments a)
    {
        var (left, top, behavior) = host.GetScrollArguments(a);
        host.SetElementScrollOffsetsWithBehavior(element, left, top, relative: true, clamp: true, behavior: behavior);
        return JSUndefined.Value;
    }

    private static JSValue GetScrollParent(IElementGeometryHost host, DomElement element)
    {
        var scrollParent = host.GetScrollParentForDomElement(element);
        return scrollParent != null ? host.ToJSObject(scrollParent) : JSNull.Value;
    }
}
