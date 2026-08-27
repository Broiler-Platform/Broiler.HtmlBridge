using System.Globalization;
using System.Text.RegularExpressions;
using Broiler.CSS;
using Broiler.Dom;
using System.Text;

namespace Broiler.HtmlBridge;

/// <summary>
/// Resolves CSS animation snapshots — for elements with <c>animation</c> and a
/// negative <c>animation-delay</c>, computes the animated property values at the
/// implied time offset and writes them directly into the element's inline style,
/// replacing the <c>animation</c>/<c>animation-delay</c> properties.  This allows
/// the static Broiler renderer to produce the correct visual output for tests that
/// rely on CSS animations (e.g. WPT <c>animation-delay-008.html</c>).
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Walks the DOM tree and resolves any CSS animations that have a negative
    /// <c>animation-delay</c> to their computed property values at <c>t=0</c>.
    /// Must be called after script execution and before serialization.
    /// </summary>
    public void ResolveAnimationSnapshots()
    {
        // 1. Collect @keyframes definitions from <style> elements.
        var keyframesMap = new Dictionary<string, List<KeyframeEntry>>(StringComparer.Ordinal);
        CollectKeyframes(DocumentElement, keyframesMap);

        if (keyframesMap.Count == 0) return;

        // 2. Walk all elements and resolve animations.
        ResolveAnimationsOnTree(DocumentElement, keyframesMap);
    }

    // -----------------------------------------------------------------
    // Keyframe parsing
    // -----------------------------------------------------------------

    private sealed record KeyframeEntry(float Position, Dictionary<string, string> Properties);

    // Instance (not static): reads <style> source through the canonical
    // GetStyleElementSourceText accessor (the single source the cascade also reads) rather than
    // hand-walking child text nodes, keeping @keyframes collection aligned with @position-try and
    // CollectAnimPropsFromStyleElements.
    private void CollectKeyframes(DomElement root, Dictionary<string, List<KeyframeEntry>> map)
    {
        if (string.Equals(root.TagName, "style", StringComparison.OrdinalIgnoreCase))
        {
            var css = GetStyleElementSourceText(root);
            var styleSheet = new CssParser().ParseStyleSheet(css);
            foreach (var atRule in styleSheet.Rules.OfType<CssAtRule>())
            {
                if (!atRule.Name.Equals("keyframes", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = atRule.Prelude.Trim().Trim('"', '\'');
                var entries = ParseKeyframeEntries(atRule);
                if (entries.Count > 0)
                    map[name] = entries;
            }
        }

        foreach (var child in ChildElements(root))
            CollectKeyframes(child, map);
    }

    private static List<KeyframeEntry> ParseKeyframeEntries(CssAtRule keyframesRule)
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

    private static Dictionary<string, string> ParseDeclarations(string text)
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

    // -----------------------------------------------------------------
    // Animation resolution
    // -----------------------------------------------------------------

    private void ResolveAnimationsOnTree(DomElement element, Dictionary<string, List<KeyframeEntry>> keyframesMap)
    {
        // Check if this element has animation properties set (inline styles).
        string? animValue = null, delayValue = null, nameValue = null;
        bool hasAnimation = false, hasDelay = false, hasName = false;

        if (BakedInlineStyle(element).Count > 0)
        {
            hasAnimation = BakedInlineStyle(element).TryGetValue("animation", out animValue);
            hasDelay = BakedInlineStyle(element).TryGetValue("animation-delay", out delayValue);
            hasName = BakedInlineStyle(element).TryGetValue("animation-name", out nameValue);
        }

        // Also check stylesheet rules that may apply to this element.
        if (!hasAnimation && !hasName)
        {
            var sheetProps = CollectStylesheetAnimationProperties(element);
            if (sheetProps != null)
            {
                if (!hasAnimation && sheetProps.TryGetValue("animation", out var sv))
                { hasAnimation = true; animValue = sv; }
                if (!hasDelay && sheetProps.TryGetValue("animation-delay", out var dv))
                { hasDelay = true; delayValue = dv; }
                if (!hasName && sheetProps.TryGetValue("animation-name", out var nv))
                { hasName = true; nameValue = nv; }
            }
        }

        if (hasAnimation || hasName)
        {
            TryResolveAnimation(element, keyframesMap,
                animValue, delayValue, nameValue);
        }

        // Snapshot before recursing: resolving an animation writes inline styles
        // and can restructure the subtree (e.g. materialising generated content),
        // and the walk can also race concurrent DOM mutation. Enumerating the live
        // element.Children then throws "Collection was modified" (crash signature
        // DomBridge.ResolveAnimationsOnTree). SnapshotChildren guards both, as the
        // other DomBridge tree walks do.
        foreach (var child in SnapshotChildren(element))
            ResolveAnimationsOnTree(child, keyframesMap);
    }

    // -----------------------------------------------------------------
    // Stylesheet animation property matching
    // -----------------------------------------------------------------

    /// <summary>
    /// Collects animation-related properties from <c>&lt;style&gt;</c> elements
    /// whose selectors match the given element.  This is a simplified matcher
    /// that handles tag selectors (e.g. <c>body</c>, <c>html</c>).
    /// </summary>
    // Instance (not static) so it can read <style> source through the canonical
    // GetStyleElementSourceText accessor — see CollectAnimPropsFromStyleElements.
    private Dictionary<string, string>? CollectStylesheetAnimationProperties(DomElement element)
    {
        // Walk up to find <style> elements.
        var root = element;
        while (ParentEl(root) != null) root = ParentEl(root);

        Dictionary<string, string>? result = null;
        CollectAnimPropsFromStyleElements(root, element, ref result);
        return result;
    }

    private void CollectAnimPropsFromStyleElements(DomElement node, DomElement target, ref Dictionary<string, string>? result)
    {
        if (string.Equals(node.TagName, "style", StringComparison.OrdinalIgnoreCase))
        {
            // Read the <style> source through the canonical GetStyleElementSourceText accessor
            // (the single source the cascade also reads) rather than hand-walking child text nodes,
            // so stylesheet-declared animation / @keyframes properties and linked-href stylesheets
            // are collected uniformly (aligned with @position-try in AnchorResolver/PositionTry.cs).
            var css = GetStyleElementSourceText(node);

            var styleSheet = new CssParser().ParseStyleSheet(css);
            foreach (var styleRule in styleSheet.Rules.OfType<CssStyleRule>())
            {
                foreach (var selector in styleRule.Selectors.Selectors)
                {
                    if (!SimpleMatchesElement(selector.Text, target))
                        continue;

                    var declarations = ParseDeclarations(
                        CssSerializer.Serialize(styleRule.Declarations));
                    foreach (var kv in declarations)
                    {
                        if (kv.Key.StartsWith("animation", StringComparison.OrdinalIgnoreCase))
                        {
                            result ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            result[kv.Key] = kv.Value;
                        }
                    }
                }
            }
        }

        foreach (var child in ChildElements(node))
            CollectAnimPropsFromStyleElements(child, target, ref result);
    }

    /// <summary>
    /// Very simple CSS selector matcher — handles tag names, classes, IDs,
    /// and <c>:root</c> pseudo-class.  Sufficient for WPT body/html selectors.
    /// </summary>
    private static bool SimpleMatchesElement(string selector, DomElement element)
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

    private void TryResolveAnimation(DomElement element, Dictionary<string, List<KeyframeEntry>> keyframesMap,
        string? animationShorthand, string? animationDelay, string? animationName)
    {
        // Parse animation parameters from the shorthand.
        string? name = null;
        double durationSec = 0;
        double delaySec = 0;
        string timingFunction = "ease";
        string fillMode = "none";

        if (!string.IsNullOrWhiteSpace(animationShorthand))
        {
            var parts = CssAnimation.TokenizeShorthand(animationShorthand!);
            var durations = new List<double>();

            foreach (var part in parts)
            {
                if (CssAnimation.TryParseTime(part, out var sec))
                    durations.Add(sec);
                else if (CssAnimation.IsTimingFunction(part))
                    timingFunction = part;
                else if (part is "none" or "forwards" or "backwards" or "both")
                    fillMode = part;
                else if (name == null && !CssAnimation.IsKnownKeyword(part))
                    name = part;
            }

            if (durations.Count >= 1) durationSec = durations[0];
            if (durations.Count >= 2) delaySec = durations[1];
        }

        // Override with individual longhand properties.
        if (!string.IsNullOrWhiteSpace(animationName))
            name = animationName;
        if (!string.IsNullOrWhiteSpace(animationDelay) &&
            CssAnimation.TryParseTime(animationDelay!, out var delayOverride))
            delaySec = delayOverride;

        if (string.IsNullOrEmpty(name) || durationSec <= 0)
            return;

        if (!keyframesMap.TryGetValue(name!, out var keyframes) || keyframes.Count == 0)
            return;

        double currentTimeMs = 0;
        var hasCurrentTimeOverride = false;
        if (AnimationStateFor(element).CurrentTimeMilliseconds.TryGet(out var currentTimeValue) &&
            currentTimeValue is double currentTimeMsValue)
        {
            currentTimeMs = currentTimeMsValue;
            hasCurrentTimeOverride = true;
        }

        double elapsed;
        if (hasCurrentTimeOverride)
        {
            var currentTimeSec = currentTimeMs / 1000.0;
            if (delaySec >= 0)
            {
                if (currentTimeSec < delaySec)
                {
                    if (fillMode is "backwards" or "both")
                    {
                        foreach (var kv in keyframes[0].Properties)
                            BakedInlineStyle(element)[kv.Key] = kv.Value;
                    }
                    return;
                }

                elapsed = currentTimeSec - delaySec;
            }
            else
            {
                elapsed = currentTimeSec;
            }
        }
        else
        {
            // Only resolve for negative delays (animation already running at t=0).
            if (delaySec >= 0)
                return;

            elapsed = Math.Abs(delaySec);
        }

        // Compute progress: elapsed / duration.
        double rawProgress = elapsed / durationSec;

        // Clamp progress to [0, 1] for a single iteration.
        rawProgress = Math.Min(rawProgress, 1.0);

        // Find the two surrounding keyframes and interpolate.
        // NOTE: The timing function is applied per-interval, not globally.
        var resolvedProps = ResolveKeyframeProperties(element, keyframes, (float)rawProgress, timingFunction);

        // Apply resolved values as inline styles and remove animation properties.
        foreach (var kv in resolvedProps)
            BakedInlineStyle(element)[kv.Key] = kv.Value;

        BakedInlineStyle(element).Remove("animation");
        BakedInlineStyle(element).Remove("animation-delay");
        BakedInlineStyle(element).Remove("animation-name");
        BakedInlineStyle(element).Remove("animation-duration");
        BakedInlineStyle(element).Remove("animation-timing-function");
    }

    private Dictionary<string, string> ResolveKeyframeProperties(DomElement element,
        List<KeyframeEntry> keyframes, float progress, string timingFunction)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Collect all unique property names from keyframes.
        var allProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kf in keyframes)
            foreach (var prop in kf.Properties.Keys)
                allProps.Add(prop);

        foreach (var prop in allProps)
        {
            // Find the keyframes that define this property.
            var relevant = keyframes
                .Where(k => k.Properties.ContainsKey(prop))
                .ToList();

            if (relevant.Count == 0) continue;

            // Find the surrounding keyframes.
            KeyframeEntry? before = null;
            KeyframeEntry? after = null;

            for (int i = 0; i < relevant.Count; i++)
            {
                if (relevant[i].Position <= progress)
                    before = relevant[i];
                if (relevant[i].Position >= progress && after == null)
                    after = relevant[i];
            }

            if (before == null && after != null)
            {
                result[prop] = after.Properties[prop];
            }
            else if (before != null && after == null)
            {
                result[prop] = before.Properties[prop];
            }
            else if (before != null && after != null)
            {
                if (before == after || before.Position == after.Position)
                {
                    result[prop] = before.Properties[prop];
                }
                else
                {
                    // Compute local progress within this interval.
                    float intervalStart = before.Position;
                    float intervalEnd = after.Position;
                    float localProgress = (progress - intervalStart) / (intervalEnd - intervalStart);

                    // Apply per-interval timing function (steps, cubic-bezier, etc.).
                    // Easing evaluation is owned by the canonical Broiler.CSS CssEasing.
                    localProgress = (float)CssEasing.Evaluate(localProgress, timingFunction);

                    // Try color interpolation for background-color, color, etc.
                    var interpolated = TryInterpolateValue(
                        element, prop, before.Properties[prop], after.Properties[prop], localProgress);
                    result[prop] = interpolated;
                }
            }
        }

        return result;
    }

    // -----------------------------------------------------------------
    // Value interpolation
    // -----------------------------------------------------------------

    /// <summary>
    /// Attempts to interpolate between two CSS values at the given progress.
    /// Supports color values (rgb, rgba, named colors) and numeric values.
    /// Falls back to discrete stepping for unsupported value types.
    /// </summary>
    private string TryInterpolateValue(DomElement element, string prop, string fromValue, string toValue, float progress)
    {
        // Try color interpolation for color-related properties. Color parsing (hex,
        // rgb/rgba, hsl/hsla, and the full named-color table) is owned by the shared
        // Broiler.CSS value parser; the bridge only interpolates the parsed channels.
        if (IsColorProperty(prop))
        {
            if (CssValueParser.TryParseColor(fromValue, out var fromColor) &&
                CssValueParser.TryParseColor(toValue, out var toColor))
            {
                int r = Math.Clamp((int)Math.Round(fromColor.Red + (toColor.Red - fromColor.Red) * progress), 0, 255);
                int g = Math.Clamp((int)Math.Round(fromColor.Green + (toColor.Green - fromColor.Green) * progress), 0, 255);
                int b = Math.Clamp((int)Math.Round(fromColor.Blue + (toColor.Blue - fromColor.Blue) * progress), 0, 255);

                double fa = fromColor.Alpha / 255.0;
                double ta = toColor.Alpha / 255.0;
                if (Math.Abs(fa - 1.0) < 0.001 && Math.Abs(ta - 1.0) < 0.001)
                    return $"rgb({r}, {g}, {b})";

                double a = fa + (ta - fa) * progress;
                return $"rgba({r}, {g}, {b}, {a.ToString("F2", CultureInfo.InvariantCulture)})";
            }
        }

        // transform interpolates component-wise between matching function lists (the
        // common animation case, e.g. scale/translate/rotate keyframes); `none` acts as
        // the identity of the other side's functions. Mismatched lists (which need full
        // matrix decomposition) fall through to discrete stepping.
        if (string.Equals(prop, "transform", StringComparison.OrdinalIgnoreCase) &&
            TryInterpolateTransform(fromValue, toValue, progress, out var interpolatedTransform))
            return interpolatedTransform;

        if (TryInterpolateLengthValue(element, prop, fromValue, toValue, progress, out var interpolatedLength))
            return interpolatedLength;

        // Fallback: discrete stepping for non-interpolatable values.
        return progress >= 1.0f ? toValue : fromValue;
    }

    private bool TryInterpolateLengthValue(
        DomElement element,
        string prop,
        string fromValue,
        string toValue,
        float progress,
        out string result)
    {
        result = string.Empty;
        if (!IsLengthInterpolableProperty(prop))
            return false;

        var percentageBasis = GetInterpolationPercentageBasis(element, prop);
        if (!TryEvaluateCssLengthWithViewport(fromValue, element, forLineHeight: false, percentageBasis, out var fromPx) ||
            !TryEvaluateCssLengthWithViewport(toValue, element, forLineHeight: false, percentageBasis, out var toPx))
        {
            return false;
        }

        var interpolated = fromPx + ((toPx - fromPx) * progress);
        result = interpolated.ToString("0.###", CultureInfo.InvariantCulture) + "px";
        return true;
    }

    private double? GetInterpolationPercentageBasis(DomElement element, string prop)
    {
        return prop switch
        {
            "width" or "min-width" or "max-width" or "left" or "right" => ResolveContainingBlockReferenceLength(element, vertical: false),
            "height" or "min-height" or "max-height" or "top" or "bottom" => ResolveContainingBlockReferenceLength(element, vertical: true),
            "margin-left" or "margin-right" or "margin-top" or "margin-bottom" or
            "padding-left" or "padding-right" or "padding-top" or "padding-bottom" =>
                ResolveContainingBlockReferenceLength(element, vertical: false),
            _ => null,
        };
    }

    private static bool IsLengthInterpolableProperty(string prop) => prop switch
    {
        "width" or "height" or "min-width" or "min-height" or "max-width" or "max-height" or
        "top" or "right" or "bottom" or "left" or
        "margin-left" or "margin-right" or "margin-top" or "margin-bottom" or
        "padding-left" or "padding-right" or "padding-top" or "padding-bottom" or
        "font-size" => true,
        _ => false,
    };

    private static bool IsColorProperty(string prop) => prop switch
    {
        "background-color" or "color" or "border-color"
            or "border-top-color" or "border-right-color"
            or "border-bottom-color" or "border-left-color"
            or "outline-color" or "text-decoration-color"
            or "fill" or "stroke" => true,
        _ => false,
    };

}
