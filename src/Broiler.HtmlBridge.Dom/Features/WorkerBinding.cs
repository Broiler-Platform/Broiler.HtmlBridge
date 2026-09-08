using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>Worker</c> — a document script running on its own thread, in a realm of its own, exchanging
/// structured-cloned messages with the page. Multithreading roadmap item #18.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two facts this is built on were measured first</b>, by the engine's own concurrency suite
/// (Broiler.JS, <c>docs/roadmap/Concurrency.status.md</c>). Its context-isolation cases showed
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
/// <b>Both halves speak JSEAL now, including the message itself.</b> The <c>Worker</c> handle, its
/// events, the handler invocation, the transfer list and both clones are <see cref="IJsRealm"/>
/// operations. The payload crosses the two threads as a <see cref="JsDetachedValue"/> — the carrier
/// <see cref="IJsClone"/> declares for exactly this, because a <see cref="JsValue"/> handle is only
/// meaningful to the realm that minted it and a clone's result may be a primitive, which a handle
/// cannot carry. <see cref="JSWorker"/> builds its second realm through
/// <see cref="IJsEngineProvider.CreateRealm"/>, which is what
/// <see cref="JsCapabilities.WorkerRealms"/> claims and now also describes.
/// </para>
/// <para>
/// No engine type is named here. The last one was <see cref="Register"/>, which took the script
/// context and the window object from the registration hub; it takes the realm and the window handle
/// now, and the two writes it makes go to the same two objects they always did.
/// </para>
/// </remarks>
internal sealed class WorkerBinding : IDisposable
{
    private readonly IWorkerHost _host;
    private readonly List<JSWorker> _workers = [];
    private readonly object _sync = new();
    private bool _disposed;

    public WorkerBinding(IWorkerHost host) => _host = host;

    /// <summary>Installs the <c>Worker</c> constructor on <paramref name="window"/> and the global.</summary>
    /// <remarks>
    /// Two writes, kept as two: the constructor is defined on <paramref name="window"/> and set on
    /// the realm's global, which is what the engine-typed form did with the window object and the
    /// script context. Under a realm whose global <em>is</em> the window those are one object and the
    /// second write is a no-op on the same value; under one where they are distinct, both spellings
    /// still resolve, which is what the pair was for.
    /// </remarks>
    public void Register(IJsRealm realm, JsValue window)
    {
        var ctor = realm.NewConstructor("Worker", CreateWorker, 1);
        realm.DefineValue(window, "Worker", ctor);
        realm.SetProperty(realm.Global, "Worker", ctor);
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

        // The worker runs on the same engine the page does, and it has to: the two realms exchange
        // structured clones, and a JsDetachedValue carries the engine that minted it precisely so
        // that a graph cannot be adopted into a realm that cannot walk it. Asking the registry by the
        // page realm's own engine name is what keeps the pair honest in a process with two providers
        // registered.
        var provider = JsEngineRegistry.Find(realm.EngineName);

        JSWorker worker;
        try
        {
            worker = new JSWorker(
                specifier,
                script.Value,
                _host,
                provider ?? throw new InvalidOperationException(
                    $"No JavaScript engine provider is registered under '{realm.EngineName}', so a " +
                    "worker realm cannot be created for the realm the page is running in."));
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
    /// preservation rather than intent.</b> They were built with the engine's constructable function
    /// type rather than the bridge's non-constructable one, so each carries a <c>prototype</c> and
    /// <c>new w.terminate()</c> answers an object where WebIDL says it should be a <c>TypeError</c>.
    /// That is the same deviation <see cref="IJsValues.NewMethod"/> exists to fix, it is observable,
    /// and fixing it is not this change's business — so <see cref="IJsValues.NewConstructor"/>
    /// reproduces it exactly and the fix is reported instead.
    /// </para>
    /// <para>
    /// <c>postMessage</c> is a constructor for the same reason and installed first for another: the
    /// member order a page enumerates is observable, and it was first before.
    /// </para>
    /// </remarks>
    private void InstallWorkerHandle(IJsRealm realm, JsValue handle, JSWorker? worker)
    {
        realm.DefineValue(handle, "postMessage",
            realm.NewConstructor("postMessage", (in call) => PostToWorker(worker, in call), 1));

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
    /// <para>
    /// The clone happens here, on the page's thread and in the page's realm, because the sending side
    /// is where <c>DataCloneError</c> belongs, where post-send mutation stops being visible to the
    /// receiver, and — for a transfer list — where the source buffers are detached. What travels is a
    /// <see cref="JsDetachedValue"/>: a graph belonging to no realm, which is the only shape that may
    /// cross to the worker's thread.
    /// </para>
    /// <para>
    /// The realm is <see cref="JsCall.Realm"/> rather than the one captured when the handle was
    /// built, which is the same realm and is now said by the call instead of by a closure.
    /// </para>
    /// </remarks>
    private static JsValue PostToWorker(JSWorker? worker, in JsCall call)
    {
        if (worker is null)
            return JsValue.Undefined;

        var realm = call.Realm;
        var transfer = WorkerTransfer.BuildTransferList(realm, call.Length > 1 ? call[1] : JsValue.Undefined);

        JsDetachedValue detached;
        try
        {
            detached = realm.Detach(call.Length > 0 ? call[0] : JsValue.Undefined, transfer);
        }
        catch (JsEngineException ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "WorkerBinding.PostToWorker",
                $"Value could not be cloned for a worker message: {ex.Message}", ex);
            throw realm.DomError("DataCloneError", "The object could not be cloned.");
        }

        worker.Post(detached);
        return JsValue.Undefined;
    }

    /// <summary>
    /// Delivers a worker's message to the page. Runs on the page thread, from the page's own drain.
    /// </summary>
    internal void DeliverToPage(JsValue handle, JsDetachedValue detached)
    {
        if (_host.Realm is not { } realm)
            return;

        // Second clone, into the page's realm, so the page gets page-realm objects. Adopt is what
        // brings the graph inside that realm's own bracket; the carrier belonged to neither realm
        // between the two calls, which is what made it safe to hand across the threads.
        JsValue materialized;
        try
        {
            materialized = realm.Adopt(detached);
        }
        catch (JsEngineException ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "WorkerBinding.DeliverToPage",
                $"A worker message could not be materialized in the page realm: {ex.Message}", ex);
            return;
        }

        var evt = realm.NewObject();
        realm.DefineValue(evt, "type", JsValue.String("message"));
        realm.DefineValue(evt, "data", materialized);

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
