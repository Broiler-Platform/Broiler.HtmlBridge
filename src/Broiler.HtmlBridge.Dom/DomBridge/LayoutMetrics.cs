using System.Text;
using System.Text.RegularExpressions;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Logging;
using Broiler.Dom;
using Broiler.CSS;
using System.Globalization;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // RF-BRIDGE-1b: tracks whether a shared-geometry read pass is active. The pass reads
    // one static post-script layout snapshot (_sharedGeometrySnapshot, built up front by
    // WithLayoutGeometryCache); nested WithLayoutGeometryCache calls share the outermost
    // pass's snapshot, and only the owner builds and tears it down — so live JS geometry
    // queries (where the DOM may mutate between calls) each lay out fresh. The old coarse
    // estimators recursed up/down/across the tree and fanned out exponentially on deep
    // css-align / css-anchor-position trees (WPT #1113); they are gone now — the shared
    // renderer layout is the sole geometry source.
    private bool _layoutGeometryPassActive;

    /// <summary>
    /// Runs <paramref name="evaluate"/> with the shared-geometry snapshot installed for the
    /// pass, then tears it down. Only sound for a read pass over a static layout snapshot
    /// (no DOM or computed-style mutation mid-pass); nested calls share the outermost pass's
    /// snapshot and only the owner builds/clears it.
    /// </summary>
    private T WithLayoutGeometryCache<T>(Func<T> evaluate)
    {
        var owner = !_layoutGeometryPassActive;
        if (owner)
        {
            _layoutGeometryPassActive = true;
            // RF-BRIDGE-1b: build the shared-geometry snapshot up front, before the read
            // pass enumerates the element tree. BuildSharedGeometrySnapshot calls
            // GetRenderDocument, which mutates the document (reflects style into
            // attributes); doing that lazily mid-traversal modified a Children collection
            // that an enclosing foreach was still enumerating (InvalidOperationException).
            // Building once here also bounds it to one layout per pass. Built
            // unconditionally so a stale snapshot left by a non-pass shared query (e.g. the
            // anchor resolver) is overwritten fresh for this pass — "fresh" meaning re-acquired
            // under the current LayoutSnapshotKey, which reuses the retained layout when no
            // layout input has moved since it was taken (AcquireSharedGeometrySnapshot).
            _sharedGeometrySnapshot = AcquireSharedGeometrySnapshot();
        }

        try
        {
            return evaluate();
        }
        finally
        {
            if (owner)
            {
                // RF-BRIDGE-1b: the shared-geometry snapshot is scoped to the same pass.
                ClearSharedGeometrySnapshot();
                _layoutGeometryPassActive = false;
            }
        }
    }

    private double GetClientWidthForDomElement(DomElement element, bool isRoot) =>
        WithLayoutGeometryCache(() =>
        {
            if (isRoot)
                return GetViewportReferenceLength(element, vertical: false);

            // RF-BRIDGE-1b: clientWidth is the padding-box width (content + padding),
            // reported in the element's own unzoomed CSS pixels (the shared snapshot is
            // in zoom-baked space, so divide out the element's own used zoom). An element
            // with no shared box (detached / display:none) reports zero.
            if (TryGetSharedLayoutGeometry(element, out var shared))
                return UnzoomSharedExtent(shared.PaddingBox.Width, element);

            return 0;
        });

    private double GetClientHeightForDomElement(DomElement element, bool isRoot) =>
        WithLayoutGeometryCache(() =>
        {
            if (isRoot)
                return GetViewportReferenceLength(element, vertical: true);

            // RF-BRIDGE-1b: clientHeight is the padding-box height (content + padding),
            // reported in the element's own unzoomed CSS pixels (see GetClientWidth). An
            // element with no shared box (detached / display:none) reports zero.
            if (TryGetSharedLayoutGeometry(element, out var shared))
                return UnzoomSharedExtent(shared.PaddingBox.Height, element);

            return 0;
        });

    private double GetClientTopForDomElement(DomElement element)
    {
        var props = GetComputedProps(element);
        return ParseCssLengthToPixelsWithViewport(props.GetValueOrDefault("border-top-width"), element);
    }

    private double GetClientLeftForDomElement(DomElement element)
    {
        var props = GetComputedProps(element);
        return ParseCssLengthToPixelsWithViewport(props.GetValueOrDefault("border-left-width"), element);
    }

    private double GetOffsetWidthForDomElement(DomElement element, bool isRoot) =>
        WithLayoutGeometryCache(() =>
        {
            if (isRoot)
                return GetViewportReferenceLength(element, vertical: false);

            if (ShouldReportZeroOffsetMetrics(element))
                return 0;

            // Anchor sizing (position-area / anchor-size() / position-try / opposing-inset auto) is
            // resolved natively in the shared snapshot (HeadlessLayoutView's NativeAnchorPlacement pass),
            // so the snapshot border box already carries the used size.
            if (TryGetSharedLayoutGeometry(element, out var shared))
                return UnzoomSharedExtent(shared.BorderBox.Width, element);

            return 0;
        });

    private double GetOffsetHeightForDomElement(DomElement element, bool isRoot) =>
        WithLayoutGeometryCache(() =>
        {
            if (isRoot)
                return GetViewportReferenceLength(element, vertical: true);

            if (ShouldReportZeroOffsetMetrics(element))
                return 0;

            // See GetOffsetWidthForDomElement: anchor sizing is resolved natively in the shared snapshot.
            if (TryGetSharedLayoutGeometry(element, out var shared))
                return UnzoomSharedExtent(shared.BorderBox.Height, element);

            return 0;
        });

    private static bool ShouldReportZeroOffsetMetrics(DomElement element) =>
        string.Equals(element.TagName, "map", StringComparison.OrdinalIgnoreCase);

    private double GetOffsetTopForDomElement(DomElement element) =>
        WithLayoutGeometryCache(() =>
        {
            // Anchor placement (position-area / position-try / anchor() inset) is resolved natively in the
            // shared snapshot, so the snapshot offset already reflects it.
            var offsetParent = GetOffsetParentForDomElement(element);
            if (TryGetSharedOffset(element, offsetParent, vertical: true, out var sharedTop))
                return sharedTop;

            return 0;
        });

    private double GetOffsetLeftForDomElement(DomElement element) =>
        WithLayoutGeometryCache(() =>
        {
            // See GetOffsetTopForDomElement: anchor placement is resolved natively in the shared snapshot.
            var offsetParent = GetOffsetParentForDomElement(element);
            if (TryGetSharedOffset(element, offsetParent, vertical: false, out var sharedLeft))
                return sharedLeft;

            return 0;
        });

    /// <summary>
    /// RF-BRIDGE-1b: derive <c>offsetTop</c>/<c>offsetLeft</c> straight from the renderer's
    /// real box geometry when the shared snapshot is active. <c>offsetTop/Left</c> is the
    /// element's border-box edge relative to the offset parent's padding edge (HTML
    /// <c>offsetParent.clientLeft/Top</c> already excludes that border), or the initial
    /// containing block when there is no offset parent. This reflects layout effects the
    /// per-property estimator cannot model — e.g. abspos static-position alignment
    /// (<c>align-self</c>/<c>justify-self</c>) in vertical writing modes. Returns
    /// <c>false</c> (fall back to the estimator) when either box is missing from the snapshot.
    /// </summary>
    private bool TryGetSharedOffset(DomElement element, DomElement offsetParent, bool vertical, out double offset)
    {
        offset = 0;
        if (!UseSharedLayoutGeometry || !TryGetSharedLayoutGeometry(element, out var elementGeometry))
            return false;

        double elementEdge = vertical ? elementGeometry.BorderBox.Top : elementGeometry.BorderBox.Left;
        double parentEdge = 0;
        if (offsetParent != null)
        {
            if (!TryGetSharedLayoutGeometry(offsetParent, out var parentGeometry))
                return false;
            parentEdge = vertical ? parentGeometry.PaddingBox.Top : parentGeometry.PaddingBox.Left;
        }

        // The snapshot delta is in zoom-baked document space (the element's own edge and
        // the offset parent's padding edge are both baked). offsetTop/Left is reported in
        // the element's own unzoomed CSS pixels — excluding its own zoom — so divide the
        // delta by the element's cumulative used zoom (a no-op when unzoomed). This is the
        // zoom-chain-aware reconciliation: both edges are in the same baked space, so a
        // single division by the element's own zoom recovers the offset-parent-relative
        // value for the common, middle-ancestor-zoom, and own-zoom cases alike.
        var zoom = GetUsedZoomForElement(element);
        offset = zoom > 0.0001 ? (elementEdge - parentEdge) / zoom : (elementEdge - parentEdge);
        return true;
    }

    private DomElement? GetOffsetParentForDomElement(DomElement element)
    {
        if (ParentEl(element) == null ||
            ReferenceEquals(element, DocumentElement) ||
            string.Equals(element.TagName, "html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(element.TagName, "body", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var props = GetComputedProps(element);
        if (string.Equals(props.GetValueOrDefault("position"), "fixed", StringComparison.OrdinalIgnoreCase))
            return null;

        var documentElement = GetOwningDocumentElement(element);
        var fallbackBody = FindBodyElement(documentElement);
        for (var current = ParentEl(element); current != null; current = ParentEl(current))
        {
            if (string.Equals(current.TagName, "body", StringComparison.OrdinalIgnoreCase))
                return current;

            if (ReferenceEquals(current, documentElement))
                return fallbackBody ?? documentElement;

            var currentProps = GetComputedProps(current);
            var currentPosition = currentProps.GetValueOrDefault("position");
            if (!string.IsNullOrWhiteSpace(currentPosition) &&
                !string.Equals(currentPosition, "static", StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }
        }

        return fallbackBody ?? documentElement;
    }

    private DomElement? GetScrollParentForDomElement(DomElement element)
    {
        var documentElement = GetOwningDocumentElement(element);
        if (!HasAssociatedLayoutBox(element))
            return null;

        if (ReferenceEquals(element, documentElement) || ReferenceEquals(element, GetDocumentScrollingElement(documentElement)))
            return null;

        if (IsViewportBodyElement(element, documentElement))
            return GetDocumentScrollingElement(documentElement);

        var props = GetComputedProps(element);
        var position = props.GetValueOrDefault("position")?.Trim().ToLowerInvariant();
        if (string.Equals(position, "fixed", StringComparison.OrdinalIgnoreCase))
        {
            var fixedContainingBlock = FindFixedPositionContainingBlock(element, documentElement);
            if (fixedContainingBlock == null)
                return null;

            return FindNearestScrollParent(fixedContainingBlock, documentElement);
        }

        if (string.Equals(position, "absolute", StringComparison.OrdinalIgnoreCase))
        {
            var containingBlock = GetOffsetParentForDomElement(element);
            if (containingBlock == null)
                return GetDocumentScrollingElement(documentElement);

            return FindNearestScrollParent(containingBlock, documentElement);
        }

        return FindNearestScrollParent(ParentEl(element), documentElement);
    }

    private DomElement GetDocumentScrollingElement(DomElement documentElement) => documentElement;

    private DomElement FindNearestScrollParent(DomElement? start, DomElement documentElement)
    {
        for (var current = start; current != null; current = ParentEl(current))
        {
            if (ReferenceEquals(current, documentElement))
                return GetDocumentScrollingElement(documentElement);

            if (IsViewportBodyElement(current, documentElement) || !HasAssociatedLayoutBox(current))
                continue;

            if (CssOverflow.ClipsOverflow(GetComputedProps(current)))
                return current;
        }

        return GetDocumentScrollingElement(documentElement);
    }

    private DomElement? FindFixedPositionContainingBlock(DomElement element, DomElement documentElement)
    {
        for (var current = ParentEl(element); current != null; current = ParentEl(current))
        {
            if (ReferenceEquals(current, documentElement))
                break;

            if (EstablishesFixedPositionContainingBlock(current))
                return current;
        }

        return null;
    }

    private bool EstablishesFixedPositionContainingBlock(DomElement element)
    {
        var props = GetComputedProps(element);
        var transform = props.GetValueOrDefault("transform");
        if (!string.IsNullOrWhiteSpace(transform) &&
            !string.Equals(transform.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var contain = props.GetValueOrDefault("contain");
        if (string.IsNullOrWhiteSpace(contain))
            return false;

        var normalized = contain.Trim().ToLowerInvariant();
        return normalized.Contains("paint") ||
               normalized.Contains("layout") ||
               normalized.Contains("strict") ||
               normalized.Contains("content");
    }

    private bool HasAssociatedLayoutBox(DomElement element)
    {
        if (IsText(element))
            return false;

        if (element.TagName.StartsWith('#'))
            return false;

        var display = GetComputedProps(element).GetValueOrDefault("display")?.Trim().ToLowerInvariant();
        return !string.Equals(display, "none", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(display, "contents", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// RF-BRIDGE-1b: computes an element's scrollable overflow extent
    /// (<c>scrollWidth</c>/<c>scrollHeight</c>) from the shared renderer-layout
    /// snapshot — the union of the element's own padding-box (client) extent and the
    /// end edges of its rendered descendants' border boxes, expressed in the element's
    /// padding-box coordinate space and including the element's end padding. This
    /// mirrors the coarse estimator's element-descendant semantics but reads real box
    /// geometry from the provider instead of re-deriving it. Returns <c>false</c> when
    /// the element has no shared box (detached / <c>display:none</c>) so the caller
    /// falls back to the estimator.
    ///
    /// <para>The snapshot is in zoom-baked space, so the raw extent is divided by the
    /// element's own used zoom (<see cref="UnzoomSharedExtent"/>) to report the
    /// element's own unzoomed CSS pixels; a zoomed descendant keeps its larger baked
    /// extent, which correctly counts as scrollable overflow.</para>
    /// </summary>
    private bool TryGetSharedScrollExtent(DomElement element, bool vertical, out double extent)
    {
        extent = 0;
        if (!TryGetSharedLayoutGeometry(element, out var box))
            return false;

        var paddingBox = box.PaddingBox;
        var contentBox = box.ContentBox;

        // This measured overflow only toward larger physical coordinates — a descendant's Right or
        // Bottom, against the padding box's Left or Top. That is the whole story for an axis that
        // grows that way, and it found nothing at all for one that does not: a `vertical-rl` block
        // axis runs right-to-left, its content overflows to the LEFT, and scrollWidth therefore came
        // back equal to clientWidth. With no range, GetScrollBounds collapsed to [0, 0] and every
        // block-axis `scrollIntoView` clamped to zero. Chromium reports 600 for the 600px child in a
        // 300px `vertical-rl` scroller where this reported 300.
        //
        // A reversed axis measures BOTH ways and takes the larger, rather than simply measuring the
        // other way, because this engine's layout is not consistent about which side it mirrors:
        // `direction: rtl` in a horizontal writing mode lays the overflow out to the right (the
        // bridge mirrors it later, in the scroll-coordinate conversion), while `vertical-rl` lays it
        // out to the left. Measuring only the reversed side fixes the second and breaks the first.
        // Taking the larger is right whichever side the engine chose, and it cannot regress a
        // forward axis, which is measured exactly as before.
        var reversed = AxisUsesReversedScrollDirection(element, vertical);
        var origin = vertical ? paddingBox.Top : paddingBox.Left;
        var reversedOrigin = vertical ? paddingBox.Bottom : paddingBox.Right;
        // The scrolling area is at least the padding-box (client) extent, and the
        // scrollable overflow region includes the container's end padding.
        var max = vertical ? (double)paddingBox.Height : paddingBox.Width;
        var endPadding = vertical
            ? Math.Max(0, paddingBox.Bottom - contentBox.Bottom)
            : Math.Max(0, paddingBox.Right - contentBox.Right);
        var reversedEndPadding = vertical
            ? Math.Max(0, contentBox.Top - paddingBox.Top)
            : Math.Max(0, contentBox.Left - paddingBox.Left);

        // Trailing margins only extend the *viewport's* scrolling area (see
        // GetScrollableOverflowEndMargin). Percentage margins resolve against the containing
        // block's inline size, for which the scroll container's content width is the basis.
        var includeEndMargins = IsViewportElementForMetrics(element);
        var marginPercentageBasis = (double)contentBox.Width;

        foreach (var descendant in EnumerateScrollableOverflowDescendants(element))
        {
            if (!TryGetSharedLayoutGeometry(descendant, out var childBox))
                continue;

            double childEnd = vertical ? childBox.BorderBox.Bottom : childBox.BorderBox.Right;
            if (includeEndMargins)
            {
                childEnd += GetScrollableOverflowEndMargin(
                    descendant, vertical, reversed: false, marginPercentageBasis);
            }

            max = Math.Max(max, (childEnd - origin) + endPadding);

            if (!reversed)
                continue;

            double childStart = vertical ? childBox.BorderBox.Top : childBox.BorderBox.Left;
            if (includeEndMargins)
            {
                childStart -= GetScrollableOverflowEndMargin(
                    descendant, vertical, reversed: true, marginPercentageBasis);
            }

            max = Math.Max(max, (reversedOrigin - childStart) + reversedEndPadding);
        }

        // The snapshot is in zoom-baked space; report the extent in the element's own
        // unzoomed CSS pixels. Only the element's own used zoom is divided out, so a
        // zoomed descendant's larger baked extent still counts as overflow.
        extent = UnzoomSharedExtent(max, element);
        return true;
    }

    /// <summary>
    /// The end-edge (bottom / right) margin of <paramref name="descendant"/> that extends the
    /// <em>viewport's</em> scrolling area past the descendant's border box.
    ///
    /// <para>The document's scrolling area is its total layout height, which runs to the end of
    /// the last block's margin box — a trailing <c>margin-bottom</c> below the last block is
    /// scrollable. Unioning border boxes alone truncated the document by exactly that margin
    /// and clamped every programmatic scroll to a max that was too small (issue #1439:
    /// css-scroll-snap/scroll-snap-root-001 must scroll to 998.4px, but the missing
    /// <c>#target { margin-bottom: 120vh }</c> capped the document at 636.4px).</para>
    ///
    /// <para><b>This is deliberately restricted to the viewport.</b> For an ordinary element
    /// scroll container, a child's margin does not by itself create scrollable overflow — an
    /// auto-sized box grows to fit its children's margins, and a margin that collapses out of
    /// it is not content the box can scroll to. <c>Wpt_CssomView_AutoSized_ScrollMetrics_Do_Not
    /// _Report_MarginOnly_Overflow</c> pins that: margins alone must leave
    /// <c>scrollHeight == clientHeight</c>. Applying this to every scroll container broke it.</para>
    ///
    /// <para>Only positive margins extend the area: a negative margin pulls content back toward
    /// the origin and never adds scrollable space. The caller takes a <c>max</c> over all
    /// descendants, so a margin that collapses up through several ancestors is counted once at
    /// whichever box carries it rather than accumulating.</para>
    /// </summary>
    /// <summary>
    /// Whether this axis's scroll coordinates run backwards — the axis grows toward smaller physical
    /// coordinates, so its scroll offsets are negative and its content overflows toward the physical
    /// start edge.
    /// </summary>
    /// <remarks>
    /// The single owner of that question. <see cref="GetScrollBounds"/> asked it to decide the sign
    /// of the scroll range and <see cref="TryGetSharedScrollExtent"/> has to ask it to decide which
    /// way to measure overflow; the two disagreeing is exactly the bug that made a `vertical-rl`
    /// container report a scroll range of zero while its bounds were ready to go negative.
    /// </remarks>
    private bool AxisUsesReversedScrollDirection(DomElement element, bool vertical)
    {
        var props = GetComputedProps(element);
        var writingMode = props.GetValueOrDefault("writing-mode")?.Trim().ToLowerInvariant();
        var isRtl = string.Equals(props.GetValueOrDefault("direction"), "rtl", StringComparison.OrdinalIgnoreCase);
        var isVertical = CssWritingMode.IsVertical(writingMode);

        return vertical
            ? isVertical && isRtl
            : (isVertical && writingMode?.EndsWith("-rl", StringComparison.Ordinal) == true)
                || (string.Equals(writingMode, "horizontal-tb", StringComparison.OrdinalIgnoreCase) && isRtl);
    }

    private double GetScrollableOverflowEndMargin(
        DomElement descendant, bool vertical, bool reversed, double percentageBasis)
    {
        var props = GetComputedProps(descendant);

        // position: fixed boxes are anchored to the viewport, not to the scroll container's
        // content, so they contribute no scrollable overflow at all (and neither do their
        // margins). The border-box union above predates this helper and still includes them;
        // this only avoids *growing* that pre-existing behaviour.
        if (string.Equals(props.GetValueOrDefault("position"), "fixed", StringComparison.OrdinalIgnoreCase))
            return 0;

        // The margin on the axis's END side, which a reversed axis puts on the opposite physical
        // edge from a forward one.
        var margin = ParseCssLengthToPixelsWithViewport(
            props.GetValueOrDefault(vertical
                ? (reversed ? "margin-top" : "margin-bottom")
                : (reversed ? "margin-left" : "margin-right")),
            descendant,
            percentageBasis: percentageBasis);

        return double.IsFinite(margin) ? Math.Max(0, margin) : 0;
    }

    /// <summary>
    /// RF-BRIDGE-1b: converts a shared-snapshot extent (in the renderer's zoom-baked
    /// document space) into <paramref name="element"/>'s own unzoomed CSS pixels by
    /// dividing out its cumulative used zoom. A no-op for unzoomed elements
    /// (used zoom == 1). Reads used zoom from the live document, which the geometry
    /// snapshot pass restores after building (see the render-doc/live-doc separation),
    /// so the true zoom is readable here.
    /// </summary>
    private double UnzoomSharedExtent(double extent, DomElement element)
    {
        var zoom = GetUsedZoomForElement(element);
        return zoom > 0.0001 ? extent / zoom : extent;
    }

    /// <summary>
    /// RF-BRIDGE-1b: <paramref name="element"/>'s border-box extent along the axis from
    /// the shared renderer layout, in the element's own unzoomed CSS pixels. Returns
    /// <c>false</c> (caller falls back to the estimator) when the shared path is off or the
    /// element has no box.
    /// </summary>
    private bool TrySharedBorderBoxExtent(DomElement element, bool vertical, out double extent)
    {
        extent = 0;
        if (!UseSharedLayoutGeometry || !TryGetSharedLayoutGeometry(element, out var box))
            return false;
        extent = UnzoomSharedExtent(vertical ? box.BorderBox.Height : box.BorderBox.Width, element);
        return true;
    }

    /// <summary>
    /// RF-BRIDGE-1b: <paramref name="element"/>'s content-box extent along the axis from
    /// the shared renderer layout (the renderer's <c>ClientRectangle</c> is the content
    /// box), in the element's own unzoomed CSS pixels. Returns <c>false</c> (caller falls
    /// back to the estimator) when the shared path is off or the element has no box.
    /// </summary>
    private bool TrySharedContentBoxExtent(DomElement element, bool vertical, out double extent)
    {
        extent = 0;
        if (!UseSharedLayoutGeometry || !TryGetSharedLayoutGeometry(element, out var box))
            return false;
        extent = UnzoomSharedExtent(vertical ? box.ContentBox.Height : box.ContentBox.Width, element);
        return true;
    }

    private double GetScrollWidthForDomElement(DomElement element, bool isRoot) =>
        WithLayoutGeometryCache(() =>
        {
            if (TryGetSelectListBoxScrollExtent(element, verticalAxis: false, out var selectScrollWidth))
                return selectScrollWidth;

            // RF-BRIDGE-1b: scroll overflow comes from the shared renderer layout — the
            // union of the element's own box and its rendered descendants, including the
            // root/viewport scrolling area, computed by TryGetSharedScrollExtent. An
            // element with no shared box (detached / display:none) reports zero.
            if (TryGetSharedScrollExtent(element, vertical: false, out var sharedScrollWidth))
                return sharedScrollWidth;

            return 0;
        });

    private double GetScrollHeightForDomElement(DomElement element, bool isRoot) =>
        WithLayoutGeometryCache(() =>
        {
            if (TryGetSelectListBoxScrollExtent(element, verticalAxis: true, out var selectScrollHeight))
                return selectScrollHeight;

            // RF-BRIDGE-1b: scroll overflow comes from the shared renderer layout (see
            // GetScrollWidth). An element with no shared box reports zero.
            if (TryGetSharedScrollExtent(element, vertical: true, out var sharedScrollHeight))
                return sharedScrollHeight;

            return 0;
        });

    private (double Left, double Top, double Width, double Height) GetBoundingClientRectForDomElement(DomElement element, bool isRoot) =>
        WithLayoutGeometryCache(() =>
        {
            if (isRoot)
                return (0, 0, GetViewportReferenceLength(element, vertical: false), GetViewportReferenceLength(element, vertical: true));

            // An SVG shape is not in the CSS box tree, so the layout rect below is 0×0 for it. Its
            // geometry comes from its own attributes instead — the same resolution hit testing uses,
            // so the two cannot disagree about where a shape is (see LayoutMetrics.SvgGeometry.cs).
            if (TryGetSvgClientRect(element, out var svgRect))
                return svgRect;

            return ComputeRenderedRect(element);
        });

    private (double Left, double Top, double Width, double Height) ComputeRenderedRect(DomElement element)
    {
        var (Left, Top, Width, Height) = ComputeUnzoomedLayoutRect(element);
        var zoom = GetUsedZoomForElement(element);

        // getBoundingClientRect returns the VISUAL rect: the element's border box after every
        // CSS transform on it and its ancestors (offsetWidth/offset* stay the untransformed layout
        // box and read the snapshot directly). Apply the own+ancestor transform chain to the box
        // corners; an untransformed chain returns the box unchanged, byte-identical to before.
        return ApplyTransformChain(element, (Left, Top, Width * zoom, Height * zoom));
    }

    private (double Left, double Top, double Width, double Height) ComputeUnzoomedLayoutRect(DomElement element)
    {
        // Phase-5 LayoutSnapshot endgame: anchor-positioned boxes (position-area / anchor() / anchor-size()
        // / position-try) are resolved natively in the shared snapshot itself — HeadlessLayoutView enables
        // the engine's NativeAnchorPlacement post-pass, so the snapshot already carries their placed/sized
        // geometry. No bridge live resolver is needed; the plain snapshot path below serves every element.
        // RF-BRIDGE-1b: the element's rect comes from the renderer's real layout (border
        // box, document coords), which powers getBoundingClientRect and offset top/left.
        // The snapshot is in zoom-baked space and ComputeRenderedRect re-applies zoom to
        // the SIZE, so the size is divided back to the element's own unzoomed CSS pixels
        // here; the position stays in the rendered document coordinate space, which is
        // what getBoundingClientRect wants (a no-op for unzoomed elements). An element with
        // no shared box (detached / display:none) reports a zero rect.
        if (TryGetSharedLayoutGeometry(element, out var sharedGeometry))
        {
            var zoom = GetUsedZoomForElement(element);
            var inverseZoom = zoom > 0.0001 ? 1.0 / zoom : 1.0;
            return (
                (double)sharedGeometry.BorderBox.Left,
                (double)sharedGeometry.BorderBox.Top,
                sharedGeometry.BorderBox.Width * inverseZoom,
                sharedGeometry.BorderBox.Height * inverseZoom);
        }

        return (0, 0, 0, 0);
    }

}
