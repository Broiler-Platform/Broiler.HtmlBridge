namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// Told when a task begins and when it ends: a timer's or an animation frame's callback, a queued
/// host action, a page script, a user's input -- each with the microtask checkpoint after it, which
/// HTML counts as part of the task.
/// </summary>
/// <remarks>
/// <para>
/// What the Long Tasks API reports is measured between the two (DomBridge/LongTasks.cs). Tasks nest
/// here where a browser's do not -- a frame's document loads, and its scripts run, inside the task of
/// the page script that reached it -- so only the outermost pair is a task.
/// </para>
/// <para>
/// Internal, and implemented by the bridge alone, as <see cref="IWorkInFlight"/> is: the public
/// <c>IDomBridgeRuntime</c> stays as it is, and what runs a task finds it by asking the bridge it holds.
/// </para>
/// </remarks>
internal interface ITaskMonitor
{
    /// <summary>A task is starting.</summary>
    void TaskStarted();

    /// <summary>The task <see cref="TaskStarted"/> started has ended.</summary>
    void TaskEnded();
}
