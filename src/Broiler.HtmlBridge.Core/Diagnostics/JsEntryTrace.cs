using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge.Core.Diagnostics;

/// <summary>What handed control to JavaScript. Distinguished only where a reader would ask "which
/// kind of turn was that?" — the answer changes where they look next.</summary>
internal enum JsEntryKind
{
    /// <summary>A script body evaluated as a program — the page's own, or one inserted into it.</summary>
    Script,

    /// <summary>A DOM event listener.</summary>
    Event,

    /// <summary>A <c>setTimeout</c>/<c>setInterval</c> callback.</summary>
    Timer,

    /// <summary>A <c>requestAnimationFrame</c> callback.</summary>
    AnimationFrame,

    /// <summary>A host action queued onto the frame, which may run script through the bridge.</summary>
    FrameAction,

    /// <summary>
    /// The host's between-task checkpoint — the microtask/promise-job drain and whatever else the
    /// host attaches to it. Bracketed for one reason: it runs script, and time not inside a turn is
    /// reported as idle, so leaving it out would file real work as a gap.
    /// </summary>
    Checkpoint,
}

/// <summary>
/// An opt-in record of every <b>turn</b> the host hands to JavaScript: how long the turn ran, and how
/// long the page sat idle before it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question it exists to answer.</b> A page that misbehaves after "nothing happened for a
/// while" has two very different possible causes, and from the outside they look identical: the
/// browser was <em>busy</em> for N seconds inside one turn (a stall), or the browser was <em>idle</em>
/// for N seconds between two turns (a gap). Anything that measures wall-clock across a turn boundary
/// — a script's own watchdog, a timing assertion, a heartbeat — cannot tell those apart either, and
/// will accuse the engine of the first when the truth is the second. Recording both numbers at the
/// same boundary is what separates them, and it takes one run to do it.
/// </para>
/// <para>
/// <b>Only the outermost turn is a turn.</b> A listener that dispatches an event synchronously, a
/// script that calls one — those nest, and the inner entries are not boundaries: the page did not
/// become idle between them. So the gap is measured on the transition into depth 1 and the turn's
/// duration on the return to depth 0, and everything in between is part of the same turn. Getting
/// this wrong would report a gap of zero for every nested call and hide the real ones among them.
/// </para>
/// <para>
/// <b>Cost when off, which is always unless someone asked.</b> <see cref="Enter"/> reads one static
/// bool and returns a <see langword="default"/> struct whose <c>Dispose</c> is a null check. Nothing
/// is timed, counted or formatted. The environment variable is read once, at type initialization.
/// </para>
/// <para>
/// <b>Where it writes.</b> Standard error, and <see cref="RenderLogger"/> for a host that collects
/// entries. Standard error is deliberate rather than lazy: <see cref="RenderLogger"/> routes to
/// <see cref="Debug.WriteLine"/>, which a Release build does not emit, and a diagnostic whose whole
/// value is "set the variable, reproduce it once, read the output" has to be readable from a normal
/// build without a debugger attached.
/// </para>
/// </remarks>
internal static class JsEntryTrace
{
    /// <summary>
    /// <c>BROILER_TRACE_JS_ENTRY</c> — unset, <c>0</c>, <c>false</c> or <c>off</c> disables it.
    /// <c>1</c>/<c>true</c>/<c>on</c> enables it with the default thresholds. A bare number sets both
    /// thresholds, in milliseconds. <c>turn=&lt;ms&gt;,gap=&lt;ms&gt;</c> sets them separately.
    /// </summary>
    private const string EnvironmentVariable = "BROILER_TRACE_JS_ENTRY";

    private const double DefaultTurnThresholdMs = 250;
    private const double DefaultGapThresholdMs = 1000;

