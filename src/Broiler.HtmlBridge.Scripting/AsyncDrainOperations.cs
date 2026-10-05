using System;
using System.Threading;
using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge.Scripting;

internal enum AsyncDrainStatus
{
    Settled,
    Exhausted
}

internal static class AsyncDrainOperations
{
    /// <summary>How long one round of a drain waits for a worker to answer before it moves on.</summary>
    private static readonly TimeSpan WorkInFlightWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Drains queued microtasks and timer tasks in bounded iterations until the
    /// bridge-backed execution environment settles or the iteration limit is reached.
    /// </summary>
    public static AsyncDrainStatus DrainUntilSettled(
        MicroTaskQueue microTasks,
        IDomBridgeRuntime bridge,
        Action? onBatchCompleted = null,
        CancellationToken cancellationToken = default,
        string callerName = "AsyncDrainOperations.DrainUntilSettled")
    {
        ArgumentNullException.ThrowIfNull(microTasks);
        ArgumentNullException.ThrowIfNull(bridge);

        var inFlight = bridge as Dom.Runtime.IWorkInFlight;

        for (var iteration = 0; iteration < DomBridgeRuntimeLimits.AsyncDrainIterationLimit; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hadWork = false;

            if (microTasks.Count > 0)
            {
                microTasks.Drain();
                hadWork = true;
            }

            // Before the virtual clock moves on, a worker working for the page finishes: in a browser it
            // answers in milliseconds, long before a page timer of any length. Its answer is queued on
            // the event loop by the time the wait ends, and the step below delivers it at the current
            // time. Bounded per piece of work (WorkerBinding.InFlightAllowance), so a stuck worker holds
            // the drain once.
            if (inFlight is { HasWorkInFlight: true, HasWorkDueNow: false })
                inFlight.AwaitWorkInFlight(WorkInFlightWait);

            if (bridge.HasPendingTimersDueBy(DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs))
            {
                bridge.FlushTimerStep();
                hadWork = true;
            }

            if (!hadWork)
                return AsyncDrainStatus.Settled;

            onBatchCompleted?.Invoke();
        }

        RenderLogger.LogWarning(LogCategory.JavaScript, callerName,
            $"Async work still due after {DomBridgeRuntimeLimits.AsyncDrainIterationLimit} drain iterations; " +
            $"stopping with pending microtasks={microTasks.Count}. " +
            "A callback is rescheduling itself with no delay, so the virtual clock cannot advance.");

        return AsyncDrainStatus.Exhausted;
    }
}
