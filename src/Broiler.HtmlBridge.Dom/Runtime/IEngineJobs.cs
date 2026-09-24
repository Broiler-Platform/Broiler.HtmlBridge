using System;
using System.Threading;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// What the script engine that runs a page offers the bridge's job queues: the two things about
/// promise jobs that only the engine can do, behind a seam that names no engine. Set on the bridge by
/// the engine that drives it (<c>ScriptEngine</c>); a bridge without one still runs, and a frame's
/// promise jobs then run wherever that engine runs them (frame job attribution depends on the engine).
/// </summary>
internal interface IEngineJobs
{
    /// <summary>
    /// <paramref name="pump"/> as a <see cref="SynchronizationContext"/> the engine treats as its job
    /// queue: a promise job the engine posts while it is current, or to a promise created while it was,
    /// goes to the pump.
    /// </summary>
    SynchronizationContext AsJobQueue(WindowJobPump pump);

    /// <summary>
    /// Offers <paramref name="job"/> to the engine's own job queue, behind every job the script running
    /// on this thread has queued so far, so that it keeps its place among the page's promise jobs (HTML
    /// has one microtask queue per event loop). The engine drains that queue on the thread whose
    /// outermost execution ends; <paramref name="job"/> runs only if that is this thread.
    /// </summary>
    /// <remarks>
    /// Whether it runs at all is not knowable here: with no execution in progress the engine hands the
    /// entry to another thread, where it does nothing. So a caller always queues the job on the host's
    /// queue as well, and runs it from whichever copy reaches it first (<see cref="WindowJobPump"/>).
    /// </remarks>
    /// <returns><see langword="false"/> when nothing was offered: no script context is current here.</returns>
    bool TryOfferToRunningScript(Action job);
}
