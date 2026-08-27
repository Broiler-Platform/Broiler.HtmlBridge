using System;
using System.Diagnostics;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Number;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The window timer / animation-frame scheduling API — <c>setTimeout</c>/<c>clearTimeout</c>,
/// <c>setInterval</c>/<c>clearInterval</c>, <c>requestAnimationFrame</c>/<c>cancelAnimationFrame</c>
/// and <c>requestIdleCallback</c>/<c>cancelIdleCallback</c> — co-located as an HtmlBridge feature
/// module (Phase 3). Each entry point is a thin adapter that
/// unwraps the JS arguments and delegates to the P2.4 <see cref="BrowserEventLoop"/> task-queue
/// owner. It holds no state of its own, so it takes the owner as a parameter rather than through a
/// host contract — and, for the three that register a callback, the P3.18
/// <see cref="WindowContextManager"/> as well, because deferred work has to remember which browsing
/// context handed it over. Previously the bridge's
/// <c>JsRegistrationSetTimeout070Core</c>..<c>CancelAnimationFrame075Core</c> in the shared
/// JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
internal static class TimerBinding
{
    public static JSValue SetTimeout(BrowserEventLoop loop, WindowContextManager windows, in Arguments a) =>
        new JSNumber(loop.SetTimeout(BindToRegisteringContext(windows, a.Length > 0 ? a[0] as JSFunction : null), ReadDelayMs(a)));

    // The delay argument (a[1]) in ms; absent / NaN / negative are treated as 0 (the event loop clamps too).
    private static double ReadDelayMs(in Arguments a) => a.Length > 1 ? a[1].DoubleValue : 0;

    public static JSValue ClearTimeout(BrowserEventLoop loop, in Arguments a)
    {
        if (a.Length > 0)
            loop.ClearTimeout((int)a[0].DoubleValue);

        return JSUndefined.Value;
    }

    public static JSValue SetInterval(BrowserEventLoop loop, WindowContextManager windows, in Arguments a) =>
        new JSNumber(loop.SetInterval(BindToRegisteringContext(windows, a.Length > 0 ? a[0] as JSFunction : null), ReadDelayMs(a)));

    public static JSValue ClearInterval(BrowserEventLoop loop, in Arguments a)
    {
        if (a.Length > 0)
            loop.ClearInterval((int)a[0].DoubleValue);

        return JSUndefined.Value;
    }

    public static JSValue RequestAnimationFrame(BrowserEventLoop loop, WindowContextManager windows, in Arguments a) =>
        new JSNumber(loop.RequestAnimationFrame(BindToRegisteringContext(windows, a.Length > 0 ? a[0] as JSFunction : null)));

    public static JSValue CancelAnimationFrame(BrowserEventLoop loop, in Arguments a)
    {
        if (a.Length > 0)
            loop.CancelAnimationFrame((int)a[0].DoubleValue);

        return JSUndefined.Value;
    }

    /// <summary>
    /// Ties a callback to the browsing context that registered it, so that when the queue drains it
    /// runs against its own <c>document</c>/<c>location</c>/<c>window</c> rather than against
    /// whichever context happens to be current at that moment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every document shares one JavaScript context here, and the drain runs from C# with no
    /// browsing context pushed — so it ran in the main one. A frame's synchronous script was already
    /// correct (the sub-document script runner pushes the frame's context around it), but the moment
    /// that script deferred anything, the callback woke up in the parent: its <c>document</c> was the
    /// parent's document, so <c>document.getElementById(…)</c> for one of its own elements returned
    /// null and the callback either threw — swallowed by the drain's catch, leaving no trace at all —
    /// or quietly mutated the wrong document. Deferring work to a timer is how most framed script
    /// does anything, so this was most of what a frame's scripting could do.
    /// </para>
    /// <para>
    /// The main window is the overwhelmingly common case and is returned unwrapped: the drain already
    /// runs in its context, so wrapping would buy nothing and cost an allocation per registration
    /// plus <c>RunWithWindowContext</c>'s save/restore on every tick — on the one call busy pages
    /// make constantly. Only a frame pays for being a frame.
    /// </para>
    /// </remarks>
    private static JSFunction? BindToRegisteringContext(WindowContextManager windows, JSFunction? callback)
    {
        if (callback is null || windows.ResolveCurrentSubWindow() is not { } frameWindow)
            return callback;

        return new DomFunction((in a) =>
        {
            // A queued callback is invoked with at most one real argument — a rAF timestamp, an
            // IdleDeadline — and `in` parameters cannot be captured, so the call is read out into
            // locals here. Arguments' first constructor parameter is `this`; Length and the indexer
            // count only the real arguments, so the one to forward is a[0].
            var thisValue = a.This ?? JSUndefined.Value;
            var argument = a.Length > 0 ? a[0] : null;

            JSValue result = JSUndefined.Value;
            windows.RunWithWindowContext(frameWindow, () =>
                result = callback.InvokeFunction(argument is null
                    ? new Arguments(thisValue)
                    : new Arguments(thisValue, argument)));
            return result;
        }, "callback", 0);
    }

