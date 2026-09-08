#if BROILER_VM_JS

using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;
using Broiler.VM.Profile.JavaScript.Compiler;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// What the <c>Debug-VM</c> and <c>Release-VM</c> configurations actually buy: script running on
/// the Broiler.VM JavaScript profile, and the document paths going somewhere else.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole file is inside <c>#if BROILER_VM_JS</c>, which is the point of it.</b> The type
/// under test is only referenced under those two configurations, so the suite compiles unchanged
/// under <c>Debug</c> and <c>Release</c> and gains these cases under the pair — which is what makes
/// the configuration gated on behaviour rather than on whether it links.
/// </para>
/// <para>
/// Everything here goes through <see cref="IScriptEngine"/>'s public surface. The delegation cases
/// use a recording stub rather than a real <c>ScriptEngine</c>, because what they pin is THAT the
/// call is forwarded verbatim; what the Broiler.JS engine then does with it is that engine's own
/// suite's business.
/// </para>
/// </remarks>
public class VmScriptEngineTests
{
    private static VmScriptEngine Engine(out RecordingEngine document)
    {
        document = new RecordingEngine();
        return new VmScriptEngine(document);
    }

    private static VmScriptEngine Engine() => new(new RecordingEngine());

    private const string DocumentUrl = "https://example.test/page";

    private static readonly ModuleRoot[] Roots =
    [
        new("https://example.test/main.mjs", "export const answer = 42;", "https://example.test/main.mjs"),
        new("https://example.test/entry.mjs", "export const ready = true;", "https://example.test/entry.mjs"),
    ];

    /// <summary>
    /// Runs <paramref name="work"/> and returns what the guest wrote through <c>print</c>.
    /// </summary>
    /// <remarks>
    /// <b><c>print</c> is the only channel a guest has to say something this test can read.</b> The
    /// engine registers the profile's write capability against the render log, so a script that
    /// prints is observable here and a script that merely assigns to a global is not — there is no
    /// host API to inspect a realm, which is the same boundary that keeps a DOM off this profile.
    /// That makes it the right instrument for asserting a module actually EVALUATED rather than
    /// that a promise was created.
    /// </remarks>
    private static string Printed(Func<VmScriptEngine, ScriptExecutionResult> work)
    {
        var written = new List<string>();
        void Capture(RenderLogEntry entry) => written.Add(entry.Message);

        RenderLogger.EntryLogged += Capture;
        try
        {
            work(Engine());
        }
        finally
        {
            RenderLogger.EntryLogged -= Capture;
        }

        return string.Join("\n", written);
    }

    [Fact]
    public void RunsAScriptOnTheVm()
    {
        Assert.True(Engine().Execute(["var answer = 6 * 7;"]));
    }

