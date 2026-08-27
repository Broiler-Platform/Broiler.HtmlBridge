using Broiler.HtmlBridge.Scripting;

namespace Broiler.HtmlBridge;

// Phase 8 item 1: the monolithic IScriptEngine is split into the four narrow capability contracts
// below (execution+config, interactive sessions, profiling, event loop). IScriptEngine (see
// IScriptEngine.cs) aggregates them and stays the v2 compatibility surface, so existing consumers
// and implementers are unaffected while new consumers can depend on just the capability they use.

/// <summary>
/// The core execution capability: run scripts against a fresh JS context — optionally with a
/// <c>document</c> built from HTML and a page <c>url</c> — returning serialised post-execution HTML or a
/// detailed result. Also carries the per-engine execution configuration (strict mode and CSP) because it
/// gates how those scripts run.
/// </summary>
public interface IScriptExecutor
{
    /// <summary>
    /// Execute the supplied <paramref name="scripts"/> in a fresh context.
    /// Returns <c>true</c> when all scripts executed without error.
    /// </summary>
    bool Execute(IReadOnlyList<string> scripts);

    /// <summary>
    /// Execute the supplied <paramref name="scripts"/> in a fresh context
    /// with a <c>document</c> object derived from <paramref name="html"/>,
    /// enabling basic DOM interaction via the <see cref="DomBridge"/>.
    /// Returns the serialised post-execution HTML, or <c>null</c> when
    /// there are no scripts to execute.
    /// </summary>
    string? Execute(IReadOnlyList<string> scripts, string html);

    /// <summary>
    /// Execute the supplied <paramref name="scripts"/> in a fresh context
    /// with a <c>document</c> object derived from <paramref name="html"/>
    /// and the page <paramref name="url"/> available via <c>window.location</c>.
    /// After script execution, fires the body <c>onload</c> event and
    /// flushes pending timers so that <c>setTimeout</c>-chained test
    /// harnesses (e.g. Acid3) run to completion.
    /// Returns the serialised post-execution HTML, or <c>null</c> when
    /// there are no scripts to execute.
    /// </summary>
    string? Execute(IReadOnlyList<string> scripts, string html, string? url);

    /// <summary>
    /// Execute regular <paramref name="scripts"/> followed by
    /// <paramref name="deferredScripts"/> (simulating end-of-parsing
    /// for <c>defer</c> script tags).  Otherwise identical to the
    /// three-argument <see cref="Execute(IReadOnlyList{string}, string, string?)"/>
    /// overload.
    /// </summary>
    string? Execute(IReadOnlyList<string> scripts, IReadOnlyList<string> deferredScripts, string html, string? url);

    /// <summary>
    /// As <see cref="Execute(IReadOnlyList{string}, IReadOnlyList{string}, string, string?)"/>, with the
    /// document's authorised ES-module roots. When the engine binds imports (see <c>EngineModuleSupport</c>)
    /// the roots run through the engine's own module machinery; otherwise they are ignored and any linked
    /// module strings the caller placed in <paramref name="deferredScripts"/> run as before.
    /// </summary>
    string? Execute(IReadOnlyList<string> scripts, IReadOnlyList<string> deferredScripts, string html, string? url, IReadOnlyList<ModuleRoot>? moduleRoots);

    /// <summary>
    /// Execute scripts and return a detailed <see cref="ScriptExecutionResult"/>
    /// that includes per-script error messages and stack traces.
    /// </summary>
    ScriptExecutionResult ExecuteDetailed(IReadOnlyList<string> scripts);

    /// <summary>
    /// Whether strict mode (<c>"use strict";</c>) is prepended to every
    /// script before execution. Default is <c>false</c>.
    /// </summary>
    bool StrictModeEnabled { get; set; }

    /// <summary>
    /// The <see cref="ContentSecurityPolicy"/> applied to this engine.
    /// When set, <c>eval()</c> calls are gated by the policy.
    /// </summary>
    ContentSecurityPolicy? Csp { get; set; }
}

/// <summary>
/// The interactive-session capability: start a session whose scripts and deferred scripts run
/// immediately (and the <c>load</c> event fires) but whose pending timers are stepped through one batch at
/// a time, so intermediate visual states (e.g. animations) can be rendered.
/// </summary>
public interface IInteractiveScriptEngine
{
    /// <summary>
    /// Starts an interactive execution session. Scripts and deferred
    /// scripts execute immediately, the <c>load</c> event fires, but
    /// pending timers are <b>not</b> flushed. The returned
    /// <see cref="InteractiveSession"/> allows the caller to step
    /// through timer callbacks one batch at a time, enabling
    /// intermediate visual states to be rendered (e.g. animations).
    /// Returns <c>null</c> when there are no scripts to execute.
    /// The caller must dispose the session when finished.
    /// </summary>
    InteractiveSession? ExecuteInteractive(IReadOnlyList<string> scripts, IReadOnlyList<string> deferredScripts, string html, string? url);

    /// <summary>
    /// As <see cref="ExecuteInteractive(IReadOnlyList{string}, IReadOnlyList{string}, string, string?)"/>,
    /// with the document's authorised ES-module roots for the engine-driven module path.
    /// </summary>
    InteractiveSession? ExecuteInteractive(IReadOnlyList<string> scripts, IReadOnlyList<string> deferredScripts, string html, string? url, IReadOnlyList<ModuleRoot>? moduleRoots);
}

/// <summary>
/// The profiling capability: an optional hook that times and records every script execution.
/// </summary>
public interface IScriptProfiling
{
    /// <summary>
    /// Optional profiling hook. When set, every script execution is timed
    /// and recorded.
    /// </summary>
    ScriptProfilingHook? Profiler { get; set; }
}

/// <summary>
/// The event-loop capability: the micro-task queue that backs <c>queueMicrotask</c> and <c>Promise</c>
/// callbacks between script/timer steps.
/// </summary>
public interface IScriptEventLoop
{
    /// <summary>
    /// The micro-task queue used for <c>queueMicrotask</c> and
    /// <c>Promise</c> callbacks.
    /// </summary>
    MicroTaskQueue MicroTasks { get; }
}
