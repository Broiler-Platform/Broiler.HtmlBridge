using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Broiler.CSS;
using Broiler.Dom;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

// position-area resolution — scroll-container geometry helpers for PositionArea.cs. These compute the scrollable
// content extents and locate the anchor's nearest scroll container so ComputePositionAreaRect can clamp a
// position-area cell to the scrollport.
public sealed partial class DomBridge
{
    /// <summary>
    /// Computes the total width of the scrollable content inside a scroll
    /// container by examining its children's widths and margins.
    /// Falls back to the container's own width if no explicit child widths
    /// are found.
    /// </summary>
    private double FindScrollContentWidth(DomElement scrollContainer, double containerWidth)
    {
        double maxWidth = containerWidth;
        foreach (var child in SnapshotChildren(scrollContainer))
        {
            if (IsText(child)) continue;
            var childProps = GetComputedProps(child);
            double? childW = TryParsePx(childProps.GetValueOrDefault("width"));
            if (childW.HasValue)
            {
                double ml = TryParsePx(childProps.GetValueOrDefault("margin-left")) ?? 0;
                double mr = TryParsePx(childProps.GetValueOrDefault("margin-right")) ?? 0;
                double totalW = childW.Value + ml + mr;
                if (totalW > maxWidth) maxWidth = totalW;
            }
        }
        return maxWidth;
    }

    /// <summary>
    /// Computes the total height of the scrollable content inside a scroll
    /// container.  In-flow block children stack vertically, so the scrollable
    /// extent is the <em>sum</em> of their heights and margins (not the tallest
    /// single child, which is what the inline/horizontal axis uses).  Absolutely
    /// and fixed positioned children are out of flow and do not contribute to
    /// the block-axis scroll extent.  The result is clamped to at least the
    /// container's own height (scrollHeight ≥ clientHeight).
    /// </summary>
    private double FindScrollContentHeight(DomElement scrollContainer, double containerHeight)
    {
        double stackedHeight = 0;
        foreach (var child in SnapshotChildren(scrollContainer))
        {
            if (IsText(child)) continue;
            var childProps = GetComputedProps(child);
            var pos = childProps.GetValueOrDefault("position");
            if (pos == "absolute" || pos == "fixed")
                continue;
            double childH = TryParsePx(childProps.GetValueOrDefault("height")) ?? 0;
            double mt = TryParsePx(childProps.GetValueOrDefault("margin-top")) ?? 0;
            double mb = TryParsePx(childProps.GetValueOrDefault("margin-bottom")) ?? 0;
            stackedHeight += childH + mt + mb;
        }
        return Math.Max(containerHeight, stackedHeight);
    }

    /// <summary>
    /// Computes the anchor's position relative to the specified container.
    /// When the anchor's containing block IS the container (e.g. the
    /// scroll container itself has position:relative), the anchor's
    /// coordinates from ComputeElementBox are already container-relative.
    /// Otherwise, both are in document coordinates and we subtract.
    /// </summary>
    private (double Left, double Top) ComputeAnchorRelativeToContainer(
        AnchorInfo anchor, DomElement container)
    {
        // Check if the container establishes a CB. If it does, the anchor's
        // ComputeElementBox walk will have stopped at the container, and
        // the returned coordinates are already container-relative.
        var containerProps = GetComputedProps(container);
        if (EstablishesContainingBlock(containerProps))
            return (anchor.Left, anchor.Top);

        var containerBox = ComputeElementBox(container);
        if (containerBox == null)
            return (anchor.Left, anchor.Top);
        return (anchor.Left - containerBox.Left, anchor.Top - containerBox.Top);
    }

    /// <summary>
    /// Finds the nearest ancestor of <paramref name="el"/> that is a scroll
    /// container (has <c>overflow: hidden/scroll/auto/clip</c>).
    /// </summary>
    private DomElement? FindNearestScrollContainer(DomElement el)
    {
        var parent = ParentEl(el);
        while (parent != null)
        {
            if (!IsText(parent))
            {
                var props = GetComputedProps(parent);
                if (CssOverflow.ClipsOverflow(props))
                    return parent;
            }
            parent = ParentEl(parent);
        }
        return null;
    }
}

