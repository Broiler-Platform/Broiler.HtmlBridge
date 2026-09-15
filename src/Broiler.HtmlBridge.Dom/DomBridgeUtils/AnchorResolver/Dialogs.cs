using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    // CSS Position 4 §top-layer: benign marker the renderer's native top-layer paint pass keys
    // on (Broiler.Layout FragmentTreeBuilder → Fragment.TopLayerOrder → PaintWalker.PaintTopLayer,
    // patch 0010 — applied and pinned). The attribute value is the element's top-layer order; a
    // later-added element (higher order) paints over an earlier one. Stamping it lets the native
    // pass paint modal dialogs, open popovers, and ::backdrops above every ordinary stacking
    // context — the correct top-layer behaviour, superseding the approximate very-large-z-index
    // emulation (now written only on the retired NativeTopLayer-off rollback path).
    internal const string TopLayerOrderAttr = "data-broiler-top-layer";

    // Native ::backdrop marker: the resolved backdrop background (UA modal/popover scrim default
    // folded with any author `background`) the renderer materialises into a native ::backdrop box
    // (Broiler.HTML DomParser, patch 0011 — applied and pinned). Stamped in NativeBackdrop mode;
    // the baked path inserts a styled <div> instead. The <div> path is still retained (not yet
    // deletable) because it carries author `::backdrop` position-try-fallbacks, which the native
    // path does not yet reproduce (see InsertDialogBackdrops).
    internal const string BackdropBgAttr = "data-broiler-backdrop";

    // The HTML UA `dialog:modal` inset/margin properties (checked both as their shorthands and
    // as longhands): any author declaration on these means the page positions the modal itself,
    // so the UA centring default must not fight it.
    internal static readonly string[] ModalPositioningProps =
    [
        "inset", "inset-block", "inset-inline", "left", "right", "top", "bottom",
        "margin", "margin-block", "margin-inline",
        "margin-left", "margin-right", "margin-top", "margin-bottom",
    ];

    /// <summary>
    /// Applies UA popover positioning and top-layer elevation to open popovers (HTML §popover).
    /// <para>
    /// The UA sheet's <c>[popover] { position: fixed; inset: 0 }</c> is unconditional — it is not
    /// gated on top-layer membership — so every open popover with no explicit position becomes a
    /// fixed box anchored at the viewport origin. Top-layer <em>elevation</em> is the separate,
    /// conditional half: a popover held out by a running <c>overlay</c> entry transition is still an
    /// out-of-flow fixed box, it just does not paint in the top layer yet. Later-shown popovers keep
    /// their source order, so they paint over earlier ones — matching the top-layer stacking these
    /// overlay tests probe.
    /// </para>
    /// </summary>
    // Base z-index for the synthetic top layer. The real top layer sits above
    // every painted stacking context (CSS Position §top-layer); Broiler has no
    // dedicated top-layer paint pass, so approximate it with a very large
    // z-index offset by each element's top-layer order, keeping open popovers
    // above ordinary positioned content and correctly ordered amongst themselves
    // (a later-shown popover paints over an earlier one). Kept below int.MaxValue
    // so the counter has headroom.
    internal const int TopLayerZIndexBase = 2_000_000_000;

    /// <summary>
    /// Property names on a <c>::backdrop</c> rule that control the backdrop's
    /// geometry and fallback positioning. When the author declares any of
    /// these they override the viewport-covering defaults so an explicitly
    /// sized or positioned backdrop is honoured (e.g. WPT
    /// <c>position-try-backdrop.html</c>, where the backdrop is a 100×100 box
    /// moved by <c>position-try-fallbacks</c>).
    /// </summary>
    private static readonly string[] BackdropGeometryProps =
    [
        "width", "height", "left", "right", "top", "bottom",
        "position", "position-anchor", "position-try-fallbacks", "position-try",
    ];

    /// <summary>
    /// Painted (non-geometry) properties an author <c>::backdrop</c> rule can set that change
    /// how the scrim composites rather than where it sits. Only <c>background</c> /
    /// <c>background-color</c> were being carried across — folded into the resolved backdrop
    /// colour by <see cref="DomBridge.GetBackdropBackground"/> — so everything else in the
    /// <c>::backdrop</c> cascade was silently dropped: <c>opacity: 0.5</c> on a green scrim
    /// painted fully opaque green instead of compositing to <c>rgb(127,191,127)</c> over the
    /// white canvas (WPT <c>the-dialog-element/modal-dialog-backdrop-opacity</c>, 2.2% match).
    /// <para>Deliberately narrow: these are the properties whose effect on the synthesized
    /// <c>&lt;div&gt;</c> is the same as on a real <c>::backdrop</c> box. Inherited and
    /// layout-affecting properties stay out — the div is a bridge implementation detail, not a
    /// faithful pseudo-element, and copying those across would leak into its subtree.</para>
    /// </summary>
    private static readonly string[] BackdropPaintingProps =
    [
        "opacity", "mix-blend-mode", "border-radius", "box-shadow",
    ];

    /// <summary>
    /// Overlays author-declared <c>::backdrop</c> painting properties (see
    /// <see cref="BackdropPaintingProps"/>) onto the synthesized backdrop div's style. The
    /// background is not among them — it is already resolved into the div's
    /// <c>background-color</c>, folded with the UA modal/popover scrim default.
    /// </summary>
    internal static void OverlayBackdropAuthorPainting(
        IReadOnlyDictionary<string, string> declarations,
        Dictionary<string, string> backdropStyle)
    {
        foreach (var prop in BackdropPaintingProps)
        {
            if (declarations.TryGetValue(prop, out var value) &&
                !string.IsNullOrWhiteSpace(value))
                backdropStyle[prop] = value.Trim();
        }
    }

    /// <summary>
    /// Overlays author-declared <c>::backdrop</c> geometry / fallback
    /// properties onto the synthesized backdrop div's style, replacing the
    /// viewport-covering defaults where the author was explicit.
    /// </summary>
    internal static void OverlayBackdropAuthorGeometry(
        IReadOnlyDictionary<string, string> declarations,
        Dictionary<string, string> backdropStyle)
    {
        foreach (var prop in BackdropGeometryProps)
        {
            if (declarations.TryGetValue(prop, out var value) &&
                !string.IsNullOrWhiteSpace(value))
                backdropStyle[prop] = value.Trim();
        }

        // The default fills the viewport with top:0/left:0 + width/height. If
        // the author positions the backdrop from the opposite edge only, drop
        // the conflicting default inset so the box is not over-constrained
        // (the renderer cannot resolve opposing left+right / top+bottom insets).
        if (declarations.ContainsKey("right") && !declarations.ContainsKey("left"))
            backdropStyle.Remove("left");
        if (declarations.ContainsKey("bottom") && !declarations.ContainsKey("top"))
            backdropStyle.Remove("top");
    }

    internal static bool HasClass(DomElement element, string name) =>
        (element.ClassName ?? string.Empty)
            .Split((char[])[' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries)
            .Contains(name, StringComparer.Ordinal);
}
