using System.Globalization;
using Broiler.CSS;
using Broiler.Dom;
using Broiler.Layout;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // -----------------------------------------------------------------
    // Anchor registry
    // -----------------------------------------------------------------

    // The engine's public AnchorRegistry (Broiler.Layout), tracking registered anchors
    // with their border-box geometry and source element scope token. Rebuilt by BuildAnchorRegistry.
    private Broiler.Layout.AnchorRegistry? _layoutAnchors;

    private void BuildAnchorRegistry(Dictionary<string, AnchorInfo> registry)
    {
        var layoutAnchors = new Broiler.Layout.AnchorRegistry();
        foreach (var el in Elements)
        {
            if (IsText(el))
                continue;

            var declarations = CollectMatchedRuleProperties(el);
            if (!declarations.TryGetValue("anchor-name", out var anchorName))
                continue;

            var box = ComputeElementBox(el);
            if (box != null)
            {
                var info = box with { SourceElement = el };
                registry[anchorName] = info;
                layoutAnchors.Register(anchorName, new AnchorRect(info.Left, info.Top, info.Width, info.Height), info);
            }
        }
        _layoutAnchors = layoutAnchors;
    }

    /// <summary>
    /// Resolves the anchor a positioned element binds to for a given <c>anchor-name</c>
    /// using Broiler.Layout's public <see cref="AnchorRegistry"/>.
    /// </summary>
    private AnchorInfo? ResolveAnchorForElement(string name, DomElement queryEl, Dictionary<string, AnchorInfo> registry)
    {
        var cb = FindContainingBlockElement(queryEl);
        return _layoutAnchors?.ResolveScope<AnchorInfo>(name, scope =>
            scope is AnchorInfo { SourceElement: { } el } && cb != null && el.IsDescendantOf(cb))
            ?? (registry.TryGetValue(name, out var global) ? global : null);
    }

    private AnchorInfo? ComputeElementBox(DomElement element)
    {
        var props = GetComputedProps(element);

        string? position = props.GetValueOrDefault("position");
        bool isPositioned = position == "absolute" || position == "fixed";

        // Prefer the renderer's real layout for the anchor rect when the shared
        // geometry path is active. The CSS-property estimator below
        // cannot model inline flow — an inline anchor after an inline-block, or with
        // real font metrics, is mis-sized/mis-placed (see AnchorScroll* / the
        // css-anchor-position anchor-scroll cluster). Real layout gets the inline
        // border box exactly; convert its document coords to the anchor's
        // containing-block-relative frame (the estimator's convention) so downstream
        // anchor() resolution is unchanged. Falls through to the estimator whenever
        // the geometry is unavailable (flag off, detached, no box) — so this is a
        // no-op unless UseSharedLayoutGeometry is enabled.
        //
        // An abspos anchor whose containing block is an inline box needs no bypass: the
        // layout engine places it at its inset position, so its shared box is correct —
        // verified by the css-anchor-position position-area-inline-container cluster.
        if (TryGetAnchorLayoutBox(element, out var layoutBox))
            return layoutBox;

        double width = TryParsePx(props.GetValueOrDefault("width")) ?? 0;
        double height = TryParsePx(props.GetValueOrDefault("height")) ?? 0;

        // For absolutely positioned elements, use their explicit insets.
        if (isPositioned)
        {
            double? topPx = TryParsePx(props.GetValueOrDefault("top"));
            double? leftPx = TryParsePx(props.GetValueOrDefault("left"));
            double? rightPx = TryParsePx(props.GetValueOrDefault("right"));
            double? bottomPx = TryParsePx(props.GetValueOrDefault("bottom"));

            double top = topPx ?? 0;
            double left = leftPx ?? 0;

            // When both left and right are specified without explicit width,
            // derive width from the containing block dimensions.
            if (width == 0 && leftPx.HasValue && rightPx.HasValue)
            {
                double cbW = FindContainingBlockWidth(element);
                width = cbW - leftPx.Value - rightPx.Value;
                if (width < 0) width = 0;
            }

            // When both top and bottom are specified without explicit height,
            // derive height from the containing block dimensions.
            if (height == 0 && topPx.HasValue && bottomPx.HasValue)
            {
                double cbH = FindContainingBlockHeight(element);
                height = cbH - topPx.Value - bottomPx.Value;
                if (height < 0) height = 0;
            }

            // When only right/bottom are specified, compute left/top from
            // the containing block dimensions.
            if (leftPx == null && rightPx.HasValue)
            {
                double cbW = FindContainingBlockWidth(element);
                left = cbW - rightPx.Value - width;
            }
            if (topPx == null && bottomPx.HasValue)
            {
                double cbH = FindContainingBlockHeight(element);
                top = cbH - bottomPx.Value - height;
            }

            return new AnchorInfo(top, left, width, height);
        }

        // For normal-flow elements, accumulate offsets from margins, padding,
        // borders, and preceding sibling heights up the ancestor chain.
        double marginLeft = TryParsePx(props.GetValueOrDefault("margin-left")) ?? 0;
        double marginTop = TryParsePx(props.GetValueOrDefault("margin-top")) ?? 0;
        double marginRight = TryParsePx(props.GetValueOrDefault("margin-right")) ?? 0;
        ParseMarginShorthand(props, ref marginLeft, ref marginTop, ref marginRight);

        double accLeft = marginLeft;
        double accTop = marginTop;

        // Add height of preceding siblings (vertical stacking in normal flow).
        accTop += ComputePrecedingSiblingHeights(element);

        // Walk up ancestors to accumulate margins, padding, and borders.
        var ancestor = ParentEl(element);
        while (ancestor != null)
        {
            var ancProps = GetComputedProps(ancestor);

            // Check if ancestor establishes a CB — if so, stop here.
            if (EstablishesContainingBlock(ancProps))
            {
                accLeft += TryParsePx(ancProps.GetValueOrDefault("padding-left")) ?? 0;
                accTop += TryParsePx(ancProps.GetValueOrDefault("padding-top")) ?? 0;
                accLeft += TryParsePx(ancProps.GetValueOrDefault("border-left-width")) ?? 0;
                accTop += TryParsePx(ancProps.GetValueOrDefault("border-top-width")) ?? 0;
                break;
            }

            // Accumulate ancestor margin + padding + border.
            double ancML = TryParsePx(ancProps.GetValueOrDefault("margin-left")) ?? 0;
            double ancMT = TryParsePx(ancProps.GetValueOrDefault("margin-top")) ?? 0;
            double ancMR = 0;
            ParseMarginShorthand(ancProps, ref ancML, ref ancMT, ref ancMR);

            // Apply UA default body margin (8px) if body has no explicit margin.
            if (string.Equals(ancestor.TagName, "body", StringComparison.OrdinalIgnoreCase) &&
                ancML == 0 && ancMT == 0 &&
                !ancProps.ContainsKey("margin") &&
                !ancProps.ContainsKey("margin-left") &&
                !ancProps.ContainsKey("margin-top"))
            {
                ancML = 8;
                ancMT = 8;
                ancMR = 8;
            }

            accLeft += ancML;
            accTop += ancMT;
            accLeft += TryParsePx(ancProps.GetValueOrDefault("padding-left")) ?? 0;
            accTop += TryParsePx(ancProps.GetValueOrDefault("padding-top")) ?? 0;
            accLeft += TryParsePx(ancProps.GetValueOrDefault("border-left-width")) ?? 0;
            accTop += TryParsePx(ancProps.GetValueOrDefault("border-top-width")) ?? 0;
            accTop += ComputePrecedingSiblingHeights(ancestor);

            ancestor = ParentEl(ancestor);
        }

        // For block-level elements without explicit width, compute width
        // from the containing block content width minus horizontal margins.
        if (width == 0)
        {
            string? display = props.GetValueOrDefault("display");
            bool isInline = display != null &&
                (display.Contains("inline", StringComparison.OrdinalIgnoreCase) &&
                 !display.Contains("inline-block", StringComparison.OrdinalIgnoreCase));

            if (!isInline)
            {
                double cbWidth = FindContainingBlockWidth(element);
                width = cbWidth - marginLeft - marginRight;
                if (width < 0) width = 0;
            }
        }

        return new AnchorInfo(accTop, accLeft, width, height);
    }

    /// <summary>
    /// Tries to source the anchor's box from the renderer's real layout,
    /// returning it in the same containing-block-relative frame the
    /// CSS-property estimator uses. This is the accurate path for inline anchors
    /// (inline flow + font metrics), which the estimator cannot model. Returns
    /// <c>false</c> — so the caller falls back to the CSS-property estimator — when
    /// the element produced no usable box, or in the event
    /// <see cref="DomBridgeUtils.UseSharedLayoutGeometry"/> is turned off; it defaults to on, so the
    /// real-layout path is what normally answers.
    /// </summary>
    private bool TryGetAnchorLayoutBox(DomElement element, out AnchorInfo box)
    {
        box = default!;
        if (!UseSharedLayoutGeometry)
            return false;
        if (!TryGetSharedLayoutGeometry(element, out var geometry))
            return false;

        var border = geometry.BorderBox;
        if (border.Width <= 0 && border.Height <= 0)
            return false;

        // Express the box relative to the CB's PADDING-box origin — the containing
        // block for absolutely-positioned content is the CB's padding box (CSS2.1
        // §10.1), which is also the frame ComputePositionAreaRect's grid uses (its
        // cbWidth/cbHeight are the padding-box dimensions at origin 0) and the frame
        // the abspos-inset estimator produces (`cbW − right − width` off the padding
        // box). Subtracting only the border-box origin left the anchor shifted by the
        // CB's border width, which is invisible for a borderless CB (border = 0, the
        // common case) but places the position-area grid one border-width off for a
        // bordered CB (css-anchor-position position-area-anchor-partially-outside).
        double originX = 0, originY = 0;
        var cb = FindGeometryContainingBlockAncestor(element);
        if (cb != null && TryGetSharedLayoutGeometry(cb, out var cbGeometry))
        {
            var cbProps = GetComputedProps(cb);
            originX = cbGeometry.BorderBox.Left + (TryParsePx(cbProps.GetValueOrDefault("border-left-width")) ?? 0);
            originY = cbGeometry.BorderBox.Top + (TryParsePx(cbProps.GetValueOrDefault("border-top-width")) ?? 0);
        }

        box = new AnchorInfo(
            border.Top - originY, border.Left - originX, border.Width, border.Height);
        return true;
    }
    /// <summary>
    /// Walks up from <paramref name="element"/> to the nearest ancestor that
    /// establishes a containing block (the frame the estimator measures against).
    /// </summary>
    private DomElement? FindGeometryContainingBlockAncestor(DomElement element)
    {
        for (var ancestor = ParentEl(element); ancestor != null; ancestor = ParentEl(ancestor))
            if (EstablishesContainingBlock(GetComputedProps(ancestor)))
                return ancestor;
        return null;
    }
    /// <summary>
    /// Gets computed CSS properties for an element (CSS rules + inline styles).
    /// </summary>
    private Dictionary<string, string> GetComputedProps(DomElement element)
    {
        if (_styleContext.TryGetComputedProps(element, out var cached))
            return cached;

        if (_styleContext.TryGetComputedPropsInProgress(element, out var inProgress))
            return inProgress;

        // Route the whole computed-style pipeline — cascade + inline, custom-property/
        // var() and CSS-wide-keyword resolution, shorthand expansion (incl. inset →
        // top/right/bottom/left), attr() length substitution, relative font-weight,
        // form-control size synthesis, and logical-size aliases — through the canonical
        // engine's sparse projection. The engine reads inline style from the bridge's live
        // InlineStyleRuntimeState map (SetInlineStyleSource in GetSyncedScopedEngine), so it
        // sees JS-set and anchor-resolver-written inline that never reaches the DOM style
        // attribute. `sparseInheritance: true` reproduces the bridge's inheritance model:
        // inherited properties propagate only from explicitly-declared ancestor values, so
        // a nowhere-declared inherited property stays absent — the null-for-undeclared
        // contract the ~90 layout/anchor/serialization/scroll consumers of this map depend on.
        var props = new Dictionary<string, string>(
            GetSyncedScopedEngine(element).GetSparseComputedStyle(element, sparseInheritance: true),
            StringComparer.OrdinalIgnoreCase);
        _styleContext.SetComputedPropsInProgress(element, props);
        try
        {
            // Two reconciliations the engine's sparse projection deliberately does not do:
            // fold the explicit `inherit` keyword to the parent's value (ComputeStyle keeps
            // it raw), and seed the user-agent `display` default (a renderer policy). Both
            // are non-clobbering, so applying them after the projection preserves the prior
            // seed-first ordering (an author `display` still wins).
            ResolveExplicitInheritedValues(props, element);
            ApplyUserAgentDisplayDefaults(props, element);
            ApplyUserAgentPropertyDefaults(props, element);

            _styleContext.SetComputedProps(element, props);
            return props;
        }
        finally
        {
            _styleContext.RemoveComputedPropsInProgress(element);
        }
    }

    private void ResolveExplicitInheritedValues(Dictionary<string, string> props, DomElement element)
    {
        Dictionary<string, string>? parentProps = null;
        foreach (var key in props.Keys.ToList())
        {
            var value = props[key];
            if (!string.Equals(value?.Trim(), "inherit", StringComparison.OrdinalIgnoreCase))
                continue;

            if (ParentEl(element) != null)
            {
                parentProps ??= GetComputedProps(ParentEl(element));
                if (parentProps.TryGetValue(key, out var parentValue) && !string.IsNullOrWhiteSpace(parentValue))
                {
                    props[key] = parentValue;
                    continue;
                }
            }

            if (CSS.Dom.CssComputedDefaults.InitialValues.TryGetValue(key, out var initialValue))
                props[key] = initialValue;
            else
                props.Remove(key);
        }
    }

    /// <summary>
    /// Computes the total height of preceding siblings in normal flow.
    /// </summary>
    private double ComputePrecedingSiblingHeights(DomElement element)
    {
        if (ParentEl(element) == null) return 0;

        double totalHeight = 0;
        // Snapshot the sibling list before walking it: ParentEl(element).Children
        // enumerates the live ChildNodes list, so any mutation of it during the
        // walk (e.g. an anchor-driven DOM move on a node sharing this parent, or
        // a lazy offset query that re-enters anchor resolution) throws
        // "Collection was modified" mid-traversal and aborts the registry build
        // for the whole document (WPT issue #1131, signature
        // DomBridge.BuildAnchorRegistry). Same defensive snapshot (SnapshotChildren)
        // idiom as ResolveAnchorCenter and InlineContainingBlocks.
        foreach (var sibling in SnapshotChildren(ParentEl(element)))
        {
            if (sibling == element) break;
            if (IsText(sibling)) continue;

            var sibProps = GetComputedProps(sibling);
            string? sibPos = sibProps.GetValueOrDefault("position");
            if (sibPos == "absolute" || sibPos == "fixed") continue;

            double sibHeight = TryParsePx(sibProps.GetValueOrDefault("height")) ?? 0;
            double sibMT = TryParsePx(sibProps.GetValueOrDefault("margin-top")) ?? 0;
            double sibMB = TryParsePx(sibProps.GetValueOrDefault("margin-bottom")) ?? 0;
            double sibMR = 0;
            ParseMarginShorthand(sibProps, ref sibMT, ref sibMB, ref sibMR);

            totalHeight += sibHeight + sibMT + sibMB;
        }
        return totalHeight;
    }
}

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
    private Dictionary<string, IReadOnlyDictionary<string, string>> ParsePositionTryRules()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        CollectPositionTryRulesFromTree(DocumentElement, result);
        return result;
    }

    private void CollectPositionTryRulesFromTree(DomElement el, Dictionary<string, IReadOnlyDictionary<string, string>> result)
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
                // model. Merge each <style>'s rules into the
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
    private void ResolvePositionTryFallbacks(DomElement root, Dictionary<string, AnchorInfo> anchorRegistry, Dictionary<string, IReadOnlyDictionary<string, string>> positionTryRules) =>
        ResolvePositionTryFallbacksTree(root, anchorRegistry, positionTryRules);

    private void ResolvePositionTryFallbacksTree(DomElement element, Dictionary<string, AnchorInfo> anchorRegistry, Dictionary<string, IReadOnlyDictionary<string, string>> positionTryRules)
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
                // the engine's fallback pass skips the already-baked box. The neutralizer is
                // stamped unconditionally.
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
        Dictionary<string, IReadOnlyDictionary<string, string>> positionTryRules, string fallbackList)
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
        Dictionary<string, IReadOnlyDictionary<string, string>> positionTryRules, string fallbackList,
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
}
