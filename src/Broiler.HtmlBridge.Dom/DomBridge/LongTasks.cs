using System.Diagnostics;
using Broiler.Dom;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// The Long Tasks API: a <c>PerformanceLongTaskTiming</c> entry for every task that ran for more than
/// 50 milliseconds, handed to the observers of the documents it is reported to.
/// </summary>
/// <remarks>
/// <para>
/// <b>What was there.</b> Nothing: <c>longtask</c> was not a type an observer could ask for, so a page
/// that watches for them -- reCAPTCHA does -- was never told of one, whatever its tasks took.
/// </para>
/// <para>
/// <b>What a task is.</b> What the event loop runs (a timer's callback, an animation frame's, a queued
/// host action -- a message, a worker's reply, an inserted script's load), a page script, and a user's
/// input, each with the microtask checkpoint after it (<see cref="Dom.Runtime.ITaskMonitor"/>). Its
/// times are real ones, on the page's clock: a long task is one that was long.
/// </para>
/// <para>
/// <b>Whose task it is.</b> A frame's work runs in its window's context -- its timers, its messages, its
/// document's scripts -- and the time spent there is the frame's. Here a frame's document loads inside
/// the task of the page script that reached it, where a browser loads it in tasks of its own, so a task
/// is shared out: each window's time in it is that window's task, and the rest is the task's own. Code
/// that reaches into another document without its window's context is not shared out, as Chromium
/// measures (a page task that runs a same-origin frame's code is the page's <c>self</c>).
/// </para>
/// <para>
/// <b>What is not the task's.</b> A fetch is made here while the task waits for it, where a browser's
/// fetch does not hold its event loop up, so the time a task waits on the network is nobody's
/// (<see cref="TaskClock"/>).
/// </para>
/// <para>
/// <b>Who is told.</b> The task's own document, as <c>self</c>, and each of its ancestors of the same
/// origin, as <c>same-origin-descendant</c>, with the frame element in that ancestor that leads to it. A
/// document of another origin is told nothing, as a browser that runs it in a process of its own tells
/// it nothing.
/// </para>
/// <para>
/// <b>Not in the performance timeline.</b> As in Chromium, <c>getEntriesByType('longtask')</c> answers
/// nothing: the entries go to the observers, and the first 200 are kept for a buffered one.
/// </para>
/// </remarks>
public sealed partial class DomBridge : Dom.Runtime.ITaskMonitor, ITaskClock
{
    // The Long Tasks API's threshold: a task longer than this is a long task.
    private const double LongTaskThresholdMs = 50;

    // A stretch of the task spent in one window -- the task's own at the bottom, each window entered
    // above -- or waiting on the network, which is no window's.
    private sealed class TaskSegment(DomElement? window, long start, bool waiting = false)
    {
        public DomElement? Window { get; } = window;
        public long Start { get; } = start;
        public bool Waiting { get; } = waiting;
        public TimeSpan Nested { get; set; }
    }

    // A window's share of a task: when it first had the task, and how long it had it.
    private sealed class TaskShare(long start)
    {
        public long Start { get; } = start;
        public TimeSpan Time { get; set; }
    }

    private int _taskDepth;
    private readonly Stack<TaskSegment> _taskSegments = new();
    private readonly List<(DomElement? Window, TaskShare Share)> _taskShares = [];
    private ITaskClock? _clockBeforeTask;
    private JsValue _longTaskEnded;

    // The segments are the page's thread's, and a fetch's wait may end on another while that thread
    // blocks for it: they are changed under this lock.
    private readonly Lock _taskGate = new();

    void Dom.Runtime.ITaskMonitor.TaskStarted()
    {
        if (_taskDepth++ != 0)
            return;

        lock (_taskGate)
        {
            _taskSegments.Clear();
            _taskShares.Clear();
            _taskSegments.Push(new TaskSegment(window: null, Stopwatch.GetTimestamp()));
        }

        _clockBeforeTask = TaskClock.Current;
        TaskClock.Current = this;
    }

    void Dom.Runtime.ITaskMonitor.TaskEnded()
    {
        if (_taskDepth == 0 || --_taskDepth != 0)
            return;

        TaskClock.Current = _clockBeforeTask;
        _clockBeforeTask = null;
        lock (_taskGate)
        {
            while (_taskSegments.Count > 0)
                EndSegment(Stopwatch.GetTimestamp());
        }

        if (!_disposed && _realm is { } realm && _longTaskEnded.IsFunction)
        {
            foreach (var (window, share) in _taskShares)
            {
                if (share.Time.TotalMilliseconds > LongTaskThresholdMs)
                    ReportLongTask(realm, RelativeToTimeOrigin(share.Start), Math.Round(share.Time.TotalMilliseconds), window);
            }
        }

        _taskShares.Clear();
    }

