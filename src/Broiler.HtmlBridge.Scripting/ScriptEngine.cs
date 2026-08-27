using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;

namespace Broiler.HtmlBridge;

/// <summary>
/// Executes JavaScript using the YantraJS engine.
/// A fresh <see cref="JSContext"/> is created for each call to
/// <see cref="Execute(IReadOnlyList{string})"/> so that scripts from different pages are isolated.
/// </summary>
public sealed partial class ScriptEngine : ITypedScriptEngine
{
    private readonly IDomBridgeRuntimeFactory _domBridgeFactory;

    public ScriptEngine()
        : this(new DomBridgeFactory())
    {
    }

    public ScriptEngine(IDomBridgeRuntimeFactory domBridgeFactory)
    {
        _domBridgeFactory = domBridgeFactory ?? throw new ArgumentNullException(nameof(domBridgeFactory));
    }

    /// <inheritdoc />
    public bool StrictModeEnabled { get; set; }

    /// <inheritdoc />
    public ContentSecurityPolicy? Csp { get; set; }

    /// <inheritdoc />
    public ScriptProfilingHook? Profiler { get; set; }

    /// <inheritdoc />
    public MicroTaskQueue MicroTasks { get; } = new();

    /// <summary>
    /// Phase 8 item 3: diagnostic for async-drain-limit exhaustion. <see langword="true"/> when a
    /// call to <see cref="DrainAsyncWork"/> ran out its iteration budget
    /// (<see cref="DomBridgeRuntimeLimits.AsyncDrainIterationLimit"/>) while microtasks or timers were
    /// still queued — i.e. the async work did not settle and draining stopped. This makes the former
    /// silent stop observable (also logged as a warning). A fresh <see cref="ScriptEngine"/> per page
    /// means this reflects whether that page's async work exhausted the budget. Stays
    /// <see langword="false"/> when every drain settled normally.
    /// </summary>
    public bool AsyncDrainLimitExhausted { get; private set; }

    /// <inheritdoc />
    public bool Execute(IReadOnlyList<string> scripts)
    {
        if (scripts.Count == 0)
            return true;

        using var context = new JSContext();
        RegisterRuntimeExtensions(context);
        var allSucceeded = true;
        for (var i = 0; i < scripts.Count; i++)
        {
            var label = ScriptLabel.Inline(i);
            try
            {
                var source = PrepareSource(scripts[i]);
                RunMeasured(label, () => context.Eval(source, label));
                MicroTasks.Drain();
            }
            catch (Exception ex)
            {
                RenderLogger.LogError(LogCategory.JavaScript, "ScriptEngine.Execute", $"Script {label} failed: {ex.Message}", ex);
                allSucceeded = false;
            }
        }
        MicroTasks.Drain();
        return allSucceeded;
    }

    /// <inheritdoc />
    public string? Execute(IReadOnlyList<string> scripts, string html)
    {
        return Execute(scripts, html, url: null);
    }

    /// <inheritdoc />
    public string? Execute(IReadOnlyList<string> scripts, string html, string? url)
    {
        return Execute(scripts, Array.Empty<string>(), html, url);
    }

    /// <inheritdoc />
    public string? Execute(IReadOnlyList<string> scripts, IReadOnlyList<string> deferredScripts, string html, string? url)
        => Execute(scripts, deferredScripts, html, url, moduleRoots: null);

    /// <summary>
    /// As <see cref="Execute(IReadOnlyList{string}, IReadOnlyList{string}, string, string?)"/>, with the
    /// document's authorised ES-module roots. When the engine binds imports (<see cref="EngineModuleSupport"/>),
    /// the roots run through the engine's own module machinery on a <see cref="BridgeModuleContext"/> after the
    /// <paramref name="deferredScripts"/>; otherwise they are left unrun (the string-rewriting linker fallback
    /// was retired in the Phase 7 tail).
    /// </summary>
    public string? Execute(IReadOnlyList<string> scripts, IReadOnlyList<string> deferredScripts, string html, string? url, IReadOnlyList<ModuleRoot>? moduleRoots)
        => ExecuteCore(scripts, deferredScripts, html, url, moduleRoots, static bridge => bridge.SerializeToHtml());

    /// <summary>
    /// Executes scripts against the canonical DOM and returns that same
    /// document for direct renderer consumption, avoiding serialization and
    /// reparsing between script execution and layout.
    /// </summary>
    public Broiler.Dom.DomDocument? ExecuteToDocument(
        IReadOnlyList<string> scripts,
        IReadOnlyList<string> deferredScripts,
        string html,
        string? url)
        => ExecuteToDocument(scripts, deferredScripts, html, url, moduleRoots: null);

