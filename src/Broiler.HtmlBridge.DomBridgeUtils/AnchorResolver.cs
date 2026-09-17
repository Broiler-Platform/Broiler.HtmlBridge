using System.Globalization;
using System.Text.RegularExpressions;
using Broiler.CSS;
using Broiler.Dom;
using Broiler.Layout;

namespace Broiler.HtmlBridge;

internal sealed record AnchorInfo(double Top, double Left, double Width, double Height, DomElement? SourceElement = null)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Tags whose rendering is already replaced (or which generate no box at all), so CSS
    /// Content 3 element replacement does not apply to them here.
    /// </summary>
    internal static readonly HashSet<string> ContentReplacementSkipTags =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "area", "audio", "base", "br", "canvas", "col", "embed", "head", "hr", "iframe",
            "img", "input", "link", "meta", "object", "param", "script", "select", "source",
            "style", "textarea", "title", "track", "video",
        };

    /// <summary>
    /// Extracts the target of a CSS <c>url(...)</c> value (used by the root
    /// element-replacement path).  Returns <c>null</c> for non-<c>url()</c>
    /// content (strings, counters, <c>normal</c>/<c>none</c>).
    /// </summary>
    internal static string? ExtractContentImageUrl(string content)
    {
        var match = ExtractContentImageUrlRegex().Match(content);
        return match.Success ? match.Groups["u"].Value.Trim() : null;
    }

    [GeneratedRegex(@"url\(\s*(['""]?)(?<u>[^'""\)]+)\1\s*\)", RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ExtractContentImageUrlRegex();
}

public static partial class DomBridgeUtils
{
    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    internal static double? TryParsePx(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value!.Trim();
        if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            v = v[..^2];
        // Don't parse pure numbers without px suffix if they contain '%'
        if (v.Contains('%')) return null;
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            return result;
        return null;
    }
    /// <summary>
    /// Tries to parse a CSS percentage value (e.g. "50%") and returns
    /// the numeric value (e.g. 50.0).
    /// </summary>
    internal static double? TryParsePercent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value!.Trim();
        if (!v.EndsWith('%')) return null;
        v = v[..^1];
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            return result;
        return null;
    }

    /// <summary>
    /// Resolves a CSS value that may be a percentage or a pixel length.
    /// Percentages are resolved against <paramref name="reference"/>.
    /// Returns 0 for values that cannot be parsed.
    /// </summary>
    internal static double ResolvePctOrPx(string value, double reference)
    {
        var pct = TryParsePercent(value);
        if (pct.HasValue)
            return reference * pct.Value / 100.0;
        return TryParsePx(value) ?? 0;
    }

    /// <summary>
    /// Returns true if the value contains a CSS percentage token.
    /// </summary>
    internal static bool HasPercent(string? value) => value != null && value.Contains('%');

}

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Parses the 'margin' shorthand into individual margin values,
    /// only overwriting values that are still at their defaults (0).
    /// </summary>
    internal static void ParseMarginShorthand(
        Dictionary<string, string> props,
        ref double marginLeft, ref double marginTop, ref double marginRight)
    {
        if (marginLeft == 0 && marginTop == 0 && marginRight == 0 &&
            props.TryGetValue("margin", out var marginShorthand))
        {
            var parts = marginShorthand.Trim().Split(null as char[], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1)
                marginTop = TryParsePx(parts[0]) ?? 0;
            if (parts.Length >= 2)
            {
                marginRight = TryParsePx(parts[1]) ?? 0;
                marginLeft = TryParsePx(parts[1]) ?? 0;
            }
            else if (parts.Length == 1)
                marginLeft = marginRight = marginTop;
            if (parts.Length >= 4)
                marginLeft = TryParsePx(parts[3]) ?? 0;
        }
    }
}

public static partial class DomBridgeUtils
{
    /// <summary>
    /// True when the computed <c>position</c> in <paramref name="props"/> is
    /// <c>sticky</c>.
    /// </summary>
    internal static bool IsSticky(Dictionary<string, string> props) =>
        string.Equals(props.GetValueOrDefault("position"), "sticky", StringComparison.OrdinalIgnoreCase);
    internal static bool IsLayoutProperty(string prop) => prop switch
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
    internal static AnchorInsetProperty MapAnchorInsetProperty(string property) => property switch
    {
        "right" => AnchorInsetProperty.Right,
        "bottom" => AnchorInsetProperty.Bottom,
        "left" => AnchorInsetProperty.Left,
        "top" => AnchorInsetProperty.Top,
        _ => AnchorInsetProperty.Other,
    };
}

