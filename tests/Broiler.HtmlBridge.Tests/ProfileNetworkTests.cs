using Broiler.HtmlBridge;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The bridge's loaders on the profile network: a cookie the profile received during navigation
/// reaches every sub-resource through its real loading path, and every <c>Set-Cookie</c> those
/// responses carry lands in the profile's store — and without a profile, no loader keeps a jar.
/// </summary>
/// <remarks>
/// <para>
/// <b>How the profile gets its cookie.</b> Each test navigates the profile's
/// <see cref="BrowserNetworkSession"/> to a <c>/login</c> page first, as a browser's address bar would
/// (a top-level navigation with no initiator), and that response sets <c>sid</c>. Nothing seeds the
/// store directly, so the tests also pin that navigation and loaders share one store.
/// </para>
/// <para>
/// <b>What is observed.</b> The loopback server logs the <c>Cookie</c> header of each request, and
/// the store's administrative snapshot shows what each response's <c>Set-Cookie</c> stored. Each
/// loader's response sets a cookie of its own name, so a stored cookie names the path that stored it.
/// </para>
/// </remarks>
public class ProfileNetworkTests
{
    private const string Script = "text/javascript";
    private const string Css = "text/css";

    private static BrowserNetworkSession NewProfile() =>
        new(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });

    /// <summary>A server whose <c>/login</c> sets the profile cookie <c>sid=abc</c>.</summary>
    private static LoopbackCookieServer NewServer() =>
        new LoopbackCookieServer().Map("/login", new Reply(Body: "<p>in</p>", SetCookies: ["sid=abc; Path=/"]));

    /// <summary>Navigates the profile to <paramref name="url"/> as browser UI does.</summary>
    private static void Navigate(BrowserNetworkSession profile, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = profile.Send(request, RequestContext.TopLevelNavigation(initiator: null));
        Assert.Equal(200, response.StatusCode);
    }

    private static bool Stored(BrowserNetworkSession profile, string name) =>
        profile.Cookies.Snapshot().Any(cookie => cookie.Name == name);

    private static ScriptEngine EngineFor(BrowserNetworkSession profile) =>
        new(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));

    private static string Page(string head, string body = "") =>
        $"<!DOCTYPE html><html><head>{head}</head><body><p id=\"p\">p</p><div id=\"out\"></div>{body}</body></html>";

    // ---------------------------------------------------------------------
    //  Scripts
    // ---------------------------------------------------------------------

    /// <summary>
    /// Classic scripts, through <see cref="ScriptExtractionService.ExtractAll(string, string?, Scripting.ContentSecurityPolicy?, ScriptFetchContext?)"/>.
    /// Two of them, so the speculative prefetcher issues both: its requests must carry the cookie too,
    /// which they do only if the context was captured when they were queued.
    /// </summary>
    [Fact]
    public void ClassicScriptsCarryTheProfileCookieAndStoreTheirSetCookie()
    {
        using var server = NewServer()
            .Map("/a.js", new Reply(ContentType: Script, Body: "var a = 1;", SetCookies: ["fromScript=1; Path=/"]))
            .Map("/b.js", new Reply(ContentType: Script, Body: "var b = 2;"));
        using var profile = NewProfile();
        Navigate(profile, server.Url("/login"));

        var pageUrl = server.Url("/page");
        var result = ScriptExtractionService.ExtractAll(
            Page("<script src=\"/a.js\"></script><script src=\"b.js\"></script>"),
            pageUrl,
            deliveredPolicy: null,
            new ScriptFetchContext(profile, DocumentRequestContext.CreateTopLevel(new Uri(pageUrl))));

        Assert.Equal(new[] { "var a = 1;", "var b = 2;" }, result.Scripts);
        // Both were in flight together, so b.js may or may not have seen a.js's cookie already.
        Assert.Equal("sid=abc", server.Single("/a.js").Cookie);
        Assert.Contains("sid=abc", server.Single("/b.js").Cookie);
        Assert.True(Stored(profile, "fromScript"));
    }

    /// <summary>
    /// A module root and its static import. The root is fetched by the extractor, the import by the
    /// engine's module loader on the bridge's module context — which gets the profile network from the
    /// bridge when the bridge attaches to it. Both are same-origin CORS requests with
    /// <c>same-origin</c> credentials, so both carry the cookie.
    /// </summary>
    [Fact]
    public void AModuleAndItsStaticImportCarryTheProfileCookieAndStoreTheirSetCookie()
    {
        using var server = NewServer()
            .Map("/mod.js", new Reply(ContentType: Script,
                Body: "import { v } from './dep.js'; document.getElementById('out').textContent = v;",
                SetCookies: ["fromModule=1; Path=/"]))
            .Map("/dep.js", new Reply(ContentType: Script, Body: "export const v = 'dep-ran';",
                SetCookies: ["fromImport=1; Path=/"]));
        using var profile = NewProfile();
        Navigate(profile, server.Url("/login"));

        var pageUrl = server.Url("/page");
        var html = Page(string.Empty, "<script type=\"module\" src=\"/mod.js\"></script>");
        var extraction = ScriptExtractionService.ExtractAll(
            html, pageUrl, deliveredPolicy: null,
            new ScriptFetchContext(profile, DocumentRequestContext.CreateTopLevel(new Uri(pageUrl))));
        Assert.Single(extraction.ModuleRoots);

        var rendered = EngineFor(profile).Execute([], [], html, pageUrl, extraction.ModuleRoots);

        Assert.Equal("dep-ran", PageProbe.OutOf(rendered!));
        Assert.Equal("sid=abc", server.Single("/mod.js").Cookie);
        Assert.Contains("sid=abc", server.Single("/dep.js").Cookie);
        Assert.True(Stored(profile, "fromModule"));
        Assert.True(Stored(profile, "fromImport"));
    }

    /// <summary>
    /// A script-inserted <c>&lt;script src&gt;</c>, fetched on the event loop by the insertion runner as
    /// the top document's.
    /// </summary>
    [Fact]
    public void AnInsertedScriptCarriesTheProfileCookieAndStoresItsSetCookie()
    {
        using var server = NewServer()
            .Map("/ins.js", new Reply(ContentType: Script,
                Body: "document.getElementById('out').textContent = 'inserted-ran';",
                SetCookies: ["fromInserted=1; Path=/"]));
        using var profile = NewProfile();
        Navigate(profile, server.Url("/login"));

        var rendered = EngineFor(profile).Execute(
            ["var s = document.createElement('script'); s.src = '/ins.js'; document.head.appendChild(s);"],
            Page(string.Empty),
            server.Url("/page"));

        Assert.Equal("inserted-ran", PageProbe.OutOf(rendered!));
        Assert.Equal("sid=abc", server.Single("/ins.js").Cookie);
        Assert.True(Stored(profile, "fromInserted"));
    }

    /// <summary>
    /// The Content-Security-Policy check a script passed before its fetch is the transport's host
    /// policy for every hop: a same-origin script that redirects to another origin the policy does not
    /// admit is refused before that origin is asked. The control is the same page with no policy.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AScriptRedirectIsCheckedAgainstThePolicyBeforeItIsFollowed(bool withPolicy)
    {
        using var server = NewServer();
        server
            .Map("/redir.js", new Reply(302, Location: server.LocalhostUrl("/elsewhere.js")))
            .Map("/elsewhere.js", new Reply(ContentType: Script, Body: "var elsewhere = 1;"));
        using var profile = NewProfile();

        var pageUrl = server.Url("/page");
        var head = (withPolicy ? CspFixture.Meta("script-src 'self'") : string.Empty) +
                   "<script src=\"/redir.js\"></script>";
        var result = ScriptExtractionService.ExtractAll(
            Page(head), pageUrl, deliveredPolicy: null,
            new ScriptFetchContext(profile, DocumentRequestContext.CreateTopLevel(new Uri(pageUrl))));

        Assert.Single(server.RequestsFor("/redir.js"));
        if (withPolicy)
        {
            Assert.Empty(result.Scripts);
            Assert.Empty(server.RequestsFor("/elsewhere.js"));
        }
        else
        {
            Assert.Equal(new[] { "var elsewhere = 1;" }, result.Scripts);
            Assert.StartsWith("localhost:", server.Single("/elsewhere.js").Host);
        }
    }

    // ---------------------------------------------------------------------
    //  Stylesheets
    // ---------------------------------------------------------------------

    /// <summary>
    /// A linked stylesheet (fetched for the CSSOM and the link's load event) and a <c>&lt;style&gt;</c>'s
    /// <c>@import</c> (fetched for the render projection): both no-cors style requests with credentials.
    /// </summary>
    [Fact]
    public void LinkedAndImportedStylesheetsCarryTheProfileCookieAndStoreTheirSetCookie()
    {
        using var server = NewServer()
            .Map("/a.css", new Reply(ContentType: Css, Body: "#p { color: rgb(1, 2, 3) }",
                SetCookies: ["fromLink=1; Path=/"]))
            .Map("/b.css", new Reply(ContentType: Css, Body: "#q { color: rgb(4, 5, 6) }",
                SetCookies: ["fromImport=1; Path=/"]));
        using var profile = NewProfile();
        Navigate(profile, server.Url("/login"));

        var rendered = EngineFor(profile).Execute(
            [PageProbe.Probe("getComputedStyle(document.getElementById('p')).color")],
            Page("<link rel=\"stylesheet\" href=\"/a.css\"><style id=\"s\">@import url(/b.css);</style>"),
            server.Url("/page"));

        Assert.Equal("rgb(1, 2, 3)", PageProbe.OutOf(rendered!));
        Assert.Contains("#q { color: rgb(4, 5, 6) }", CspFixture.StyleText(rendered!));
        Assert.All(server.RequestsFor("/a.css"), request => Assert.Contains("sid=abc", request.Cookie));
        Assert.NotEmpty(server.RequestsFor("/a.css"));
        Assert.All(server.RequestsFor("/b.css"), request => Assert.Contains("sid=abc", request.Cookie));
        Assert.NotEmpty(server.RequestsFor("/b.css"));
        Assert.True(Stored(profile, "fromLink"));
        Assert.True(Stored(profile, "fromImport"));
    }

    // ---------------------------------------------------------------------
    //  Frames
    // ---------------------------------------------------------------------

    /// <summary>
    /// A same-site iframe: its document is a nested navigation of the page, and its own external
    /// script is fetched as the frame document's. Both carry the cookie, and both responses store.
    /// </summary>
    [Fact]
    public void AnIframeAndItsScriptCarryTheProfileCookieAndStoreTheirSetCookie()
    {
        using var server = NewServer()
            .Map("/frame", new Reply(
                Body: "<html><body><p id='in'>frame</p><script src='/frame.js'></script></body></html>",
                SetCookies: ["fromFrame=1; Path=/"]))
            .Map("/frame.js", new Reply(ContentType: Script,
                Body: "document.getElementById('in').textContent = 'frame-script-ran';",
                SetCookies: ["fromFrameScript=1; Path=/"]));
        using var profile = NewProfile();
        Navigate(profile, server.Url("/login"));

        var rendered = EngineFor(profile).Execute(
            [PageProbe.Probe("document.getElementById('f').contentDocument.getElementById('in').textContent")],
            Page(string.Empty, "<iframe id=\"f\" src=\"/frame\"></iframe>"),
            server.Url("/page"));

        Assert.Equal("frame-script-ran", PageProbe.OutOf(rendered!));
        Assert.Equal("sid=abc", server.RequestsFor("/frame")[0].Cookie);
        Assert.Contains("sid=abc", server.RequestsFor("/frame.js")[0].Cookie);
        Assert.True(Stored(profile, "fromFrame"));
        Assert.True(Stored(profile, "fromFrameScript"));
    }

    /// <summary>
    /// A redirected frame is at its final URL: <c>location</c> reports it, and the frame's relative
    /// URLs resolve against it. Before, both were the <c>src</c> URL the redirect left.
    /// </summary>
    [Fact]
    public void AFrameIsAtThePostRedirectUrl()
    {
        using var server = NewServer()
            .Map("/start", new Reply(302, Location: "/final/doc.html", SetCookies: ["fromRedirect=1; Path=/"]))
            .Map("/final/doc.html", new Reply(Body: "<html><body><p id='in'>x</p><script src='rel.js'></script></body></html>"))
            .Map("/final/rel.js", new Reply(ContentType: Script,
                Body: "document.getElementById('in').textContent = 'rel-ran';"));
        using var profile = NewProfile();
        Navigate(profile, server.Url("/login"));

        var rendered = EngineFor(profile).Execute(
            [PageProbe.Probe(
                "document.getElementById('f').contentWindow.location.href + ' ' + " +
                "document.getElementById('f').contentDocument.getElementById('in').textContent")],
            Page(string.Empty, "<iframe id=\"f\" src=\"/start\"></iframe>"),
            server.Url("/page"));

        Assert.Equal($"{server.Url("/final/doc.html")} rel-ran", PageProbe.OutOf(rendered!));
        // The redirect hop's Set-Cookie was stored before it was followed, and sent to the next hop.
        Assert.True(Stored(profile, "fromRedirect"));
        Assert.Contains("fromRedirect=1", server.RequestsFor("/final/doc.html")[0].Cookie);
    }

    /// <summary>
    /// A cross-site frame: the page is on <c>localhost</c> and the frame on <c>127.0.0.1</c>, and the
    /// frame's script comes back from <c>localhost</c> — the top-level site. The script request must not
    /// carry the <c>SameSite=Lax</c> cookie the profile holds for <c>localhost</c>: the requesting
    /// document is the frame, whose site for cookies is opaque because it is cross-site with the top.
    /// Had the script been attributed to the top document, it would have been same-site and carried
    /// it. The <c>SameSite=None</c> cookie is the control that shows the request carried cookies at all.
    /// The frame's own navigation is cross-site too, so the page's Lax-by-default <c>sid</c> for
    /// <c>127.0.0.1</c> stays home.
    /// </summary>
    [Fact]
    public void ACrossSiteFramesSubresourceGetsNoSameSiteLaxCookie()
    {
        using var server = NewServer()
            .Map("/login-local", new Reply(Body: "<p>in</p>",
                SetCookies: ["lax=1; Path=/; SameSite=Lax", "none=1; Path=/; SameSite=None; Secure"]));
        server
            .Map("/xframe", new Reply(
                Body: $"<html><body><p id='in'>x</p><script src='{server.LocalhostUrl("/x.js")}'></script></body></html>"))
            .Map("/x.js", new Reply(ContentType: Script, Body: "document.getElementById('in').textContent = 'x-ran';"));
        using var profile = NewProfile();
        Navigate(profile, server.Url("/login"));
        Navigate(profile, server.LocalhostUrl("/login-local"));
        Assert.True(Stored(profile, "sid"));
        Assert.True(Stored(profile, "lax"));
        Assert.True(Stored(profile, "none"));

        // The page cannot read a cross-origin frame's document, so the probe only touches the frame's
        // window, which loads it; the frame's own script runs as it loads.
        var rendered = EngineFor(profile).Execute(
            [PageProbe.Probe("typeof document.getElementById('f').contentWindow")],
            Page(string.Empty, $"<iframe id=\"f\" src=\"{server.Url("/xframe")}\"></iframe>"),
            server.LocalhostUrl("/page"));

        Assert.Equal("object", PageProbe.OutOf(rendered!));
        var frame = server.RequestsFor("/xframe")[0];
        var script = server.Single("/x.js");
        Assert.StartsWith("127.0.0.1:", frame.Host);
        Assert.StartsWith("localhost:", script.Host);
        Assert.Null(frame.Cookie);
        Assert.Equal("none=1", script.Cookie);
    }

    /// <summary>
    /// The control for the test above: the same frame served same-site (page on <c>localhost</c>)
    /// receives the Lax cookie on both requests.
    /// </summary>
    [Fact]
    public void ASameSiteFramesSubresourceGetsTheSameSiteLaxCookie()
    {
        using var server = NewServer()
            .Map("/login-local", new Reply(Body: "<p>in</p>", SetCookies: ["lax=1; Path=/; SameSite=Lax"]))
            .Map("/xframe", new Reply(Body: "<html><body><p id='in'>x</p><script src='/x.js'></script></body></html>"))
            .Map("/x.js", new Reply(ContentType: Script, Body: "document.getElementById('in').textContent = 'x-ran';"));
        using var profile = NewProfile();
        Navigate(profile, server.LocalhostUrl("/login-local"));

        var rendered = EngineFor(profile).Execute(
            [PageProbe.Probe("document.getElementById('f').contentDocument.getElementById('in').textContent")],
            Page(string.Empty, "<iframe id=\"f\" src=\"/xframe\"></iframe>"),
            server.LocalhostUrl("/page"));

        Assert.Equal("x-ran", PageProbe.OutOf(rendered!));
        Assert.Equal("lax=1", server.RequestsFor("/xframe")[0].Cookie);
        Assert.Equal("lax=1", server.Single("/x.js").Cookie);
    }

    // ---------------------------------------------------------------------
    //  No profile: the fallback clients keep nothing
    // ---------------------------------------------------------------------

    /// <summary>
    /// Two bridges with no profile network, one after the other, against one origin: a cookie the
    /// first one's responses set is never sent by the second — nor by the first again. The fallback
    /// clients are process-wide, and before this change each kept an automatic jar that did exactly
    /// that, for every document of every host.
    /// </summary>
    [Fact]
    public void WithoutAProfileNoCookieIsKeptBetweenTwoBridges()
    {
        using var server = new LoopbackCookieServer()
            .Map("/set.css", new Reply(ContentType: Css, Body: "#p { color: red }", SetCookies: ["leak=1; Path=/"]))
            .Map("/echo.css", new Reply(ContentType: Css, Body: "#p { color: blue }"))
            .Map("/set.js", new Reply(ContentType: Script, Body: "var s = 1;", SetCookies: ["leakjs=1; Path=/"]))
            .Map("/echo.js", new Reply(ContentType: Script, Body: "var e = 1;"));

        // First document: its stylesheet and script responses set cookies.
        new ScriptEngine().Execute(["1;"], Page("<style id=\"s\">@import url(/set.css);</style>"), server.Url("/one"));
        ScriptExtractionService.ExtractAll(Page("<script src=\"/set.js\"></script>"), server.Url("/one"));

        // Second document, second bridge: nothing may come back.
        new ScriptEngine().Execute(["1;"], Page("<style id=\"s\">@import url(/echo.css);</style>"), server.Url("/two"));
        ScriptExtractionService.ExtractAll(Page("<script src=\"/echo.js\"></script>"), server.Url("/two"));

        Assert.NotEmpty(server.RequestsFor("/set.css"));
        Assert.NotEmpty(server.RequestsFor("/echo.css"));
        Assert.All(server.RequestsFor("/echo.css"), request => Assert.Null(request.Cookie));
        Assert.Null(server.Single("/echo.js").Cookie);
    }
}
