using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Broiler.CSS;
using Broiler.Dom;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeHostUtils;
using static Broiler.HtmlBridge.DomBridgeUtils;

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
            // are collected uniformly (aligned with @position-try in DomBridge/AnchorResolver/AnchorRegistry.cs).
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

}

/// <summary>
/// Web Animations API — <c>element.animate(keyframes, options)</c>. The render is a single
/// snapshot, so this evaluates the animation's effect at the snapshot time (the animation has
/// just started; a negative <c>delay</c> pre-advances it — the pattern WPT interpolation tests
/// and <c>interpolation-testcommon.js</c> use to sample a mid-point instantly) and bakes the
/// interpolated property values into the element's render style. Previously
/// <c>element.animate</c> was a no-op stub, so animation-driven property values never rendered
/// (a scaled/faded/animated element drew at its base value).
/// </summary>
/// <remarks>
/// <b>This file speaks JSEAL end to end.</b> <c>Animatable.animate()</c> is installed by
/// <c>DomBridge/ElementInterface.cs</c> with its realm-minting <c>AddInterfaceMethod</c>, so
/// <see cref="ElementAnimate"/> receives a <see cref="JsCall"/>, and
/// <see cref="ParseAnimationKeyframes"/>, <see cref="DomBridgeUtils.ParseAnimationTiming"/> and
/// <see cref="DomBridgeUtils.ParseAnimationPseudoElement"/> read the keyframes and the options object through the
/// realm. The Animation it hands back is built through the realm by <c>BuildAnimation</c>
/// (<c>DomBridge/Registration/Window.cs</c>) and returned as built. This remark said the file was
/// engine-typed end to end: that the installer used the engine's own <c>AddPrototypeMethod</c> and
/// handed <see cref="ElementAnimate"/> an engine argument frame no <c>JsCall</c> could be made from,
/// so the parsing had to move with the installation, and that the result was unwrapped at the
/// return. Both moved, and the unwrap went with the callback's engine return type, as the comment at
/// the end of <see cref="ElementAnimate"/> records.
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// <c>element.animate(keyframes, options)</c> — bakes the animation's snapshot-time value and
    /// returns a minimal Animation object. Never throws into the caller: a malformed keyframe or
    /// option leaves the element unbaked rather than aborting the script.
    /// </summary>
    private JsValue ElementAnimate(DomElement element, in JsCall call)
    {
        try
        {
            var keyframes = ParseAnimationKeyframes(Realm, call.Length > 0 ? call[0] : JsValue.Undefined);
            var options = call.Length > 1 ? call[1] : JsValue.Undefined;
            var timing = ParseAnimationTiming(Realm, options);
            if (keyframes.Count >= 1 && timing.DurationMs > 0 &&
                TryComputeSnapshotProgress(timing, out var progress))
            {
                var resolved = ResolveKeyframeProperties(element, keyframes, progress, timing.Easing);
                var pseudoElement = ParseAnimationPseudoElement(Realm, options);
                if (pseudoElement is null)
                {
                    foreach (var kv in resolved)
                        BakedInlineStyle(element)[kv.Key] = kv.Value;
                }
                else
                {
                    var target = AnimatedPseudoStyleFor(element, pseudoElement);
                    foreach (var kv in resolved)
                        target[kv.Key] = kv.Value;
                }
                InvalidateStyleScope(element);
            }
        }
        catch
        {
            // Web Animations must not break the page: a bad animate() call is inert.
        }

        // Realm-built and returned as built. This used to unwrap, because the callback's return
        // type was the engine's; the Animation a page gets from animate() and the one it finds in
        // getAnimations() were always the same object either way.
        return BuildAnimation(element);
    }

    // ------------------------------------------------------------------
    //  Animations targeting a pseudo-element (KeyframeAnimationOptions.pseudoElement)
    // ------------------------------------------------------------------

    /// <summary>
    /// Values baked by <c>element.animate(…, { pseudoElement })</c>, per element and pseudo. A
    /// pseudo-element has no node to hang an inline style on, so the bake cannot go where the
    /// element's own does; these are emitted as author rules at serialization instead (see
    /// <c>ApplyAnimatedPseudoSerializationOverrides</c>), which is what carries them to the renderer
    /// through the ordinary cascade.
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        DomElement, Dictionary<string, Dictionary<string, string>>> _animatedPseudoStyles = new();

    /// <summary>
    /// Whether any <c>animate()</c> call has targeted a pseudo-element in this document. The
    /// serialization pass that emits them walks the whole tree, so it is gated on this rather than
    /// run unconditionally — the overwhelming majority of documents never use the feature.
    /// </summary>
    private bool _hasAnimatedPseudoStyles;

    private Dictionary<string, string> AnimatedPseudoStyleFor(DomElement element, string pseudoElement)
    {
        _hasAnimatedPseudoStyles = true;
        var byPseudo = _animatedPseudoStyles.GetValue(
            element, static _ => new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal));
        if (!byPseudo.TryGetValue(pseudoElement, out var properties))
        {
            properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            byPseudo[pseudoElement] = properties;
        }
        return properties;
    }

    /// <summary>
    /// Carries the pseudo bakes onto a clone — the render projection imports the whole tree, so
    /// without this the serialization pass would look them up on a node that never saw the
    /// <c>animate()</c> call and find nothing. Same contract as the other per-element tables copied
    /// by <c>CopyBridgeRuntimeStateTo</c>.
    /// </summary>
    private void CopyAnimatedPseudoStyles(DomElement source, DomElement clone)
    {
        if (!_animatedPseudoStyles.TryGetValue(source, out var byPseudo) || byPseudo.Count == 0)
            return;

        foreach (var (pseudoElement, properties) in byPseudo)
        {
            var target = AnimatedPseudoStyleFor(clone, pseudoElement);
            target.Clear();
            foreach (var (property, value) in properties)
                target[property] = value;
        }
    }

    /// <summary>The pseudo-elements of <paramref name="element"/> that <c>animate()</c> has targeted,
    /// in the order they were first animated. Empty for the overwhelming majority of elements.</summary>
    internal IReadOnlyCollection<string> AnimatedPseudoElementsOf(DomElement element) =>
        _animatedPseudoStyles.TryGetValue(element, out var byPseudo)
            ? byPseudo.Keys
            : [];

    /// <summary>
    /// The animated values for one pseudo-element of <paramref name="element"/>, or
    /// <see langword="null"/> when <c>animate()</c> never targeted it.
    /// </summary>
    internal Dictionary<string, string>? AnimatedPseudoStyle(DomElement element, string pseudoElement) =>
        _animatedPseudoStyles.TryGetValue(element, out var byPseudo) &&
        byPseudo.TryGetValue(pseudoElement, out var properties) &&
        properties.Count > 0
            ? properties
            : null;

    private List<KeyframeEntry> ParseAnimationKeyframes(IJsRealm realm, JsValue keyframesValue)
    {
        var entries = new List<KeyframeEntry>();
        if (!keyframesValue.IsArray)
        {
            // The other half of the Web Animations keyframe argument: the *property-indexed* form,
            // `{ opacity: [0, 1], backgroundColor: ["green", "green"] }`, where each property
            // carries its own list of values rather than each keyframe carrying a property set.
            // Only the array form was understood, so an animation written this way parsed to zero
            // keyframes and was silently inert — including WPT
            // css/css-pseudo/backdrop-animate-002, which uses it exclusively.
            return keyframesValue.IsObject
                ? ParsePropertyIndexedKeyframes(realm, keyframesValue)
                : entries;
        }

        // The same hole-skipping walk the transfer lists use: a hole is absent, not undefined.
        var items = Dom.Features.WorkerTransfer.ArrayElements(realm, keyframesValue).ToList();
        for (var i = 0; i < items.Count; i++)
        {
            var keyframe = items[i];
            if (!keyframe.IsObject)
                continue;

            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (keyframeKey, cssName) in AnimatableProperties)
            {
                var value = realm.GetProperty(keyframe, keyframeKey);
                if (value.IsMissing || value.IsNullish)
                    continue;
                var text = realm.ToJsString(value);
                if (!string.IsNullOrWhiteSpace(text))
                    properties[cssName] = text;
            }

            if (properties.Count == 0)
                continue;

            // Explicit offset, else distribute evenly across the keyframe list (spec default).
            float position;
            var offset = realm.GetProperty(keyframe, "offset");
            if (offset.IsNumber)
                position = (float)offset.AsNumber;
            else
                position = items.Count <= 1 ? 0f : (float)i / (items.Count - 1);

            entries.Add(new KeyframeEntry(position, properties));
        }

        entries.Sort((x, y) => x.Position.CompareTo(y.Position));
        return entries;
    }
}
