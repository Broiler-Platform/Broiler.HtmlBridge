using System;
using System.Threading;
using Broiler.HtmlBridge.Scripting;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// A microtask queued twice -- offered to the engine's own job queue behind the running script's jobs,
/// and queued on the host's -- that runs from whichever copy reaches it first.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why twice.</b> The page's promise jobs wait in the engine's queue, which runs when the outermost
/// execution ends; the bridge's microtasks (a frame's promise jobs, <c>queueMicrotask</c>) waited on the
/// host's queue, drained after it. So a frame's job, or a <c>queueMicrotask</c> callback, queued between
/// two of the page's promise jobs ran after both -- where HTML, with one microtask queue per event
/// loop, runs it between them. Offered to the engine's queue it keeps its place. But the engine takes
/// the offer only while an execution is in progress, and with none it hands the entry to another
/// thread, where it does nothing (<see cref="IEngineJobs.TryOfferToRunningScript"/>); which of the two
/// happened is not observable when the offer is made. The host's copy is what runs the job then, and
/// the first copy to run is the only one that does anything.
/// </para>
/// <para>
/// Both copies run on the page's thread, so the claim is never contended; it is interlocked because a
/// cheap guarantee is better than an argued one.
/// </para>
/// </remarks>
internal sealed class OnceJob
{
    private readonly Action _job;
    private int _ran;

    private OnceJob(Action job) => _job = job;

    /// <summary>
    /// Queues <paramref name="job"/> behind the running script's jobs when <paramref name="engine"/>
    /// takes it, and on <paramref name="queue"/> in any case, to run once.
    /// </summary>
    public static void Queue(MicroTaskQueue queue, IEngineJobs? engine, Action job)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(job);

        if (engine is null)
        {
            queue.Enqueue(job);
            return;
        }

        var once = new OnceJob(job);
        engine.TryOfferToRunningScript(once.Run);
        queue.Enqueue(once.Run);
    }

    private void Run()
    {
        if (Interlocked.Exchange(ref _ran, 1) == 0)
            _job();
    }
}
