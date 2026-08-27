using Broiler.Dom;
using Broiler.CSS;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // -----------------------------------------------------------------
    // Containing block establishment (shared helper)
    // -----------------------------------------------------------------
    //
    // The bridge's EnsureContainingBlockPositioning pre-bake (which added position:relative to
    // transform/contain/will-change CB establishers so the static renderer treated them as CBs)
    // was deleted in Phase 4 item-2 step 3 — the Broiler.Layout engine resolves these containing
    // blocks natively (CssBox.EstablishesNonPositionAbsPosContainingBlock, the engine mirror of
    // the helper below). EstablishesContainingBlock stays: PositionArea / InlineContainingBlocks /
    // AnchorRegistry / Visibility still use it.

    /// <summary>
    /// Determines whether an element with the given CSS properties
    /// establishes a containing block for absolutely positioned descendants.
    /// Per CSS spec, this includes:
    /// <list type="bullet">
    ///   <item>position: relative/absolute/fixed/sticky</item>
    ///   <item>transform (any non-none value)</item>
    ///   <item>contain: layout/paint/strict/content</item>
    ///   <item>will-change: transform</item>
    /// </list>
    /// </summary>
    private static bool EstablishesContainingBlock(Dictionary<string, string> props)
    {
        if (props.TryGetValue("position", out var pos) &&
            (pos == "relative" || pos == "absolute" || pos == "fixed" || pos == "sticky"))
            return true;

        // The transform/contain/will-change trio is the canonical Broiler.CSS predicate
        // shared with the layout engine's native containing-block path.
        return CssContainingBlock.CreatedByTransformContainOrWillChange(
            props.GetValueOrDefault("transform"),
            props.GetValueOrDefault("contain"),
            props.GetValueOrDefault("will-change"));
    }
}
