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
    /// A frame's document is severed from the main tree, so walking
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
                    // normally and the engine translation matches. The handoff is unconditional.
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

public sealed partial class DomBridge
{
    // -----------------------------------------------------------------
    // position-area resolution for JS offset queries
    // -----------------------------------------------------------------

    // The resolved position-area rect is memoized here: a perf cache that avoids rebuilding
    // the anchor registry on every offset query. The table is a per-bridge-instance CWT keyed
    // by element identity, so a detached element's memo is collected with it. It is invalidated
    // on a position-area / position-anchor mutation and copied on clone.
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

public sealed partial class DomBridge
{
    // -----------------------------------------------------------------
    // Shared anchor-lookup helpers
    // -----------------------------------------------------------------
    //
    // The Broiler.Layout engine resolves position-visibility natively
    // (CssBox.ResolvePositionVisibility), so the bridge pre-bakes no display:none onto
    // anchor-positioned elements whose anchor is scrolled out / invalid. The anchor-lookup
    // helpers below are still used by PositionArea / AnchorRegistry to bind targets to their
    // anchor and containing block.

    /// <summary>
    /// Finds the <see cref="Broiler.Dom.DomElement"/> that has the given
    /// <c>anchor-name</c> (from CSS rules or inline styles).
    /// </summary>
    private DomElement? FindElementByAnchorName(string anchorName)
    {
        foreach (var el in Elements)
        {
            if (IsText(el)) continue;
            // Check inline styles first.
            if (BakedInlineStyle(el).TryGetValue("anchor-name", out var n) &&
                string.Equals(n.Trim(), anchorName, StringComparison.Ordinal))
                return el;
        }

        // Fall back to the shared cascade.
        foreach (var el in Elements)
        {
            if (IsText(el)) continue;
            var declarations = CollectMatchedRuleProperties(el);
            if (declarations.TryGetValue("anchor-name", out var name) &&
                string.Equals(name.Trim(), anchorName, StringComparison.Ordinal))
                return el;
        }
        return null;
    }

    /// <summary>
    /// Finds the nearest positioned ancestor that serves as the containing
    /// block for an absolutely positioned element.
    /// </summary>
    private DomElement? FindContainingBlockElement(DomElement el)
    {
        var parent = ParentEl(el);
        while (parent != null)
        {
            var pProps = GetComputedProps(parent);
            if (EstablishesContainingBlock(pProps))
                return parent;
            parent = ParentEl(parent);
        }
        return null;
    }

    /// <summary>
    /// Finds the containing block for the anchor referenced by the target element.
    /// The anchor's CB is typically the same as the target's CB when both are
    /// inside the same positioned ancestor.
    /// </summary>
    private DomElement? FindAnchorContainingBlock(DomElement target)
    {
        // Find the anchor element by looking at the target's position-anchor.
        var cssProps = GetComputedProps(target);
        string? posAnchor = cssProps.GetValueOrDefault("position-anchor");
        if (string.IsNullOrWhiteSpace(posAnchor)) return null;

        var anchorEl = FindElementByAnchorName(posAnchor);
        if (anchorEl == null) return null;

        return FindContainingBlockElement(anchorEl);
    }
}
