using System;
using System.Collections.Concurrent;
using System.Threading;
using Broiler.HtmlBridge.Logging;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Globals;
using Broiler.JavaScript.Runtime;

using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// One worker: a thread that owns a <see cref="JSContext"/>, runs the worker script in it, and then
/// pumps messages until it is closed or terminated.
/// </summary>
/// <remarks>
/// <para>
/// <b>One thread, one context, for the thread's whole life.</b> That is item #15's rule kept rather
/// than bent: the context is created on the worker thread, every script and every handler runs on
/// that thread, and it is disposed there. Nothing outside ever evaluates in it. The page and the
/// worker meet only at two concurrent queues.
/// </para>
/// <para>
/// <b>Blocking take, not a spin.</b> The pump waits on a <see cref="BlockingCollection{T}"/>, so an
/// idle worker costs nothing; termination completes the collection, which wakes the pump and ends it.
/// </para>
/// </remarks>
internal sealed class JSWorker
{
    private readonly string _name;
    private readonly WorkerScript _script;
    private readonly IWorkerHost _host;
    private readonly BlockingCollection<JSValue> _inbox = new(new ConcurrentQueue<JSValue>());
    private readonly CancellationTokenSource _cancel = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();

    private readonly WorkerTimers _timers = new();

    private JSObject? _handle;
    private WorkerBinding? _owner;
    private volatile bool _closed;

    public JSWorker(string name, WorkerScript script, IWorkerHost host)
    {
        _name = name;
        _script = script;
        _host = host;
        _thread = new Thread(Pump) { IsBackground = true, Name = $"broiler-worker:{name}" };
    }

    /// <summary>
    /// Binds the page-side handle and starts the thread. Separate from the constructor because the
    /// handle does not exist until the binding has built it, and the thread must not deliver a
    /// message before there is something to deliver it to.
    /// </summary>
    public void Attach(JSObject handle, WorkerBinding owner)
    {
        _handle = handle;
        _owner = owner;
        _thread.Start();
    }

    /// <summary>Queues an already-detached clone for the worker. Called on the page thread.</summary>
    public void Post(JSValue detached)
    {
        if (_closed || _cancel.IsCancellationRequested)
            return;

        try
        {
            _inbox.Add(detached);
        }
        catch (InvalidOperationException)
        {
            // The inbox was completed by a concurrent Terminate; the message is simply not delivered,
            // which is what terminate() means.
        }
    }

