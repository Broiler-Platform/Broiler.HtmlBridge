using Broiler.HtmlBridge.Logging;
using System;
using System.Collections.Generic;

namespace Broiler.HtmlBridge.Scripting;

/// <summary>
/// A simple micro-task queue that collects callbacks and drains them in
/// FIFO order. This models the micro-task queue defined by the
/// <see href="https://html.spec.whatwg.org/multipage/webappapis.html#microtask-queue">
/// HTML Living Standard</see>. Tasks enqueued during draining are processed
/// in the same drain cycle (like a real browser).
/// </summary>
public sealed class MicroTaskQueue
{
    private readonly Queue<Action> _queue = new();
    private bool _draining;

    /// <summary>Number of tasks currently queued (excludes tasks already being drained).</summary>
    public int Count => _queue.Count;

    /// <summary>
    /// Enqueue a micro-task. If the queue is currently draining, the task
    /// will execute before the current drain cycle completes.
    /// </summary>
    public void Enqueue(Action task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _queue.Enqueue(task);
    }

    /// <summary>
    /// Drain the queue, executing every task in FIFO order. Tasks added
    /// during draining are also executed. Exceptions are captured and
    /// returned so that one failing micro-task does not prevent subsequent
    /// tasks from running.
    /// </summary>
    /// <returns>List of exceptions thrown by individual tasks (empty on success).</returns>
    public IReadOnlyList<Exception> Drain()
    {
        if (_draining)
            return [];

        _draining = true;
        var errors = new List<Exception>();

        try
        {
            while (_queue.Count > 0)
            {
                var task = _queue.Dequeue();
                try
                {
                    task();
                }
                catch (Exception ex)
                {
                    RenderLogger.LogError(LogCategory.JavaScript, "MicroTaskQueue.Drain", $"Microtask failed: {ex.Message}", ex);
                    errors.Add(ex);
                }
            }
        }
        finally
        {
            _draining = false;
        }

        return errors;
    }
}
