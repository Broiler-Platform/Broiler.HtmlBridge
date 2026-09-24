using Broiler.HtmlBridge;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A frame's script, and everything it defers, speaks for the frame's document and never for the page
/// that embeds it: <c>document.cookie</c>, <c>location</c>, and the client and base URL of every request
/// it makes. Every document shares one realm here, so the frame's identity has to travel with the work
/// it queues — microtasks, promise reactions, <c>await</c> continuations, module bodies — and with the
/// requests the bridge makes on its behalf (<c>@import</c>, module imports, a redirected frame's origin).
/// </summary>
/// <remarks>
/// <para>
/// <b>The two sites.</b> The page is on <c>localhost</c>, the frame on <c>127.0.0.1</c> (another site).
/// The profile holds <c>localhost</c> cookies of every kind — <c>SameSite=None</c>, <c>Lax</c>, default
/// and <c>HttpOnly</c> — and one <c>127.0.0.1</c> cookie, <c>ipc=1</c>, the only one the frame may read.
/// </para>
/// <para>
/// <b>How a frame reports.</b> It sends what it sees to <c>/report</c> on its own origin, so every
/// report is a request the frame is entitled to make; a report that names the page's cookies, or a
/// relative request that went to <c>localhost</c>, is the defect.
/// </para>
/// </remarks>
public class FrameIdentityTests
{
    private const string Script = "text/javascript";
    private const string FrameCookie = "ipc=1";

