using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

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
/// now happens at the engine's own eval hook, which fires for the dynamic-function constructor of
/// every function kind at every arity, and for <c>ShadowRealm.prototype.evaluate</c> on the context
/// that constructed the ShadowRealm, which for one the page constructs is the page's own.
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
    /// And under a policy forbidding <c>'unsafe-eval'</c> the page's script still runs: the refusal
    /// reaches what the script asks to evaluate at run time, and not the script itself.
    /// </summary>
    /// <remarks>
    /// This does not reach <c>IJsSource.EvaluateClassicScript</c>.
    /// <c>ScriptEngine.Execute</c> hands the script to <c>RunPageScripts</c>, which evaluates it on
    /// the engine context directly and consults no <c>script-src</c>. What is asserted is narrower:
    /// what this policy installs, <c>ScriptEngine</c>'s <c>eval</c> stub and the realm's eval hook,
    /// does not stop a script the host evaluates. The refusals above are the <c>Function</c>
    /// constructor's, and nothing in this file calls <c>eval</c>. A classic script under the same
    /// narrowing is asserted at realm level, by
    /// <c>JsealConformanceTests.AClassicScriptRunsInARealmThatForbidsGuestEvaluation</c>.
    /// </remarks>
    [Fact]
    public void APolicyForbiddingUnsafeEvalStillRunsThePagesOwnScript()
    {
        Assert.Equal("ok:42", Run("script-src 'self'", Attempt("6 * 7")));
    }

    /// <summary>
    /// A <c>Function</c> constructor called with no arguments is refused under the same policy: it is
    /// an <c>'unsafe-eval'</c> question however few strings it is handed.
    /// </summary>
    [Fact]
    public void APolicyForbiddingUnsafeEvalRefusesAnArgumentlessFunctionConstructor()
    {
        Assert.Equal(
            "refused:SyntaxError",
            Run("script-src 'self'", Attempt("typeof new Function()")));
    }

    /// <summary>The control for that route.</summary>
    [Fact]
    public void APolicyPermittingUnsafeEvalAllowsAnArgumentlessFunctionConstructor()
    {
        Assert.Equal(
            "ok:function",
            Run("script-src 'self' 'unsafe-eval'", Attempt("typeof new Function()")));
    }

    /// <summary>
    /// The async, generator and async-generator constructors share that path, and none of them is a
    /// global a stub could replace: a page reaches each one through a function's prototype.
    /// </summary>
    [Theory]
    [InlineData("async function () {}")]
    [InlineData("function* () {}")]
    [InlineData("async function* () {}")]
    public void APolicyForbiddingUnsafeEvalRefusesTheArgumentlessSiblingConstructors(string kind)
    {
        Assert.Equal(
            "refused:SyntaxError",
            Run("script-src 'self'", Attempt($"typeof new (Object.getPrototypeOf({kind}).constructor)()")));
    }

    /// <summary>The control for those routes.</summary>
    [Theory]
    [InlineData("async function () {}")]
    [InlineData("function* () {}")]
    [InlineData("async function* () {}")]
    public void APolicyPermittingUnsafeEvalAllowsTheArgumentlessSiblingConstructors(string kind)
    {
        Assert.Equal(
            "ok:function",
            Run("script-src 'self' 'unsafe-eval'", Attempt($"typeof new (Object.getPrototypeOf({kind}).constructor)()")));
    }

    /// <summary>
    /// <c>ShadowRealm.prototype.evaluate</c> compiles the page's string in a realm of its own, and the
    /// policy that governs that string is still the page's.
    /// </summary>
    [Fact]
    public void APolicyForbiddingUnsafeEvalRefusesShadowRealmEvaluate()
    {
        Assert.Equal(
            "refused:SyntaxError",
            Run("script-src 'self'", Attempt("new ShadowRealm().evaluate('6 * 7')")));
    }

    /// <summary>The control for that route.</summary>
    [Fact]
    public void APolicyPermittingUnsafeEvalAllowsShadowRealmEvaluate()
    {
        Assert.Equal(
            "ok:42",
            Run("script-src 'self' 'unsafe-eval'", Attempt("new ShadowRealm().evaluate('6 * 7')")));
    }
}
