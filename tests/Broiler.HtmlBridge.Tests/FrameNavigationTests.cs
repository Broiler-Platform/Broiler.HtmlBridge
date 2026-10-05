using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A frame navigated through its <c>location</c> -- by its own script, by the page through the
/// frame's window, across origins -- loads the new document in place of the old one, and the page's
/// own <c>location = url</c> asks the host to navigate.
/// </summary>
/// <remarks>
/// <para>
/// <b>A frame's navigation was logged and dropped</b>, and assigning <c>location</c> replaced it with a
/// string: the frame kept its document whatever its script or its page asked for.
/// </para>
/// <para>
/// The page is on <c>localhost</c>, served by a loopback server with the frames' documents; a frame
/// writes what it sees into the page's <c>#out</c> through <c>pageOut</c>, a global the page's script
/// sets, since every document shares one realm. A document that reports to the server instead does so
/// with a request to <c>/report</c>.
/// </para>
/// </remarks>
public class FrameNavigationTests
{
    private static BrowserNetworkSession NewProfile() =>
        new(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });

    private static ScriptEngine EngineFor(BrowserNetworkSession profile) =>
        new(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));

    private static string Page(string body) =>
        $"<!DOCTYPE html><html><head></head><body><div id=\"out\">waiting</div>{body}</body></html>";

    private static string Html(string script, string body = "") =>
        $"<html><body>{body}<script>{script}</script></body></html>";

    /// <summary>Runs <paramref name="script"/> on a page at <c>localhost</c> holding <paramref name="body"/>, settles it, and answers its <c>#out</c>.</summary>
    private static string Settle(LoopbackCookieServer server, string body, string script)
    {
        using var profile = NewProfile();
        using var session = EngineFor(profile).ExecuteInteractive(
            ["var pageOut = document.getElementById('out'); var loads = 0;" + script], [], Page(body), server.LocalhostUrl("/page"));
        return PageProbe.OutOf(session!.SettleLoadWindow(), decode: true);
    }

    private static string FrameAt(string path, string attributes = "") =>
        $"<iframe id=\"f\" src=\"{path}\" onload=\"loads++\"{attributes}></iframe>";

    /// <summary>
    /// A frame that sends itself on with <c>location.href</c> loads the new document: its script runs
    /// there, against its own document, its location is the new URL, the frame element's <c>load</c>
    /// fires for each document, and its <c>src</c> is still what the page wrote. It kept the first
    /// document.
    /// </summary>
    [Fact]
    public void AFrameNavigatesItselfThroughLocationHref()
    {
        using var server = new LoopbackCookieServer();
        server
            .Map("/first", new Reply(Body: Html("location.href = '/second';")))
            .Map("/second", new Reply(Body: Html(
                "setTimeout(function () { pageOut.textContent = ['second', location.pathname, document.getElementById('mark').textContent," +
                " loadsSeen.join(','), pageOut.ownerDocument.getElementById('f').getAttribute('src')].join('|'); }, 50);",
                "<p id='mark'>SECOND</p>")));

        Assert.Equal(
            "second|/second|SECOND|/first,/second|/first",
            Settle(server,
                "<iframe id=\"f\" src=\"/first\" onload=\"loadsSeen.push(this.contentWindow.location.pathname)\"></iframe>",
                "var loadsSeen = []; document.getElementById('f').contentWindow;"));
    }

    /// <summary>
    /// The page sends its frame on through the frame's window, whichever way it spells it:
    /// assigning <c>location</c>, which forwards to <c>href</c>, assigning <c>href</c>, or calling
    /// <c>assign</c> or <c>replace</c>.
    /// </summary>
    [Theory]
    [InlineData("w.location = '/second';")]
    [InlineData("w.location.href = '/second';")]
    [InlineData("w.location.assign('/second');")]
    [InlineData("frames[0].location.replace('/second');")]
    public void APageNavigatesItsFrameThroughTheFramesWindow(string navigate)
    {
        using var server = new LoopbackCookieServer();
        server
            .Map("/first", new Reply(Body: Html("pageOut.textContent = 'first';")))
            .Map("/second", new Reply(Body: Html("pageOut.textContent = 'second ' + location.pathname;")));

        Assert.Equal(
            "second /second",
            Settle(server, FrameAt("/first"), "var w = document.getElementById('f').contentWindow;" + navigate));
    }

    /// <summary>
    /// A fragment navigation in a frame loads nothing: the frame's own window hears
    /// <c>hashchange</c>, and its document is the one it had.
    /// </summary>
    [Fact]
    public void AFramesFragmentNavigationFiresHashchangeAtTheFrame()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/first", new Reply(Body: Html(
            "window.addEventListener('hashchange', function (e) { pageOut.textContent = ['hashchange', location.hash, e.newURL.split('#')[1]].join('|'); });" +
            "location.hash = 'part';")));

        Assert.Equal(
            "hashchange|#part|part",
            Settle(server, FrameAt("/first"), "document.getElementById('f').contentWindow;"));
        Assert.Single(server.RequestsFor("/first"));
    }

    /// <summary>
    /// A page navigates a frame of another origin through the window it is handed for it, which can
    /// be navigated though nothing of it can be read; the new document is requested and runs.
    /// </summary>
    [Fact]
    public void APageNavigatesAFrameOfAnotherOrigin()
    {
        using var server = new LoopbackCookieServer();
        var report = server.Url("/report");
        server
            .Map("/first", new Reply(Body: Html("var marker = 1;")))
            .Map("/second", new Reply(Body: Html($"fetch('{report}?at=' + location.pathname, {{ mode: 'no-cors' }});")))
            .Map("/report", new Reply(ContentType: "text/plain", Body: "x"));

        Settle(server, FrameAt(server.Url("/first")),
            $"document.getElementById('f').contentWindow.location = '{server.Url("/second")}';" +
            "pageOut.textContent = 'asked';");

        Assert.Single(server.RequestsFor("/second"));
        Assert.Contains(server.RequestsFor("/report"),
            request => Uri.UnescapeDataString(request.Path).EndsWith("?at=/second", StringComparison.Ordinal));
    }

    /// <summary>
    /// The page's own <c>location = url</c> and <c>window.location = url</c> ask the host to
    /// navigate, as <c>location.href = url</c> does, and leave <c>location</c> the Location. They
    /// replaced it with a string and asked for nothing.
    /// </summary>
    [Theory]
    [InlineData("location = '/elsewhere';")]
    [InlineData("window.location = '/elsewhere';")]
    public void ThePagesOwnLocationAssignmentAsksTheHostToNavigate(string assign)
    {
        using var server = new LoopbackCookieServer();
        using var profile = NewProfile();
        using var session = EngineFor(profile).ExecuteInteractive(
            ["var pageOut = document.getElementById('out');" + assign + "pageOut.textContent = typeof location + '|' + location.pathname;"],
            [], Page(string.Empty), server.LocalhostUrl("/page"));
        var html = session!.SettleLoadWindow();

        Assert.Equal("object|/page", PageProbe.OutOf(html, decode: true));
        Assert.Equal(server.LocalhostUrl("/elsewhere"), session.TakePendingNavigation()?.Url);
    }

    /// <summary>
    /// A frame whose script keeps asking for the URL it shows -- here a page's frame-busting
    /// <c>top.location = self.location</c>, which, every document sharing one global object, is the
    /// frame's own Location -- is reloaded three times and then left alone, rather than forever.
    /// </summary>
    [Fact]
    public void AFrameThatKeepsReloadingItselfIsStopped()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/busting", new Reply(Body: Html(
            "parent.ran = (parent.ran || 0) + 1; pageOut.textContent = 'ran ' + parent.ran;" +
            "if (top !== self) top.location = self.location.href;")));

        Assert.Equal(
            "ran 4",
            Settle(server, FrameAt("/busting"), "document.getElementById('f').contentWindow;"));
        Assert.Equal(4, server.RequestsFor("/busting").Length);
    }

    /// <summary>
    /// A navigation happens after the script that asked for it, which goes on running, and of two
    /// asked for in one script only the second happens.
    /// </summary>
    [Fact]
    public void ANavigationIsALaterTaskAndTheLatestWins()
    {
        using var server = new LoopbackCookieServer();
        server
            .Map("/first", new Reply(Body: Html(
                "location.href = '/a'; location.href = '/b'; pageOut.textContent = 'still first ' + location.pathname;")))
            .Map("/a", new Reply(Body: Html("pageOut.textContent += ' then a';")))
            .Map("/b", new Reply(Body: Html("pageOut.textContent += ' then b';")));

        Assert.Equal(
            "still first /first then b",
            Settle(server, FrameAt("/first"), "document.getElementById('f').contentWindow;"));
        Assert.Empty(server.RequestsFor("/a"));
    }

    /// <summary>
    /// The host renders the document the frame navigated to: it is stamped on the frame element,
    /// since the frame's <c>src</c> still names the one it left.
    /// </summary>
    [Fact]
    public void TheHostRendersTheDocumentAFrameNavigatedTo()
    {
        using var server = new LoopbackCookieServer();
        server
            .Map("/first", new Reply(Body: Html("location.replace('/second');", "<p>FIRST</p>")))
            .Map("/second", new Reply(Body: Html(string.Empty, "<p>SECOND</p>")));

        using var profile = NewProfile();
        using var session = EngineFor(profile).ExecuteInteractive(
            ["document.getElementById('f').contentWindow;"], [], Page(FrameAt("/first")), server.LocalhostUrl("/page"));
        var html = session!.SettleLoadWindow();

        Assert.Contains("data-broiler-frame-document", html);
        Assert.Contains("SECOND", html);
        Assert.DoesNotContain("FIRST", html);
    }

    /// <summary>A frame its location navigated shows its <c>src</c> again once the page sets it.</summary>
    [Fact]
    public void SettingSrcAfterANavigationLoadsTheSrc()
    {
        using var server = new LoopbackCookieServer();
        server
            .Map("/first", new Reply(Body: Html("var marker = 1;")))
            .Map("/second", new Reply(Body: Html("setTimeout(function () { pageOut.ownerDocument.getElementById('f').src = '/third'; }, 10);")))
            .Map("/third", new Reply(Body: Html("pageOut.textContent = 'third ' + location.pathname;")));

        Assert.Equal(
            "third /third",
            Settle(server, FrameAt("/first"), "document.getElementById('f').contentWindow.location = '/second';"));
    }

    /// <summary>
    /// <c>location.href = "javascript:void(0)"</c> -- a page doing nothing -- leaves the frame's
    /// document in place rather than tearing it down for a document that never comes.
    /// </summary>
    [Fact]
    public void AJavascriptUrlDoesNotTearAFrameDown()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/first", new Reply(Body: Html(
            "location.href = 'javascript:void(0)'; setTimeout(function () { pageOut.textContent = 'kept ' + document.getElementById('mark').id; }, 10);",
            "<p id='mark'>m</p>")));

        Assert.Equal(
            "kept mark",
            Settle(server, FrameAt("/first"), "document.getElementById('f').contentWindow;"));
    }
}
