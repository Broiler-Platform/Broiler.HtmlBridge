using Broiler.Dom;
using Broiler.CSS;

namespace Broiler.HtmlBridge;

/// <summary>
/// CSS Scroll Snap (Level 1) snap-position resolution for programmatic scrolls.
///
/// <para>A scroll container whose <c>scroll-snap-type</c> names an axis with the
/// <c>mandatory</c> strictness must come to rest on a snap position on that axis after
/// <em>any</em> scroll — including <c>scrollIntoView</c>, <c>scrollTo</c>/<c>scroll</c>,
/// <c>scrollBy</c>, and a plain <c>scrollTop</c>/<c>scrollLeft</c> assignment (CSS Scroll
/// Snap 1 §2, §6.1). Snapping is therefore applied at the single choke point where a
/// requested offset becomes the stored one (<c>ResolveElementScrollOffsets</c>), rather than
/// in any individual scrolling entry point.</para>
///
/// <para>Only <c>mandatory</c> snapping is implemented. <c>proximity</c> leaves the choice of
/// whether a candidate is "close enough" up to the UA, so declining to snap is a conforming
/// outcome and matches what the engine did before.</para>
///
/// <para>Root propagation falls out of the existing container model: for the viewport the
/// scroll container element is <c>&lt;html&gt;</c>, so <c>scroll-snap-type</c> and
/// <c>scroll-padding</c> are read from the root element and <c>&lt;body&gt;</c> is never
/// consulted — which is exactly the distinction css-scroll-snap/scroll-snap-root-002 asserts
/// (issue #1439).</para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Returns the snap position closest to <paramref name="proposedOffset"/> on
    /// <paramref name="vertical"/>'s axis, or <paramref name="proposedOffset"/> unchanged when
    /// <paramref name="scrollContainer"/> is not a mandatory snap container on that axis or
    /// has no snap areas.
    /// </summary>
    /// <param name="proposedOffset">The already-clamped offset the scroll would land on.</param>
    private double ResolveScrollSnapPosition(DomElement scrollContainer, bool vertical, double proposedOffset)
    {
        if (!double.IsFinite(proposedOffset) || !HasMandatoryScrollSnapOnAxis(scrollContainer, vertical))
            return proposedOffset;

        var (minLeft, maxLeft, minTop, maxTop) = GetScrollBounds(scrollContainer);
        var min = vertical ? minTop : minLeft;
        var max = vertical ? maxTop : maxLeft;

        var best = proposedOffset;
        var bestDistance = double.PositiveInfinity;

        foreach (var descendant in EnumerateRenderedDescendants(scrollContainer))
        {
            var alignment = GetScrollSnapAlignmentForAxis(descendant, scrollContainer, vertical);
            if (alignment == null)
                continue;

            // A box only snaps in its own nearest scroll container, so a snap area inside a
            // nested scroller must not pull this container's offset around.
            if (!ReferenceEquals(FindScrollContainer(descendant), scrollContainer))
                continue;

            var candidate = ResolveScrollIntoViewOffset(descendant, scrollContainer, vertical, alignment);
            if (!double.IsFinite(candidate))
                continue;

            candidate = Math.Clamp(candidate, min, max);

            var distance = Math.Abs(candidate - proposedOffset);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether <paramref name="scrollContainer"/> declares <c>scroll-snap-type</c> with
    /// <c>mandatory</c> strictness covering <paramref name="vertical"/>'s axis. The axis
    /// keyword may be physical (<c>x</c>/<c>y</c>), logical (<c>block</c>/<c>inline</c>, which
    /// resolve through the container's <c>writing-mode</c>), or <c>both</c>.
    /// </summary>
    private bool HasMandatoryScrollSnapOnAxis(DomElement scrollContainer, bool vertical)
    {
        var props = GetComputedProps(scrollContainer);
        var declared = props.GetValueOrDefault("scroll-snap-type");
        if (string.IsNullOrWhiteSpace(declared))
            return false;

        var tokens = declared.Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens[0] == "none")
            return false;

        // Strictness is optional and defaults to `proximity`, which this implementation
        // deliberately leaves un-snapped (see the type remarks).
        if (!tokens.Contains("mandatory"))
            return false;

        var blockAxisIsVertical = !CssWritingMode.IsVertical(props.GetValueOrDefault("writing-mode"));
        return tokens[0] switch
        {
            "both" => true,
            "x" => !vertical,
            "y" => vertical,
            "block" => vertical == blockAxisIsVertical,
            "inline" => vertical != blockAxisIsVertical,
            _ => false,
        };
    }

    /// <summary>
    /// The physical <c>start</c>/<c>end</c>/<c>center</c> alignment <paramref name="element"/>
    /// requests on <paramref name="vertical"/>'s axis, or null when it declares no
    /// <c>scroll-snap-align</c> or opts that axis out with <c>none</c>.
    ///
    /// <para><c>scroll-snap-align</c> takes one or two logical keywords in block-then-inline
    /// order, so they are mapped to physical axes through the same writing-mode/direction
    /// resolution <c>scrollIntoView</c>'s <c>block</c>/<c>inline</c> options use.</para>
    /// </summary>
    private string? GetScrollSnapAlignmentForAxis(DomElement element, DomElement scrollContainer, bool vertical)
    {
        var declared = GetComputedProps(element).GetValueOrDefault("scroll-snap-align");
        if (string.IsNullOrWhiteSpace(declared))
            return null;

        var tokens = declared.Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return null;

        var block = tokens[0];
        var inline = tokens.Length > 1 ? tokens[1] : tokens[0];
        if (!IsScrollSnapAlignmentKeyword(block) || !IsScrollSnapAlignmentKeyword(inline))
            return null;

        // Map through the container's writing mode by asking for the physical alignments of
        // this logical pair, then keep only the axis being snapped. "none" is checked on the
        // logical value first, because the physical resolution normalises it away.
        var blockAxisIsVertical = !CssWritingMode.IsVertical(
            GetComputedProps(scrollContainer).GetValueOrDefault("writing-mode"));
        var logicalForAxis = vertical == blockAxisIsVertical ? block : inline;
        if (logicalForAxis == "none")
            return null;

        var (horizontal, verticalAlignment) =
            ResolvePhysicalScrollIntoViewAlignments(scrollContainer, block, inline);
        return vertical ? verticalAlignment : horizontal;
    }

    private static bool IsScrollSnapAlignmentKeyword(string token) =>
        token is "none" or "start" or "end" or "center";
}
