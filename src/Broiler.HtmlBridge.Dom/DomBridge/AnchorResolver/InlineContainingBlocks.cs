using System.Globalization;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // -----------------------------------------------------------------
    // Inline anchor registry (anchors set via JS style manipulation)
    // -----------------------------------------------------------------

    private void BuildInlineAnchorRegistry(Dictionary<string, AnchorInfo> registry)
    {
        foreach (var el in Elements)
        {
            if (BakedInlineStyle(el).TryGetValue("anchor-name", out var anchorName) &&
                !string.IsNullOrWhiteSpace(anchorName))
            {
                var box = ComputeElementBoxWithContainer(el);
                if (box != null && !registry.ContainsKey(anchorName))
                {
                    var info = box with { SourceElement = el };
                    registry[anchorName] = info;
                    if (_anchorCandidates != null)
                    {
                        if (!_anchorCandidates.TryGetValue(anchorName, out var list))
                            _anchorCandidates[anchorName] = list = [];
                        list.Add(info);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Computes an element's box position relative to its positioned
    /// containing block, resolving <c>right</c> to <c>left</c> when needed.
    /// </summary>
    private AnchorInfo? ComputeElementBoxWithContainer(DomElement element) =>
        // Delegate to the main ComputeElementBox which already handles
        // both positioned and normal-flow elements with ancestor offset
        // accumulation and block-width computation.
        ComputeElementBox(element);

    /// <summary>
    /// Finds the width of the nearest positioned ancestor (containing block)
    /// for an absolutely positioned element.
    /// </summary>
    private double FindContainingBlockWidth(DomElement element)
    {
        var parent = ParentEl(element);
        while (parent != null)
        {
            var parentProps = GetComputedProps(parent);

            if (EstablishesContainingBlock(parentProps))
            {
                double? explicitW = TryParsePx(parentProps.GetValueOrDefault("width"));
                // Inline elements (e.g. <span>) with position:relative
                // may not have explicit width. Estimate from content.
                explicitW ??= EstimateInlineContentWidth(parent);

                // A no-width block-level containing block (e.g. a transformed
                // wrapper with no explicit width) fills ITS own containing
                // block; resolve its used width from the nearest sized
                // ancestor up the normal-flow parent chain rather than
                // defaulting to the viewport (css-anchor-position transform-005:
                // a nested no-width anchor under a sized 100px ancestor made
                // anchor-size(width) resolve to the viewport width).
                explicitW ??= ResolveBlockUsedWidthFromAncestors(parent);

                double w = explicitW ?? _viewportWidth;
                // Subtract padding from the CB width to get content width.
                w -= TryParsePx(parentProps.GetValueOrDefault("padding-left")) ?? 0;
                w -= TryParsePx(parentProps.GetValueOrDefault("padding-right")) ?? 0;

                return w;
            }

            parent = ParentEl(parent);
        }

        // No positioned ancestor found; use viewport width minus default body
        // margin (8px each side) as the effective content width for block layout.
        return _viewportWidth - 16;
    }

    /// <summary>
    /// Resolves the used content width of a block-level element that has no
    /// explicit <c>width</c> by walking up the normal-flow parent chain to the
    /// nearest ancestor with a definite width and returning its content width.
    /// A block with no explicit width fills its containing block, so a no-width
    /// wrapper (even one that establishes a containing block, e.g. via
    /// <c>transform</c>) inherits the width of an outer sized ancestor rather
    /// than defaulting to the viewport. Returns <c>null</c> when no sized
    /// ancestor is found (caller falls back to the viewport width).
    /// </summary>
    private double? ResolveBlockUsedWidthFromAncestors(DomElement blockEl)
    {
        for (var a = ParentEl(blockEl); a != null && !IsText(a); a = ParentEl(a))
        {
            var ap = GetComputedProps(a);
            double? w = TryParsePx(ap.GetValueOrDefault("width"));
            if (w.HasValue)
            {
                double cw = w.Value;
                cw -= TryParsePx(ap.GetValueOrDefault("padding-left")) ?? 0;
                cw -= TryParsePx(ap.GetValueOrDefault("padding-right")) ?? 0;
                cw -= TryParsePx(ap.GetValueOrDefault("border-left-width")) ?? 0;
                cw -= TryParsePx(ap.GetValueOrDefault("border-right-width")) ?? 0;
                return cw < 0 ? 0 : cw;
            }
        }
        return null;
    }
    /// <summary>
    /// Finds the height of the nearest positioned ancestor (containing block)
    /// for an absolutely positioned element.
    /// </summary>
    private double FindContainingBlockHeight(DomElement element)
    {
        var parent = ParentEl(element);
        while (parent != null)
        {
            var parentProps = GetComputedProps(parent);

            if (EstablishesContainingBlock(parentProps))
            {
                double? explicitH = TryParsePx(parentProps.GetValueOrDefault("height"));
                if (explicitH == null)
                {
                    // Inline elements may not have explicit height. Estimate from content.
                    explicitH = EstimateInlineContentHeight(parent);
                }
                double h = explicitH ?? _viewportHeight;
                h -= TryParsePx(parentProps.GetValueOrDefault("padding-top")) ?? 0;
                h -= TryParsePx(parentProps.GetValueOrDefault("padding-bottom")) ?? 0;
                return h;
            }
            parent = ParentEl(parent);
        }
        return _viewportHeight;
    }
    /// <summary>
    /// Returns the computed left margin of the <c>&lt;body&gt;</c> element
    /// (defaults to 8px per CSS 2 § UA stylesheet).
    /// </summary>
    private double FindBodyMarginLeft()
    {
        var body = FindBodyElement();
        if (body != null)
        {
            var props = GetComputedProps(body);
            return TryParsePx(props.GetValueOrDefault("margin-left")) ?? 8;
        }
        return 8;
    }
    /// <summary>
    /// Returns the computed top margin of the <c>&lt;body&gt;</c> element
    /// (defaults to 8px per CSS 2 § UA stylesheet).
    /// </summary>
    private double FindBodyMarginTop()
    {
        var body = FindBodyElement();
        if (body != null)
        {
            var props = GetComputedProps(body);
            return TryParsePx(props.GetValueOrDefault("margin-top")) ?? 8;
        }
        return 8;
    }
    private DomElement? FindBodyElement()
    {
        foreach (var el in Elements)
        {
            if (!IsText(el) &&
                string.Equals(el.TagName, "body", StringComparison.OrdinalIgnoreCase))
                return el;
        }
        return null;
    }
    /// <summary>
    /// Estimates the content width of an inline element (e.g. a positioned
    /// <c>&lt;span&gt;</c>) by examining its text content and child element widths.
    /// Returns <c>null</c> if the element is not an inline element.
    /// </summary>
    private double? EstimateInlineContentWidth(DomElement element)
    {
        string? display = GetComputedProps(element).GetValueOrDefault("display");
        bool isInline = IsInlineElement(element.TagName, display);
        if (!isInline) return null;

        double totalWidth = 0;
        var elProps = GetComputedProps(element);
        double fontSize = TryParsePx(elProps.GetValueOrDefault("font-size")) ?? 16;

        // Check parent font-size as well (inline elements inherit).
        if (ParentEl(element) != null)
        {
            var parentProps = GetComputedProps(ParentEl(element));
            double parentFs = TryParsePx(parentProps.GetValueOrDefault("font-size")) ?? 16;
            if (parentFs > 0) fontSize = parentFs;
        }

        // Snapshot before iterating: this estimation is reached from
        // BuildAnchorRegistry's box computation, where the live ChildNodes list
        // can be mutated mid-walk and throw "Collection was modified" (WPT issue
        // #1131) or overflow the ToList() copy ("Destination array was not long
        // enough"). SnapshotChildren tolerates both, matching the other walks here.
        foreach (var child in SnapshotChildren(element))
        {
            if (IsText(child))
            {
                // Estimate text width from font-size (Ahem font: 1ch = font-size).
                int charCount = BridgeText(child).Length;
                totalWidth += charCount * fontSize;
            }
            else
            {
                var childProps = GetComputedProps(child);
                // Skip absolutely positioned children (they don't contribute to flow width).
                string? childPos = childProps.GetValueOrDefault("position");
                if (childPos == "absolute" || childPos == "fixed") continue;

                double childW = TryParsePx(childProps.GetValueOrDefault("width")) ?? 0;
                totalWidth += childW;
            }
        }
        return totalWidth > 0 ? totalWidth : null;
    }
    /// <summary>
    /// Estimates the content height of an inline element by using its
    /// line-height or font-size.
    /// Returns <c>null</c> if the element is not an inline element.
    /// </summary>
    private double? EstimateInlineContentHeight(DomElement element)
    {
        string? display = GetComputedProps(element).GetValueOrDefault("display");
        bool isInline = IsInlineElement(element.TagName, display);
        if (!isInline) return null;

        var props = GetComputedProps(element);
        double fontSize = TryParsePx(props.GetValueOrDefault("font-size")) ?? 16;

        // Check parent for font-size and line-height (inline elements inherit).
        if (ParentEl(element) != null)
        {
            var parentProps = GetComputedProps(ParentEl(element));
            double parentFs = TryParsePx(parentProps.GetValueOrDefault("font-size")) ?? 16;
            if (parentFs > 0) fontSize = parentFs;

            string? lhVal = parentProps.GetValueOrDefault("line-height");
            if (lhVal != null)
            {
                var lhTrimmed = lhVal.Trim();
                // Check for explicit px value first (e.g. "100px").
                if (lhTrimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                {
                    double? lhPx = TryParsePx(lhTrimmed);
                    if (lhPx.HasValue) return lhPx.Value;
                }
                // Unitless values are line-height multipliers (e.g. "1", "1.5").
                if (double.TryParse(lhTrimmed,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var lhMul))
                    return parentFs * lhMul;
            }
        }

        return fontSize;
    }
    /// <summary>
    /// Determines if an element is an inline element based on its tag name
    /// and display property.
    /// </summary>
    private static bool IsInlineElement(string tagName, string? display)
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
    /// Checks whether the given element is an inline-level element that
    /// establishes a containing block (e.g. <c>&lt;span&gt;</c> with
    /// <c>position: relative</c>).  Broiler's renderer cannot correctly
    /// place absolutely positioned children inside such elements.
    /// </summary>
    private bool IsInlineContainingBlock(DomElement element)
    {
        var props = GetComputedProps(element);
        string? display = props.GetValueOrDefault("display");
        if (!IsInlineElement(element.TagName, display))
            return false;
        return EstablishesContainingBlock(props);
    }
    /// <summary>
    /// Promotes absolutely positioned children out of inline containing
    /// blocks to the nearest block-level ancestor.  Adjusts their
    /// coordinates to be relative to the block ancestor instead of the
    /// inline element.  This is needed because Broiler's renderer does
    /// not support positioning absolutely positioned elements inside
    /// inline boxes (like <c>&lt;span&gt;</c>).
    /// </summary>
    private void PromoteAbsPosFromInlineCBs(DomElement root)
    {
        // Collect all promotions first (to avoid mutating during traversal).
        var promotions = new List<(DomElement child, DomElement inlineCB, DomElement blockAncestor,
            double offX, double offY)>();
        CollectInlineCBPromotions(root, promotions);

        foreach (var (child, inlineCB, blockAncestor, offX, offY) in promotions)
        {
            // Adjust the child's left/top styles.
            // Read from computed CSS (rules + inline) to get the correct
            // current values, then write to inline style (which overrides).
            var childCss = GetComputedProps(child);
            double curLeft = TryParsePx(childCss.GetValueOrDefault("left")) ?? 0;
            double curTop = TryParsePx(childCss.GetValueOrDefault("top")) ?? 0;
            double curWidth = TryParsePx(childCss.GetValueOrDefault("width")) ?? 0;
            double curHeight = TryParsePx(childCss.GetValueOrDefault("height")) ?? 0;
            BakedInlineStyle(child)["position"] = childCss.GetValueOrDefault("position") ?? "absolute";
            // Ensure inline elements (like <span>) are treated as block-level
            // after absolute positioning, so the renderer paints backgrounds.
            string? childDisplay = childCss.GetValueOrDefault("display");
            if (IsInlineElement(child.TagName, childDisplay))
                BakedInlineStyle(child)["display"] = "block";
            BakedInlineStyle(child)["left"] = $"{(curLeft + offX).ToString(CultureInfo.InvariantCulture)}px";
            BakedInlineStyle(child)["top"] = $"{(curTop + offY).ToString(CultureInfo.InvariantCulture)}px";
            // Ensure width and height are preserved as inline styles.
            if (curWidth > 0)
                BakedInlineStyle(child)["width"] = $"{curWidth.ToString(CultureInfo.InvariantCulture)}px";
            if (curHeight > 0)
                BakedInlineStyle(child)["height"] = $"{curHeight.ToString(CultureInfo.InvariantCulture)}px";
            // Preserve background-color if specified in CSS rules.
            string? bg = childCss.GetValueOrDefault("background-color")
                      ?? childCss.GetValueOrDefault("background");
            if (!string.IsNullOrWhiteSpace(bg) && bg != "transparent" && bg != "initial")
                BakedInlineStyle(child)["background-color"] = bg;

            // Move from inline CB to block ancestor.
            RemoveChildFrom(inlineCB, child);
            blockAncestor.AppendChild(child);

            // Ensure the block ancestor has position:relative.
            var blockProps = GetComputedProps(blockAncestor);
            string? blockPos = blockProps.GetValueOrDefault("position");
            if (blockPos == null || blockPos == "static")
                BakedInlineStyle(blockAncestor)["position"] = "relative";
        }
    }
    /// <summary>
    /// Whether the inline containing block <paramref name="inlineCB"/> directly holds an
    /// absolutely-positioned <c>position-area</c> box that native mode routes to the
    /// engine (<see cref="IsNativeMvpPositionAreaBox"/>). When it does, the whole inline
    /// CB is left un-promoted so the engine can lay out the intact anchor + target subtree
    /// (P5.8d.2b inline-CB expansion).
    /// </summary>
    private bool InlineCbHasNativeAnchorBox(DomElement inlineCB)
    {
        foreach (var child in SnapshotChildren(inlineCB))
        {
            if (IsText(child)) continue;
            if (IsNativeMvpPositionAreaBox(child))
                return true;
        }
        return false;
    }

    private void CollectInlineCBPromotions(
        DomElement element,
        List<(DomElement child, DomElement inlineCB, DomElement blockAncestor,
            double offX, double offY)> promotions)
    {
        // When an inline containing block holds a native-MVP position-area box, the engine lays
        // out the whole inline subtree (anchor + positioned boxes) against the real inline-box
        // geometry, so this bridge promotion must NOT DOM-move any of that inline CB's abspos
        // children — doing so would tear the anchor out of the target's containing block and break
        // the native placement. Skip collecting from such an inline CB (still recurse for nested
        // ones). The NativeAnchorPlacement flag check is dropped in Phase 4 item-2 step 5 (a
        // provable no-op on the native default path, where it was already true).
        if (!IsText(element) && IsInlineContainingBlock(element) &&
            !InlineCbHasNativeAnchorBox(element))
        {
            var (offX, offY, blockAncestor) = ComputeInlineCBOffset(element);
            if (blockAncestor != null)
            {
                // Collect absolutely positioned children.
                foreach (var child in SnapshotChildren(element))
                {
                    if (IsText(child)) continue;
                    var childProps = GetComputedProps(child);
                    string? childPos = childProps.GetValueOrDefault("position");
                    if (childPos == "absolute" || childPos == "fixed")
                    {
                        promotions.Add((child, element, blockAncestor, offX, offY));
                    }
                }
            }
        }

        foreach (var child in SnapshotChildren(element))
            CollectInlineCBPromotions(child, promotions);
    }
    /// <summary>
    /// Computes the offset from an inline containing block to the nearest
    /// block-level ancestor.  This offset is used to adjust absolute
    /// coordinates when promoting position-area elements out of an inline CB.
    /// Returns (offsetX, offsetY, blockAncestor).
    /// </summary>
    private (double offsetX, double offsetY, DomElement? blockAncestor) ComputeInlineCBOffset(DomElement inlineCB)
    {
        // Walk up from the inline CB to find the nearest block-level ancestor.
        // The inline CB's position within its parent block is determined by:
        // - Preceding text content (widths of chars)
        // - Preceding inline siblings
        // - Line breaks
        // We estimate this from the layout context.
        var parent = ParentEl(inlineCB);
        DomElement? blockAncestor = null;

        // Find nearest block-level ancestor.
        while (parent != null)
        {
            if (!IsText(parent))
            {
                var parentProps = GetComputedProps(parent);
                string? parentDisplay = parentProps.GetValueOrDefault("display");
                if (!IsInlineElement(parent.TagName, parentDisplay))
                {
                    blockAncestor = parent;
                    break;
                }
            }
            parent = ParentEl(parent);
        }

        if (blockAncestor == null) return (0, 0, null);

        // Compute the inline CB's position within the block ancestor.
        // This accounts for preceding siblings (line breaks, text) and
        // the text/inline content before the inline CB in the same line.
        double offsetX = 0, offsetY = 0;

        // Walk from the block ancestor down to the inline CB, accumulating
        // offset from preceding siblings' dimensions and the inline CB's
        // own position within its parent.
        offsetX += EstimateInlineOffsetX(inlineCB, blockAncestor);
        offsetY += EstimateInlineOffsetY(inlineCB, blockAncestor);

        return (offsetX, offsetY, blockAncestor);
    }
    /// <summary>
    /// Estimates the horizontal position of an inline element within
    /// its nearest block ancestor, accounting for preceding text and
    /// inline content.
    /// </summary>
    private double EstimateInlineOffsetX(DomElement inlineEl, DomElement blockAncestor)
    {
        double offset = 0;
        var parent = ParentEl(inlineEl);
        while (parent != null && parent != blockAncestor)
        {
            // Accumulate horizontal position from parent's preceding content
            offset += EstimatePrecedingInlineWidth(inlineEl, parent);
            inlineEl = parent;
            parent = ParentEl(parent);
        }
        // Final level: position within the block ancestor
        if (parent == blockAncestor)
            offset += EstimatePrecedingInlineWidth(inlineEl, blockAncestor);
        return offset;
    }
    /// <summary>
    /// Estimates the vertical position of an inline element within
    /// its nearest block ancestor, accounting for preceding block
    /// siblings, line breaks (<c>&lt;br&gt;</c>), and text nodes that
    /// contain only line breaks.
    /// </summary>
    private double EstimateInlineOffsetY(DomElement inlineEl, DomElement blockAncestor)
    {
        double offset = 0;
        var parent = ParentEl(inlineEl);
        while (parent != null && parent != blockAncestor)
        {
            offset += EstimatePrecedingBlockHeight(inlineEl, parent);
            inlineEl = parent;
            parent = ParentEl(parent);
        }
        if (parent == blockAncestor)
            offset += EstimatePrecedingBlockHeight(inlineEl, blockAncestor);
        return offset;
    }
    /// <summary>
    /// Estimates the total height of preceding siblings in block/inline context,
    /// handling <c>&lt;br&gt;</c> elements (which contribute the parent's
    /// line-height) and text nodes with line breaks.
    /// </summary>
    private double EstimatePrecedingBlockHeight(DomElement element, DomElement parent)
    {
        double totalHeight = 0;
        var parentProps = GetComputedProps(parent);
        double fontSize = TryParsePx(parentProps.GetValueOrDefault("font-size")) ?? 16;
        double lineHeight = ResolveLineHeight(parentProps, fontSize);

        // Snapshot the child list: a raw foreach over the live LegacyChildList can
        // throw "Collection was modified" (or the sibling ToList() overflow) if the
        // tree is mutated mid-walk, aborting anchor resolution. SnapshotChildren
        // tolerates that, matching the idiom used across these anchor walks.
        foreach (var sibling in SnapshotChildren(parent))
        {
            if (sibling == element) break;
            if (IsText(sibling))
            {
                // Count line breaks in text content.
                var text = BridgeText(sibling);
                int lineBreaks = text.Count(c => c == '\n');
                // Don't count text node line breaks as they're usually
                // just whitespace in the HTML source.
                continue;
            }

            var sibProps = GetComputedProps(sibling);
            string? sibPos = sibProps.GetValueOrDefault("position");
            if (sibPos == "absolute" || sibPos == "fixed") continue;

            if (sibling.TagName.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                // <br> creates a line break with the parent's line-height.
                totalHeight += lineHeight;
                continue;
            }

            double sibHeight = TryParsePx(sibProps.GetValueOrDefault("height")) ?? 0;
            double sibMT = TryParsePx(sibProps.GetValueOrDefault("margin-top")) ?? 0;
            double sibMB = TryParsePx(sibProps.GetValueOrDefault("margin-bottom")) ?? 0;
            double sibMR = 0;
            ParseMarginShorthand(sibProps, ref sibMT, ref sibMB, ref sibMR);

            totalHeight += sibHeight + sibMT + sibMB;
        }
        return totalHeight;
    }
    /// <summary>
    /// Resolves the computed line-height from CSS properties.
    /// Handles unitless values (multipliers of font-size), pixel values,
    /// and the "normal" keyword (defaults to 1.2 × font-size).
    /// </summary>
    private static double ResolveLineHeight(Dictionary<string, string> props, double fontSize)
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
    /// <summary>
    /// Estimates the total width of inline content preceding <paramref name="element"/>
    /// within <paramref name="parent"/>.  This includes text nodes and inline elements.
    /// </summary>
    private double EstimatePrecedingInlineWidth(DomElement element, DomElement parent)
    {
        double width = 0;
        var props = GetComputedProps(parent);
        double fontSize = TryParsePx(props.GetValueOrDefault("font-size")) ?? 16;

        foreach (var sibling in SnapshotChildren(parent))
        {
            if (sibling == element) break;

            if (IsText(sibling))
            {
                // Decode HTML entities (e.g. &nbsp; → \u00A0) before counting.
                var text = System.Net.WebUtility.HtmlDecode(BridgeText(sibling));
                int charCount = 0;
                foreach (char c in text)
                {
                    if (c == '\n' || c == '\r') continue;
                    if (c == '\u00A0' || !char.IsWhiteSpace(c)) // &nbsp; or visible
                        charCount++;
                }
                width += charCount * fontSize;
            }
            else
            {
                var sibProps = GetComputedProps(sibling);
                string? sibPos = sibProps.GetValueOrDefault("position");
                if (sibPos == "absolute" || sibPos == "fixed") continue;

                // Check for explicit width
                double? sibW = TryParsePx(sibProps.GetValueOrDefault("width"));
                if (sibW.HasValue)
                    width += sibW.Value;
                else if (sibling.TagName.Equals("br", StringComparison.OrdinalIgnoreCase))
                    width = 0; // Line break resets horizontal position
            }
        }
        return width;
    }
}