    /// <summary>A window's context was entered (<see cref="Dom.Runtime.WindowContextManager.WindowEntered"/>): its time is its own.</summary>
    private void TaskWindowEntered() => TaskWindowEntered(CurrentScriptFrame());

    /// <summary>The frame <paramref name="window"/> holds -- the page for <see langword="null"/> -- has the task now.</summary>
    private void TaskWindowEntered(DomElement? window)
    {
        if (_taskDepth == 0)
            return;

        lock (_taskGate)
            _taskSegments.Push(new TaskSegment(window, Stopwatch.GetTimestamp()));
    }

    /// <summary>
    /// Marks what follows, until the scope is disposed, as the work of the frame
    /// <paramref name="container"/> holds: loading its document or building its window, which a browser
    /// does in tasks of the frame's own.
    /// </summary>
    private FrameWorkScope FrameWork(DomElement container)
    {
        TaskWindowEntered(container);
        return new FrameWorkScope(this);
    }

    private sealed class FrameWorkScope(DomBridge bridge) : IDisposable
    {
        private DomBridge? _bridge = bridge;

        public void Dispose() => Interlocked.Exchange(ref _bridge, null)?.TaskWindowExited();
    }

    /// <summary>The window's context was left: back to whatever had the task before.</summary>
    private void TaskWindowExited()
    {
        if (_taskDepth == 0)
            return;

        lock (_taskGate)
        {
            if (_taskSegments.Count > 1)
                EndSegment(Stopwatch.GetTimestamp());
        }
    }

    void ITaskClock.Pause()
    {
        lock (_taskGate)
        {
            if (_taskSegments.Count > 0)
                _taskSegments.Push(new TaskSegment(window: null, Stopwatch.GetTimestamp(), waiting: true));
        }
    }

    void ITaskClock.Resume()
    {
        lock (_taskGate)
        {
            if (_taskSegments.TryPeek(out var top) && top.Waiting)
                EndSegment(Stopwatch.GetTimestamp());
        }
    }

    // Closes the innermost segment: its time less what it nested goes to its window, and all of it is
    // nested time of the segment around it.
    private void EndSegment(long end)
    {
        var segment = _taskSegments.Pop();
        var elapsed = Stopwatch.GetElapsedTime(segment.Start, end);
        if (_taskSegments.TryPeek(out var outer))
            outer.Nested += elapsed;
        if (segment.Waiting)
            return;

        var own = elapsed - segment.Nested;
        foreach (var (window, share) in _taskShares)
        {
            if (ReferenceEquals(window, segment.Window))
            {
                share.Time += own;
                return;
            }
        }

        _taskShares.Add((segment.Window, new TaskShare(segment.Start) { Time = own }));
    }

    /// <summary>
    /// Hands a long task to the timeline script, for its own document -- the page's, or the frame
    /// <paramref name="owner"/> holds -- and for that document's ancestors of the same origin.
    /// </summary>
    private void ReportLongTask(IJsRealm realm, double startTime, double duration, DomElement? owner)
    {
        var ownDocument = owner is null ? TopDocumentContext : FrameDocumentContext(owner);
        var reports = new List<JsValue> { LongTaskReport(realm, TimelineKeyOf(ownDocument), "self", container: null) };

        // Up through the frames, each container named to the document it is in.
        for (var container = owner; container is not null; container = GetFrameForContentDocument(GetOwningDocument(container)))
        {
            var ancestor = DocumentContextFor(container);
            if (ancestor.Origin.IsSameOrigin(ownDocument.Origin))
                reports.Add(LongTaskReport(realm, TimelineKeyOf(ancestor), "same-origin-descendant", container));
        }

        realm.Invoke(_longTaskEnded, JsValue.Undefined,
            [JsValue.Number(startTime), JsValue.Number(duration), realm.NewArray([.. reports])]);
    }

    /// <summary>
    /// One document's view of a long task: its timeline, the attribution's name, and the frame element
    /// it was attributed to in that document -- the window itself, with nothing to name, for its own.
    /// </summary>
    private static JsValue LongTaskReport(IJsRealm realm, int timeline, string name, DomElement? container)
    {
        var report = realm.NewObject();
        realm.DefineValue(report, "key", JsValue.Number(timeline));
        realm.DefineValue(report, "name", JsValue.String(name));
        realm.DefineValue(report, "containerType", JsValue.String(container?.TagName?.ToLowerInvariant() ?? "window"));
        realm.DefineValue(report, "containerSrc", JsValue.String(AttributeOf(container, "src")));
        realm.DefineValue(report, "containerId", JsValue.String(AttributeOf(container, "id")));
        realm.DefineValue(report, "containerName", JsValue.String(AttributeOf(container, "name")));
        return report;

        static string AttributeOf(DomElement? element, string name) =>
            element is not null && TryGetAttribute(element, name, out var value) ? value : string.Empty;
    }
}
