using System;
using System.Threading;

namespace Broiler.HtmlBridge.Scripting;

/// <summary>
/// A <see cref="SynchronizationContext"/> that routes posted callbacks into a
/// <see cref="MicroTaskQueue"/> instead of the thread pool, so JavaScript stays single-threaded.
/// </summary>
/// <remarks>
/// <para>
/// The engine resolves a promise reaction's target with
/// <c>SynchronizationContext.Current ?? context.synchronizationContext</c> and falls back to
/// <c>ThreadPool.QueueUserWorkItem</c> when neither is set. Both fallbacks run the reaction
/// <em>concurrently with the thread that is driving the page</em>: an <c>await</c> continuation
/// resumed on a pool thread races the host, which then serializes, renders, or reads results before
/// the continuation has finished. Anything cheap appeared to work — the race was usually won — while
/// a continuation whose next step was expensive lost it. A geometry query (a full layout) reliably
/// lost, so <c>await something; el.getBoundingClientRect()</c> looked like it silently truncated the
/// rest of the function: no exception, no rejection, not even a <c>finally</c>, because the
/// continuation was still running on another thread when the page was captured.
/// </para>
/// <para>
/// Posting into the microtask queue instead makes a reaction a microtask, which is what the HTML
/// spec calls it, and it then runs on the driving thread at the checkpoints the host already has
/// (<c>TaskCheckpointCallback</c>, and the drain that follows each script phase). <see cref="Send"/>
/// stays synchronous — a caller asking to run inline is entitled to that, and the queue is not
/// reentrancy-safe for it.
/// </para>
/// <para>
/// Install it around script execution with <see cref="Install"/>, whose returned scope restores the
/// thread's previous context. It must be current on the thread while script runs, because a promise
/// captures its context when it is created, not when it settles.
/// </para>
/// </remarks>
public sealed class MicroTaskSynchronizationContext : SynchronizationContext
{
    private readonly MicroTaskQueue _queue;

    public MicroTaskSynchronizationContext(MicroTaskQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        _queue.Enqueue(() => d(state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        d(state);
    }

    // The engine may copy the context onto a captured execution context; the queue is shared, so the
    // copy must post to the same one rather than to a fresh queue nothing drains.
    public override SynchronizationContext CreateCopy() => this;

    /// <summary>
    /// Makes a context backed by <paramref name="queue"/> current on the calling thread until the
    /// returned scope is disposed.
    /// </summary>
    public static Scope Install(MicroTaskQueue queue) => new(queue);

    public readonly struct Scope : IDisposable
    {
        private readonly SynchronizationContext? _previous;

        internal Scope(MicroTaskQueue queue)
        {
            _previous = Current;
            SetSynchronizationContext(new MicroTaskSynchronizationContext(queue));
        }

        public void Dispose() => SetSynchronizationContext(_previous);
    }
}
