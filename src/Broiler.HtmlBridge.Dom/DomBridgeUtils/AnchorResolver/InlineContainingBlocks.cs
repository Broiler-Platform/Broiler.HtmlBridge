using System.Globalization;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Determines if an element is an inline element based on its tag name
    /// and display property.
    /// </summary>
    internal static bool IsInlineElement(string tagName, string? display)
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
    /// Resolves the computed line-height from CSS properties.
    /// Handles unitless values (multipliers of font-size), pixel values,
    /// and the "normal" keyword (defaults to 1.2 × font-size).
    /// </summary>
    internal static double ResolveLineHeight(Dictionary<string, string> props, double fontSize)
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
}
