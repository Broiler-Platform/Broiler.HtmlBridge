using System.Net;

using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Scripting;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// What a script may compile at run time on the two entry points that build no document,
/// <c>ScriptEngine.Execute(scripts)</c> and <c>ScriptEngine.ExecuteDetailed(scripts)</c>, under the
/// policy a host sets on <see cref="ScriptEngine.Csp"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A document's script and a document-free script meet one refusal.</b> Under a policy that
/// withholds <c>'unsafe-eval'</c>, every route but <c>eval</c> that compiles a string at run time is
/// refused with the <c>SyntaxError</c> a document's script catches (<see cref="PageEvaluationPolicyTests"/>),
/// and <c>eval</c> meets the stub, whose error is not a <c>SyntaxError</c> and has the name a document's
/// script catches (<see cref="EvalIsAnsweredByTheStubAsADocumentsIs"/>). Under a policy that permits it,
/// or with no policy, nothing is refused.
/// </para>
/// <para>
/// <b>Results come back as a thrown value or through the page, never through a route under test.</b> A
/// document-free probe runs the attempt inside its own try/catch and throws the outcome string after a
/// marker, and <c>ExecuteDetailed</c> reports it as the error's message: a throw is syntax, not a compile.
/// <c>Execute</c> answers only a bool, so its scripts throw only when the outcome differs from the one
/// expected, and <see cref="TheExecutePredicateCanFail"/> shows that the predicate carries information.
/// A document's script writes its outcome to <c>#out</c>, read back from the serialized page. The two
/// uncaught-refusal tests run the route bare and read the errors, or the bool, directly.
/// </para>
/// </remarks>
public class DocumentFreeEvaluationPolicyTests
{
    private const string Forbidding = "script-src 'self'";
    private const string Permitting = "script-src 'self' 'unsafe-eval'";
    private const string PageUrl = "https://example.test/document-free";
    private const string Marker = "outcome:";

    private static ContentSecurityPolicy? Policy(string? text)
    {
        if (text is null)
            return null;

        var policy = new ContentSecurityPolicy();
        policy.Parse(text);
        return policy;
    }

    /// <summary>The probe <see cref="PageEvaluationPolicyTests"/> uses: a value, or the name of what was thrown.</summary>
    private static string Attempt(string expression) =>
        "(function () {" +
        $"  try {{ return 'ok:' + String({expression}); }}" +
        "  catch (e) { return 'refused:' + ((e && e.name) || 'unnamed'); }" +
        "})()";

    /// <summary>A script that throws <paramref name="expression"/>'s value after the marker.</summary>
    private static string Report(string expression) => $"throw '{Marker}' + {expression};";

    /// <summary>
    /// Runs <paramref name="scripts"/> through <c>ExecuteDetailed</c> and answers what the last one threw
    /// after the marker. Exactly one error, from the last script, is required, so a script before it that
    /// failed for any other reason fails the test rather than reading as an outcome.
    /// </summary>
    private static string Outcome(ScriptEngine engine, params string[] scripts)
    {
        var result = engine.ExecuteDetailed(scripts);

        Assert.False(result.Success, "the last script always throws its outcome");
        var error = Assert.Single(result.Errors);
        Assert.Equal(scripts.Length - 1, error.ScriptIndex);
        var at = error.Message.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no outcome marker in: {error.Message}");
        return error.Message[(at + Marker.Length)..];
    }

    /// <summary>One attempt under <paramref name="policy"/>, through <c>ExecuteDetailed</c>.</summary>
    private static string Detailed(string? policy, string expression) =>
        Outcome(new ScriptEngine { Csp = Policy(policy) }, Report(Attempt(expression)));

    /// <summary>Whether <c>Execute</c> ran the attempt and it produced <paramref name="expected"/>.</summary>
    private static bool Plain(string? policy, string expression, string expected) =>
        new ScriptEngine { Csp = Policy(policy) }
            .Execute([$"if ({Attempt(expression)} !== '{expected}') throw 0;"]);

