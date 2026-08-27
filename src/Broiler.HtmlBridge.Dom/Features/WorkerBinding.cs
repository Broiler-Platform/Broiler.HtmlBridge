using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Broiler.HtmlBridge.Logging;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Engine.Core;
using Broiler.JavaScript.Globals;
using Broiler.JavaScript.Runtime;

using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>Worker</c> — a document script running on its own thread, in its own
/// <see cref="JSContext"/>, exchanging structured-cloned messages with the page.
/// Multithreading roadmap item #18.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two facts this is built on were measured first.</b> <c>JSContextIsolationTests</c> showed
/// four contexts on four threads stay isolated under real overlap, and <c>--js-context-scaling</c>
/// showed they run genuinely in parallel (2.66×/3.22× at four threads) rather than serializing on a
/// global lock — the outcome that would have made a worker pointless.
/// <c>CrossContextStructuredCloneTests</c> then showed a clone taken with the receiving context
/// current produces receiver-realm objects sharing no identity with the sender's.
/// </para>
/// <para>
/// <b>Messages are cloned twice, and the second clone is the whole reason this is safe.</b> The
/// obvious design — clone once on the sender and hand the result over — puts one realm's object
/// graph in another thread's hands. The obvious fix — clone once on the receiver, from the sender's
/// live value — is worse: the sending script keeps running and can mutate that graph while the
/// receiver walks it, which is a data race on engine internals.
/// </para>
/// <para>
/// So a message is cloned on the <em>sending</em> thread into a graph that no script can reach (that
/// clone is also what makes post-send mutation invisible, as the messaging model requires, and what
/// raises <c>DataCloneError</c> at the right moment), and cloned again on the <em>receiving</em>
/// thread, with the receiving context current, into that realm. The intermediate is unreachable from
/// either side's script, so nothing can mutate it while it is read. Both clones are the engine's own
/// <c>structuredClone</c>: reimplementing the algorithm would have meant a second definition of
/// which types survive, and it would have drifted.
/// </para>
/// <para>
/// <b>Delivery respects item #15.</b> Each context is still driven by exactly one thread and one
/// event loop; nothing here dispatches JavaScript from a foreign thread. A worker's outbound message
/// is queued onto the page's <c>BrowserEventLoop</c> as a frame action — the queue is a
/// <c>ConcurrentDictionary</c>, so enqueuing from the worker thread is safe, and the page's own drain
/// runs the callback. Because pending frame actions count as pending work, a reply in flight keeps
/// the host's drain alive instead of racing the end of the document.
/// </para>
/// <para>
/// <b>Deliberately out of this slice</b>, and refused or absent rather than half-built: timers inside
/// a worker, <c>importScripts</c>, module workers, <c>SharedWorker</c>, nested workers, and
/// transferables (an <c>ArrayBuffer</c> in a transfer list is cloned, not transferred). The worker
/// global is <c>self</c>, <c>postMessage</c>, <c>onmessage</c>/<c>addEventListener('message')</c>,
/// <c>close()</c> and <c>console</c>.
/// </para>
/// </remarks>
internal sealed class WorkerBinding : IDisposable
{
    private readonly IWorkerHost _host;
    private readonly List<JSWorker> _workers = [];
    private readonly object _sync = new();
    private bool _disposed;

    public WorkerBinding(IWorkerHost host) => _host = host;

    /// <summary>Installs the <c>Worker</c> constructor on <paramref name="window"/> and the context.</summary>
    public void Register(JSContext context, JSObject window)
    {
        var ctor = new JSFunction((in a) => CreateWorker(in a), "Worker", 1);
        window.FastAddValue("Worker", ctor, JSPropertyAttributes.EnumerableConfigurableValue);
        context["Worker"] = ctor;
    }

    /// <summary>
    /// Raises a DOM exception in the page context and yields a value for the caller to return.
    /// Tolerates a null context (before attach), where throwing is impossible and the operation is
    /// simply a no-op.
    /// </summary>
    private JSValue Reject(string message, string name)
    {
        if (_host.JsContext is { } context)
            DomBridge.ThrowDOMException(context, message, name);

        return JSUndefined.Value;
    }

    private JSValue CreateWorker(in Arguments a)
    {
        var specifier = a.Length > 0 ? a[0]?.ToString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(specifier))
            return Reject("Worker requires a script URL.", "SyntaxError");

        var script = _host.ResolveWorkerScript(specifier, baseDirectory: null);
        if (script is null)
        {
            // A worker whose script cannot be fetched fires `error` at the Worker object; it does
            // not throw from the constructor, and it must not take the page down.
            var failed = new JSObject();
            InstallWorkerHandle(failed, worker: null);
            _host.QueueFrameAction(() => FireErrorEvent(failed, $"Worker script not found: {specifier}"));
            return failed;
        }