    /// <summary>
    /// 2^14 ms. Not an arbitrary round number: it is the threshold Google's botguard VM compares
    /// against (<c>qA += K &gt;&gt; 14 &gt; 0</c>), and crossing it twice makes that VM poison its own
    /// state and fail with a TypeError far from the cause. Any script may pick its own number, so a
    /// crossing is called out rather than being the only thing reported.
    /// </summary>
    private const double WatchdogMs = 16384;

    private static readonly double TurnThresholdMs;
    private static readonly double GapThresholdMs;

    /// <summary>Whether anything is being recorded. False in every run that did not ask.</summary>
    public static bool IsActive { get; }

    // The JS thread is one thread, but the event loop may drain from another, so the depth is
    // per-thread and everything shared is written under the lock below.
    [ThreadStatic] private static int _depth;

    private static readonly object Gate = new();
    private static long _lastExitTimestamp;      // 0 until the first turn has finished
    private static long _turns;
    private static long _slowTurns;
    private static long _longGaps;
    private static long _watchdogTurns;
    private static long _watchdogGaps;
    private static double _totalTurnMs;
    private static double _maxTurnMs;
    private static string _maxTurnLabel = string.Empty;
    private static double _maxGapMs;
    private static string _maxGapLabel = string.Empty;

    static JsEntryTrace()
    {
        IsActive = TryParseConfiguration(
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            out var turnMs,
            out var gapMs);

        TurnThresholdMs = turnMs;
        GapThresholdMs = gapMs;

        if (!IsActive)
            return;

        Write($"enabled (turn >= {TurnThresholdMs:0.#} ms, gap >= {GapThresholdMs:0.#} ms; " +
              $"a crossing of {WatchdogMs:0} ms is marked WATCHDOG)");

        // The summary is the half that answers the question — a run with no line over threshold is
        // itself the answer, and a reader should not have to infer it from silence.
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => LogSummary("ProcessExit");
    }

    /// <summary>
    /// Reads the <c>BROILER_TRACE_JS_ENTRY</c> contract. Returns whether the trace is on, and the
    /// thresholds it runs with — which are the defaults whenever the value does not say otherwise.
    /// </summary>
    /// <remarks>
    /// Separated from the static constructor so the contract a reader types can be tested without
    /// the process-wide state that reads it once. The one judgement call in here: a value that
    /// parses as nothing recognisable still turns the trace ON with the defaults. Silently
    /// disabling on a typo is the failure mode that costs a reader the reproduction they just
    /// spent, and the enabled form announces its own thresholds, so a wrong guess is visible in the
    /// first line of output rather than in an absence of output.
    /// </remarks>
    internal static bool TryParseConfiguration(string? raw, out double turnMs, out double gapMs)
    {
        turnMs = DefaultTurnThresholdMs;
        gapMs = DefaultGapThresholdMs;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var value = raw.Trim();
        if (value.Equals("0", StringComparison.Ordinal) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase))
            return false;

