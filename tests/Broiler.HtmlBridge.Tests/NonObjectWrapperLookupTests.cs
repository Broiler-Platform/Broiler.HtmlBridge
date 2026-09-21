namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// What the bridge answers when page script hands a DOM entry point something that is not an object
/// where a node is wanted — a string, a number, <c>null</c>.
/// <para>
/// The bridge resolves a wrapper to its node through one reverse lookup, and half of the host
/// contracts used to put a <c>wrapper.IsObject</c> test in front of that lookup while the other half
/// called it bare. The two spellings answer the same thing: a non-object handle carries no
/// <c>ObjectIdentity</c>, so the registry reports it absent and the lookup returns <see langword="null"/>
/// exactly as the guard did. These cases pin that, by driving a formerly-guarded entry point and a
/// formerly-unguarded one with the same junk and asserting both still answer what they answered
/// before the guards came out.
/// </para>
/// </summary>
public class NonObjectWrapperLookupTests
{
    private const string PageUrl = "https://example.test/non-object-lookup";

    private const string PageHtml =
        "<html><body>" +
        "<div id=\"host\"><span id=\"child\">c</span></div>" +
        "<select id=\"sel\"><option>a</option></select>" +
        "<div id=\"out\"></div>" +
        "</body></html>";

    private static string Run(string expression) =>
        PageProbe.OutOf(PageProbe.Render([PageProbe.GuardedProbe(expression)], PageHtml, PageUrl));

    // ── Formerly guarded: the lookup sat behind wrapper.IsObject ──────────────────────────────────

    [Theory]
    [InlineData("'nope'")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("undefined")]
    [InlineData("true")]
    public void ContainsAnswersTheSameForEveryNonObject(string argument) =>
        Assert.Equal(
            Run("document.getElementById('host').contains(null)"),
            Run($"document.getElementById('host').contains({argument})"));

    [Theory]
    [InlineData("'nope'")]
    [InlineData("42")]
    [InlineData("null")]
    public void CompareDocumentPositionAnswersTheSameForEveryNonObject(string argument) =>
        Assert.Equal(
            Run("document.getElementById('host').compareDocumentPosition(null)"),
            Run($"document.getElementById('host').compareDocumentPosition({argument})"));

    [Theory]
    [InlineData("'nope'")]
    [InlineData("42")]
    [InlineData("null")]
    public void GetComputedStyleAnswersTheSameForEveryNonObject(string argument) =>
        Assert.Equal(
            Run("String(getComputedStyle(null) && getComputedStyle(null).display)"),
            Run($"String(getComputedStyle({argument}) && getComputedStyle({argument}).display)"));

    // ── Formerly unguarded: the same lookup, called bare ──────────────────────────────────────────

    [Theory]
    [InlineData("'nope'")]
    [InlineData("42")]
    [InlineData("null")]
    public void InsertAdjacentElementAnswersTheSameForEveryNonObject(string argument) =>
        Assert.Equal(
            Run("document.getElementById('host').insertAdjacentElement('beforebegin', null)"),
            Run($"document.getElementById('host').insertAdjacentElement('beforebegin', {argument})"));

    // ── The two spellings agree with each other, which is the whole point ─────────────────────────

    [Fact]
    public void AGuardedAndAnUnguardedEntryPointBothSurviveAJunkArgument()
    {
        // Neither may fail with an internal error; each answers its own DOM result for "no node".
        var guarded = Run("document.getElementById('host').contains('nope')");
        var unguarded = Run("document.getElementById('host').insertAdjacentElement('beforebegin', 'nope')");

        Assert.DoesNotContain("threw NullReferenceException", guarded);
        Assert.DoesNotContain("threw InvalidCastException", guarded);
        Assert.DoesNotContain("threw NullReferenceException", unguarded);
        Assert.DoesNotContain("threw InvalidCastException", unguarded);
    }

    [Fact]
    public void TheDocumentStillWorksAfterAJunkArgumentReachesTheLookup()
    {
        // The junk call must not leave the tree or the wrapper registry damaged.
        var html = PageProbe.Render(
            [
                "document.getElementById('host').contains('nope');",
                "try { document.getElementById('host').insertAdjacentElement('beforebegin', 'nope'); } catch (e) {}",
                PageProbe.Probe("document.getElementById('child').parentNode.id"),
            ],
            PageHtml,
            PageUrl);

        Assert.Equal("host", PageProbe.OutOf(html));
    }
}
