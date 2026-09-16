using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A <c>&lt;script&gt;</c> element the page creates and appends: that its program runs at all, and
/// that the policy governing it is <c>script-src</c>.
/// <para>
/// <b>Nothing exercised this path before.</b> No test in this suite constructed a script element,
/// and none constructs a worker either — so the two evaluation routes the bridge owns besides the
/// document's own scripts were carried entirely by the compiler. That mattered when both moved from
/// the eval-gated source member to <c>EvaluateClassicScript</c>: the swap type-checks
/// whether or not it is right, and a member that refused everything would have looked identical to
/// one that ran everything until a page loaded.
/// </para>
/// <para>
/// <b>The probe writes its marker BEFORE appending, and that ordering is the whole test.</b> An
/// inserted script runs synchronously inside <c>appendChild</c>, so a probe that marked <c>#out</c>
/// afterwards overwrote the program's own write and reported "did not run" for a program that had
/// just run. Written that way round first, it made all four cases agree on the wrong answer — two by
/// failing and two by passing vacuously, which is the more dangerous half.
/// </para>
/// <para>
/// The worker half is still uncovered here, and deliberately rather than by oversight: a worker
/// needs a second realm on a second thread, which is <c>JsCapabilities.WorkerRealms</c>, and driving
/// one from this harness is a larger piece of work than the vocabulary change it would guard.
/// </para>
/// </summary>
public class ScriptInsertedElementTests
{
    private const string PageUrl = "https://example.test/inserted";

    /// <summary>
    /// Runs <paramref name="script"/> against a page carrying <paramref name="policy"/>, and answers
    /// what <c>#out</c> holds once everything has settled.
    /// </summary>
    /// <remarks>
    /// The probe writes into <c>#out</c> and so does the program it inserts, with different values.
    /// Reading the surviving one distinguishes "the inserted program ran" from "it did not". Every
    /// program inserted here is inline, which <c>ScriptInsertionRunner</c> runs synchronously in
    /// <c>appendChild</c>; only a <c>src</c> script waits for its own turn on the event loop.
    /// </remarks>
    private static string Run(string? policy, string script)
    {
        var pageHtml =
            "<html><head>" +
            (policy is null
                ? string.Empty
                : $"<meta http-equiv=\"Content-Security-Policy\" content=\"{policy}\">") +
            "</head><body><div id=\"out\">nothing</div></body></html>";

        var html = new ScriptEngine().Execute([script], pageHtml, PageUrl);

        Assert.NotNull(html);

        const string open = "<div id=\"out\">";
        var start = html!.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no #out div in serialized output: {html}");
        start += open.Length;
        var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"unterminated #out div in serialized output: {html}");
        return html[start..end];
    }

    /// <summary>Creates a script element carrying <paramref name="program"/> and appends it.</summary>
    private static string InsertScript(string program) =>
        "(function () {" +
        "  document.getElementById('out').textContent = 'inserted';" +
        "  var s = document.createElement('script');" +
        $"  s.textContent = \"{program}\";" +
        "  document.body.appendChild(s);" +
        "})()";

    [Fact]
    public void AScriptInsertedElementRunsItsProgram()
    {
        Assert.Equal(
            "ran",
            Run(null, InsertScript("document.getElementById('out').textContent = 'ran';")));
    }

    /// <summary>
    /// The control, and it is the half that says the assertion above is about the program running
    /// rather than about the marker being writable: with the same insertion and a program that writes
    /// nothing, <c>#out</c> keeps what the probe put there.
    /// </summary>
    [Fact]
    public void TheProbeSurvivesWhenTheInsertedProgramWritesNothing()
    {
        Assert.Equal("inserted", Run(null, InsertScript("var untouched = 1;")));
    }

    /// <summary>
    /// A script element is governed by <c>script-src</c>, so a policy that permits evaluation and
    /// not inline script stops it. <c>'unsafe-eval'</c> is there so that the page's realm is built
    /// with guest evaluation, leaving the runner's <c>script-src</c> check as the only thing on
    /// the path that can stop the program. (This used <c>script-src 'none'</c>, which also leaves
    /// out <c>'unsafe-eval'</c>, so a program the realm refused would have read the same as one the
    /// runner refused.) The probe still runs because <c>ScriptEngine.Execute</c> evaluates the
    /// script it is handed without consulting the policy.
    /// </summary>
    [Fact]
    public void ScriptSrcGovernsAnInsertedScriptElement()
    {
        Assert.Equal(
            "inserted",
            Run(
                "script-src 'unsafe-eval'",
                InsertScript("document.getElementById('out').textContent = 'ran';")));
    }

    /// <summary>
    /// And <c>'unsafe-eval'</c> does not govern it. This is the assertion the whole three-member
    /// source contract exists to make available: a policy that forbids the page evaluating strings at
    /// run time says nothing about the page's script elements, and every browser runs them.
    /// </summary>
    [Fact]
    public void UnsafeEvalDoesNotGovernAnInsertedScriptElement()
    {
        Assert.Equal(
            "ran",
            Run(
                "script-src 'unsafe-inline'",
                InsertScript("document.getElementById('out').textContent = 'ran';")));
    }
}
