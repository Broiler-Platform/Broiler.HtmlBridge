using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Broiler.HtmlBridge.Logging;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>setTimeout</c> / <c>setInterval</c> for a worker: real deadlines, page ordering semantics.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not the page's <c>BrowserEventLoop</c>.</b> Reusing it was the first thing tried,
/// and its clock is the reason it cannot be: that loop is explicitly virtual — "not wall-clock: a
/// synchronous drain has no real time, only the relative ordering of deadlines" — because the page's
/// timers are drained in bounded bursts by a host that wants determinism, never pumped continuously.
/// A worker is the opposite: a long-lived pump. Under a virtual clock a
/// <c>setInterval(fn, 1000)</c> in a worker has its deadline reached the instant the loop looks at
/// it, so it would fire as fast as the CPU allows and the worker would spin hot forever. Real
/// deadlines are what make an idle worker idle.
/// </para>
/// <para>
/// <b>What is kept from the page loop is its ordering</b>, because that part is correct and worth not
/// re-deciding: one deadline-ordered queue shared by <c>setTimeout</c> and <c>setInterval</c> (their
/// ids are interchangeable per the HTML spec), a monotonic sequence number for a FIFO tiebreak among
/// equal deadlines, and a delay clamped to a non-negative finite value so <c>NaN</c>, negative and
/// absent delays all mean 0.
/// </para>
/// <para>
/// <b>The determinism this costs is real and is confined to the worker.</b> A page's timers still
/// fire on the virtual clock and a capture is still reproducible; a worker's timers fire in real
/// time, so <em>when</em> its messages arrive relative to the page's drain is not reproducible. That
/// is inherent to a worker rather than a choice made here — it runs on another thread — and the
/// containment is that a page only ever observes a worker through queued messages it must explicitly
/// wait for.
/// </para>
/// <para>
/// <b>Not thread-safe, and does not need to be.</b> Every method is called from the worker's own pump
/// thread: registration happens inside a callback running there, and firing happens there too. The
/// only cross-thread interaction a worker has is its inbox and the page's frame-action queue, both of
/// which are concurrent collections owned elsewhere.
/// </para>
/// </remarks>
internal sealed class WorkerTimers
{
    private readonly record struct Entry(double DeadlineMs, long Seq, JSFunction Fn, double? PeriodMs);

    private readonly Dictionary<int, Entry> _timers = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _idCounter;
    private long _seqCounter;

    private double NowMs => _clock.Elapsed.TotalMilliseconds;

    /// <summary>Registers a timer, returning its id. Repeating when <paramref name="periodMs"/> is set.</summary>
    /// <remarks>
    /// An id is allocated even for a non-function callback, matching <c>setTimeout("string")</c>'s
    /// observable behaviour of returning a clearable handle that never fires.
    /// </remarks>
    public int Add(JSFunction? callback, double delayMs, bool repeating)
    {
        var id = ++_idCounter;
        if (callback is null)
            return id;

        var delay = Normalize(delayMs);
        _timers[id] = new Entry(NowMs + delay, ++_seqCounter, callback, repeating ? delay : null);
        return id;
    }

    public void Clear(int id) => _timers.Remove(id);

    public void ClearAll() => _timers.Clear();

    /// <summary>
    /// Milliseconds until the earliest deadline, or <see langword="null"/> when nothing is pending.
    /// Never negative — an overdue timer reports 0 so the caller does not wait at all.
    /// </summary>
    public double? TimeUntilNext()
    {
        if (_timers.Count == 0)
            return null;

        var now = NowMs;
        var earliest = double.MaxValue;
        foreach (var entry in _timers.Values)
        {
            if (entry.DeadlineMs < earliest)
                earliest = entry.DeadlineMs;
        }

        return Math.Max(0, earliest - now);
    }

    /// <summary>
    /// Fires every timer whose deadline has passed, in <c>(deadline, seq)</c> order, and reschedules
    /// the repeating ones. Returns whether anything ran.
    /// </summary>
    /// <remarks>
    /// The due set is snapshotted before anything is invoked, so a callback that registers another
    /// timer is carried to the next call instead of being fired in this one — the same
    /// one-group-per-step property the page loop relies on to stop a self-rescheduling timer from
    /// starving everything else, here stopping it from starving the inbox.
    /// </remarks>
    public bool RunDue()
    {
        if (_timers.Count == 0)
            return false;

        var now = NowMs;
        var due = _timers
            .Where(kv => kv.Value.DeadlineMs <= now)
            .OrderBy(kv => kv.Value.DeadlineMs)
            .ThenBy(kv => kv.Value.Seq)
            .ToArray();

        if (due.Length == 0)
            return false;

        foreach (var (id, entry) in due)
        {
            // A callback may have cleared this timer (or itself) since the snapshot was taken.
            if (!_timers.ContainsKey(id))
                continue;

            if (entry.PeriodMs is { } period)
                _timers[id] = entry with { DeadlineMs = now + period, Seq = ++_seqCounter };
            else
                _timers.Remove(id);

            try
            {
                entry.Fn.InvokeFunction(new Arguments(JSUndefined.Value));
            }
            catch (Exception ex)
            {
                // A throwing timer must not kill the worker's pump, exactly as a throwing timer does
                // not kill the page's.
                RenderLogger.LogError(LogCategory.JavaScript, "WorkerTimers.RunDue",
                    $"A worker timer callback threw: {ex.Message}", ex);
            }
        }

        return true;
    }

    /// <summary>Clamps a delay to a non-negative finite value; NaN and negatives mean 0.</summary>
    private static double Normalize(double delayMs) =>
        double.IsNaN(delayMs) || delayMs < 0 ? 0 : double.IsInfinity(delayMs) ? 0 : delayMs;
}
