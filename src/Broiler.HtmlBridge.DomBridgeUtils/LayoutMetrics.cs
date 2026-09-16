using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Broiler.CSS;
using Broiler.Dom;
using Broiler.Layout;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static bool ShouldReportZeroOffsetMetrics(DomElement element) =>
        string.Equals(element.TagName, "map", StringComparison.OrdinalIgnoreCase);
}

public static partial class DomBridgeUtils
{
    // RF-BRIDGE-1b: when true, element-geometry queries (offset*/client*/
    // getBoundingClientRect/check-layout) resolve through the renderer's real layout
    // engine via the injected ILayoutView instead of the coarse LayoutMetrics
    // estimators. Enabled once increments 1-3 landed (the LayoutMetrics entry points and
    // the anchor resolver route through the provider, and the Broiler.HTML inline-box
    // geometry fix is live on CI) and the increment-4 parity gate confirmed the shared
    // path matches or improves on the estimators — see
    // SharedLayoutGeometryParityTests.Shared_Geometry_Matches_Or_Beats_Estimator_On_CheckLayout_Corpus.
    internal static bool UseSharedLayoutGeometry = true;

    // The preferred binding is the per-session factory supplied through DomBridgeSessionOptions,
    // which keeps simultaneous documents independent. This process-static factory remains only
    // as a source-compatibility fallback for composition roots that have not migrated yet. A bare
    // `new DomBridge()` with neither factory falls back to an empty view and does not pull in the
    // concrete renderer stack.
    internal static Func<ILayoutView>? LayoutViewFactory;

    internal static readonly IReadOnlyDictionary<DomElement, BoxGeometry> EmptySharedGeometry =
        new Dictionary<DomElement, BoxGeometry>();
}

public static partial class DomBridgeUtils
{
    private static bool HasExplicitBodyMargin(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               !string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase);
    }
}

public static partial class DomBridgeUtils
{
    internal static bool IsDocumentElement(DomElement element) =>
        string.Equals(element.TagName, "html", StringComparison.OrdinalIgnoreCase);

    internal static bool IsViewportBodyElement(DomElement element, DomElement documentElement) =>
        string.Equals(element.TagName, "body", StringComparison.OrdinalIgnoreCase) &&
        ReferenceEquals(ParentEl(element), documentElement);

