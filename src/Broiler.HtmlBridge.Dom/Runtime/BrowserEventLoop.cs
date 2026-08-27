using System.Collections.Concurrent;
using Broiler.HtmlBridge.Logging;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The single owner of a document's task queues and their drain (HtmlBridge complexity-reduction
/// roadmap Phase 2, P2.4): <c>setTimeout</c>/<c>setInterval</c> callbacks, <c>requestAnimationFrame</c>
/// callbacks, and internally-queued frame actions, plus the id counters and the "cleared timer"
/// set. It replaces the timer lists and the <c>FlushTimerStep</c>/<c>FlushTimers</c> drain that were
/// spread across the bridge, so there is one place that queues, cancels and runs pending work.
/// </summary>
/// <remarks>
/// The queues are concurrent because JS Promise/async/generator and scroll/timer continuations are
/// dispatched on ThreadPool threads, so a continuation may register a timer concurrently with a
/// drain; <c>ConcurrentDictionary.ToArray()</c> snapshots consistently and <c>TryRemove</c> drains
/// only the collected entries, so a timer registered mid-drain is carried to the next step rather
/// than wiped. Defining the single-owner thread-affinity model (rather than relying on concurrent
/// collections) is the remaining Phase-2 goal this class is the seam for; today it preserves the
/// existing defensive concurrency. Instance-scoped to the owning bridge/document; <see cref="Clear"/>
/// runs on re-parse and disposal and drops pending work without running it.
/// </remarks>
internal sealed class BrowserEventLoop
{
    private int _timerIdCounter;
    // Host-task ids descend from 0 so they cannot collide with the ascending setTimeout/setInterval
    // ids the page holds; see QueueTask.
    private int _hostTaskIdCounter;

    /// <summary>
    /// A queued task — a one-shot <c>setTimeout</c> (<paramref name="Period"/> <c>null</c>) or a repeating
    /// <c>setInterval</c> (<paramref name="Period"/> = its interval in ms). <paramref name="Deadline"/> is its
    /// virtual firing time (ms on the loop's virtual clock — <c>now + max(0, delay)</c> at registration; an
    /// interval reschedules to <c>Deadline + Period</c> after each tick), and <paramref name="Seq"/> a
    /// monotonic registration counter for a FIFO tiebreak among equal deadlines. The drain fires timers in
    /// <c>(Deadline, Seq)</c> order so an earlier-deadline timer runs first (HTML event-loop timer ordering) —
    /// e.g. <c>setTimeout(a, 100); setTimeout(b, 0)</c> runs <c>b</c> before <c>a</c>, and a fast
    /// <c>setInterval</c> ticks the right number of times before a slower <c>setTimeout</c>.
    /// <para>
    /// Exactly one of <paramref name="Fn"/> (a page callback) and <paramref name="HostTask"/> (host work
    /// queued through <see cref="QueueTask"/>) is set. Keeping them as separate fields rather than wrapping
    /// every callback in an <see cref="Action"/> keeps <c>setTimeout</c> — which busy pages call constantly —
    /// free of a closure allocation per registration.
    /// </para>
    /// </summary>
    private readonly record struct TimerEntry(double Deadline, long Seq, JSFunction? Fn, Action? HostTask, double? Period);

    private long _timerSeqCounter;
    // The loop's virtual clock (ms). Advances to the earliest pending deadline as timers fire, so a timer
    // scheduled by a callback is dated relative to the virtual "now" at that point. Not wall-clock: a
    // synchronous drain has no real time, only the relative ordering of deadlines.
    private double _virtualNowMs;
    // One unified deadline-ordered queue for setTimeout and setInterval (they share an id space; clearTimeout
    // and clearInterval are interchangeable per the HTML spec).
    private readonly ConcurrentDictionary<int, TimerEntry> _timers = new();
    private readonly ConcurrentDictionary<int, byte> _clearedTimerIds = new();
    private int _rafIdCounter;
    private readonly ConcurrentDictionary<int, JSFunction> _rafCallbacks = new();
    private int _frameActionIdCounter;
    private readonly ConcurrentDictionary<int, Action> _frameActions = new();

    // ------------------------------------------------------------------
    //  Registration / cancellation
    // ------------------------------------------------------------------

