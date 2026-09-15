namespace Broiler.HtmlBridge;

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
