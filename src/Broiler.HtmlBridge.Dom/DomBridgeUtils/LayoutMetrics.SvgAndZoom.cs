using System.Text;
using System.Text.RegularExpressions;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

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
