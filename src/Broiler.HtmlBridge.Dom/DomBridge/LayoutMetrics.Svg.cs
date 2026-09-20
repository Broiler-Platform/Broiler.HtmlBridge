using System.Globalization;
using Broiler.CSS;
using Broiler.Dom;
using Broiler.HtmlBridge.Logging;
using Broiler.Layout.Engine;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// Sibling partial peeled out of <c>LayoutMetrics.cs</c> to keep it
/// under the 750-line guideline: SVG geometry/text-metric resolution, element zoom / transform-scale
/// resolution, and the border-box size helpers. Pure partial-class relocation — no signature,
/// accessibility, or logic change.
/// </summary>
public sealed partial class DomBridge
{
    private double GetUsedZoomForElement(DomElement element)
    {
        var props = GetComputedProps(element);
        var specifiedZoom = props.GetValueOrDefault("zoom");
        var parentZoom = ParentEl(element) != null ? GetUsedZoomForElement(ParentEl(element)) : RootUsedZoomBase();
        return CssZoom.ResolveUsed(specifiedZoom, parentZoom);
    }

    /// <summary>
    /// The used-zoom base at the document root. Normally <c>1.0</c>; in native visual-viewport mode
    /// (<see cref="NativeVisualViewport"/>) the document-root pinch-zoom scale is folded in here as a
    /// root-level zoom, matching the extraction scale (<see cref="Broiler.Layout.Engine.NativeAnchorPlacement.VisualViewportScale"/>,
    /// patch 0006) — so a scaled <c>BoxGeometry</c> divides back to unaffected <c>offset*</c> while
    /// <c>getBoundingClientRect</c> stays scaled (this model treats pinch-zoom as a root zoom). Off the
    /// native path this is <c>1.0</c>, so the DOM `zoom` bake continues to carry the factor unchanged.
    /// </summary>
    private double RootUsedZoomBase() =>
        NativeVisualViewport && HasActiveVisualViewport() ? GetVisualViewportScale() : 1.0;


