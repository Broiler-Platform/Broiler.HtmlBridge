using Broiler.HtmlBridge;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A web page's workers: a script of the page's own origin fetched over the network, what it imports
/// fetched by URL, a <c>data:</c> or <c>blob:</c> script, and the refusals -- another origin's script,
/// a redirect to one, a script that is not JavaScript, and what the page's policy forbids.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only local files were read for a worker</b>, so every worker an <c>http(s)</c> page started failed
/// with an <c>error</c> event. reCAPTCHA's checkbox frame starts one from its own origin and waits for
/// its answer.
/// </para>
/// <para>
/// The page is on <c>localhost</c>, served by a loopback server that also answers on <c>127.0.0.1</c>,
/// another origin. Each page writes what its worker answered, or why it failed, into <c>#out</c>; the
/// settle waits for the worker, which is part of what is tested
/// (<see cref="APageThatGivesUpOnItsWorkerAfterATimeoutHearsItFirst"/>).
/// </para>
/// </remarks>
public class WorkerNetworkTests
{
    private const string Js = "text/javascript";

    private static BrowserNetworkSession NewProfile() =>
        new(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });

    /// <summary>
    /// Starts <c>new Worker(<paramref name="workerUrl"/>)</c> on a page at <c>localhost</c>, posts
    /// <c>hi</c> to it, and answers what the page wrote: the worker's answer, <c>error</c> when it fired
    /// one, or <c>threw</c> and the error's name when the constructor threw.
    /// </summary>
    private static string Run(LoopbackCookieServer server, string workerUrl, string head = "", string setup = "") =>
        RunScript(server,
            "var out = document.getElementById('out');" + setup +
            "try {" +
            $"  var worker = new Worker({workerUrl});" +
            "  worker.onmessage = function (e) { out.textContent = String(e.data); };" +
            "  worker.onerror = function (e) { out.textContent = 'error'; };" +
            "  worker.postMessage('hi');" +
            "} catch (e) { out.textContent = 'threw ' + e.name; }",
            head);

    /// <summary>Runs <paramref name="script"/> on a page at <c>localhost</c>, settles it, and answers its <c>#out</c>.</summary>
    private static string RunScript(LoopbackCookieServer server, string script, string head = "")
    {
        using var profile = NewProfile();
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));
        using var session = engine.ExecuteInteractive(
            [script],
            [],
            $"<html><head>{head}</head><body><div id=\"out\">waiting</div></body></html>",
            server.LocalhostUrl("/page"));

        return PageProbe.OutOf(session!.SettleLoadWindow(), decode: true);
    }

    private const string Echo =
        "onmessage = function (e) { postMessage('echo ' + e.data + ' from ' + self.location.pathname); };";

    /// <summary>
    /// A worker script of the page's origin is fetched, runs, answers the page's message and knows
    /// its own URL. It failed with an <c>error</c> event.
    /// </summary>
    [Fact]
    public void AWorkerScriptOfThePagesOriginIsFetchedAndRuns()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/worker.js", new Reply(ContentType: Js, Body: Echo));

        Assert.Equal("echo hi from /worker.js", Run(server, "'/worker.js'"));
    }

    /// <summary>
    /// A worker imports scripts by URL, relative to its own script: one of its origin, and one of
    /// another origin, which <c>importScripts</c> may load as a <c>&lt;script&gt;</c> may.
    /// </summary>
    [Fact]
    public void AWorkerImportsScriptsByUrl()
    {
        using var server = new LoopbackCookieServer();
        server
            .Map("/w/worker.js", new Reply(ContentType: Js, Body:
                $"importScripts('lib.js', '{server.Url("/other.js")}');" +
                "onmessage = function (e) { postMessage(lib(e.data) + ' ' + other()); };"))
            .Map("/w/lib.js", new Reply(ContentType: Js, Body: "function lib(x) { return 'lib ' + x; }"))
            .Map("/other.js", new Reply(ContentType: Js, Body: "function other() { return 'other'; }"));

        Assert.Equal("lib hi other", Run(server, "'/w/worker.js'"));
    }

    /// <summary>
    /// Another origin's worker script is a <c>SecurityError</c> from the constructor, and is never
    /// requested.
    /// </summary>
    [Fact]
    public void AnotherOriginsWorkerScriptIsASecurityError()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/worker.js", new Reply(ContentType: Js, Body: Echo));

        Assert.Equal("threw SecurityError", Run(server, $"'{server.Url("/worker.js")}'"));
        Assert.Empty(server.RequestsFor("/worker.js"));
    }

    /// <summary>
    /// A worker script that redirects to another origin fails with an <c>error</c> event, without the
    /// other origin's script ever being requested.
    /// </summary>
    [Fact]
    public void AWorkerScriptRedirectedToAnotherOriginFails()
    {
        using var server = new LoopbackCookieServer();
        server
            .Map("/hop", new Reply(302, Location: server.Url("/worker.js")))
            .Map("/worker.js", new Reply(ContentType: Js, Body: Echo));

        Assert.Equal("error", Run(server, "'/hop'"));
        Assert.Empty(server.RequestsFor("/worker.js"));
    }

    /// <summary>A worker script served as anything but JavaScript fails with an <c>error</c> event, as do one that is not there and one that fails to run.</summary>
    [Theory]
    [InlineData("text/html", Echo)]
    [InlineData("application/json", Echo)]
    [InlineData(Js, "this is not ( JavaScript")]
    public void AWorkerScriptThatIsNotRunnableJavaScriptFails(string contentType, string body)
    {
        using var server = new LoopbackCookieServer();
        server.Map("/worker.js", new Reply(ContentType: contentType, Body: body));

        Assert.Equal("error", Run(server, "'/worker.js'"));
        Assert.Equal("error", Run(server, "'/missing.js'"));
    }

    /// <summary>A worker's script may be carried in a <c>data:</c> URL, or a <c>blob:</c> URL the page made.</summary>
    [Fact]
    public void AWorkerRunsFromADataOrABlobUrl()
    {
        using var server = new LoopbackCookieServer();

        Assert.Equal(
            "data hi",
            Run(server, "'data:text/javascript,' + encodeURIComponent(\"onmessage = function (e) { postMessage('data ' + e.data); };\")"));
        Assert.Equal(
            "blob hi",
            Run(server, "url",
                setup: "var url = URL.createObjectURL(new Blob([\"onmessage = function (e) { postMessage('blob ' + e.data); };\"], { type: 'text/javascript' }));"));
    }

    /// <summary>
    /// A worker's global has what a worker script reaches for without a document: <c>performance</c>,
    /// <c>atob</c>/<c>btoa</c>, <c>TextEncoder</c>, <c>URL</c>, <c>crypto.getRandomValues</c>, a
    /// <c>MessageChannel</c> between its own ports, and its own <c>location</c>. reCAPTCHA's worker,
    /// once it could be loaded, threw on the first three of them in turn.
    /// </summary>
    [Fact]
    public void AWorkersGlobalHasWhatAWorkerScriptReachesFor()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/worker.js", new Reply(ContentType: Js, Body:
            "onmessage = function () {" +
            " var channel = new MessageChannel();" +
            " channel.port1.onmessage = function (e) {" +
            "  postMessage([typeof performance.now(), btoa('hi'), new TextEncoder().encode('\\u00e9').length," +
            "   new URL('a/b?x=1', 'https://h.test/p/').href, crypto.getRandomValues(new Uint8Array(2)).length," +
            "   e.data, self.location.origin === location.origin].join('|'));" +
            " };" +
            " channel.port2.postMessage('through the channel');" +
            "};"));

        Assert.Equal(
            "number|aGk=|2|https://h.test/p/a/b?x=1|2|through the channel|true",
            Run(server, "'/worker.js'"));
    }

    /// <summary>
    /// The page's Content-Security-Policy decides where its workers may come from: <c>worker-src</c>,
    /// falling back to <c>script-src</c>. A refused URL is a <c>SecurityError</c>, and is not requested.
    /// </summary>
    [Theory]
    [InlineData("worker-src 'none'", "threw SecurityError")]
    [InlineData("worker-src 'self'", "echo hi from /worker.js")]
    [InlineData("script-src 'unsafe-inline' https://example.test", "threw SecurityError")]
    [InlineData("script-src 'self' 'unsafe-inline'", "echo hi from /worker.js")]
    [InlineData("script-src 'unsafe-inline'; worker-src 'self'", "echo hi from /worker.js")]
    public void ThePagesPolicyDecidesWhereItsWorkersComeFrom(string policy, string expected)
    {
        using var server = new LoopbackCookieServer();
        server.Map("/worker.js", new Reply(ContentType: Js, Body: Echo));

        Assert.Equal(expected, Run(server, "'/worker.js'", head: CspFixture.Meta(policy)));
        if (expected.StartsWith("threw", StringComparison.Ordinal))
            Assert.Empty(server.RequestsFor("/worker.js"));
    }

    /// <summary>
    /// A page that posts to its worker and gives up on it after a timeout hears the worker's answer
    /// first, as it does in a browser: the page's clock does not run on to the timeout while the
    /// worker is still working, here fetching its script and then answering. reCAPTCHA's frame waits
    /// for its worker this way, five seconds at most.
    /// </summary>
    [Fact]
    public void APageThatGivesUpOnItsWorkerAfterATimeoutHearsItFirst()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/worker.js", new Reply(ContentType: Js, Body: Echo));

        Assert.Equal(
            "answered: echo hi from /worker.js",
            RunScript(server,
                "var out = document.getElementById('out'); var settled = false;" +
                "var worker = new Worker('/worker.js');" +
                "worker.onmessage = function (e) { if (!settled) { settled = true; out.textContent = 'answered: ' + e.data; } };" +
                "setTimeout(function () { if (!settled) { settled = true; out.textContent = 'gave up'; } }, 5000);" +
                "worker.postMessage('hi');"));
    }

    /// <summary>
    /// The page waits for its worker as long as the worker takes here to do what a browser does at once:
    /// longer than five seconds when it has a large script to compile first, as reCAPTCHA's worker does.
    /// So a page that gives up on it after five seconds still hears it first, whether its load window is
    /// settled in one go or stepped, as a window steps it. Here the worker's start takes six seconds; the
    /// page used to stop waiting after five and give up.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APageWaitsForAWorkerSlowerThanItsTimeout(bool settle)
    {
        const string worker =
            "var until = Date.now() + 6000; while (Date.now() < until) { }" +
            "onmessage = function (e) { postMessage('answered ' + e.data); };";
        using var session = new ScriptEngine().ExecuteInteractive(
            ["var out = document.getElementById('out'), settled = false;" +
             $"var worker = new Worker('data:text/javascript,' + encodeURIComponent({System.Text.Json.JsonSerializer.Serialize(worker)}));" +
             "worker.onmessage = function (e) { if (!settled) { settled = true; out.textContent = e.data; } };" +
             "setTimeout(function () { if (!settled) { settled = true; out.textContent = 'gave up'; } }, 5000);" +
             "worker.postMessage('hi');"],
            [],
            "<html><head></head><body><div id=\"out\">waiting</div></body></html>",
            "https://example.test/page");
        Assert.NotNull(session);

        string html;
        if (settle)
        {
            html = session!.SettleLoadWindow();
        }
        else
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (session!.HasWorkDueInLoadWindow && clock.Elapsed < TimeSpan.FromMinutes(1))
            {
                if (session.Step() is null)
                    Thread.Sleep(10);
            }

            html = session.CurrentHtml();
        }

        Assert.Equal("answered hi", PageProbe.OutOf(html, decode: true));
    }
}