    internal static bool IsDomDescendantOrSelf(DomElement node, DomElement potentialAncestor)
    {
        for (var current = node; current != null; current = ParentEl(current))
        {
            if (ReferenceEquals(current, potentialAncestor))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Picks the side named by <paramref name="longhandName"/> out of the matching
    /// <c>scroll-margin</c>/<c>scroll-padding</c> box shorthand in <paramref name="props"/>,
    /// or null when the shorthand is absent. Logical (<c>-block</c>/<c>-inline</c>) shorthands
    /// are not consulted — only the physical four-side form.
    /// </summary>
    internal static string? ResolveScrollInsetFromShorthand(
        Dictionary<string, string> props, string longhandName)
    {
        var lastDash = longhandName.LastIndexOf('-');
        if (lastDash <= 0)
            return null;

        var shorthandName = longhandName[..lastDash];
        var side = longhandName[(lastDash + 1)..];
        var shorthand = props.GetValueOrDefault(shorthandName);
        if (string.IsNullOrWhiteSpace(shorthand))
            return null;

        var parts = shorthand.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var (top, right, bottom, left) = CssBoxShorthand.SelectTrbl(parts);
        return side switch
        {
            "top" => top,
            "right" => right,
            "bottom" => bottom,
            "left" => left,
            _ => null,
        };
    }
}

public static partial class DomBridgeUtils
{
    internal static string NormalizeScrollIntoViewAlignment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "start" or "center" or "end" or "nearest" or "start-if-needed" or "end-if-needed"
            ? normalized
            : fallback;
    }

    internal static string ResolvePhysicalAxisAlignment(string? alignment, bool startMapsToPhysicalStart)
    {
        var normalized = NormalizeScrollIntoViewAlignment(alignment, "start");
        if (normalized is "center" or "nearest" || startMapsToPhysicalStart)
            return normalized;

        return normalized switch
        {
            "start" => "end",
            "end" => "start",
            "start-if-needed" => "end-if-needed",
            "end-if-needed" => "start-if-needed",
            _ => normalized
        };
    }

    internal static string NormalizeScrollBehavior(string? behavior)
    {
        if (string.IsNullOrWhiteSpace(behavior))
            return "auto";

        var normalized = behavior.Trim().ToLowerInvariant();
        return normalized is "instant" or "smooth" ? normalized : "auto";
    }

    internal static bool AreClose(double left, double right) => Math.Abs(left - right) < 0.0001;

    internal static string? GetOverflowAxisValue(Dictionary<string, string> props, bool vertical)
    {
        var axisValue = props.GetValueOrDefault(vertical ? "overflow-y" : "overflow-x");
        if (string.IsNullOrWhiteSpace(axisValue))
            axisValue = props.GetValueOrDefault("overflow");
        return axisValue;
    }

    /// <summary>
    /// Whether a root-propagated <c>overflow</c> value makes the viewport non-scrollable.
    /// Only <c>clip</c> does: it suppresses the scroll container entirely (CSS Overflow 3
    /// §3.3), so the scrollport has no scroll offset to set.
    /// <para><c>overflow: hidden</c> does <em>not</em> belong here. It still establishes a
    /// scroll container — it only removes the <em>user-interaction</em> affordance, while
    /// programmatic scrolling (<c>scrollTop</c>/<c>scrollLeft</c>, <c>scrollTo</c>,
    /// <c>scrollIntoView</c>) keeps working. Treating it as non-scrollable pinned every such
    /// document at offset 0, which silently broke the very common WPT reftest idiom
    /// <c>:root { overflow: hidden; /* hide scrollbars for reftest analysis */ }</c> on any
    /// test that also scrolls (issue #1439: css-scroll-snap/scroll-snap-root-001 and -002
    /// both rendered their unscrolled red FAIL block).</para>
    /// </summary>
    internal static bool DisablesRootScrolling(string? overflowValue)
    {
        if (string.IsNullOrWhiteSpace(overflowValue))
            return false;

        return overflowValue.Trim().ToLowerInvariant().Contains("clip");
    }

    internal static bool EnablesScrollingBox(string? overflowValue)
    {
        if (string.IsNullOrWhiteSpace(overflowValue))
            return false;

        var value = overflowValue.Trim().ToLowerInvariant();
        return value.Contains("hidden") || value.Contains("scroll") || value.Contains("auto") || value.Contains("clip");
    }

    internal static int CountSelectOptions(DomElement element)
    {
        int count = 0;
        foreach (var child in ChildElements(element).Where(c => !IsText(c)))
        {
            if (string.Equals(child.TagName, "option", StringComparison.OrdinalIgnoreCase))
            {
                count++;
                continue;
            }

            count += CountSelectOptions(child);
        }

        return count;
    }
}

public static partial class DomBridgeUtils
{
    internal static bool IsScrollSnapAlignmentKeyword(string token) =>
        token is "none" or "start" or "end" or "center";
}

public static partial class DomBridgeUtils
{
    private static bool IsSvgShapeElement(DomElement element)
    {
        var tag = element.TagName;
        return string.Equals(tag, "rect", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "svg:rect", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "image", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "svg:image", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "foreignobject", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "svg:foreignobject", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSvgViewportElement(DomElement element)
    {
        var tag = element.TagName;
        return string.Equals(tag, "svg", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "svg:svg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSvgElement(DomElement element) =>
        string.Equals(element.NamespaceUri, "http://www.w3.org/2000/svg", StringComparison.OrdinalIgnoreCase) ||
        IsSvgViewportElement(element) ||
        IsSvgShapeElement(element);

    internal static bool IsSvgGroupElement(DomElement element)
    {
        var tag = element.TagName;
        return string.Equals(tag, "g", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "svg:g", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSvgTextContentElement(DomElement element)
    {
        var tag = element.TagName;
        return string.Equals(tag, "text", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "svg:text", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "tspan", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "svg:tspan", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "textpath", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "svg:textpath", StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetDirectTextContent(DomElement element)
    {
        // RF-BRIDGE-1c Phase F (F3c part 2d): a node's direct text is its text-node children.
        var sb = new StringBuilder();
        foreach (var child in element.ChildNodes)
        {
            if (IsText(child) && !string.IsNullOrWhiteSpace(BridgeText(child)))
                sb.Append(BridgeText(child));
        }

        return sb.ToString();
    }

    internal static bool HasOwnSvgCoordinate(DomElement element, string attributeName) =>
        TryGetAttribute(element, attributeName, out var rawValue) &&
        !string.IsNullOrWhiteSpace(rawValue);

    internal static bool IsSvgTextPathElement(DomElement element)
    {
        var tag = element.TagName;
        return string.Equals(tag, "textpath", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "svg:textpath", StringComparison.OrdinalIgnoreCase);
    }

    internal static DomElement? FindNearestSvgViewportAncestor(DomElement element)
    {
        for (var current = ParentEl(element); current != null; current = ParentEl(current))
        {
            if (IsSvgViewportElement(current))
                return current;
        }

        return null;
    }

    [GeneratedRegex(@"[Mm]\s*(?<x>[-+]?[0-9]*\.?[0-9]+)(?:[\s,]+(?<y>[-+]?[0-9]*\.?[0-9]+))", RegexOptions.CultureInvariant)]
    internal static partial System.Text.RegularExpressions.Regex TryResolveSvgTextPathStartRegex();
}

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

public static partial class DomBridgeUtils
{
    // Maps the WPT check-layout-th.js data-* attributes this evaluator understands
    // to a short property label. Restricted to the box metrics the bridge computes
    // directly (offset / client box); scroll and bounding-client-rect checks are not
    // yet covered.
    internal static readonly (string Attribute, string Property)[] CheckLayoutAttributeMap =
    [
        ("data-offset-x", "offset-x"),
        ("data-offset-y", "offset-y"),
        ("data-expected-width", "width"),
        ("data-expected-height", "height"),
        ("data-expected-client-width", "client-width"),
        ("data-expected-client-height", "client-height"),
        ("data-total-x", "total-x"),
        ("data-total-y", "total-y"),
    ];

    /// <summary>Concise CSS-ish descriptor for reporting (tag + id/first-class + title).</summary>
    internal static string DescribeElement(Broiler.Dom.DomElement element)
    {
        var builder = new StringBuilder(element.TagName.ToLowerInvariant());
        if (!string.IsNullOrEmpty(element.Id))
        {
            builder.Append('#').Append(element.Id);
        }
        else if (!string.IsNullOrEmpty(element.ClassName))
        {
            var firstClass = element.ClassName
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (firstClass is not null)
                builder.Append('.').Append(firstClass);
        }

        if (TryGetAttribute(element, "title", out var title) && !string.IsNullOrEmpty(title))
            builder.Append("[title=").Append(title).Append(']');

        return builder.ToString();
    }
}

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
