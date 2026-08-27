using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

/// <summary>
/// Compiles a document's classic script sources on a thread budget, ahead of the ordered loop that
/// evaluates them. Multithreading roadmap item #16.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a prefetch/consume split, the fifth in this document's history</b>, and it is the same
/// shape as items #2, #8, #12 and #17: the consume site does not move, does not change order and
/// does not change what it computes — <c>JSContext.Eval</c> is called on the same sources in the
/// same document order on the same thread. What changes is that the compile each of those calls
/// would have performed inline has already happened, on a worker, while the host was doing
/// something else. Evaluation itself is never moved off the calling thread: item #15 made the
/// engine's event loop single-threaded on purpose, and nothing here weakens that.
/// </para>
/// <para>
/// <b>The store the roadmap said was missing is the context's own cache.</b> §6 of
/// <c>multithreading.md</c> recorded item #16 as blocked because a context builds its
/// <see cref="DictionaryCodeCache"/> inside its constructor and <c>JSContextOptions</c> can carry
/// cache <em>options</em> but not a cache <em>instance</em>, so "a compile-ahead worker today has
/// nowhere to put its results". The premise is right and the conclusion does not follow:
/// <see cref="JSContext.CodeCache"/> is public, so the store exists as soon as the context does —
/// and the context exists before any of this document's script sources do, because the last of them
/// is not known until the external fetches have returned. So no engine change is needed, the
/// isolation question §6 raised (a process-shared cache serving one document's code to the next)
/// does not arise, and the cache's lifetime is exactly the context's, which is exactly the lifetime
/// it had before.
/// </para>
/// <para>
/// <b>The key a worker writes must be the key the consumer asks for.</b> This is item #17's lesson
/// restated — a prefetch filed under a key nobody looks up is not a saved round trip, it is a
/// second one — and here the key is structural rather than textual:
/// <c>DictionaryCodeCache</c> keys on (source span, location, argument list, <see
/// cref="JSCompilationOptions"/>). So <see cref="Start"/> takes the location the consuming
/// <c>Eval</c> will pass and reads <see cref="JSContext.CompilationOptions"/> from the very context
/// that will do the consuming, rather than reconstructing either. A worker has no ambient context
/// (<c>JSEngine.Current</c> is <c>[ThreadStatic]</c> and null there), which is precisely why both
/// have to be passed explicitly: <c>CoreScript.Compile</c> would otherwise default them from a
/// context that is not there.
/// </para>
/// <para>
/// <b>Top-level classic scripts only, and that restriction is load-bearing.</b> The compiler reads
/// one piece of ambient state that the cache key does not carry —
/// <c>JSContext.IsCompilingDirectEval</c> and the binding scopes that go with it, consulted in
/// <c>FastCompiler</c>'s constructor. For a script being run at the top of the host's loop that
/// state is "none" on both threads: null context on the worker, and a context that is not inside a
/// direct eval on the consumer. Hand this an <c>eval()</c> body and the two would stop agreeing, so
/// it is offered document script sources and nothing else.
/// </para>
/// <para>
/// <b>A compile that throws is discarded here and rethrown at the consume site.</b>
/// <c>DictionaryCodeCache</c> removes an entry whose factory threw, so a source with a syntax error
/// is not cached as a failure — the consuming <c>Eval</c> compiles it again on the calling thread
/// and raises the same <c>SyntaxError</c>, from the same place, with a live context to build it
/// from. The worker swallows everything for the same reason: it is running ahead of a call site
/// that has its own error handling, and it must not be the thing that reports a failure.
/// </para>
/// <para>
/// <b>A budget of 1 is the pre-change path, not a one-wide version of the new one.</b> At 1 — or
/// with fewer than <see cref="MinimumScriptsToOverlap"/> sources — <see cref="Start"/> returns
/// <c>null</c> and no worker is created at all, so every compile happens exactly where it used to,
/// in the order it used to, on the thread it used to. That is what the exit gate compares against.
/// </para>
/// </remarks>
/// <summary>
/// One classic script source together with the location its consuming <c>Eval</c> will pass. The
/// two travel as a pair because the code cache keys on both: compiling a source under a location
/// the evaluation will not use files the entry where nothing looks.
/// </summary>
/// <param name="Source">The top-level classic script source.</param>
/// <param name="Location">
/// The location the consuming <c>Eval</c> passes — the script's name in a stack trace.
/// <c>null</c> is not a "missing" value: it is what a host that calls <c>Eval(source)</c> without a
/// path passes, and matching it is the point.
/// </param>
public readonly record struct ScriptCompileUnit(string Source, string? Location);

