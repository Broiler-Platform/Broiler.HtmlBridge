namespace Broiler.HtmlBridge;

/// <summary>
/// Abstraction over a JavaScript execution engine.
/// </summary>
/// <remarks>
/// Phase 8 item 1 split the monolithic engine surface into four narrow capability contracts —
/// <see cref="IScriptExecutor"/> (execution + configuration), <see cref="IInteractiveScriptEngine"/>
/// (stepped interactive sessions), <see cref="IScriptProfiling"/> (the profiling hook) and
/// <see cref="IScriptEventLoop"/> (the micro-task queue). <c>IScriptEngine</c> now aggregates them and is
/// retained unchanged as the v2 compatibility surface: every member it used to declare is still reachable
/// through it, so existing consumers and implementers are unaffected, while new consumers can depend on
/// just the capability they use. The members are declared on the capability interfaces above; a deliberate
/// public-surface v3 (not this refactor) would be required to drop or reshape any of them.
/// </remarks>
public interface IScriptEngine : IScriptExecutor, IInteractiveScriptEngine, IScriptProfiling, IScriptEventLoop
{
}
