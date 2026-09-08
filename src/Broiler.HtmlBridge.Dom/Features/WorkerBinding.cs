using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Logging;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Globals;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>Worker</c> — a document script running on its own thread, in a realm of its own, exchanging
/// structured-cloned messages with the page. Multithreading roadmap item #18.
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
/// <b>Delivery respects item #15.</b> Each realm is still driven by exactly one thread and one
/// event loop; nothing here dispatches JavaScript from a foreign thread. A worker's outbound message
/// is queued onto the page's <c>BrowserEventLoop</c> as a frame action — the queue is a
/// <c>ConcurrentDictionary</c>, so enqueuing from the worker thread is safe, and the page's own drain
/// runs the callback. Because pending frame actions count as pending work, a reply in flight keeps
/// the host's drain alive instead of racing the end of the document.
/// </para>
/// <para>
/// <b>Deliberately out of this slice</b>, and refused or absent rather than half-built: module
/// workers, <c>SharedWorker</c>, nested workers, and true zero-copy transfer (an <c>ArrayBuffer</c>
/// in a transfer list is copied and then detached). The worker global is <c>self</c>,
/// <c>postMessage</c>, <c>onmessage</c>/<c>addEventListener('message')</c>, the timers,
/// <c>importScripts</c>, <c>close()</c> and <c>console</c>.
/// </para>
/// <para>
/// <b>The page-side half is migrated to JSEAL; the message payload is not.</b> The <c>Worker</c>
/// handle, its events and the handler invocation all speak <see cref="IJsRealm"/>. What does not is
/// the structured clone, for the reason <see cref="MessagingBinding"/> records at greater length:
/// JSEAL declares no clone operation, and a clone's result may be a primitive, which a JSEAL handle
/// cannot carry (see <see cref="JsInterop"/>). So the payload is read from an engine argument frame
/// and travels to <see cref="JSWorker"/> as an engine value, and <see cref="JSWorker"/> — which
/// creates a second realm on a thread of its own, something JSEAL declares as
/// <see cref="JsCapabilities.WorkerRealms"/> but offers no way to <em>do</em> — stays engine-typed
/// whole.
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
    /// <remarks>
    /// The engine-typed signature is pinned by <c>DomBridge/Registration/Registration.cs</c>, which is
    /// not this round's to change; both objects it hands over cross as handles without being
    /// converted, because that is what a handle over an object already holds. The context is the
    /// realm's global under this engine, and it is written through as one.
    /// </remarks>
    public void Register(JSContext context, JSObject window)
    {
        // Not the `Realm` property, which throws: this runs during the registration pass that adopts
        // the realm, and a null here would mean the bridge called it in the wrong order.
        var realm = _host.Realm
            ?? throw new InvalidOperationException(
                "Worker cannot be registered before the bridge has adopted a JavaScript realm.");

        var ctor = realm.NewConstructor("Worker", CreateWorker, 1);
        realm.DefineValue(JsInterop.FromEngineObject(window), "Worker", ctor);
        realm.SetProperty(JsInterop.FromEngineObject(context), "Worker", ctor);
    }

    private JsValue CreateWorker(in JsCall call)
    {
        var realm = call.Realm;
        var specifier = call.Length > 0 ? realm.ToJsString(call[0]) : string.Empty;
        if (string.IsNullOrWhiteSpace(specifier))
            throw realm.DomError("SyntaxError", "Worker requires a script URL.");

        var script = _host.ResolveWorkerScript(specifier, baseDirectory: null);
        if (script is null)
        {
            // A worker whose script cannot be fetched fires `error` at the Worker object; it does
            // not throw from the constructor, and it must not take the page down.
            var failed = realm.NewObject();
            InstallWorkerHandle(realm, failed, worker: null);
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
            throw realm.DomError("AbortError", "The worker could not be started.");
        }

        lock (_sync)
        {
            if (_disposed)
            {
                worker.Terminate();
                return JsValue.Undefined;
            }

            _workers.Add(worker);
        }

        var handle = realm.NewObject();
        InstallWorkerHandle(realm, handle, worker);
        worker.Attach(handle, this);
        return handle;
    }

    /// <summary>The page-side <c>Worker</c> object: postMessage, terminate, onmessage/onerror.</summary>
    /// <remarks>
    /// <para>
    /// <b><c>terminate</c> and <c>addEventListener</c> are installed as constructors, and that is
    /// preservation rather than intent.</b> They were built with <c>JSFunction</c> rather than
    /// <c>DomFunction</c>, so each carries a <c>prototype</c> and <c>new w.terminate()</c> answers an
    /// object where WebIDL says it should be a <c>TypeError</c>. That is the same deviation
    /// <c>DomFunction</c> exists to fix, it is observable, and fixing it is not this change's
    /// business — so <see cref="IJsValues.NewConstructor"/> reproduces it exactly and the fix is
    /// reported instead.
    /// </para>
    /// <para>
    /// <c>postMessage</c> keeps its engine argument frame: it reads the payload the structured clone
    /// consumes, and the clone has no JSEAL expression. It is installed on the engine's object rather
    /// than through the realm so that the member order a page enumerates is the one it always was.
    /// </para>
    /// </remarks>
    private void InstallWorkerHandle(IJsRealm realm, JsValue handle, JSWorker? worker)
    {
        JsInterop.ToEngineObject(handle).FastAddValue(
            "postMessage",
            new JavaScript.BuiltIns.Function.JSFunction(
                (in a) => PostToWorker(realm, worker, in a), "postMessage", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        realm.DefineValue(handle, "terminate",
            realm.NewConstructor("terminate", (in _) => { worker?.Terminate(); return JsValue.Undefined; }));

        realm.DefineValue(handle, "onmessage", JsValue.Null);
        realm.DefineValue(handle, "onerror", JsValue.Null);

        // addEventListener is accepted for the two event types this slice fires, so page code
        // written the idiomatic way works rather than silently registering nothing.
        var listeners = new List<(string Type, JsValue Fn)>();
        realm.DefineValue(handle, "addEventListener",
            realm.NewConstructor("addEventListener", (in call) =>
            {
                if (call.Length >= 2 && call[1].IsFunction)
                    listeners.Add((call.Realm.ToJsString(call[0]), call[1]));
                return JsValue.Undefined;
            }, 2));

        _handleListeners[handle] = listeners;
    }

    private readonly ConcurrentDictionary<JsValue, List<(string Type, JsValue Fn)>> _handleListeners = new();

    /// <summary>
    /// <c>worker.postMessage(message, transfer)</c> — clone on the page's thread, then hand the
    /// unreachable intermediate to the worker.
    /// </summary>
    /// <remarks>
    /// Engine-typed for its payload, and only for that: the value is cloned here, on the page's
    /// thread with the page's context current, because the sending side is where
    /// <c>DataCloneError</c> belongs, where post-send mutation stops being visible to the receiver,
    /// and — for a transfer list — where the source buffers are detached.
    /// </remarks>
    private JSValue PostToWorker(IJsRealm realm, JSWorker? worker, in Arguments a)
    {
        if (worker is null)
            return JSUndefined.Value;

        var payload = a.Length > 0 ? a[0] : JSUndefined.Value;
        var transfer = WorkerTransfer.BuildCloneOptions(realm, a.Length > 1 ? a[1] : null);
        var detached = CloneDetached(payload, transfer);
        if (detached is null)
            throw realm.DomError("DataCloneError", "The object could not be cloned.");

        worker.Post(detached);
        return JSUndefined.Value;
    }

    /// <summary>
    /// Clones <paramref name="value"/> in the current realm into a graph no script holds a reference
    /// to. Returns <see langword="null"/> when the value is not cloneable.
    /// </summary>
    private static JSValue? CloneDetached(JSValue value, JSValue transferOptions)
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
    internal void DeliverToPage(JsValue handle, JSValue detached)
    {
        if (_host.Realm is not { } realm)
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

        var evt = realm.NewObject();
        realm.DefineValue(evt, "type", JsValue.String("message"));

        // `data` is the clone, which may be a primitive and so cannot cross as a handle; it is
        // installed on the engine's own object, in its original position.
        JsInterop.ToEngineObject(evt).FastAddValue("data", materialized, JSPropertyAttributes.EnumerableConfigurableValue);

        Invoke(realm, handle, "onmessage", "message", evt);
    }

    internal void FireErrorEvent(JsValue handle, string message)
    {
        if (_host.Realm is not { } realm)
            return;

        var evt = realm.NewObject();
        realm.DefineValue(evt, "type", JsValue.String("error"));
        realm.DefineValue(evt, "message", JsValue.String(message));
        Invoke(realm, handle, "onerror", "error", evt);
    }

    private void Invoke(IJsRealm realm, JsValue handle, string handlerProperty, string eventType, JsValue evt)
    {
        try
        {
            var handler = realm.GetProperty(handle, handlerProperty);
            if (handler.IsFunction)
                realm.Invoke(handler, handle, [evt]);

            if (_handleListeners.TryGetValue(handle, out var listeners))
            {
                foreach (var (type, fn) in listeners.ToArray())
                {
                    if (string.Equals(type, eventType, StringComparison.Ordinal))
                        realm.Invoke(fn, handle, [evt]);
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
        // cleared, and would keep a realm alive past the document that created it.
        foreach (var worker in workers)
            worker.Terminate();

        _handleListeners.Clear();
    }
}