    /// <summary>
    /// As <see cref="ExecuteToDocument(IReadOnlyList{string}, IReadOnlyList{string}, string, string?)"/>,
    /// with the document's authorised ES-module roots for the engine-driven module path.
    /// </summary>
    public Broiler.Dom.DomDocument? ExecuteToDocument(
        IReadOnlyList<string> scripts,
        IReadOnlyList<string> deferredScripts,
        string html,
        string? url,
        IReadOnlyList<ModuleRoot>? moduleRoots)
        => ExecuteCore(scripts, deferredScripts, html, url, moduleRoots, static bridge => bridge.GetRenderDocument());

    private T? ExecuteCore<T>(
        IReadOnlyList<string> scripts,
        IReadOnlyList<string> deferredScripts,
        string html,
        string? url,
        IReadOnlyList<ModuleRoot>? moduleRoots,
        Func<IDomBridgeRuntime, T> createResult)
        where T : class
    {
        var roots = moduleRoots ?? [];
        if (scripts.Count == 0 && deferredScripts.Count == 0 && roots.Count == 0)
            return null;

        var previousCsp = Csp;
        Csp = ContentSecurityPolicy.FromHtml(html) ?? previousCsp;

        // Drive the engine's own module machinery only when it actually binds imports (patches 0010/0011);
        // otherwise the page runs on a plain JSContext and modules come in as linked strings via the linker.
        var useEngineModules = roots.Count > 0 && EngineModuleSupport.Available;
        var moduleContext = useEngineModules ? new BridgeModuleContext(Csp, url) : null;

        try
        {
            // Current on this thread for the whole of script execution, because a promise captures
            // its synchronization context when it is created, not when it settles. Without it the
            // engine resumes continuations on the thread pool, concurrently with this thread.
            using var microTaskContext = MicroTaskSynchronizationContext.Install(MicroTasks);
            using JSContext context = moduleContext ?? new JSContext();
            RegisterRuntimeExtensions(context);
            var bridge = _domBridgeFactory.Create();
            try
            {
                bridge.Csp = Csp;
                bridge.TaskCheckpointCallback = () => MicroTasks.Drain();

                if (!string.IsNullOrEmpty(url))
                    bridge.Attach(context, html, url);
                else
                    bridge.Attach(context, html);

                // Event-loop ordering (EL-3): run the synchronous script phases (regular → deferred → modules)
                // with only microtask checkpoints between them, then — after the window load event — drain the
                // timer queue to completion. So a timer scheduled by an early script fires after all script
                // execution (in deadline order), not eagerly between scripts, matching the HTML task model.
                RunPageScripts(context, bridge, moduleContext, scripts, deferredScripts, url, roots,
                    _ => MicroTasks.Drain(), DrainAsyncWork, "ScriptEngine.Execute");
                return createResult(bridge);
            }
            finally
            {
                // This path owns the bridge — unlike ExecuteInteractive, which hands it to the
                // returned InteractiveSession — so the per-document session (layout view and its
                // container, timers, listeners, observers) is released here instead of leaking one
                // per executed page. `createResult` has already run, and it yields either
                // serialized HTML or an isolated render projection; neither reads bridge state
                // afterwards. Runs before the enclosing `using` releases the borrowed JSContext.
                // The cast is because IDomBridgeRuntime is not IDisposable (see DomBridge.Lifetime).
                (bridge as IDisposable)?.Dispose();
            }
        }
        finally
        {
            Csp = previousCsp;
        }
    }