    [Fact]
    public void NoScriptsSucceedsWithoutComposingARuntime()
    {
        var result = Engine().ExecuteDetailed([]);

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// One artifact, one instance, one realm: the second script has to see the first one's
    /// declarations or the engine is running a document's scripts as separate programs.
    /// </summary>
    [Fact]
    public void ScriptsShareOneRealmInOrder()
    {
        Assert.True(Engine().Execute(["var a = 40;", "var b = a + 2;"]));
    }

    /// <summary>
    /// A script that throws fails, and does not take the ones after it with it — which is what a
    /// document does.
    /// </summary>
    [Fact]
    public void AThrowFailsOnlyItsOwnScript()
    {
        var result = Engine().ExecuteDetailed(["throw new Error('boom');", "var after = 1;"]);

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Equal(0, error.ScriptIndex);
        Assert.Contains("boom", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The attribution case. A compile diagnostic carries a line and a column and no unit, so a
    /// refused batch says nothing about which script it was reading; the engine compiles each one
    /// alone to find out. Getting this wrong points every author at script 0.
    /// </summary>
    [Fact]
    public void ARefusedScriptIsNamedRatherThanBlamedOnTheFirst()
    {
        var result = Engine().ExecuteDetailed(["var fine = 1;", "var broken = ;"]);

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Equal(1, error.ScriptIndex);
    }

    /// <summary>
    /// A DIRECT <c>eval</c> inside a function is refused, and the refusal comes from the profile
    /// rather than from this engine's provider.
    /// </summary>
    /// <remarks>
    /// <b>This is the sharpest limit on what "eval works" means here, so it is pinned rather than
    /// left to be discovered.</b> The profile resolves every name at lowering, so evaluated source
    /// cannot see the calling frame's bindings, and it says so in the error it throws. An indirect
    /// eval evaluates in global scope, where there is no calling frame to see, and is admitted —
    /// see the test below. Registering the source provider does not change this and could not:
    /// the refusal happens before anything is asked of the host.
    /// </remarks>
    [Fact]
    public void ADirectEvalInsideAFunctionIsRefusedByTheProfile()
    {
        var result = Engine().ExecuteDetailed(["function f() { return eval('1 + 1'); } f();"], null);

        Assert.False(result.Success);
        Assert.Contains("direct eval inside a function", Assert.Single(result.Errors).Message, StringComparison.Ordinal);
    }

    /// <summary>An indirect eval inside a function evaluates in global scope and is admitted.</summary>
    [Fact]
    public void AnIndirectEvalInsideAFunctionIsAnswered()
    {
        Assert.Contains(
            "indirect=2",
            Printed(engine => engine.ExecuteDetailed(
                ["function f() { return (0, eval)('1 + 1'); } print('indirect=' + f());"], null)));
    }

    /// <summary>
    /// <c>StrictModeEnabled</c> makes the document's scripts strict and leaves <c>eval</c>'d source
    /// alone — on BOTH engines, identically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is pinned because it looks like a bug and is not one.</b> Reading the two engines
    /// side by side suggests an inconsistency: <c>VmScriptEngine.Unit</c> passes
    /// <c>StrictModeEnabled</c> to the compiler as <c>ForceStrict</c>, while
    /// <c>VmSourceProvider</c> builds its unit with the default — so a strict-mode page appears to
    /// get strict scripts and sloppy <c>eval</c>. It does. So does Broiler.JS, whose
    /// <c>PrepareSource</c> prepends the directive to the scripts the engine runs and leaves the
    /// native <c>eval</c> untouched.
    /// </para>
    /// <para>
    /// The agreement is the point, and it is also what the specification says: an indirect
    /// <c>eval</c> evaluates a new script whose strictness comes from its own source, so a host
    /// that forced it strict would make the same page behave differently here than anywhere else.
    /// A future change that "fixes" one engine will fail this and have to fix both, or neither.
    /// </para>
    /// <para>
    /// The probe is an assignment to an undeclared name: sloppy mode creates a global, strict mode
    /// throws — so <c>Execute</c> returning <see langword="true"/> means sloppy.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StrictModeReachesDocumentScriptsAndNotEval(bool strict)
    {
        var vm = Engine();
        vm.StrictModeEnabled = strict;

        var js = new ScriptEngine { StrictModeEnabled = strict };

        // Indirect eval: sloppy on both, whatever the flag says.
        Assert.True(vm.Execute(["(0, eval)('vmProbe" + strict + " = 1;');"]));
        Assert.True(js.Execute(["(0, eval)('jsProbe" + strict + " = 1;');"]));

        // The document's own script: strict exactly when the flag says so, on both.
        Assert.Equal(!strict, vm.Execute(["vmDirect" + strict + " = 1;"]));
        Assert.Equal(!strict, js.Execute(["jsDirect" + strict + " = 1;"]));
    }

    [Fact]
    public void EvalIsAnswered()
    {
        // The VALUE and not just the absence of a throw: a refused eval and an eval that answered
        // the wrong thing are both failures, and only one of them shows up as an exception.
        Assert.Contains("eval=42", Printed(engine => engine.ExecuteDetailed(["print('eval=' + eval('21 * 2'));"], null)));
    }

    [Fact]
    public void TheFunctionConstructorIsAnswered()
    {
        Assert.Contains(
            "function=7",
            Printed(engine => engine.ExecuteDetailed(
                ["var f = new Function('return 7;'); print('function=' + f());"], null)));
    }

    /// <summary>
    /// And the same call is refused when the page's policy forbids evaluation — not by a check
    /// inside the engine, but because a runtime built for that page registers no provider at all.
    /// </summary>
    [Fact]
    public void EvalIsRefusedWhenThePolicyForbidsIt()
    {
        var engine = Engine();
        var csp = new ContentSecurityPolicy();
        csp.Parse("script-src 'self'");

        Assert.False(csp.AllowsEval);

        engine.Csp = csp;

        Assert.False(engine.Execute(["eval('1 + 1');"]));
    }

    /// <summary>
    /// A page that states no policy is not a page that forbids evaluation. Defaulting to refusal
    /// would make this engine quietly stricter than the Broiler.JS one on the very same document.
    /// </summary>
    [Fact]
    public void NoPolicyMeansEvaluationIsAnswered()
    {
        var engine = Engine();

        Assert.Null(engine.Csp);
        Assert.True(engine.Execute(["eval('1 + 1');"]));
    }

    /// <summary>
    /// A policy that permits evaluation registers the provider again, so the decision tracks the
    /// document rather than being taken once for the engine.
    /// </summary>
    [Fact]
    public void APermissivePolicyAnswersEvaluation()
    {
        var engine = Engine();
        var csp = new ContentSecurityPolicy();
        csp.Parse("script-src 'self' 'unsafe-eval'");

        Assert.True(csp.AllowsEval);

        engine.Csp = csp;

        Assert.True(engine.Execute(["eval('1 + 1');"]));
    }

    /// <summary>
    /// A dynamic <c>import()</c> of a module the document declared resolves and evaluates. The
    /// specifier is resolved by the host against the referrer's base — the same resolution
    /// <c>ScriptExtractionService</c> used to form the keys — and the module graph is compiled by
    /// the same provider that answers <c>eval</c>.
    /// </summary>
    [Fact]
    public void ADynamicImportOfADeclaredModuleEvaluates()
    {
        var written = Printed(engine => engine.ExecuteDetailed(
            ["import('./main.mjs').then(m => print('answer=' + m.answer), e => print('rejected'));"],
            Roots,
            DocumentUrl));

        Assert.Contains("answer=42", written);
    }

    /// <summary>
    /// A specifier the document never declared is not found, and the promise rejects. A browser
    /// fetches nothing on the guest's behalf here, so this is a resolution answer rather than a
    /// network one.
    /// </summary>
    /// <remarks>
    /// The rejection handler is what makes this test say anything. A rejected promise is a value,
    /// so a script that only creates one succeeds either way — asserting on the result would pass
    /// whether the import resolved, rejected, or was never attempted.
    /// </remarks>
    [Fact]
    public void ADynamicImportOfAnUndeclaredModuleRejects()
    {
        var written = Printed(engine => engine.ExecuteDetailed(
            ["import('./absent.mjs').then(m => print('resolved'), e => print('rejected'));"],
            Roots,
            DocumentUrl));

        Assert.Contains("rejected", written);
        Assert.DoesNotContain("resolved", written);
    }

    /// <summary>
    /// And with the policy forbidding evaluation there is no provider to ask, so even a module the
    /// document declared is refused. The prohibition covers <c>import()</c> and not just
    /// <c>eval</c>, because both are the same guest-initiated load.
    /// </summary>
    [Fact]
    public void ADynamicImportIsRefusedWhenThePolicyForbidsEvaluation()
    {
        var csp = new ContentSecurityPolicy();
        csp.Parse("script-src 'self'");

        var written = Printed(engine =>
        {
            engine.Csp = csp;
            return engine.ExecuteDetailed(
                ["import('./main.mjs').then(m => print('answer=' + m.answer), e => print('rejected'));"],
                Roots,
                DocumentUrl);
        });

        Assert.Contains("rejected", written);
        Assert.DoesNotContain("answer=42", written);
    }

    [Fact]
    public void DocumentBearingExecutionGoesToTheDocumentEngine()
    {
        var engine = Engine(out var document);

        var answer = engine.Execute(["var x = 1;"], "<html></html>", "https://example.test/");

        Assert.Equal(RecordingEngine.Answer, answer);
        Assert.Equal(1, document.Executions);
    }

    [Fact]
    public void InteractiveExecutionGoesToTheDocumentEngine()
    {
        var engine = Engine(out var document);

        Assert.Null(engine.ExecuteInteractive(["var x = 1;"], [], "<html></html>", null));
        Assert.Equal(1, document.InteractiveExecutions);
    }

    /// <summary>
    /// Configuration is set on both engines. A page that turned strict mode on and then rendered
    /// through the delegated path would otherwise run non-strict.
    /// </summary>
    [Fact]
    public void ConfigurationReachesTheDocumentEngine()
    {
        var engine = Engine(out var document);

        engine.StrictModeEnabled = true;

        Assert.True(document.StrictModeEnabled);
        Assert.True(engine.StrictModeEnabled);
    }

    /// <summary>There is one micro-task queue, and it is the document engine's.</summary>
    [Fact]
    public void TheMicroTaskQueueIsShared()
    {
        var engine = Engine(out var document);

        Assert.Same(document.MicroTasks, engine.MicroTasks);
    }


    /// <summary>
    /// The compilation cache: what it must save, and what it must never get wrong.
    /// </summary>
    /// <remarks>
    /// <b>Every case here counts compilations rather than timing them.</b> "The second run compiled
    /// nothing" is a fact a machine either reproduces or does not; "the second run was faster" is a
    /// measurement, and a measurement inside a test is a flake waiting for a loaded build agent.
    /// Each test uses its own cache instance so the shared one cannot make one test's result depend
    /// on another's having run first.
    /// </remarks>
    public class Caching
    {
        private static VmScriptEngine Engine(VmCompilationCache cache) =>
            new(new RecordingEngine()) { Cache = cache };

        private static VmCompilationCache Fresh() => new(maximumEntries: 8, maximumBytes: 1 << 20);

        [Fact]
        public void TheSameDocumentIsCompiledOnce()
        {
            var cache = Fresh();
            string[] scripts = ["var a = 1;", "var b = a + 1;"];

            Assert.True(Engine(cache).Execute(scripts));
            Assert.Equal(1, cache.Compilations);

            Assert.True(Engine(cache).Execute(scripts));
            Assert.Equal(1, cache.Compilations);
            Assert.Equal(1, cache.Hits);
        }

        [Fact]
        public void ADifferentDocumentIsCompiledAgain()
        {
            var cache = Fresh();

            Engine(cache).Execute(["var a = 1;"]);
            Engine(cache).Execute(["var a = 2;"]);

            Assert.Equal(2, cache.Compilations);
            Assert.Equal(0, cache.Hits);
        }

        /// <summary>
        /// Strict mode changes what the compiler emits, so it has to change the key. Serving the
        /// sloppy artifact to a strict document would run a different program than the one asked
        /// for — the failure a cache key exists to prevent.
        /// </summary>
        [Fact]
        public void StrictModeIsPartOfTheIdentity()
        {
            var cache = Fresh();
            string[] scripts = ["var a = 1;"];

            Engine(cache).Execute(scripts);

            var strict = Engine(cache);
            strict.StrictModeEnabled = true;
            strict.Execute(scripts);

            Assert.Equal(2, cache.Compilations);
            Assert.Equal(0, cache.Hits);
        }

        /// <summary>
        /// Two documents whose script lists are byte-identical SHARE an entry when no document URL
        /// is supplied — which is correct, and is what most callers get.
        /// </summary>
        /// <remarks>
        /// <c>Execute(scripts)</c> and <c>ExecuteDetailed(scripts)</c> pass no URL, so the referrer
        /// is empty and the identity is the script list and the compilation flags. Two pages with
        /// the same scripts then compile to the same program, byte for byte, and sharing is not a
        /// collision but the point. It is pinned because the opposite was asserted in this branch's
        /// first description of the cache, and prose about a key is worth exactly what a test says
        /// it is.
        /// </remarks>
        [Fact]
        public void WithoutADocumentUrlIdenticalScriptsShare()
        {
            var cache = Fresh();
            string[] scripts = ["var shared = 1;"];

            Engine(cache).Execute(scripts);
            Engine(cache).Execute(scripts);

            Assert.Equal(1, cache.Compilations);
            Assert.Equal(1, cache.Hits);
        }

        /// <summary>
        /// And with one supplied it separates them, because the URL is what a relative specifier
        /// resolves against — so the same text under two documents is two programs.
        /// </summary>
        [Fact]
        public void TheDocumentUrlIsPartOfTheIdentity()
        {
            var cache = Fresh();
            string[] scripts = ["var a = 1;"];

            Engine(cache).ExecuteDetailed(scripts, null, "https://example.test/one");
            Engine(cache).ExecuteDetailed(scripts, null, "https://example.test/two");

            Assert.Equal(2, cache.Compilations);
            Assert.Equal(0, cache.Hits);
        }

        /// <summary>A cached document still runs, and still produces its value.</summary>
        /// <remarks>
        /// The counter says compilation was skipped; this says the bytes that were served are the
        /// program. A cache that returned stale or truncated bytes would satisfy the counter and
        /// fail here.
        /// </remarks>
        [Fact]
        public void ACachedDocumentStillRuns()
        {
            var cache = Fresh();
            string[] scripts = ["print('cached=' + (20 + 22));"];

            Assert.Contains("cached=42", Printed(cache, scripts));
            Assert.Contains("cached=42", Printed(cache, scripts));
            Assert.Equal(1, cache.Compilations);
        }

        /// <summary>
        /// Two scripts differing only in a lone surrogate are two entries.
        /// </summary>
        /// <remarks>
        /// <b>This is the case that made the key hash UTF-16 code units instead of UTF-8.</b>
        /// <c>Encoding.UTF8.GetBytes</c> uses replacement fallback, so <c>\uD800</c> and
        /// <c>\uD801</c> both encode to the replacement bytes and hashed identically — and this is
        /// reachable rather than theoretical, because the profile's tokenizer accepts a lone
        /// surrogate as a legal JavaScript string element and says so. The cache would have served
        /// one page's program to another.
        /// </remarks>
        [Fact]
        public void ALoneSurrogateIsPartOfTheIdentity()
        {
            var cache = Fresh();

            Engine(cache).Execute(["var s = '\uD800';"]);
            Engine(cache).Execute(["var s = '\uD801';"]);

            Assert.Equal(2, cache.Compilations);
            Assert.Equal(0, cache.Hits);
        }

        /// <summary>A refusal is not stored, so a broken script is not cached as broken.</summary>
        [Fact]
        public void ARefusalIsNotCached()
        {
            var cache = Fresh();
            string[] scripts = ["var broken = ;"];

            Assert.False(Engine(cache).Execute(scripts));
            Assert.False(Engine(cache).Execute(scripts));

            Assert.Equal(0, cache.Hits);
        }

        /// <summary>The bound is enforced, and the least recently used entry is the one that goes.</summary>
        [Fact]
        public void TheCacheIsBounded()
        {
            var cache = new VmCompilationCache(maximumEntries: 2, maximumBytes: 1 << 20);

            Engine(cache).Execute(["var a = 1;"]);
            Engine(cache).Execute(["var b = 2;"]);
            Engine(cache).Execute(["var a = 1;"]);   // keeps the first entry the most recent
            Engine(cache).Execute(["var c = 3;"]);   // evicts `b`, the least recently used

            Assert.Equal(3, cache.Compilations);
            Assert.Equal(1, cache.Hits);

            Engine(cache).Execute(["var a = 1;"]);   // still resident
            Assert.Equal(2, cache.Hits);

            Engine(cache).Execute(["var b = 2;"]);   // was evicted, so compiles again
            Assert.Equal(4, cache.Compilations);
        }

        /// <summary>
        /// One body evaluated repeatedly is compiled once — the pattern that actually repeats.
        /// </summary>
        /// <remarks>
        /// <c>new Function</c> with one body called from a loop, or a template evaluated per row.
        /// The guest asks the host to compile every time; the host answers from what it compiled
        /// the first time.
        /// </remarks>
        [Fact]
        public void ARepeatedEvalIsCompiledOnce()
        {
            var engine = Engine(Fresh());
            var guest = Fresh();
            engine.GuestLoadCache = guest;

            Assert.True(engine.Execute(["for (var i = 0; i < 5; i++) { (0, eval)('1 + 1'); }"]));

            Assert.Equal(1, guest.Compilations);
            Assert.Equal(4, guest.Hits);
        }

        [Fact]
        public void DistinctEvalsAreCompiledSeparately()
        {
            var engine = Engine(Fresh());
            var guest = Fresh();
            engine.GuestLoadCache = guest;

            Assert.True(engine.Execute(["(0, eval)('1 + 1'); (0, eval)('2 + 2');"]));

            Assert.Equal(2, guest.Compilations);
            Assert.Equal(0, guest.Hits);
        }

        /// <summary>A cached eval still answers, and still answers the right value.</summary>
        [Fact]
        public void ARepeatedEvalStillProducesItsValue()
        {
            var engine = Engine(Fresh());
            var guest = Fresh();
            engine.GuestLoadCache = guest;

            var written = new List<string>();
            void Capture(RenderLogEntry entry) => written.Add(entry.Message);

            RenderLogger.EntryLogged += Capture;
            try
            {
                engine.Execute(["print('a=' + (0, eval)('20 + 22')); print('b=' + (0, eval)('20 + 22'));"]);
            }
            finally
            {
                RenderLogger.EntryLogged -= Capture;
            }

            var all = string.Join("\n", written);
            Assert.Contains("a=42", all);
            Assert.Contains("b=42", all);
            Assert.Equal(1, guest.Compilations);
        }

        /// <summary>
        /// The two caches are separate, and a page's evaluation cannot reach the shared one.
        /// </summary>
        /// <remarks>
        /// This is the eviction half of why guest source is scoped per engine: were it one cache, a
        /// page that evaluated enough distinct strings would push out every other page's compiled
        /// document. The timing half cannot be asserted here — it is an argument about what a page
        /// could learn, not about a counter — and is recorded in <c>GuestLoadCache</c>'s remarks.
        /// </remarks>
        [Fact]
        public void GuestSourceDoesNotEnterTheDocumentCache()
        {
            var document = Fresh();
            var guest = Fresh();
            var engine = Engine(document);
            engine.GuestLoadCache = guest;

            engine.Execute(["(0, eval)('1 + 1');"]);

            Assert.Equal(1, document.Compilations);   // the document itself, and nothing more
            Assert.Equal(1, guest.Compilations);      // the eval, in its own cache
        }

        /// <summary>
        /// The shapes the cache key mirrors, pinned so that one growing a field fails HERE.
        /// </summary>
        /// <remarks>
        /// <b>This is the way this cache is most likely to go wrong, and it cannot be caught by
        /// any test of behaviour.</b> The five records are positional, live in the Broiler.VM
        /// submodule, and take their optional parameters with defaults — so a new field added
        /// upstream compiles here unchanged, is read by the compiler, changes the artifact, and is
        /// silently absent from the key. The cache would then serve one program for another with
        /// nothing failing anywhere.
        /// <para>
        /// A gitlink bump that adds a field trips this instead. When it does, the fix is to add the
        /// field to <c>VmCompilationCache.Key</c> and then to this list — in that order.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData(typeof(JsScriptUnit), "Name,Options,Referrer,Text,ForceStrict")]
        [InlineData(typeof(JsModuleUnit), "Key,Options,Requests,Text")]
        [InlineData(typeof(JsResolvedRequest), "Key,Specifier")]
        [InlineData(typeof(JsCompileRequest), "Backend,Form,Manifest")]
        [InlineData(typeof(SliceParseOptions), "AllowTopLevelAwait,Goal,GoalIsStrict,MaximumNestingDepth")]
        public void TheKeyMirrorsEveryFieldOfTheCompilerInputs(Type shape, string expected)
        {
            var actual = shape
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(p => p.Name)
                .Where(n => n != "EqualityContract")
                .OrderBy(n => n, StringComparer.Ordinal);

            Assert.Equal(
                expected.Split(',').OrderBy(n => n, StringComparer.Ordinal),
                actual);
        }

        private static string Printed(VmCompilationCache cache, IReadOnlyList<string> scripts)
        {
            var written = new List<string>();
            void Capture(RenderLogEntry entry) => written.Add(entry.Message);

            RenderLogger.EntryLogged += Capture;
            try
            {
                Engine(cache).Execute(scripts);
            }
            finally
            {
                RenderLogger.EntryLogged -= Capture;
            }

            return string.Join("\n", written);
        }
    }

    /// <summary>
    /// An <see cref="IScriptEngine"/> that records what it was asked and answers a constant.
    /// </summary>
    private sealed class RecordingEngine : IScriptEngine
    {
        internal const string Answer = "<html><!--served by the document engine--></html>";

        internal int Executions { get; private set; }

        internal int InteractiveExecutions { get; private set; }

        public bool StrictModeEnabled { get; set; }

        public ContentSecurityPolicy? Csp { get; set; }

        public ScriptProfilingHook? Profiler { get; set; }

        public MicroTaskQueue MicroTasks { get; } = new();

        public bool Execute(IReadOnlyList<string> scripts) => throw new NotSupportedException(
            "The script-only paths must not be delegated: they are what runs on the VM.");

        public ScriptExecutionResult ExecuteDetailed(IReadOnlyList<string> scripts) =>
            throw new NotSupportedException(
                "The script-only paths must not be delegated: they are what runs on the VM.");

        public string? Execute(IReadOnlyList<string> scripts, string html) => Served();

        public string? Execute(IReadOnlyList<string> scripts, string html, string? url) => Served();

        public string? Execute(
            IReadOnlyList<string> scripts, IReadOnlyList<string> deferredScripts, string html, string? url) =>
            Served();

        public string? Execute(
            IReadOnlyList<string> scripts,
            IReadOnlyList<string> deferredScripts,
            string html,
            string? url,
            IReadOnlyList<ModuleRoot>? moduleRoots) =>
            Served();

        public InteractiveSession? ExecuteInteractive(
            IReadOnlyList<string> scripts, IReadOnlyList<string> deferredScripts, string html, string? url) =>
            Stepped();

        public InteractiveSession? ExecuteInteractive(
            IReadOnlyList<string> scripts,
            IReadOnlyList<string> deferredScripts,
            string html,
            string? url,
            IReadOnlyList<ModuleRoot>? moduleRoots) =>
            Stepped();

        private string Served()
        {
            Executions++;
            return Answer;
        }

        // Null rather than a session: InteractiveSession's constructor is internal to
        // Broiler.HtmlBridge.Scripting and takes a JSContext, which is the same fact that makes
        // ExecuteInteractive undelegatable to the VM in the first place. Null is a documented
        // answer of that method, so the test asserts on the count instead.
        private InteractiveSession? Stepped()
        {
            InteractiveExecutions++;
            return null;
        }
    }
}

#endif
