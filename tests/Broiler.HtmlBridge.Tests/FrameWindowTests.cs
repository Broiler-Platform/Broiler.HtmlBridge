using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The windows a frame's script reaches: the top window as <c>top</c> and <c>parent</c>, which is the
/// page's and not the frame's own, and the frame's window, which stays the same object when the frame
/// navigates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every document shares one global object</b>, and in a frame's script it answers for the frame.
/// <c>top</c> and <c>parent</c> were that global, so a frame reached itself through them:
/// <c>top.location</c> was the frame's Location, and a frame-busting <c>top.location = self.location</c>
/// reloaded the frame where a browser moves the tab.
/// </para>
/// <para>
/// <b>A navigated frame had a new window</b>, so a <c>contentWindow</c> the page kept stayed on the old
/// document and was not the frame's <c>contentWindow</c> any more.
/// </para>
/// <para>
/// The page is on <c>localhost</c>; <see cref="LoopbackCookieServer.Url"/> is another origin to it. A
/// frame writes what it sees into the page's <c>#out</c> through <c>pageOut</c>, a global the page's
/// script sets.
/// </para>
/// </remarks>
public class FrameWindowTests
{
    private static BrowserNetworkSession NewProfile() =>
        new(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });

    private static ScriptEngine EngineFor(BrowserNetworkSession profile) =>
        new(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));

    private static string Page(string body) =>
        $"<!DOCTYPE html><html><head></head><body><div id=\"out\">waiting</div>{body}</body></html>";

    private static string Html(string script, string body = "") =>
        $"<html><body>{body}<script>{script}</script></body></html>";

    private static string FrameAt(string path) => $"<iframe id=\"f\" src=\"{path}\"></iframe>";

    private const string Prelude = "var pageOut = document.getElementById('out'); var pageMarker = 'page';";

    /// <summary>
    /// A frame of the page's origin reaches the page through <c>parent</c> and <c>top</c>: its
    /// document, its location, its name and its globals, and the two are one object that is not the
    /// frame's own window.
    /// </summary>
    [Fact]
    public void AFrameReachesThePageThroughParentAndTop()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/frame", new Reply(Body: Html(
            "pageOut.textContent = [" +
            "  top.location.pathname, parent.location.pathname, location.pathname," +
            "  parent.document.getElementById('out') === pageOut, document.getElementById('out') === null," +
            "  parent === top, top !== self, top.top === top, top.window === top, top.self === parent," +
            "  parent.pageMarker, top.name, name," +
            "  parent.document.defaultView === parent, typeof top.setTimeout, 'pageMarker' in top" +
            "].join('|');")));

        using var profile = NewProfile();
        using var session = EngineFor(profile).ExecuteInteractive(
            [Prelude + "name = 'top-name'; document.getElementById('f').contentWindow;"], [],
            Page("<iframe id=\"f\" name=\"inner\" src=\"/frame\"></iframe>"), server.LocalhostUrl("/page"));

        Assert.Equal(
            "/page|/page|/frame|true|true|true|true|true|true|true|page|top-name|inner|true|function|true",
            PageProbe.OutOf(session!.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// A frame of the page's origin that busts out of its frame -- <c>top.location = self.location</c>
    /// -- asks the host to navigate the page to it. It reloaded the frame.
    /// </summary>
    [Fact]
    public void FrameBustingNavigatesThePage()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/busting", new Reply(Body: Html("if (top !== self) top.location = self.location.href;")));

        using var profile = NewProfile();
        using var session = EngineFor(profile).ExecuteInteractive(
            [Prelude + "document.getElementById('f').contentWindow;"], [], Page(FrameAt("/busting")), server.LocalhostUrl("/page"));
        session!.SettleLoadWindow();

        Assert.Equal(server.LocalhostUrl("/busting"), session.TakePendingNavigation()?.Url);
        Assert.Single(server.RequestsFor("/busting"));
    }

    /// <summary>
    /// A frame of another origin gets the top window's cross-origin view: reading its location throws
    /// a <c>SecurityError</c>, and navigating it is refused while the user has not activated the frame,
    /// as Chromium refuses it.
    /// </summary>
    [Fact]
    public void AFrameOfAnotherOriginCannotReadOrSilentlyNavigateThePage()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/other", new Reply(Body: Html(
            "var read; try { read = top.location.href; } catch (e) { read = e.name; }" +
            "var doc; try { doc = typeof parent.document; } catch (e) { doc = e.name; }" +
            "top.location = '/away';" +
            "pageOut.textContent = [read, doc, top === parent, top.top === top, typeof top.postMessage].join('|');")));

        using var profile = NewProfile();
        using var session = EngineFor(profile).ExecuteInteractive(
            [Prelude + "document.getElementById('f').contentWindow;"], [], Page(FrameAt(server.Url("/other"))), server.LocalhostUrl("/page"));

        Assert.Equal("SecurityError|SecurityError|true|true|function", PageProbe.OutOf(session!.SettleLoadWindow(), decode: true));
        Assert.Null(session.TakePendingNavigation());
    }

    /// <summary>
    /// A frame of another origin that the user has just pressed may navigate the page, as one may in
    /// Chromium within its activation's five seconds.
    /// </summary>
    [Fact]
    public void AFrameOfAnotherOriginTheUserActivatedMayNavigateThePage()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/other", new Reply(Body: Html(
            "document.addEventListener('click', function () { top.location.replace('" + server.LocalhostUrl("/away") + "'); });",
            "<div id='press'>press</div>")));

        using var profile = NewProfile();
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            Network = profile,
            Cookies = profile,
            LayoutViewFactory = () => new DeclaredFrameLayoutView(new Dictionary<string, System.Drawing.RectangleF>
            {
                ["f"] = new(0, 40, 300, 150),
                ["press"] = new(0, 40, 300, 100),
            }),
        }));
        using var session = engine.ExecuteInteractive(
            [Prelude + "document.getElementById('f').contentWindow;"], [], Page(FrameAt(server.Url("/other"))), server.LocalhostUrl("/page"));
        session!.SettleLoadWindow();

        session.DispatchPointer(new PointerInput(PointerInputKind.Down, 20, 60) { Buttons = 1 });
        session.DispatchPointer(new PointerInput(PointerInputKind.Up, 20, 60));

        Assert.Equal(server.LocalhostUrl("/away"), session.TakePendingNavigation()?.Url);
    }

    /// <summary>
    /// The page's message to its frame names, as its <c>source</c>, the window the frame has as
    /// <c>parent</c> and <c>top</c>; the frame answers through it, and the page hears the answer.
    /// </summary>
    [Fact]
    public void APagesMessageToItsFrameComesFromTheFramesParent()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/frame", new Reply(Body: Html(
            "window.addEventListener('message', function (e) {" +
            "  e.source.postMessage([e.source === parent, e.source === top, e.data].join('|'), '*'); });")));

        using var profile = NewProfile();
        using var session = EngineFor(profile).ExecuteInteractive(
            [Prelude +
             "window.addEventListener('message', function (e) { pageOut.textContent = e.data + '|' + (e.source === frames[0]); });" +
             "var f = document.getElementById('f'); f.contentWindow;" +
             "setTimeout(function () { f.contentWindow.postMessage('hi', '*'); }, 10);"],
            [], Page(FrameAt("/frame")), server.LocalhostUrl("/page"));

        Assert.Equal("true|true|hi|true", PageProbe.OutOf(session!.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// A frame that navigates keeps its window: the <c>contentWindow</c> the page kept is still the
    /// frame's, it shows the new document, and nothing the old document or the page set on it, and no
    /// listener of the old document, is left on it.
    /// </summary>
    [Fact]
    public void AFramesWindowIsTheSameObjectAfterItNavigates()
    {
        using var server = new LoopbackCookieServer();
        server
            .Map("/first", new Reply(Body: Html(
                "window.firstMarker = 1; window.addEventListener('message', function () { pageOut.textContent += '|old listener'; });")))
            .Map("/second", new Reply(Body: Html(
                "window.addEventListener('message', function (e) { pageOut.textContent += '|new listener ' + e.data; });",
                "<p id='mark'>SECOND</p>")));

        using var profile = NewProfile();
        using var session = EngineFor(profile).ExecuteInteractive(
            [Prelude +
             "var f = document.getElementById('f'); var kept = f.contentWindow; kept.pageSet = 1;" +
             "f.onload = function () {" +
             "  if (kept.location.pathname !== '/second') { kept.location = '/second'; return; }" +
             "  pageOut.textContent = [f.contentWindow === kept, frames[0] === kept, kept.document.getElementById('mark') !== null," +
             "    typeof kept.firstMarker, typeof kept.pageSet, kept.parent === window, kept.top === window].join('|');" +
             "  kept.postMessage('m', '*');" +
             "};"],
            [], Page(FrameAt("/first")), server.LocalhostUrl("/page"));

        Assert.Equal(
            "true|true|true|undefined|undefined|true|true|new listener m",
            PageProbe.OutOf(session!.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// A frame whose <c>src</c> the page sets keeps its window as well: a navigation of the same frame,
    /// not a new one.
    /// </summary>
    [Fact]
    public void AFramesWindowIsTheSameObjectAfterItsSrcChanges()
    {
        using var server = new LoopbackCookieServer();
        server
            .Map("/first", new Reply(Body: Html("var marker = 1;")))
            .Map("/second", new Reply(Body: Html(string.Empty, "<p id='mark'>SECOND</p>")));

        using var profile = NewProfile();
        using var session = EngineFor(profile).ExecuteInteractive(
            [Prelude +
             "var f = document.getElementById('f'); var kept = f.contentWindow;" +
             "f.onload = function () {" +
             "  if (kept.location.pathname !== '/second') { f.src = '/second'; return; }" +
             "  pageOut.textContent = [f.contentWindow === kept, kept.document.getElementById('mark').textContent].join('|');" +
             "};"],
            [], Page(FrameAt("/first")), server.LocalhostUrl("/page"));

        Assert.Equal("true|SECOND", PageProbe.OutOf(session!.SettleLoadWindow(), decode: true));
    }
}
