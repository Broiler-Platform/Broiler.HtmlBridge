using System.Text;
using System.Text.RegularExpressions;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Logging;
using Broiler.Dom;
using Broiler.CSS;
using System.Globalization;

namespace Broiler.HtmlBridge;

/// <summary>
/// Sibling partial peeled out of <c>LayoutMetrics.cs</c> (Phase 3 ratchet, 2026-07-17) to keep it
/// under the 750-line guard: SVG geometry/text-metric resolution, element zoom / transform-scale
/// resolution, and the border-box size helpers. Pure partial-class relocation — no signature,
/// accessibility, or logic change.
/// </summary>
public sealed partial class DomBridge
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

    private static bool IsSvgGroupElement(DomElement element)
    {
        var tag = element.TagName;
        return string.Equals(tag, "g", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "svg:g", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSvgTextContentElement(DomElement element)
    {
        var tag = element.TagName;
        return string.Equals(tag, "text", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "svg:text", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "tspan", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "svg:tspan", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "textpath", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "svg:textpath", StringComparison.OrdinalIgnoreCase);
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

    private static string GetDirectTextContent(DomElement element)
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

    private static bool HasOwnSvgCoordinate(DomElement element, string attributeName) =>
        TryGetAttribute(element, attributeName, out var rawValue) &&
        !string.IsNullOrWhiteSpace(rawValue);

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

    private static bool IsSvgTextPathElement(DomElement element)
    {
        var tag = element.TagName;
        return string.Equals(tag, "textpath", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tag, "svg:textpath", StringComparison.OrdinalIgnoreCase);
    }

    private static DomElement? FindNearestSvgViewportAncestor(DomElement element)
    {
        for (var current = ParentEl(element); current != null; current = ParentEl(current))
        {
            if (IsSvgViewportElement(current))
                return current;
        }

        return null;
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


    // CreateSvgLengthValue moved to the SvgElementBinding feature module (Phase 3 P3.50) — its only
    // consumer (the SVGAnimatedLength stub) moved there too.

    [GeneratedRegex(@"[Mm]\s*(?<x>[-+]?[0-9]*\.?[0-9]+)(?:[\s,]+(?<y>[-+]?[0-9]*\.?[0-9]+))", RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex TryResolveSvgTextPathStartRegex();
}
