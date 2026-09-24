using Broiler.HtmlBridge;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// What a page may read of a document of another origin, and what may read the page's: the gates on
/// <c>contentDocument</c>, <c>contentWindow</c> and <c>window.frames</c>, a message's <c>source</c> and
/// <c>origin</c>, <c>document.cookie</c> reached through a leaked node, a linked sheet's rules, and the
/// local files a web page may name.
/// </summary>
/// <remarks>
/// The page is on <c>localhost</c> and the other origin on <c>127.0.0.1</c> (another site). Every document
/// shares one realm here, so each of these is a check the bridge makes on the running document's origin,
/// taken from the documents' request contexts.
/// </remarks>
public class CrossOriginAccessTests
{
    private static BrowserNetworkSession NewProfile() =>
        new(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });

    private static ScriptEngine EngineFor(BrowserNetworkSession profile) =>
        new(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));

    private static void Navigate(BrowserNetworkSession profile, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = profile.Send(request, RequestContext.TopLevelNavigation(initiator: null));
        Assert.Equal(200, response.StatusCode);
    }

    private static string Page(string head, string body) =>
        $"<!DOCTYPE html><html><head>{head}</head><body><div id=\"out\"></div>{body}</body></html>";

    private static LoopbackCookieServer NewServer()
    {
        var server = new LoopbackCookieServer();
        server
            .Map("/hop", new Reply(302, Location: server.Url("/target")))
            .Map("/target", new Reply(Body: "<html><body><p id='in'>OTHER-ORIGIN-MARKER</p></body></html>"))
            .Map("/same", new Reply(Body: "<html><body><p id='in'>SAME</p></body></html>"))
            .Map("/report", new Reply(ContentType: "text/plain", Body: "x"));
        return server;
    }

    /// <summary>Runs <paramref name="script"/> guarded on a page at <c>localhost</c> and answers what it wrote.</summary>
    private static string Run(LoopbackCookieServer server, BrowserNetworkSession profile, string body, string script)
    {
        var rendered = EngineFor(profile).Execute([PageProbe.GuardedProbe(script)], Page(string.Empty, body), server.LocalhostUrl("/page"));
        return PageProbe.OutOf(rendered!, decode: true);
    }

    private static string[] Reports(LoopbackCookieServer server) =>
        [.. server.RequestsFor("/report").Select(r => Uri.UnescapeDataString(r.Path))];

    private const string MarkerOf =
        "(function (f) { var d = f.contentDocument;" +
        " return d === null ? 'null' : (d.getElementById('in') ? d.getElementById('in').textContent : 'doc'); })";

    // ---------------------------------------------------------------- sandboxed frames

    /// <summary>
    /// A frame sandboxed without <c>allow-same-origin</c> has an opaque origin, so it is cross-origin to
    /// the page whatever it loaded -- including a document its same-origin <c>src</c> redirected to
    /// another site -- and neither <c>contentDocument</c>, <c>contentWindow</c> nor <c>window.frames</c>
    /// gives it up. With <c>allow-same-origin</c> a same-origin frame is still the page's to read, and
    /// so -- by the bridge's long-standing allowance for markup the page wrote into the URL itself -- is
    /// an unsandboxed <c>data:</c> frame; a sandboxed one is not.
    /// </summary>
    [Theory]
    [InlineData("sandbox=\"allow-scripts\" src=\"/hop\"", "null|null|0")]
    [InlineData("sandbox=\"\" src=\"/hop\"", "null|null|0")]
    [InlineData("sandbox=\"allow-scripts\" src=\"/same\"", "null|null|0")]
    [InlineData("src=\"/hop\"", "null|null|0")]
    [InlineData("sandbox=\"allow-same-origin\" src=\"/same\"", "SAME|[object Object]|1")]
    [InlineData("sandbox=\"allow-scripts\" src=\"data:text/html,<p id='in'>DATA</p>\"", "null|null|0")]
    [InlineData("src=\"data:text/html,<p id='in'>DATA</p>\"", "DATA|[object Object]|1")]
    public void ASandboxedFrameIsCrossOriginWhateverItLoaded(string attributes, string expected)
    {
        using var server = NewServer();
        using var profile = NewProfile();

        var seen = Run(server, profile, $"<iframe id=\"f\" {attributes}></iframe>",
            $"[{MarkerOf}(document.getElementById('f')), String(document.getElementById('f').contentWindow), window.frames.length].join('|')");

        Assert.Equal(expected, seen);
    }

    // ---------------------------------------------------------------- nested frames

    /// <summary>
    /// A cross-origin frame's own child on the frame's origin is the frame's to read: the gate compares
    /// the child with the document whose script asks, not with the top page.
    /// </summary>
    [Fact]
    public void ACrossOriginFrameReadsItsOwnSameOriginChild()
    {
        using var server = NewServer();
        server
            .Map("/outer", new Reply(Body:
                $"<html><body><iframe id='c' src='{server.Url("/inner")}'></iframe><script>" +
                "var d = document.getElementById('c').contentDocument;" +
                $"fetch('{server.Url("/report")}?v=' + (d === null ? 'null' : d.getElementById('in').textContent), {{ mode: 'no-cors' }});" +
                "</script></body></html>"))
            .Map("/inner", new Reply(Body: "<html><body><p id='in'>INNER</p></body></html>"));
        using var profile = NewProfile();

        using var session = EngineFor(profile).ExecuteInteractive(
            ["var touch = document.getElementById('f').contentWindow;"], [],
            Page(string.Empty, $"<iframe id=\"f\" src=\"{server.Url("/outer")}\"></iframe>"), server.LocalhostUrl("/page"));
        session!.SettleLoadWindow();

        Assert.Contains("/report?v=INNER", Reports(server));
        // And the page still cannot read the cross-origin frame.
        Assert.Equal("null", Run(server, profile, $"<iframe id=\"f\" src=\"{server.Url("/outer")}\"></iframe>", "String(document.getElementById('f').contentWindow)"));
    }

    // ---------------------------------------------------------------- postMessage

    /// <summary>
    /// A message from a cross-origin frame hands the receiver a <c>source</c> it can post back to and
    /// nothing else: reading its <c>document</c> is a <c>SecurityError</c>. A reply posted to it reaches
    /// the frame. A same-origin frame's <c>source</c> is its window, document included.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMessagesSourceFromACrossOriginFrameCanOnlyBePostedTo(bool crossOrigin)
    {
        using var server = NewServer();
        var report = server.Url("/report");
        server.Map("/poster", new Reply(Body:
            "<html><body><p id='in'>FRAME-MARKER</p><script>" +
            $"window.addEventListener('message', function (e) {{ fetch('{report}?reply=' + e.data, {{ mode: 'no-cors' }}); }});" +
            "parent.postMessage('hi', '*');</script></body></html>"));
        using var profile = NewProfile();

        var frameUrl = crossOrigin ? server.Url("/poster") : server.LocalhostUrl("/poster");
        const string script =
            "window.addEventListener('message', function (e) {" +
            " var doc; try { doc = e.source.document.getElementById('in').textContent; } catch (x) { doc = 'threw ' + x.name; }" +
            " e.source.postMessage('back', '*');" +
            " document.getElementById('out').textContent = [doc, e.source === e.source.window].join('|'); });" +
            "var touch = document.getElementById('f').contentWindow;";
        using var session = EngineFor(profile).ExecuteInteractive(
            [script], [], Page(string.Empty, $"<iframe id=\"f\" src=\"{frameUrl}\"></iframe>"), server.LocalhostUrl("/page"));
        var seen = PageProbe.OutOf(session!.SettleLoadWindow(), decode: true);

        Assert.Equal(crossOrigin ? "threw SecurityError|true" : "FRAME-MARKER|true", seen);
        Assert.Contains("/report?reply=back", Reports(server));
    }

    /// <summary>
    /// A message's <c>origin</c> is the sending document's origin as the bridge knows it, whatever the
    /// frame's script wrote over its <c>location</c>: an embedder checking <c>e.origin</c> against its
    /// own origin turns a cross-site frame away, and a message the page addressed to its own origin is
    /// not delivered to a frame that claims it.
    /// </summary>
    [Theory]
    [InlineData("location.origin = PAGE;")]
    [InlineData("try { Object.defineProperty(location, 'origin', { value: PAGE }); } catch (e) { }")]
    [InlineData("")]
    public void AFrameCannotClaimAnotherOriginInItsMessages(string spoof)
    {
        using var server = NewServer();
        var pageOrigin = $"http://localhost:{server.Port}";
        var report = server.Url("/report");
        server.Map("/xframe", new Reply(Body:
            $"<html><body><script>var PAGE = '{pageOrigin}';" + spoof +
            $"window.addEventListener('message', function (e) {{ fetch('{report}?got=' + e.data, {{ mode: 'no-cors' }}); }});" +
            "parent.postMessage('claim', '*');</script></body></html>"));
        using var profile = NewProfile();

        var script =
            $"var TRUSTED = '{pageOrigin}';" +
            "window.addEventListener('message', function (e) {" +
            " document.getElementById('out').textContent = (e.origin === TRUSTED ? 'ACCEPTED' : 'REJECTED') + ' ' + e.origin;" +
            " e.source.postMessage('for-page-origin', TRUSTED); e.source.postMessage('for-anyone', '*'); });" +
            "var touch = document.getElementById('f').contentWindow;";
        using var session = EngineFor(profile).ExecuteInteractive(
            [script], [], Page(string.Empty, $"<iframe id=\"f\" src=\"{server.Url("/xframe")}\"></iframe>"), server.LocalhostUrl("/page"));
        var seen = PageProbe.OutOf(session!.SettleLoadWindow(), decode: true);

        Assert.Equal($"REJECTED http://127.0.0.1:{server.Port}", seen);
        Assert.DoesNotContain("/report?got=for-page-origin", Reports(server));
        Assert.Contains("/report?got=for-anyone", Reports(server));
    }

    // ---------------------------------------------------------------- document.cookie

    /// <summary>
    /// A cross-origin frame's script that reaches the page's Document through a node the page left on a
    /// global cannot read or set the page's cookies through it; the frame's own <c>document.cookie</c>
    /// is untouched.
    /// </summary>
    [Fact]
    public void AnotherOriginsScriptCannotUseThePagesCookiesThroughALeakedNode()
    {
        using var server = NewServer();
        server
            .Map("/login-local", new Reply(Body: "<p>in</p>", SetCookies: ["pagemarker=PAGE-COOKIE; Path=/"]))
            .Map("/login-ip", new Reply(Body: "<p>in</p>", SetCookies: ["ipc=1; Path=/; SameSite=None; Secure"]));
        var report = server.Url("/report");
        server.Map("/child", new Reply(Body:
            "<html><body><script>" +
            $"function r(k, v) {{ fetch('{report}?k=' + k + '&v=' + encodeURIComponent(String(v)), {{ mode: 'no-cors' }}); }}" +
            "try { r('read', pageNode.ownerDocument.cookie); } catch (e) { r('read', 'threw ' + e.name); }" +
            "try { pageNode.ownerDocument.cookie = 'planted=1; path=/'; r('write', 'done'); } catch (e) { r('write', 'threw ' + e.name); }" +
            "r('own', document.cookie);" +
            "</script></body></html>"));
        using var profile = NewProfile();
        Navigate(profile, server.LocalhostUrl("/login-local"));
        Navigate(profile, server.Url("/login-ip"));

        using var session = EngineFor(profile).ExecuteInteractive(
            ["var pageNode = document.getElementById('out'); var touch = document.getElementById('f').contentWindow;"], [],
            Page(string.Empty, $"<iframe id=\"f\" src=\"{server.Url("/child")}\"></iframe>"), server.LocalhostUrl("/page"));
        session!.SettleLoadWindow();

        var reports = Reports(server);
        Assert.Contains("/report?k=read&v=threw SecurityError", reports);
        Assert.Contains("/report?k=write&v=threw SecurityError", reports);
        Assert.Contains("/report?k=own&v=ipc=1", reports);
        Assert.DoesNotContain(profile.Cookies.Snapshot(), cookie => cookie.Name == "planted");
    }

    // ---------------------------------------------------------------- local files

    private static string LocalFile(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "broiler-htmlbridge-tests", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return new Uri(path).AbsoluteUri;
    }

    /// <summary>
    /// An http(s) page cannot load a local file into a frame or an object: nothing of the file is read
    /// -- the local page's own script never runs -- and the frame's document is an empty, opaque one
    /// that the page cannot read either.
    /// </summary>
    [Fact]
    public void AnHttpPageCannotReadALocalFileThroughAFrame()
    {
        using var server = NewServer();
        var fileUrl = LocalFile("marker.html",
            $"<p id='in'>LOCAL-FILE-MARKER</p><script>fetch('{server.Url("/report")}?ran=local', {{ mode: 'no-cors' }});</script>");
        using var profile = NewProfile();

        var seen = Run(server, profile,
            $"<iframe id=\"f\" src=\"{fileUrl}\"></iframe><object id=\"o\" type=\"text/html\" data=\"{fileUrl}\"></object>",
            "[String(document.getElementById('f').contentDocument), String(document.getElementById('o').contentDocument)].join('|')");

        Assert.Equal("null|null", seen);
        Assert.DoesNotContain("LOCAL-FILE-MARKER", seen);
        Assert.Empty(server.RequestsFor("/report"));
    }

    /// <summary>
    /// An http(s) page does not run a local script, apply a local stylesheet or start a local worker;
    /// a <c>file:</c> page still runs its local script.
    /// </summary>
    [Fact]
    public void AnHttpPageDoesNotLoadLocalScriptsStylesheetsOrWorkers()
    {
        var script = LocalFile("secret.js", "window.localSecret = 'LOCAL-SCRIPT';");
        var sheet = LocalFile("secret.css", "#box { width: 31px; }");
        var worker = LocalFile("worker.js", "postMessage('LOCAL-WORKER');");
        using var server = NewServer();
        using var profile = NewProfile();

        var html = Page(
            $"<link rel=\"stylesheet\" href=\"{sheet}\">",
            $"<div id=\"box\"></div><script src=\"{script}\"></script>");
        var probe = PageProbe.GuardedProbe(
            "[typeof window.localSecret, getComputedStyle(document.getElementById('box')).width].join('|')");
        var workerProbe =
            $"try {{ var w = new Worker('{worker}'); w.onmessage = function (e) {{ document.title = 'worker:' + e.data; }}; }} catch (e) {{ document.title = 'worker-threw'; }}";
        var extraction = ScriptExtractionService.ExtractAll(
            html, server.LocalhostUrl("/page"), deliveredPolicy: null,
            new ScriptFetchContext(profile, DocumentRequestContext.CreateTopLevel(new Uri(server.LocalhostUrl("/page")))));

        using var session = EngineFor(profile).ExecuteInteractive(
            [.. extraction.Scripts, probe, workerProbe], [], html, server.LocalhostUrl("/page"));
        var settled = session!.SettleLoadWindow();

        Assert.StartsWith("undefined|", PageProbe.OutOf(settled, decode: true));
        Assert.DoesNotContain("31px", PageProbe.OutOf(settled, decode: true));
        Assert.DoesNotContain("LOCAL-WORKER", settled);

        // Control: the same local script is run for a file: page.
        var filePage = LocalFile("page.html", string.Empty);
        var local = ScriptExtractionService.ExtractAll(Page(string.Empty, $"<script src=\"{script}\"></script>"), filePage);
        var rendered = new ScriptEngine().Execute(
            [.. local.Scripts, PageProbe.Probe("typeof window.localSecret")], Page(string.Empty, string.Empty), filePage);
        Assert.Equal("string", PageProbe.OutOf(rendered!));
    }

    // ---------------------------------------------------------------- stylesheets

    /// <summary>
    /// A linked sheet another origin serves without CORS still applies, but its rules are not the
    /// page's to read or edit: <c>cssRules</c>, <c>insertRule</c> and <c>deleteRule</c> throw
    /// <c>SecurityError</c> -- here for a sheet that reflects the page site's cookie the profile sent
    /// with it. A same-origin sheet, and a CORS-approved one, stay readable.
    /// </summary>
    [Theory]
    [InlineData("cross", "threw SecurityError|threw SecurityError|threw SecurityError|31px")]
    [InlineData("same", "READ|ok|ok|31px")]
    [InlineData("cors", "READ|ok|ok|31px")]
    public void ACrossOriginSheetsRulesAreNotReadable(string kind, string expected)
    {
        using var server = NewServer();
        server
            .Map("/login-local", new Reply(Body: "<p>in</p>", SetCookies: ["xssession=SECRET-abcdef; Path=/; SameSite=None; Secure"]))
            .Map("/reflect.css", request => new Reply(ContentType: "text/css",
                Body: "#box { width: 31px; } #leak::before { content: \"" + (request.Cookie ?? "none") + "\"; }",
                Headers: [("Access-Control-Allow-Origin", $"http://127.0.0.1:{server.Port}")]));
        using var profile = NewProfile();
        Navigate(profile, server.LocalhostUrl("/login-local"));

        var link = kind switch
        {
            "cross" => $"<link rel=\"stylesheet\" href=\"{server.LocalhostUrl("/reflect.css")}\">",
            "cors" => $"<link rel=\"stylesheet\" crossorigin=\"anonymous\" href=\"{server.LocalhostUrl("/reflect.css")}\">",
            _ => $"<link rel=\"stylesheet\" href=\"{server.Url("/reflect.css")}\">",
        };
        const string script =
            "var sheet = document.styleSheets[0];" +
            "function attempt(f) { try { return f(); } catch (e) { return 'threw ' + e.name; } }" +
            "document.getElementById('out').textContent = [" +
            " attempt(function () { return sheet.cssRules.length > 0 && sheet.cssRules[0].cssText ? 'READ' : 'EMPTY'; })," +
            " attempt(function () { sheet.insertRule('#x { color: red; }', 0); return 'ok'; })," +
            " attempt(function () { sheet.deleteRule(0); return 'ok'; })," +
            " getComputedStyle(document.getElementById('box')).width].join('|');";

        var rendered = EngineFor(profile).Execute(
            [script], Page(link, "<div id=\"box\"></div><div id=\"leak\"></div>"), server.Url("/page"));
        var seen = PageProbe.OutOf(rendered!, decode: true);

        Assert.Equal(expected, seen);
        Assert.DoesNotContain("SECRET", seen);
    }
}
