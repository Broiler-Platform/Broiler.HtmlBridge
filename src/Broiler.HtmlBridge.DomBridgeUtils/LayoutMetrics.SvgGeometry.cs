using System.Globalization;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>Whether this element's client rect comes from SVG geometry attributes rather than
    /// from the CSS box tree.</summary>
    internal static bool IsSvgGeometryElement(DomElement element) =>
        SvgLocalName(element) is "rect" or "image" or "foreignobject"
            or "circle" or "ellipse" or "line" or "polyline" or "polygon";

    /// <summary>The tag name without an <c>svg:</c> prefix, lowercased.</summary>
    internal static string SvgLocalName(DomElement element)
    {
        var tag = element.TagName ?? string.Empty;
        var colon = tag.LastIndexOf(':');
        if (colon >= 0)
            tag = tag[(colon + 1)..];

        return tag.ToLowerInvariant();
    }

    /// <summary>The bounding box of a <c>points</c> list.</summary>
    internal static bool TryGetSvgPointsBounds(DomElement element,
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

    internal static bool TryGetSvgViewBox(DomElement viewport,
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
}
