using System.Globalization;
using Broiler.CSS;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static List<KeyframeEntry> ParseKeyframeEntries(CssAtRule keyframesRule)
    {
        var entries = new List<KeyframeEntry>();

        foreach (var styleRule in keyframesRule.Rules.OfType<CssStyleRule>())
        {
            var declarations = ParseDeclarations(
                CssSerializer.Serialize(styleRule.Declarations));

            foreach (var selector in styleRule.Selectors.Selectors)
            {
                var s = selector.Text.Trim().ToLowerInvariant();
                float? pos = s switch
                {
                    "from" => 0f,
                    "to" => 1f,
                    _ when s.EndsWith('%') && float.TryParse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) => pct / 100f,
                    _ => null,
                };

                if (pos.HasValue)
                    entries.Add(new KeyframeEntry(pos.Value, declarations));
            }
        }

        return [.. entries.OrderBy(e => e.Position)];
    }

    internal static Dictionary<string, string> ParseDeclarations(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var declarations = new CssParser().ParseDeclarations(text);
        foreach (var declaration in declarations.Declarations)
        {
            var value = declaration.Value.Text;
            if (declaration.Important)
                value += " !important";
            result[declaration.Name] = value;
        }
        return result;
    }

    /// <summary>
    /// Very simple CSS selector matcher — handles tag names, classes, IDs,
    /// and <c>:root</c> pseudo-class.  Sufficient for WPT body/html selectors.
    /// </summary>
    internal static bool SimpleMatchesElement(string selector, DomElement element)
    {
        var selTrimmed = selector.Trim().ToLowerInvariant();

        // Tag name selector (e.g. "body", "html")
        if (selTrimmed == element.TagName?.ToLowerInvariant())
            return true;

        // :root matches the html element
        if (selTrimmed == ":root" &&
            string.Equals(element.TagName, "html", StringComparison.OrdinalIgnoreCase))
            return true;

        // ID selector (e.g. "#myid")
        if (selTrimmed.StartsWith('#'))
        {
            var id = selTrimmed[1..];
            return string.Equals(element.Id, id, StringComparison.OrdinalIgnoreCase);
        }

        // Class selector (e.g. ".myclass")
        if (selTrimmed.StartsWith('.'))
        {
            var cls = selTrimmed[1..];
            return element.ClassName?.Split(' ').Any(c => string.Equals(c, cls, StringComparison.OrdinalIgnoreCase)) == true;
        }

        return false;
    }

    internal static bool IsLengthInterpolableProperty(string prop) => prop switch
    {
        "width" or "height" or "min-width" or "min-height" or "max-width" or "max-height" or
        "top" or "right" or "bottom" or "left" or
        "margin-left" or "margin-right" or "margin-top" or "margin-bottom" or
        "padding-left" or "padding-right" or "padding-top" or "padding-bottom" or
        "font-size" => true,
        _ => false,
    };

    internal static bool IsColorProperty(string prop) => prop switch
    {
        "background-color" or "color" or "border-color"
            or "border-top-color" or "border-right-color"
            or "border-bottom-color" or "border-left-color"
            or "outline-color" or "text-decoration-color"
            or "fill" or "stroke" => true,
        _ => false,
    };
}
