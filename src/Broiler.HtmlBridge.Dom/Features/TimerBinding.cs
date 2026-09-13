using System;
using System.Diagnostics;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;

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
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's, and as of this commit that is the whole of it: arguments are
/// read off the call frame, the callback wrapper and the <c>IdleDeadline</c> are minted by the realm,
/// a callback is invoked through it, and a callback handed to <see cref="BrowserEventLoop"/> stays the
/// handle the realm minted. This file names no engine type.
/// </para>
/// <para>
/// <b>The exception this remark used to describe had already stopped existing.</b> It said
/// <see cref="BrowserEventLoop"/>'s queues "hold the engine's own function type", and listed what
/// porting them would take: its <c>TimerEntry.Fn</c>, its <c>_rafCallbacks</c> map and its
/// registration signatures — three of them, not the four it counted — taking a handle, its
/// "no callback" test becoming <c>!callback.IsFunction</c>, and its drain invoking through a realm it
/// would have to be handed. Every one of those is how that class is already written. What was left was
/// a private <c>ToEngineCallback</c> here unwrapping each handle to the engine's function, and three
/// overloads there wrapping the same reference straight back into the handle it came from — a round
/// trip whose two halves were each other's only caller.
/// </para>
/// </remarks>
internal static class TimerBinding
{
    public static JsValue SetTimeout(BrowserEventLoop loop, WindowContextManager windows, in JsCall call) =>
        JsValue.Number(loop.SetTimeout(
            BindToRegisteringContext(call.Realm, windows, call[0]), ReadDelayMs(in call)));

    // The delay argument (call[1]) in ms; absent / NaN / negative are treated as 0 (the event loop
    // clamps too). ToNumber rather than the handle's own reading: `setTimeout(f, "100")` is ordinary
    // page code, and the string has to coerce the way the language says.
    private static double ReadDelayMs(in JsCall call) => call.Length > 1 ? call.Realm.ToNumber(call[1]) : 0;

    public static JsValue ClearTimeout(BrowserEventLoop loop, in JsCall call)
    {
        if (call.Length > 0)
            loop.ClearTimeout((int)call.Realm.ToNumber(call[0]));

        return JsValue.Undefined;
    }

    public static JsValue SetInterval(BrowserEventLoop loop, WindowContextManager windows, in JsCall call) =>
        JsValue.Number(loop.SetInterval(
            BindToRegisteringContext(call.Realm, windows, call[0]), ReadDelayMs(in call)));

    public static JsValue ClearInterval(BrowserEventLoop loop, in JsCall call)
    {
        if (call.Length > 0)
            loop.ClearInterval((int)call.Realm.ToNumber(call[0]));

        return JsValue.Undefined;
    }

    public static JsValue RequestAnimationFrame(BrowserEventLoop loop, WindowContextManager windows, in JsCall call) =>
        JsValue.Number(loop.RequestAnimationFrame(
            BindToRegisteringContext(call.Realm, windows, call[0])));