    /// <summary>What a document's own script, under a meta <paramref name="policy"/>, writes for <paramref name="expression"/>.</summary>
    private static string DocumentOutcome(string policy, string expression)
    {
        var pageHtml =
            "<html><head>" +
            $"<meta http-equiv=\"Content-Security-Policy\" content=\"{policy}\">" +
            "</head><body><div id=\"out\">nothing</div></body></html>";

        var html = new ScriptEngine().Execute(
            [$"document.getElementById('out').textContent = {expression};"],
            pageHtml,
            PageUrl);

        Assert.NotNull(html);

        const string open = "<div id=\"out\">";
        var start = html!.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no #out div in serialized output: {html}");
        start += open.Length;
        var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"unterminated #out div in serialized output: {html}");
        return WebUtility.HtmlDecode(html[start..end]);
    }

    /// <summary>Every route that compiles a string at run time and is governed by <c>'unsafe-eval'</c>, bar <c>eval</c>.</summary>
    public static TheoryData<string> RefusedRoutes => new()
    {
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

    // -- refused under a policy that forbids 'unsafe-eval' --------------------------------------------

    /// <summary>Through <c>ExecuteDetailed(scripts)</c>, every route is refused with a <c>SyntaxError</c>.</summary>
    [Theory]
    [MemberData(nameof(RefusedRoutes))]
    public void ExecuteDetailedRefusesEveryCompilingRouteUnderAForbiddingPolicy(string route)
    {
        Assert.Equal("refused:SyntaxError", Detailed(Forbidding, route));
    }

    /// <summary>
    /// The same routes through <c>Execute(scripts)</c>, whose only channel is whether every script ran
    /// without throwing: the script throws unless the route was refused with a <c>SyntaxError</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedRoutes))]
    public void ExecuteRefusesEveryCompilingRouteUnderAForbiddingPolicy(string route)
    {
        Assert.True(Plain(Forbidding, route, "refused:SyntaxError"));
    }

    /// <summary>
    /// A callback the host drains between scripts, and a promise reaction, are refused too. The outcome
    /// is read by a later script, so an attempt that never ran reads as <c>pending</c> and fails.
    /// </summary>
    [Theory]
    [InlineData("queueMicrotask(function () { r = PROBE; });")]
    [InlineData("Promise.resolve().then(function () { r = PROBE; });")]
    public void ARouteReachedFromQueuedWorkIsRefused(string schedule)
    {
        var outcome = Outcome(
            new ScriptEngine { Csp = Policy(Forbidding) },
            "var r = 'pending'; " + schedule.Replace("PROBE", Attempt("new Function('return 7')()")),
            Report("r"));

        Assert.NotEqual("pending", outcome);
        Assert.Equal("refused:SyntaxError", outcome);
    }

    /// <summary>
    /// One refusal, asserted as behaviour: the error a document-free script catches is, name and message,
    /// the one a document's script catches under the same policy.
    /// </summary>
    [Fact]
    public void TheRefusalIsTheOneADocumentMeets()
    {
        const string probe =
            "(function () { try { new Function('return 1'); return 'compiled'; } " +
            "catch (e) { return e.name + '|' + e.message; } })()";

        var documentOutcome = DocumentOutcome(Forbidding, probe);
        var documentFreeOutcome = Outcome(new ScriptEngine { Csp = Policy(Forbidding) }, Report(probe));

        Assert.StartsWith("SyntaxError|", documentOutcome);
        Assert.Equal(documentOutcome, documentFreeOutcome);
    }

    /// <summary>
    /// Every route in <see cref="RefusedRoutes"/> meets the same error: one name, one message, and a
    /// <c>SyntaxError</c> by <c>instanceof</c>. <c>eval</c> is not among them because the stub answers it;
    /// see <see cref="EvalIsAnsweredByTheStubAsADocumentsIs"/>.
    /// </summary>
    [Fact]
    public void EveryRouteButEvalMeetsOneRefusal()
    {
        var tries = string.Join(",", RefusedRoutes.Select(row => $"function () {{ return {(string)row[0]}; }}"));
        var probe =
            "(function () {" +
            "  var keys = [];" +
            $"  var tries = [{tries}];" +
            "  for (var i = 0; i < tries.length; i++) {" +
            "    var key;" +
            "    try { tries[i](); key = 'compiled'; }" +
            "    catch (e) { key = e.name + '|' + (e instanceof SyntaxError) + '|' + e.message; }" +
            "    if (keys.indexOf(key) < 0) keys.push(key);" +
            "  }" +
            "  return keys.join(' || ');" +
            "})()";

        var outcome = Outcome(new ScriptEngine { Csp = Policy(Forbidding) }, Report(probe));

        Assert.DoesNotContain(" || ", outcome);
        Assert.StartsWith("SyntaxError|true|", outcome);
        Assert.Contains("'unsafe-eval'", outcome);
    }

    /// <summary>
    /// An uncaught refusal fails the script that asked, and the script after it still runs: it reports
    /// that it ran by throwing the marker, so its error is the evidence.
    /// </summary>
    [Fact]
    public void AnUncaughtRefusalFailsItsScriptAndTheNextStillRuns()
    {
        var result = new ScriptEngine { Csp = Policy(Forbidding) }
            .ExecuteDetailed(["new Function('return 7');", $"throw '{Marker}ran';"]);

        Assert.Collection(
            result.Errors,
            first =>
            {
                Assert.Equal(0, first.ScriptIndex);
                Assert.Contains("'unsafe-eval'", first.Message);
            },
            second =>
            {
                Assert.Equal(1, second.ScriptIndex);
                Assert.Contains($"{Marker}ran", second.Message);
            });
    }

    /// <summary>
    /// An uncaught refusal makes <c>Execute(scripts)</c> answer <see langword="false"/>; the same script under
    /// a permitting policy answers <see langword="true"/>.
    /// </summary>
    [Fact]
    public void ExecuteReportsAnUncaughtRefusalAsAFailure()
    {
        Assert.False(new ScriptEngine { Csp = Policy(Forbidding) }.Execute(["new Function('return 7');"]));
        Assert.True(new ScriptEngine { Csp = Policy(Permitting) }.Execute(["new Function('return 7');"]));
    }

    /// <summary>The refusal is taken before any strictness applies, so the strict-mode prefix changes nothing.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StrictModeDoesNotChangeTheRefusal(bool strict)
    {
        var engine = new ScriptEngine { Csp = Policy(Forbidding), StrictModeEnabled = strict };

        Assert.Equal("refused:SyntaxError", Outcome(engine, Report(Attempt("new Function('return 7')()"))));
    }

    /// <summary>
    /// The policy is read per call, not once per engine: after a refusing call, clearing <c>Csp</c> lets
    /// the next call on the same engine compile, and setting it again makes the call after that refuse.
    /// </summary>
    [Fact]
    public void ThePolicyIsReadPerCall()
    {
        var engine = new ScriptEngine { Csp = Policy(Forbidding) };
        var probe = Report(Attempt("new Function('return 7')()"));

        Assert.Equal("refused:SyntaxError", Outcome(engine, probe));

        engine.Csp = null;

        Assert.Equal("ok:7", Outcome(engine, probe));

        engine.Csp = Policy(Forbidding);

        Assert.Equal("refused:SyntaxError", Outcome(engine, probe));
    }

    // -- controls and guards: green before and after ---------------------------------------------------

    /// <summary>A policy that allows <c>'unsafe-eval'</c> compiles every route, through both entry points.</summary>
    [Theory]
    [MemberData(nameof(PermittedRoutes))]
    public void APolicyPermittingUnsafeEvalCompilesEveryRoute(string route, string answer)
    {
        Assert.Equal(answer, Detailed(Permitting, route));
        Assert.True(Plain(Permitting, route, answer));
    }

    /// <summary>No policy is not a forbidding policy.</summary>
    [Theory]
    [MemberData(nameof(PermittedRoutes))]
    public void NoPolicyCompilesEveryRoute(string route, string answer)
    {
        Assert.Equal(answer, Detailed(null, route));
        Assert.True(Plain(null, route, answer));
    }

    /// <summary>
    /// The ShadowRealm rows above cannot pass through a missing constructor: a document-free context has
    /// one to refuse.
    /// </summary>
    [Fact]
    public void TheDocumentFreeContextHasAShadowRealmToRefuse()
    {
        Assert.Equal("ok:function", Detailed(Forbidding, "typeof ShadowRealm"));
    }

    /// <summary>The script the host hands in still runs under a forbidding policy; this also pins the result channel.</summary>
    [Fact]
    public void TheHostsOwnScriptStillRunsUnderAForbiddingPolicy()
    {
        Assert.Equal("ok:42", Detailed(Forbidding, "6 * 7"));
        Assert.True(Plain(Forbidding, "6 * 7", "ok:42"));
    }

    /// <summary>The <c>Execute</c> predicate answers false when the outcome is not the one expected.</summary>
    [Fact]
    public void TheExecutePredicateCanFail()
    {
        Assert.False(Plain(Forbidding, "6 * 7", "ok:41"));
    }

    /// <summary>
    /// <c>eval</c> keeps the stub's answer on these paths, and it is the answer a document's script gets:
    /// a call whose callee is not the intrinsic <c>eval</c> never reaches the engine's hook. Only error names
    /// are compared, because the stub's message carries a CLR exception's text.
    /// </summary>
    [Theory]
    [InlineData("eval('6 * 7')")]
    [InlineData("(0, eval)('6 * 7')")]
    public void EvalIsAnsweredByTheStubAsADocumentsIs(string route)
    {
        var documentFree = Detailed(Forbidding, route);

        Assert.StartsWith("refused:", documentFree);
        Assert.NotEqual("refused:SyntaxError", documentFree);
        Assert.Equal(DocumentOutcome(Forbidding, Attempt(route)), documentFree);

        Assert.Equal("ok:42", Detailed(Permitting, route));
        Assert.Equal("ok:42", Detailed(null, route));
    }

    /// <summary>A document call's meta policy is restored away, so a later document-free call reads none.</summary>
    [Fact]
    public void ADocumentsPolicyIsNotLeftBehind()
    {
        var engine = new ScriptEngine();
        var pageHtml =
            "<html><head>" +
            $"<meta http-equiv=\"Content-Security-Policy\" content=\"{Forbidding}\">" +
            "</head><body></body></html>";

        Assert.NotNull(engine.Execute(["var x = 1;"], pageHtml, PageUrl));
        Assert.Null(engine.Csp);
        Assert.Equal("ok:7", Outcome(engine, Report(Attempt("new Function('return 7')()"))));
    }
}
