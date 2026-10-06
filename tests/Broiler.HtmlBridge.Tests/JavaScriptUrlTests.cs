using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A navigation to a <c>javascript:</c> URL runs its script, as Chromium runs it: in a later task, in the
/// document it would have navigated, under that document's Content-Security-Policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Such a link did nothing</b> -- or handed the host a navigation to the <c>javascript:</c> URL, which
/// took the page away for a link meant only to run code.
/// </para>
/// <para>
/// Measured in Chromium: <c>a.click()</c> on a <c>javascript:</c> link and <c>location.href =
/// "javascript:…"</c> both run the script after the calling script, its microtasks and a
/// <c>setTimeout(…, 0)</c> queued before them. Only a script of the document's own origin runs one there:
/// another origin's <c>location</c> navigation throws a <c>SecurityError</c>, and its link runs nothing.
/// </para>
/// </remarks>
public class JavaScriptUrlTests
{
    private const string PageUrl = "https://example.test/js-url";

    private const string Recorder =
        "var out = document.getElementById('out'), log = [];" +
        "function show() { out.textContent = log.join('|'); }" +
        "function note(entry) { log.push(entry); show(); }";

    private static InteractiveSession Start(string script, string head = "", string body = "")
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions()));
        var session = engine.ExecuteInteractive(
            [Recorder + script + ";show();"], [], $"<html><head>{head}</head><body>{body}<div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);
        return session!;
    }

    private static (string Out, NavigationRequest? Pending) Settle(string script, string head = "", string body = "")
    {
        using var session = Start(script, head, body);
        var html = session.SettleLoadWindow();
        return (PageProbe.OutOf(html, decode: true), session.TakePendingNavigation());
    }

    /// <summary>
    /// A link's <c>javascript:</c> URL and <c>location.href</c>'s each run as a task, after the script, its
    /// microtasks and a timer queued first, percent-decoded; nothing navigates.
    /// </summary>
    [Fact]
    public void AJavaScriptUrlRunsAsATask()
    {
        var (log, pending) = Settle(
            "setTimeout(function () { note('timeout'); }, 0);" +
            "document.getElementById('js').click(); log.push('after click');" +
            "Promise.resolve().then(function () { note('microtask'); });" +
            "location.href = \"javascript:note('location%20ran')\"; log.push('after href')",
            body: "<a id=\"js\" href=\"javascript:note('link ran ' + document.readyState)\">js</a>");

        Assert.Equal("after click|after href|microtask|timeout|link ran complete|location ran", log);
        Assert.Null(pending);
    }

    /// <summary>A key's activation of a focused <c>javascript:</c> link runs its script too.</summary>
    [Fact]
    public void EnterOnAJavaScriptLinkRunsIt()
    {
        using var session = Start("document.getElementById('js').focus();", body: "<a id=\"js\" href=\"javascript:note('enter ran')\">js</a>");
        session.SettleLoadWindow();

        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Down, "Enter", "Enter") { KeyCode = 13 });
        Assert.Equal("enter ran", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
        Assert.Null(session.TakePendingNavigation());
    }

    /// <summary>The host's link to a <c>javascript:</c> URL runs it in the page.</summary>
    [Fact]
    public void TheHostsJavaScriptUrlRunsInThePage()
    {
        using var session = Start(string.Empty);
        session.SettleLoadWindow();

        Assert.True(session.RunJavaScriptUrl("javascript:note('host ran')"));
        Assert.False(session.RunJavaScriptUrl("https://example.test/other"));
        Assert.Equal("host ran", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// A page runs a <c>javascript:</c> URL in a frame of its origin, and such a frame one in the page -- its
    /// <c>top.location</c>'s, and its link's that targets the page; a frame of another origin's
    /// <c>location</c> throws the <c>SecurityError</c> Chromium throws, and runs nothing.
    /// The page is at <c>localhost</c>, the frame of another origin at <c>127.0.0.1</c>.
    /// </summary>
    [Fact]
    public void OnlyAScriptOfTheDocumentsOriginRunsOneThere()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/same", new Reply(Body:
            "<html><body><a id='link' target='_top' href=\"javascript:void (document.getElementById('out').textContent += '|same-link-to-page')\">link</a><script>" +
            "top.location.href = \"javascript:void (document.getElementById('out').textContent += '|same-to-page')\";" +
            "document.getElementById('link').click();</script></body></html>"));
        server.Map("/other", new Reply(Body: "<html><body><p>other</p></body></html>"));

        using var profile = new BrowserNetworkSession(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });
        using var session = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }))
            .ExecuteInteractive(
                ["var out = document.getElementById('out');" +
                 "addEventListener('message', function (e) { out.textContent += '|' + e.data; });" +
                 "document.getElementById('same').contentWindow.location.href = \"javascript:parent.postMessage('page-to-same', '*')\";" +
                 "var other = document.getElementById('other').contentWindow;" +
                 "try { other.location.href = \"javascript:parent.postMessage('page-to-other', '*')\"; } catch (e) { out.textContent += '|href ' + e.name + ': ' + e.message; }" +
                 "try { other.location.replace(\"javascript:parent.postMessage('page-to-other', '*')\"); } catch (e) { out.textContent += '|replace ' + e.name; }" +
                 "try { other.location = \"javascript:parent.postMessage('page-to-other', '*')\"; } catch (e) { out.textContent += '|location ' + e.name; }"],
                [],
                "<!DOCTYPE html><html><body><div id=\"out\">out</div>" +
                $"<iframe id=\"same\" src=\"{server.LocalhostUrl("/same")}\"></iframe><iframe id=\"other\" src=\"{server.Url("/other")}\"></iframe></body></html>",
                server.LocalhostUrl("/page"));

        var log = PageProbe.OutOf(session!.SettleLoadWindow(), decode: true).Split('|');
        Assert.Equal(
            "href SecurityError: Failed to set a named property 'href' on 'Location': The current window does not have " +
            "permission to navigate the target frame to 'javascript:parent.postMessage('page-to-other', '*')'.",
            Assert.Single(log, entry => entry.StartsWith("href ", StringComparison.Ordinal)));
        Assert.Contains("replace SecurityError", log);
        Assert.Contains("location SecurityError", log);
        Assert.Contains("same-to-page", log);
        Assert.Contains("same-link-to-page", log);
        Assert.Contains("page-to-same", log);
        Assert.DoesNotContain("page-to-other", log);
        Assert.Null(session.TakePendingNavigation());
    }

    /// <summary>
    /// A frame of another origin the user has just activated may navigate the page, but not to a
    /// <c>javascript:</c> URL: its <c>top.location</c> throws a <c>SecurityError</c>, a link of its targeting
    /// the page runs nothing, and neither runs anything in the page.
    /// </summary>
    [Fact]
    public void AFrameOfAnotherOriginRunsNoneInThePage()
    {
        const string IntoPage = "javascript:document.getElementById('out').textContent = 'ran in the page'";
        using var server = new LoopbackCookieServer();
        server.Map("/other", new Reply(Body:
            $"<html><body><div id='press'>press</div><a id='link' target='_top' href=\"{IntoPage}\">link</a><script>" +
            "document.getElementById('press').addEventListener('click', function () {" +
            $"  try {{ top.location.replace(\"{IntoPage}\"); parent.postMessage('replaced', '*'); }} catch (e) {{ parent.postMessage('replace ' + e.name, '*'); }}" +
            "  document.getElementById('link').click(); parent.postMessage('link clicked', '*'); });" +
            "</script></body></html>"));

        using var profile = new BrowserNetworkSession(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });
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
            ["var out = document.getElementById('out'), log = [];" +
             "addEventListener('message', function (e) { log.push(e.data); out.textContent = log.join('|'); });" +
             "document.getElementById('f').contentWindow;"],
            [],
            $"<!DOCTYPE html><html><body><div id=\"out\">waiting</div><iframe id=\"f\" src=\"{server.Url("/other")}\"></iframe></body></html>",
            server.LocalhostUrl("/page"));
        session!.SettleLoadWindow();

        session.DispatchPointer(new PointerInput(PointerInputKind.Down, 20, 60) { Buttons = 1 });
        session.DispatchPointer(new PointerInput(PointerInputKind.Up, 20, 60));

        Assert.Equal("replace SecurityError|link clicked", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
        Assert.Null(session.TakePendingNavigation());
    }

    /// <summary>
    /// What a <c>javascript:</c> URL's script answers, when it is a string, is a document: a frame's replaces
    /// the frame's at the URL it shows, its scripts running, and the page's is handed to the host to show at
    /// the page's URL, its entry replaced (measured in Chromium). Anything else replaces nothing.
    /// </summary>
    [Fact]
    public void AStringResultIsTheDocument()
    {
        using var session = Start(
            "var f = document.getElementById('f'); f.contentWindow;" +
            "f.addEventListener('load', function () { var p = f.contentDocument.querySelector('p');" +
            "  if (p && p.textContent === 'replaced') note(p.textContent + ' at ' + f.contentWindow.location.href); });" +
            "f.contentWindow.location.href = \"javascript:'<p>replaced</p><script>parent.note(\\\"script ran\\\")</script>'\";" +
            "location.href = \"javascript:'<h1>page</h1>'\"; location.href = 'javascript:void 0'; location.href = 'javascript:42';",
            body: "<iframe id=\"f\" srcdoc=\"&lt;p&gt;first&lt;/p&gt;\"></iframe>");

        Assert.Equal("script ran|replaced at about:srcdoc", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
        var pending = session.TakePendingNavigation();
        Assert.Equal(NavigationKind.Replace, pending?.Kind);
        Assert.Equal(PageUrl, pending!.Url);
        Assert.Equal("<h1>page</h1>", pending.Document);
    }

    /// <summary>A Content-Security-Policy that does not allow inline script refuses a <c>javascript:</c> URL.</summary>
    [Fact]
    public void APolicyWithoutUnsafeInlineRefusesIt()
    {
        var (log, pending) = Settle(
            "location.href = \"javascript:note('ran')\"; setTimeout(function () { note('done'); }, 10)",
            head: "<meta http-equiv=\"Content-Security-Policy\" content=\"script-src 'self'\">");

        Assert.Equal("done", log);
        Assert.Null(pending);
    }
}