    public void Terminate()
    {
        if (_cancel.IsCancellationRequested)
            return;

        _cancel.Cancel();
        try { _inbox.CompleteAdding(); } catch (ObjectDisposedException) { }

        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(5)))
        {
            // A worker stuck in an infinite loop cannot be interrupted safely; say so rather than
            // block the page's teardown forever. The thread is a background thread, so it does not
            // keep the process alive.
            RenderLogger.LogWarning(LogCategory.JavaScript, "JSWorker.Terminate",
                $"Worker '{_name}' did not stop within 5s; abandoning the thread.");
        }
    }

    private void Pump()
    {
        JSContext? context = null;
        try
        {
            context = new JSContext();
            InstallWorkerGlobals(context);

            try
            {
                context.Eval(_script.Source);
            }
            catch (Exception ex)
            {
                RenderLogger.LogError(LogCategory.JavaScript, "JSWorker.Pump",
                    $"Worker '{_name}' script threw: {ex.Message}", ex);
                QueueError($"Worker script error: {ex.Message}");
                return;
            }

            _started.Set();

            // The pump waits for whichever comes first: an inbound message, or the next timer
            // deadline. A plain blocking take would sleep through every timer; a poll would burn a
            // core. TryTake's timeout is exactly "time until the next deadline", so an idle worker
            // with no timers blocks indefinitely and one with timers wakes only when it must.
            while (!_cancel.IsCancellationRequested && !_closed)
            {
                var untilNext = _timers.TimeUntilNext();
                var waitMs = untilNext is null
                    ? Timeout.Infinite
                    : (int)Math.Min(int.MaxValue, Math.Ceiling(untilNext.Value));

                bool took;
                JSValue? detached;
                try
                {
                    took = _inbox.TryTake(out detached, waitMs, _cancel.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    // CompleteAdding raced us and the collection is drained: close() or terminate().
                    break;
                }

                if (took && detached is not null)
                    DispatchToWorker(context, detached);

                // Always after the take, whether it produced a message or timed out: a message that
                // arrives just before a deadline must not postpone the timer past it.
                _timers.RunDue();
            }
        }
        catch (OperationCanceledException)
        {
            // terminate(): the expected way out.
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "JSWorker.Pump",
                $"Worker '{_name}' failed: {ex.Message}", ex);
        }
        finally
        {
            _started.Set();
            _timers.ClearAll();
            context?.Dispose();
        }
    }

    /// <summary>
    /// Materializes the page's detached clone into the worker's realm and calls its handler. Runs on
    /// the worker thread, with the worker's context current, which is what makes the resulting
    /// objects the worker's own.
    /// </summary>
    private void DispatchToWorker(JSContext context, JSValue detached)
    {
        try
        {
            var data = JSGlobalStatic.StructuredClone(new Arguments(JSUndefined.Value, detached));

            var evt = new JSObject();
            evt.FastAddValue("type", new JSString("message"), JSPropertyAttributes.EnumerableConfigurableValue);
            evt.FastAddValue("data", data, JSPropertyAttributes.EnumerableConfigurableValue);

            if (context["onmessage"] is JSFunction handler)
                handler.InvokeFunction(new Arguments(JSUndefined.Value, evt));

            if (context["__broilerWorkerListeners"] is JSArray listeners)
            {
                foreach (var (_, listener) in listeners.GetArrayElements(withHoles: false))
                {
                    if (listener is JSFunction fn)
                        fn.InvokeFunction(new Arguments(JSUndefined.Value, evt));
                }
            }
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "JSWorker.DispatchToWorker",
                $"Worker '{_name}' message handler threw: {ex.Message}", ex);
            QueueError($"Worker message handler error: {ex.Message}");
        }
    }

    /// <summary>
    /// The worker global. Small on purpose — see <see cref="WorkerBinding"/>'s remarks for what is
    /// deliberately absent rather than half-built.
    /// </summary>
    private void InstallWorkerGlobals(JSContext context)
    {
        // A real DOMException, so a worker catching a NetworkError or DataCloneError finds a .name
        // and .code to branch on rather than the bare string ThrowDOMException falls back to.
        DomBridge.RegisterDOMException(context);

        context["self"] = context.Eval("this");
        context["onmessage"] = JSNull.Value;
        context["__broilerWorkerListeners"] = new JSArray();

        context["postMessage"] = new JSFunction((in a) =>
        {
            var payload = a.Length > 0 ? a[0] : JSUndefined.Value;

            // Cloned here, on the worker thread in the worker's realm, into a graph no script holds.
            // The page clones it again on its own thread; see WorkerBinding's remarks. A transfer
            // list detaches the worker's own buffers, which is why it is applied on this side.
            JSValue detached;
            try
            {
                var transfer = WorkerTransfer.BuildCloneOptions(context, a.Length > 1 ? a[1] : null);
                detached = transfer.IsNullOrUndefined
                    ? JSGlobalStatic.StructuredClone(new Arguments(JSUndefined.Value, payload))
                    : JSGlobalStatic.StructuredClone(new Arguments(JSUndefined.Value, payload, transfer));
            }
            catch (JSException ex)
            {
                RenderLogger.LogWarning(LogCategory.JavaScript, "JSWorker.postMessage",
                    $"Worker '{_name}' posted a value that could not be cloned: {ex.Message}", ex);
                return JSUndefined.Value;
            }

            var handle = _handle;
            var owner = _owner;
            if (handle is null || owner is null || _cancel.IsCancellationRequested)
                return JSUndefined.Value;

            // Onto the page's event loop, not into it: the queue is concurrent, and the page's own
            // drain is what will run this.
            _host.QueueFrameAction(() => owner.DeliverToPage(handle, detached));
            return JSUndefined.Value;
        }, "postMessage", 1);

        context["addEventListener"] = new JSFunction((in a) =>
        {
            if (a.Length >= 2 && a[1] is JSFunction fn &&
                string.Equals(a[0]?.ToString(), "message", StringComparison.Ordinal) &&
                context["__broilerWorkerListeners"] is JSArray listeners)
            {
                listeners.AddArrayItem(fn);
            }

            return JSUndefined.Value;
        }, "addEventListener", 2);

        context["setTimeout"] = new JSFunction((in a) => new JSNumber(
            _timers.Add(a.Length > 0 ? a[0] as JSFunction : null, DelayOf(a), repeating: false)), "setTimeout", 2);

        context["setInterval"] = new JSFunction((in a) => new JSNumber(
            _timers.Add(a.Length > 0 ? a[0] as JSFunction : null, DelayOf(a), repeating: true)), "setInterval", 2);

        // One id space, and clearTimeout/clearInterval interchangeable, per the HTML spec — the same
        // contract the page's loop keeps.
        var clear = new JSFunction((in a) =>
        {
            if (a.Length > 0 && a[0] is { } id && !id.IsNullOrUndefined)
                _timers.Clear((int)id.DoubleValue);

            return JSUndefined.Value;
        }, "clearTimeout", 1);
        context["clearTimeout"] = clear;
        context["clearInterval"] = clear;

        // importScripts(...urls): fetch and run each in order, synchronously, in this global.
        // Specifiers resolve against the worker's own script directory, which is what the HTML spec
        // means by "relative to the worker's script URL" — not the document's base path.
        context["importScripts"] = new JSFunction((in a) =>
        {
            for (var i = 0; i < a.Length; i++)
            {
                var specifier = a[i]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(specifier))
                    continue;

                var imported = _host.ResolveWorkerScript(specifier, _script.BaseDirectory);
                if (imported is null)
                {
                    // Spec: a script that cannot be fetched is a NetworkError, and it aborts the
                    // whole call — later specifiers in the same call do not run.
                    DomBridge.ThrowDOMException(context, $"importScripts: could not load '{specifier}'.", "NetworkError");
                    return JSUndefined.Value;
                }

                // Deliberately not wrapped: a throwing imported script propagates to the caller,
                // exactly as an inline one would. Swallowing it here would leave the worker running
                // with a half-initialised global and no way to find out.
                context.Eval(imported.Value.Source);
            }

            return JSUndefined.Value;
        }, "importScripts", 1);

        context["close"] = new JSFunction((in _) =>
        {
            _closed = true;
            // A closing worker stops its timers; leaving them would keep the pump awake past close().
            _timers.ClearAll();
            try { _inbox.CompleteAdding(); } catch (ObjectDisposedException) { }
            return JSUndefined.Value;
        }, "close", 0);

        // A worker without console is a worker nobody can debug; routed to the same logger the rest
        // of the bridge uses rather than to the page's console object, which belongs to another realm.
        var console = new JSObject();
        foreach (var level in new[] { "log", "info", "warn", "error", "debug" })
        {
            var captured = level;
            console.FastAddValue(
                captured,
                new JSFunction((in a) =>
                {
                    var text = a.Length > 0 ? a[0]?.ToString() ?? string.Empty : string.Empty;
                    RenderLogger.LogDebug(LogCategory.JavaScript, $"worker:{_name}", $"[{captured}] {text}");
                    return JSUndefined.Value;
                }, captured, 1),
                JSPropertyAttributes.EnumerableConfigurableValue);
        }

        context["console"] = console;
    }

    /// <summary>The delay argument of a timer call, defaulting to 0 when absent or not a number.</summary>
    private static double DelayOf(in Arguments a) =>
        a.Length > 1 && a[1] is { } delay && !delay.IsNullOrUndefined ? delay.DoubleValue : 0;

    private void QueueError(string message)
    {
        var handle = _handle;
        var owner = _owner;
        if (handle is null || owner is null)
            return;

        _host.QueueFrameAction(() => owner.FireErrorEvent(handle, message));
    }
}
