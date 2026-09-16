using System.Globalization;
using System.Linq;
using Broiler.CSS;
using Broiler.Dom;
using Broiler.Layout;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // -----------------------------------------------------------------
    // anchor() resolution
    // -----------------------------------------------------------------

    // The anchor()/anchor-size() grammar (token matching + typed extraction) is the
    // canonical Broiler.CSS.AnchorFunction model (Phase 5 item 4). These callbacks
    // keep only the used-value geometry; AnchorFunction.Rewrite/RewriteSize supply
    // the parsed AnchorFunctionRef/AnchorSizeFunctionRef.
    private void ResolveAnchorFunctions(DomElement element, Dictionary<string, AnchorInfo> anchorRegistry,
        Dictionary<string, IReadOnlyDictionary<string, string>>? positionTryRules = null)
    {
        var cssProps = CollectMatchedRuleProperties(element);

        bool hasAnchorRef = false;
        bool hasAnchorSizeRef = false;
        foreach (var kv in cssProps)
        {
            if (kv.Value.Contains("anchor(", StringComparison.OrdinalIgnoreCase))
                hasAnchorRef = true;
            if (kv.Value.Contains("anchor-size(", StringComparison.OrdinalIgnoreCase))
                hasAnchorSizeRef = true;
        }
        // Also check inline styles for anchor-size()
        foreach (var kv in BakedInlineStyle(element))
        {
            if (kv.Value.Contains("anchor-size(", StringComparison.OrdinalIgnoreCase))
                hasAnchorSizeRef = true;
        }

        // A box that uses both anchor-size() and anchor() insets is handed off as a unit — the
        // engine sizes then places it in one post-pass. Neither pure gate admits it (each excludes
        // the other function), so this single flag drives both the inset-skip below and the
        // size-skip further down, keeping the two halves' bake/handoff decision in lockstep. The
        // NativeAnchorPlacement flag check is dropped in Phase 4 item-2 step 5 (a provable no-op on
        // the native default path, where it was already true).
        bool combinedMvp = hasAnchorRef && hasAnchorSizeRef &&
            IsMvpNativeAnchorCombinedBox(element, cssProps, anchorRegistry);

        // For the MVP subset, skip baking the anchor() insets entirely so the box's
        // `left/right/top/bottom: anchor(...)` CSS survives to the render and the Broiler.Layout
        // engine's placement post-pass resolves it natively (see CssBox.TryApplyAnchorInsetPlacement).
        // Every other anchor() box is baked below.
        if (hasAnchorRef &&
            (combinedMvp ||
             IsMvpNativeAnchorInsetBox(element, cssProps, anchorRegistry, positionTryRules)))
            hasAnchorRef = false;

        if (hasAnchorRef)
        {
            // Need CB dimensions for resolving anchor positions in right/bottom contexts.
            double cbW = FindContainingBlockWidth(element);
            double cbH = FindContainingBlockHeight(element);

            // Get the implicit anchor name from position-anchor.
            string? implicitAnchor = cssProps.GetValueOrDefault("position-anchor") ??
                                     BakedInlineStyle(element).GetValueOrDefault("position-anchor");

            // When the target element is fixed-positioned (e.g. top-layer dialog)
            // and the anchor is NOT fixed-positioned, anchor positions must be
            // adjusted by the document scroll offset so the anchor's viewport
            // position is used instead of its document position.
            bool targetIsFixed =
                (cssProps.GetValueOrDefault("position") ?? BakedInlineStyle(element).GetValueOrDefault("position")) == "fixed" ||
                (DialogStateFor(element).Modal.TryGet(out var tModal) && tModal is true);
            double scrollAdjY = 0, scrollAdjX = 0;
            if (targetIsFixed)
            {
                var docEl = DocumentElement;
                if (ScrollStateFor(docEl).Top.TryGet(out var stv) && stv is double scrollTop)
                    scrollAdjY = scrollTop;
                if (ScrollStateFor(docEl).Left.TryGet(out var slv) && slv is double scrollLeft)
                    scrollAdjX = scrollLeft;
            }

            foreach (var kv in cssProps)
            {
                var propName = kv.Key.ToLowerInvariant();
                var resolved = AnchorFunction.Rewrite(kv.Value, r =>
                {
                    var anchorName = string.IsNullOrEmpty(r.Name)
                        ? (implicitAnchor ?? string.Empty)
                        : r.Name!;
                    var fallback = r.Fallback;

                    if (!anchorRegistry.TryGetValue(anchorName, out var anchor) ||
                        !IsAnchorAccessible(anchor.SourceElement, element))
                    {
                        // Anchor not found or not accessible — use fallback or 0px.
                        return fallback ?? "0px";
                    }

                    // Compute the raw edge position (from CB origin).
                    // When the target is fixed and the anchor is not fixed,
                    // adjust for document scroll to get viewport position.
                    // Use only the CSS computed position to determine if the
                    // anchor is fixed — modal dialogs with position:absolute
                    // are still shifted by scroll simulation and need adjustment.
                    bool anchorIsFixed = anchor.SourceElement != null &&
                        GetComputedProps(anchor.SourceElement).GetValueOrDefault("position") == "fixed";

                    // Scroll-driven positioning (CSS Anchor Positioning § scroll):
                    // an anchored element must track its anchor's *scrolled* position.
                    // When a scroll container is an ancestor of the anchor but NOT of
                    // the target (the target lives outside that scroller, e.g. a fixed
                    // or sibling-of-scroller abspos box), the anchor's edges shift by
                    // that scroller's scroll offset while the target does not — so the
                    // resolved inset must subtract it. Scrollers that contain the target
                    // too move both together and are skipped. (The target-inside-scroller
                    // case is handled separately by ApplyScrollSimulation, which shifts
                    // the target's own subtree.)
                    double nestedX = 0, nestedY = 0;
                    if (!anchorIsFixed && anchor.SourceElement != null)
                        ComputeInterveningScrollOffset(
                            anchor.SourceElement, element, out nestedX, out nestedY);

                    double adjY = anchorIsFixed ? 0 : scrollAdjY + nestedY;
                    double adjX = anchorIsFixed ? 0 : scrollAdjX + nestedX;

                    // Edge coordinate math (anchor edge − scroll adjustment, plus the
                    // right/bottom opposite-edge flip) is the canonical
                    // Broiler.Layout.AnchorGeometry model (Phase 5 item 3).
                    double value = AnchorGeometry.ResolveEdge(
                        anchor.Left, anchor.Top, anchor.Right, anchor.Bottom,
                        r.Side, adjX, adjY, MapAnchorInsetProperty(propName), cbW, cbH);

                    return $"{value.ToString(CultureInfo.InvariantCulture)}px";
                });

                if (resolved != kv.Value)
                    BakedInlineStyle(element)[kv.Key] = resolved;
            }

            // Apply non-anchor CSS properties (e.g. position, margin).
            foreach (var kv in cssProps)
            {
                if (!kv.Value.Contains("anchor(", StringComparison.OrdinalIgnoreCase) &&
                    !kv.Value.Contains("anchor-size(", StringComparison.OrdinalIgnoreCase) &&
                    !BakedInlineStyle(element).ContainsKey(kv.Key) &&
                    IsLayoutProperty(kv.Key))
                {
                    BakedInlineStyle(element)[kv.Key] = kv.Value;
                }
            }

            // Remove 'inset' shorthand.
            BakedInlineStyle(element).Remove("inset");
        }

        // Resolve anchor-size() function calls in both CSS and inline styles.
        // Skip baking for the MVP subset so the box's `width/height: anchor-size(...)` survives to
        // the engine's sizing pass (CssBox.TryApplyNativeAnchorSizing). The combined-box flag skips
        // the size bake too, so a box that also has anchor() insets keeps both halves un-baked for
        // the engine. The NativeAnchorPlacement flag check is dropped in Phase 4 item-2 step 5 (a
        // provable no-op on the native default path, where it was already true).
        if (hasAnchorSizeRef &&
            !(combinedMvp || IsMvpNativeAnchorSizeBox(element, cssProps, anchorRegistry)))
        {
            ResolveAnchorSizeFunctions(element, cssProps, anchorRegistry);
        }

        // Snapshot the child list: resolving anchor functions on a descendant can
        // mutate the live DOM (e.g. anchor-driven style/structure changes under
        // content-visibility), and iterating the live collection while it changes
        // throws "Collection was modified" (WPT content-visibility-anchor-positioning)
        // or overflows the ToList() copy. SnapshotChildren tolerates both.
        foreach (var child in SnapshotChildren(element))
            ResolveAnchorFunctions(child, anchorRegistry, positionTryRules);
    }

    /// <summary>
    /// Accumulates the scroll offset of scroll containers that lie between the
    /// anchor and the target — i.e. that are ancestors of <paramref name="anchorEl"/>
    /// but do not also contain <paramref name="targetEl"/>. Such a scroller moves the
    /// anchor (and its edges) but not the target, so an anchored element positioned
    /// against it must subtract this offset to stay pinned to the anchor's scrolled
    /// position. The walk stops at the first scroller that also contains the target
    /// (that scroller scrolls both, or is the target's containing block). The offset
    /// is scaled to match <c>ApplyScrollSimulation</c> under an active visual viewport.
    /// </summary>
    private void ComputeInterveningScrollOffset(DomElement anchorEl, DomElement targetEl, out double offX, out double offY)
    {
        offX = 0;
        offY = 0;
        var scale = GetScrollSimulationScaleFactor();

        // A position:sticky element stays pinned to its nearest scroll
        // container's edge instead of translating with that scroller's scroll.
        // When the anchor (or a box between it and its scroller) is sticky, the
        // anchor's scrolled position does NOT shift by the full scroll offset,
        // so a target outside the scroller must not subtract it — doing so drives
        // the anchored box off-screen (css-anchor-position anchor-scroll-to-sticky-004).
        bool stickyToNextScroller = IsSticky(GetComputedProps(anchorEl));

        for (var el = ParentEl(anchorEl); el != null; el = ParentEl(el))
        {
            var props = GetComputedProps(el);
            if (!CssOverflow.ClipsOverflow(props))
            {
                // A sticky box below the next scroller pins the anchor to it;
                // remember that until we reach the scroller itself.
                if (IsSticky(props))
                    stickyToNextScroller = true;
                continue;
            }

            // The target lives inside this scroller too → they scroll together
            // (or this scroller is the target's containing block); no separation.
            if (IsDescendantOrSelf(targetEl, el))
                break;

            // Skip scrollers the anchor is sticky-pinned to: the anchor resists
            // this scroller's scroll, so its edges don't move by that offset.
            if (!stickyToNextScroller)
            {
                if (ScrollStateFor(el).Left.TryGet(out var sl) && sl is double slv)
                    offX += slv * scale;
                if (ScrollStateFor(el).Top.TryGet(out var st) && st is double stv)
                    offY += stv * scale;
            }

            // Past this scroller, sticky pinning resets: a sticky box higher up
            // pins to the next scroll container, not this one.
            stickyToNextScroller = false;
        }
    }
    /// <summary>
    /// Resolves <c>anchor-size()</c> function calls in CSS properties and inline
    /// styles, replacing them with computed pixel values from the anchor element's
    /// dimensions.
    /// </summary>
    private void ResolveAnchorSizeFunctions(
        DomElement element,
        Dictionary<string, string> cssProps,
        Dictionary<string, AnchorInfo> anchorRegistry)
    {
        // Get implicit anchor name from position-anchor.
        string? implicitAnchor = cssProps.GetValueOrDefault("position-anchor") ??
                                 BakedInlineStyle(element).GetValueOrDefault("position-anchor");

        // The anchor's registered geometry is in used (already-zoomed) pixels when it comes from the
        // real layout box (TryGetAnchorLayoutBox / shared geometry). The resolved anchor-size() length
        // is written as an author length on this positioned element, which is then re-scaled by the
        // element's own used zoom (the DOM `zoom` bake). Divide the used size by that zoom so the value
        // lands in the element's pre-zoom (CSS-px) frame and is not zoom-counted twice — matching the
        // reference where a zoom:2 anchor's 200px width covers a zoom:2 positioned box. A no-op for the
        // common unzoomed case (used zoom == 1). See css-anchor-position/anchor-size-css-zoom.
        double usedZoom = GetUsedZoomForElement(element);
        if (usedZoom <= 0) usedZoom = 1;

        string ResolveValue(string value)
        {
            return AnchorFunction.RewriteSize(value, r =>
            {
                var anchorName = string.IsNullOrEmpty(r.Name)
                    ? (implicitAnchor ?? string.Empty)
                    : r.Name!;

                if (!anchorRegistry.TryGetValue(anchorName, out var anchor))
                    return "0px";

                double result = AnchorGeometry.ResolveSize(r.Dimension, anchor.Width, anchor.Height) / usedZoom;

                return $"{result.ToString(CultureInfo.InvariantCulture)}px";
            });
        }

        // Resolve in CSS properties and apply as inline styles.
        foreach (var kv in cssProps)
        {
            if (kv.Value.Contains("anchor-size(", StringComparison.OrdinalIgnoreCase))
            {
                BakedInlineStyle(element)[kv.Key] = ResolveValue(kv.Value);
            }
        }

        // Resolve in existing inline styles.
        var inlineKeys = new List<string>(BakedInlineStyle(element).Keys);
        foreach (var key in inlineKeys)
        {
            if (BakedInlineStyle(element)[key].Contains("anchor-size(", StringComparison.OrdinalIgnoreCase))
            {
                BakedInlineStyle(element)[key] = ResolveValue(BakedInlineStyle(element)[key]);
            }
        }
    }

}