public sealed partial class DomBridge
{
    // -----------------------------------------------------------------
    // Scroll simulation
    // -----------------------------------------------------------------

    /// <summary>
    /// Simulates scroll positions set via JavaScript (<c>element.scrollTop</c>,
    /// <c>element.scrollLeft</c>) by shifting children of scroll containers
    /// with negative margins.  Combined with <c>overflow: hidden</c>, this
    /// produces the same visual output as a real browser scroll.
    /// </summary>
    private void ApplyScrollSimulation(DomElement root) =>
        ApplyScrollSimulationTree(root, GetScrollSimulationScaleFactor());

    /// <summary>
    /// The same pass for every nested browsing context this session has materialised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A frame's document is severed from the main tree (P4.4b), so walking
    /// <see cref="DocumentElement"/> never reaches it: a frame whose script had scrolled something
    /// serialized with none of that state, and the capture showed the frame at its initial scroll
    /// position. The scroll offset was recorded correctly — <c>scrollTop</c> read back the value
    /// <c>scrollIntoView</c> had set — it simply never reached the markup.
    /// </para>
    /// <para>
    /// Sound to re-run per frame for the reason the top-layer passes are
    /// (<see cref="ApplySubDocumentTopLayer"/>): everything this reads is already per-element or
    /// per-document — the recorded scroll state, and a computed <c>overflow</c> resolved by that
    /// document's own style scope — and it needs no geometry, which is the thing this bridge only
    /// measures for the main frame.
    /// </para>
    /// <para>
    /// The visual-viewport scale is deliberately not applied inside a frame. Pinch zoom scales the
    /// frame's box as a whole, so scaling the offset the frame scrolled *within* itself would count
    /// the same zoom twice.
    /// </para>
    /// </remarks>
    private void ApplySubDocumentScrollSimulation()
    {
        // Snapshot: reading a computed style can materialise a further frame, which would otherwise
        // mutate the map mid-iteration.
        foreach (var contentDocument in _browsingContexts.ContentDocuments.ToList())
        {
            if (GetDocumentElement(contentDocument) is { } subRoot)
                ApplyScrollSimulationTree(subRoot, scrollScale: 1);
        }
    }

    private void ApplyScrollSimulationTree(DomElement el, double scrollScale)
    {
        if (!IsText(el))
        {
            double scrollTop = 0;
            double scrollLeft = 0;
            if (ScrollStateFor(el).Top.TryGet(out var st) && st is double stv)
                scrollTop = stv;
            if (ScrollStateFor(el).Left.TryGet(out var sl) && sl is double slv)
                scrollLeft = slv;

            if (!AreClose(scrollScale, 1))
            {
                scrollTop *= scrollScale;
                scrollLeft *= scrollScale;
            }

            if (scrollTop != 0 || scrollLeft != 0)
            {
                // Only apply to elements that clip overflow, or to the
                // document scrolling element (<html>) which is implicitly
                // clipped by the viewport.
                var props = GetComputedProps(el);
                bool clips = CssOverflow.ClipsOverflow(props);
                bool isDocScrollingElement =
                    string.Equals(el.TagName, "html", StringComparison.OrdinalIgnoreCase);

                if ((clips || isDocScrollingElement) && el.ChildNodes.Count > 0)
                {
                    // Hand the scroll offset to the Broiler.Layout engine via data attributes
                    // instead of DOM-shifting the content. The engine's scroll post-pass
                    // (CssBox.RunScrollSimulation) translates the container's content and its
                    // overflow box (or the viewport, for the document scrolling element) clips it —
                    // no wrapper div, no inline position/top/left/visibility writes, and no
                    // fixed-descendant reparenting (OffsetTop/OffsetLeft skip position:fixed at every
                    // depth, CSS2.1 §9.6.1). The document scrolling element (<html>) is included:
                    // with a scrollable root (tall content) documentElement.scrollTop resolves
                    // normally and the engine translation matches. The flag check is dropped in
                    // Phase 4 item-2 step 5 — the handoff is unconditional (a provable no-op on the
                    // native default path, where the flag was already true); the retired baked
                    // DOM-shift wrapper (and its scroll-hidden / anchor-cb markers) is deleted.
                    if (scrollTop != 0)
                        SetAttr(el, "data-broiler-scroll-top",
                            scrollTop.ToString(CultureInfo.InvariantCulture));
                    if (scrollLeft != 0)
                        SetAttr(el, "data-broiler-scroll-left",
                            scrollLeft.ToString(CultureInfo.InvariantCulture));

                    // Recurse into children (nested scroll containers) and skip the DOM-shift.
                    for (int i = 0; i < el.ChildNodes.Count; i++)
                        if (ChildAt(el, i) is DomElement scrolledChild)
                            ApplyScrollSimulationTree(scrolledChild, scrollScale);
                    return;
                }
            }
        }

        // Use index-based loop because the list may grow during iteration
        // (wrapper insertion above).
        for (int i = 0; i < el.ChildNodes.Count; i++)
            if (ChildAt(el, i) is DomElement child)
                ApplyScrollSimulationTree(child, scrollScale);
    }

