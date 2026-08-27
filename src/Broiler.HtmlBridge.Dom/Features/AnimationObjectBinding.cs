using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Number;
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
internal static class AnimationObjectBinding
{
    public static JSValue GetCurrentTime(AnimationRuntimeState state, in Arguments a)
    {
        if (state.CurrentTimeMilliseconds.TryGet(out var value) && value is double currentTimeMs)
            return new JSNumber(currentTimeMs);

        return new JSNumber(0);
    }

    public static JSValue SetCurrentTime(AnimationRuntimeState state, in Arguments a)
    {
        if (a.Length > 0)
            state.CurrentTimeMilliseconds.Set(a[0].DoubleValue);
        return JSUndefined.Value;
    }

    // ready.then(cb): the layout is static, so the animation is already "ready" — run the callback
    // synchronously and return the ready object for chaining.
    public static JSValue Then(JSObject? ready, in Arguments a)
    {
        if (a.Length > 0 && a[0] is JSFunction fn)
            fn.InvokeFunction(new Arguments(JSUndefined.Value, JSUndefined.Value));
        return ready;
    }
}
