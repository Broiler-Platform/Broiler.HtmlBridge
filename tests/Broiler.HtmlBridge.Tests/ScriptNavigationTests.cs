using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// The host side of <c>window.location</c>: what a script's navigation request looks like by the
/// time a host reads it off the session.
/// <para>
/// <c>LocationBindingTests</c> covers what the page sees. This covers what the browser sees, which
/// is the half that decides whether a search renders as its results or as the box the query was
/// typed into. See <c>docs/script-initiated-navigation.md</c>.
/// </para>
/// </summary>
public class ScriptNavigationTests
{
    private const string PageUrl = "https://example.test/page";
    private const string OtherUrl = "https://other.test/elsewhere";
    private const string PageHtml = "<html><body><div id=\"out\"></div></body></html>";

    /// <summary>
    /// Runs <paramref name="script"/> and returns the navigation a host would find waiting on the
    /// session, which is where a host reads it from.
    /// </summary>
    private static NavigationRequest? PendingAfter(string script, string url = PageUrl)
    {
        using var session = new ScriptEngine().ExecuteInteractive([script], [], PageHtml, url);
        Assert.NotNull(session);
        return session!.PendingNavigation;
    }

    [Fact]
    public void ReplaceRecordsTheTargetAndTheKind()
    {
        var pending = PendingAfter($"location.replace('{OtherUrl}');");

        Assert.NotNull(pending);
        Assert.Equal(OtherUrl, pending!.Url);
        // Replace, not Assign: a host keeping session history has to know this document does not
        // stay in it.
        Assert.Equal(NavigationKind.Replace, pending.Kind);
    }

    [Theory]
    [InlineData("location.assign('https://other.test/elsewhere');")]
    [InlineData("location.href = 'https://other.test/elsewhere';")]
    public void AssignAndHrefAreTheSameNavigation(string script)
    {
        var pending = PendingAfter(script);

        Assert.NotNull(pending);
        Assert.Equal(OtherUrl, pending!.Url);
        Assert.Equal(NavigationKind.Assign, pending.Kind);
    }

    [Fact]
    public void ReloadAsksForTheDocumentsOwnUrl()
    {
        var pending = PendingAfter("location.reload();");

        Assert.NotNull(pending);
        Assert.Equal(PageUrl, pending!.Url);
        Assert.Equal(NavigationKind.Reload, pending.Kind);
    }

    [Fact]
    public void ARelativeTargetIsResolvedBeforeTheHostSeesIt()
    {
        // The host has no idea what the page wrote, so the binding resolves it. A host handed
        // "/search" would have to reconstruct the base URL to act on it.
        var pending = PendingAfter("location.replace('/search?q=test');");

        Assert.NotNull(pending);
        Assert.Equal("https://example.test/search?q=test", pending!.Url);
    }

    [Fact]
    public void TheLastRequestIsTheOneThatStands()
    {
        // A browser starts navigating on the first assignment and supersedes it on the second,
        // landing where the script last asked to go.
        var pending = PendingAfter("location.replace('https://first.test/a'); location.replace('https://second.test/b');");

        Assert.NotNull(pending);
        Assert.Equal("https://second.test/b", pending!.Url);
    }

    [Fact]
    public void AFragmentNavigationIsNotHandedToTheHost()
    {
        // It is same-document: the binding performed it outright, and there is nothing to load.
        Assert.Null(PendingAfter("location.hash = '#section';"));
        Assert.Null(PendingAfter("location.replace('#section');"));
    }

    [Fact]
    public void APageThatAsksForNothingLeavesNothingPending()
    {
        Assert.Null(PendingAfter("document.getElementById('out').textContent = 'quiet';"));
    }

    [Fact]
    public void ANavigationRequestedFromATimerIsStillWaitingAfterTheLoadWindow()
    {
        // This is why a host reads PendingNavigation after settling rather than straight after the
        // synchronous scripts: the script that decides to leave usually runs on a timer, and asking
        // too early misses exactly the pages that navigate.
        using var session = new ScriptEngine().ExecuteInteractive(
            [$"setTimeout(function () {{ location.replace('{OtherUrl}'); }}, 0);"],
            [],
            PageHtml,
            PageUrl);

        Assert.NotNull(session);
        Assert.Null(session!.PendingNavigation);

        session.SettleLoadWindow(CancellationToken.None);

        Assert.Equal(OtherUrl, session.PendingNavigation?.Url);
    }
}