    /// <summary>
    /// The single script-execution pipeline shared by the render/typed path (<see cref="ExecuteCore{T}"/>)
    /// and the interactive path (<see cref="ExecuteInteractive(IReadOnlyList{string}, IReadOnlyList{string}, string, string?, IReadOnlyList{ModuleRoot})"/>).
    /// On an already-attached <paramref name="bridge"/>/<paramref name="context"/> it runs, in document order:
    /// the regular <paramref name="scripts"/> (tracking each one's <c>&lt;script&gt;</c> DOM element index for
    /// <c>document.write</c> and applying the <see cref="Profiler"/> when set), the
    /// <paramref name="deferredScripts"/> (end-of-parse for <c>defer</c>), and the authorised engine-driven
    /// module <paramref name="roots"/> (Phase 7 item 6, only when <paramref name="moduleContext"/> is non-null),
    /// then fires the window <c>load</c> event (critical for <c>&lt;body onload&gt;</c> harnesses like Acid3).
    /// Async work is settled in two phases (EL-3 event-loop ordering): <paramref name="interScriptDrain"/> runs
    /// after each eval and drains only microtasks (a microtask checkpoint), so timers are <em>not</em> fired
    /// eagerly between synchronous scripts; <paramref name="finalDrain"/> runs once after the load event. The
    /// render path passes a full timer-draining <c>finalDrain</c> (so timers fire, in deadline order, after all
    /// script execution), while the interactive path drains only microtasks in both and leaves timers for the
    /// session to step. Per-script failures are caught and logged under
    /// <paramref name="logSource"/>; they do not abort the remaining scripts.
    /// </summary>
    private void RunPageScripts(
        JSContext context,
        IDomBridgeRuntime bridge,
        BridgeModuleContext? moduleContext,
        IReadOnlyList<string> scripts,
        IReadOnlyList<string> deferredScripts,
        string? url,
        IReadOnlyList<ModuleRoot> roots,
        Action<IDomBridgeRuntime> interScriptDrain,
        Action<IDomBridgeRuntime> finalDrain,
        string logSource)
    {
        // Track the corresponding <script> DOM element index so that document.currentScript names
        // the running script and document.write() inserts at its position. Only the elements each
        // bucket actually runs are counted: pairing the buckets against every <script> in the tree
        // attributed the n-th executed script to the n-th element, which on any document carrying a
        // data block before a script — a JSON-LD block, an import map — is a different element
        // (ScriptElementMap).
        var scriptElements = ScriptElementMap.Classic(bridge.Elements);
        var deferredScriptElements = ScriptElementMap.Deferred(bridge.Elements);

        for (var i = 0; i < scripts.Count; i++)
        {
            bridge.CurrentScriptIndex = i < scriptElements.Count ? scriptElements[i] : -1;
            var label = ScriptLabel.Inline(i);
            try
            {
                var source = PrepareSource(scripts[i]);
                RunMeasured(label, () => context.Eval(source, label));
                interScriptDrain(bridge);
            }
            catch (Exception ex)
            {
                RenderLogger.LogError(LogCategory.JavaScript, logSource, $"Script {label} failed: {ex.Message}", ex);
            }
        }
        bridge.CurrentScriptIndex = -1;

        // Execute deferred scripts after all regular scripts (end-of-parsing for <script defer>).
        for (var i = 0; i < deferredScripts.Count; i++)
        {
            // A deferred script is as much the running script as a non-deferred one; this bucket
            // never set the index, so document.currentScript was null and document.write appended
            // to <body> for the whole of it.
            bridge.CurrentScriptIndex = i < deferredScriptElements.Count ? deferredScriptElements[i] : -1;
            var label = ScriptLabel.Deferred(i);
            try
            {
                var source = PrepareSource(deferredScripts[i]);
                RunMeasured(label, () => context.Eval(source, label));
                interScriptDrain(bridge);
            }
            catch (Exception ex)
            {
                RenderLogger.LogError(LogCategory.JavaScript, logSource, $"Script {label} failed: {ex.Message}", ex);
            }
        }

        bridge.CurrentScriptIndex = -1;

        // Engine-driven ES modules (Phase 7 item 6): modules are deferred, so run the authorised roots
        // after the classic deferred scripts. Each root executes on the same realm the DOM is attached to,
        // and the engine loads its transitive imports itself (CSP-gated) via BridgeModuleContext's resolution
        // seams — no EsModuleLinker involved. Reached only when EngineModuleSupport.Available.
        if (moduleContext != null)
        {
            foreach (var root in roots)
            {
                try
                {
                    RunMeasured(ScriptLabel.Module(root.Key), () =>
                        moduleContext.RunScriptAsync(root.Source, root.BaseUrl ?? url ?? string.Empty, uniqueModuleID: root.Key)
                            .GetAwaiter().GetResult());
                    interScriptDrain(bridge);
                }
                catch (Exception ex)
                {
                    RenderLogger.LogError(LogCategory.JavaScript, logSource, $"Module root {root.Key} failed: {ex.Message}", ex);
                }
            }
        }

        bridge.FireWindowLoadEvent();
        finalDrain(bridge);
    }

    /// <summary>
    /// Runs <paramref name="work"/> (a single script/module evaluation), timing it through
    /// <see cref="Profiler"/> when a hook is attached and running it directly otherwise. Every script the
    /// engine executes — inline (<c>inline-{i}</c>), deferred (<c>deferred-{i}</c>) and engine-driven module
    /// roots (<c>module-{key}</c>) — funnels through here so the profiling hook, when set, sees a complete
    /// and consistent timeline rather than the inline scripts alone (Phase 8 item 4).
    /// </summary>
    private void RunMeasured(string label, Action work)
    {
        if (Profiler != null)
            Profiler.Measure(label, work);
        else
            work();
    }