// Native anchor()/anchor-size() handoff gating (Phase 5, P5.8d.2b). These predicates
// decide whether an anchored box is the MVP subset the layout engine reproduces
// natively (so the bridge skips pre-baking).
public sealed partial class DomBridge
{
    /// <summary>
    /// Whether an element's <c>anchor()</c> insets are the MVP subset the engine's native
    /// placement post-pass reproduces (P5.8d.2b), so the bridge can hand them off instead of
    /// pre-baking (see <see cref="Broiler.Layout.Engine.CssBox.TryApplyAnchorInsetPlacement"/>).
    /// Requires: every <c>anchor()</c> reference is in a physical inset (<c>left</c>/<c>right</c>/
    /// <c>top</c>/<c>bottom</c>) and names a registered, accessible anchor; no <c>anchor-size()</c>;
    /// opposing insets on an axis only over a childless <c>auto</c> or an intrinsic-keyword length;
    /// not a non-modal <c>fixed</c> box; no intervening scroll offset (the engine uses none); and
    /// <c>position-try</c> only as <see cref="NativePositionTryHandoffSupported"/> admits.
    /// </summary>
    private bool IsMvpNativeAnchorInsetBox(
        DomElement element, Dictionary<string, string> cssProps,
        Dictionary<string, AnchorInfo> anchorRegistry,
        Dictionary<string, IReadOnlyDictionary<string, string>>? positionTryRules = null)
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

