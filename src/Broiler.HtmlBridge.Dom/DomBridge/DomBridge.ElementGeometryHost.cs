using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IElementGeometryHost implementation for the ElementGeometryBinding feature module (Phase 3):
// the box-model metrics and scrolling operations are the one Phase 3 family that genuinely reads the live
// layout, so the contract is wide by design. Each member forwards to the existing private LayoutMetrics.*
// method — the module now names the exact geometry surface it depends on instead of reaching into the
// bridge directly.
//
// This file is the engine-typed half of the seam, and two members sit on it. The scroll-option readers in
// LayoutMetrics.Scrolling.cs read an engine call frame and are shared word for word with the window and
// sub-window scroll hosts, which have not migrated; so the *dispatch* on what the page passed is asked of
// the JSEAL handle here, and the reads out of the options dictionary are still that file's, on the engine
// object the handle carries. Both stop being needed when LayoutMetrics.Scrolling.cs moves and all three
// hosts can name one JSEAL reader.
public sealed partial class DomBridge : Dom.Features.IElementGeometryHost
{
    bool Dom.Features.IElementGeometryHost.IsViewportElementForMetrics(DomElement element) => IsViewportElementForMetrics(element);

    double Dom.Features.IElementGeometryHost.GetClientTopForDomElement(DomElement element) => GetClientTopForDomElement(element);
    double Dom.Features.IElementGeometryHost.GetClientLeftForDomElement(DomElement element) => GetClientLeftForDomElement(element);
    double Dom.Features.IElementGeometryHost.GetClientWidthForDomElement(DomElement element, bool isRoot) => GetClientWidthForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetClientHeightForDomElement(DomElement element, bool isRoot) => GetClientHeightForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetOffsetWidthForDomElement(DomElement element, bool isRoot) => GetOffsetWidthForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetOffsetHeightForDomElement(DomElement element, bool isRoot) => GetOffsetHeightForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetScrollWidthForDomElement(DomElement element, bool isRoot) => GetScrollWidthForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetScrollHeightForDomElement(DomElement element, bool isRoot) => GetScrollHeightForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetOffsetTopForDomElement(DomElement element) => GetOffsetTopForDomElement(element);
    double Dom.Features.IElementGeometryHost.GetOffsetLeftForDomElement(DomElement element) => GetOffsetLeftForDomElement(element);

    double? Dom.Features.IElementGeometryHost.GetElementScrollOffset(DomElement element, bool vertical) => GetElementScrollOffset(element, vertical);

    void Dom.Features.IElementGeometryHost.SetElementScrollOffsetsWithBehavior(DomElement element,
        double? left, double? top, bool relative, bool clamp, string? behavior)
        => SetElementScrollOffsetsWithBehavior(element, left, top, relative, clamp, behavior);

    DomElement? Dom.Features.IElementGeometryHost.GetOffsetParentForDomElement(DomElement element) => GetOffsetParentForDomElement(element);
    DomElement? Dom.Features.IElementGeometryHost.GetScrollParentForDomElement(DomElement element) => GetScrollParentForDomElement(element);

    (double Left, double Top, double Width, double Height) Dom.Features.IElementGeometryHost.GetBoundingClientRectForDomElement(DomElement element, bool isRoot)
        => GetBoundingClientRectForDomElement(element, isRoot);

    /// <summary>
    /// <c>scrollIntoView</c>'s argument, which is a dictionary, a boolean, or nothing at all — the same
    /// three answers the engine-typed reader in <c>LayoutMetrics.Scrolling.cs</c> gives, decided from the
    /// JSEAL handle instead of from the engine value.
    /// </summary>
    /// <remarks>
    /// The no-argument case is <see cref="JsValue.IsMissing"/> rather than a length test for the reason
    /// the contract records: <c>scrollIntoView()</c> and <c>scrollIntoView(undefined)</c> are different
    /// calls here — the first aligns "start-if-needed", the second aligns "nearest" — and Missing is what
    /// tells them apart.
    /// </remarks>
    (string Block, string Inline, string? Behavior) Dom.Features.IElementGeometryHost.GetScrollIntoViewOptions(in JsCall call)
    {
        const string defaultBlock = "start";
        const string defaultInline = "nearest";

        var first = call[0];
        if (first.IsMissing)
            return (defaultBlock, "start-if-needed", null);

        if (first.IsObject)
        {
            var options = Dom.Runtime.JsInterop.ToEngineObject(first);
            return (
                NormalizeScrollIntoViewAlignment(GetOptionalStringOption(options, "block"), defaultBlock),
                NormalizeScrollIntoViewAlignment(GetOptionalStringOption(options, "inline"), defaultInline),
                GetOptionalScrollBehavior(options));
        }

        if (first.IsBoolean)
        {
            return first.AsBoolean
                ? (defaultBlock, defaultInline, null)
                : ("end", defaultInline, null);
        }

        return (defaultBlock, defaultInline, null);
    }

    void Dom.Features.IElementGeometryHost.ScrollElementIntoView(DomElement element, string? block, string? inline, string? behavior)
        => ScrollElementIntoView(element, block, inline, behavior);

    /// <summary>
    /// <c>scroll</c>/<c>scrollTo</c>/<c>scrollBy</c>'s arguments: a scroll-options dictionary, or the
    /// <c>(x, y)</c> pair.
    /// </summary>
    /// <remarks>
    /// The coordinates go through the realm's <c>ToNumber</c>, which is the coercion the engine was
    /// performing before — <c>el.scrollTo("100", "0")</c> scrolls, it does not scroll to NaN.
    /// </remarks>
    (double? Left, double? Top, string? Behavior) Dom.Features.IElementGeometryHost.GetScrollOptions(in JsCall call)
    {
        var first = call[0];
        if (first.IsMissing)
            return (null, null, null);

        if (first.IsObject)
        {
            var options = Dom.Runtime.JsInterop.ToEngineObject(first);
            return (
                GetOptionalScrollCoordinate(options, "left"),
                GetOptionalScrollCoordinate(options, "top"),
                GetOptionalScrollBehavior(options));
        }

        var second = call[1];
        return (call.Realm.ToNumber(first), second.IsMissing ? null : call.Realm.ToNumber(second), null);
    }

    JsValue Dom.Features.IElementGeometryHost.WrapNode(DomNode node) => WrapNode(node);
}
