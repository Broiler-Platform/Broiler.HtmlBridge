using System;
using System.Threading;
using Broiler.HtmlBridge.Scripting;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The job queue a browsing context's script runs against: every promise reaction, <c>await</c>
/// resumption and <c>queueMicrotask</c> callback queued while it is current runs later on the host's
/// microtask queue, inside that browsing context's window context — and only while the document that
/// queued it is still the one its window shows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why jobs need to carry their window.</b> Every document of a page shares one realm here, and
/// which document a script speaks for is a window-context switch
/// (<see cref="WindowContextManager.RunWithWindowContext"/>) around the call, undone when the call
/// returns. A job the call queued runs after that: at the end of the outermost execution, or at the
/// host's next microtask checkpoint. It ran in the top document's context, so a cross-site frame
/// needed only a <c>queueMicrotask</c> or an <c>await</c> to read the embedding site's
/// <c>document.cookie</c> and to <c>fetch()</c> as the embedding document, with its cookies and a
/// readable same-origin response. HTML carries the incumbent settings object with each job
/// (HostMakeJobCallback) for exactly this reason.
/// </para>
/// <para>
/// <b>Why a job also carries its document.</b> HTML runs no job whose settings object's document is no
/// longer fully active. A frame navigated to another document keeps its container, and — keyed on
/// that container — its window would resolve to the new document: the old document's pending reaction
/// would run with the new one's <c>document</c>, cookies and fetch client, so a cross-site document's
/// work could resume as a same-origin one. <see cref="WindowContextManager.LivenessOf"/> captures which
/// document the window showed when the job was queued, and a job whose document has gone is dropped.
/// </para>
/// <para>
/// <b>How the engine is made to use it.</b> Which <see cref="SynchronizationContext"/> an engine treats
/// as its job queue is the engine's business: Broiler.JS takes one that carries its own marker
/// interface, and a promise, or an <c>await</c>, created while one is current keeps it for its
/// reactions. This type names no engine, so the script engine that drives the bridge wraps it in its
/// own marker (<see cref="IEngineJobs.AsJobQueue"/>, supplied by <c>ScriptEngine</c>), and the window
/// context switch makes that wrapper current for the length of the switch. A host whose engine
/// supplies no <see cref="IEngineJobs"/> still gets <c>queueMicrotask</c> attributed (it reads
/// <see cref="Active"/>), but its promise jobs run wherever that engine runs them: frame job
/// attribution depends on the engine.
/// </para>
/// <para>
/// <b>Where a job waits.</b> HTML has one microtask queue per event loop, so a frame's job queued while
/// script is running keeps its place among the page's own promise jobs, which the engine keeps in a
/// queue of its own and runs when the outermost execution ends. <see cref="Post"/> therefore offers
/// the job there (<see cref="IEngineJobs.TryOfferToRunningScript"/>), still wrapped in this window's
/// context and liveness check, and queues it on the host's queue as well: the engine takes the offer
/// only while an execution is in progress on this thread, and whichever copy reaches the page's
/// thread first runs the job (<see cref="OnceJob"/>). A job posted with no script running -- a host
/// task settling a promise -- therefore waits on the host's queue for its next checkpoint.
/// </para>
/// <para>
/// <b>What it errs towards.</b> A reaction the embedding page registered on a promise the frame
/// created runs as the frame: the capture is the promise's, and a job cannot be told apart by the
/// code it resumes. That direction takes privileges away rather than granting them; the opposite
/// capture would let the frame's reactions run as the page whenever the page settles them.
/// </para>
/// <para>
/// The queue is the host's (<c>ScriptEngine.MicroTasks</c>), which is drained on the thread that runs
/// the page, so posting from a thread-pool thread — a host task completing — never runs JavaScript
/// there.
/// </para>
/// </remarks>
internal sealed class WindowJobPump : SynchronizationContext
{
    [ThreadStatic]
    private static WindowJobPump? t_current;

    private readonly MicroTaskQueue _queue;
    private readonly WindowContextManager _windows;
    private readonly JsValue _window;
    private readonly Func<bool>? _isLive;
    private readonly IEngineJobs? _engine;

    // The thread that switched into the window: the one running the page's script, whose engine queue
    // a job may join. A post from any other thread goes to the host's queue only.
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;

    public WindowJobPump(
        MicroTaskQueue queue,
        WindowContextManager windows,
        JsValue window,
        Func<bool>? isLive = null,
        IEngineJobs? engine = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(windows);
        _queue = queue;
        _windows = windows;
        _window = window;
        _isLive = isLive;
        _engine = engine;
    }

    /// <summary>
    /// The pump of the window context switch in progress on this thread, or <see langword="null"/>
    /// outside one. Tracked here rather than read off <see cref="SynchronizationContext.Current"/>,
    /// which holds the engine's wrapper around the pump, or nothing when the engine supplies none.
    /// </summary>
    public static WindowJobPump? Active
    {
        get => t_current;
        internal set => t_current = value;
    }

    /// <summary>The window whose context every job posted here runs in.</summary>
    public JsValue Window => _window;

    /// <summary>Whether the document this pump's jobs belong to is still its window's document.</summary>
    public bool IsLive => _isLive is null || _isLive();

    /// <summary>
    /// Queues <paramref name="job"/> on the host's microtask queue to run in this pump's window context,
    /// unless by then the document that queued it is no longer the one its window shows. For work that
    /// is deferred past the running script by design (a frame's module roots); a microtask goes through
    /// <see cref="Queue"/>.
    /// </summary>
    public void Enqueue(Action job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _queue.Enqueue(() => RunNow(job));
    }

    /// <summary>
    /// Queues <paramref name="job"/> as a microtask of this pump's window: behind the jobs the running
    /// script has queued when one is running on this thread, on the host's queue otherwise. Either way
    /// it runs in this pump's window context, and not at all once its document has gone.
    /// </summary>
    public void Queue(Action job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var engine = Environment.CurrentManagedThreadId == _ownerThread ? _engine : null;
        OnceJob.Queue(_queue, engine, () => RunNow(job));
    }

    private void RunNow(Action job)
    {
        if (IsLive)
            _windows.RunWithWindowContext(_window, job);
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        Queue(() => d(state));
    }

    // Inline, as MicroTaskSynchronizationContext does: a caller asking to run synchronously is
    // already on the thread that runs the page.
    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        d(state);
    }

    // A copy must post to the same queue in the same window, not to a context nothing drains.
    public override SynchronizationContext CreateCopy() => this;
}
