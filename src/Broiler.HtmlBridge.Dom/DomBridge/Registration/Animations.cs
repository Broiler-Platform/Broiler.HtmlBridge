using System.Runtime.CompilerServices;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Dom;
using Broiler.CSS;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // Phase 2 item 4 (de-globalization, 2026-07-17): the per-element Web Animations timeline
    // (currentTime) was the Animation slot of the process-static ElementRuntimeState table; it is now
    // a per-bridge instance table, owned by the session's bridge. Still an element-keyed
    // ConditionalWeakTable, so it GCs with the element and the cloneNode copy (see CloneDomElement) is
    // preserved. The one static caller (the AnimationObjectBinding currentTime get/set feature
    // callbacks) is threaded the resolved AnimationRuntimeState by the now-instance BuildAnimationObject.
    private readonly ConditionalWeakTable<DomElement, AnimationRuntimeState> _animationRuntimeStates = [];

    private AnimationRuntimeState AnimationStateFor(DomElement element) =>
        _animationRuntimeStates.GetValue(element, static _ => new AnimationRuntimeState());

    private JSArray BuildAnimationList(DomElement? target)
    {
        var animations = new List<JSValue>();
        foreach (var element in Elements)
        {
            if (IsText(element) || IsComment(element))
                continue;
            if (target != null && !ReferenceEquals(element, target))
                continue;

            if (!TryGetAnimationProperties(element, out var animationShorthand, out var animationDelay))
                continue;

            EnsureAnimationCurrentTime(element, animationShorthand, animationDelay);
            animations.Add(BuildAnimationObject(element));
        }

        return new JSArray(animations);
    }

    private bool TryGetAnimationProperties(
        DomElement element,
        out string? animationShorthand,
        out string? animationDelay)
    {
        animationShorthand = null;
        animationDelay = null;

        if (InlineStyle(element).TryGetValue("animation", out animationShorthand))
        {
            InlineStyle(element).TryGetValue("animation-delay", out animationDelay);
            return true;
        }

        var stylesheetProps = CollectStylesheetAnimationProperties(element);
        if (stylesheetProps == null)
            return false;

        var hasAnimation = stylesheetProps.TryGetValue("animation", out animationShorthand);
        stylesheetProps.TryGetValue("animation-delay", out animationDelay);
        return hasAnimation || stylesheetProps.ContainsKey("animation-name");
    }

    private void EnsureAnimationCurrentTime(
        DomElement element,
        string? animationShorthand,
        string? animationDelay)
    {
        if (AnimationStateFor(element).CurrentTimeMilliseconds.IsSet)
            return;

        double delaySec = 0;
        if (!string.IsNullOrWhiteSpace(animationDelay) &&
            CssAnimation.TryParseTime(animationDelay, out var delayOverride))
        {
            delaySec = delayOverride;
        }
        else if (!string.IsNullOrWhiteSpace(animationShorthand))
        {
            var durations = new List<double>();
            foreach (var part in CssAnimation.TokenizeShorthand(animationShorthand))
            {
                if (CssAnimation.TryParseTime(part, out var seconds))
                    durations.Add(seconds);
            }

            if (durations.Count >= 2)
                delaySec = durations[1];
        }

        var currentTimeMs = delaySec > 0 ? (delaySec * 1000.0) + 1.0 : Math.Abs(delaySec) * 1000.0;
        AnimationStateFor(element).CurrentTimeMilliseconds.Set(currentTimeMs);
    }

    private JSObject BuildAnimationObject(DomElement element)
    {
        var animation = new JSObject();
        // The animation-object currentTime/ready.then surface is the co-located AnimationObjectBinding
        // feature module (Phase 3). currentTime reads/writes the element's per-bridge animation timeline;
        // resolve it once here (stable CWT identity for this element/bridge) and hand it to the callbacks.
        var animationState = AnimationStateFor(element);
        animation.FastAddProperty(
            "currentTime",
            new DomFunction((in _) => Dom.Features.AnimationObjectBinding.GetCurrentTime(animationState, in _), "get currentTime"),
            new DomFunction((in a) => Dom.Features.AnimationObjectBinding.SetCurrentTime(animationState, in a), "set currentTime"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        var ready = new JSObject();
        ready.FastAddValue(
            "then",
            new DomFunction((in a) => Dom.Features.AnimationObjectBinding.Then(ready, in a), "then", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        ready.FastAddValue(
            "catch",
            new DomFunction((in _) => ready, "catch", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        animation.FastAddValue("ready", ready, JSPropertyAttributes.EnumerableConfigurableValue);
        return animation;
    }

}
