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
/// under the 750-line guard: CSS <c>&lt;length&gt;</c> / <c>calc()</c>-style math evaluation against a
/// viewport/containing-block basis, and the font-size / line-height reference resolution the length
/// evaluation depends on. Pure partial-class relocation — no signature, accessibility, or logic change.
/// </summary>
public sealed partial class DomBridge
{
    private double ParseCssLengthToPixelsWithViewport(string? value, DomElement? referenceElement = null,
        bool forLineHeight = false, double? percentageBasis = null, bool forFontSize = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        return TryEvaluateCssLengthWithViewport(value, referenceElement, forLineHeight, percentageBasis, out var px, forFontSize)
            ? px
            : 0;
    }

    private bool TryEvaluateCssLengthWithViewport(
        string value,
        DomElement? referenceElement,
        bool forLineHeight,
        double? percentageBasis,
        out double result,
        bool forFontSize = false)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        while (normalized.Length >= 2 &&
               normalized[0] == '(' &&
               normalized[^1] == ')' &&
               CssLengthParser.HasBalancedParens(normalized[1..^1]))
        {
            normalized = normalized[1..^1].Trim();
        }

        if (TryEvaluateMathLengthFunction(normalized, referenceElement, forLineHeight, percentageBasis, out result, forFontSize))
            return true;

        var additiveOperatorIndex = CssLengthParser.FindTopLevelAdditiveOperator(normalized);
        if (additiveOperatorIndex > 0)
        {
            if (!TryEvaluateCssLengthWithViewport(
                    normalized[..additiveOperatorIndex],
                    referenceElement,
                    forLineHeight,
                    percentageBasis,
                    out var left,
                    forFontSize) ||
                !TryEvaluateCssLengthWithViewport(
                    normalized[(additiveOperatorIndex + 1)..],
                    referenceElement,
                    forLineHeight,
                    percentageBasis,
                    out var right,
                    forFontSize))
            {
                return false;
            }

            result = normalized[additiveOperatorIndex] == '+'
                ? left + right
                : left - right;
            return true;
        }

        var lower = normalized.ToLowerInvariant();
        if (percentageBasis.HasValue && lower.EndsWith('%') &&
            double.TryParse(lower[..^1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var percent))
        {
            result = percentageBasis.Value * (percent / 100.0);
            return true;
        }

        if (referenceElement != null &&
            lower.EndsWith("rem") &&
            double.TryParse(lower[..^3], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var rem))
        {
            result = rem * ResolveFontSizeForLength(referenceElement, rootRelative: true);
            return true;
        }

        if (referenceElement != null &&
            lower.EndsWith("em") &&
            double.TryParse(lower[..^2], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var em))
        {
            // For the font-size property itself, em resolves against the parent's
            // font-size (not the element's own), otherwise resolving the element's
            // font-size would recurse into itself.
            double emBasis;
            if (forFontSize)
            {
                var parent = ParentEl(referenceElement);
                emBasis = parent != null ? ResolveFontSizeForElement(parent) : 16;
            }
            else
            {
                emBasis = ResolveFontSizeForLength(referenceElement, rootRelative: false);
            }

            result = em * emBasis;
            return true;
        }

        if (referenceElement != null &&
            lower.EndsWith("rlh") &&
            double.TryParse(lower[..^3], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var rlh))
        {
            result = rlh * ResolveLineHeightForLength(referenceElement, rootRelative: true);
            return true;
        }

        if (referenceElement != null &&
            lower.EndsWith("lh") &&
            double.TryParse(lower[..^2], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var lh))
        {
            result = lh * ResolveLineHeightForLength(referenceElement, rootRelative: false, forLineHeight);
            return true;
        }

        var px = ParseCssLengthToPixels(normalized, _viewportWidth, _viewportHeight);
        if (double.IsNaN(px))
            return false;

        result = px;
        return true;
    }

