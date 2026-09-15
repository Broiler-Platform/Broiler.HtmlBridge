using System.Globalization;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>Composes a CSS <c>transform</c> function list into a single affine matrix. Functions
    /// apply left-to-right as outermost-to-innermost (CSS Transforms §1), so the list is folded in
    /// reverse. Unrecognised or 3D functions contribute identity — a conservative no-op that leaves
    /// the geometry untransformed rather than wrong.</summary>
    internal static Affine ParseTransformFunctions(string transform, double boxWidth, double boxHeight)
    {
        var functions = SplitTransformFunctions(transform);
        var matrix = Affine.Identity;
        // Fold in reverse so the first-listed function ends up outermost (applied last to a point).
        for (var i = functions.Count - 1; i >= 0; i--)
        {
            var f = ParseTransformFunction(functions[i].Name, functions[i].Args, boxWidth, boxHeight);
            matrix = matrix.Then(f);
        }
        return matrix;
    }

    private static Affine ParseTransformFunction(string name, string args, double boxWidth, double boxHeight)
    {
        var values = args.Split(',');

        double Number(int i) =>
            i < values.Length && double.TryParse(values[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

        double Length(int i, double percentBasis) =>
            i < values.Length ? ResolveLength(values[i].Trim(), percentBasis) : 0;

        // A scale factor is <number> | <percentage> (css-transforms-2 §"scale"), and the percentage
        // is simply the ratio: scale(50%) is scale(0.5). Number() could not parse "50%" at all and
        // fell back to 0, which collapsed the box instead of halving it. Absent or unparsable falls
        // back to 1, matching this parser's rule that what it cannot model contributes identity.
        double Ratio(int i)
        {
            if (i >= values.Length)
                return 1;

            var v = values[i].Trim();
            if (v.EndsWith('%'))
                return double.TryParse(v[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)
                    ? pct / 100.0
                    : 1;

            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 1;
        }

        switch (name)
        {
            case "matrix":
                return values.Length >= 6
                    ? new Affine(Number(0), Number(1), Number(2), Number(3), Number(4), Number(5))
                    : Affine.Identity;
            case "scale":
            {
                var sx = Ratio(0);
                var sy = values.Length >= 2 ? Ratio(1) : sx;
                return new Affine(sx, 0, 0, sy, 0, 0);
            }
            case "scalex":
                return new Affine(Ratio(0), 0, 0, 1, 0, 0);
            case "scaley":
                return new Affine(1, 0, 0, Ratio(0), 0, 0);
            case "translate":
                return new Affine(1, 0, 0, 1, Length(0, boxWidth), Length(1, boxHeight));
            case "translatex":
                return new Affine(1, 0, 0, 1, Length(0, boxWidth), 0);
            case "translatey":
                return new Affine(1, 0, 0, 1, 0, Length(0, boxHeight));
            case "rotate":
            case "rotatez":
            {
                var radians = ParseAngleRadians(values.Length > 0 ? values[0].Trim() : "0");
                var cos = Math.Cos(radians);
                var sin = Math.Sin(radians);
                return new Affine(cos, sin, -sin, cos, 0, 0);
            }
            case "skewx":
                return new Affine(1, 0, Math.Tan(ParseAngleRadians(values.Length > 0 ? values[0].Trim() : "0")), 1, 0, 0);
            case "skewy":
                return new Affine(1, Math.Tan(ParseAngleRadians(values.Length > 0 ? values[0].Trim() : "0")), 0, 1, 0, 0);
            default:
                // translate3d/scale3d/matrix3d/perspective/etc. — not modelled here; leave identity.
                return Affine.Identity;
        }
    }

    private static double ResolveLength(string value, double percentBasis)
    {
        if (value.EndsWith("%", StringComparison.Ordinal) &&
            double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            return percentBasis * pct / 100.0;
        return ParsePixels(value) ?? 0;
    }

    private static double? ParsePixels(string value)
    {
        var v = value.Trim();
        if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            v = v[..^2];
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static double ParseAngleRadians(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v.EndsWith("deg", StringComparison.Ordinal))
            return double.TryParse(v[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var deg) ? deg * Math.PI / 180.0 : 0;
        if (v.EndsWith("grad", StringComparison.Ordinal))
            return double.TryParse(v[..^4], NumberStyles.Float, CultureInfo.InvariantCulture, out var g) ? g * Math.PI / 200.0 : 0;
        if (v.EndsWith("turn", StringComparison.Ordinal))
            return double.TryParse(v[..^4], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t * 2 * Math.PI : 0;
        if (v.EndsWith("rad", StringComparison.Ordinal))
            return double.TryParse(v[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare) ? bare * Math.PI / 180.0 : 0;
    }

    /// <summary>Splits a transform value into its <c>name(args)</c> functions, in source order.</summary>
    private static System.Collections.Generic.List<(string Name, string Args)> SplitTransformFunctions(string transform)
    {
        var result = new System.Collections.Generic.List<(string, string)>();
        var position = 0;
        while (position < transform.Length)
        {
            var open = transform.IndexOf('(', position);
            if (open < 0)
                break;
            var close = transform.IndexOf(')', open + 1);
            if (close < 0)
                break;

            var name = transform[position..open].Trim().ToLowerInvariant();
            var args = transform[(open + 1)..close];
            if (name.Length > 0)
                result.Add((name, args));
            position = close + 1;
        }
        return result;
    }
}
