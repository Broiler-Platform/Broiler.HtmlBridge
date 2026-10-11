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
    /// <summary>
    /// How long one round of a drain waits for a worker to answer before it moves on: the whole of
    /// the worker's allowance, so the clock waits here as long as a window's step waits for it. A
    /// shorter round moved the clock on while the worker was still within its allowance.
    /// </summary>
    private static readonly TimeSpan WorkInFlightWait = Dom.Features.WorkerBinding.InFlightAllowance;

    /// <summary>
    /// Drains queued microtasks and timer tasks in bounded iterations until the
    /// bridge-backed execution environment settles or the iteration limit is reached.
    /// </summary>
    public static AsyncDrainStatus DrainUntilSettled(
        MicroTaskQueue microTasks,
        IDomBridgeRuntime bridge,
        Action? onBatchCompleted = null,
        CancellationToken cancellationToken = default,
        string callerName = "AsyncDrainOperations.DrainUntilSettled",
        double virtualTimeBudgetMs = DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs)
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

            // Workers retain their bounded allowance. Network I/O waits in short slices,
            // waking for completion or a page timer; its clock advances with real time.
            if (inFlight is { HasWorkInFlight: true, HasWorkDueNow: false })
                inFlight.AwaitWorkInFlight(WorkInFlightWait);

            if (bridge.HasPendingTimersDueBy(virtualTimeBudgetMs) ||
                inFlight is { HasWorkInFlight: true, HasWorkDueNow: true })
            {
                hadWork |= bridge.FlushTimerStep();
            }

            if (!hadWork && inFlight is { HasWorkInFlight: true })
            {
                // Waiting for real I/O is not a runaway JavaScript task.
                iteration--;
                continue;
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
