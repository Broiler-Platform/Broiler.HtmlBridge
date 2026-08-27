using System.Globalization;
using System.Linq;
using Broiler.CSS;
using Broiler.Dom;
using Broiler.Layout;

namespace Broiler.HtmlBridge;

// Native anchor()/anchor-size() handoff gating (Phase 5, P5.8d.2b). These predicates
// decide whether an anchored box is the MVP subset the layout engine reproduces
// natively (so the bridge skips pre-baking), split out of AnchorFunctions.cs to keep
// each anchor-resolver file under the Phase-3 750-line guard.
public sealed partial class DomBridge
{
    /// <summary>
    /// Whether an element's <c>anchor()</c> insets are the MVP subset the engine's native
    /// placement post-pass reproduces (P5.8d.2b), so the bridge can hand them off instead of
    /// pre-baking (see <see cref="CssBox"/>'s <c>TryApplyAnchorInsetPlacement</c>). Requires:
    /// every <c>anchor()</c> reference is in a physical inset (<c>left</c>/<c>right</c>/
    /// <c>top</c>/<c>bottom</c>) and names a registered, accessible anchor; no
    /// <c>anchor-size()</c>; at most one inset per axis (opposing-inset sizing needs a re-flow
    /// the reposition-only pass can't do); the box is not fixed/modal and has no intervening
    /// scroll offset (the engine uses no scroll adjustment); and no <c>position-try</c>.
    /// </summary>
    private bool IsMvpNativeAnchorInsetBox(
        DomElement element, Dictionary<string, string> cssProps,
        Dictionary<string, AnchorInfo> anchorRegistry,
        Dictionary<string, Dictionary<string, string>>? positionTryRules = null)
    {
        // Merge inline styles over matched-rule props (inline wins), matching what the
        // engine cascade projects onto the box.
        var merged = new Dictionary<string, string>(cssProps, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in BakedInlineStyle(element))
            merged[kv.Key] = kv.Value;

        // Whether the box has no in-flow content — the engine's opposing-inset sizing and the
        // position-try fallback resize are childless-only (a childful box would need a re-flow).
        bool childless = !ChildElements(element).Any()
            && string.IsNullOrWhiteSpace(GetElementTextContent(element));

        // position-try is admitted only for the subset the engine's native fallback pass
        // reproduces (P5.8d.2b position-try expansion): the @position-try rule bodies must be
        // available to the engine (the channel) and the box's base must be a size the engine can
        // reproduce for its overflow test — a definite-size single-inset reposition, or a
        // childless opposing-inset auto base (P5.8d.2b opposing-inset position-try expansion).
        // Otherwise the box (base + fallback) stays fully baked on the bridge path.
        if ((merged.ContainsKey("position-try-fallbacks") || merged.ContainsKey("position-try"))
            && !NativePositionTryHandoffSupported(merged, positionTryRules, childless))
            return false;

        // A NON-modal fixed target gets a document-scroll adjustment the engine MVP does not
        // apply — keep it baked. A MODAL dialog (top-layer, UA position:fixed) IS handed off
        // (P5.8d.2b modal-dialog anchor() expansion): on an anchor page document scroll uses
        // the bridge DOM-shift (DocumentHasAnchorContent scopes out the native scroll), so the
        // anchor's box geometry the engine reads already reflects the scroll and no adjustment
        // is needed. The engine has no top-layer accessibility model, but it does not need one:
        // the registered+`IsAnchorAccessible` check below keeps a modal target whose anchor is
        // inaccessible (a succeeding top-layer anchor, or a non-modal target seeing a top-layer
        // anchor) baked, so the engine only ever places a modal target against an accessible
        // registered anchor.
        var position = merged.GetValueOrDefault("position");
        bool isModalDialog = DialogStateFor(element).Modal.TryGet(out var modal) && modal is true;
        if (position == "fixed" && !isModalDialog)
            return false;

        // Every anchor()/anchor-size() must be an anchor() in a physical inset; any
        // anchor-size(), or an anchor() outside left/right/top/bottom, stays baked.
        bool anyAnchorInset = false;
        foreach (var (key, value) in merged)
        {
            if (value.Contains("anchor-size(", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!value.Contains("anchor(", StringComparison.OrdinalIgnoreCase))
                continue;
            if (key is not ("left" or "right" or "top" or "bottom"))
                return false;
            anyAnchorInset = true;
        }
        if (!anyAnchorInset)
            return false;

        // Opposing insets on an axis size the box. The engine resolves that size (border box
        // fills the inset-modified CB minus margins) for a childless box with an auto length on
        // that axis; OR, for a `min-content` length, the engine sizes the box from its real
        // laid-out intrinsic width (so a childful box is fine — the size comes from layout, not
        // the inset-sizing path). An explicit length combined with opposing insets is
        // over-constrained and stays baked.
        static bool OpposingAxisSizable(bool childless, string? length) =>
            (childless && IsAutoLength(length))
            || IsEngineSizedIntrinsic(length);
        if (HasInset(merged, "left") && HasInset(merged, "right")
            && !OpposingAxisSizable(childless, merged.GetValueOrDefault("width")))
            return false;
        if (HasInset(merged, "top") && HasInset(merged, "bottom")
            && !OpposingAxisSizable(childless, merged.GetValueOrDefault("height")))
            return false;

        // Every referenced anchor must be registered, accessible, and moved by no scroll
        // relative to the target (the engine resolves with a zero scroll adjustment).
        string? implicitAnchor = merged.GetValueOrDefault("position-anchor");
        foreach (var key in new[] { "left", "right", "top", "bottom" })
        {
            if (!merged.TryGetValue(key, out var value) ||
                !value.Contains("anchor(", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!AnchorFunction.TryGetFirst(value, out var reference))
                return false;
            var name = string.IsNullOrEmpty(reference.Name)
                ? (implicitAnchor ?? string.Empty)
                : reference.Name!;
            if (string.IsNullOrEmpty(name) || name == "auto")
                return false;
            if (!anchorRegistry.TryGetValue(name, out var anchor) ||
                !IsAnchorAccessible(anchor.SourceElement, element))
                return false;
            // A non-fixed anchor separated from the target by a scroll container shifts by
            // that scroller's offset (the bridge subtracts it); the engine MVP does not.
            if (anchor.SourceElement != null)
            {
                bool anchorIsFixed =
                    GetComputedProps(anchor.SourceElement).GetValueOrDefault("position") == "fixed";
                if (!anchorIsFixed)
                {
                    ComputeInterveningScrollOffset(anchor.SourceElement, element, out var sx, out var sy);
                    if (sx != 0 || sy != 0)
                        return false;
                }
            }
        }

        return true;
    }

    /// <summary>Whether a physical inset in <paramref name="props"/> is present and not
    /// <c>auto</c>.</summary>
    private static bool HasInset(Dictionary<string, string> props, string name) =>
        props.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) && v.Trim() != "auto";

    /// <summary>Whether a <c>width</c>/<c>height</c> value is <c>auto</c> (or unset), so an
    /// opposing pair of insets determines the used size.</summary>
    private static bool IsAutoLength(string? value) =>
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
    private static bool IsEngineSizedIntrinsic(string? value)
    {
        var t = (value ?? string.Empty).Trim();
        return t.Equals("min-content", StringComparison.OrdinalIgnoreCase)
            || t.Equals("max-content", StringComparison.OrdinalIgnoreCase)
            || t.Equals("fit-content", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether an anchor()-inset box's <c>position-try</c> is the subset the engine's native
    /// fallback pass (<c>CssBox.TryApplyPositionTryFallback</c>) reproduces exactly, so the
    /// bridge can hand off the box (base + fallback) instead of pre-baking it. Requires: the
    /// parsed <c>@position-try</c> rules are available to the engine and every referenced
    /// fallback name resolves to one; and a base size the engine can reproduce for its overflow
    /// test on each axis (<see cref="AxisSizeHandoffSupported"/>) — a definite pixel length with
    /// a single inset (a reposition base), or a childless opposing-inset <c>auto</c> length the
    /// engine sizes from the two insets. A <c>min-content</c>/free-<c>auto</c> base still needs
    /// the bridge's min-content estimator the engine has no equivalent for and stays baked.
    /// Every other position-try box stays baked.
    /// </summary>
    private bool NativePositionTryHandoffSupported(
        Dictionary<string, string> merged,
        Dictionary<string, Dictionary<string, string>>? positionTryRules,
        bool childless)
    {
        if (positionTryRules == null || positionTryRules.Count == 0)
            return false;

        string? fallbacks = merged.GetValueOrDefault("position-try-fallbacks")
            ?? merged.GetValueOrDefault("position-try");
        if (string.IsNullOrWhiteSpace(fallbacks))
            return false;
        var names = PositionTryRule.ParseFallbackList(fallbacks);
        if (names.Length == 0)
            return false;
        foreach (var name in names)
            if (!positionTryRules.ContainsKey(name))
                return false;

        // The base size on each axis must be one the engine can reproduce for its overflow
        // test (it reads the box's laid-out size). See AxisSizeHandoffSupported.
        if (!AxisSizeHandoffSupported(merged, "width", "left", "right", childless))
            return false;
        if (!AxisSizeHandoffSupported(merged, "height", "top", "bottom", childless))
            return false;

        return true;
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
    private static bool AxisSizeHandoffSupported(
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

    /// <summary>
    /// Whether an element's <c>anchor-size()</c> sizing is the MVP subset the engine's native
    /// sizing pass reproduces (P5.8d.2b), so the bridge can hand it off instead of pre-baking
    /// (see <see cref="CssBox"/>'s <c>TryApplyNativeAnchorSizing</c>). Requires: an absolutely
    /// positioned, <b>childless</b> box (the engine only sizes childless boxes without a
    /// re-flow); <c>anchor-size()</c> only in <c>width</c>/<c>height</c>, each naming a
    /// registered accessible anchor; no <c>anchor()</c> inset (the combined case stays baked);
    /// no <c>position-area</c>; no right/bottom inset (the engine grows the box from its
    /// laid-out origin); no CSS <c>zoom</c>; not a modal dialog; and no <c>position-try</c>.
    /// </summary>
    private bool IsMvpNativeAnchorSizeBox(
        DomElement element, Dictionary<string, string> cssProps,
        Dictionary<string, AnchorInfo> anchorRegistry)
    {
        var merged = new Dictionary<string, string>(cssProps, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in BakedInlineStyle(element))
            merged[kv.Key] = kv.Value;

        // Absolutely positioned only (fixed gets a scroll adjustment the engine MVP omits).
        if (merged.GetValueOrDefault("position") != "absolute")
            return false;
        if (merged.ContainsKey("position-try-fallbacks") || merged.ContainsKey("position-try"))
            return false;
        // position-area boxes are placed+sized by the position-area path, not here.
        var area = merged.GetValueOrDefault("position-area");
        if (!string.IsNullOrWhiteSpace(area) && area != "none")
            return false;
        if (DialogStateFor(element).Modal.TryGet(out var modal) && modal is true)
            return false;
        // The engine sizes already-laid-out (already-zoomed) box geometry; a CSS zoom scale
        // would double-count, so keep zoomed boxes baked.
        if (Math.Abs(GetUsedZoomForElement(element) - 1.0) > 0.0001)
            return false;
        // Childless: the engine only resizes a box with no in-flow children/words.
        if (ChildElements(element).Any())
            return false;
        if (!string.IsNullOrWhiteSpace(GetElementTextContent(element)))
            return false;
        // The engine grows the box from its laid-out left/top origin, so a right/bottom inset
        // (which the baked path resolves via a re-layout) stays baked.
        if (HasInset(merged, "right") || HasInset(merged, "bottom"))
            return false;

        // anchor-size() only in width/height; any anchor() inset means the combined case,
        // which stays baked (the anchor()-inset gate already excludes anchor-size boxes).
        bool anySize = false;
        foreach (var (key, value) in merged)
        {
            if (value.Contains("anchor(", StringComparison.OrdinalIgnoreCase) &&
                !value.Contains("anchor-size(", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!value.Contains("anchor-size(", StringComparison.OrdinalIgnoreCase))
                continue;
            if (key is not ("width" or "height"))
                return false;
            anySize = true;
        }
        if (!anySize)
            return false;

        // Every referenced anchor must be registered and accessible.
        string? implicitAnchor = merged.GetValueOrDefault("position-anchor");
        foreach (var key in new[] { "width", "height" })
        {
            if (!merged.TryGetValue(key, out var value) ||
                !value.Contains("anchor-size(", StringComparison.OrdinalIgnoreCase))
                continue;
            bool ok = true;
            AnchorFunction.RewriteSize(value, r =>
            {
                var name = string.IsNullOrEmpty(r.Name)
                    ? (implicitAnchor ?? string.Empty)
                    : r.Name!;
                if (string.IsNullOrEmpty(name) || name == "auto" ||
                    !anchorRegistry.TryGetValue(name, out var anchor) ||
                    !IsAnchorAccessible(anchor.SourceElement, element))
                    ok = false;
                return "0px";
            });
            if (!ok)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether a box that uses <b>both</b> <c>anchor-size()</c> (in <c>width</c>/<c>height</c>)
    /// and <c>anchor()</c> (in a physical inset) is the MVP subset the engine reproduces exactly
    /// (P5.8d.2b combined expansion). The engine's post-pass sizes the box
    /// (<c>TryApplyNativeAnchorSizing</c>) <em>before</em> it places it
    /// (<c>TryApplyAnchorInsetPlacement</c>), so a right/bottom inset repositions against the
    /// resolved size — the two pure passes compose with no engine change. Neither pure gate
    /// admits the combined box (each excludes the other function), so this is the single seam
    /// that hands both halves off together. Requires the intersection of both pure MVP subsets:
    /// an absolutely-positioned, <b>childless</b>, non-modal, non-zoomed box with no
    /// <c>position-area</c>/<c>position-try</c>; <c>anchor-size()</c> only in <c>width</c>/
    /// <c>height</c> and <c>anchor()</c> only in <c>left</c>/<c>right</c>/<c>top</c>/<c>bottom</c>;
    /// at most one inset per axis (an opposing pair plus a definite <c>anchor-size()</c> is
    /// over-constrained and stays baked); and every referenced anchor registered, accessible and
    /// moved by no intervening scroll (the engine resolves with a zero scroll adjustment).
    /// </summary>
    private bool IsMvpNativeAnchorCombinedBox(
        DomElement element, Dictionary<string, string> cssProps,
        Dictionary<string, AnchorInfo> anchorRegistry)
    {
        var merged = new Dictionary<string, string>(cssProps, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in BakedInlineStyle(element))
            merged[kv.Key] = kv.Value;

        // Absolutely positioned only (fixed/modal targets get a document-scroll adjustment the
        // engine MVP does not apply — both pure gates exclude them for the same reason).
        if (merged.GetValueOrDefault("position") != "absolute")
            return false;
        if (DialogStateFor(element).Modal.TryGet(out var modal) && modal is true)
            return false;
        // A CSS zoom scale would double-count (the engine sizes already-zoomed geometry).
        if (Math.Abs(GetUsedZoomForElement(element) - 1.0) > 0.0001)
            return false;
        // Childless: the engine only sizes/repositions a box with no in-flow children/words.
        if (ChildElements(element).Any())
            return false;
        if (!string.IsNullOrWhiteSpace(GetElementTextContent(element)))
            return false;
        // position-try and position-area are handled by their own paths, not this one.
        if (merged.ContainsKey("position-try-fallbacks") || merged.ContainsKey("position-try"))
            return false;
        var area = merged.GetValueOrDefault("position-area");
        if (!string.IsNullOrWhiteSpace(area) && area != "none")
            return false;

        // Classify every reference: anchor-size() only in width/height, anchor() (that is not an
        // anchor-size) only in a physical inset. Any function elsewhere keeps the box baked.
        bool anyInset = false, anySize = false;
        foreach (var (key, value) in merged)
        {
            bool hasSize = value.Contains("anchor-size(", StringComparison.OrdinalIgnoreCase);
            bool hasInset = !hasSize && value.Contains("anchor(", StringComparison.OrdinalIgnoreCase);
            if (hasSize)
            {
                if (key is not ("width" or "height"))
                    return false;
                anySize = true;
            }
            if (hasInset)
            {
                if (key is not ("left" or "right" or "top" or "bottom"))
                    return false;
                anyInset = true;
            }
        }
        // Both halves must be present — otherwise the pure inset/size gates already handle it.
        if (!anyInset || !anySize)
            return false;

        // At most one inset per axis: opposing insets plus a definite anchor-size() width/height
        // is over-constrained (the engine's opposing-inset sizing only fires for an auto length),
        // so keep that case on the bridge path.
        if (HasInset(merged, "left") && HasInset(merged, "right"))
            return false;
        if (HasInset(merged, "top") && HasInset(merged, "bottom"))
            return false;

        string? implicitAnchor = merged.GetValueOrDefault("position-anchor");

        // Each inset's anchor must be registered, accessible and unmoved by intervening scroll.
        foreach (var key in new[] { "left", "right", "top", "bottom" })
        {
            if (!merged.TryGetValue(key, out var value) ||
                value.Contains("anchor-size(", StringComparison.OrdinalIgnoreCase) ||
                !value.Contains("anchor(", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!AnchorFunction.TryGetFirst(value, out var reference) ||
                !IsCombinedAnchorRefNative(element, reference.Name, implicitAnchor, anchorRegistry))
                return false;
        }
        // Each anchor-size()'s anchor must be registered and accessible (dimensions are
        // scroll-independent, but the shared no-scroll check below is harmless and consistent).
        foreach (var key in new[] { "width", "height" })
        {
            if (!merged.TryGetValue(key, out var value) ||
                !value.Contains("anchor-size(", StringComparison.OrdinalIgnoreCase))
                continue;
            bool ok = true;
            AnchorFunction.RewriteSize(value, r =>
            {
                if (!IsCombinedAnchorRefNative(element, r.Name, implicitAnchor, anchorRegistry))
                    ok = false;
                return "0px";
            });
            if (!ok)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether an <c>anchor()</c>/<c>anchor-size()</c> reference in a combined box names an
    /// anchor the engine can resolve to the same value the bridge would bake: registered,
    /// accessible to <paramref name="element"/>, and (unless fixed) separated from it by no
    /// intervening scroll offset (the engine's placement uses a zero scroll adjustment).
    /// </summary>
    private bool IsCombinedAnchorRefNative(
        DomElement element, string? refName, string? implicitAnchor,
        Dictionary<string, AnchorInfo> anchorRegistry)
    {
        var name = string.IsNullOrEmpty(refName) ? (implicitAnchor ?? string.Empty) : refName!;
        if (string.IsNullOrEmpty(name) || name == "auto")
            return false;
        if (!anchorRegistry.TryGetValue(name, out var anchor) ||
            !IsAnchorAccessible(anchor.SourceElement, element))
            return false;
        if (anchor.SourceElement != null &&
            GetComputedProps(anchor.SourceElement).GetValueOrDefault("position") != "fixed")
        {
            ComputeInterveningScrollOffset(anchor.SourceElement, element, out var sx, out var sy);
            if (sx != 0 || sy != 0)
                return false;
        }
        return true;
    }
}
