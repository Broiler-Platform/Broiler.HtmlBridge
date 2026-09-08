using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;
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
    // callbacks) is threaded the resolved AnimationRuntimeState by the now-instance BuildAnimation.
    private readonly ConditionalWeakTable<DomElement, AnimationRuntimeState> _animationRuntimeStates = [];

    private AnimationRuntimeState AnimationStateFor(DomElement element) =>
        _animationRuntimeStates.GetValue(element, static _ => new AnimationRuntimeState());

    private JsValue BuildAnimationList(DomElement? target)
    {
        var animations = new List<JsValue>();
        foreach (var element in Elements)
        {
            if (IsText(element) || IsComment(element))
                continue;
            if (target != null && !ReferenceEquals(element, target))
                continue;

            if (!TryGetAnimationProperties(element, out var animationShorthand, out var animationDelay))
                continue;

            EnsureAnimationCurrentTime(element, animationShorthand, animationDelay);
            animations.Add(BuildAnimation(element));
        }

        // The list's own storage, not a copy of it: NewArray takes a span and materialises the array
        // from it, which is the same one pass the engine's list constructor made.
        return Realm.NewArray(CollectionsMarshal.AsSpan(animations));
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

    /// <summary>
    /// One <c>Animation</c> object for <paramref name="element"/>: its <c>currentTime</c> accessor
    /// pair and the <c>ready</c> thenable.
    /// </summary>
    /// <remarks>
    /// The surface is the co-located AnimationObjectBinding feature module (Phase 3), written against
    /// JSEAL — so the object, its accessor pair and the two ready-promise methods are minted by the
    /// realm, which names the accessors "get/set currentTime" and makes every function
    /// non-constructable exactly as the bridge's own native-callable type did. currentTime reads and writes
    /// the element's per-bridge animation timeline; it is resolved once here (a stable
    /// ConditionalWeakTable identity for this element and bridge) and handed to the callbacks.
    /// </remarks>
    private JsValue BuildAnimation(DomElement element)
    {
        var realm = Realm;
        var animation = realm.NewObject();
        var animationState = AnimationStateFor(element);
        realm.DefineAccessor(
            animation,
            "currentTime",
            (in c) => Dom.Features.AnimationObjectBinding.GetCurrentTime(animationState, in c),
            (in c) => Dom.Features.AnimationObjectBinding.SetCurrentTime(animationState, in c));

        var ready = realm.NewObject();
        realm.DefineValue(ready, "then",
            realm.NewMethod("then", (in c) => Dom.Features.AnimationObjectBinding.Then(ready, in c), 1));
        realm.DefineValue(ready, "catch",
            realm.NewMethod("catch", (in _) => ready, 1));

        realm.DefineValue(animation, "ready", ready);
        return animation;
    }
}
