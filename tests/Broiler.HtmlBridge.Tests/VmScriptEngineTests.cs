#if BROILER_VM_JS

using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Scripting;

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
    /// <c>eval</c> is refused whatever the page says, because answering it needs the profile's
    /// source-provider capability and this engine registers none. The assertion is that the refusal
    /// is reported as a script failure rather than crashing the host or passing silently.
    /// </summary>
    [Fact]
    public void EvalIsRefused()
    {
        Assert.False(Engine().Execute(["eval('1 + 1');"]));
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
