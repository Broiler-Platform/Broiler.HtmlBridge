using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// What a dedicated worker a page starts may compile at run time: the page's own decision about
/// <c>'unsafe-eval'</c>, carried into the realm the worker runs in.
/// </summary>
/// <remarks>
/// <para>
/// <b>A worker used to be a way round the page's policy.</b> The page's realm refused <c>eval</c>,
/// the <c>Function</c> constructors and <c>ShadowRealm.prototype.evaluate</c>, while the realm a
/// <c>Worker</c> builds on its own thread refused none of them, so a page under a policy withholding
/// <c>'unsafe-eval'</c> could start a worker and compile there instead.
/// </para>
/// <para>
/// <b>The result comes back as a message, never through a route under test.</b> The worker's script is
/// a classic script read from a file, which <c>'unsafe-eval'</c> does not govern. It runs each attempt
/// inside its own try/catch and posts the outcome string; the page's <c>onmessage</c> handler, a
/// function, writes it into <c>#out</c>, and the test reads the serialized page. Nothing on that path
/// compiles a string.
/// </para>
/// <para>
/// <b>The page is driven through an interactive session, because a worker replies on its own
/// thread.</b> <c>Execute</c> drains the page's event loop until no work is due within its load window,
/// or its iteration budget runs out, and then tears the bridge down, which terminates a worker whose
/// reply has not been queued by then. A session keeps the page alive, and the test steps its loop
/// whenever the worker's reply has been queued, under a wall-clock bound, so a worker that never
/// answers fails the test instead of hanging it. A worker whose
/// script throws reports through <c>onerror</c>, and a <c>Worker</c> constructor that throws is caught by
/// the page, and each writes the failure into <c>#out</c> the same way.
/// </para>
/// </remarks>
public class WorkerEvaluationPolicyTests
{
    private const string Forbidding = "script-src 'self'";
    private const string Permitting = "script-src 'self' 'unsafe-eval'";
    // A page on the file system: only a file: document has local files read for it, and a worker
    // script is always one (the host resolves file-shaped specifiers only).
    private static string PageUrlIn(DirectoryInfo directory) =>
        new Uri(Path.Combine(directory.FullName, "page.html")).AbsoluteUri;
    private const string Waiting = "waiting";

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Every route that compiles a string at run time and is governed by <c>'unsafe-eval'</c>.</summary>
    public static TheoryData<string> RefusedRoutes => new()
    {
        "eval('6 * 7')",
        "(0, eval)('6 * 7')",
        "new Function('return 7')()",
        "Function('a', 'b', 'return a + b')(3, 4)",
        "(function () {}).constructor('return 7')()",
        "Reflect.construct(Function, ['return 7'])()",
        "typeof new Function()",
        "typeof new (Object.getPrototypeOf(async function () {}).constructor)()",
        "typeof new (Object.getPrototypeOf(function* () {}).constructor)()",
        "typeof new (Object.getPrototypeOf(async function* () {}).constructor)()",
        "new ShadowRealm().evaluate('6 * 7')",
    };

    /// <summary>The same routes, and what each answers when it is allowed to compile.</summary>
    public static TheoryData<string, string> PermittedRoutes => new()
    {
        { "eval('6 * 7')", "ok:42" },
        { "(0, eval)('6 * 7')", "ok:42" },
        { "new Function('return 7')()", "ok:7" },
        { "Function('a', 'b', 'return a + b')(3, 4)", "ok:7" },
        { "(function () {}).constructor('return 7')()", "ok:7" },
        { "Reflect.construct(Function, ['return 7'])()", "ok:7" },
        { "typeof new Function()", "ok:function" },
        { "typeof new (Object.getPrototypeOf(async function () {}).constructor)()", "ok:function" },
        { "typeof new (Object.getPrototypeOf(function* () {}).constructor)()", "ok:function" },
        { "typeof new (Object.getPrototypeOf(async function* () {}).constructor)()", "ok:function" },
        { "new ShadowRealm().evaluate('6 * 7')", "ok:42" },
    };

    /// <summary>
    /// A worker script that attempts <paramref name="expression"/> and posts either its value or the name
    /// of whatever was thrown, so a refusal and a success are told apart inside the worker.
    /// </summary>
    private static string Attempting(string expression) =>
        "function attempt(f) {" +
        "  try { return 'ok:' + String(f()); }" +
        "  catch (e) { return 'refused:' + ((e && e.name) || 'unnamed'); }" +
        "}" +
        $"postMessage(attempt(function () {{ return {expression}; }}));";

