using System.Globalization;
using System.Text;
using Broiler.CSS;
using Broiler.JSeal;

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

internal readonly record struct AnimationTiming(
    double DurationMs, double DelayMs, string Easing, string Fill,
    double Iterations, double IterationStart);

internal sealed record KeyframeEntry(float Position, Dictionary<string, string> Properties);

public static partial class DomBridgeUtils
{
    /// <summary>Web-Animations keyframe keys (camelCase) mapped to their CSS property names, probed by
    /// name on each keyframe object or on the property-indexed keyframes object. It is a fixed list rather
    /// than the interpolator's own: no property off it is read (the array form also reads each keyframe's
    /// <c>offset</c>), and a listed property the interpolator has no blending rule for steps
    /// discretely.</summary>
    internal static readonly (string Keyframe, string Css)[] AnimatableProperties =
    [
        ("transform", "transform"),
        ("opacity", "opacity"),
        ("color", "color"),
        ("background", "background"),
        ("backgroundColor", "background-color"),
        ("borderColor", "border-color"),
        ("outlineColor", "outline-color"),
        ("width", "width"),
        ("height", "height"),
        ("left", "left"),
        ("top", "top"),
        ("right", "right"),
        ("bottom", "bottom"),
        ("marginTop", "margin-top"),
        ("marginRight", "margin-right"),
        ("marginBottom", "margin-bottom"),
        ("marginLeft", "margin-left"),
        ("paddingTop", "padding-top"),
        ("paddingRight", "padding-right"),
        ("paddingBottom", "padding-bottom"),
        ("paddingLeft", "padding-left"),
        ("borderRadius", "border-radius"),
        ("fontSize", "font-size"),
        ("lineHeight", "line-height"),
        ("letterSpacing", "letter-spacing"),
        ("filter", "filter"),
    ];

    /// <summary>
    /// The <c>pseudoElement</c> option, normalised to the double-colon form, or
    /// <see langword="null"/> when the animation targets the element itself. A syntactically
    /// invalid value is treated as absent rather than throwing — the whole call is best-effort.
    /// </summary>
    internal static string? ParseAnimationPseudoElement(IJsRealm realm, JsValue optionsValue)
    {
        if (!optionsValue.IsObject)
            return null;

        var value = realm.GetProperty(optionsValue, "pseudoElement");
        if (value.IsNullish || value.IsMissing)
            return null;

        var text = realm.ToJsString(value).Trim().ToLowerInvariant();
        if (text.Length == 0)
            return null;

        // Both `:before` and `::before` name the same pseudo-element; canonicalise on `::`.
        if (!text.StartsWith("::", StringComparison.Ordinal))
        {
            if (!text.StartsWith(':'))
                return null;
            text = ":" + text;
        }

        var name = text[2..];
        if (name.Length == 0 || !name.All(c => char.IsAsciiLetter(c) || c == '-'))
            return null;

        return text;
    }

    internal static AnimationTiming ParseAnimationTiming(IJsRealm realm, JsValue optionsValue)
    {
        double duration = 0, delay = 0, iterations = 1, iterationStart = 0;
        var easing = "linear";
        var fill = "none";

        if (optionsValue.IsNumber)
        {
            duration = optionsValue.AsNumber;
        }
        else if (optionsValue.IsObject)
        {
            if (realm.GetProperty(optionsValue, "duration") is { IsNumber: true } d)
                duration = d.AsNumber;
            if (realm.GetProperty(optionsValue, "delay") is { IsNumber: true } dl)
                delay = dl.AsNumber;
            if (realm.GetProperty(optionsValue, "iterations") is { IsNumber: true } it)
                iterations = it.AsNumber;
            if (realm.GetProperty(optionsValue, "iterationStart") is { IsNumber: true } iterStart)
                iterationStart = iterStart.AsNumber;
            if (realm.GetProperty(optionsValue, "easing") is { } e && !e.IsNullish)
                easing = realm.ToJsString(e);
            if (realm.GetProperty(optionsValue, "fill") is { } f && !f.IsNullish)
                fill = realm.ToJsString(f);
        }

        return new AnimationTiming(duration, delay, easing, fill, iterations, iterationStart);
    }

    /// <summary>
    /// Iteration progress at the snapshot. The animation has just started (current time 0), so
    /// active time is <c>-delay</c>; a negative delay lands it mid-animation. Returns false when
    /// the animation has no effect at the snapshot (before/after its active phase and not filled).
    /// </summary>
    internal static bool TryComputeSnapshotProgress(AnimationTiming timing, out float progress)
    {
        progress = 0f;
        var iterations = timing.Iterations <= 0 ? 1 : timing.Iterations;
        var activeDuration = timing.DurationMs * iterations;
        var activeTime = 0.0 - timing.DelayMs;

        var fillsBackwards = timing.Fill is "backwards" or "both";
        var fillsForwards = timing.Fill is "forwards" or "both";

        if (activeTime < 0)
        {
            if (!fillsBackwards)
                return false;
            activeTime = 0;
        }
        else if (activeTime >= activeDuration)
        {
            if (!fillsForwards)
                return false;
            activeTime = activeDuration;
        }

        var overallProgress = (timing.DurationMs > 0 ? activeTime / timing.DurationMs : 0) + timing.IterationStart;
        // Iteration progress in [0,1]; a whole-number boundary at the end of the active phase
        // resolves to 1 (the animation's final value), not 0.
        var iterationProgress = overallProgress - Math.Floor(overallProgress);
        if (iterationProgress == 0 && overallProgress > 0)
            iterationProgress = 1;

        progress = (float)Math.Clamp(iterationProgress, 0.0, 1.0);
        return true;
    }