        JSWorker worker;
        try
        {
            worker = new JSWorker(specifier, script.Value, _host);
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "WorkerBinding.CreateWorker",
                $"Could not start worker '{specifier}': {ex.Message}", ex);
            return Reject("The worker could not be started.", "AbortError");
        }

        lock (_sync)
        {
            if (_disposed)
            {
                worker.Terminate();
                return JSUndefined.Value;
            }

            _workers.Add(worker);
        }

        var handle = new JSObject();
        InstallWorkerHandle(handle, worker);
        worker.Attach(handle, this);
        return handle;
    }

    /// <summary>The page-side <c>Worker</c> object: postMessage, terminate, onmessage/onerror.</summary>
    private void InstallWorkerHandle(JSObject handle, JSWorker? worker)
    {
        handle.FastAddValue(
            "postMessage",
            new JSFunction((in a) =>
            {
                if (worker is null)
                    return JSUndefined.Value;

                var payload = a.Length > 0 ? a[0] : JSUndefined.Value;

                // Cloned here, on the page's thread with the page's context current: the sending
                // side is where DataCloneError belongs, where post-send mutation stops being visible
                // to the receiver, and — for a transfer list — where the source buffers are detached.
                var transfer = _host.JsContext is { } pageContext
                    ? WorkerTransfer.BuildCloneOptions(pageContext, a.Length > 1 ? a[1] : null)
                    : JSUndefined.Value;
                var detached = CloneDetached(payload, transfer);
                if (detached is null)
                    return Reject("The object could not be cloned.", "DataCloneError");

                worker.Post(detached);
                return JSUndefined.Value;
            }, "postMessage", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        handle.FastAddValue(
            "terminate",
            new JSFunction((in _) => { worker?.Terminate(); return JSUndefined.Value; }, "terminate", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        handle.FastAddValue("onmessage", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
        handle.FastAddValue("onerror", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);

        // addEventListener is accepted for the two event types this slice fires, so page code
        // written the idiomatic way works rather than silently registering nothing.
        var listeners = new List<(string Type, JSFunction Fn)>();
        handle.FastAddValue(
            "addEventListener",
            new JSFunction((in a) =>
            {
                if (a.Length >= 2 && a[1] is JSFunction fn)
                    listeners.Add((a[0]?.ToString() ?? string.Empty, fn));
                return JSUndefined.Value;
            }, "addEventListener", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        _handleListeners[handle] = listeners;
    }

    private readonly ConcurrentDictionary<JSObject, List<(string Type, JSFunction Fn)>> _handleListeners = new();

    /// <summary>
    /// Clones <paramref name="value"/> in the current realm into a graph no script holds a reference
    /// to. Returns <see langword="null"/> when the value is not cloneable.
    /// </summary>
    private JSValue? CloneDetached(JSValue value, JSValue transferOptions)
    {
        try
        {
            return transferOptions.IsNullOrUndefined
                ? JSGlobalStatic.StructuredClone(new Arguments(JSUndefined.Value, value))
                : JSGlobalStatic.StructuredClone(new Arguments(JSUndefined.Value, value, transferOptions));
        }
        catch (JSException ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "WorkerBinding.CloneDetached",
                $"Value could not be cloned for a worker message: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// Delivers a worker's message to the page. Runs on the page thread, from the page's own drain.
    /// </summary>
    internal void DeliverToPage(JSObject handle, JSValue detached)
    {
        var context = _host.JsContext;
        if (context is null)
            return;

        // Second clone, with the page's context current, so the page gets page-realm objects.
        JSValue materialized;
        try
        {
            materialized = JSGlobalStatic.StructuredClone(new Arguments(JSUndefined.Value, detached));
        }
        catch (JSException ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "WorkerBinding.DeliverToPage",
                $"A worker message could not be materialized in the page realm: {ex.Message}", ex);
            return;
        }

        var evt = new JSObject();
        evt.FastAddValue("type", new JSString("message"), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("data", materialized, JSPropertyAttributes.EnumerableConfigurableValue);

        Invoke(handle, "onmessage", "message", evt);
    }

    internal void FireErrorEvent(JSObject handle, string message)
    {
        var evt = new JSObject();
        evt.FastAddValue("type", new JSString("error"), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("message", new JSString(message), JSPropertyAttributes.EnumerableConfigurableValue);
        Invoke(handle, "onerror", "error", evt);
    }

    private void Invoke(JSObject handle, string handlerProperty, string eventType, JSObject evt)
    {
        try
        {
            if (handle[(KeyString)handlerProperty] is JSFunction handler)
                handler.InvokeFunction(new Arguments(handle, evt));

            if (_handleListeners.TryGetValue(handle, out var listeners))
            {
                foreach (var (type, fn) in listeners.ToArray())
                {
                    if (string.Equals(type, eventType, StringComparison.Ordinal))
                        fn.InvokeFunction(new Arguments(handle, evt));
                }
            }
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "WorkerBinding.Invoke",
                $"A worker '{eventType}' handler threw: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        List<JSWorker> workers;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            workers = [.. _workers];
            _workers.Clear();
        }

        // Every worker thread is stopped and joined before the bridge finishes tearing down.
        // Leaving one running would let it queue a frame action onto an event loop that is being
        // cleared, and would keep a JSContext alive past the document that created it.
        foreach (var worker in workers)
            worker.Terminate();

        _handleListeners.Clear();
    }
}