public sealed class ScriptCompileAhead : IDisposable
{
    /// <summary>Environment variable that overrides the default thread budget.</summary>
    public const string ThreadsEnvironmentVariable = "BROILER_SCRIPT_COMPILE_THREADS";

    /// <summary>
    /// Sources a document must have before the compile-ahead is worth starting. Two, because one
    /// script compiled on a worker is one script compiled serially plus a handoff: the consuming
    /// <c>Eval</c> is the very next thing the host does with it, so there is nothing left to
    /// overlap it with.
    /// </summary>
    public const int MinimumScriptsToOverlap = 2;

    private static int _maxDegreeOfParallelism = ReadConfiguredDegree();

    private static long _documentsCompiledAhead;
    private static long _scriptsQueued;
    private static long _scriptsCompiled;
    private static long _scriptsFailed;
    private static long _documentsBelowMinimum;

    private readonly Task _work;

    private ScriptCompileAhead(Task work) => _work = work;

    /// <summary>
    /// Maximum concurrent compiles. <c>1</c> disables the compile-ahead entirely — see the class
    /// remarks; it is the pre-change path rather than a one-wide version of the new one.
    /// </summary>
    public static int MaxDegreeOfParallelism
    {
        get => _maxDegreeOfParallelism;
        set => _maxDegreeOfParallelism = Math.Max(1, value);
    }

    /// <summary>
    /// Whether to count what the compile-ahead did. Off by default; the counters are per document
    /// and per script rather than per compilation unit, so the cost is a handful of interlocked
    /// writes per render even when it is on.
    /// </summary>
    /// <remarks>
    /// The question worth asking of this item is not how fast <em>N</em> concurrent compiles are —
    /// that is arithmetic — but how many scripts a document gives it to overlap, and how often a
    /// document is skipped for having too few. A flat scaling row means something different in each
    /// case: "the compiles did not overlap" and "there was nothing to overlap" call for opposite
    /// next steps. That is the same reason <c>ImagePrefetch</c> counts documents below its own
    /// minimum.
    /// </remarks>
    public static bool CollectDiagnostics { get; set; }

    /// <summary>
    /// Documents whose scripts were compiled ahead, sources queued, sources whose compile finished
    /// on a worker, sources whose compile threw on a worker (and is therefore left for the consume
    /// site to redo and report), and documents skipped for naming fewer than
    /// <see cref="MinimumScriptsToOverlap"/> sources.
    /// </summary>
    public static (long DocumentsCompiledAhead, long ScriptsQueued, long ScriptsCompiled, long ScriptsFailed, long DocumentsBelowMinimum)
        Diagnostics => (
            Interlocked.Read(ref _documentsCompiledAhead),
            Interlocked.Read(ref _scriptsQueued),
            Interlocked.Read(ref _scriptsCompiled),
            Interlocked.Read(ref _scriptsFailed),
            Interlocked.Read(ref _documentsBelowMinimum));

    /// <summary>Zeroes the counters.</summary>
    public static void ResetDiagnostics()
    {
        Interlocked.Exchange(ref _documentsCompiledAhead, 0);
        Interlocked.Exchange(ref _scriptsQueued, 0);
        Interlocked.Exchange(ref _scriptsCompiled, 0);
        Interlocked.Exchange(ref _scriptsFailed, 0);
        Interlocked.Exchange(ref _documentsBelowMinimum, 0);
    }

    /// <summary>
    /// Starts compiling <paramref name="sources"/> into <paramref name="context"/>'s code cache and
    /// returns immediately, or returns <c>null</c> when the budget or the source count says not to
    /// (see the class remarks — that case is the unmodified path, not a degenerate version of this
    /// one). The caller keeps evaluating in document order and finds the compiles done, in flight,
    /// or not yet started; all three are correct and only the first is faster.
    /// </summary>
    /// <param name="context">
    /// The context that will evaluate these sources. Both halves of the cache key that this type
    /// cannot see — the cache instance and the compilation options — are read from it, so a source
    /// compiled here is one this context's <c>Eval</c> will find rather than one it will recompile.
    /// </param>
    /// <param name="sources">
    /// Top-level classic script sources, in document order. Not <c>eval()</c> bodies and not module
    /// sources: see the class remarks on why the restriction is load-bearing.
    /// </param>
    /// <param name="location">
    /// The source location the consuming <c>Eval</c> will pass, because it is part of the cache key.
    /// <c>null</c> is the common case and is not a "missing" value — it is what a host that calls
    /// <c>Eval(source)</c> without a path passes, and matching it is the point.
    /// </param>
    public static ScriptCompileAhead? Start(
        JSContext context,
        IReadOnlyList<string> sources,
        string? location = null)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var units = new ScriptCompileUnit[sources.Count];
        for (var i = 0; i < sources.Count; i++)
            units[i] = new ScriptCompileUnit(sources[i], location);