    private string? GetElementTransformValue(DomElement element)
    {
        var props = GetComputedProps(element);
        var transform = props.GetValueOrDefault("transform");
        if (!string.IsNullOrWhiteSpace(transform) &&
            !string.Equals(transform.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return transform;
        }

        return TryGetAttribute(element, "transform", out var attributeTransform)
            ? attributeTransform
            : null;
    }

    private bool TryGetSvgChildrenUnionRect(DomElement element,
        out (double Left, double Top, double Width, double Height) rect)
    {
        var found = false;
        var minLeft = 0d;
        var minTop = 0d;
        var maxRight = 0d;
        var maxBottom = 0d;

        foreach (var child in ChildElements(element))
        {
            if (IsText(child) || child.TagName.StartsWith('#'))
                continue;

            var (Left, Top, Width, Height) = GetHitTestRectForElement(child);
            if (Width <= 0 || Height <= 0)
                continue;

            if (!found)
            {
                found = true;
                minLeft = Left;
                minTop = Top;
                maxRight = Left + Width;
                maxBottom = Top + Height;
                continue;
            }

            minLeft = Math.Min(minLeft, Left);
            minTop = Math.Min(minTop, Top);
            maxRight = Math.Max(maxRight, Left + Width);
            maxBottom = Math.Max(maxBottom, Top + Height);
        }

        rect = found
            ? (minLeft, minTop, Math.Max(0, maxRight - minLeft), Math.Max(0, maxBottom - minTop))
            : (0, 0, 0, 0);
        return found;
    }

    private bool TryGetSvgTextHitTestRect(DomElement element,
        out (double Left, double Top, double Width, double Height) rect)
    {
        var text = GetDirectTextContent(element);
        if (string.IsNullOrWhiteSpace(text))
        {
            rect = (0, 0, 0, 0);
            return false;
        }

        var fontSize = ResolveFontSizeForElement(element);
        if (fontSize <= 0)
            fontSize = 16;

        var width = text
            .Replace("\r", string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .DefaultIfEmpty(string.Empty)
            .Max(line => line.Length) * fontSize * 0.6;
        if (width <= 0)
        {
            rect = (0, 0, 0, 0);
            return false;
        }

        var baselineX = ResolveSvgTextCoordinate(element, "x");
        var baselineY = ResolveSvgTextCoordinate(element, "y");
        if (IsSvgTextPathElement(element) &&
            TryResolveSvgTextPathStart(element, out var pathStart))
        {
            if (!HasOwnSvgCoordinate(element, "x"))
                baselineX = pathStart.X;
            if (!HasOwnSvgCoordinate(element, "y"))
                baselineY = pathStart.Y;
        }

        var viewport = FindNearestSvgViewportAncestor(element);
        if (viewport != null)
        {
            var (Left, Top, Width, Height) = ComputeRenderedRect(viewport);
            baselineX += Left;
            baselineY += Top;
        }

        rect = (baselineX, baselineY - fontSize, width, fontSize);
        return true;
    }

    private double ResolveSvgTextCoordinate(DomElement element, string attributeName)
    {
        for (var current = element; current != null; current = ParentEl(current))
        {
            if (!IsSvgTextContentElement(current))
                continue;

            if (TryGetAttribute(current, attributeName, out var rawValue))
            {
                var percentageBasis = ResolveContainingBlockReferenceLength(
                    current,
                    vertical: string.Equals(attributeName, "y", StringComparison.OrdinalIgnoreCase));
                var resolved = ParseCssLengthToPixelsWithViewport(rawValue, current, percentageBasis: percentageBasis);
                if (resolved > 0 || string.Equals(rawValue?.Trim(), "0", StringComparison.Ordinal))
                    return resolved;

                var scalar = rawValue?
                    .Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (double.TryParse(
                    scalar,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var numericValue))
                {
                    return numericValue;
                }
            }

            if (IsSvgTextPathElement(current) &&
                TryResolveSvgTextPathStart(current, out var pathStart))
            {
                return string.Equals(attributeName, "y", StringComparison.OrdinalIgnoreCase)
                    ? pathStart.Y
                    : pathStart.X;
            }
        }

        return 0;
    }

    private bool TryResolveSvgTextPathStart(DomElement element, out (double X, double Y) point)
    {
        point = default;
        if (!TryGetAttribute(element, "href", out var href) &&
            !TryGetAttribute(element, "xlink:href", out href))
        {
            return false;
        }

        href = href?.Trim();
        if (string.IsNullOrWhiteSpace(href) || !href.StartsWith('#'))
            return false;

        var documentElement = GetOwningDocumentElement(element);
        var referencedPath = documentElement != null
            ? FindInTree(documentElement, candidate => string.Equals(candidate.Id, href[1..], StringComparison.Ordinal))
            : null;
        if (referencedPath == null ||
            !TryGetAttribute(referencedPath, "d", out var pathData) ||
            string.IsNullOrWhiteSpace(pathData))
        {
            return false;
        }

        var moveMatch = TryResolveSvgTextPathStartRegex().Match(pathData);
        if (!moveMatch.Success ||
            !double.TryParse(moveMatch.Groups["x"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(moveMatch.Groups["y"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var y))
        {
            return false;
        }

        point = (x, y);
        return true;
    }

    private double GetBorderBoxWidth(Dictionary<string, string> props, DomElement? element = null)
    {
        var containingBlockWidth = element != null ? ResolveContainingBlockReferenceLength(element, vertical: false) : (double?)null;
        return ParseCssLengthToPixelsWithViewport(props.GetValueOrDefault("width"), element, percentageBasis: containingBlockWidth)
             + ParseCssLengthToPixelsWithViewport(props.GetValueOrDefault("padding-left"), element, percentageBasis: containingBlockWidth)
             + ParseCssLengthToPixelsWithViewport(props.GetValueOrDefault("padding-right"), element, percentageBasis: containingBlockWidth)
             + ParseCssLengthToPixelsWithViewport(props.GetValueOrDefault("border-left-width"), element)
             + ParseCssLengthToPixelsWithViewport(props.GetValueOrDefault("border-right-width"), element);
    }

    private double GetBorderBoxHeight(Dictionary<string, string> props, DomElement? element = null)
    {
        var containingBlockWidth = element != null ? ResolveContainingBlockReferenceLength(element, vertical: false) : (double?)null;
        var containingBlockHeight = element != null ? ResolveContainingBlockReferenceLength(element, vertical: true) : (double?)null;
        return ParseCssLengthToPixelsWithViewport(props.GetValueOrDefault("height"), element, percentageBasis: containingBlockHeight)
             + ParseCssLengthToPixelsWithViewport(props.GetValueOrDefault("padding-top"), element, percentageBasis: containingBlockWidth)
             + ParseCssLengthToPixelsWithViewport(props.GetValueOrDefault("padding-bottom"), element, percentageBasis: containingBlockWidth)
             + ParseCssLengthToPixelsWithViewport(props.GetValueOrDefault("border-top-width"), element)
             + ParseCssLengthToPixelsWithViewport(props.GetValueOrDefault("border-bottom-width"), element);
    }

    // CreateSvgLengthValue moved to the SvgElementBinding feature module — its only
    // consumer (the SVGAnimatedLength stub) moved there too.
}

/// <summary>
/// Client-space geometry for the elements <em>inside</em> an SVG viewport.
/// </summary>
/// <remarks>
/// <para>
/// The SVG root itself is a replaced box the layout engine places, so it has always had geometry.
/// Nothing below it did: an SVG child is not in the CSS box tree, so
/// <c>getBoundingClientRect</c> answered <c>0,0,0,0</c> for every shape, and hit testing — which
/// asks each element for a rect and skips anything empty — could never descend past the root.
/// <c>document.elementFromPoint</c> over a <c>&lt;rect&gt;</c> returned the <c>&lt;svg&gt;</c>.
/// </para>
/// <para>
/// This resolves a shape's own geometry attributes into client space, which is the piece those two
/// answers were missing. Three mappings compose, outermost first: the viewport's rendered origin
/// (from the box tree), the <c>viewBox</c> transform, and the accumulated <c>translate()</c> of the
/// ancestor <c>&lt;g&gt;</c> chain. A group's rect stays the union of its children's — it already
/// was — which now works because the children have rects at all.
/// </para>
/// <para>
/// <b>What is modelled and what is not.</b> Shapes with explicit geometry attributes resolve
/// exactly: <c>rect</c>, <c>image</c>, <c>foreignObject</c>, <c>circle</c>, <c>ellipse</c>,
/// <c>line</c>, <c>polyline</c> and <c>polygon</c>. <c>path</c> and <c>use</c> do not — a path's
/// bounds need the curve, and <c>use</c> needs its referent's — so they return no rect and behave
/// exactly as every shape did before, rather than being given a wrong one. Of the transform
/// functions only <c>translate()</c> is accumulated, which is what an ancestor chain overwhelmingly
/// carries; a <c>rotate</c>/<c>scale</c>/<c>matrix</c> on the chain is ignored rather than
/// approximated, so its subtree keeps the untransformed rect. <c>preserveAspectRatio</c> is
/// modelled at its default (<c>xMidYMid meet</c>): uniform scale, centred. Each of these is a
/// bounded, nameable gap rather than a silent zero.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// The client rect of an element inside an SVG viewport, from SVG geometry rather than from the
    /// CSS box tree it is not in.
    /// </summary>
    /// <remarks>
    /// The one entry point for both consumers — <c>getBoundingClientRect</c> and hit testing — so
    /// the two cannot disagree about where a shape is. A group has no geometry of its own and takes
    /// the union of its children's, which is what a browser reports for it and what having shape
    /// rects at all makes possible.
    /// </remarks>
    private bool TryGetSvgClientRect(DomElement element,
        out (double Left, double Top, double Width, double Height) rect)
    {
        if (TryGetSvgGeometryRect(element, out rect))
            return true;

        if (IsSvgGroupElement(element) && TryGetSvgChildrenUnionRect(element, out rect))
            return true;

        rect = (0, 0, 0, 0);
        return false;
    }

    /// <summary>
    /// The client rect of an SVG shape, or <see langword="false"/> when this element is not one
    /// whose geometry can be resolved from its attributes.
    /// </summary>
    private bool TryGetSvgGeometryRect(DomElement element,
        out (double Left, double Top, double Width, double Height) rect)
    {
        rect = (0, 0, 0, 0);

        if (!IsSvgGeometryElement(element))
            return false;

        var viewport = FindNearestSvgViewportAncestor(element);
        if (viewport == null)
            return false;

        if (!TryGetSvgUserSpaceBounds(element, viewport, out var user))
            return false;

        // The ancestor <g transform="translate(…)"> chain is user-space, so it is added before the
        // viewBox scale rather than after it.
        var (offsetX, offsetY) = AccumulatedSvgTranslate(element, viewport);
        var map = GetSvgViewBoxMapping(viewport);
        var origin = ComputeRenderedRect(viewport);

        var width = user.Width * map.ScaleX;
        var height = user.Height * map.ScaleY;
        if (width <= 0 || height <= 0)
            return false;

        rect = (
            origin.Left + map.OffsetX + ((user.X + offsetX) * map.ScaleX),
            origin.Top + map.OffsetY + ((user.Y + offsetY) * map.ScaleY),
            width,
            height);
        return true;
    }

    /// <summary>The shape's bounds in the user space of its nearest viewport.</summary>
    private bool TryGetSvgUserSpaceBounds(DomElement element, DomElement viewport,
        out (double X, double Y, double Width, double Height) bounds)
    {
        bounds = default;

        switch (SvgLocalName(element))
        {
            case "rect":
            case "image":
            case "foreignobject":
            {
                var width = ResolveSvgLength(element, viewport, "width", vertical: false);
                var height = ResolveSvgLength(element, viewport, "height", vertical: true);
                if (width <= 0 || height <= 0)
                    return false;

                bounds = (
                    ResolveSvgLength(element, viewport, "x", vertical: false),
                    ResolveSvgLength(element, viewport, "y", vertical: true),
                    width,
                    height);
                return true;
            }

            case "circle":
            {
                var r = ResolveSvgLength(element, viewport, "r", vertical: false);
                if (r <= 0)
                    return false;

                var cx = ResolveSvgLength(element, viewport, "cx", vertical: false);
                var cy = ResolveSvgLength(element, viewport, "cy", vertical: true);
                bounds = (cx - r, cy - r, r * 2, r * 2);
                return true;
            }

            case "ellipse":
            {
                var rx = ResolveSvgLength(element, viewport, "rx", vertical: false);
                var ry = ResolveSvgLength(element, viewport, "ry", vertical: true);
                if (rx <= 0 || ry <= 0)
                    return false;

                var cx = ResolveSvgLength(element, viewport, "cx", vertical: false);
                var cy = ResolveSvgLength(element, viewport, "cy", vertical: true);
                bounds = (cx - rx, cy - ry, rx * 2, ry * 2);
                return true;
            }

            case "line":
            {
                var x1 = ResolveSvgLength(element, viewport, "x1", vertical: false);
                var y1 = ResolveSvgLength(element, viewport, "y1", vertical: true);
                var x2 = ResolveSvgLength(element, viewport, "x2", vertical: false);
                var y2 = ResolveSvgLength(element, viewport, "y2", vertical: true);
                bounds = (Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
                return bounds.Width > 0 || bounds.Height > 0;
            }

            case "polyline":
            case "polygon":
                return TryGetSvgPointsBounds(element, out bounds);
        }

        return false;
    }

    /// <summary>
    /// One geometry attribute in user units. An SVG geometry attribute is a number or a CSS length,
    /// and a percentage resolves against the viewport — so both spellings a page may use resolve the
    /// same way, and a bare <c>"50"</c> does not fall through the CSS length parser as zero.
    /// </summary>
    private double ResolveSvgLength(DomElement element, DomElement viewport, string attributeName, bool vertical)
    {
        if (!TryGetAttribute(element, attributeName, out var raw) || string.IsNullOrWhiteSpace(raw))
            return 0;

        raw = raw.Trim();

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
            return plain;

        var basis = GetSvgViewportUserLength(viewport, vertical);
        var resolved = ParseCssLengthToPixelsWithViewport(raw, element, percentageBasis: basis);
        return double.IsFinite(resolved) ? resolved : 0;
    }

    /// <summary>The viewport's own extent in user units — the <c>viewBox</c> when there is one, and
    /// the rendered size otherwise. This is what a percentage inside it resolves against.</summary>
    private double GetSvgViewportUserLength(DomElement viewport, bool vertical)
    {
        if (TryGetSvgViewBox(viewport, out var box))
            return vertical ? box.Height : box.Width;

        var rendered = ComputeRenderedRect(viewport);
        return vertical ? rendered.Height : rendered.Width;
    }

    /// <summary>
    /// The user-space to viewport-space mapping a <c>viewBox</c> establishes.
    /// </summary>
    /// <remarks>
    /// <c>preserveAspectRatio</c> is modelled at its default, <c>xMidYMid meet</c>: one scale for
    /// both axes, chosen so the box fits, with the slack split evenly. A non-default value is not
    /// read, so a <c>slice</c> or a corner alignment maps as if it were the default — visibly wrong
    /// only when the viewBox aspect differs from the viewport's, and the same shape of gap the class
    /// documentation lists rather than a silent zero.
    /// </remarks>
    private (double ScaleX, double ScaleY, double OffsetX, double OffsetY) GetSvgViewBoxMapping(DomElement viewport)
    {
        if (!TryGetSvgViewBox(viewport, out var box) || box.Width <= 0 || box.Height <= 0)
            return (1, 1, 0, 0);

        var rendered = ComputeRenderedRect(viewport);
        if (rendered.Width <= 0 || rendered.Height <= 0)
            return (1, 1, 0, 0);

        var scale = Math.Min(rendered.Width / box.Width, rendered.Height / box.Height);
        return (
            scale,
            scale,
            ((rendered.Width - (box.Width * scale)) / 2) - (box.X * scale),
            ((rendered.Height - (box.Height * scale)) / 2) - (box.Y * scale));
    }

    /// <summary>
    /// The <c>translate()</c> accumulated from <paramref name="element"/> up to, but not including,
    /// its viewport.
    /// </summary>
    /// <remarks>
    /// The element's own transform is included, because in SVG an element's <c>transform</c> applies
    /// to the element itself. A group's rect is the union of its children's, and each child walks up
    /// through the group and so already carries the group's translate — which is why the union path
    /// must not, and does not, add it a second time.
    /// </remarks>
    private (double X, double Y) AccumulatedSvgTranslate(DomElement element, DomElement viewport)
    {
        double x = 0, y = 0;
        for (var current = element; current != null && current != viewport; current = ParentEl(current))
        {
            if (SvgForeignObjectBoxes.TryParseLoneTranslate(GetElementTransformValue(current), out var dx, out var dy))
            {
                x += dx;
                y += dy;
            }
        }

        return (x, y);
    }

}