    private double GetScrollSimulationScaleFactor() => HasActiveVisualViewport() ? GetVisualViewportScale() : 1;

}

/// <summary>
/// CSS Positioned Layout 3 §sticky: computes the offset of
/// <c>position: sticky</c> boxes and rewrites them to <c>position: relative</c>
/// with that offset, so the static Broiler renderer (which treats
/// <c>sticky</c> as static) reproduces the pinned position.
///
/// <para>This is a first increment: it handles the physical <c>top</c>/
/// <c>bottom</c>/<c>left</c>/<c>right</c> inset constraints against the
/// element's nearest scroll container (or the viewport), clamped to the
/// element's containing block, evaluated at the container's current scroll
/// offset.  It covers the common "pin to an edge" case — including a sticky
/// box inside a fixed ancestor (WPT css-position/sticky/
/// position-sticky-fixed-ancestor-002/003, #1316), where the box's natural
/// position is already outside the scrollport and it pins to the edge.</para>
/// </summary>
public sealed partial class DomBridge
{
    private void ResolveStickyPositioning(DomElement root) => ResolveStickyPositioningTree(root);

    private void ResolveStickyPositioningTree(DomElement el)
    {
        if (!IsText(el) && IsSticky(GetComputedProps(el)))
        {
            // A sticky box with a scroll container is handed to the Broiler.Layout engine's sticky
            // post-pass (CssBox.RunStickyPositioning): position:sticky is left un-baked so it
            // survives serialization → cascade → the engine, which pins it against the natively
            // scroll-shifted scrollport. The flag check is dropped in Phase 4 item-2 step 5 — the
            // MVP-skip is unconditional (a provable no-op on the native default path, where the
            // flag was already true); only the not-yet-native residue (no scroll container) bakes.
            if (!IsMvpNativeStickyBox(el))
                ApplyStickyOffset(el);
        }

        // Index-based: ApplyStickyOffset only mutates el's own style, not the
        // child list, but stay defensive.
        for (int i = 0; i < el.ChildNodes.Count; i++)
            if (ChildAt(el, i) is DomElement child)
                ResolveStickyPositioningTree(child);
    }

    /// <summary>
    /// Whether a <c>position: sticky</c> box is handled by the Broiler.Layout engine's native
    /// sticky post-pass in native mode (so the bridge skips pre-baking it to
    /// <c>relative</c> + offset): the box has a scroll container (a non-document clipping element
    /// or the document scrolling element for page scroll), which the engine pins against the
    /// natively scroll-shifted scrollport (<c>CssBox.TryGetStickyScrollport</c>). Anchor pages are
    /// included as of the twenty-fourth expansion: the native scroll handoff
    /// (<see cref="ApplyScrollSimulationTree"/>) is no longer scoped to no-anchor pages, so a
    /// sticky box's scroll container is engine-shifted on an anchor page too and the engine's
    /// sticky pass (run after scroll, before anchor placement) reads the shifted geometry.
    /// </summary>
    private bool IsMvpNativeStickyBox(DomElement el)
    {
        return FindScrollContainer(el) != null;
    }