    private static BrowserNetworkSession NewProfile() =>
        new(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });

    private static void Navigate(BrowserNetworkSession profile, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = profile.Send(request, RequestContext.TopLevelNavigation(initiator: null));
        Assert.Equal(200, response.StatusCode);
    }

    private static ScriptEngine EngineFor(BrowserNetworkSession profile) =>
        new(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));

    /// <summary>
    /// A server and a profile holding the page site's cookies (<c>localhost</c>) and the frame site's
    /// one cookie (<c>127.0.0.1</c>).
    /// </summary>
    private sealed class CrossSite : IDisposable
    {
        public CrossSite(LoopbackCookieServer server, BrowserNetworkSession profile) => (Server, Profile) = (server, profile);

        public LoopbackCookieServer Server { get; }

        public BrowserNetworkSession Profile { get; }

        public void Dispose()
        {
            Profile.Dispose();
            Server.Dispose();
        }
    }

    private static CrossSite CrossSiteSetup()
    {
        var server = new LoopbackCookieServer()
            .Map("/login-local", new Reply(Body: "<p>in</p>", SetCookies:
            [
                "none=1; Path=/; SameSite=None; Secure",
                "lax=1; Path=/; SameSite=Lax",
                "topjs=visible; Path=/",
                "session=s3cret; Path=/; HttpOnly",
            ]))
            .Map("/login-ip", new Reply(Body: "<p>in</p>", SetCookies: ["ipc=1; Path=/; SameSite=None; Secure"]))
            .Map("/report", new Reply(ContentType: "text/plain", Body: "x"))
            .Map("/rel", new Reply(ContentType: "text/plain", Body: "x"))
            .Map("/data", new Reply(ContentType: "text/plain", Body: "frame-data"));
        var profile = NewProfile();
        Navigate(profile, server.LocalhostUrl("/login-local"));
        Navigate(profile, server.Url("/login-ip"));
        return new CrossSite(server, profile);
    }

    /// <summary>
    /// The frame's reporter: <c>report(kind)</c> sends the frame's view of <c>document.cookie</c> and
    /// <c>location</c> to its own origin, and makes a relative request that must resolve against the
    /// frame's URL and go out as the frame's.
    /// </summary>
    private static string Reporter(LoopbackCookieServer server) =>
        "function report(kind) {" +
        $" fetch('{server.Url("/report")}?k=' + kind + '&c=' + encodeURIComponent(document.cookie) + '&loc=' + encodeURIComponent(String(location.href)), {{ mode: 'no-cors' }});" +
        " fetch('/rel?k=' + kind);" +
        "}";

    private static Dictionary<string, string> Query(string target)
    {
        var query = target.Split('?', 2) is [_, var q] ? q : string.Empty;
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty);
    }

    /// <summary>
    /// Asserts that the frame reported <paramref name="kinds"/> once each, as the frame: its own
    /// cookies, its own location, and a relative request to its own origin that carries none of the
    /// page site's cookies.
    /// </summary>
    private static void AssertReportedAsTheFrame(LoopbackCookieServer server, params string[] kinds)
    {
        var reports = server.RequestsFor("/report").Select(r => Query(r.Path)).ToList();
        var relative = server.RequestsFor("/rel");

        foreach (var kind in kinds)
        {
            var report = reports.Where(r => r["k"] == kind).ToList();
            Assert.True(report.Count == 1,
                $"expected one '{kind}' report, got {report.Count}; reports: " +
                string.Join(" | ", reports.Select(r => string.Join(",", r.Select(p => $"{p.Key}={p.Value}")))));
            Assert.Equal(FrameCookie, report[0]["c"]);
            Assert.StartsWith(server.Url("/xframe"), report[0]["loc"]);

            var request = Assert.Single(relative, r => Query(r.Path)["k"] == kind);
            Assert.Equal($"127.0.0.1:{server.Port}", request.Host);
            Assert.Equal(FrameCookie, request.Cookie);
        }

        // Nothing the frame did reached the page's site with the page's cookies.
        Assert.DoesNotContain(relative, r => r.Host.StartsWith("localhost", StringComparison.Ordinal));
        Assert.DoesNotContain(reports, r => r["c"].Contains("lax=1", StringComparison.Ordinal));
    }

    private static void Settle(InteractiveSession? session)
    {
        Assert.NotNull(session);
        session!.SettleLoadWindow();
    }

    /// <summary>
    /// Microtasks, promise reactions, <c>await</c> continuations — after a plain value and after a
    /// timer — and the reactions to a <c>fetch()</c> the frame made all run as the frame, whether the
    /// frame was in the markup or inserted by the page's own script (which runs the frame's scripts
    /// while the page's script is still on the stack).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EverythingAFrameDefersRunsAsTheFrame(bool insertedByScript)
    {
        using var site = CrossSiteSetup();
        var (server, profile) = (site.Server, site.Profile);
        server.Map("/xframe", new Reply(Body:
            "<html><body><script>" + Reporter(server) +
            "report('sync');" +
            "queueMicrotask(function () { report('qmt'); });" +
            "Promise.resolve().then(function () { report('then'); });" +
            "(async function () { await null; report('await');" +
            " await new Promise(function (resolve) { setTimeout(resolve, 0); }); report('await-timer'); })();" +
            "fetch('/data').then(function (r) { return r.text(); }).then(function () { report('fetch-then'); });" +
            "setTimeout(function () { Promise.resolve().then(function () { report('timer-then'); }); }, 0);" +
            "</script></body></html>"));

        var frameUrl = server.Url("/xframe");
        var body = insertedByScript
            ? string.Empty
            : $"<iframe id=\"f\" src=\"{frameUrl}\"></iframe>";
        string[] scripts = insertedByScript
            ? [$"var f = document.createElement('iframe'); f.id = 'f'; f.src = '{frameUrl}'; document.body.appendChild(f); Promise.resolve().then(function () {{ window.topThen = document.cookie; }});"]
            : ["var z = 1;"];
        var html = $"<!DOCTYPE html><html><head></head><body><div id=\"out\"></div>{body}</body></html>";

        using var session = EngineFor(profile).ExecuteInteractive(scripts, [], html, server.LocalhostUrl("/page"));
        Settle(session);

        AssertReportedAsTheFrame(server, "sync", "qmt", "then", "await", "await-timer", "fetch-then", "timer-then");
        Assert.All(server.RequestsFor("/data"), r => Assert.Equal($"127.0.0.1:{server.Port}", r.Host));
    }

    /// <summary>
    /// The page's own microtasks stay the page's: a reaction the page queued while a frame was being
    /// loaded under its script reads the page's cookies, not the frame's.
    /// </summary>
    [Fact]
    public void ThePagesOwnReactionsStayThePages()
    {
        using var site = CrossSiteSetup();
        var (server, profile) = (site.Server, site.Profile);
        server.Map("/xframe", new Reply(Body: "<html><body><script>Promise.resolve().then(function () { window.frameThen = 1; });</script></body></html>"));

        var html = "<!DOCTYPE html><html><head></head><body><div id=\"out\"></div></body></html>";
        var rendered = EngineFor(profile).Execute(
            [$"var f = document.createElement('iframe'); f.src = '{server.Url("/xframe")}'; document.body.appendChild(f);" +
             "Promise.resolve().then(function () { document.getElementById('out').textContent = document.cookie + '|' + location.host; });"],
            html,
            server.LocalhostUrl("/page"));

        var seen = PageProbe.OutOf(rendered!);
        Assert.Contains("lax=1", seen);
        Assert.EndsWith($"|localhost:{server.Port}", seen);
    }

    /// <summary>
    /// A frame's module script runs as the frame: its <c>document.cookie</c> and <c>location</c> are the
    /// frame's, and its relative <c>fetch()</c> goes to the frame's origin. The page has a module of its
    /// own, which is what makes the frame's modules run on the shared module context.
    /// </summary>
    [Fact]
    public void AFramesModuleScriptRunsAsTheFrame()
    {
        using var site = CrossSiteSetup();
        var (server, profile) = (site.Server, site.Profile);
        server
            .Map("/xframe", new Reply(Body:
                "<html><body><script>" + Reporter(server) + "</script>" +
                "<script type=\"module\">report('module'); Promise.resolve().then(function () { report('module-then'); });" +
                $" fetch('{server.LocalhostUrl("/credentialed")}', {{ mode: 'no-cors', credentials: 'include' }});</script>" +
                "</body></html>"))
            .Map("/credentialed", new Reply(ContentType: "text/plain", Body: "x"));

        var pageUrl = server.LocalhostUrl("/page");
        var html = "<!DOCTYPE html><html><head></head><body><div id=\"out\"></div>" +
                   "<script type=\"module\">window.topModule = 1;</script>" +
                   $"<iframe id=\"f\" src=\"{server.Url("/xframe")}\"></iframe></body></html>";
        var extraction = ScriptExtractionService.ExtractAll(html, pageUrl);
        Assert.NotEmpty(extraction.ModuleRoots);

        using var session = EngineFor(profile).ExecuteInteractive(["var z = 1;"], [], html, pageUrl, extraction.ModuleRoots);
        Settle(session);

        AssertReportedAsTheFrame(server, "module", "module-then");

        // A cross-site frame's credentialed request carries only the page site's SameSite=None cookie.
        Assert.Equal("none=1", server.Single("/credentialed").Cookie);
    }

    /// <summary>
    /// A sandboxed <c>srcdoc</c> frame's inline module imports are its own requests: an opaque origin,
    /// so no cookie and <c>Origin: null</c> — even though the page, whose URL the frame's inline module
    /// resolves against, has an inline module of its own.
    /// </summary>
    [Fact]
    public void ASandboxedSrcdocFramesModuleImportIsTheFramesRequest()
    {
        using var server = new LoopbackCookieServer()
            .Map("/login", new Reply(Body: "<p>in</p>", SetCookies: ["sid=abc; Path=/"]))
            .Map("/dep.js", new Reply(ContentType: Script, Body: "window.dep = 1;"))
            .Map("/topdep.js", new Reply(ContentType: Script, Body: "export const a = 1;"));
        using var profile = NewProfile();
        Navigate(profile, server.Url("/login"));

        var pageUrl = server.Url("/page");
        const string srcdoc = "&lt;script&gt;var x = 1;&lt;/script&gt;&lt;script type=&quot;module&quot;&gt;import &quot;./dep.js&quot;;&lt;/script&gt;";
        var html = "<!DOCTYPE html><html><head></head><body><div id=\"out\"></div>" +
                   "<script type=\"module\">import { a } from './topdep.js'; window.topmod = a;</script>" +
                   $"<iframe id=\"f\" sandbox=\"allow-scripts\" srcdoc=\"{srcdoc}\"></iframe>" +
                   "<script>var probe = typeof document.getElementById('f').contentWindow;</script></body></html>";
        var extraction = ScriptExtractionService.ExtractAll(html, pageUrl);

        using var session = EngineFor(profile).ExecuteInteractive(
            extraction.Scripts, extraction.DeferredScripts, html, pageUrl, extraction.ModuleRoots);
        Settle(session);

        Assert.Equal("sid=abc", server.Single("/topdep.js").Cookie);
        var dep = server.Single("/dep.js");
        Assert.Null(dep.Cookie);
        Assert.Equal("null", dep.Origin);
    }

    /// <summary>
    /// A page with a module of its own (which is what gives it a module context, and a classic script an
    /// <c>import()</c>) embedding the frame at <c>127.0.0.1</c>/<paramref name="framePath"/>.
    /// </summary>
    private static InteractiveSession? RunPageWithModuleAndFrame(CrossSite site, string framePath)
    {
        var pageUrl = site.Server.LocalhostUrl("/page");
        var html = "<!DOCTYPE html><html><head></head><body><div id=\"out\"></div>" +
                   "<script type=\"module\">window.topModule = 1;</script>" +
                   $"<iframe id=\"f\" src=\"{site.Server.Url(framePath)}\"></iframe></body></html>";
        var extraction = ScriptExtractionService.ExtractAll(html, pageUrl);
        Assert.NotEmpty(extraction.ModuleRoots);

        return EngineFor(site.Profile).ExecuteInteractive(["var z = 1;"], [], html, pageUrl, extraction.ModuleRoots);
    }

    /// <summary>
    /// A cross-site frame's classic script calls <c>import()</c> for a module on the page's site. It is
    /// the frame's request: a CORS request from the frame's origin with <c>same-origin</c> credentials,
    /// so it carries no cookie — the page site's <c>Lax</c> cookie least of all, which it would carry as
    /// the page's own same-origin request — and it succeeds only because the module allows any origin.
    /// </summary>
    [Fact]
    public void ACrossSiteFramesDynamicImportIsTheFramesRequest()
    {
        using var site = CrossSiteSetup();
        var server = site.Server;
        server
            .Map("/xframe", new Reply(Body:
                "<html><body><script>" + Reporter(server) +
                $"import('{server.LocalhostUrl("/dyn.js")}').then(" +
                "function (m) { report('import-' + m.v); }, function () { report('import-failed'); });" +
                "</script></body></html>"))
            .Map("/dyn.js", new Reply(ContentType: Script, Body: "export const v = 'ran';",
                Headers: [("Access-Control-Allow-Origin", "*")]));

        using var session = RunPageWithModuleAndFrame(site, "/xframe");
        Settle(session);

        var module = server.Single("/dyn.js");
        Assert.StartsWith("localhost:", module.Host);
        Assert.Null(module.Cookie);
        Assert.Equal($"http://127.0.0.1:{server.Port}", module.Origin);
        AssertReportedAsTheFrame(server, "import-ran");
    }

    /// <summary>
    /// A frame's <c>import()</c> resolves against the frame's URL and is held to the frame's own policy,
    /// not the page's: under <c>script-src 'unsafe-inline'</c> (which admits no URL) nothing is requested,
    /// though the page has no policy at all. The control, without the policy, fetches the module from
    /// the frame's directory on the frame's origin.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFramesDynamicImportResolvesAgainstTheFrameUnderTheFramesPolicy(bool withPolicy)
    {
        using var site = CrossSiteSetup();
        var server = site.Server;
        server
            .Map("/sub/xframe", new Reply(Body:
                "<html><head>" +
                (withPolicy ? CspFixture.Meta("script-src 'unsafe-inline'") : string.Empty) +
                "</head><body><script>" + Reporter(server) +
                "import('./dyn.js').then(function (m) { report('import-' + m.v); }, function () { report('import-failed'); });" +
                "</script></body></html>"))
            .Map("/sub/dyn.js", new Reply(ContentType: Script, Body: "export const v = 'ran';"));

        using var session = RunPageWithModuleAndFrame(site, "/sub/xframe");
        Settle(session);

        if (withPolicy)
        {
            Assert.Empty(server.RequestsFor("/sub/dyn.js"));
            Assert.Empty(server.RequestsFor("/dyn.js"));
        }
        else
        {
            var module = server.Single("/sub/dyn.js");
            Assert.Equal($"127.0.0.1:{server.Port}", module.Host);
            // Same-origin with the frame: its own cookie, and none of the page site's.
            Assert.Equal(FrameCookie, module.Cookie);
        }
    }

    /// <summary>
    /// A cross-site frame's <c>@import</c>s — in its own <c>&lt;style&gt;</c> and inside a sheet it links —
    /// are the frame's requests: like the linked sheet itself, they carry the page site's
    /// <c>SameSite=None</c> cookie and not its <c>Lax</c> one.
    /// </summary>
    [Fact]
    public void ACrossSiteFramesImportsAreTheFramesRequests()
    {
        using var site = CrossSiteSetup();
        var (server, profile) = (site.Server, site.Profile);
        server
            .Map("/xframe", new Reply(Body:
                "<html><head>" +
                $"<style>@import url(\"{server.LocalhostUrl("/imp.css")}\");</style>" +
                $"<link rel=\"stylesheet\" href=\"{server.LocalhostUrl("/lnk.css")}\">" +
                "</head><body><div id=\"d\">x</div><script>" +
                "var c = getComputedStyle(document.getElementById('d')).color;" +
                "</script></body></html>"))
            .Map("/imp.css", new Reply(ContentType: "text/css", Body: "#d { color: red; }"))
            .Map("/lnk.css", new Reply(ContentType: "text/css", Body: $"@import url(\"{server.LocalhostUrl("/lnkimp.css")}\");"))
            .Map("/lnkimp.css", new Reply(ContentType: "text/css", Body: "#d { background: blue; }"));

        EngineFor(profile).Execute(
            ["var z = typeof document.getElementById('f').contentWindow;"],
            $"<!DOCTYPE html><html><head></head><body><div id=\"out\"></div><iframe id=\"f\" src=\"{server.Url("/xframe")}\"></iframe></body></html>",
            server.LocalhostUrl("/page"));

        foreach (var path in new[] { "/lnk.css", "/imp.css", "/lnkimp.css" })
        {
            var requests = server.RequestsFor(path);
            Assert.NotEmpty(requests);
            Assert.All(requests, r => Assert.Equal("none=1", r.Cookie));
        }
    }

    /// <summary>
    /// The speculative preload scan fetches a <c>crossorigin</c> stylesheet the way its link does: an
    /// anonymous CORS request, which carries no cookie and whose <c>Set-Cookie</c> is not stored.
    /// </summary>
    [Fact]
    public void ThePreloadScanHonoursALinksCrossOriginAttribute()
    {
        using var server = new LoopbackCookieServer()
            .Map("/login-local", new Reply(Body: "<p>in</p>", SetCookies: ["none=1; Path=/; SameSite=None; Secure"]));
        server.Map("/cdn.css", new Reply(ContentType: "text/css", Body: "#out { color: red; }",
            SetCookies: ["fromSheet=1; Path=/; SameSite=None; Secure"],
            Headers: [("Access-Control-Allow-Origin", "*")]));
        using var profile = NewProfile();
        Navigate(profile, server.LocalhostUrl("/login-local"));

        var html = "<!DOCTYPE html><html><head>" +
                   $"<link rel=\"stylesheet\" crossorigin=\"anonymous\" href=\"{server.LocalhostUrl("/cdn.css")}\">" +
                   "</head><body><div id=\"out\"></div></body></html>";
        using var session = EngineFor(profile).ExecuteInteractive(
            ["var z = getComputedStyle(document.getElementById('out')).color;"], [], html, server.Url("/page"));
        Settle(session);

        var requests = server.RequestsFor("/cdn.css");
        Assert.NotEmpty(requests);
        Assert.All(requests, r =>
        {
            Assert.Null(r.Cookie);
            Assert.Equal($"http://127.0.0.1:{server.Port}", r.Origin);
        });
        Assert.DoesNotContain(profile.Cookies.Snapshot(), cookie => cookie.Name == "fromSheet");
    }

    /// <summary>
    /// A frame whose same-origin <c>src</c> redirects to another origin is a cross-origin frame: the page
    /// gets neither its document nor its window (and so neither its <c>document.cookie</c> nor a way to
    /// run code as it), and <c>window.frames</c> does not list it. An <c>&lt;object&gt;</c> is judged
    /// the same way. A frame that stays on the page's origin is still reachable.
    /// </summary>
    [Fact]
    public void AFrameRedirectedToAnotherOriginIsCrossOrigin()
    {
        using var site = CrossSiteSetup();
        var (server, profile) = (site.Server, site.Profile);
        server
            .Map("/hop", new Reply(302, Location: server.Url("/target")))
            .Map("/target", new Reply(Body: "<html><body><p id='in'>target</p></body></html>"))
            .Map("/same", new Reply(Body: "<html><body><p id='in'>same</p></body></html>"));

        var rendered = EngineFor(profile).Execute(
            [PageProbe.GuardedProbe(
                "[String(document.getElementById('f').contentDocument), String(document.getElementById('f').contentWindow)," +
                " String(document.getElementById('o').contentDocument), window.frames.length," +
                " document.getElementById('s').contentDocument.getElementById('in').textContent].join('|')")],
            "<!DOCTYPE html><html><head></head><body><div id=\"out\"></div>" +
            "<iframe id=\"f\" src=\"/hop\"></iframe>" +
            "<object id=\"o\" type=\"text/html\" data=\"/hop\"></object>" +
            "<iframe id=\"s\" src=\"/same\"></iframe></body></html>",
            server.LocalhostUrl("/page"));

        Assert.Equal("null|null|null|1|same", PageProbe.OutOf(rendered!));
    }
    /// <summary>
    /// A frame's module root that outlasts any wait -- a static import the server answers slowly, then
    /// a top-level <c>await</c> on a timer, which cannot fire while the thread that fires it waits --
    /// still runs every part of itself as the frame, and the load does not wait for it. The page has a
    /// module of its own, which is what runs the frame's modules on the shared module context.
    /// </summary>
    /// <remarks>
    /// The frame's roots used to be evaluated on the engine's worker thread and waited for, up to 30 s,
    /// inside the frame's window context. The timer here cannot fire during that wait, so the load took
    /// the whole budget, and the module then resumed on the worker's own pump after the switch had
    /// been undone -- as the embedding page, with its cookies, its location and its fetch client.
    /// </remarks>
    [Fact]
    public void AFramesModuleThatWaitsOnTheEventLoopRunsAsTheFrameWithoutHoldingUpTheLoad()
    {
        using var site = CrossSiteSetup();
        var server = site.Server;
        server
            .Map("/slowdep.js", _ =>
            {
                Thread.Sleep(1500);
                return new Reply(ContentType: Script, Body: "export const v = 'dep';");
            })
            .Map("/xframe", new Reply(Body:
                "<html><body><script>" + Reporter(server) + "</script>" +
                "<script type=\"module\">import { v } from './slowdep.js';" +
                " report('module-' + v);" +
                " await new Promise(function (resolve) { setTimeout(resolve, 0); });" +
                " report('module-tla');" +
                " await null; report('module-tla-then');</script>" +
                "</body></html>"));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var session = RunPageWithModuleAndFrame(site, "/xframe");
        Settle(session);
        watch.Stop();

        AssertReportedAsTheFrame(server, "module-dep", "module-tla", "module-tla-then");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20),
            $"the load waited {watch.Elapsed.TotalSeconds:0.0} s for a frame module that waits on the event loop");
    }

    /// <summary>
    /// A frame inserted after load, from a timer the host steps on its own (UI) thread, runs its module
    /// without holding that step up: the module's top-level <c>await</c> waits for a timer, which runs
    /// on a later step, and the module finishes as the frame.
    /// </summary>
    [Fact]
    public void AFrameModuleInsertedAfterLoadDoesNotHoldUpTheStepThatInsertedIt()
    {
        var html = "<!DOCTYPE html><html><head></head><body><div id=\"out\">unset</div>" +
                   "<script type=\"module\">window.topModule = 1;</script></body></html>";
        const string pageUrl = "https://page.example/";
        var extraction = ScriptExtractionService.ExtractAll(html, pageUrl);
        Assert.NotEmpty(extraction.ModuleRoots);

        const string insertFrame =
            "setTimeout(function () {" +
            " var f = document.createElement('iframe');" +
            " f.setAttribute('srcdoc', '<script type=\"module\">' +" +
            "  'await new Promise(function (r) { setTimeout(r, 0); });' +" +
            "  'parent.frameResult = \"tla \" + location.href;' +" +
            "  '</' + 'script>');" +
            " document.body.appendChild(f);" +
            "}, 0);" +
            // The frame's `parent.document` is the frame's own here (every document shares one global),
            // so the page reads the result back itself.
            "setTimeout(function check() {" +
            " if (window.frameResult) document.getElementById('out').textContent = window.frameResult;" +
            " else setTimeout(check, 10);" +
            "}, 10);";
        using var session = new ScriptEngine().ExecuteInteractive([insertFrame], [], html, pageUrl, extraction.ModuleRoots);
        Assert.NotNull(session);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.NotNull(session!.Step());
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10),
            $"the step that inserted the frame took {watch.Elapsed.TotalSeconds:0.0} s");

        Assert.Equal("tla about:srcdoc", PageProbe.OutOf(session.SettleLoadWindow()));
    }

    /// <summary>
    /// Work a frame's document left queued does not run once the frame has navigated to another
    /// document -- here one on the page's own site, which the old document would otherwise have run
    /// as: with the page site's cookies, the new document's location and a same-origin fetch client.
    /// The page navigates the frame from a timer and then settles what the old document was waiting
    /// for. The controls, without the navigation, report as the frame.
    /// </summary>
    [Theory]
    [InlineData("page-resolved", "new Promise(function (r) { parent.resolveOld = r; }).then(function () { report('page-resolved'); });", true)]
    [InlineData("page-resolved", "new Promise(function (r) { parent.resolveOld = r; }).then(function () { report('page-resolved'); });", false)]
    [InlineData("await-cont", "(async function () { await new Promise(function (r) { parent.resolveOld = r; }); report('await-cont'); })();", true)]
    [InlineData("await-cont", "(async function () { await new Promise(function (r) { parent.resolveOld = r; }); report('await-cont'); })();", false)]
    [InlineData("own-timer", "setTimeout(function () { report('own-timer'); }, 300);", true)]
    [InlineData("own-timer", "setTimeout(function () { report('own-timer'); }, 300);", false)]
    public void WorkANavigatedAwayFrameLeftQueuedDoesNotRunAsItsSuccessor(string kind, string frameWork, bool navigate)
    {
        using var site = CrossSiteSetup();
        var server = site.Server;
        server
            .Map("/xframe", new Reply(Body: "<html><body><script>" + Reporter(server) + frameWork + "</script></body></html>"))
            .Map("/same", new Reply(Body: "<html><body><p>the page site's document</p></body></html>"));

        var navigation = navigate ? $"document.getElementById('f').src = '{server.LocalhostUrl("/same")}';" : string.Empty;
        var html = "<!DOCTYPE html><html><head></head><body><div id=\"out\"></div>" +
                   $"<iframe id=\"f\" src=\"{server.Url("/xframe")}\"></iframe></body></html>";
        using var session = EngineFor(site.Profile).ExecuteInteractive(
            ["setTimeout(function () { " + navigation + " if (window.resolveOld) window.resolveOld(); }, 100);"],
            [], html, server.LocalhostUrl("/page"));
        Settle(session);
        Thread.Sleep(500);
        session!.Complete();

        if (navigate)
        {
            Assert.NotEmpty(server.RequestsFor("/same"));
            Assert.Empty(server.RequestsFor("/report").Where(r => Query(r.Path)["k"] == kind));
            Assert.Empty(server.RequestsFor("/rel").Where(r => Query(r.Path)["k"] == kind));
        }
        else
        {
            AssertReportedAsTheFrame(server, kind);
        }
    }
    private const string OrderFramePage =
        "<html><body><iframe id=\"f\" srcdoc=\"<html><body><p id='inner'>frame</p></body></html>\"></iframe>" +
        "<div id=\"out\">unset</div></body></html>";

    private const string OrderPlainPage = "<html><body><div id=\"out\">unset</div></body></html>";

    // Listeners on the frame's window run in the frame's window context, so the jobs they queue are the
    // frame's.
    private const string OrderSetup =
        "var log = []; var w = document.getElementById('f').contentWindow;" +
        "w.addEventListener('ping', function () { log.push('Fsync@' + location.href);" +
        " Promise.resolve().then(function () { log.push('F1@' + location.href); }); });" +
        "w.addEventListener('qping', function () { queueMicrotask(function () { log.push('FQ'); }); });";

    private const string OrderReport = "setTimeout(function () { document.getElementById('out').textContent = log.join(','); }, 0);";

    /// <summary>
    /// A frame's microtasks and the page's share one queue, as HTML's one microtask queue per event loop
    /// has it: a job the frame queued between two of the page's runs between them, in the frame's
    /// window context -- and so does a <c>queueMicrotask</c> callback, the page's own included.
    /// </summary>
    [Theory]
    [InlineData("page, frame, page", true,
        "Promise.resolve().then(function () { log.push('P1'); }); w.dispatchEvent(new Event('ping'));" +
        " Promise.resolve().then(function () { log.push('P2'); });",
        "Fsync@about:srcdoc,P1,F1@about:srcdoc,P2")]
    [InlineData("frame, page", true,
        "w.dispatchEvent(new Event('ping')); Promise.resolve().then(function () { log.push('P1'); });",
        "Fsync@about:srcdoc,F1@about:srcdoc,P1")]
    [InlineData("the page's job sees the frame's earlier one", true,
        "w.dispatchEvent(new Event('ping'));" +
        " Promise.resolve().then(function () { log.push('P1 saw ' + (log.join().indexOf('F1') >= 0 ? 'F1' : 'nothing')); });",
        "Fsync@about:srcdoc,F1@about:srcdoc,P1 saw F1")]
    [InlineData("frame queueMicrotask", true,
        "Promise.resolve().then(function () { log.push('P1'); }); w.dispatchEvent(new Event('qping'));" +
        " Promise.resolve().then(function () { log.push('P2'); });",
        "P1,FQ,P2")]
    [InlineData("page queueMicrotask", false,
        "Promise.resolve().then(function () { log.push('P1'); }); queueMicrotask(function () { log.push('Q1'); });" +
        " Promise.resolve().then(function () { log.push('P2'); });",
        "P1,Q1,P2")]
    [InlineData("page only", false,
        "Promise.resolve().then(function () { log.push('P1'); }); Promise.resolve().then(function () { log.push('P2'); });",
        "P1,P2")]
    public void FrameAndPageMicrotasksRunInTheOrderTheyWereQueued(string name, bool withFrame, string script, string expected)
    {
        var page = withFrame ? OrderFramePage : OrderPlainPage;
        var setup = withFrame ? OrderSetup : "var log = [];";
        var rendered = new ScriptEngine().Execute([setup + script + OrderReport], page, "https://example.test/page");

        Assert.True(expected == PageProbe.OutOf(rendered!), $"{name}: expected {expected}, got {PageProbe.OutOf(rendered!)}");
    }
}