    private bool TryEvaluateMathLengthFunction(string value, DomElement? referenceElement,
        bool forLineHeight, double? percentageBasis, out double result,
        bool forFontSize = false)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value) || value[^1] != ')')
            return false;

        static bool StartsWithFunction(string candidate, string functionName)
            => candidate.StartsWith(functionName + "(", StringComparison.OrdinalIgnoreCase);

        if (StartsWithFunction(value, "calc"))
        {
            var content = value[5..^1];
            return CssLengthParser.HasBalancedParens(content) &&
                   TryEvaluateCssLengthWithViewport(content, referenceElement, forLineHeight, percentageBasis, out result, forFontSize);
        }

        if (!StartsWithFunction(value, "min") && !StartsWithFunction(value, "max"))
            return false;

        var isMax = StartsWithFunction(value, "max");
        var contentValue = value[4..^1];
        if (!CssLengthParser.HasBalancedParens(contentValue))
            return false;

        var parts = CssLengthParser.SplitTopLevelArguments(contentValue);
        if (parts.Count == 0)
            return false;

        double? candidate = null;
        foreach (var part in parts)
        {
            if (!TryEvaluateCssLengthWithViewport(part, referenceElement, forLineHeight, percentageBasis, out var parsed, forFontSize))
                return false;

            candidate = candidate.HasValue
                ? (isMax ? Math.Max(candidate.Value, parsed) : Math.Min(candidate.Value, parsed))
                : parsed;
        }

        if (!candidate.HasValue)
            return false;

        result = candidate.Value;
        return true;
    }


    private double ResolveContainingBlockReferenceLength(DomElement element, bool vertical)
    {
        if (ParentEl(element) == null ||
            string.Equals(element.TagName, "html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(element.TagName, "body", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ParentEl(element).TagName, "html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ParentEl(element).TagName, "body", StringComparison.OrdinalIgnoreCase))
        {
            return GetViewportReferenceLength(element, vertical);
        }

        var (Left, Top, Width, Height) = ComputeUnzoomedLayoutRect(ParentEl(element));
        var reference = vertical ? Height : Width;
        return reference > 0 ? reference : GetViewportReferenceLength(element, vertical);
    }

    private double GetViewportReferenceLength(DomElement? element, bool vertical)
    {
        if (element != null)
        {
            var documentElement = GetOwningDocumentElement(element);
            var frameElement = GetOuterFrameElement(documentElement);
            if (frameElement != null)
            {
                var frameProps = GetComputedProps(frameElement);
                var frameLength = ParseCssLengthToPixelsWithViewport(
                    frameProps.GetValueOrDefault(vertical ? "height" : "width"),
                    frameElement);
                if (frameLength > 0)
                    return frameLength;

                if (TryGetAttribute(frameElement, vertical ? "height" : "width", out var frameAttribute) &&
                    double.TryParse(frameAttribute, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out frameLength) &&
                    frameLength > 0)
                {
                    return frameLength;
                }
            }
        }

        return vertical ? _viewportHeight : _viewportWidth;
    }

    private static bool HasExplicitBodyMargin(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               !string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase);
    }

    private double ResolveLineHeightForLength(DomElement element, bool rootRelative, bool forLineHeight = false)
    {
        var target = rootRelative ? GetRootElement(element) : (forLineHeight ? ParentEl(element) ?? element : element);
        return ResolveLineHeightForElement(target);
    }

    private double ResolveFontSizeForLength(DomElement element, bool rootRelative)
    {
        var target = rootRelative ? GetRootElement(element) : element;
        return ResolveFontSizeForElement(target);
    }

    private DomElement GetRootElement(DomElement element)
    {
        DomElement? htmlElement = null;
        var current = element;
        while (ParentEl(current) != null)
        {
            current = ParentEl(current);
            if (string.Equals(current.TagName, "html", StringComparison.OrdinalIgnoreCase))
                htmlElement = current;
        }

        return htmlElement ?? current;
    }

    private double ResolveLineHeightForElement(DomElement element)
    {
        var props = GetComputedProps(element);
        var fontSize = ResolveFontSizeForElement(element);
        var lineHeight = props.GetValueOrDefault("line-height");
        if (string.IsNullOrWhiteSpace(lineHeight) ||
            string.Equals(lineHeight, "normal", StringComparison.OrdinalIgnoreCase))
        {
            return fontSize * 1.2;
        }

        var normalized = lineHeight.Trim().ToLowerInvariant();
        if (double.TryParse(normalized, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var multiplier))
        {
            return fontSize * multiplier;
        }

        return ParseCssLengthToPixelsWithViewport(lineHeight, element, forLineHeight: true);
    }

    private double ResolveFontSizeForElement(DomElement element)
    {
        var props = GetComputedProps(element);
        var fontSize = ParseCssLengthToPixelsWithViewport(props.GetValueOrDefault("font-size"), element, forFontSize: true);
        if (fontSize > 0)
            return fontSize;

        for (var current = element; current != null; current = ParentEl(current))
        {
            if (!TryGetAttribute(current, "font-size", out var attributeValue) ||
                string.IsNullOrWhiteSpace(attributeValue))
            {
                continue;
            }

            var attributeFontSize = ParseCssLengthToPixelsWithViewport(attributeValue, current, forFontSize: true);
            if (attributeFontSize > 0)
                return attributeFontSize;
        }

        return 16;
    }

}