    /// <summary>
    /// Whether an anchor()-inset box's <c>position-try</c> is the subset the engine's native
    /// fallback pass (<c>CssBox.TryApplyPositionTryFallback</c>) reproduces exactly, so the
    /// bridge can hand off the box (base + fallback) instead of pre-baking it. Requires: the
    /// parsed <c>@position-try</c> rules are available to the engine and every referenced
    /// fallback name resolves to one; and a base size the engine can reproduce for its overflow
    /// test on each axis (<see cref="DomBridgeUtils.AxisSizeHandoffSupported"/>) — a definite pixel length with
    /// a single inset (a reposition base), or a childless opposing-inset <c>auto</c> length the
    /// engine sizes from the two insets. A <c>min-content</c>/free-<c>auto</c> base still needs
    /// the bridge's min-content estimator the engine has no equivalent for and stays baked.
    /// Every other position-try box stays baked.
    /// </summary>
    private bool NativePositionTryHandoffSupported(
        Dictionary<string, string> merged,
        Dictionary<string, IReadOnlyDictionary<string, string>>? positionTryRules,
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
    /// Whether an element's <c>anchor-size()</c> sizing is the MVP subset the engine's native
    /// sizing pass reproduces (P5.8d.2b), so the bridge can hand it off instead of pre-baking
    /// (see <see cref="Broiler.Layout.Engine.CssBox.TryApplyNativeAnchorSizing"/>). Requires: an absolutely
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
