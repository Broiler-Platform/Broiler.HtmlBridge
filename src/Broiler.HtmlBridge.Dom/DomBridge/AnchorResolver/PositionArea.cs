using System.Globalization;
using Broiler.CSS;
using Broiler.Dom;
using Broiler.Layout;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // -----------------------------------------------------------------
    // position-area resolution
    // -----------------------------------------------------------------

    /// <summary>
    /// Resolves <c>position-area</c> values on elements that have
    /// <c>position-anchor</c>.  Computes the 3×3 grid from the anchor
    /// element's position and the containing block, then selects the
    /// region specified by position-area and sets explicit inline styles.
    /// </summary>
    private void ResolvePositionAreaValues(DomElement element, Dictionary<string, AnchorInfo> anchorRegistry,
        HashSet<DomElement> scrollContainersNeedingRelative,
        List<(DomElement element, DomElement oldParent, DomElement newParent)> deferredDomMoves)
    {
        if (!IsText(element))
        {
            var cssProps = CollectMatchedRuleProperties(element);
            foreach (var kv in BakedInlineStyle(element))
                cssProps[kv.Key] = kv.Value;

            string? positionArea = cssProps.GetValueOrDefault("position-area");
            string? positionAnchor = cssProps.GetValueOrDefault("position-anchor");

            if (!string.IsNullOrWhiteSpace(positionArea) &&
                positionArea != "none" &&
                !string.IsNullOrWhiteSpace(positionAnchor) &&
                ResolveAnchorForElement(positionAnchor, element, anchorRegistry) is { } anchor)
            {
                // Find the anchor element to determine its scroll container.
                var anchorEl = FindElementByAnchorName(positionAnchor);
                var rawScrollContainer = anchorEl != null
                    ? FindNearestScrollContainer(anchorEl)
                    : null;

                // Only use the scroll container as the CB when the
                // positioned element is actually inside that scroll
                // container.  When the element is outside (e.g. the
                // anchor is in a scrollable sibling), the grid must
                // be computed against the element's own CB.
                var scrollContainer = rawScrollContainer != null &&
                    IsDescendantOfElement(element, rawScrollContainer)
                        ? rawScrollContainer
                        : null;

                // Compute the grid cell using the anchor's scroll container
                // (or the element's own CB) as the containing block.
                //
                // For the MVP subset, skip pre-baking entirely (rect stays null) so the box's
                // position-area/position-anchor CSS survives to the render and the Broiler.Layout
                // engine's placement post-pass positions it natively. Every other box is baked
                // below. The flag check is dropped in Phase 4 item-2 step 5 — the MVP-skip is
                // unconditional (a provable no-op on the native default path, where the flag was
                // already true); only the not-yet-native residue is baked.
                var rect =
                    IsMvpNativeAnchorBox(element, positionAnchor, cssProps, scrollContainer)
                        ? null
                        : ComputePositionAreaRect(
                            element, anchor, positionArea, scrollContainer);

                // A native (un-baked) box whose containing block is the anchor's scroll
                // container still needs that scroll container to establish the CB the
                // engine resolves — the baked path adds position:relative below (inside
                // the rect!=null block); do the same here so the native CB frame matches.
                // (P5.8d.2b scroll-simulation expansion.)
                if (rect == null && scrollContainer != null)
                    scrollContainersNeedingRelative.Add(scrollContainer);

                if (rect != null)
                {
                    // Preserve position:fixed when the element already has it;
                    // otherwise default to position:absolute.
                    if (!cssProps.TryGetValue("position", out var origPos) ||
                        !origPos.Equals("fixed", StringComparison.OrdinalIgnoreCase))
                    {
                        BakedInlineStyle(element)["position"] = "absolute";
                    }
                    else
                    {
                        BakedInlineStyle(element)["position"] = "fixed";
                    }

                    double cellW = rect.Value.Width;
                    double cellH = rect.Value.Height;

                    // Percentage margins and padding resolve against the *inline
                    // size* of the containing block (CSS Writing Modes §7.4 / CSS
                    // Box Model §3): the cell's width when the containing block is a
                    // horizontal writing mode, its height when it is vertical. Insets
                    // keep resolving against their own physical axis (top/bottom →
                    // height, left/right → width) below — only margin/padding follow
                    // the inline axis. The relevant writing mode is the containing
                    // block's (the position-area cell inherits it), not the anchored
                    // box's — WPT position-area-percents-001's `.vertical` cases key
                    // off the container's writing-mode, not the box's.
                    double marginPadBasis = cellW;
                    {
                        var cbForBasis = FindContainingBlockElement(element);
                        if (cbForBasis != null && CssWritingMode.IsVertical(
                                GetComputedProps(cbForBasis).GetValueOrDefault("writing-mode")))
                            marginPadBasis = cellH;
                    }

                    // Resolve any percentage insets within the cell.
                    // CSS spec: top/bottom % resolve against CB height,
                    // left/right % resolve against CB width.  For position-area
                    // the CB is the position-area cell.
                    double insetTop = 0, insetRight = 0, insetBottom = 0, insetLeft = 0;
                    string? rawInset = cssProps.GetValueOrDefault("inset");
                    if (rawInset != null)
                    {
                        var insetParts = rawInset.Split([' ', '\t'],
                            StringSplitOptions.RemoveEmptyEntries);
                        if (insetParts.Length > 0)
                        {
                            var (t, r, b, l) = CssBoxShorthand.SelectTrbl(insetParts);
                            insetTop = ResolvePctOrPx(t, cellH);
                            insetRight = ResolvePctOrPx(r, cellW);
                            insetBottom = ResolvePctOrPx(b, cellH);
                            insetLeft = ResolvePctOrPx(l, cellW);
                        }
                    }
                    else
                    {
                        // Check individual inset properties from CSS
                        string? rawTop2 = cssProps.GetValueOrDefault("top");
                        string? rawRight2 = cssProps.GetValueOrDefault("right");
                        string? rawBottom2 = cssProps.GetValueOrDefault("bottom");
                        string? rawLeft2 = cssProps.GetValueOrDefault("left");
                        if (rawTop2 != null && rawTop2 != "auto")
                            insetTop = ResolvePctOrPx(rawTop2, cellH);
                        if (rawRight2 != null && rawRight2 != "auto")
                            insetRight = ResolvePctOrPx(rawRight2, cellW);
                        if (rawBottom2 != null && rawBottom2 != "auto")
                            insetBottom = ResolvePctOrPx(rawBottom2, cellH);
                        if (rawLeft2 != null && rawLeft2 != "auto")
                            insetLeft = ResolvePctOrPx(rawLeft2, cellW);
                    }

                    // Resolve percentage margins against the containing block's
                    // inline size (marginPadBasis: cell width for horizontal WMs,
                    // cell height for vertical ones).
                    double marginTop2 = ResolvePctOrPx(
                        cssProps.GetValueOrDefault("margin-top") ?? "0", marginPadBasis);
                    double marginRight2 = ResolvePctOrPx(
                        cssProps.GetValueOrDefault("margin-right") ?? "0", marginPadBasis);
                    double marginBottom2 = ResolvePctOrPx(
                        cssProps.GetValueOrDefault("margin-bottom") ?? "0", marginPadBasis);
                    double marginLeft2 = ResolvePctOrPx(
                        cssProps.GetValueOrDefault("margin-left") ?? "0", marginPadBasis);

                    // Resolve percentage margins from the 'margin' shorthand
                    string? marginShorthand = cssProps.GetValueOrDefault("margin");
                    if (marginShorthand != null)
                    {
                        var mp = marginShorthand.Split([' ', '\t'],
                            StringSplitOptions.RemoveEmptyEntries);
                        if (mp.Length > 0)
                        {
                            var (t, r, b, l) = CssBoxShorthand.SelectTrbl(mp);
                            if (!cssProps.ContainsKey("margin-top"))
                                marginTop2 = ResolvePctOrPx(t, marginPadBasis);
                            if (!cssProps.ContainsKey("margin-right"))
                                marginRight2 = ResolvePctOrPx(r, marginPadBasis);
                            if (!cssProps.ContainsKey("margin-bottom"))
                                marginBottom2 = ResolvePctOrPx(b, marginPadBasis);
                            if (!cssProps.ContainsKey("margin-left"))
                                marginLeft2 = ResolvePctOrPx(l, marginPadBasis);
                        }
                    }

                    // Resolve percentage padding against the same inline-size basis.
                    double padTop = 0, padRight = 0, padBottom = 0, padLeft = 0;
                    string? padShorthand = cssProps.GetValueOrDefault("padding");
                    if (padShorthand != null)
                    {
                        var pp = padShorthand.Split([' ', '\t'],
                            StringSplitOptions.RemoveEmptyEntries);
                        if (pp.Length > 0)
                        {
                            var (t, r, b, l) = CssBoxShorthand.SelectTrbl(pp);
                            padTop = ResolvePctOrPx(t, marginPadBasis);
                            padRight = ResolvePctOrPx(r, marginPadBasis);
                            padBottom = ResolvePctOrPx(b, marginPadBasis);
                            padLeft = ResolvePctOrPx(l, marginPadBasis);
                        }
                    }
                    if (cssProps.TryGetValue("padding-top", out var pt))
                        padTop = ResolvePctOrPx(pt, marginPadBasis);
                    if (cssProps.TryGetValue("padding-right", out var pr))
                        padRight = ResolvePctOrPx(pr, marginPadBasis);
                    if (cssProps.TryGetValue("padding-bottom", out var pb))
                        padBottom = ResolvePctOrPx(pb, marginPadBasis);
                    if (cssProps.TryGetValue("padding-left", out var pl))
                        padLeft = ResolvePctOrPx(pl, marginPadBasis);

                    // Resolve the element's box within the cell — the IMCB
                    // (inset-modified containing block), the used width/height
                    // (percentages against the cell, explicit lengths clamped to it,
                    // else fill the IMCB) and the alignment-based position — via the
                    // canonical Broiler.Layout model (Phase 5 item 3). The bridge keeps
                    // the CSS parsing above and the box-sizing / percentage-box
                    // branches below.
                    string? rawW = cssProps.GetValueOrDefault("width");
                    string? rawH = cssProps.GetValueOrDefault("height");
                    var box = PositionAreaGrid.ResolveElementBox(
                        rect.Value,
                        insetTop, insetRight, insetBottom, insetLeft,
                        TryParsePx(rawW), TryParsePercent(rawW),
                        TryParsePx(rawH), TryParsePercent(rawH),
                        PositionAreaValue.Parse(positionArea));

                    double imcbLeft = box.ImcbLeft, imcbTop = box.ImcbTop;
                    double imcbW = box.ImcbWidth, imcbH = box.ImcbHeight;
                    double resolvedW = box.Width, resolvedH = box.Height;
                    double finalLeft = box.Left, finalTop = box.Top;

                    // Broiler's renderer cannot place absolutely positioned
                    // children inside inline elements (e.g. <span> with
                    // position:relative).  When the containing block is an
                    // inline element, promote the coordinates to the nearest
                    // block-level ancestor and ensure it has position:relative.
                    if (scrollContainer == null)
                    {
                        var inlineCB = FindContainingBlockElement(element);
                        if (inlineCB != null && IsInlineContainingBlock(inlineCB))
                        {
                            var (inlineOffX, inlineOffY, blockAncestor) =
                                ComputeInlineCBOffset(inlineCB);
                            finalLeft += inlineOffX;
                            finalTop += inlineOffY;

                            // Ensure the block ancestor has position:relative
                            // so the renderer can place the abs-pos element.
                            if (blockAncestor != null)
                            {
                                var blockProps = GetComputedProps(blockAncestor);
                                string? blockPos = blockProps.GetValueOrDefault("position");
                                if (blockPos == null || blockPos == "static")
                                    BakedInlineStyle(blockAncestor)["position"] = "relative";
                            }

                            // Defer the move to avoid collection modification
                            // during tree traversal.
                            if (blockAncestor != null && ParentEl(element) != blockAncestor
                                && ParentEl(element) != null)
                            {
                                deferredDomMoves.Add((element, ParentEl(element), blockAncestor));
                            }
                        }
                    }

                    // Determine whether to use cell-boundary positioning
                    // (left/top/right/bottom to define the IMCB) so the
                    // renderer's box model naturally handles margins, borders,
                    // and padding.  Use this when the element stretches to
                    // fill the cell (place-self: stretch, the default for
                    // position-area) and has percentage-based box properties.
                    bool hasPercentBoxProps =
                        HasPercent(cssProps.GetValueOrDefault("margin")) ||
                        HasPercent(cssProps.GetValueOrDefault("margin-top")) ||
                        HasPercent(cssProps.GetValueOrDefault("margin-right")) ||
                        HasPercent(cssProps.GetValueOrDefault("margin-bottom")) ||
                        HasPercent(cssProps.GetValueOrDefault("margin-left")) ||
                        HasPercent(cssProps.GetValueOrDefault("padding")) ||
                        HasPercent(cssProps.GetValueOrDefault("padding-top")) ||
                        HasPercent(cssProps.GetValueOrDefault("padding-right")) ||
                        HasPercent(cssProps.GetValueOrDefault("padding-bottom")) ||
                        HasPercent(cssProps.GetValueOrDefault("padding-left")) ||
                        HasPercent(rawInset) ||
                        HasPercent(cssProps.GetValueOrDefault("top")) ||
                        HasPercent(cssProps.GetValueOrDefault("left")) ||
                        HasPercent(cssProps.GetValueOrDefault("right")) ||
                        HasPercent(cssProps.GetValueOrDefault("bottom"));

                    if (hasPercentBoxProps)
                    {
                        // Resolve all percentages explicitly and use
                        // left/top + computed content width/height.
                        // Content width = IMCB width - margins - borders - padding
                        double borderW = TryParsePx(cssProps.GetValueOrDefault("border-left-width")) ?? 0;
                        double borderE = TryParsePx(cssProps.GetValueOrDefault("border-right-width")) ?? 0;
                        double borderN = TryParsePx(cssProps.GetValueOrDefault("border-top-width")) ?? 0;
                        double borderS = TryParsePx(cssProps.GetValueOrDefault("border-bottom-width")) ?? 0;

                        // Parse border shorthand if individual widths not set
                        string? borderShort = cssProps.GetValueOrDefault("border");
                        if (borderShort != null)
                        {
                            var borderParts = borderShort.Split([' ', '\t'],
                                StringSplitOptions.RemoveEmptyEntries);
                            foreach (var bp in borderParts)
                            {
                                var bw = TryParsePx(bp);
                                if (bw.HasValue)
                                {
                                    if (borderW == 0) borderW = bw.Value;
                                    if (borderE == 0) borderE = bw.Value;
                                    if (borderN == 0) borderN = bw.Value;
                                    if (borderS == 0) borderS = bw.Value;
                                    break;
                                }
                            }
                        }

                        // Content = IMCB minus margin/border/padding on each axis
                        // (canonical Broiler.Layout used-value math, Phase 5 item 3).
                        var (contentW, contentH) = PositionAreaGrid.ContentSizeFillingImcb(
                            imcbW, imcbH,
                            new PositionAreaEdges(marginTop2, marginRight2, marginBottom2, marginLeft2),
                            new PositionAreaEdges(borderN, borderE, borderS, borderW),
                            new PositionAreaEdges(padTop, padRight, padBottom, padLeft));

                        finalLeft = imcbLeft;
                        finalTop = imcbTop;

                        // Set resolved pixel values for margins and padding
                        // to override the percentage values from CSS.
                        BakedInlineStyle(element)["margin-top"] = $"{marginTop2.ToString(CultureInfo.InvariantCulture)}px";
                        BakedInlineStyle(element)["margin-right"] = $"{marginRight2.ToString(CultureInfo.InvariantCulture)}px";
                        BakedInlineStyle(element)["margin-bottom"] = $"{marginBottom2.ToString(CultureInfo.InvariantCulture)}px";
                        BakedInlineStyle(element)["margin-left"] = $"{marginLeft2.ToString(CultureInfo.InvariantCulture)}px";
                        BakedInlineStyle(element)["padding-top"] = $"{padTop.ToString(CultureInfo.InvariantCulture)}px";
                        BakedInlineStyle(element)["padding-right"] = $"{padRight.ToString(CultureInfo.InvariantCulture)}px";
                        BakedInlineStyle(element)["padding-bottom"] = $"{padBottom.ToString(CultureInfo.InvariantCulture)}px";
                        BakedInlineStyle(element)["padding-left"] = $"{padLeft.ToString(CultureInfo.InvariantCulture)}px";
                        BakedInlineStyle(element).Remove("margin");
                        BakedInlineStyle(element).Remove("padding");
                        BakedInlineStyle(element).Remove("inset");

                        resolvedW = contentW;
                        resolvedH = contentH;
                    }

                    // When box-sizing is border-box, the resolved width/height
                    // represent the total (border-box) dimensions.  The renderer
                    // treats the CSS 'width'/'height' properties as content-box
                    // dimensions, so we need to subtract borders and padding to
                    // get the correct content width/height.  We also set explicit
                    // pixel border-width values in the inline style so the
                    // renderer uses CSS-spec values (medium=3px) rather than its
                    // own default (medium=2px).
                    double borderBoxW = resolvedW;
                    double borderBoxH = resolvedH;
                    string? boxSizing = cssProps.GetValueOrDefault("box-sizing");
                    if (boxSizing != null &&
                        boxSizing.Equals("border-box", StringComparison.OrdinalIgnoreCase) &&
                        !hasPercentBoxProps)
                    {
                        double bdrL = CssBorderWidth.Resolve(cssProps, "border-left-width", "border");
                        double bdrR = CssBorderWidth.Resolve(cssProps, "border-right-width", "border");
                        double bdrT = CssBorderWidth.Resolve(cssProps, "border-top-width", "border");
                        double bdrB = CssBorderWidth.Resolve(cssProps, "border-bottom-width", "border");

                        // border-box → content-box: subtract border + padding per axis
                        // (canonical Broiler.Layout used-value math, Phase 5 item 3).
                        (resolvedW, resolvedH) = PositionAreaGrid.BorderBoxToContentSize(
                            resolvedW, resolvedH,
                            new PositionAreaEdges(bdrT, bdrR, bdrB, bdrL),
                            new PositionAreaEdges(padTop, padRight, padBottom, padLeft));

                        // Override border-width with explicit pixel values so
                        // the renderer doesn't use its own keyword mapping
                        // (which maps medium→2px instead of the spec's 3px).
                        // Always set the value (even 0px) to ensure the
                        // renderer doesn't fall back to its keyword defaults.
                        BakedInlineStyle(element)["border-top-width"] = $"{bdrT.ToString(CultureInfo.InvariantCulture)}px";
                        BakedInlineStyle(element)["border-right-width"] = $"{bdrR.ToString(CultureInfo.InvariantCulture)}px";
                        BakedInlineStyle(element)["border-bottom-width"] = $"{bdrB.ToString(CultureInfo.InvariantCulture)}px";
                        BakedInlineStyle(element)["border-left-width"] = $"{bdrL.ToString(CultureInfo.InvariantCulture)}px";
                    }

                    BakedInlineStyle(element)["left"] = $"{finalLeft.ToString(CultureInfo.InvariantCulture)}px";
                    BakedInlineStyle(element)["top"] = $"{finalTop.ToString(CultureInfo.InvariantCulture)}px";
                    BakedInlineStyle(element)["width"] = $"{resolvedW.ToString(CultureInfo.InvariantCulture)}px";
                    BakedInlineStyle(element)["height"] = $"{resolvedH.ToString(CultureInfo.InvariantCulture)}px";

                    // Record the scroll container for deferred position:relative.
                    if (scrollContainer != null)
                        scrollContainersNeedingRelative.Add(scrollContainer);

                    // Store resolved offsets for JS offset property queries.
                    // Use border-box dimensions (matching offsetWidth/offsetHeight).
                    SetPositionAreaResolution(element, finalLeft, finalTop, borderBoxW, borderBoxH);

                    // Non-MVP box: this box was just baked into explicit inline pixel values, so
                    // neutralize position-area on it (inline wins the cascade) to stop the engine's
                    // placement post-pass from repositioning an already-placed box. Stamped
                    // unconditionally as of Phase 4 item-2 step 5 (harmless on the retired baked
                    // path, where the engine post-pass does not run).
                    BakedInlineStyle(element)["position-area"] = "none";
                }
            }
        }

        // Snapshot before the recursive descent: element.Children enumerates the
        // live ChildNodes list, and resolving a child can re-enter anchor
        // resolution through a lazy offset/box query (ComputeElementBox →
        // ResolvePositionAreaForElement) or surface a DOM move on a node sharing
        // this parent, mutating the list mid-walk and throwing "Collection was
        // modified" — which aborts position-area resolution for the whole
        // document (WPT issue #1147, signature
        // DomBridge.ResolvePositionAreaValues). Same defensive snapshot idiom
        // (SnapshotChildren) as BuildAnchorRegistry / ResolveAnchorCenter /
        // InlineContainingBlocks.
        foreach (var child in SnapshotChildren(element))
            ResolvePositionAreaValues(child, anchorRegistry, scrollContainersNeedingRelative,
                deferredDomMoves);
    }

    /// <summary>
    /// Decides whether a <c>position-area</c> box belongs to the native-placement MVP
    /// subset (P5.8d.2b) — the boxes the Broiler.Layout engine's placement post-pass
    /// currently reproduces exactly, so the bridge can hand them off instead of
    /// pre-baking. Requires: an explicit dashed-ident <c>position-anchor</c> that names a
    /// registered anchor, and no <c>position-try</c> or <c>anchor()</c>/<c>anchor-size()</c>
    /// on the element (those entangled features stay on the bridge path until later
    /// expansions). Both block and inline containing blocks (relative or abspos/fixed) are
    /// in the subset — the engine places against the real inline-box geometry the bridge
    /// estimator could not. An intervening scroll container (the anchor's scroll container
    /// is the box's containing block) is also in the subset (P5.8d.2b scroll-simulation
    /// expansion): the bridge's <c>ApplyScrollSimulation</c> DOM-shifts the scrolled
    /// content before render, so the engine reads the already-scrolled anchor geometry.
    /// </summary>
    private bool IsMvpNativeAnchorBox(
        DomElement element, string positionAnchor, Dictionary<string, string> cssProps,
        DomElement? scrollContainer)
    {
        // An intervening scroll container (the anchor's scroll container is this box's
        // containing block) is now IN the MVP subset (P5.8d.2b scroll-simulation
        // expansion). The bridge's ApplyScrollSimulation pre-pass DOM-shifts the scroll
        // container's content (wrapping it in a position:relative offset div) before the
        // final render, so the engine's box tree already carries the scrolled anchor
        // geometry; the native post-pass reads the shifted anchor border box and places
        // the target against it. The scroll container is still registered as the box's
        // containing block below (scrollContainersNeedingRelative) in native mode too, so
        // the CB frame the engine resolves is identical to the baked path — verified
        // baked-vs-native pixel-identical by ScrollContainerAnchorParityTests (with and
        // without a scroll offset, positioned and static scroll containers).

        // An explicit, named anchor — a dashed-ident like `--a`, not the default `auto`.
        var anchorName = positionAnchor.Trim();
        if (!anchorName.StartsWith("--", StringComparison.Ordinal))
            return false;

        // The anchor-name must be registered. Shared names are now allowed: the engine's
        // AnchorRegistry keeps every candidate and binds a query to the one in its own
        // containing-block scope (matching the bridge's ResolveAnchorForElement), so a
        // duplicate name no longer forces the bridge path.
        if (_anchorCandidates == null || !_anchorCandidates.ContainsKey(anchorName))
            return false;

        // An inline containing block — whether relatively or absolutely/fixed positioned —
        // is now IN the MVP subset (P5.8d.2b inline-CB and abspos-inline-CB expansions): the
        // engine's abspos layout places a box inside an inline element against the real
        // inline-box bounding box (CssBox.GetInlineBoundingBox, CSS2.1 §10.1), which the
        // bridge's estimator could not. The box stays inside the inline CB
        // (PromoteAbsPosFromInlineCBs skips the whole inline CB in native mode — see
        // InlineCbHasNativeAnchorBox) so the engine lays out the intact anchor + target
        // subtree.
        //
        // The engine does not blockify an abspos inline element (the cascade's CSS2.1 §9.7
        // adjustment fires only for float — see DomParser.CascadeApplyStyles), so it treats
        // an abspos <span> containing block as inline and reads its inline bounding box. For
        // an out-of-flow inline whose content is a simple run that equals the block
        // shrink-to-fit extent (the css-anchor-position abs-inline-container case), that is
        // the correct containing-block rect, so the anchored boxes place identically to a
        // relative inline CB. No bridge/engine disagreement remains; both inline-CB cases go
        // native.

        // position-try and anchor()/anchor-size() are out of the MVP subset.
        if (cssProps.ContainsKey("position-try-fallbacks") || cssProps.ContainsKey("position-try"))
            return false;
        foreach (var value in cssProps.Values)
        {
            if (value.Contains("anchor(", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("anchor-size(", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether native mode would hand this element's <c>position-area</c> off to the
    /// engine's placement post-pass instead of pre-baking it — i.e. it satisfies
    /// <see cref="IsMvpNativeAnchorBox"/> under the same computed-property, anchor and
    /// scroll-container resolution <see cref="ResolvePositionAreaValues"/> uses. Used by
    /// the inline-CB promotion pass to leave such a box (and its inline containing block's
    /// subtree) intact for the engine rather than DOM-moving it out. Returns
    /// <c>false</c> when the element has no resolvable <c>position-area</c>/anchor.
    /// </summary>
    private bool IsNativeMvpPositionAreaBox(DomElement element)
    {
        if (IsText(element))
            return false;

        var cssProps = CollectMatchedRuleProperties(element);
        foreach (var kv in BakedInlineStyle(element))
            cssProps[kv.Key] = kv.Value;

        string? positionArea = cssProps.GetValueOrDefault("position-area");
        string? positionAnchor = cssProps.GetValueOrDefault("position-anchor");
        if (string.IsNullOrWhiteSpace(positionArea) || positionArea == "none" ||
            string.IsNullOrWhiteSpace(positionAnchor))
            return false;

        // The registration check lives in IsMvpNativeAnchorBox (via _anchorCandidates,
        // which is populated alongside the full anchor registry), so an unregistered
        // anchor already yields false there — no separate anchor lookup needed.
        var anchorEl = FindElementByAnchorName(positionAnchor);
        var rawScrollContainer = anchorEl != null ? FindNearestScrollContainer(anchorEl) : null;
        var scrollContainer = rawScrollContainer != null &&
            IsDescendantOfElement(element, rawScrollContainer)
                ? rawScrollContainer
                : null;

        return IsMvpNativeAnchorBox(element, positionAnchor, cssProps, scrollContainer);
    }

    /// <summary>
    /// Computes the grid cell for a given <c>position-area</c> value using
    /// the 3×3 grid defined by the anchor element and containing block.
    /// When <paramref name="scrollContainer"/> is provided, the grid is
    /// computed relative to that container (the anchor's scroll container)
    /// rather than the target element's normal containing block.
    /// </summary>
    private PositionAreaCell? ComputePositionAreaRect(DomElement element, AnchorInfo anchor, string positionArea,
        DomElement? scrollContainer = null)
    {
        double cbWidth, cbHeight;
        double anchorLeft, anchorRight, anchorTop, anchorBottom;
        double cbOffsetX = 0, cbOffsetY = 0;

        if (scrollContainer != null)
        {
            // Use the scroll container's own dimensions as the CB.
            var scProps = GetComputedProps(scrollContainer);
            cbWidth = TryParsePx(scProps.GetValueOrDefault("width")) ?? _viewportWidth;
            cbHeight = TryParsePx(scProps.GetValueOrDefault("height")) ?? _viewportHeight;

            // For the position-area grid, use the scroll content dimensions
            // (the actual scrollable area) rather than the scroll port dimensions.
            // The scroll container is the CB for the target — either it already
            // establishes a CB, or ResolvePositionAreaValues will apply
            // position:relative to it via scrollContainersNeedingRelative — so
            // keying the grid off the scrollable extent is what the target sees.
            // Otherwise a target with position-area at the anchor's own edge
            // (e.g. "bottom" on an anchor at the scrollport bottom) collapses to
            // a zero-height cell — the "bottom" row spans gridBottom..anchorBottom
            // which are both the scrollport bottom (WPT #1177,
            // position-visibility-remove-anchors-visible).
            double scrollContentWidth = FindScrollContentWidth(scrollContainer, cbWidth);
            double scrollContentHeight = FindScrollContentHeight(scrollContainer, cbHeight);

            // Compute anchor position relative to the scroll container.
            var anchorRelPos = ComputeAnchorRelativeToContainer(anchor, scrollContainer);
            anchorLeft = anchorRelPos.Left;
            anchorRight = anchorRelPos.Left + anchor.Width;
            anchorTop = anchorRelPos.Top;
            anchorBottom = anchorRelPos.Top + anchor.Height;

            // Use scroll content dimensions for the grid edges.
            cbWidth = scrollContentWidth;
            cbHeight = scrollContentHeight;
        }
        else
        {
            cbWidth = FindContainingBlockWidth(element);
            cbHeight = FindContainingBlockHeight(element);
            anchorLeft = anchor.Left;
            anchorRight = anchor.Right;
            anchorTop = anchor.Top;
            anchorBottom = anchor.Bottom;

            // Determine the containing block for coordinate system.
            var cbEl = FindContainingBlockElement(element);
            if (cbEl == null)
            {
                // No positioned ancestor → initial CB = body content area.
                // Anchor coordinates from ComputeElementBox are document-absolute,
                // and the grid origin must account for body margin.
                cbOffsetX = FindBodyMarginLeft();
                cbOffsetY = FindBodyMarginTop();

                // ComputeElementBox measures the anchor relative to its nearest
                // CB-establishing ancestor and stops there. When that ancestor is
                // a non-body wrapper (e.g. a transformed div), the returned coords
                // are wrapper-relative, not document-absolute as assumed above.
                // Shift the anchor into the document frame by adding the wrapper's
                // own position (css-anchor-position transform-005: a transformed
                // anchor wrapper below a <p> otherwise placed block-end one text
                // line too high, exposing the red containing block).
                var anchorWrapper = anchor.SourceElement != null
                    ? FindContainingBlockElement(anchor.SourceElement)
                    : null;
                if (anchorWrapper != null)
                {
                    var wrapperBox = ComputeElementBox(anchorWrapper);
                    if (wrapperBox != null)
                    {
                        anchorLeft += wrapperBox.Left;
                        anchorRight += wrapperBox.Left;
                        anchorTop += wrapperBox.Top;
                        anchorBottom += wrapperBox.Top;
                    }
                }
            }
            else
            {
                // A positioned ancestor was found. The anchor's CSS top/left
                // values from ComputeElementBox are relative to the anchor's
                // own CB. If the anchor's CB is the same as the target's CB,
                // coordinates are already in the right frame — grid origin is 0.
                // Otherwise, we need to map the anchor coordinates.
                var anchorCBEl = FindAnchorContainingBlock(element, cbEl);
                if (anchorCBEl == cbEl)
                {
                    // Same CB → anchor coords are CB-relative → grid at origin.
                    cbOffsetX = 0;
                    cbOffsetY = 0;
                }
                else
                {
                    // Different CBs → use document coordinates.
                    var box = ComputeElementBox(cbEl);
                    cbOffsetX = box?.Left ?? 0;
                    cbOffsetY = box?.Top ?? 0;
                }
            }
        }

        // The 3×3 grid geometry (edges from the CB∪anchor union, cell selection per
        // block/inline span) is the canonical Broiler.Layout.PositionAreaGrid model
        // (Phase 5 item 3). This method keeps the DOM-dependent resolution of the CB
        // frame and the anchor's edges above; the neutral grid math is delegated.
        return PositionAreaGrid.ComputeCell(
            cbOffsetX, cbOffsetY, cbWidth, cbHeight,
            anchorLeft, anchorTop, anchorRight, anchorBottom,
            PositionAreaValue.Parse(positionArea));
    }

    // Scroll-container geometry helpers (FindScrollContentWidth/Height, ComputeAnchorRelativeToContainer,
    // FindNearestScrollContainer, IsDescendantOfElement) live in the sibling partial
    // PositionArea.ScrollGeometry.cs to keep each file under the Phase 3 750-line ratchet.
}
