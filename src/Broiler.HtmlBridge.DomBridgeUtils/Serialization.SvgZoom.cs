using System.Text.RegularExpressions;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static string ScaleSvgNumericMatch(Match match, double factor)
    {
        if (!double.TryParse(match.Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return match.Value;
        }

        return (number * factor).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static double GetSvgFontRelativeUnitRatio(string unit) => unit.ToLowerInvariant() switch
    {
        // Broiler's SVG length resolution currently uses the same deterministic
        // Ahem-like 0.8em approximation that the existing font-relative zoom
        // coverage already assumes for ex/cap units.
        "ex" or "rex" or "cap" or "rcap" => 0.8,
        _ => 1.0
    };

    internal static readonly string[] SvgZoomScaledUnits =
    [
        "rcap", "rch", "ric", "rex", "rlh", "rem",
        "vmin", "vmax",
        "cap",
        "em", "ex", "ch", "ic", "lh",
        "vw", "vh",
        "px", "pt", "pc", "cm", "mm", "in", "q"
    ];

    internal static readonly HashSet<string> SvgAbsoluteOrViewportUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "vw", "vh", "vmin", "vmax",
        "px", "pt", "pc", "cm", "mm", "in", "q"
    };

    internal static readonly HashSet<string> SvgFontRelativeUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "em", "ex", "cap", "ch", "ic", "lh"
    };

    internal static readonly HashSet<string> SvgRootFontRelativeUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "rem", "rex", "rcap", "rch", "ric", "rlh"
    };

    [GeneratedRegex(@"-?\d*\.?\d+(?:[eE][+-]?\d+)?")]
    internal static partial System.Text.RegularExpressions.Regex ScaleSvgPointRegex();

    [GeneratedRegex(@"-?\d*\.?\d+(?:[eE][+-]?\d+)?")]
    internal static partial System.Text.RegularExpressions.Regex ScaleSvgPathRegex();

    [GeneratedRegex(@"(?<![\w.-])(-?\d*\.?\d+)px(?:\s*/|(?=\s|$))", RegexOptions.IgnoreCase)]
    internal static partial System.Text.RegularExpressions.Regex FontShortHandRegex();
}
