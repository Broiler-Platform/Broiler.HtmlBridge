using System.Globalization;
using Broiler.CSS;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    internal static double? TryParsePx(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value!.Trim();
        if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            v = v[..^2];
        // Don't parse pure numbers without px suffix if they contain '%'
        if (v.Contains('%')) return null;
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            return result;
        return null;
    }
    /// <summary>
    /// Tries to parse a CSS percentage value (e.g. "50%") and returns
    /// the numeric value (e.g. 50.0).
    /// </summary>
    internal static double? TryParsePercent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value!.Trim();
        if (!v.EndsWith('%')) return null;
        v = v[..^1];
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            return result;
        return null;
    }

    /// <summary>
    /// Resolves a CSS value that may be a percentage or a pixel length.
    /// Percentages are resolved against <paramref name="reference"/>.
    /// Returns 0 for values that cannot be parsed.
    /// </summary>
    internal static double ResolvePctOrPx(string value, double reference)
    {
        var pct = TryParsePercent(value);
        if (pct.HasValue)
            return reference * pct.Value / 100.0;
        return TryParsePx(value) ?? 0;
    }

    /// <summary>
    /// Returns true if the value contains a CSS percentage token.
    /// </summary>
    internal static bool HasPercent(string? value) => value != null && value.Contains('%');

}
