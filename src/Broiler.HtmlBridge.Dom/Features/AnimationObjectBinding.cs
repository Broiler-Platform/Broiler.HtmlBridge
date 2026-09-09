using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The Web Animations <c>Animation</c> object surface built by <c>BuildAnimationObject</c> — its
/// <c>currentTime</c> get/set and its <c>ready</c>-promise <c>then</c> — co-located as an HtmlBridge
/// feature module (Phase 3). <c>currentTime</c> reads/writes the element's animation timeline on the
/// per-bridge <see cref="AnimationRuntimeState"/> the bridge resolves and hands in (Phase 2 item 4
/// de-globalization — was the process-static <c>GetElementRuntimeState(element).Animation</c> slot);
/// <c>then</c> is a synchronous promise shim that invokes its callback immediately and returns the
/// <c>ready</c> object (touching no bridge state). These are pure static callbacks (the animation object
/// is built in a static context), so the module has no host contract. Previously the bridge's
/// <c>JsRegistrationGetCurrentTime152Core</c>/<c>SetCurrentTime153Core</c>/<c>Then154Core</c> in the
/// shared JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine type;
/// the realm arrives on the call frame, and the callbacks stay static with their state — the animation
/// state and the <c>ready</c> object — captured by the bridge's closures exactly as before.
/// </remarks>
internal static class AnimationObjectBinding
{
    public static JsValue GetCurrentTime(AnimationRuntimeState state, in JsCall call)
    {
        if (state.CurrentTimeMilliseconds.TryGet(out var value) && value is double currentTimeMs)
            return JsValue.Number(currentTimeMs);

        return JsValue.Number(0);
    }

    /// <remarks>
    /// The realm's <c>ToNumber</c>, not the handle's <c>AsNumber</c>: the engine's <c>DoubleValue</c>
    /// this replaces <em>is</em> the ECMAScript coercion, so <c>animation.currentTime = "500"</c> — and
    /// an object with a <c>valueOf</c> — set the timeline rather than making it NaN, and a setter is
    /// exactly the site a page assigns a non-number to.
    /// </remarks>
    public static JsValue SetCurrentTime(AnimationRuntimeState state, in JsCall call)
    {
        if (call.Length > 0)
            state.CurrentTimeMilliseconds.Set(call.Realm.ToNumber(call[0]));
        return JsValue.Undefined;
    }

    // ready.then(cb): the layout is static, so the animation is already "ready" — run the callback
    // synchronously and return the ready object for chaining.
    public static JsValue Then(JsValue ready, in JsCall call)
    {
        // The callback is invoked with an undefined receiver and one undefined argument, which is what
        // the engine-typed call frame (receiver, first argument) spelled before.
        if (call.Length > 0 && call[0].IsFunction)
            call.Realm.Invoke(call[0], JsValue.Undefined, [JsValue.Undefined]);
        return ready;
    }
}
