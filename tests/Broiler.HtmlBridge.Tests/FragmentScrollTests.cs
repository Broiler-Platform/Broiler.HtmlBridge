using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A script's fragment navigation scrolls the page as Chromium's does, and the page's viewport and the
/// host's view of it follow each other.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing scrolled</b>: <c>location.hash = '#far'</c> moved the URL and left the page where it was, and
/// the page never knew where the user had scrolled the host's view, nor the host where the page had
/// scrolled itself.
/// </para>
/// <para>
/// Measured in Chromium: the scroll happens at once, the element named at the top; the same fragment again,
/// a fragment naming nothing and <c>pushState</c> do not scroll; an empty fragment, or <c>top</c> naming
/// nothing, scrolls to the top. The layout is declared (<see cref="DeclaredBoxLayoutView"/>).
/// </para>
/// </remarks>
public class FragmentScrollTests
{
    private const string PageUrl = "https://example.test/scroll";

    private static InteractiveSession Start(string script)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredBoxLayoutView(new Dictionary<string, System.Drawing.RectangleF>
            {
                ["root"] = new(0, 0, 1024, 5000),
                ["far"] = new(0, 3000, 100, 20),
                ["link"] = new(0, 0, 50, 20),
            }),
        }));

        var session = engine.ExecuteInteractive(
            ["var out = document.getElementById('out'), log = [];" +
             "function show() { out.textContent = log.join('|'); }" +
             "function at(label) { log.push(label + ' ' + Math.round(scrollY)); }" + script + ";show();"],
            [], "<html id=\"root\"><body><a id=\"link\" href=\"#far\">far</a><p id=\"far\">far</p><div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);
        return session!;
    }

    /// <summary>Chromium's rules for when a fragment navigation scrolls, and where to.</summary>
    [Fact]
    public void AFragmentNavigationScrollsAtOnce()
    {
        using var session = Start(
            "location.hash = '#far'; at('far');" +
            "scrollTo(0, 0); location.hash = '#far'; at('same again');" +
            "scrollTo(0, 500); location.hash = '#nope'; at('nothing named');" +
            "scrollTo(0, 500); location.hash = '#top'; at('top');" +
            "scrollTo(0, 500); location.hash = '#far'; scrollTo(0, 500); location.hash = ''; at('empty');" +
            "scrollTo(0, 500); history.pushState(null, '', '#far'); at('pushState');" +
            "scrollTo(0, 0); location.hash = '#x'; document.getElementById('link').click(); at('link')");

        Assert.Equal(
            "far 3000|same again 0|nothing named 500|top 0|empty 0|pushState 500|link 3000",
            PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// The host reads where the page scrolled itself, and the page follows the host's scroll and hears its
    /// <c>scroll</c> in a later task; a link into the page the host followed is the host's to scroll.
    /// </summary>
    [Fact]
    public void ThePageAndTheHostFollowEachOthersScroll()
    {
        using var session = Start(
            "addEventListener('scroll', function () { log.push('scroll ' + Math.round(scrollY)); show(); });" +
            "scrollTo(0, 700);");
        session.SettleLoadWindow();
        Assert.Equal(700, session.ViewportScroll.Y);

        // The page's scroll event is a task: it has not fired when the call returns.
        session.ScrollViewportTo(0, 1200);
        Assert.Equal(1200, session.ViewportScroll.Y);
        Assert.DoesNotContain("scroll 1200", PageProbe.OutOf(session.CurrentHtml(), decode: true));
        Assert.EndsWith("scroll 1200", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));

        Assert.True(session.NavigateToFragment(PageUrl + "#far"));
        Assert.Equal(1200, session.ViewportScroll.Y);
    }
}
