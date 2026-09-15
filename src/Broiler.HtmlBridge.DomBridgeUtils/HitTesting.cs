using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static bool IsTableCellElement(DomElement element)
    {
        var tag = element.TagName;
        return string.Equals(tag, "td", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "th", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAreaElement(DomElement element) =>
        string.Equals(element.TagName, "area", StringComparison.OrdinalIgnoreCase);

    internal static bool IsOutsideListItemMarkerCandidate(DomElement element,
        IReadOnlyDictionary<string, string> props)
    {
        var isListItem = string.Equals(element.TagName, "li", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(props.GetValueOrDefault("display"), "list-item", StringComparison.OrdinalIgnoreCase);
        if (!isListItem)
            return false;

        if (string.Equals(props.GetValueOrDefault("list-style-position"), "inside", StringComparison.OrdinalIgnoreCase))
            return false;

        var listStyleType = props.GetValueOrDefault("list-style-type");
        var listStyleImage = props.GetValueOrDefault("list-style-image");
        return !string.Equals(listStyleType, "none", StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(listStyleImage) &&
                !string.Equals(listStyleImage, "none", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsTableStructuralHitTestOnlyElement(DomElement element)
    {
        var tag = element.TagName;
        return string.Equals(tag, "tr", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "thead", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "tbody", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "tfoot", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "colgroup", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "col", StringComparison.OrdinalIgnoreCase);
    }

    internal static List<double> ParseAreaCoords(string? rawCoords)
    {
        if (string.IsNullOrWhiteSpace(rawCoords))
            return [];

        return [.. rawCoords
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ParsePositiveOrNegativeDouble)];
    }

    internal static bool IsPointInsideRectArea(IReadOnlyList<double> coords, double x, double y)
    {
        var left = Math.Min(coords[0], coords[2]);
        var right = Math.Max(coords[0], coords[2]);
        var top = Math.Min(coords[1], coords[3]);
        var bottom = Math.Max(coords[1], coords[3]);
        return x >= left && x <= right && y >= top && y <= bottom;
    }

    internal static bool IsPointInsideCircleArea(IReadOnlyList<double> coords, double x, double y)
    {
        var dx = x - coords[0];
        var dy = y - coords[1];
        return dx * dx + dy * dy <= coords[2] * coords[2];
    }

    internal static bool IsPointInsidePolygonArea(IReadOnlyList<double> coords, double x, double y)
    {
        var inside = false;
        var pointCount = coords.Count / 2;
        for (int i = 0, j = pointCount - 1; i < pointCount; j = i++)
        {
            var xi = coords[i * 2];
            var yi = coords[i * 2 + 1];
            var xj = coords[j * 2];
            var yj = coords[j * 2 + 1];

            var intersects = ((yi > y) != (yj > y)) &&
                             (x < (xj - xi) * (y - yi) / ((yj - yi) == 0 ? double.Epsilon : (yj - yi)) + xi);
            if (intersects)
                inside = !inside;
        }

        return inside;
    }

    internal static double ParsePositiveDouble(string? rawValue)
    {
        var parsed = ParsePositiveOrNegativeDouble(rawValue);
        return parsed > 0 ? parsed : 0;
    }

    private static double ParsePositiveOrNegativeDouble(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return 0;

        return double.TryParse(
            rawValue.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0;
    }
}
