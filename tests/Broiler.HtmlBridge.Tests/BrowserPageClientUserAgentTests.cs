using Broiler.Layout.Net;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// The browser window's own navigations are identified, not just the CLI's captures.
/// </summary>
/// <remarks>
/// Both entry points had the same defect and only one of them is exercised by a capture test: a
/// <c>User-Agent</c> added to <c>CaptureService</c> alone would leave <c>Broiler.App</c> answering
/// <c>403 Forbidden</c> on <c>https://www.mediawiki.org/wiki/MediaWiki</c> exactly as reported. The
/// browser builds one process-wide client for every navigation, so asserting on that client is
/// asserting on every page the window will ever load.
/// </remarks>
public class BrowserPageClientUserAgentTests
{
    [Fact(Timeout = 600000)]
    public void The_Page_Client_Sends_A_User_Agent()
    {
        using var client = BrowserApp.CreatePageHttpClient();

        // Joined rather than compared element-wise: User-Agent is a typed header, so the collection
        // holds the three product tokens .NET re-joins with spaces when it writes the header.
        Assert.True(
            client.DefaultRequestHeaders.TryGetValues("User-Agent", out var values),
            "the browser's page client sends no User-Agent, which mediawiki.org answers 403");
        Assert.Equal(BroilerUserAgent.Value, string.Join(" ", values!));
    }
}
