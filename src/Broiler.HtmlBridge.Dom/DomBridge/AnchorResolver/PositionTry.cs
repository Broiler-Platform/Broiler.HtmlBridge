using System.Globalization;
using Broiler.CSS;
using Broiler.Dom;
using Broiler.Layout;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // -----------------------------------------------------------------
    // @position-try parsing and fallback resolution
    // -----------------------------------------------------------------

    /// <summary>
    /// Parses all <c>@position-try</c> at-rules from <c>&lt;style&gt;</c>
    /// elements, returning a dictionary mapping rule name to its property
    /// declarations.
    /// </summary>
    private Dictionary<string, Dictionary<string, string>> ParsePositionTryRules()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        CollectPositionTryRulesFromTree(DocumentElement, result);
        return result;
    }

    private void CollectPositionTryRulesFromTree(DomElement el, Dictionary<string, Dictionary<string, string>> result)
    {
        if (string.Equals(el.TagName, "style", StringComparison.OrdinalIgnoreCase))
        {
            // Read the <style> source through the canonical GetStyleElementSourceText accessor
            // (the same source the cascade reads) rather than hand-walking child text nodes, so
            // @position-try rules are collected consistently in the resolve-only and full-render
            // paths. Use the raw source (not the serialized rule model): the CSS rule serializer
            // does not round-trip the @position-try at-rule.
            var raw = GetStyleElementSourceText(el);
            if (!string.IsNullOrEmpty(raw))
            {
                // The @position-try at-rule grammar (comment stripping + rule and
                // declaration parsing) is the canonical Broiler.CSS.PositionTryRule
                // model (Phase 5 item 4). Merge each <style>'s rules into the
                // document-wide accumulator (later duplicates win, in document order).
                foreach (var rule in PositionTryRule.Parse(raw))
                    result[rule.Key] = rule.Value;
            }
        }

        foreach (var child in SnapshotChildren(el))
            CollectPositionTryRulesFromTree(child, result);
    }
    /// <summary>
    /// For elements with <c>position-try-fallbacks</c>, checks whether
    /// the base style overflows the containing block and applies the first
    /// non-overflowing fallback from the <c>@position-try</c> rules.
    /// </summary>
    private void ResolvePositionTryFallbacks(DomElement root, Dictionary<string, AnchorInfo> anchorRegistry, Dictionary<string, Dictionary<string, string>> positionTryRules) =>
        ResolvePositionTryFallbacksTree(root, anchorRegistry, positionTryRules);

    private void ResolvePositionTryFallbacksTree(DomElement element, Dictionary<string, AnchorInfo> anchorRegistry, Dictionary<string, Dictionary<string, string>> positionTryRules)
    {
        if (!IsText(element) && !IsComment(element))
        {
            // Collect all CSS + inline properties to find position-try-fallbacks.
            var cssProps = CollectMatchedRuleProperties(element);
            foreach (var kv in BakedInlineStyle(element))
                cssProps[kv.Key] = kv.Value;

            string? fallbacks = cssProps.GetValueOrDefault("position-try-fallbacks") ??
                                cssProps.GetValueOrDefault("position-try");

            if (!string.IsNullOrWhiteSpace(fallbacks) && positionTryRules.Count > 0)
            {
                // A box in the anchor()-inset position-try handoff subset had its base left
                // un-baked in ResolveAnchorFunctions (same IsMvpNativeAnchorInsetBox predicate);
                // the engine's post-pass applies the fallback from the PositionTryRules channel,
                // so skip baking and leave the position-try + anchor() CSS intact. Every other
                // position-try box is baked here and has its position-try neutralized inline so
                // the engine's fallback pass skips the already-baked box. The NativeAnchorPlacement
                // flag check is dropped in Phase 4 item-2 step 5 (a provable no-op on the native
                // default path, where it was already true); the neutralizer is stamped
                // unconditionally (harmless on the retired baked path).
                if (IsMvpNativeAnchorInsetBox(element, cssProps, anchorRegistry, positionTryRules))
                {
                    // handed off to the engine — the bridge does not touch this box
                }
                else
                {
                    TryApplyFallback(element, cssProps, anchorRegistry, positionTryRules, fallbacks!);
                    BakedInlineStyle(element)["position-try-fallbacks"] = "none";
                    BakedInlineStyle(element)["position-try"] = "normal";
                }
            }
        }

        // Snapshot: TryApplyFallback resolves anchor geometry, which can lazily
        // reflect style into the DOM and mutate a Children collection an enclosing
        // recursion frame is still walking ("Collection was modified", issue #1143).
        foreach (var child in SnapshotChildren(element))
            ResolvePositionTryFallbacksTree(child, anchorRegistry, positionTryRules);
    }

    private void TryApplyFallback(DomElement element, Dictionary<string, string> baseProps, Dictionary<string, AnchorInfo> anchorRegistry,
        Dictionary<string, Dictionary<string, string>> positionTryRules, string fallbackList)
    {
        // Get the containing block dimensions.
        double cbWidth = FindContainingBlockWidth(element);
        double cbHeight = FindContainingBlockHeight(element);

        // Check if the base style overflows the IMCB. The base insets are already baked
        // to inline px here; the live read path (ResolvePositionTryForElement) resolves them
        // fresh and calls the same ComputeFallbackPlacement core.
        double baseLeft = TryParsePx(BakedInlineStyle(element).GetValueOrDefault("left")) ?? 0;
        double baseTop = TryParsePx(BakedInlineStyle(element).GetValueOrDefault("top")) ?? 0;
        double baseRight = TryParsePx(BakedInlineStyle(element).GetValueOrDefault("right")) ??
                           TryParsePx(baseProps.GetValueOrDefault("right")) ?? 0;
        double baseBottom = TryParsePx(BakedInlineStyle(element).GetValueOrDefault("bottom")) ??
                            TryParsePx(baseProps.GetValueOrDefault("bottom")) ?? 0;
        double baseWidth = TryParsePx(BakedInlineStyle(element).GetValueOrDefault("width")) ??
                           TryParsePx(baseProps.GetValueOrDefault("width")) ?? 0;
        double baseHeight = TryParsePx(BakedInlineStyle(element).GetValueOrDefault("height")) ??
                            TryParsePx(baseProps.GetValueOrDefault("height")) ?? 0;

        // Estimate content width for min-content/max-content.
        string? widthVal = baseProps.GetValueOrDefault("width");
        bool hasAutoWidth = widthVal == "min-content" || widthVal == "max-content" ||
                            widthVal == "auto" || widthVal == "fit-content";
        if (hasAutoWidth && baseWidth == 0)
        {
            // Estimate from child element widths.
            baseWidth = EstimateMinContentWidth(element);
        }

        if (ComputeFallbackPlacement(baseProps, anchorRegistry, positionTryRules, fallbackList,
                baseLeft, baseTop, baseRight, baseBottom, baseWidth, baseHeight, cbWidth, cbHeight) is { } placed)
        {
            BakedInlineStyle(element)["left"] = $"{placed.left.ToString(CultureInfo.InvariantCulture)}px";
            BakedInlineStyle(element)["top"] = $"{placed.top.ToString(CultureInfo.InvariantCulture)}px";
            BakedInlineStyle(element)["width"] = $"{placed.width.ToString(CultureInfo.InvariantCulture)}px";
            BakedInlineStyle(element)["height"] = $"{placed.height.ToString(CultureInfo.InvariantCulture)}px";
            BakedInlineStyle(element).Remove("right");
            BakedInlineStyle(element).Remove("bottom");
            BakedInlineStyle(element).Remove("inset");
        }
    }

    /// <summary>
    /// The pure <c>@position-try</c> fallback algorithm shared by the render bake
    /// (<see cref="TryApplyFallback"/>) and the live read path
    /// (the position-try live read path): given the base placement geometry, tests
    /// whether it overflows and, if so, returns the first fallback whose resolved placement fits
    /// (or <c>null</c> when the base fits or none fits). Reposition + resize; <c>anchor()</c> insets
    /// in a fallback resolve through <see cref="AnchorGeometry"/>, matching the base.
    /// </summary>
    private (double left, double top, double width, double height)? ComputeFallbackPlacement(
        Dictionary<string, string> baseProps, Dictionary<string, AnchorInfo> anchorRegistry,
        Dictionary<string, Dictionary<string, string>> positionTryRules, string fallbackList,
        double baseLeft, double baseTop, double baseRight, double baseBottom,
        double baseWidth, double baseHeight, double cbWidth, double cbHeight)
    {
        double imcbWidth = cbWidth - baseLeft - baseRight;
        double imcbHeight = cbHeight - baseTop - baseBottom;

        bool baseOverflows = AnchorGeometry.Overflows(
            baseLeft, baseTop, baseWidth, baseHeight, cbWidth, cbHeight, imcbWidth, imcbHeight);

        if (!baseOverflows)
            return null; // Base style fits; no fallback needed.

        // Parse the fallback list (comma-separated @position-try names).
        var names = PositionTryRule.ParseFallbackList(fallbackList);

        // Get implicit anchor name from position-anchor.
        string? implicitAnchor = baseProps.GetValueOrDefault("position-anchor");

        foreach (var name in names)
        {
            if (!positionTryRules.TryGetValue(name, out var tryProps))
                continue;

            // Compute the element position/size with this fallback applied.
            // Start with the base properties, then overlay the try properties.
            var merged = new Dictionary<string, string>(baseProps, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in tryProps)
                merged[kv.Key] = kv.Value;

            // Handle "inset: auto" — reset all inset properties so the
            // base style's insets don't leak through to the fallback.
            if (merged.TryGetValue("inset", out var insetVal) &&
                insetVal.Trim() == "auto")
            {
                if (!tryProps.ContainsKey("left"))
                    merged["left"] = "auto";
                if (!tryProps.ContainsKey("right"))
                    merged["right"] = "auto";
                if (!tryProps.ContainsKey("top"))
                    merged["top"] = "auto";
                if (!tryProps.ContainsKey("bottom"))
                    merged["bottom"] = "auto";
            }

            // Resolve explicit width/height first so that subsequent
            // left-from-right and top-from-bottom calculations use
            // the correct element dimensions.
            double tryWidth = baseWidth, tryHeight = baseHeight;

            if (merged.TryGetValue("width", out var wv))
            {
                var w = TryParsePx(wv);
                if (w.HasValue) tryWidth = w.Value;
            }
            if (merged.TryGetValue("height", out var hv))
            {
                var h = TryParsePx(hv);
                if (h.HasValue) tryHeight = h.Value;
            }

            // Resolve any anchor() references in the try properties.
            double tryLeft = 0, tryTop = 0;

            if (merged.TryGetValue("left", out var lv) && lv != "auto")
            {
                var resolvedL = AnchorFunction.Rewrite(lv, r =>
                    ResolveAnchorEdge(r, anchorRegistry, "left", cbWidth, cbHeight, implicitAnchor));
                tryLeft = TryParsePx(resolvedL) ?? 0;
            }

            if (merged.TryGetValue("right", out var rv) && rv != "auto")
            {
                var resolvedR = AnchorFunction.Rewrite(rv, r =>
                    ResolveAnchorEdge(r, anchorRegistry, "right", cbWidth, cbHeight, implicitAnchor));
                var rightPx = TryParsePx(resolvedR);
                if (rightPx.HasValue)
                {
                    if (!merged.TryGetValue("left", out var leftV) ||
                        leftV == "auto" || string.IsNullOrEmpty(leftV))
                    {
                        // No left specified; compute left from right + width.
                        tryLeft = cbWidth - rightPx.Value - tryWidth;
                    }
                    else
                    {
                        // Both left and right specified; compute width.
                        tryWidth = cbWidth - tryLeft - rightPx.Value;
                    }
                }
            }

            if (merged.TryGetValue("top", out var tv) && tv != "auto")
            {
                var resolvedT = AnchorFunction.Rewrite(tv, r =>
                    ResolveAnchorEdge(r, anchorRegistry, "top", cbWidth, cbHeight, implicitAnchor));
                tryTop = TryParsePx(resolvedT) ?? 0;
            }

            if (merged.TryGetValue("bottom", out var bv) && bv != "auto")
            {
                var resolvedB = AnchorFunction.Rewrite(bv, r =>
                    ResolveAnchorEdge(r, anchorRegistry, "bottom", cbWidth, cbHeight, implicitAnchor));
                var bottomPx = TryParsePx(resolvedB);
                if (bottomPx.HasValue)
                {
                    if (!merged.TryGetValue("top", out var topV) ||
                        topV == "auto" || string.IsNullOrEmpty(topV))
                    {
                        tryTop = cbHeight - bottomPx.Value - tryHeight;
                    }
                    else
                    {
                        tryHeight = cbHeight - tryTop - bottomPx.Value;
                    }
                }
            }

            bool fits = AnchorGeometry.Fits(tryLeft, tryTop, tryWidth, tryHeight, cbWidth, cbHeight);

            if (fits)
                return (tryLeft, tryTop, tryWidth, tryHeight);
        }

        return null;
    }

    /// <summary>
    /// Estimates the min-content width of an element by examining its
    /// children's explicit widths. This is a heuristic for elements
    /// with <c>width: min-content</c>.
    /// </summary>
    private double EstimateMinContentWidth(DomElement element)
    {
        double maxWidth = 0;
        foreach (var child in SnapshotChildren(element))
        {
            if (IsText(child)) continue;
            var childProps = CollectMatchedRuleProperties(child);
            foreach (var kv in BakedInlineStyle(child))
                childProps[kv.Key] = kv.Value;

            double childWidth = TryParsePx(childProps.GetValueOrDefault("width")) ?? 0;
            if (childWidth > maxWidth)
                maxWidth = childWidth;
        }
        return maxWidth;
    }
    private static string ResolveAnchorEdge(AnchorFunctionRef reference, Dictionary<string, AnchorInfo> registry,
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
