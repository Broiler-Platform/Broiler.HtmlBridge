#if BROILER_VM_JS

using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;
using Broiler.VM.Profile.JavaScript.Compiler;

namespace Broiler.HtmlBridge.Tests;

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
public partial class VmScriptEngineTests
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
    /// A DIRECT <c>eval</c> inside a function sees the calling frame's bindings.
    /// </summary>
    /// <remarks>
    /// Up to Broiler.VM 0.1.0-preview.3 the profile refused this outright, because it resolved every
    /// name at lowering and evaluated source could not reach the caller's frame. From preview.4 the
    /// artifact carries an eval scope map and the request carries what the calling site permits
    /// (<c>SliceParseOptions.IsEval</c> / <c>EvalFlags</c>), which the source provider hands to the
    /// compiler through <c>JsCompiler.TryReadProgramRequest</c>. Pinned so that a provider decoding
    /// the payload as bare text again fails here rather than as a SyntaxError on some page.
    /// </remarks>
    [Fact]
    public void ADirectEvalInsideAFunctionSeesTheCallingFrame()
    {
        Assert.Contains(
            "direct=42",
            Printed(engine => engine.ExecuteDetailed(
                ["function f() { var x = 40; return eval('x + 2'); } print('direct=' + f());"], null)));
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
        var csp = CspFixture.CspOf("script-src 'self'");

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
        var csp = CspFixture.CspOf("script-src 'self' 'unsafe-eval'");

        Assert.True(csp.AllowsEval);

        engine.Csp = csp;

        Assert.True(engine.Execute(["eval('1 + 1');"]));
    }

    private static string AttemptOnTheProfile(string expression) =>
        "(function () {" +
        $"  try {{ return 'ok:' + String({expression}); }}" +
        "  catch (e) { return 'refused:' + ((e && e.name) || 'unnamed'); }" +
        "})()";

    /// <summary>
    /// The <c>Function</c> constructor, at any arity, is refused on the profile when the policy withholds
    /// <c>'unsafe-eval'</c>, which is what <c>IScriptExecutor.Csp</c> promises of every engine, and answered
    /// under a permitting policy or none, which is this engine's own choice
    /// (<c>VmScriptEngine.AnswersGuestLoads</c>). Only the refusal is pinned, not its name: the profile's
    /// error is its own.
    /// </summary>
    [Theory]
    [InlineData("new Function('return 7')()", "ok:7")]
    [InlineData("new Function('a', 'b', 'return a + b')(3, 4)", "ok:7")]
    [InlineData("typeof new Function()", "ok:function")]
    public void TheFunctionConstructorIsAnsweredExactlyWhenThePolicyPermitsEvaluation(string route, string permitted)
    {
        var forbidding = CspFixture.CspOf("script-src 'self'");
        var refusing = Engine();
        refusing.Csp = forbidding;

        Assert.True(refusing.Execute(
            [$"var r = {AttemptOnTheProfile(route)}; if (r.indexOf('refused:') !== 0) throw 0;"]));

        var permitting = CspFixture.CspOf("script-src 'self' 'unsafe-eval'");
        var answering = Engine();
        answering.Csp = permitting;

        Assert.True(answering.Execute(
            [$"var r = {AttemptOnTheProfile(route)}; if (r !== '{permitted}') throw 0;"]));

        Assert.True(Engine().Execute(
            [$"var r = {AttemptOnTheProfile(route)}; if (r !== '{permitted}') throw 0;"]));
    }

    /// <summary>
    /// The async-function, generator and async-generator constructors are refused on the profile whatever
    /// the policy: its realm builds each constructor as a native that always throws. Each script first
    /// checks that the route reaches a function other than <c>Function</c>, so an error on the way there
    /// does not read as the refusal. Only the refusal is pinned, not its name.
    /// </summary>
    [Theory]
    [InlineData("async function () {}")]
    [InlineData("function* () {}")]
    [InlineData("async function* () {}")]
    public void TheOtherFunctionConstructorsAreRefusedOnTheProfileWhateverThePolicy(string kind)
    {
        foreach (var text in new string?[] { "script-src 'self'", "script-src 'self' 'unsafe-eval'", null })
        {
            var engine = Engine();
            engine.Csp = CspFixture.Csp(text);

            Assert.True(
                engine.Execute(
                [
                    $"var C = Object.getPrototypeOf({kind}).constructor; " +
                    "if (typeof C !== 'function' || C === Function) throw 0; " +
                    $"var r = {AttemptOnTheProfile("typeof new C()")}; if (r.indexOf('refused:') !== 0) throw 0;"
                ]),
                $"did not reach a function other than Function, or it was not refused, under policy: {text ?? "(none)"}");
        }
    }

    /// <summary>
    /// The profile has no <c>ShadowRealm</c>, so there is no <c>evaluate</c> for a policy to gate. One
    /// arriving would fail this and would have to meet the policy.
    /// </summary>
    [Fact]
    public void TheProfileHasNoShadowRealmToGate()
    {
        Assert.True(Engine().Execute(["if (typeof ShadowRealm !== 'undefined') throw 0;"]));
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
        var csp = CspFixture.CspOf("script-src 'self'");

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
