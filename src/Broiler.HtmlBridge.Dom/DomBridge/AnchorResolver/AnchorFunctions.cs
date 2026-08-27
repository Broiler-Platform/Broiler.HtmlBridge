using System.Globalization;
using System.Linq;
using Broiler.CSS;
using Broiler.Dom;
using Broiler.Layout;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // -----------------------------------------------------------------
    // anchor() resolution
    // -----------------------------------------------------------------

    // The anchor()/anchor-size() grammar (token matching + typed extraction) is the
    // canonical Broiler.CSS.AnchorFunction model (Phase 5 item 4). These callbacks
    // keep only the used-value geometry; AnchorFunction.Rewrite/RewriteSize supply
    // the parsed AnchorFunctionRef/AnchorSizeFunctionRef.
    private void ResolveAnchorFunctions(DomElement element, Dictionary<string, AnchorInfo> anchorRegistry,
        Dictionary<string, Dictionary<string, string>>? positionTryRules = null)
    {
        var cssProps = CollectMatchedRuleProperties(element);

        bool hasAnchorRef = false;
        bool hasAnchorSizeRef = false;
        foreach (var kv in cssProps)
        {
            if (kv.Value.Contains("anchor(", StringComparison.OrdinalIgnoreCase))
                hasAnchorRef = true;
            if (kv.Value.Contains("anchor-size(", StringComparison.OrdinalIgnoreCase))
                hasAnchorSizeRef = true;
        }
        // Also check inline styles for anchor-size()
        foreach (var kv in BakedInlineStyle(element))
        {
            if (kv.Value.Contains("anchor-size(", StringComparison.OrdinalIgnoreCase))
                hasAnchorSizeRef = true;
        }

        // A box that uses both anchor-size() and anchor() insets is handed off as a unit — the
        // engine sizes then places it in one post-pass. Neither pure gate admits it (each excludes
        // the other function), so this single flag drives both the inset-skip below and the
        // size-skip further down, keeping the two halves' bake/handoff decision in lockstep. The
        // NativeAnchorPlacement flag check is dropped in Phase 4 item-2 step 5 (a provable no-op on
        // the native default path, where it was already true).
        bool combinedMvp = hasAnchorRef && hasAnchorSizeRef &&
            IsMvpNativeAnchorCombinedBox(element, cssProps, anchorRegistry);

        // For the MVP subset, skip baking the anchor() insets entirely so the box's
        // `left/right/top/bottom: anchor(...)` CSS survives to the render and the Broiler.Layout
        // engine's placement post-pass resolves it natively (see CssBox.TryApplyAnchorInsetPlacement).
        // Every other anchor() box is baked below.
        if (hasAnchorRef &&
            (combinedMvp ||
             IsMvpNativeAnchorInsetBox(element, cssProps, anchorRegistry, positionTryRules)))
            hasAnchorRef = false;

        if (hasAnchorRef)
        {
            // Need CB dimensions for resolving anchor positions in right/bottom contexts.
            double cbW = FindContainingBlockWidth(element);
            double cbH = FindContainingBlockHeight(element);

            // Get the implicit anchor name from position-anchor.
            string? implicitAnchor = cssProps.GetValueOrDefault("position-anchor") ??
                                     BakedInlineStyle(element).GetValueOrDefault("position-anchor");

            // When the target element is fixed-positioned (e.g. top-layer dialog)
            // and the anchor is NOT fixed-positioned, anchor positions must be
            // adjusted by the document scroll offset so the anchor's viewport
            // position is used instead of its document position.
            bool targetIsFixed =
                (cssProps.GetValueOrDefault("position") ?? BakedInlineStyle(element).GetValueOrDefault("position")) == "fixed" ||
                (DialogStateFor(element).Modal.TryGet(out var tModal) && tModal is true);
            double scrollAdjY = 0, scrollAdjX = 0;
            if (targetIsFixed)
            {
                var docEl = DocumentElement;
                if (ScrollStateFor(docEl).Top.TryGet(out var stv) && stv is double scrollTop)
                    scrollAdjY = scrollTop;
                if (ScrollStateFor(docEl).Left.TryGet(out var slv) && slv is double scrollLeft)
                    scrollAdjX = scrollLeft;
            }

            foreach (var kv in cssProps)
            {
                var propName = kv.Key.ToLowerInvariant();
                var resolved = AnchorFunction.Rewrite(kv.Value, r =>
                {
                    var anchorName = string.IsNullOrEmpty(r.Name)
                        ? (implicitAnchor ?? string.Empty)
                        : r.Name!;
                    var fallback = r.Fallback;

                    if (!anchorRegistry.TryGetValue(anchorName, out var anchor) ||
                        !IsAnchorAccessible(anchor.SourceElement, element))
                    {
                        // Anchor not found or not accessible — use fallback or 0px.
                        return fallback ?? "0px";
                    }

                    // Compute the raw edge position (from CB origin).
                    // When the target is fixed and the anchor is not fixed,
                    // adjust for document scroll to get viewport position.
                    // Use only the CSS computed position to determine if the
                    // anchor is fixed — modal dialogs with position:absolute
                    // are still shifted by scroll simulation and need adjustment.
                    bool anchorIsFixed = anchor.SourceElement != null &&
                        GetComputedProps(anchor.SourceElement).GetValueOrDefault("position") == "fixed";

                    // Scroll-driven positioning (CSS Anchor Positioning § scroll):
                    // an anchored element must track its anchor's *scrolled* position.
                    // When a scroll container is an ancestor of the anchor but NOT of
                    // the target (the target lives outside that scroller, e.g. a fixed
                    // or sibling-of-scroller abspos box), the anchor's edges shift by
                    // that scroller's scroll offset while the target does not — so the
                    // resolved inset must subtract it. Scrollers that contain the target
                    // too move both together and are skipped. (The target-inside-scroller
                    // case is handled separately by ApplyScrollSimulation, which shifts
                    // the target's own subtree.)
                    double nestedX = 0, nestedY = 0;
                    if (!anchorIsFixed && anchor.SourceElement != null)
                        ComputeInterveningScrollOffset(
                            anchor.SourceElement, element, out nestedX, out nestedY);

                    double adjY = anchorIsFixed ? 0 : scrollAdjY + nestedY;
                    double adjX = anchorIsFixed ? 0 : scrollAdjX + nestedX;

                    // Edge coordinate math (anchor edge − scroll adjustment, plus the
                    // right/bottom opposite-edge flip) is the canonical
                    // Broiler.Layout.AnchorGeometry model (Phase 5 item 3).
                    double value = AnchorGeometry.ResolveEdge(
                        anchor.Left, anchor.Top, anchor.Right, anchor.Bottom,
                        r.Side, adjX, adjY, MapAnchorInsetProperty(propName), cbW, cbH);

                    return $"{value.ToString(CultureInfo.InvariantCulture)}px";
                });

                if (resolved != kv.Value)
                    BakedInlineStyle(element)[kv.Key] = resolved;
            }

            // Apply non-anchor CSS properties (e.g. position, margin).
            foreach (var kv in cssProps)
            {
                if (!kv.Value.Contains("anchor(", StringComparison.OrdinalIgnoreCase) &&
                    !kv.Value.Contains("anchor-size(", StringComparison.OrdinalIgnoreCase) &&
                    !BakedInlineStyle(element).ContainsKey(kv.Key) &&
                    IsLayoutProperty(kv.Key))
                {
                    BakedInlineStyle(element)[kv.Key] = kv.Value;
                }
            }

            // Remove 'inset' shorthand.
            BakedInlineStyle(element).Remove("inset");
        }

        // Resolve anchor-size() function calls in both CSS and inline styles.
        // Skip baking for the MVP subset so the box's `width/height: anchor-size(...)` survives to
        // the engine's sizing pass (CssBox.TryApplyNativeAnchorSizing). The combined-box flag skips
        // the size bake too, so a box that also has anchor() insets keeps both halves un-baked for
        // the engine. The NativeAnchorPlacement flag check is dropped in Phase 4 item-2 step 5 (a
        // provable no-op on the native default path, where it was already true).
        if (hasAnchorSizeRef &&
            !(combinedMvp || IsMvpNativeAnchorSizeBox(element, cssProps, anchorRegistry)))
        {
            ResolveAnchorSizeFunctions(element, cssProps, anchorRegistry);
        }

        // Snapshot the child list: resolving anchor functions on a descendant can
        // mutate the live DOM (e.g. anchor-driven style/structure changes under
        // content-visibility), and iterating the live collection while it changes
        // throws "Collection was modified" (WPT content-visibility-anchor-positioning)
        // or overflows the ToList() copy. SnapshotChildren tolerates both.
        foreach (var child in SnapshotChildren(element))
            ResolveAnchorFunctions(child, anchorRegistry, positionTryRules);
    }

    /// <summary>
    /// Accumulates the scroll offset of scroll containers that lie between the
    /// anchor and the target — i.e. that are ancestors of <paramref name="anchorEl"/>
    /// but do not also contain <paramref name="targetEl"/>. Such a scroller moves the
    /// anchor (and its edges) but not the target, so an anchored element positioned
    /// against it must subtract this offset to stay pinned to the anchor's scrolled
    /// position. The walk stops at the first scroller that also contains the target
    /// (that scroller scrolls both, or is the target's containing block). The offset
    /// is scaled to match <c>ApplyScrollSimulation</c> under an active visual viewport.
    /// </summary>
    private void ComputeInterveningScrollOffset(DomElement anchorEl, DomElement targetEl, out double offX, out double offY)
    {
        offX = 0;
        offY = 0;
        var scale = GetScrollSimulationScaleFactor();

        // A position:sticky element stays pinned to its nearest scroll
        // container's edge instead of translating with that scroller's scroll.
        // When the anchor (or a box between it and its scroller) is sticky, the
        // anchor's scrolled position does NOT shift by the full scroll offset,
        // so a target outside the scroller must not subtract it — doing so drives
        // the anchored box off-screen (css-anchor-position anchor-scroll-to-sticky-004).
        bool stickyToNextScroller = IsSticky(GetComputedProps(anchorEl));

        for (var el = ParentEl(anchorEl); el != null; el = ParentEl(el))
        {
            var props = GetComputedProps(el);
            if (!CssOverflow.ClipsOverflow(props))
            {
                // A sticky box below the next scroller pins the anchor to it;
                // remember that until we reach the scroller itself.
                if (IsSticky(props))
                    stickyToNextScroller = true;
                continue;
            }

            // The target lives inside this scroller too → they scroll together
            // (or this scroller is the target's containing block); no separation.
            if (IsDescendantOrSelf(targetEl, el))
                break;

            // Skip scrollers the anchor is sticky-pinned to: the anchor resists
            // this scroller's scroll, so its edges don't move by that offset.
            if (!stickyToNextScroller)
            {
                if (ScrollStateFor(el).Left.TryGet(out var sl) && sl is double slv)
                    offX += slv * scale;
                if (ScrollStateFor(el).Top.TryGet(out var st) && st is double stv)
                    offY += stv * scale;
            }

            // Past this scroller, sticky pinning resets: a sticky box higher up
            // pins to the next scroll container, not this one.
            stickyToNextScroller = false;
        }
    }
    /// <summary>
    /// True when the computed <c>position</c> in <paramref name="props"/> is
    /// <c>sticky</c>.
    /// </summary>
    private static bool IsSticky(Dictionary<string, string> props) =>
        string.Equals(props.GetValueOrDefault("position"), "sticky", StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// True when <paramref name="node"/> is <paramref name="ancestor"/> or a
    /// descendant of it.
    /// </summary>
    private static bool IsDescendantOrSelf(DomElement node, DomElement ancestor)
    {
        for (var cur = node; cur != null; cur = ParentEl(cur))
            if (cur == ancestor)
                return true;
        return false;
    }
    private static bool IsLayoutProperty(string prop) => prop switch
    {
        "position" or "top" or "right" or "bottom" or "left"
            or "margin" or "margin-top" or "margin-right"
            or "margin-bottom" or "margin-left"
            or "width" or "height" => true,
        _ => false,
    };

    /// <summary>
    /// Maps the CSS inset property an <c>anchor()</c> resolves into to the
    /// <see cref="AnchorInsetProperty"/> the Layout edge resolver flips against
    /// (only right/bottom differ; everything else uses the raw edge).
    /// </summary>
    private static AnchorInsetProperty MapAnchorInsetProperty(string property) => property switch
    {
        "right" => AnchorInsetProperty.Right,
        "bottom" => AnchorInsetProperty.Bottom,
        "left" => AnchorInsetProperty.Left,
        "top" => AnchorInsetProperty.Top,
        _ => AnchorInsetProperty.Other,
    };
    /// <summary>
    /// Resolves <c>anchor-size()</c> function calls in CSS properties and inline
    /// styles, replacing them with computed pixel values from the anchor element's
    /// dimensions.
    /// </summary>
    private void ResolveAnchorSizeFunctions(
        DomElement element,
        Dictionary<string, string> cssProps,
        Dictionary<string, AnchorInfo> anchorRegistry)
    {
        // Get implicit anchor name from position-anchor.
        string? implicitAnchor = cssProps.GetValueOrDefault("position-anchor") ??
                                 BakedInlineStyle(element).GetValueOrDefault("position-anchor");

        // The anchor's registered geometry is in used (already-zoomed) pixels when it comes from the
        // real layout box (TryGetAnchorLayoutBox / shared geometry). The resolved anchor-size() length
        // is written as an author length on this positioned element, which is then re-scaled by the
        // element's own used zoom (the DOM `zoom` bake). Divide the used size by that zoom so the value
        // lands in the element's pre-zoom (CSS-px) frame and is not zoom-counted twice — matching the
        // reference where a zoom:2 anchor's 200px width covers a zoom:2 positioned box. A no-op for the
        // common unzoomed case (used zoom == 1). See css-anchor-position/anchor-size-css-zoom.
        double usedZoom = GetUsedZoomForElement(element);
        if (usedZoom <= 0) usedZoom = 1;

        string ResolveValue(string value)
        {
            return AnchorFunction.RewriteSize(value, r =>
            {
                var anchorName = string.IsNullOrEmpty(r.Name)
                    ? (implicitAnchor ?? string.Empty)
                    : r.Name!;

                if (!anchorRegistry.TryGetValue(anchorName, out var anchor))
                    return "0px";

                double result = AnchorGeometry.ResolveSize(r.Dimension, anchor.Width, anchor.Height) / usedZoom;

                return $"{result.ToString(CultureInfo.InvariantCulture)}px";
            });
        }

        // Resolve in CSS properties and apply as inline styles.
        foreach (var kv in cssProps)
        {
            if (kv.Value.Contains("anchor-size(", StringComparison.OrdinalIgnoreCase))
            {
                BakedInlineStyle(element)[kv.Key] = ResolveValue(kv.Value);
            }
        }

        // Resolve in existing inline styles.
        var inlineKeys = new List<string>(BakedInlineStyle(element).Keys);
        foreach (var key in inlineKeys)
        {
            if (BakedInlineStyle(element)[key].Contains("anchor-size(", StringComparison.OrdinalIgnoreCase))
            {
                BakedInlineStyle(element)[key] = ResolveValue(BakedInlineStyle(element)[key]);
            }
        }
    }

}
