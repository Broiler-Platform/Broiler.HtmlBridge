using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

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
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine type:
/// the members and the <c>DOMRect</c>-like objects come from the realm, and each body reads a
/// <see cref="JsCall"/>. The two scroll-offset setters coerce with the realm's <c>ToNumber</c> rather
/// than reading the handle's own number, because <c>el.scrollTop = "120"</c> is a string the engine was
/// coercing before and <see cref="JsValue.AsNumber"/> deliberately answers NaN for one.
/// </remarks>
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
    public static void InstallElementMembers(IElementGeometryHost host, IJsRealm realm, JsValue target, JsElementSource element)
    {
        // -- TODO-G4 / TODO-G19: Box model properties for all elements --
        // clientWidth/clientHeight, scrollWidth/scrollHeight, scrollTop/scrollLeft, and
        // getBoundingClientRect()
        realm.DefineAccessor(target, "clientTop",
            (in call) => JsValue.Number(host.GetClientTopForDomElement(element(in call, "clientTop"))), null);

        realm.DefineAccessor(target, "clientLeft",
            (in call) => JsValue.Number(host.GetClientLeftForDomElement(element(in call, "clientLeft"))), null);

        realm.DefineAccessor(target, "clientWidth",
            (in call) => Metric(host, element(in call, "clientWidth"), host.GetClientWidthForDomElement), null);

        realm.DefineAccessor(target, "clientHeight",
            (in call) => Metric(host, element(in call, "clientHeight"), host.GetClientHeightForDomElement), null);

        realm.DefineAccessor(target, "scrollWidth",
            (in call) => Metric(host, element(in call, "scrollWidth"), host.GetScrollWidthForDomElement), null);

        realm.DefineAccessor(target, "scrollHeight",
            (in call) => Metric(host, element(in call, "scrollHeight"), host.GetScrollHeightForDomElement), null);

        realm.DefineAccessor(target, "scrollTop",
            (in call) => GetScrollTop(host, element(in call, "scrollTop")),
            (in call) => SetScrollTop(host, element(in call, "scrollTop"), in call));

        realm.DefineAccessor(target, "scrollLeft",
            (in call) => GetScrollLeft(host, element(in call, "scrollLeft")),
            (in call) => SetScrollLeft(host, element(in call, "scrollLeft"), in call));

        // getBoundingClientRect() — returns DOMRect-like object
        realm.DefineValue(target, "getBoundingClientRect",
            realm.NewMethod("getBoundingClientRect",
                (in call) => Rect(call.Realm, host, element(in call, "getBoundingClientRect"), GetBoundingClientRect), 0));

        // getClientRects() — returns array with one DOMRect for root elements
        realm.DefineValue(target, "getClientRects",
            realm.NewMethod("getClientRects",
                (in call) => Rect(call.Realm, host, element(in call, "getClientRects"), GetClientRects), 0));

        realm.DefineValue(target, "scrollIntoView",
            realm.NewMethod("scrollIntoView",
                (in call) => ScrollIntoView(host, element(in call, "scrollIntoView"), in call), 1));

        realm.DefineValue(target, "scroll",
            realm.NewMethod("scroll", (in call) => Scroll(host, element(in call, "scroll"), in call), 2));

        realm.DefineValue(target, "scrollTo",
            realm.NewMethod("scrollTo", (in call) => Scroll(host, element(in call, "scrollTo"), in call), 2));

        realm.DefineValue(target, "scrollBy",
            realm.NewMethod("scrollBy", (in call) => ScrollBy(host, element(in call, "scrollBy"), in call), 2));
    }

    /// <summary>
    /// The metrics <c>HTMLElement</c> owns — the <c>offset*</c> family — on its prototype. Like the
    /// <c>Element</c> half above, the viewport test is asked per call rather than snapshotted at
    /// install.
    /// </summary>
    public static void InstallHtmlElementMembers(IElementGeometryHost host, IJsRealm realm, JsValue target, JsElementSource element)
    {
        realm.DefineAccessor(target, "offsetWidth",
            (in call) => Metric(host, element(in call, "offsetWidth"), host.GetOffsetWidthForDomElement), null);

        realm.DefineAccessor(target, "offsetHeight",
            (in call) => Metric(host, element(in call, "offsetHeight"), host.GetOffsetHeightForDomElement), null);

        realm.DefineAccessor(target, "offsetTop",
            (in call) => JsValue.Number(host.GetOffsetTopForDomElement(element(in call, "offsetTop"))), null);

        realm.DefineAccessor(target, "offsetLeft",
            (in call) => JsValue.Number(host.GetOffsetLeftForDomElement(element(in call, "offsetLeft"))), null);

        realm.DefineAccessor(target, "offsetParent",
            (in call) => GetOffsetParent(host, element(in call, "offsetParent")), null);
    }

    /// <summary>
    /// <c>scrollParent</c>, the bridge's own — on no browser's prototype at all, so it stays an own
    /// property of each element wrapper rather than being smuggled onto one.
    /// </summary>
    public static void InstallBridgeMembers(IElementGeometryHost host, IJsRealm realm, JsValue obj, DomElement element)
    {
        realm.DefineValue(obj, "scrollParent",
            realm.NewMethod("scrollParent", (in _) => GetScrollParent(host, element), 0));
    }

    /// <summary>One metric read, with the viewport test the metrics take resolved for this element.</summary>
    private static JsValue Metric(IElementGeometryHost host, DomElement element, Func<DomElement, bool, double> read) =>
        JsValue.Number(read(element, host.IsViewportElementForMetrics(element)));

    /// <summary>The same, for the two rect readers.</summary>
    private static JsValue Rect(IJsRealm realm, IElementGeometryHost host, DomElement element,
        Func<IJsRealm, IElementGeometryHost, DomElement, bool, JsValue> read) =>
        read(realm, host, element, host.IsViewportElementForMetrics(element));

    private static JsValue GetScrollTop(IElementGeometryHost host, DomElement element)
    {
        if (host.GetElementScrollOffset(element, vertical: true) is double sv)
            return JsValue.Number(sv);
        return JsValue.Number(0);
    }

    private static JsValue SetScrollTop(IElementGeometryHost host, DomElement element, in JsCall call)
    {
        if (call.Length > 0)
            host.SetElementScrollOffsetsWithBehavior(element, top: call.Realm.ToNumber(call[0]));
        return JsValue.Undefined;
    }

    private static JsValue GetScrollLeft(IElementGeometryHost host, DomElement element)
    {
        if (host.GetElementScrollOffset(element, vertical: false) is double sv)
            return JsValue.Number(sv);
        return JsValue.Number(0);
    }

    private static JsValue SetScrollLeft(IElementGeometryHost host, DomElement element, in JsCall call)
    {
        if (call.Length > 0)
            host.SetElementScrollOffsetsWithBehavior(element, left: call.Realm.ToNumber(call[0]));
        return JsValue.Undefined;
    }

    private static JsValue GetOffsetParent(IElementGeometryHost host, DomElement element)
    {
        var offsetParent = host.GetOffsetParentForDomElement(element);
        return offsetParent != null ? host.WrapNode(offsetParent) : JsValue.Null;
    }

    private static JsValue GetBoundingClientRect(IJsRealm realm, IElementGeometryHost host, DomElement element, bool isViewportElement)
    {
        var (Left, Top, Width, Height) = host.GetBoundingClientRectForDomElement(element, isViewportElement);
        return BuildRect(realm, Left, Top, Width, Height);
    }

    private static JsValue GetClientRects(IJsRealm realm, IElementGeometryHost host, DomElement element, bool isViewportElement)
    {
        var (Left, Top, Width, Height) = host.GetBoundingClientRectForDomElement(element, isViewportElement);
        var rect = BuildRect(realm, Left, Top, Width, Height);
        return Width > 0 || Height > 0 || isViewportElement ? realm.NewArray([rect]) : realm.NewArray();
    }

    // Builds the DOMRect-like object (x/y/top/left/right/bottom/width/height) shared by
    // getBoundingClientRect() and getClientRects().
    private static JsValue BuildRect(IJsRealm realm, double left, double top, double width, double height)
    {
        var rect = realm.NewObject();
        realm.DefineValue(rect, "x", JsValue.Number(left));
        realm.DefineValue(rect, "y", JsValue.Number(top));
        realm.DefineValue(rect, "top", JsValue.Number(top));
        realm.DefineValue(rect, "left", JsValue.Number(left));
        realm.DefineValue(rect, "right", JsValue.Number(left + width));
        realm.DefineValue(rect, "bottom", JsValue.Number(top + height));
        realm.DefineValue(rect, "width", JsValue.Number(width));
        realm.DefineValue(rect, "height", JsValue.Number(height));
        return rect;
    }

    private static JsValue ScrollIntoView(IElementGeometryHost host, DomElement element, in JsCall call)
    {
        var (Block, Inline, Behavior) = host.GetScrollIntoViewOptions(in call);
        host.ScrollElementIntoView(element, Block, Inline, Behavior);
        return JsValue.Undefined;
    }

    // scroll() / scrollTo() — absolute scroll to (left, top).
    private static JsValue Scroll(IElementGeometryHost host, DomElement element, in JsCall call)
    {
        var (left, top, behavior) = host.GetScrollOptions(in call);
        // Clamped to the scrolling area — see the note in WindowScrollBinding.
        host.SetElementScrollOffsetsWithBehavior(element, left, top, clamp: true, behavior: behavior);
        return JsValue.Undefined;
    }

    // scrollBy() — relative scroll.
    private static JsValue ScrollBy(IElementGeometryHost host, DomElement element, in JsCall call)
    {
        var (left, top, behavior) = host.GetScrollOptions(in call);
        host.SetElementScrollOffsetsWithBehavior(element, left, top, relative: true, clamp: true, behavior: behavior);
        return JsValue.Undefined;
    }

    private static JsValue GetScrollParent(IElementGeometryHost host, DomElement element)
    {
        var scrollParent = host.GetScrollParentForDomElement(element);
        return scrollParent != null ? host.WrapNode(scrollParent) : JsValue.Null;
    }
}
