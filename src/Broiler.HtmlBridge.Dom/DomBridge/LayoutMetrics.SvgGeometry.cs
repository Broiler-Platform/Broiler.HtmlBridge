using System.Globalization;
using Broiler.Dom;
using Broiler.Layout.Engine;

namespace Broiler.HtmlBridge;

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

    /// <summary>Whether this element's client rect comes from SVG geometry attributes rather than
    /// from the CSS box tree.</summary>
    private static bool IsSvgGeometryElement(DomElement element) =>
        SvgLocalName(element) is "rect" or "image" or "foreignobject"
            or "circle" or "ellipse" or "line" or "polyline" or "polygon";

    /// <summary>The tag name without an <c>svg:</c> prefix, lowercased.</summary>
    private static string SvgLocalName(DomElement element)
    {
        var tag = element.TagName ?? string.Empty;
        var colon = tag.LastIndexOf(':');
        if (colon >= 0)
            tag = tag[(colon + 1)..];

        return tag.ToLowerInvariant();
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

    /// <summary>The bounding box of a <c>points</c> list.</summary>
    private static bool TryGetSvgPointsBounds(DomElement element,
        out (double X, double Y, double Width, double Height) bounds)
    {
        bounds = default;
        if (!TryGetAttribute(element, "points", out var raw) || string.IsNullOrWhiteSpace(raw))
            return false;

        var numbers = raw
            .Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(static token =>
                double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : double.NaN)
            .ToArray();

        double minX = 0, minY = 0, maxX = 0, maxY = 0;
        var any = false;
        for (var i = 0; i + 1 < numbers.Length; i += 2)
        {
            var (x, y) = (numbers[i], numbers[i + 1]);
            if (!double.IsFinite(x) || !double.IsFinite(y))
                continue;

            if (!any)
            {
                any = true;
                (minX, minY, maxX, maxY) = (x, y, x, y);
                continue;
            }

            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        if (!any)
            return false;

        bounds = (minX, minY, maxX - minX, maxY - minY);
        return bounds.Width > 0 || bounds.Height > 0;
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

    private static bool TryGetSvgViewBox(DomElement viewport,
        out (double X, double Y, double Width, double Height) box)
    {
        box = default;
        if (!TryGetAttribute(viewport, "viewBox", out var raw) || string.IsNullOrWhiteSpace(raw))
            return false;

        var parts = raw
            .Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(static token =>
                double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : double.NaN)
            .ToArray();

        if (parts.Length < 4 || parts.Any(static value => !double.IsFinite(value)))
            return false;

        box = (parts[0], parts[1], parts[2], parts[3]);
        return true;
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