    /// <summary>
    /// The budget an idle callback is given, in milliseconds — Background Tasks §2.2 caps
    /// <c>timeRemaining()</c> at 50 ms, and that cap is what a page draining a queue actually
    /// measures itself against.
    /// </summary>
    private const double IdleBudgetMs = 50;

    /// <summary>
    /// <c>requestIdleCallback(callback, { timeout })</c> — Background Tasks §2.3, queued as an
    /// ordinary task because a headless drain has no idle period to wait for. What makes it work is
    /// the <c>IdleDeadline</c> the callback is handed: it used to be a bare alias of
    /// <c>setTimeout</c>, so the callback got the timer's zero arguments and the first thing every
    /// caller does with the parameter — <c>deadline.timeRemaining()</c> — threw "Cannot get property
    /// timeRemaining of undefined". That is not a peripheral API: MediaWiki's ResourceLoader
    /// evaluates its module implementations inside one (<c>asyncEvalTask</c> loops until
    /// <c>timeRemaining() &lt;= 0</c>), and its storage store walks localStorage in another.
    /// </summary>
    /// <remarks>
    /// The second argument is an options dictionary, not a delay, so routing it through
    /// <see cref="ReadDelayMs"/> read <c>NaN</c> off the object and scheduled at 0 regardless of what
    /// the page asked for; <c>timeout</c> is read out of it properly here.
    /// </remarks>
    public static JSValue RequestIdleCallback(BrowserEventLoop loop, WindowContextManager windows, in Arguments a)
    {
        if (a.Length == 0 || BindToRegisteringContext(windows, a[0] as JSFunction) is not { } callback)
            return new JSNumber(loop.SetTimeout(null));

        // A timeout means "run by then at the latest". There is no idle period here for the callback
        // to have been run in earlier, so a callback that carries one is always running because that
        // deadline arrived — which is what didTimeout reports.
        var timeoutMs = ReadIdleTimeoutMs(a);
        var didTimeout = timeoutMs > 0;

        // The deadline is minted when the callback runs, not when it is registered: the budget is the
        // time this invocation has used, so a re-registered callback gets a fresh one each time.
        var withDeadline = new DomFunction((in _) =>
        {
            callback.InvokeFunction(new Arguments(JSUndefined.Value, CreateIdleDeadline(didTimeout)));
            return JSUndefined.Value;
        }, "requestIdleCallback callback", 0);

        // Scheduling it on the timer queue is what keeps the handle cancellable: the id comes from the
        // same space clearTimeout/cancelIdleCallback act on.
        return new JSNumber(loop.SetTimeout(withDeadline, timeoutMs));
    }

    /// <summary>
    /// <c>cancelIdleCallback</c> — the handle came from the timer id space, so this is
    /// <see cref="ClearTimeout"/> under the name Background Tasks gives it.
    /// </summary>
    public static JSValue CancelIdleCallback(BrowserEventLoop loop, in Arguments a) => ClearTimeout(loop, in a);

    // The `timeout` member of the options dictionary (a[1]), in ms. Absent, non-numeric or
    // non-positive means the page asked for no deadline at all.
    private static double ReadIdleTimeoutMs(in Arguments a)
    {
        if (a.Length < 2 || a[1] is not JSObject options)
            return 0;

        var timeout = options[(KeyString)"timeout"];
        if (timeout is null || timeout.IsNull || timeout.IsUndefined)
            return 0;

        var ms = timeout.DoubleValue;
        return double.IsNaN(ms) || ms <= 0 ? 0 : ms;
    }

    /// <summary>
    /// An <c>IdleDeadline</c> (Background Tasks §2.2) over the wall clock, measured from the moment
    /// the callback is entered. Real elapsed time rather than the event loop's virtual clock,
    /// because the budget is what bounds the work the callback does inside a single synchronous
    /// call, which the virtual clock knows nothing about — and because a budget that never runs down
    /// is a page's `while (deadline.timeRemaining() > n)` loop that never yields.
    /// </summary>
    private static JSObject CreateIdleDeadline(bool didTimeout)
    {
        var enteredAt = Stopwatch.GetTimestamp();

        var deadline = new JSObject();
        deadline.FastAddValue("didTimeout",
            didTimeout ? JSBoolean.True : JSBoolean.False,
            JSPropertyAttributes.EnumerableConfigurableValue);
        deadline.FastAddValue("timeRemaining",
            new DomFunction((in _) => new JSNumber(
                Math.Max(0, IdleBudgetMs - Stopwatch.GetElapsedTime(enteredAt).TotalMilliseconds)),
                "timeRemaining", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        return deadline;
    }
}