    private void ApplyStickyOffset(DomElement el)
    {
        var props = GetComputedProps(el);

        var scrollContainer = FindScrollContainer(el);
        var containingBlock = GetStickyContainingBlock(el);
        if (scrollContainer == null || containingBlock == null)
            return;

        double dy = ComputeStickyShift(el, props, scrollContainer, containingBlock, vertical: true);
        double dx = ComputeStickyShift(el, props, scrollContainer, containingBlock, vertical: false);

        // The renderer treats `sticky` as static; expressing the resolved
        // position as `relative` + offset reproduces it.  Relative and sticky
        // both establish a containing block / stacking context, so this is
        // behaviour-preserving for descendants.
        BakedInlineStyle(el)["position"] = "relative";

        if (dy != 0)
        {
            BakedInlineStyle(el)["top"] = dy.ToString("0.###", CultureInfo.InvariantCulture) + "px";
            BakedInlineStyle(el).Remove("bottom");
        }

        if (dx != 0)
        {
            BakedInlineStyle(el)["left"] = dx.ToString("0.###", CultureInfo.InvariantCulture) + "px";
            BakedInlineStyle(el).Remove("right");
        }
    }

    private double ComputeStickyShift(DomElement el, Dictionary<string, string> props,
        DomElement scrollContainer, DomElement containingBlock, bool vertical)
    {
        string startInset = vertical ? "top" : "left";
        string endInset = vertical ? "bottom" : "right";
        bool hasStart = HasStickyInset(props.GetValueOrDefault(startInset));
        bool hasEnd = HasStickyInset(props.GetValueOrDefault(endInset));
        if (!hasStart && !hasEnd)
            return 0;

        bool scIsRoot = IsDocumentElement(scrollContainer);
        double scrollportSize = vertical
            ? GetClientHeightForDomElement(scrollContainer, scIsRoot)
            : GetClientWidthForDomElement(scrollContainer, scIsRoot);
        double scroll = GetElementScrollOffset(scrollContainer, vertical);
        // RF-BRIDGE-1b: prefer the renderer's real natural offset (scroll-aware shared
        // geometry) over the coarse estimator; the scroll container's own scroll is
        // subtracted here, matching ComputeOffsetWithinAncestor's caller contract.
        double naturalInScrollport = OffsetWithinAncestorPreferShared(el, scrollContainer, vertical) - scroll;
        double size = StickyBorderBoxSize(el, props, vertical);

        double shift = 0;
        if (hasStart)
        {
            double inset = ParseStickyInset(props.GetValueOrDefault(startInset), el, scrollContainer, vertical);
            double needed = inset - naturalInScrollport;      // push toward the end (down/right)
            if (needed > 0)
                shift = needed;
        }
        if (shift == 0 && hasEnd)
        {
            double inset = ParseStickyInset(props.GetValueOrDefault(endInset), el, scrollContainer, vertical);
            double overflow = (naturalInScrollport + size) - (scrollportSize - inset);
            if (overflow > 0)
                shift = -overflow;                            // push toward the start (up/left)
        }
        if (shift == 0)
            return 0;

        // Clamp so the box stays within its containing block's content box
        // (CSS Position 3: a sticky box never leaves its containing block).
        double naturalInCb = OffsetWithinAncestorPreferShared(el, containingBlock, vertical);
        double cbExtent = TrySharedContentBoxExtent(containingBlock, vertical, out var sharedCbExtent)
            ? sharedCbExtent
            : 0;
        double minShift = -naturalInCb;
        double maxShift = cbExtent - size - naturalInCb;
        if (maxShift < minShift)
            maxShift = minShift;

        return Math.Clamp(shift, minShift, maxShift);
    }