        return Start(context, units);
    }

    /// <summary>
    /// As <see cref="Start(JSContext, IReadOnlyList{string}, string?)"/>, but with a location <em>per
    /// source</em> rather than one for all of them.
    /// <para>
    /// A host that names each script — so a stack trace says which one threw instead of the
    /// engine's default <c>vm.js</c> — passes a different location for every source, and the
    /// location is part of the cache key. Compiling them all under one location would file every
    /// entry where the consuming <c>Eval</c> does not look, so the work would be done twice and the
    /// overlap this type exists for would be lost silently. This overload is how a naming host keeps
    /// it.
    /// </para>
    /// </summary>
    public static ScriptCompileAhead? Start(
        JSContext context,
        IReadOnlyList<ScriptCompileUnit> units)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(units);

        var budget = _maxDegreeOfParallelism;
        if (budget <= 1)
            return null;

        if (units.Count < MinimumScriptsToOverlap)
        {
            if (CollectDiagnostics && units.Count > 0)
                Interlocked.Increment(ref _documentsBelowMinimum);
            return null;
        }

        // Read on the calling thread, from the context that will consume the results. A worker
        // cannot read either: JSEngine.Current is [ThreadStatic] and null there, so
        // CoreScript.Compile would key the entry under a default JSCompilationOptions and file it
        // where nothing looks.
        var cache = context.CodeCache;
        if (cache is null)
            return null;

        var options = context.CompilationOptions;

        if (CollectDiagnostics)
        {
            Interlocked.Increment(ref _documentsCompiledAhead);
            Interlocked.Add(ref _scriptsQueued, units.Count);
        }

        var pending = new List<ScriptCompileUnit>(units.Count);
        foreach (var unit in units)
        {
            if (!string.IsNullOrEmpty(unit.Source))
                pending.Add(unit);
        }

        if (pending.Count == 0)
            return null;

        var threads = Math.Min(budget, pending.Count);

        // Task.Run rather than Parallel.For on this thread: the caller must get control back
        // immediately, since the whole point is that it goes on parsing while these run.
        var work = Task.Run(() => Parallel.For(
            0,
            pending.Count,
            new ParallelOptions { MaxDegreeOfParallelism = threads },
            i => CompileOne(pending[i].Source, pending[i].Location, cache, options)));

        return new ScriptCompileAhead(work);
    }

    /// <summary>
    /// Waits for every queued compile to finish. Never throws: a compile that failed has already
    /// been discarded by the cache and belongs to the consume site.
    /// </summary>
    /// <remarks>
    /// Joining is tidiness rather than correctness — the consume site is safe against a compile
    /// that is in flight (it blocks on the same <c>Lazy</c>), never started (it compiles inline) or
    /// finished (it reads the entry). What the join buys is that a document's compiles do not
    /// outlive the document, which matters for the next one's measurement and for a host that tears
    /// its context down the moment serialization is over.
    /// </remarks>
    public void Join()
    {
        try
        {
            _work.GetAwaiter().GetResult();
        }
        catch
        {
            // CompileOne swallows per-source failures, so reaching here means the loop itself
            // faulted. There is nothing for this type to report: every source is still compilable
            // at its consume site, which is where a failure is supposed to surface.
        }
    }

    /// <inheritdoc cref="Join" />
    public void Dispose() => Join();

    private static void CompileOne(
        string source,
        string? location,
        ICodeCache cache,
        JSCompilationOptions options)
    {
        try
        {
            CoreScript.Compile(source, location, args: null, codeCache: cache, compilationOptions: options);
            if (CollectDiagnostics)
                Interlocked.Increment(ref _scriptsCompiled);
        }
        catch
        {
            // Deliberately total. A syntax error, a compiler limit, a missing compiler assembly —
            // every one of them is a condition the consuming Eval already handles, on a thread with
            // a live context to raise it from. The cache removed the entry whose factory threw, so
            // the consume site recompiles rather than inheriting a poisoned one.
            if (CollectDiagnostics)
                Interlocked.Increment(ref _scriptsFailed);
        }
    }

    private static int ReadConfiguredDegree()
    {
        var configured = Environment.GetEnvironmentVariable(ThreadsEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) &&
            int.TryParse(configured, out var threads) &&
            threads > 0)
        {
            return threads;
        }

        return Environment.ProcessorCount;
    }
}