    /// <summary>
    /// Starts a worker running <paramref name="workerSource"/> from a page whose markup declares
    /// <paramref name="pagePolicy"/> (no policy when <see langword="null"/>), on an engine whose own
    /// policy is <paramref name="hostPolicy"/>, and answers the first thing the page writes into <c>#out</c>.
    /// </summary>
    /// <param name="pagePolicy">The policy the page's markup declares, or <see langword="null"/> for none.</param>
    /// <param name="workerSource">The worker's top-level script.</param>
    /// <param name="hostPolicy">The policy set on the engine, or <see langword="null"/> for none.</param>
    /// <param name="importedSource">
    /// When set, written beside the worker script as <c>imported.js</c>, for a worker that imports it.
    /// </param>
    private static string WorkerReply(
        string? pagePolicy,
        string workerSource,
        string? hostPolicy = null,
        string? importedSource = null)
    {
        var directory = Directory.CreateTempSubdirectory("broiler-worker-policy-");
        try
        {
            var workerPath = Path.Combine(directory.FullName, "worker.js");
            File.WriteAllText(workerPath, workerSource);
            if (importedSource is not null)
                File.WriteAllText(Path.Combine(directory.FullName, "imported.js"), importedSource);

            var pageHtml = CspFixture.MetaPage(pagePolicy, $"<div id=\"out\">{Waiting}</div>");

            // An apostrophe in the temporary path would end the string literal; the host decodes the
            // escape when it turns the URL back into a path.
            var workerUrl = new Uri(workerPath).AbsoluteUri.Replace("'", "%27");

            var engine = new ScriptEngine { Csp = CspFixture.Csp(hostPolicy) };
            using var session = engine.ExecuteInteractive(
                [
                    "try {" +
                    $"  var worker = new Worker('{workerUrl}');" +
                    "  worker.onmessage = function (e) { document.getElementById('out').textContent = String(e.data); };" +
                    "  worker.onerror = function (e) { document.getElementById('out').textContent = 'error:' + e.message; };" +
                    "} catch (e) {" +
                    "  document.getElementById('out').textContent = 'threw:' + ((e && e.name) || 'unnamed');" +
                    "}"
                ],
                [],
                pageHtml,
                PageUrlIn(directory));

            Assert.NotNull(session);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var html = session!.CurrentHtml();
                var outcome = Out(html);
                if (outcome != Waiting)
                    return outcome;

                if (clock.Elapsed > ReplyTimeout)
                    Assert.Fail($"The worker did not answer within {ReplyTimeout.TotalSeconds} s: {html}");

                if (session.HasPendingWork)
                    session.Step();
                else
                    Thread.Sleep(10);
            }
        }
        finally
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A scanner holding the file open must not fail a test that has already answered.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// What <c>#out</c> holds in <paramref name="html"/>, decoded. The page is read from a live
    /// session rather than from a completed call, so the slice is taken directly.
    /// </summary>
    private static string Out(string html) => PageProbe.OutOf(html, decode: true);

    // -- refused: the page's policy forbids 'unsafe-eval' ----------------------------------------------

    /// <summary>
    /// Under a page policy that withholds <c>'unsafe-eval'</c>, every route a worker has to compile a
    /// string is refused with a <c>SyntaxError</c>, <c>eval</c> included: a worker has no stub standing in
    /// front of its realm's refusal.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedRoutes))]
    public void AWorkerOnAPageWhosePolicyForbidsUnsafeEvalRefusesEveryCompilingRoute(string route)
    {
        Assert.Equal("refused:SyntaxError", WorkerReply(Forbidding, Attempting(route)));
    }

    /// <summary>
    /// A script the worker imports runs in the worker's realm, so what it asks to compile meets the same
    /// refusal, and under a permitting policy it compiles.
    /// </summary>
    [Theory]
    [InlineData(Forbidding, "refused:SyntaxError")]
    [InlineData(Permitting, "ok:7")]
    public void AScriptAWorkerImportsMeetsTheWorkersDecision(string pagePolicy, string expected)
    {
        Assert.Equal(
            expected,
            WorkerReply(
                pagePolicy,
                "importScripts('imported.js');",
                importedSource: Attempting("new Function('return 7')()")));
    }

    /// <summary>
    /// The refusal belongs to the worker's realm, not to its top-level script: an attempt made from a
    /// timer callback, which the worker's loop invokes after that script has finished, meets the same
    /// decision.
    /// </summary>
    [Theory]
    [InlineData(Forbidding, "refused:SyntaxError")]
    [InlineData(Permitting, "ok:7")]
    public void AnAttemptFromAWorkerTimerMeetsTheWorkersDecision(string pagePolicy, string expected)
    {
        Assert.Equal(
            expected,
            WorkerReply(
                pagePolicy,
                "setTimeout(function () {" + Attempting("new Function('return 7')()") + "}, 10);"));
    }

    /// <summary>
    /// The decision the worker inherits is the page realm's, so a policy the host sets on the engine
    /// reaches the worker of a page whose markup declares none.
    /// </summary>
    [Fact]
    public void AWorkerFollowsAPolicyTheHostSetWhenThePageDeclaresNone()
    {
        Assert.Equal(
            "refused:SyntaxError",
            WorkerReply(pagePolicy: null, Attempting("new Function('return 7')()"), hostPolicy: Forbidding));
    }

    // -- controls: the refusal is the policy's, and nothing else stops the worker ------------------------

    /// <summary>The same routes compile in a worker on a page whose policy permits <c>'unsafe-eval'</c>.</summary>
    [Theory]
    [MemberData(nameof(PermittedRoutes))]
    public void AWorkerOnAPageWhosePolicyPermitsUnsafeEvalCompilesEveryRoute(string route, string answer)
    {
        Assert.Equal(answer, WorkerReply(Permitting, Attempting(route)));
    }

    /// <summary>A page that states no policy is not a page that forbids evaluation, in its worker either.</summary>
    [Theory]
    [MemberData(nameof(PermittedRoutes))]
    public void AWorkerOnAPageWithNoPolicyCompilesEveryRoute(string route, string answer)
    {
        Assert.Equal(answer, WorkerReply(pagePolicy: null, Attempting(route)));
    }

    /// <summary>
    /// Under a forbidding policy the worker's own script still runs and still reaches the page: the refusal
    /// is of what the worker asks to compile, not of the worker.
    /// </summary>
    [Fact]
    public void AWorkersOwnScriptStillRunsUnderAForbiddingPolicy()
    {
        Assert.Equal("ok:42", WorkerReply(Forbidding, Attempting("6 * 7")));
    }
}
