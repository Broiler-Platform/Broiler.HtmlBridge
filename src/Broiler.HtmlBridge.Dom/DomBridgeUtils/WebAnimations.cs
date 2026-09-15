using System.Globalization;
using System.Text;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

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

    /// <summary>
    /// The property-indexed keyframe form: each animatable property maps to a list of values (or a
    /// single value), distributed evenly over the effect. Each property is turned into its own
    /// keyframes, which is exactly how <c>ResolveKeyframeProperties</c> reads them — it brackets
    /// each property against only the keyframes that define it, so properties with different list
    /// lengths need no common offset grid.
    /// </summary>
    internal static List<DomBridge.KeyframeEntry> ParsePropertyIndexedKeyframes(IJsRealm realm, JsValue keyframes)
    {
        var byPosition = new SortedDictionary<float, Dictionary<string, string>>();

        foreach (var (keyframeKey, cssName) in AnimatableProperties)
        {
            var value = realm.GetProperty(keyframes, keyframeKey);
            if (value.IsMissing || value.IsNullish)
                continue;

            var values = value.IsArray
                ? Dom.Features.WorkerTransfer.ArrayElements(realm, value).ToList()
                : [value];

            for (var i = 0; i < values.Count; i++)
            {
                if (values[i].IsMissing || values[i].IsNullish)
                    continue;
                var text = realm.ToJsString(values[i]);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                // A list of one is a single keyframe at offset 1 (the spec's implicit-from case);
                // otherwise the values spread evenly from 0 to 1.
                var position = values.Count <= 1 ? 1f : (float)i / (values.Count - 1);
                if (!byPosition.TryGetValue(position, out var properties))
                {
                    properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    byPosition[position] = properties;
                }
                properties[cssName] = text;
            }
        }

        return byPosition.Select(entry => new DomBridge.KeyframeEntry(entry.Key, entry.Value)).ToList();
    }

    internal static DomBridge.AnimationTiming ParseAnimationTiming(IJsRealm realm, JsValue optionsValue)
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

        return new DomBridge.AnimationTiming(duration, delay, easing, fill, iterations, iterationStart);
    }

    /// <summary>
    /// Iteration progress at the snapshot. The animation has just started (current time 0), so
    /// active time is <c>-delay</c>; a negative delay lands it mid-animation. Returns false when
    /// the animation has no effect at the snapshot (before/after its active phase and not filled).
    /// </summary>
    internal static bool TryComputeSnapshotProgress(DomBridge.AnimationTiming timing, out float progress)
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

    private static List<DomBridge.TransformFunction>? ParseTransformFunctions(string value)
    {
        value = value?.Trim() ?? string.Empty;
        var functions = new List<DomBridge.TransformFunction>();
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

            functions.Add(new DomBridge.TransformFunction(name, args));
        }

        return functions;
    }

    private static DomBridge.TransformFunction IdentityTransform(DomBridge.TransformFunction function)
    {
        var lower = function.Name.ToLowerInvariant();
        string identity =
            lower.StartsWith("scale", StringComparison.Ordinal) ? "1"
            : lower.StartsWith("rotate", StringComparison.Ordinal) || lower.StartsWith("skew", StringComparison.Ordinal) ? "0deg"
            : "0";
        return new DomBridge.TransformFunction(function.Name, function.Args.Select(_ => identity).ToList());
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