    /// <summary>Registers a one-shot timeout, returning its id. The id is allocated even when
    /// <paramref name="callback"/> is <c>null</c> (matching <c>setTimeout</c> with a non-function arg).
    /// <paramref name="delayMs"/> sets the timer's virtual deadline (<c>now + max(0, delay)</c>); it is
    /// clamped to a non-negative finite value (a <c>NaN</c>/negative/absent delay is 0), so timeouts fire in
    /// deadline order.</summary>
    public int SetTimeout(JSFunction? callback, double delayMs = 0)
    {
        var id = Interlocked.Increment(ref _timerIdCounter);
        if (callback is not null)
        {
            var delay = double.IsNaN(delayMs) || delayMs < 0 ? 0 : delayMs;
            var seq = Interlocked.Increment(ref _timerSeqCounter);
            _timers[id] = new TimerEntry(_virtualNowMs + delay, seq, callback, HostTask: null, Period: null);
        }
        return id;
    }

    /// <summary>
    /// Queues host work as a task on this queue, due now — the same queue the page's timers are in, so it
    /// takes its turn in <c>(Deadline, Seq)</c> order rather than after every timer that happens to be due.
    /// </summary>
    /// <remarks>
    /// This is what a script-inserted <c>&lt;script src&gt;</c>'s fetch-and-run is queued as, and the
    /// ordering is the point. A frame action would run after <em>all</em> due timers in the step, so a page
    /// that injects a script and then schedules <c>setTimeout(check, 100)</c> would run its check against a
    /// script that had not loaded — the drain advances the virtual clock to the earliest deadline, so 100 ms
    /// is "due" in the very first step. Due-now-in-registration-order instead means the load beats a timer
    /// scheduled after it and any timer with a later deadline, which is what a browser does: a fetch that
    /// completes in milliseconds settles before a timeout waiting on it.
    /// <para>
    /// The id comes from a separate descending counter, so host tasks occupy negative ids and page timers
    /// positive ones. They share the queue but not the id space: <c>clearTimeout</c> only ever sees ids
    /// <c>setTimeout</c> handed out, so the "clear every timer" loop some pages run
    /// (<c>for (i = 1; i &lt; n; i++) clearTimeout(i)</c>) cannot cancel a pending script load.
    /// </para>
    /// </remarks>
    public void QueueTask(Action task)
    {
        var id = Interlocked.Decrement(ref _hostTaskIdCounter);
        var seq = Interlocked.Increment(ref _timerSeqCounter);
        _timers[id] = new TimerEntry(_virtualNowMs, seq, Fn: null, task, Period: null);
    }

    /// <summary>Cancels a timeout and marks its id cleared so an in-flight drain skips it.</summary>
    public void ClearTimeout(int id) => CancelTimer(id);

    /// <summary>Registers a repeating interval, returning its id. <paramref name="periodMs"/> is the tick
    /// period (clamped to a non-negative finite value); the interval fires at <c>now + period</c> and then
    /// every <c>period</c> ms on the virtual clock, in deadline order with the other timers.</summary>
    public int SetInterval(JSFunction? callback, double periodMs = 0)
    {
        var id = Interlocked.Increment(ref _timerIdCounter);
        if (callback is not null)
        {
            var period = double.IsNaN(periodMs) || periodMs < 0 ? 0 : periodMs;
            var seq = Interlocked.Increment(ref _timerSeqCounter);
            _timers[id] = new TimerEntry(_virtualNowMs + period, seq, callback, HostTask: null, period);
        }
        return id;
    }

    /// <summary>Cancels an interval and marks its id cleared. Interchangeable with <see cref="ClearTimeout"/>
    /// (both act on the shared timer id space).</summary>
    public void ClearInterval(int id) => CancelTimer(id);

    private void CancelTimer(int id)
    {
        _timers.TryRemove(id, out _);
        _clearedTimerIds[id] = 0;
    }

    /// <summary>Registers a one-shot animation-frame callback, returning its id.</summary>
    public int RequestAnimationFrame(JSFunction? callback)
    {
        var id = Interlocked.Increment(ref _rafIdCounter);
        if (callback is not null)
            _rafCallbacks[id] = callback;
        return id;
    }