        if (value.Equals("1", StringComparison.Ordinal) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase))
            return true;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var both))
        {
            turnMs = both;
            gapMs = both;
            return true;
        }

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 ||
                !double.TryParse(part[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                continue;

            var key = part[..eq].Trim();
            if (key.Equals("turn", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("entry", StringComparison.OrdinalIgnoreCase))
                turnMs = ms;
            else if (key.Equals("gap", StringComparison.OrdinalIgnoreCase))
                gapMs = ms;
        }

        return true;
    }

    /// <summary>
    /// Opens a turn, or returns the inactive <see langword="default"/> when nothing is being recorded.
    /// Dispose it when control returns to the host — <c>using</c> at the call site, so a callback that
    /// throws still closes the turn it opened.
    /// </summary>
    public static Scope Enter(JsEntryKind kind, string label) => IsActive ? new Scope(kind, label) : default;

    /// <summary>Writes the totals: turn count, time in script, the longest turn and the longest gap.</summary>
    public static void LogSummary(string context)
    {
        if (!IsActive)
            return;

        string line;
        lock (Gate)
        {
            line = _turns == 0
                ? $"summary ({context}): no JavaScript turns were recorded"
                : $"summary ({context}): {_turns} turns, {_totalTurnMs:0.#} ms in script; " +
                  $"{_slowTurns} over {TurnThresholdMs:0.#} ms, {_longGaps} gaps over {GapThresholdMs:0.#} ms; " +
                  $"longest turn {_maxTurnMs:0.#} ms ({_maxTurnLabel}), longest gap {_maxGapMs:0.#} ms (before {_maxGapLabel}); " +
                  $"watchdog crossings: {_watchdogTurns} by a turn, {_watchdogGaps} by a gap";
        }

        Write(line);
    }

    private static void Record(JsEntryKind kind, string label, double turnMs, double gapMs, bool hadGap)
    {
        // Composed under the lock, written outside it. Writing inside would hold the gate across
        // stderr and RenderLogger — which takes a lock of its own — for the length of an I/O, on the
        // thread that is running the page.
        string? gapLine = null;
        string? turnLine = null;

        lock (Gate)
        {
            _turns++;
            _totalTurnMs += turnMs;

            if (turnMs > _maxTurnMs)
            {
                _maxTurnMs = turnMs;
                _maxTurnLabel = $"{kind}:{label}";
            }

            if (hadGap && gapMs > _maxGapMs)
            {
                _maxGapMs = gapMs;
                _maxGapLabel = $"{kind}:{label}";
            }

            if (hadGap && gapMs >= GapThresholdMs)
            {
                _longGaps++;
                var watchdog = gapMs >= WatchdogMs;
                if (watchdog)
                    _watchdogGaps++;

                // "idle" is the word that matters: nothing ran here, so no amount of engine speed
                // would have shortened it.
                gapLine = $"gap  {gapMs,10:0.#} ms idle before {kind}:{label}{(watchdog ? "   <-- WATCHDOG" : string.Empty)}";
            }

            if (turnMs >= TurnThresholdMs)
            {
                _slowTurns++;
                var watchdog = turnMs >= WatchdogMs;
                if (watchdog)
                    _watchdogTurns++;

                turnLine = $"turn {turnMs,10:0.#} ms busy in    {kind}:{label}{(watchdog ? "   <-- WATCHDOG" : string.Empty)}";
            }
        }

        if (gapLine is not null)
            Write(gapLine);

        if (turnLine is not null)
            Write(turnLine);
    }

    private static void Write(string message)
    {
        try
        {
            Console.Error.WriteLine($"[js-entry] {message}");
        }
        catch (Exception)
        {
            // A closed or redirected stderr is not a reason to change what the page does.
        }

        RenderLogger.LogDebug(LogCategory.JavaScript, nameof(JsEntryTrace), message);
    }

    /// <summary>
    /// One open turn. <see langword="default"/> is the inactive form, whose <c>Dispose</c> returns
    /// without touching anything.
    /// </summary>
    internal readonly struct Scope : IDisposable
    {
        private readonly JsEntryKind _kind;
        private readonly string? _label;
        private readonly long _startTimestamp;
        private readonly long _gapTicks;
        private readonly bool _outermost;
        private readonly bool _hadGap;

        internal Scope(JsEntryKind kind, string label)
        {
            _kind = kind;
            _label = label;
            _startTimestamp = Stopwatch.GetTimestamp();

            _outermost = _depth == 0;
            _depth++;

            if (_outermost)
            {
                var lastExit = Interlocked.Read(ref _lastExitTimestamp);
                _hadGap = lastExit != 0;
                _gapTicks = _hadGap ? _startTimestamp - lastExit : 0;
            }
            else
            {
                _hadGap = false;
                _gapTicks = 0;
            }
        }

        public void Dispose()
        {
            if (_label is null)
                return;

            _depth--;
            if (!_outermost)
                return;

            var end = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _lastExitTimestamp, end);
            Record(_kind, _label, Ms(end - _startTimestamp), Ms(_gapTicks), _hadGap);
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