    private double StickyBorderBoxSize(DomElement el, Dictionary<string, string> props, bool vertical)
    {
        // RF-BRIDGE-1b: the renderer's real border-box size from the shared snapshot
        // (scroll-independent). With the estimators deleted, an explicit CSS border-box from
        // the computed props is the only fallback (a real in-flow sticky box is always in the
        // snapshot); a genuinely-boxless element reports 0.
        if (TrySharedBorderBoxExtent(el, vertical, out var sharedSize))
            return sharedSize;
        double size = vertical ? GetBorderBoxHeight(props, el) : GetBorderBoxWidth(props, el);
        return size > 0 ? size : 0;
    }

    private double ParseStickyInset(string? value, DomElement el, DomElement scrollContainer, bool vertical)
    {
        // Percentage insets on a sticky box resolve against the scroll
        // container's content-box size along the axis.
        double basis = vertical
            ? GetClientHeightForDomElement(scrollContainer, IsDocumentElement(scrollContainer))
            : GetClientWidthForDomElement(scrollContainer, IsDocumentElement(scrollContainer));
        return ParseCssLengthToPixelsWithViewport(value, el, percentageBasis: basis);
    }

    /// <summary>
    /// The containing block used to clamp a sticky box: its nearest ancestor
    /// that establishes a block-level box (skips inline/anonymous wrappers).
    /// </summary>
    private DomElement? GetStickyContainingBlock(DomElement el)
    {
        for (var current = GetScrollTraversalParent(el); current != null; current = GetScrollTraversalParent(current))
        {
            if (IsText(current))
                continue;
            var display = GetComputedProps(current).GetValueOrDefault("display") ?? "block";
            bool inlineLevel = display is "inline" or "inline-block" or "inline-table"
                or "inline-flex" or "inline-grid";
            if (!inlineLevel)
                return current;
        }
        return null;
    }
}

public sealed partial class DomBridge
{
    // -----------------------------------------------------------------
    // position-area resolution for JS offset queries
    // -----------------------------------------------------------------

    // RF-BRIDGE-1b (Milestone 2.5): the resolved position-area rect is memoized here
    // instead of in the retired ElementRuntimeState.Layout (LayoutRuntimeState). It is
    // both a perf cache — avoids rebuilding the anchor registry on every offset query —
    // and a re-entrancy guard: ResolvePositionAreaForElement is reachable from the
    // geometry entry points, so an already-resolved element short-circuits here before
    // re-entering resolution (rebuilding the registry per call recurses / corrupts shared
    // state — the failure mode that reverted the naive cache removal). RF-BRIDGE (Phase 2
    // item 4, 2026-07-17): this memo is now a per-bridge-instance CWT (was process-static),
    // de-globalizing one of the two remaining process-static per-element runtime tables. It
    // is still keyed by element identity, so a detached element's memo is collected with it,
    // and the invalidation (position-area / position-anchor mutation) and clone-copy
    // semantics are unchanged from the old static table.
    private readonly ConditionalWeakTable<DomElement, PositionAreaResolution>
        _positionAreaResolutions = [];

    private sealed class PositionAreaResolution
    {
        public double Left;
        public double Top;
        public double Width;
        public double Height;
    }

    private bool TryGetPositionAreaResolution(
        DomElement element, out (double left, double top, double width, double height) rect)
    {
        if (_positionAreaResolutions.TryGetValue(element, out var r))
        {
            rect = (r.Left, r.Top, r.Width, r.Height);
            return true;
        }

        rect = default;
        return false;
    }

    private void SetPositionAreaResolution(
        DomElement element, double left, double top, double width, double height) =>
        _positionAreaResolutions.AddOrUpdate(element, new PositionAreaResolution
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
        });

    internal void ClearPositionAreaResolution(DomElement element) =>
        _positionAreaResolutions.Remove(element);

    private void CopyPositionAreaResolution(DomElement source, DomElement target)
    {
        if (_positionAreaResolutions.TryGetValue(source, out var r))
            _positionAreaResolutions.AddOrUpdate(target, new PositionAreaResolution
            {
                Left = r.Left,
                Top = r.Top,
                Width = r.Width,
                Height = r.Height,
            });
    }

}