    /// <inheritdoc />
    public InteractiveSession? ExecuteInteractive(IReadOnlyList<string> scripts, IReadOnlyList<string> deferredScripts, string html, string? url)
        => ExecuteInteractive(scripts, deferredScripts, html, url, moduleRoots: null);

    /// <summary>
    /// As <see cref="ExecuteInteractive(IReadOnlyList{string}, IReadOnlyList{string}, string, string?)"/>,
    /// with the document's authorised ES-module roots. When the engine binds imports the roots run through
    /// the engine's module machinery on a <see cref="BridgeModuleContext"/> (whose lifetime transfers to the
    /// returned session); otherwise they are ignored and the linked strings in <paramref name="deferredScripts"/>
    /// run as before. Modules are deferred, so they run eagerly here after the deferred scripts.
    /// </summary>
    public InteractiveSession? ExecuteInteractive(IReadOnlyList<string> scripts, IReadOnlyList<string> deferredScripts, string html, string? url, IReadOnlyList<ModuleRoot>? moduleRoots)
    {
        var roots = moduleRoots ?? [];
        if (scripts.Count == 0 && deferredScripts.Count == 0 && roots.Count == 0)
            return null;

        var previousCsp = Csp;
        Csp = ContentSecurityPolicy.FromHtml(html) ?? previousCsp;

        var useEngineModules = roots.Count > 0 && EngineModuleSupport.Available;
        var moduleContext = useEngineModules ? new BridgeModuleContext(Csp, url) : null;

        // Ownership of the context + bridge transfers to the returned InteractiveSession, which
        // disposes both. If setup throws before the session is built, the catch disposes them so a
        // failed ExecuteInteractive never leaks a JS context or an event loop (Phase 8 item 2). The
        // CSP is restored in the finally on every path.
        // Scoped to setup: the promises created while the page's scripts run capture it here. A
        // later session Step() runs on the caller's thread, which installs nothing — the interactive
        // path drains explicitly between steps, so a reaction that lands on the pool there is the
        // caller's own pacing rather than a race against a render.
        using var microTaskContext = MicroTaskSynchronizationContext.Install(MicroTasks);
        JSContext context = moduleContext ?? new JSContext();
        IDomBridgeRuntime? bridge = null;
        try
        {
            RegisterRuntimeExtensions(context);
            bridge = _domBridgeFactory.Create();
            bridge.Csp = Csp;
            bridge.TaskCheckpointCallback = () => MicroTasks.Drain();

            if (!string.IsNullOrEmpty(url))
                bridge.Attach(context, html, url);
            else
                bridge.Attach(context, html);

            // Same pipeline as the render path, but the interactive session drains only microtasks at every
            // point (inter-script and final) and leaves pending timers for the caller to step through one
            // batch at a time.
            RunPageScripts(context, bridge, moduleContext, scripts, deferredScripts, url, roots,
                _ => MicroTasks.Drain(), _ => MicroTasks.Drain(), "ScriptEngine.ExecuteInteractive");

            return new InteractiveSession(context, bridge, MicroTasks);
        }
        catch
        {
            // Failed construction must not leak the private event loop / context.
            (bridge as IDisposable)?.Dispose();
            context.Dispose();
            throw;
        }
        finally
        {
            Csp = previousCsp;
        }
    }

    /// <inheritdoc />
    public ScriptExecutionResult ExecuteDetailed(IReadOnlyList<string> scripts)
    {
        if (scripts.Count == 0)
            return new ScriptExecutionResult { Success = true };

        using var context = new JSContext();
        RegisterRuntimeExtensions(context);
        var errors = new List<ScriptError>();

        for (var i = 0; i < scripts.Count; i++)
        {
            try
            {
                var source = PrepareSource(scripts[i]);
                RunMeasured($"inline-{i}", () => context.Eval(source));
                MicroTasks.Drain();
            }
            catch (Exception ex)
            {
                RenderLogger.LogError(LogCategory.JavaScript, "ScriptEngine.ExecuteDetailed", $"Script inline-{i} failed: {ex.Message}", ex);
                errors.Add(new ScriptError
                {
                    ScriptIndex = i,
                    Message = ex.Message,
                    StackTrace = ex.StackTrace ?? string.Empty
                });
            }
        }
        MicroTasks.Drain();

        return new ScriptExecutionResult
        {
            Success = errors.Count == 0,
            Errors = errors
        };
    }

