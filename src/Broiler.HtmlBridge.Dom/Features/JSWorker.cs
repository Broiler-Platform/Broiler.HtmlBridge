using System;
using System.Collections.Concurrent;
using System.Threading;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// One worker: a thread that owns a realm, runs the worker script in it, and then pumps messages
/// until it is closed or terminated.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file used to be the one the messaging/worker group could not express, and the five things
/// it said a contract would have to decide have each been decided.</b> They are recorded here because
/// the answers are the interesting part, not the rename:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Who creates the second realm, and on which thread.</b> This thread does, through
/// <see cref="IJsEngineProvider.CreateRealm"/>, on the first line of <see cref="Pump"/> — so the
/// realm is built, used and disposed by one thread and never touched from another. The provider is
/// the one the <em>page's</em> realm names, because the two exchange structured clones and a clone
/// carries the engine that made it.
/// </description></item>
/// <item><description>
/// <b>How a value is cloned out of one realm into another.</b> <see cref="IJsClone"/>, in the two
/// halves the crossing actually has: <see cref="IJsClone.Detach"/> on the sending thread and
/// <see cref="IJsClone.Adopt"/> on this one.
/// </description></item>
/// <item><description>
/// <b>What the host holds between the two clones.</b> A <see cref="JsDetachedValue"/> — a carrier
/// belonging to no realm, which is what the inbox is a queue of. That is the "third thing" the
/// contract had to name, and naming it is what let the inbox stop being a queue of engine values.
/// </description></item>
/// <item><description>
/// <b>How long a realm stays current on a thread.</b> Unchanged, and that is the point: a provider
/// makes its realm current for one contract call. The clone is now a contract call, so it happens
/// inside that bracket and mints into <em>this</em> realm rather than into whichever realm the thread
/// last touched. Nothing here brackets a host-authored block with a realm, so the ambient state
/// <see cref="JsCall"/> keeps off the contract stays off it.
/// </description></item>
/// <item><description>
/// <b>What drives the worker realm's job queue.</b> This loop does, explicitly, by draining after
/// every message and every timer batch — because <see cref="IJsJobs"/> is pull-shaped on purpose: in
/// a browser the decision of when a microtask checkpoint happens belongs to the event loop, and this
/// loop <em>is</em> the worker's event loop. A promise a worker callback creates reports to the realm
/// the provider built for it, and this is where that queue is emptied. Without the drain the queue
/// would fill and never run, which is a live-lock nothing would report.
/// </description></item>
/// </list>
/// <para>
/// <b>No engine type is named here at all.</b> The last one was an adapter that reached
/// <c>DomBridge.RegisterDOMException</c> through the worker realm's own script context; that
/// installer takes an <see cref="IJsRealm"/> now and is handed this worker's realm directly, which is
/// also the more honest call — the constructor belongs to the realm it is installed in, and a worker
/// has its own on its own thread.
/// </para>
/// <para>
/// <b>One thread, one realm, for the thread's whole life.</b> That is item #15's rule kept rather
/// than bent: the realm is created on the worker thread, every script and every handler runs on that
/// thread, and it is disposed there. Nothing outside ever evaluates in it. The page and the worker
/// meet only at two concurrent queues.
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
    private readonly IJsEngineProvider _provider;
    private readonly BlockingCollection<JsDetachedValue> _inbox = new(new ConcurrentQueue<JsDetachedValue>());
    private readonly CancellationTokenSource _cancel = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();

    private readonly WorkerTimers _timers = new();

    private JsValue _handle;
    private WorkerBinding? _owner;
    private volatile bool _closed;

    public JSWorker(string name, WorkerScript script, IWorkerHost host, IJsEngineProvider provider)
    {
        _name = name;
        _script = script;
        _host = host;
        _provider = provider;
        _thread = new Thread(Pump) { IsBackground = true, Name = $"broiler-worker:{name}" };
    }

    /// <summary>
    /// Binds the page-side handle and starts the thread. Separate from the constructor because the
    /// handle does not exist until the binding has built it, and the thread must not deliver a
    /// message before there is something to deliver it to.
    /// </summary>
    public void Attach(JsValue handle, WorkerBinding owner)
    {
        _handle = handle;
        _owner = owner;
        _thread.Start();
    }

    /// <summary>Queues an already-detached clone for the worker. Called on the page thread.</summary>
    public void Post(JsDetachedValue detached)
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
        IJsRealm? realm = null;
        try
        {
            realm = _provider.CreateRealm(JsRealmOptions.Default);
            InstallWorkerGlobals(realm);

            try
            {
                // A worker's top-level script is a classic script: worker-src decides whether the
                // worker may be created at all, and the script then runs. Neither that decision nor
                // this evaluation is 'unsafe-eval's business, so this is not the eval-gated member --
                // which it was, on the reasoning that the text is the page's. True, and not what
                // decides it.
                realm.EvaluateClassicScript(_script.Source, $"worker:{_name}");
            }
            catch (Exception ex)
            {
                RenderLogger.LogError(LogCategory.JavaScript, "JSWorker.Pump",
                    $"Worker '{_name}' script threw: {ex.Message}", ex);
                QueueError($"Worker script error: {ex.Message}");
                return;
            }

            // The worker's first microtask checkpoint: a top-level promise the script created has to
            // run its reactions before the loop parks on the inbox, or it would wait for a message
            // that may never come.
            DrainJobs(realm);

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
                JsDetachedValue? detached;
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
                    DispatchToWorker(realm, detached);

                // Always after the take, whether it produced a message or timed out: a message that
                // arrives just before a deadline must not postpone the timer past it.
                _timers.RunDue();

                // One checkpoint per turn of the loop, covering both the handler and the timers: this
                // is the worker's event loop, and draining the realm's jobs is what an event loop
                // does between one piece of script and the next.
                DrainJobs(realm);
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
            realm?.Dispose();
        }
    }

    /// <summary>
    /// Runs the realm's queued microtasks, reporting a job that threw rather than letting it end the
    /// worker.
    /// </summary>
    /// <remarks>
    /// A job that throws stops the drain and leaves its successors queued, which the contract says
    /// and which is right — the next checkpoint runs them. What is wrong is losing the failure, and
    /// JSEAL has no logger to report one to by construction, so the host that drives the drain is
    /// where the report belongs. That host is this file.
    /// </remarks>
    private void DrainJobs(IJsRealm realm)
    {
        try
        {
            realm.DrainJobs();
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "JSWorker.DrainJobs",
                $"A microtask in worker '{_name}' threw: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Materializes the page's detached clone into the worker's realm and calls its handler. Runs on
    /// the worker thread, and the realm it is adopted into is this realm because that is the one
    /// asked — which is what makes the resulting objects the worker's own.
    /// </summary>
    private void DispatchToWorker(IJsRealm realm, JsDetachedValue detached)
    {
        if (!WorkerTransfer.CanStructuredClone(realm))
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "JSWorker.DispatchToWorker",
                $"Worker '{_name}' cannot receive a message: this engine does not implement structured clone.");
            return;
        }

        try
        {
            var data = realm.Adopt(detached);

            var evt = realm.NewObject();
            realm.DefineValue(evt, "type", JsValue.String("message"));
            realm.DefineValue(evt, "data", data);

            var handler = realm.GetProperty(realm.Global, "onmessage");
            if (handler.IsFunction)
                realm.Invoke(handler, JsValue.Undefined, [evt]);

            var listeners = realm.GetProperty(realm.Global, ListenersProperty);
            if (listeners.IsArray)
            {
                foreach (var listener in WorkerTransfer.ArrayElements(realm, listeners))
                {
                    if (listener.IsFunction)
                        realm.Invoke(listener, JsValue.Undefined, [evt]);
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
    /// Where <c>addEventListener('message', …)</c> keeps its listeners: an ordinary array on the
    /// worker global, which is a property a worker script can see and always could.
    /// </summary>
    private const string ListenersProperty = "__broilerWorkerListeners";

    /// <summary>
    /// The worker global. Small on purpose — see <see cref="WorkerBinding"/>'s remarks for what is
    /// deliberately absent rather than half-built.
    /// </summary>
    /// <remarks>
    /// Every member is installed with a plain write, in the order it always was, because the order a
    /// worker script sees from <c>Object.getOwnPropertyNames(self)</c> is observable. Each function is
    /// a <see cref="IJsValues.NewConstructor"/> rather than a <see cref="IJsValues.NewMethod"/> for
    /// the same reason as elsewhere in this migration: they were built with the engine's constructable
    /// function type, so each carries a <c>prototype</c>, and preserving that is what makes this a
    /// refactor rather than a fix. (WebIDL says an operation should not be constructable. That is a
    /// pre-existing deviation and correcting it belongs in its own change.)
    /// </remarks>
    private void InstallWorkerGlobals(IJsRealm realm)
    {
        // A real DOMException, so a worker catching a NetworkError or DataCloneError finds a .name
        // and .code to branch on rather than the bare string the fallback produces.
        DomBridge.RegisterDOMException(realm);

        var global = realm.Global;

        // `self` is the global, which under this realm is the same object `this` evaluated to at the
        // top level — the fact JsCapabilities.GlobalIsVariableScope asserts, said directly instead of
        // through an evaluation.
        realm.SetProperty(global, "self", global);
        realm.SetProperty(global, "onmessage", JsValue.Null);
        realm.SetProperty(global, ListenersProperty, realm.NewArray());

        realm.SetProperty(global, "postMessage",
            realm.NewConstructor("postMessage", PostMessageFromWorker, 1));

        realm.SetProperty(global, "addEventListener",
            realm.NewConstructor("addEventListener", AddWorkerEventListener, 2));

        realm.SetProperty(global, "setTimeout", realm.NewConstructor("setTimeout",
            (in call) => JsValue.Number(_timers.Add(CallbackOf(in call), DelayOf(in call), repeating: false)), 2));

        realm.SetProperty(global, "setInterval", realm.NewConstructor("setInterval",
            (in call) => JsValue.Number(_timers.Add(CallbackOf(in call), DelayOf(in call), repeating: true)), 2));

        // One id space, and clearTimeout/clearInterval interchangeable, per the HTML spec — the same
        // contract the page's loop keeps. One function object under two names, as it always was.
        var clear = realm.NewConstructor("clearTimeout", (in call) =>
        {
            if (call.Length > 0 && !call[0].IsNullish)
                _timers.Clear((int)call.Realm.ToNumber(call[0]));

            return JsValue.Undefined;
        }, 1);
        realm.SetProperty(global, "clearTimeout", clear);
        realm.SetProperty(global, "clearInterval", clear);

        realm.SetProperty(global, "importScripts", realm.NewConstructor("importScripts", ImportScripts, 1));

        realm.SetProperty(global, "close", realm.NewConstructor("close", (in _) =>
        {
            _closed = true;
            // A closing worker stops its timers; leaving them would keep the pump awake past close().
            _timers.ClearAll();
            try { _inbox.CompleteAdding(); } catch (ObjectDisposedException) { }
            return JsValue.Undefined;
        }, 0));

        // A worker without console is a worker nobody can debug; routed to the same logger the rest
        // of the bridge uses rather than to the page's console object, which belongs to another realm.
        var console = realm.NewObject();
        foreach (var level in new[] { "log", "info", "warn", "error", "debug" })
        {
            var captured = level;
            realm.DefineValue(console, captured, realm.NewConstructor(captured, (in call) =>
            {
                var text = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
                RenderLogger.LogDebug(LogCategory.JavaScript, $"worker:{_name}", $"[{captured}] {text}");
                return JsValue.Undefined;
            }, 1));
        }

        realm.SetProperty(global, "console", console);
    }

    /// <summary>
    /// <c>postMessage</c> inside the worker: clone here, on this thread and in this realm, then hand
    /// the carrier to the page.
    /// </summary>
    /// <remarks>
    /// The clone happens on this side for three reasons that all point the same way: it is where a
    /// <c>DataCloneError</c> belongs, it is what makes post-send mutation invisible to the page, and a
    /// transfer list detaches the <em>worker's</em> own buffers, which only this realm can do.
    /// </remarks>
    private JsValue PostMessageFromWorker(in JsCall call)
    {
        var realm = call.Realm;

        if (!WorkerTransfer.CanStructuredClone(realm))
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "JSWorker.postMessage",
                $"Worker '{_name}' cannot post: this engine does not implement structured clone.");
            return JsValue.Undefined;
        }

        JsDetachedValue detached;
        try
        {
            var transfer = WorkerTransfer.BuildTransferList(realm, call.Length > 1 ? call[1] : JsValue.Undefined);
            detached = realm.Detach(call.Length > 0 ? call[0] : JsValue.Undefined, transfer);
        }
        catch (JsEngineException ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "JSWorker.postMessage",
                $"Worker '{_name}' posted a value that could not be cloned: {ex.Message}", ex);
            return JsValue.Undefined;
        }

        var handle = _handle;
        var owner = _owner;
        if (!handle.IsObject || owner is null || _cancel.IsCancellationRequested)
            return JsValue.Undefined;

        // Onto the page's event loop, not into it: the queue is concurrent, and the page's own
        // drain is what will run this.
        _host.QueueFrameAction(() => owner.DeliverToPage(handle, detached));
        return JsValue.Undefined;
    }

    /// <remarks>
    /// Only <c>message</c> is honoured, as before. The type is read with the realm's coercion because
    /// the former <c>a[0].ToString()</c> was the observable ECMAScript one and a page may pass an
    /// object with a <c>toString</c>; the listener array is appended to by index, which is what
    /// growing an array is.
    /// </remarks>
    private static JsValue AddWorkerEventListener(in JsCall call)
    {
        var realm = call.Realm;
        if (call.Length < 2 || !call[1].IsFunction ||
            !string.Equals(realm.ToJsString(call[0]), "message", StringComparison.Ordinal))
        {
            return JsValue.Undefined;
        }

        var listeners = realm.GetProperty(realm.Global, ListenersProperty);
        if (listeners.IsArray)
            realm.DefineIndex(listeners, (uint)realm.GetProperty(listeners, "length").AsNumber, call[1]);

        return JsValue.Undefined;
    }

    /// <summary>
    /// <c>importScripts(...urls)</c>: fetch and run each in order, synchronously, in this global.
    /// </summary>
    /// <remarks>
    /// Specifiers resolve against the worker's own script directory, which is what the HTML spec means
    /// by "relative to the worker's script URL" — not the document's base path.
    /// </remarks>
    private JsValue ImportScripts(in JsCall call)
    {
        var realm = call.Realm;

        for (var i = 0; i < call.Length; i++)
        {
            var specifier = call[i].IsMissing ? string.Empty : realm.ToJsString(call[i]);
            if (string.IsNullOrWhiteSpace(specifier))
                continue;

            var imported = _host.ResolveWorkerScript(specifier, _script.BaseDirectory);
            if (imported is null)
            {
                // Spec: a script that cannot be fetched is a NetworkError, and it aborts the
                // whole call — later specifiers in the same call do not run.
                throw realm.DomError("NetworkError", $"importScripts: could not load '{specifier}'.");
            }

            // Deliberately not wrapped: a throwing imported script propagates to the caller,
            // exactly as an inline one would. Swallowing it here would leave the worker running
            // with a half-initialised global and no way to find out.
            realm.EvaluateClassicScript(imported.Value.Source, $"worker:{_name}:{specifier}");
        }

        return JsValue.Undefined;
    }

    /// <summary>
    /// The callback argument of a timer call as the work to run when it comes due, or
    /// <see langword="null"/> when argument zero is not a function.
    /// </summary>
    /// <remarks>
    /// <see cref="WorkerTimers"/> schedules deadlines and names no JavaScript type, so the call into
    /// JavaScript is made here — the same receiver (<c>undefined</c>) and empty argument list the
    /// scheduler used to pass itself. A null answer is what keeps <c>setTimeout("string")</c> handing
    /// back a clearable id that never fires.
    /// </remarks>
    private static Action? CallbackOf(in JsCall call)
    {
        if (!call[0].IsFunction)
            return null;

        var realm = call.Realm;
        var callback = call[0];
        return () => realm.Invoke(callback, JsValue.Undefined);
    }

    /// <summary>The delay argument of a timer call, defaulting to 0 when absent or not a number.</summary>
    /// <remarks>
    /// The realm's coercion rather than the handle's, because the former <c>DoubleValue</c> was the
    /// engine's <c>ToNumber</c> and <c>setTimeout(f, "10")</c> is a call a page makes.
    /// </remarks>
    private static double DelayOf(in JsCall call) =>
        call.Length > 1 && !call[1].IsNullish ? call.Realm.ToNumber(call[1]) : 0;

    private void QueueError(string message)
    {
        var handle = _handle;
        var owner = _owner;
        if (!handle.IsObject || owner is null)
            return;

        _host.QueueFrameAction(() => owner.FireErrorEvent(handle, message));
    }
}