    // ------------------------------------------------------------------
    //  Transform interpolation (component-wise between matching lists)
    // ------------------------------------------------------------------

    internal static bool TryInterpolateTransform(string fromValue, string toValue, float progress, out string result)
    {
        result = string.Empty;
        var from = ParseTransformFunctions(fromValue);
        var to = ParseTransformFunctions(toValue);
        if (from is null || to is null)
            return false;

        if (from.Count == 0 && to.Count == 0)
        {
            result = "none";
            return true;
        }

        // `none` interpolates as the identity of the other side's function list.
        if (from.Count == 0)
            from = to.Select(IdentityTransform).ToList();
        else if (to.Count == 0)
            to = from.Select(IdentityTransform).ToList();

        if (from.Count != to.Count)
            return false;

        var builder = new StringBuilder();
        for (var i = 0; i < from.Count; i++)
        {
            if (!string.Equals(from[i].Name, to[i].Name, StringComparison.OrdinalIgnoreCase) ||
                from[i].Args.Count != to[i].Args.Count)
                return false;

            var args = new List<string>(from[i].Args.Count);
            for (var j = 0; j < from[i].Args.Count; j++)
            {
                if (!TryInterpolateNumericToken(from[i].Args[j], to[i].Args[j], progress, out var arg))
                    return false;
                args.Add(arg);
            }

            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(from[i].Name).Append('(').Append(string.Join(", ", args)).Append(')');
        }

        result = builder.ToString();
        return true;
    }

    private static List<TransformFunction>? ParseTransformFunctions(string value)
    {
        value = value?.Trim() ?? string.Empty;
        var functions = new List<TransformFunction>();
        if (value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase))
            return functions;

        var i = 0;
        var n = value.Length;
        while (i < n)
        {
            while (i < n && (char.IsWhiteSpace(value[i]) || value[i] == ','))
                i++;
            if (i >= n)
                break;

            var nameStart = i;
            while (i < n && value[i] != '(')
                i++;
            if (i >= n)
                return null;
            var name = value[nameStart..i].Trim();
            i++; // '('

            var argStart = i;
            while (i < n && value[i] != ')')
                i++;
            if (i >= n || name.Length == 0)
                return null;
            var args = value[argStart..i]
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            i++; // ')'

            functions.Add(new TransformFunction(name, args));
        }

        return functions;
    }

    private static TransformFunction IdentityTransform(TransformFunction function)
    {
        var lower = function.Name.ToLowerInvariant();
        string identity =
            lower.StartsWith("scale", StringComparison.Ordinal) ? "1"
            : lower.StartsWith("rotate", StringComparison.Ordinal) || lower.StartsWith("skew", StringComparison.Ordinal) ? "0deg"
            : "0";
        return new TransformFunction(function.Name, function.Args.Select(_ => identity).ToList());
    }

    private static bool TryInterpolateNumericToken(string from, string to, float progress, out string result)
    {
        result = string.Empty;
        if (!TrySplitNumberUnit(from, out var fromNumber, out var fromUnit) ||
            !TrySplitNumberUnit(to, out var toNumber, out var toUnit))
            return false;

        string unit;
        if (string.Equals(fromUnit, toUnit, StringComparison.OrdinalIgnoreCase))
            unit = fromUnit;
        else if (fromNumber == 0 && fromUnit.Length == 0)
            unit = toUnit;
        else if (toNumber == 0 && toUnit.Length == 0)
            unit = fromUnit;
        else
            return false; // incompatible units (e.g. px vs %); no conversion here

        var value = fromNumber + (toNumber - fromNumber) * progress;
        result = value.ToString("0.#####", CultureInfo.InvariantCulture) + unit;
        return true;
    }

    private static bool TrySplitNumberUnit(string token, out double number, out string unit)
    {
        number = 0;
        unit = string.Empty;
        token = token.Trim();
        if (token.Length == 0)
            return false;

        var i = 0;
        if (token[i] is '+' or '-')
            i++;
        var digitsStart = i;
        while (i < token.Length && (char.IsDigit(token[i]) || token[i] == '.'))
            i++;
        if (i == digitsStart)
            return false;

        if (!double.TryParse(token[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return false;

        unit = token[i..].Trim();
        return true;
    }
}

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
            // Balanced, so a nested function argument does not end the function early:
            // `translate(calc(1px + 2px), 0)` closes at its own ')', not calc's. The hand-scan this
            // replaces took the first ')' and truncated the argument list there.
            //
            // No match is -1 (Broiler.CSS #54; it used to be text.Length - 1, which for a value
            // ending in '(' is the opening index itself, so the guard here had to compare the two
            // indices and then check the landing character). An unterminated `translateY(` would
            // otherwise take the rest of the string as its argument.
            var close = CssSyntax.FindMatching(transform, open, '(', ')');
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

internal sealed record TransformFunction(string Name, List<string> Args);

/// <summary>A CSS 2D affine transform <c>matrix(a,b,c,d,e,f)</c> mapping a point
/// <c>(x,y) → (a·x + c·y + e, b·x + d·y + f)</c>.</summary>
internal readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    public bool IsIdentity =>
        A == 1 && B == 0 && C == 0 && D == 1 && E == 0 && F == 0;

    /// <summary>The matrix that applies <c>this</c> first and then <paramref name="next"/>.</summary>
    public Affine Then(Affine next) => new(
        next.A * A + next.C * B,
        next.B * A + next.D * B,
        next.A * C + next.C * D,
        next.B * C + next.D * D,
        next.A * E + next.C * F + next.E,
        next.B * E + next.D * F + next.F);
}