public static partial class DomBridgeUtils
{
    /// <summary>Whether a physical inset in <paramref name="props"/> is present and not
    /// <c>auto</c>.</summary>
    internal static bool HasInset(Dictionary<string, string> props, string name) =>
        props.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) && v.Trim() != "auto";

    /// <summary>Whether a <c>width</c>/<c>height</c> value is <c>auto</c> (or unset), so an
    /// opposing pair of insets determines the used size.</summary>
    internal static bool IsAutoLength(string? value) =>
        string.IsNullOrWhiteSpace(value) || value!.Trim() == "auto";

    /// <summary>
    /// Whether a <c>width</c>/<c>height</c> value is an intrinsic keyword the engine sizes from the
    /// box's real laid-out content — <c>min-content</c>, <c>max-content</c>, or bare
    /// <c>fit-content</c>. For these the engine's native position-try pass reads the box's actual
    /// laid-out extent (<c>Bounds.Width</c>) for its overflow test, so the box is handed off (the
    /// engine's size is at least as correct as — and, where the bridge's crude
    /// <c>EstimateMinContentWidth</c> heuristic mis-measures a max/fit box <em>as</em> min-content,
    /// more correct than — the baked estimate). All three go through the identical engine mechanism
    /// (P5.8d.2b validated <c>min-content</c>; <c>max-content</c>/<c>fit-content</c> differ only in
    /// the laid-out size the engine already computes). The functional <c>fit-content(&lt;length&gt;)</c>
    /// form is intentionally excluded (it is not a bare keyword).
    /// </summary>
    internal static bool IsEngineSizedIntrinsic(string? value)
    {
        var t = (value ?? string.Empty).Trim();
        return t.Equals("min-content", StringComparison.OrdinalIgnoreCase)
            || t.Equals("max-content", StringComparison.OrdinalIgnoreCase)
            || t.Equals("fit-content", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether one axis of a position-try base has a used size the engine reproduces for its
    /// overflow test: either a definite pixel length with a single inset (a reposition base),
    /// or — for a childless box — an opposing-inset auto length, where both insets are present
    /// with an <c>auto</c> length so the engine sizes the box from the two insets
    /// (<c>CssBox.TryApplyAnchorInsetPlacement</c>'s opposing-inset path). A
    /// <c>min-content</c>/free-<c>auto</c> length has no bridge-matching engine size, and a
    /// definite length combined with opposing insets is over-constrained; both stay baked.
    /// </summary>
    internal static bool AxisSizeHandoffSupported(
        Dictionary<string, string> merged, string lengthProp, string startInset, string endInset,
        bool childless)
    {
        bool opposing = HasInset(merged, startInset) && HasInset(merged, endInset);
        if (TryParsePx(merged.GetValueOrDefault(lengthProp)).HasValue)
            return !opposing;
        // An intrinsic-keyword base (`min-content`/`max-content`/`fit-content`) is handed off: the
        // engine's native position-try pass reads the box's real laid-out intrinsic size for its
        // overflow test (CssBox.TryApplyPositionTryFallback), which is at least as correct as — and,
        // for content the bridge's crude EstimateMinContentWidth heuristic mis-measures (it sizes a
        // max/fit box as min-content), more correct than — the baked estimate. Validated by
        // position-try-002 (an opposing-inset min-content base) rendering identically native, and by
        // MaxContentBase_LeavesBoxUnbaked / the max-content live-geometry regression for the max/fit
        // extension (they go through the identical engine read-real-size mechanism).
        if (IsEngineSizedIntrinsic(merged.GetValueOrDefault(lengthProp)))
            return true;
        return childless && opposing && IsAutoLength(merged.GetValueOrDefault(lengthProp));
    }
}

public static partial class DomBridgeUtils
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
    internal static bool EstablishesContainingBlock(Dictionary<string, string> props)
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

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Determines if an element is an inline element based on its tag name
    /// and display property.
    /// </summary>
    internal static bool IsInlineElement(string tagName, string? display)
    {
        if (display != null)
        {
            var d = display.Trim().ToLowerInvariant();
            // inline-block establishes a containing block for abspos children
            // and is treated as block-level for layout purposes, so it is
            // NOT considered inline here.
            if (d == "inline") return true;
            if (d == "block" || d == "flex" || d == "grid" || d == "table" ||
                d == "list-item" || d == "flow-root" || d == "inline-block" ||
                d == "inline-flex" || d == "inline-grid")
                return false;
        }
        // Default inline elements.
        var tag = tagName.ToLowerInvariant();
        return tag is "span" or "a" or "strong" or "em" or "b" or "i" or
               "code" or "small" or "big" or "sub" or "sup" or "abbr" or
               "cite" or "q" or "mark" or "label" or "time";
    }

    /// <summary>
    /// Resolves the computed line-height from CSS properties.
    /// Handles unitless values (multipliers of font-size), pixel values,
    /// and the "normal" keyword (defaults to 1.2 × font-size).
    /// </summary>
    internal static double ResolveLineHeight(Dictionary<string, string> props, double fontSize)
    {
        string? lh = props.GetValueOrDefault("line-height");
        if (string.IsNullOrWhiteSpace(lh) || lh == "normal")
            return fontSize * 1.2;

        var v = lh!.Trim();

        // Explicit pixel value.
        if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            double? px = TryParsePx(v);
            if (px.HasValue) return px.Value;
        }

        // Unitless: a multiplier of font-size.
        if (double.TryParse(v, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var multiplier))
            return fontSize * multiplier;

        return fontSize * 1.2;
    }
}

public static partial class DomBridgeUtils
{
    internal static string ResolveAnchorEdge(AnchorFunctionRef reference, Dictionary<string, AnchorInfo> registry,
        string contextProp, double cbWidth, double cbHeight, string? implicitAnchor = null)
    {
        var anchorName = string.IsNullOrEmpty(reference.Name)
            ? (implicitAnchor ?? string.Empty)
            : reference.Name!;

        if (!registry.TryGetValue(anchorName, out var anchor))
            return "0px";

        // Edge coordinate math (no scroll adjustment on the fallback path) is the
        // canonical Broiler.Layout.AnchorGeometry model (Phase 5 item 3).
        double value = AnchorGeometry.ResolveEdge(
            anchor.Left, anchor.Top, anchor.Right, anchor.Bottom,
            reference.Side, 0, 0, MapAnchorInsetProperty(contextProp), cbWidth, cbHeight);

        return $"{value.ToString(CultureInfo.InvariantCulture)}px";
    }
}

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
    /// colour by <c>DomBridge.GetBackdropBackground</c> — so everything else in the
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
