using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// <c>window.location</c>'s navigation surface: which requests move the document's URL and which
/// are only recorded.
/// <para>
/// The contract pinned here is the one Google Search's bot-check bootstrap depends on — the methods
/// exist, they return rather than throw, and a cross-document target leaves every URL component
/// answering for the document actually in hand. Nothing covered it before, and the regression that
/// matters is silent: back to a <c>TypeError</c> at <c>location.replace(url)</c>, which aborts its
/// caller and derails the rest of the page.
/// </para>
/// <para>
/// The fragment cases are the other half. A fragment navigation is not a load, so it is the one
/// navigation this engine can actually perform, and all four spellings have to agree about it.
/// </para>
/// </summary>
public class LocationBindingTests
{
    private const string PageUrl = "https://example.test/page";
    private const string PageHtml = "<html><body><div id=\"out\"></div></body></html>";

    /// <summary>
    /// Runs <paramref name="script"/> against a document at <paramref name="url"/> and returns what
    /// it wrote to <c>#out</c>. Reading the result out of the serialized DOM keeps the test to the
    /// engine's public surface — the binding under test is internal, and reaching for it directly
    /// would pin its shape rather than its behaviour.
    /// </summary>
    private static string Run(string script, string url = PageUrl)
    {
        var html = new ScriptEngine().Execute(
            [$"document.getElementById('out').textContent = String({script});"],
            PageHtml,
            url);

        Assert.NotNull(html);

        const string open = "<div id=\"out\">";
        var start = html!.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no #out div in serialized output: {html}");
        start += open.Length;
        var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"unterminated #out div in serialized output: {html}");
        return html[start..end];
    }

    [Fact]
    public void ReplaceToAnotherDocumentLeavesTheUrlAlone()
    {
        // The capture renders the document it was given. `href` answering the target would describe
        // a document nobody loaded, which is the harder thing to debug of the two.
        Assert.Equal(PageUrl, Run("(location.replace('https://other.test/elsewhere'), location.href)"));
    }

    [Fact]
    public void ReplaceReturnsRatherThanThrows()
    {
        // The whole reason the method exists: `undefined is not a function` was a TypeError at the
        // call, and a throw here would abort the caller just as thoroughly as the missing method did.
        Assert.Equal("ok", Run("(function () { try { location.replace('https://other.test/x'); return 'ok'; } catch (e) { return 'threw: ' + e; } })()"));
    }

    [Fact]
    public void AssignToADifferentQueryIsNotAFragmentNavigation()
    {
        // Same host and path, different query — a real navigation, and one this engine does not make.
        Assert.Equal(PageUrl, Run("(location.assign('?q=1'), location.href)"));
    }

    [Theory]
    [InlineData("location.hash = '#section'")]
    [InlineData("location.href = '#section'")]
    [InlineData("location.assign('#section')")]
    [InlineData("location.replace('#section')")]
    [InlineData("location.replace('https://example.test/page#section')")]
    public void EverySpellingOfAFragmentNavigationMovesTheFragment(string navigation)
    {
        Assert.Equal("#section", Run($"({navigation}, location.hash)"));
    }

    [Fact]
    public void AFragmentNavigationMovesHrefWithHash()
    {
        // The two disagreeing is the bug that was here: `hash` was a writable data property, so the
        // write stuck while `href` went on ending in the old fragment.
        Assert.Equal(PageUrl + "#section", Run("(location.hash = '#section', location.href)"));
    }

    [Fact]
    public void SettingHashWithoutTheLeadingMarkerStillSetsAFragment()
    {
        // HTML §7.10.5 prepends the "#" when the page leaves it off — it is part of the spelling,
        // not of the value.
        Assert.Equal("#section", Run("(location.hash = 'section', location.hash)"));
    }

    [Fact]
    public void AFragmentNavigationLeavesTheOtherComponentsAlone()
    {
        Assert.Equal(
            "https: example.test /page",
            Run("(location.hash = '#section', location.protocol + ' ' + location.host + ' ' + location.pathname)"));
    }

    [Fact]
    public void AFragmentNavigationFiresHashChangeWithBothUrls()
    {
        var script = """
            (function () {
              var seen = [];
              window.addEventListener('hashchange', function (e) { seen.push(e.oldURL + ' to ' + e.newURL); });
              location.hash = '#second';
              return seen.join('|');
            })()
            """;

        Assert.Equal(
            "https://example.test/page#first to https://example.test/page#second",
            Run(script, PageUrl + "#first"));
    }

    [Fact]
    public void NavigatingToTheFragmentAlreadyInHandFiresNothing()
    {
        // Still a same-document navigation, still not a load — but HTML §7.4.5 fires hashchange
        // only when the fragment actually changed.
        var script = """
            (function () {
              var fired = 0;
              window.addEventListener('hashchange', function () { fired++; });
              location.hash = '#first';
              return fired;
            })()
            """;

        Assert.Equal("0", Run(script, PageUrl + "#first"));
    }

    [Fact]
    public void ACrossDocumentNavigationFiresNoHashChange()
    {
        var script = """
            (function () {
              var fired = 0;
              window.addEventListener('hashchange', function () { fired++; });
              location.replace('https://other.test/elsewhere');
              return fired;
            })()
            """;

        Assert.Equal("0", Run(script));
    }

    [Fact]
    public void ReplaceWithNoArgumentDoesNotClearTheFragment()
    {
        // An empty target resolves to this document's URL with the fragment dropped, which is a
        // same-document URL nobody asked to navigate to. Reading it as a fragment navigation would
        // silently discard the fragment the document actually has.
        Assert.Equal("#first", Run("(location.replace(), location.hash)", PageUrl + "#first"));
    }

    [Fact]
    public void ReloadLeavesTheUrlAlone()
    {
        Assert.Equal(PageUrl, Run("(location.reload(), location.href)"));
    }

    [Fact]
    public void LocationStringifiesToItsHref()
    {
        // Pages build URLs with `"" + location`; Object.prototype.toString made that useless.
        Assert.Equal(PageUrl + "#section", Run("(location.hash = '#section', '' + location)"));
    }

    [Fact]
    public void DocumentLocationIsTheSameObjectAsWindowLocation()
    {
        // HTML §3.1.5 — one Location registered on both, not a copy, and pages compare the two.
        Assert.Equal("true", Run("(document.location === window.location)"));
    }
}