    public static JsValue CancelAnimationFrame(BrowserEventLoop loop, in JsCall call)
    {
        if (call.Length > 0)
            loop.CancelAnimationFrame((int)call.Realm.ToNumber(call[0]));

        return JsValue.Undefined;
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
    /// <para>
    /// A value that is not callable is handed back untouched, and the loop reads it as "allocate an id
    /// and queue nothing" on its own: every registration allocates the id first and stores an entry only
    /// when <c>IsFunction</c> holds. This used to be spelled by narrowing the value to a CLR
    /// <see langword="null"/> on the way through, which the test at the other end could not tell from
    /// any other non-function and which cost a crossing into the engine to produce.
    /// </para>
    /// </remarks>
    private static JsValue BindToRegisteringContext(IJsRealm realm, WindowContextManager windows, JsValue callback)
    {
        if (!callback.IsFunction || windows.ResolveCurrentSubWindow() is not { } frameWindow)
            return callback;

        return realm.NewMethod("callback", (in call) =>
        {
            // A queued callback is invoked with at most one real argument — a rAF timestamp, an
            // IdleDeadline — and a call frame cannot be captured by the closure below, so the call is
            // read out into locals here. `This` is already `undefined` rather than absent when the
            // caller supplied no receiver, so it forwards as it stands.
            var thisValue = call.This;
            var argument = call[0];

            JsValue result = JsValue.Undefined;
            windows.RunWithWindowContext(frameWindow, () =>
                result = argument.IsMissing
                    ? realm.Invoke(callback, thisValue)
                    : realm.Invoke(callback, thisValue, [argument]));
            return result;
        });
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
    public static JsValue RequestIdleCallback(BrowserEventLoop loop, WindowContextManager windows, in JsCall call)
    {
        var realm = call.Realm;
        var callback = BindToRegisteringContext(realm, windows, call[0]);
        if (!callback.IsFunction)
            return JsValue.Number(loop.SetTimeout(JsValue.Undefined));

        // A timeout means "run by then at the latest". There is no idle period here for the callback
        // to have been run in earlier, so a callback that carries one is always running because that
        // deadline arrived — which is what didTimeout reports.
        var timeoutMs = ReadIdleTimeoutMs(in call);
        var didTimeout = timeoutMs > 0;

        // The deadline is minted when the callback runs, not when it is registered: the budget is the
        // time this invocation has used, so a re-registered callback gets a fresh one each time.
        var withDeadline = realm.NewMethod("requestIdleCallback callback", (in _) =>
        {
            realm.Invoke(callback, JsValue.Undefined, [CreateIdleDeadline(realm, didTimeout)]);
            return JsValue.Undefined;
        });

        // Scheduling it on the timer queue is what keeps the handle cancellable: the id comes from the
        // same space clearTimeout/cancelIdleCallback act on.
        return JsValue.Number(loop.SetTimeout(withDeadline, timeoutMs));
    }

    /// <summary>
    /// <c>cancelIdleCallback</c> — the handle came from the timer id space, so this is
    /// <see cref="ClearTimeout"/> under the name Background Tasks gives it.
    /// </summary>
    public static JsValue CancelIdleCallback(BrowserEventLoop loop, in JsCall call) => ClearTimeout(loop, in call);

    // The `timeout` member of the options dictionary (call[1]), in ms. Absent, non-numeric or
    // non-positive means the page asked for no deadline at all. An argument that was never supplied
    // is Missing rather than undefined, and IsNullish covers all three of those the way the old
    // null / IsNull / IsUndefined trio did.
    private static double ReadIdleTimeoutMs(in JsCall call)
    {
        if (call.Length < 2 || !call[1].IsObject)
            return 0;

        var timeout = call.Realm.GetProperty(call[1], "timeout");
        if (timeout.IsNullish)
            return 0;

        var ms = call.Realm.ToNumber(timeout);
        return double.IsNaN(ms) || ms <= 0 ? 0 : ms;
    }

    /// <summary>
    /// An <c>IdleDeadline</c> (Background Tasks §2.2) over the wall clock, measured from the moment
    /// the callback is entered. Real elapsed time rather than the event loop's virtual clock,
    /// because the budget is what bounds the work the callback does inside a single synchronous
    /// call, which the virtual clock knows nothing about — and because a budget that never runs down
    /// is a page's `while (deadline.timeRemaining() > n)` loop that never yields.
    /// </summary>
    private static JsValue CreateIdleDeadline(IJsRealm realm, bool didTimeout)
    {
        var enteredAt = Stopwatch.GetTimestamp();

        var deadline = realm.NewObject();
        realm.DefineValue(deadline, "didTimeout", JsValue.Boolean(didTimeout));
        realm.DefineValue(deadline, "timeRemaining",
            realm.NewMethod("timeRemaining", (in _) => JsValue.Number(
                Math.Max(0, IdleBudgetMs - Stopwatch.GetElapsedTime(enteredAt).TotalMilliseconds)), 0));
        return deadline;
    }
}