    /// <summary>
    /// Drain queued microtasks and timer tasks until the bridge-backed execution
    /// environment settles, matching the checkpointing used by the WPT harness.
    /// </summary>
    private void DrainAsyncWork(IDomBridgeRuntime bridge)
    {
        for (var iteration = 0; iteration < DomBridgeRuntimeLimits.AsyncDrainIterationLimit; iteration++)
        {
            var hadWork = false;

            if (MicroTasks.Count > 0)
            {
                MicroTasks.Drain();
                hadWork = true;
            }

            if (bridge.HasPendingTimersDueBy(DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs))
            {
                bridge.FlushTimerStep();
                hadWork = true;
            }

            if (!hadWork)
                return; // settled — nothing left that this capture's window covers
        }

        // Phase 8 item 3: the iteration budget is exhausted while work is *still due now*. The
        // virtual-time horizon above already retires the ordinary case — a page holding an interval,
        // whose next tick is simply later — so reaching here means work that keeps regenerating at
        // the current instant and never lets the clock move: a setTimeout or queueMicrotask that
        // reschedules itself with no delay. Record it on the engine and log it, so the truncation is
        // diagnosable rather than invisible.
        AsyncDrainLimitExhausted = true;
        RenderLogger.LogWarning(LogCategory.JavaScript, "ScriptEngine.DrainAsyncWork",
            $"Async work still due after {DomBridgeRuntimeLimits.AsyncDrainIterationLimit} drain iterations; " +
            $"stopping with pending microtasks={MicroTasks.Count}. " +
            "A callback is rescheduling itself with no delay, so the virtual clock cannot advance.");
    }

    /// <summary>
    /// Optionally prepend <c>"use strict";</c> to the script source.
    /// </summary>
    private string PrepareSource(string script) => StrictModeEnabled ? "\"use strict\";\n" + script : script;

    /// <summary>
    /// Register Milestone 4 runtime extensions on the JS context:
    /// <c>queueMicrotask</c>, CSP-gated <c>eval</c>, and polyfills for
    /// ES2023+ built-ins not natively provided by YantraJS.
    /// </summary>
    private void RegisterRuntimeExtensions(JSContext context)
    {
        // queueMicrotask(fn)
        context["queueMicrotask"] = new JSFunction((in Arguments a) => JsScriptEngineQueueMicrotask001Core(in a), "queueMicrotask", 1);

        // CSP-gated eval wrapper
        if (Csp != null && !Csp.AllowsEval)
        {
            context["eval"] = new JSFunction((in Arguments _) => JsScriptEngineEval002Core(in _), "eval", 1);
        }

        // WeakRef polyfill (YantraJS may not expose this natively)
        RegisterWeakRefPolyfill(context);

        // FinalizationRegistry polyfill
        RegisterFinalizationRegistryPolyfill(context);
    }

    /// <summary>
    /// Register a minimal <c>WeakRef</c> constructor.  Because .NET's GC
    /// model differs from V8/SpiderMonkey, the implementation uses
    /// <see cref="WeakReference{T}"/> under the hood.
    /// </summary>
    private static void RegisterWeakRefPolyfill(JSContext context)
    {
        // Only install if not already present
        try
        {
            var existing = context.Eval("typeof WeakRef");
            if (existing is JSString s && s.ToString() != "undefined")
                return;
        }
        catch (Exception ex) { RenderLogger.LogDebug(LogCategory.JavaScript, "ScriptEngine.WeakRefPolyfill", $"WeakRef not present, installing polyfill: {ex.Message}"); }

        var weakRefCtor = new JSFunction((in Arguments args) => JsScriptEngineWeakRef004Core(in args), "WeakRef", 1);

        context["WeakRef"] = weakRefCtor;
    }

    /// <summary>
    /// Register a minimal <c>FinalizationRegistry</c> constructor.
    /// Since .NET GC timing is non-deterministic, the cleanup callback
    /// is exposed but invocation depends on GC scheduling.
    /// </summary>
    private static void RegisterFinalizationRegistryPolyfill(JSContext context)
    {
        try
        {
            var existing = context.Eval("typeof FinalizationRegistry");
            if (existing is JSString s && s.ToString() != "undefined")
                return;
        }
        catch (Exception ex) { RenderLogger.LogDebug(LogCategory.JavaScript, "ScriptEngine.FinalizationRegistryPolyfill", $"FinalizationRegistry not present, installing polyfill: {ex.Message}"); }

        var registryCtor = new JSFunction((in Arguments args) => JsScriptEngineFinalizationRegistry007Core(in args), "FinalizationRegistry", 1);

        context["FinalizationRegistry"] = registryCtor;
    }
}
