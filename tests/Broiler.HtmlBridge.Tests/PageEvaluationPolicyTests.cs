using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// What a page may evaluate at run time, decided by its own Content-Security-Policy.
/// <para>
/// <b>These are the first tests in this repository where a page's policy reaches the realm it runs
/// in.</b> A browser's realm is always ADOPTED — the host builds the context and the bridge wraps
/// it — and the adopting constructor used to invent a permissive policy, so every narrowing a host
/// could express was discarded on exactly the path that matters. The realm-level suite could assert
/// that a realm built without guest evaluation refuses it; nothing could assert that a PAGE was ever
/// given such a realm, because none ever was.
/// </para>
/// <para>
/// <b><c>new Function</c> is the case worth having.</b> <c>eval</c> was already refused, by a stub
/// that replaces one global binding in <c>ScriptEngine</c>. That stub cannot see <c>new Function</c>,
/// and it cannot see <c>Function.prototype.constructor</c> either, so a page under a policy
/// forbidding <c>'unsafe-eval'</c> could compile whatever it liked by the second route. The refusal
/// now happens at the engine's own eval hook, which fires for the dynamic-function constructor and
/// for every function kind.
/// </para>
/// </summary>
public class PageEvaluationPolicyTests
{
    private const string PageUrl = "https://example.test/policy";

    /// <summary>
    /// Runs <paramref name="script"/> against a page carrying <paramref name="policy"/> and answers
    /// what it wrote to <c>#out</c>.
    /// </summary>
    private static string Run(string policy, string script)
    {
        var pageHtml =
            "<html><head>" +
            $"<meta http-equiv=\"Content-Security-Policy\" content=\"{policy}\">" +
            "</head><body><div id=\"out\">nothing</div></body></html>";

        var html = new ScriptEngine().Execute(
            [$"document.getElementById('out').textContent = String({script});"],
            pageHtml,
            PageUrl);

        Assert.NotNull(html);

        const string open = "<div id=\"out\">";
        var start = html!.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no #out div in serialized output: {html}");
        start += open.Length;
        var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"unterminated #out div in serialized output: {html}");
        return html[start..end];
    }

    /// <summary>
    /// Attempts <paramref name="expression"/> and answers either its value or the name of whatever
    /// was thrown, so a refusal and a success are told apart by the page rather than by the harness.
    /// </summary>
    private static string Attempt(string expression) =>
        "(function () {" +
        $"  try {{ return 'ok:' + String({expression}); }}" +
        "  catch (e) { return 'refused:' + ((e && e.name) || 'unnamed'); }" +
        "})()";

    [Fact]
    public void APolicyForbiddingUnsafeEvalRefusesTheFunctionConstructor()
    {
        Assert.Equal(
            "refused:SyntaxError",
            Run("script-src 'self'", Attempt("new Function('return 7')()")));
    }

    /// <summary>
    /// The control, and the one that says the refusal above is the policy's doing: the same page
    /// under a policy that permits evaluation compiles and runs the same function.
    /// </summary>
    [Fact]
    public void APolicyPermittingUnsafeEvalAllowsTheFunctionConstructor()
    {
        Assert.Equal(
            "ok:7",
            Run("script-src 'self' 'unsafe-eval'", Attempt("new Function('return 7')()")));
    }

    /// <summary>
    /// <c>Function.prototype.constructor</c> is the route a global stub cannot fence, and it is
    /// refused by the same hook for the same reason.
    /// </summary>
    [Fact]
    public void APolicyForbiddingUnsafeEvalRefusesTheConstructorReachedThroughAFunction()
    {
        Assert.Equal(
            "refused:SyntaxError",
            Run("script-src 'self'", Attempt("(function () {}).constructor('return 7')()")));
    }

    /// <summary>The control for that route.</summary>
    [Fact]
    public void APolicyPermittingUnsafeEvalAllowsTheConstructorReachedThroughAFunction()
    {
        Assert.Equal(
            "ok:7",
            Run("script-src 'self' 'unsafe-eval'", Attempt("(function () {}).constructor('return 7')()")));
    }

    /// <summary>
    /// And a page whose policy forbids evaluation still runs its own script — the assertion the
    /// three-member source contract exists to make, checked here at the level a page can see.
    /// </summary>
    [Fact]
    public void APolicyForbiddingUnsafeEvalStillRunsThePagesOwnScript()
    {
        Assert.Equal("ok:42", Run("script-src 'self'", Attempt("6 * 7")));
    }
}
