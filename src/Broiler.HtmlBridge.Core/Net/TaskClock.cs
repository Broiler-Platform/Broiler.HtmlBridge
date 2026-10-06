using System;

namespace Broiler.HtmlBridge;

/// <summary>
/// The clock of the task a thread is running for a document, which a wait on the network pauses: a
/// browser's fetch does not hold up its event loop, so the time a blocking fetch here waits is not the
/// task's, and a long task is not made of it.
/// </summary>
/// <remarks>
/// Thread-static, because the task is the thread's: the page's thread has one while it runs a task,
/// and the threads that prefetch or run workers have none, so their fetches pause nothing.
/// </remarks>
internal static class TaskClock
{
    [ThreadStatic] private static ITaskClock? t_current;

    /// <summary>The clock of the task this thread is running, or <see langword="null"/> for none.</summary>
    internal static ITaskClock? Current
    {
        get => t_current;
        set => t_current = value;
    }

    /// <summary>
    /// Pauses the running task's clock until the scope is disposed, on whichever thread that is: the
    /// wait on the network an awaited fetch ends after.
    /// </summary>
    internal static WaitScope Waiting()
    {
        var clock = t_current;
        clock?.Pause();
        return new WaitScope(clock);
    }

    /// <summary>A wait on the network, which <see cref="Dispose"/> ends.</summary>
    internal readonly struct WaitScope(ITaskClock? clock) : IDisposable
    {
        public void Dispose() => clock?.Resume();
    }
}

/// <summary>A task's clock, which a wait on the network stops and starts again.</summary>
internal interface ITaskClock
{
    /// <summary>A wait is starting: what follows is not the task's time.</summary>
    void Pause();

    /// <summary>The wait <see cref="Pause"/> began is over.</summary>
    void Resume();
}