    /// <summary>Cancels a pending animation-frame callback.</summary>
    public void CancelAnimationFrame(int id) => _rafCallbacks.TryRemove(id, out _);

    /// <summary>Queues an internal frame action (e.g. a smooth-scroll continuation) for the next drain step.</summary>
    public void QueueFrameAction(Action action) =>
        _frameActions[Interlocked.Increment(ref _frameActionIdCounter)] = action;

    // ------------------------------------------------------------------
    //  Drain
    // ------------------------------------------------------------------

    /// <summary>Whether any timer (timeout/interval), animation-frame callback or frame action is queued.</summary>
    public bool HasPendingWork =>
        !_timers.IsEmpty || !_rafCallbacks.IsEmpty || !_frameActions.IsEmpty;

    /// <summary>
    /// Whether any queued work is due at or before <paramref name="horizonMs"/> on the virtual clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="HasPendingWork"/> cannot answer "is this page finished?", because an interval is
    /// never finished: it reschedules to <c>Deadline + Period</c> after every tick, so a page holding
    /// one keeps pending work forever by design. A drain that waits for the queue to empty therefore
    /// cannot terminate on the ordinary case, and each step advances the virtual clock by a whole
    /// period — so waiting it out does not merely spin, it simulates page time that a capture was
    /// never meant to cover.
    /// </para>
    /// <para>
    /// A horizon separates the two questions. Work scheduled beyond it is simply later, not stuck,
    /// and a caller can stop without calling the page broken. Work still due at time zero after
    /// repeated draining is the genuine runaway — a callback that reschedules itself with no delay —
    /// and stays visible because it never moves the clock past the horizon.
    /// </para>
    /// <para>
    /// Animation-frame callbacks and frame actions carry no deadline; they are due whenever they are
    /// queued, so they count as due at any horizon.
    /// </para>
    /// </remarks>
    public bool HasWorkDueBy(double horizonMs)
    {
        if (!_rafCallbacks.IsEmpty || !_frameActions.IsEmpty)
            return true;

        foreach (var kv in _timers.ToArray())
        {
            if (!_clearedTimerIds.ContainsKey(kv.Key) && kv.Value.Deadline <= horizonMs)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Runs pending work to a fixed point: repeatedly runs one batch (up to a safety cap) until no
    /// batch does anything, then clears the processed-timer-id set. <paramref name="taskCheckpoint"/>
    /// runs after each task (a spec-like microtask checkpoint). Used before DOM capture/serialisation.
    /// </summary>
    public void DrainAll(Action? taskCheckpoint)
    {
        const int maxIterations = 1000;
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            if (!DrainStep(taskCheckpoint))
                break;
        }

        _clearedTimerIds.Clear();
    }

    /// <summary>
    /// Runs one batch of pending timeout, interval, animation-frame and frame-action work. Returns
    /// <c>true</c> if anything ran (more may be pending), <c>false</c> if there was nothing to do.
    /// Used by interactive rendering to step through animations one frame at a time.
    /// </summary>
    public bool DrainStep(Action? taskCheckpoint)
    {
        void RunTaskCheckpoint()
        {
            try
            {
                taskCheckpoint?.Invoke();
            }
            catch (Exception ex)
            {
                RenderLogger.LogError(LogCategory.JavaScript, "BrowserEventLoop.DrainStep", $"Task checkpoint error: {ex.Message}", ex);
            }
        }

        // ConcurrentDictionary.ToArray() takes a consistent snapshot even while other threads
        // register callbacks, and TryRemove drains only the entries we actually collected — so a
        // timer registered concurrently (or by a callback that runs during this drain) is carried to
        // the next step instead of being wiped by a blanket Clear().

        // Advance the virtual clock to the earliest pending timer deadline, then collect every timer due at
        // that time (an interval that reschedules to the same instant, plus co-scheduled timers) in
        // (deadline, seq) order. Firing only the earliest deadline group per step is what lets a fast
        // setInterval tick the right number of times before a slower setTimeout: each step advances the
        // clock by one deadline. A timer scheduled mid-step is carried to the next step, which — together
        // with the one-group-per-step granularity — preserves the runaway self-rescheduling-timer cap of
        // DrainAll (a setTimeout(fn, 0) that reschedules itself fires once per step, bounded by the cap).
        var pending = new List<(int Id, TimerEntry Entry)>();
        var earliest = double.PositiveInfinity;
        var timerSnapshot = _timers.ToArray();
        foreach (var kv in timerSnapshot)
        {
            if (!_clearedTimerIds.ContainsKey(kv.Key) && kv.Value.Deadline < earliest)
                earliest = kv.Value.Deadline;
        }
        if (!double.IsPositiveInfinity(earliest))
        {
            if (earliest > _virtualNowMs) _virtualNowMs = earliest;
            foreach (var kv in timerSnapshot)
            {
                if (kv.Value.Deadline <= _virtualNowMs && !_clearedTimerIds.ContainsKey(kv.Key)
                    && _timers.TryRemove(kv.Key, out var entry))
                    pending.Add((kv.Key, entry));
            }
            pending.Sort(static (x, y) =>
            {
                var byDeadline = x.Entry.Deadline.CompareTo(y.Entry.Deadline);
                return byDeadline != 0 ? byDeadline : x.Entry.Seq.CompareTo(y.Entry.Seq);
            });
        }

        // Collect rAF callbacks (one-shot: remove as collected)
        var rafSnapshot = new List<(int Id, JSFunction Fn)>();
        foreach (var kv in _rafCallbacks.ToArray())
        {
            if (_rafCallbacks.TryRemove(kv.Key, out var fn))
                rafSnapshot.Add((kv.Key, fn));
        }

        var frameActionSnapshot = new List<Action>();
        foreach (var kv in _frameActions.ToArray())
        {
            if (_frameActions.TryRemove(kv.Key, out var action))
                frameActionSnapshot.Add(action);
        }

        if (pending.Count == 0 && rafSnapshot.Count == 0 && frameActionSnapshot.Count == 0)
            return false;

        // Execute the due timers in (deadline, seq) order. Each fires at most once this step; an interval is
        // rescheduled for its next tick (Deadline + Period) for a later step unless it was cleared while
        // running, so a self-rescheduling timer/interval ticks once per step (bounded by the DrainAll cap).
        foreach (var (id, entry) in pending)
        {
            if (_clearedTimerIds.ContainsKey(id)) continue;
            try
            {
                if (entry.Fn is { } fn)
                    fn.InvokeFunction(new Arguments(JSUndefined.Value));
                else
                    entry.HostTask?.Invoke();
            }
            catch (Exception ex) { RenderLogger.LogError(LogCategory.JavaScript, "BrowserEventLoop.DrainStep", $"timer callback error: {ex.Message}", ex); }
            finally { RunTaskCheckpoint(); }

            if (entry.Period is double period && !_clearedTimerIds.ContainsKey(id))
            {
                var nextSeq = Interlocked.Increment(ref _timerSeqCounter);
                _timers[id] = new TimerEntry(entry.Deadline + period, nextSeq, entry.Fn, entry.HostTask, period);
            }
        }

        // Execute rAF callbacks
        foreach (var (id, fn) in rafSnapshot)
        {
            try { fn.InvokeFunction(new Arguments(JSUndefined.Value, new JSNumber(0))); }
            catch (Exception ex) { RenderLogger.LogError(LogCategory.JavaScript, "BrowserEventLoop.DrainStep", $"rAF callback error: {ex.Message}", ex); }
            finally { RunTaskCheckpoint(); }
        }

        foreach (var action in frameActionSnapshot)
        {
            try { action(); }
            catch (Exception ex) { RenderLogger.LogError(LogCategory.JavaScript, "BrowserEventLoop.DrainStep", $"frame action error: {ex.Message}", ex); }
            finally { RunTaskCheckpoint(); }
        }

        return true;
    }

    /// <summary>Drops every queued task and resets the id counters. Runs on re-parse and disposal;
    /// pending work is discarded, never run.</summary>
    public void Clear()
    {
        _timers.Clear();
        _clearedTimerIds.Clear();
        _rafCallbacks.Clear();
        _frameActions.Clear();
        _timerIdCounter = 0;
        _hostTaskIdCounter = 0;
        _timerSeqCounter = 0;
        _virtualNowMs = 0;
        _rafIdCounter = 0;
        _frameActionIdCounter = 0;
    }
}
