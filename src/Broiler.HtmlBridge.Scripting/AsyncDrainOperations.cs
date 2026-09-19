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

        for (var iteration = 0; iteration < DomBridgeRuntimeLimits.AsyncDrainIterationLimit; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hadWork = false;

            if (microTasks.Count > 0)
            {
                microTasks.Drain();
                hadWork = true;
            }

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
