using Broiler.Dom;
using Broiler.CSS;

namespace Broiler.HtmlBridge;

// position-area resolution — scroll-container geometry helpers, split out of PositionArea.cs to keep each
// file under the Phase 3 750-line ratchet (HtmlBridgeArchitectureGuardTests). These compute the scrollable
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

    /// <summary>
    /// Returns <c>true</c> when <paramref name="el"/> is a descendant of
    /// <paramref name="potentialAncestor"/> in the DOM tree.
    /// </summary>
    private static bool IsDescendantOfElement(DomElement el, DomElement potentialAncestor)
    {
        var current = ParentEl(el);
        while (current != null)
        {
            if (ReferenceEquals(current, potentialAncestor)) return true;
            current = ParentEl(current);
        }
        return false;
    }
}
