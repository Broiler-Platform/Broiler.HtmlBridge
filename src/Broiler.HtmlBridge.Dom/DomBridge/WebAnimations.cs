using Broiler.HtmlBridge.Jseal;
using Broiler.Dom;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

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
/// (<c>DomBridge/Registration/Animations.cs</c>) and returned as built. This remark said the file was
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

    internal readonly record struct AnimationTiming(
        double DurationMs, double DelayMs, string Easing, string Fill,
        double Iterations, double IterationStart);

    // ------------------------------------------------------------------
    //  Transform interpolation (component-wise between matching lists)
    // ------------------------------------------------------------------

    internal sealed record TransformFunction(string Name, List<string> Args);
}
