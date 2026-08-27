using System;
using System.Collections.Generic;
using Broiler.Dom;
using System.Globalization;

namespace Broiler.HtmlBridge;

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

    private static bool HasStickyInset(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase);

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
