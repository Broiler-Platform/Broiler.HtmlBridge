using System.Net;
using Broiler.HtmlBridge;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Which Web Storage areas the documents of a page use: the page's own, an area of a frame's own storage
/// key, or none (DomBridge/WebStorage.cs). Every expectation here was measured in Chromium first, with a
/// page on one site and frames from another.
/// </summary>
/// <remarks>
/// <para>
/// Every document shares one global object here, and every frame used to share the page's two areas
/// with it: a frame of another site read the page's items and the page read the frame's. reCAPTCHA's
/// anchor frame kept its keys in the area of whichever page embedded it.
/// </para>
/// <para>
/// The page is on <c>localhost</c> and the other site on <c>127.0.0.1</c>, both served by one
/// <see cref="LoopbackCookieServer"/>. A frame's script reports what it saw to <c>/report</c>, one
/// request per observation, and the page writes what it sees to <c>#out</c>.
/// </para>
/// </remarks>
public class FrameStorageTests
{
    private static BrowserNetworkSession NewProfile() =>
        new(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });

    private static ScriptEngine EngineFor(BrowserNetworkSession profile) =>
        new(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));

    // `keys(area)` lists an area's keys, sorted, or 'none'; `seen(name, w)` is the keys of the area that
    // `w` (or the script's own window) answers as `name`, or the error reading it threw.
    private const string Keys =
        "function keys(area) { var k = []; for (var i = 0; i < area.length; i++) k.push(area.key(i)); return k.sort().join('+') || 'none'; }" +
        "function seen(name, w) { try { return keys((w || window)[name]); } catch (e) { return e.name + ': ' + e.message; } }" +
        "function both(w) { return seen('localStorage', w) + '|' + seen('sessionStorage', w); }";

    /// <summary>What a frame's script needs to report: <c>report(label, value)</c> as well as the helpers above.</summary>
    private static string Reporter(LoopbackCookieServer server) =>
        Keys + "function report(label, value) {" +
        $" fetch('{server.Url("/report")}?' + label + '=' + encodeURIComponent(value), {{ mode: 'no-cors' }}); }}";

    private static string FrameDocument(LoopbackCookieServer server, string script) =>
        $"<!DOCTYPE html><html><body><script>{Reporter(server)}{script}</script></body></html>";

    /// <summary>
    /// Runs the page's <paramref name="script"/> on a page at <c>localhost</c> holding
    /// <paramref name="body"/>, settles it -- its frames' timers and requests too -- and answers what the
    /// page wrote to <c>#out</c>.
    /// </summary>
    private static string Run(LoopbackCookieServer server, BrowserNetworkSession profile, string body, string script)
    {
        using var session = EngineFor(profile).ExecuteInteractive(
            [Keys + script], [],
            $"<!DOCTYPE html><html><head></head><body><div id=\"out\"></div>{body}</body></html>",
            server.LocalhostUrl("/page"));
        return PageProbe.OutOf(session!.SettleLoadWindow(), decode: true);
    }

    /// <summary>Every <c>label=value</c> the frames reported, sorted.</summary>
    private static string[] Reports(LoopbackCookieServer server) =>
        [.. server.RequestsFor("/report").Select(r => Uri.UnescapeDataString(r.Path.Split('?', 2)[1])).Order(StringComparer.Ordinal)];

    // Writes the page's own item into both areas, then has `#out` show both areas once everything has run.
    private const string PageWritesThenReports =
        "localStorage.setItem('page', '1'); sessionStorage.setItem('page', '1');";

    private const string ReportPageLater =
        "setTimeout(function () { document.getElementById('out').textContent = both(); }, 50);";

    [Fact]
    public void AFrameOfAnotherSiteHasAreasOfItsOwn()
    {
        // Nothing of the page's is there, what the frame writes stays there, and a timer the frame set
        // still reads the frame's areas. A bare name and `window.` reach the same area.
        using var server = new LoopbackCookieServer();
        server.Map("/frame", new Reply(Body: FrameDocument(server,
            "report('before', both());" +
            "localStorage.setItem('frame', '1'); sessionStorage.setItem('frame', '1');" +
            "report('after', both());" +
            "report('bare', (localStorage === window.localStorage) + '/' + (sessionStorage === window.sessionStorage));" +
            "setTimeout(function () { report('timer', keys(localStorage) + '|' + keys(sessionStorage)); }, 0);")));
        using var profile = NewProfile();

        var page = Run(server, profile, $"<iframe id=\"f\" src=\"{server.Url("/frame")}\"></iframe>",
            PageWritesThenReports + "var touch = document.getElementById('f').contentWindow;" + ReportPageLater);

        Assert.Equal("page|page", page);
        Assert.Equal(["after=frame|frame", "bare=true/true", "before=none|none", "timer=frame|frame"], Reports(server));
    }

    [Fact]
    public void FramesOfOneForeignOriginShareTheirAreas()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/frame", new Reply(Body: FrameDocument(server,
            "var name = location.search.slice(1);" +
            "localStorage.setItem(name, '1'); sessionStorage.setItem(name, '1');" +
            "report(name, both());")));
        using var profile = NewProfile();

        var page = Run(server, profile,
            $"<iframe id=\"a\" src=\"{server.Url("/frame?a")}\"></iframe><iframe id=\"b\" src=\"{server.Url("/frame?b")}\"></iframe>",
            PageWritesThenReports +
            "var touch = [document.getElementById('a').contentWindow, document.getElementById('b').contentWindow];" +
            ReportPageLater);

        Assert.Equal("page|page", page);
        Assert.Equal(["a=a|a", "b=a+b|a+b"], Reports(server));
    }

    [Fact]
    public void AFrameOfThePagesOriginSharesThePagesAreas()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/frame", new Reply(Body: FrameDocument(server,
            "report('before', both());" +
            "localStorage.setItem('frame', '1'); sessionStorage.setItem('frame', '1');")));
        using var profile = NewProfile();

        var page = Run(server, profile, $"<iframe id=\"f\" src=\"{server.LocalhostUrl("/frame")}\"></iframe>",
            PageWritesThenReports + "var touch = document.getElementById('f').contentWindow;" + ReportPageLater);

        Assert.Equal("frame+page|frame+page", page);
        Assert.Equal(["before=page|page"], Reports(server));
    }

    /// <summary>
    /// A frame that inherited the page's origin -- a srcdoc frame, an about:blank one -- and a sandboxed
    /// frame allowed its origin share the page's areas, and so does a frame the page reaches through
    /// <c>frames</c>.
    /// </summary>
    [Theory]
    [InlineData("srcdoc")]
    [InlineData("sandboxed with allow-same-origin")]
    public void AFrameWithThePagesOriginSharesThePagesAreas(string kind)
    {
        using var server = new LoopbackCookieServer();
        server.Map("/frame", new Reply(Body: FrameDocument(server, "report('frame', both()); localStorage.setItem('frame', '1');")));
        using var profile = NewProfile();

        var frame = kind == "srcdoc"
            ? $"<iframe id=\"f\" srcdoc=\"{WebUtility.HtmlEncode(FrameDocument(server, "report('frame', both()); localStorage.setItem('frame', '1');"))}\"></iframe>"
            : $"<iframe id=\"f\" sandbox=\"allow-scripts allow-same-origin\" src=\"{server.LocalhostUrl("/frame")}\"></iframe>";

        var page = Run(server, profile, frame,
            PageWritesThenReports + "var touch = document.getElementById('f').contentWindow;" + ReportPageLater);

        Assert.Equal("frame+page|page", page);
        Assert.Equal(["frame=page|page"], Reports(server));
    }

    [Fact]
    public void AnAboutBlankFrameIsWrittenThroughToThePagesAreas()
    {
        using var server = new LoopbackCookieServer();
        using var profile = NewProfile();

        var page = Run(server, profile, "<iframe id=\"f\"></iframe>",
            "frames[0].localStorage.setItem('blank', '1'); frames[0].sessionStorage.setItem('blank', '1');" +
            "document.getElementById('out').textContent = both();");

        Assert.Equal("blank|blank", page);
    }

    /// <summary>
    /// A document with an opaque origin reads either area as a <c>SecurityError</c>, with Chromium's
    /// message for why its origin is opaque: it is sandboxed (or inside a sandboxed frame), it is a
    /// <c>data:</c> document, or it inherited a <c>data:</c> document's origin.
    /// </summary>
    [Theory]
    [InlineData("sandboxed", "The document is sandboxed and lacks the 'allow-same-origin' flag.")]
    [InlineData("data", "Storage is disabled inside 'data:' URLs.")]
    [InlineData("inside data", "Access is denied for this document.")]
    [InlineData("inside sandboxed", "The document is sandboxed and lacks the 'allow-same-origin' flag.")]
    public void ADocumentWithAnOpaqueOriginHasNoStorage(string kind, string reason)
    {
        using var server = new LoopbackCookieServer();
        var probe = FrameDocument(server, "report('frame', both());");
        server.Map("/frame", new Reply(Body: probe));
        using var profile = NewProfile();

        // A document whose only content is a srcdoc frame holding the probe, which it builds.
        var holder = $"<!DOCTYPE html><html><body><iframe id=\"c\" srcdoc=\"{WebUtility.HtmlEncode(probe)}\"></iframe>" +
                     "<script>var touch = document.getElementById('c').contentWindow;</script></body></html>";
        var frame = kind switch
        {
            "sandboxed" => $"<iframe id=\"f\" sandbox=\"allow-scripts\" src=\"{server.LocalhostUrl("/frame")}\"></iframe>",
            "data" => $"<iframe id=\"f\" src=\"data:text/html,{Uri.EscapeDataString(probe)}\"></iframe>",
            "inside data" => $"<iframe id=\"f\" src=\"data:text/html,{Uri.EscapeDataString(holder)}\"></iframe>",
            _ => $"<iframe id=\"f\" sandbox=\"allow-scripts\" srcdoc=\"{WebUtility.HtmlEncode(holder)}\"></iframe>",
        };

        var page = Run(server, profile, frame,
            PageWritesThenReports + "var touch = document.getElementById('f').contentWindow;" + ReportPageLater);

        Assert.Equal("page|page", page);
        Assert.Equal(
            [$"frame=SecurityError: Failed to read the 'localStorage' property from 'Window': {reason}" +
             $"|SecurityError: Failed to read the 'sessionStorage' property from 'Window': {reason}"],
            Reports(server));
    }

    /// <summary>
    /// A frame of the page's own origin inside a frame of another site is partitioned as that frame's
    /// third party: it sees neither the page's items nor the other site's, and what it writes the page
    /// does not see. Its <c>top</c> is still the page's window, and the page's areas are what that has.
    /// </summary>
    [Fact]
    public void AFrameOfThePagesOriginInsideAnotherSiteIsPartitioned()
    {
        using var server = new LoopbackCookieServer();
        server
            .Map("/outer", new Reply(Body:
                $"<!DOCTYPE html><html><body><iframe id=\"c\" src=\"{server.LocalhostUrl("/inner")}\"></iframe><script>" +
                "localStorage.setItem('outer', '1'); sessionStorage.setItem('outer', '1');" +
                // The child is of another origin than this frame, so only a member that needs its
                // window -- postMessage -- loads it.
                "document.getElementById('c').contentWindow.postMessage('load', '*');</script></body></html>"))
            .Map("/inner", new Reply(Body: FrameDocument(server,
                "report('inner', both()); report('top', both(top));" +
                "localStorage.setItem('inner', '1'); sessionStorage.setItem('inner', '1');")));
        using var profile = NewProfile();

        var page = Run(server, profile, $"<iframe id=\"f\" src=\"{server.Url("/outer")}\"></iframe>",
            PageWritesThenReports + "var touch = document.getElementById('f').contentWindow;" + ReportPageLater);

        Assert.Equal("page|page", page);
        Assert.Equal(["inner=none|none", "top=page|page"], Reports(server));
    }

    [Fact]
    public void ThePageCannotReachTheStorageOfAFrameOfAnotherOrigin()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/frame", new Reply(Body: "<!DOCTYPE html><html><body></body></html>"));
        using var profile = NewProfile();

        var page = Run(server, profile,
            $"<iframe src=\"{server.Url("/frame")}\"></iframe><iframe src=\"{server.LocalhostUrl("/frame")}\"></iframe>",
            PageWritesThenReports +
            "var other; try { other = keys(frames[0].localStorage); } catch (e) { other = e.name; }" +
            "document.getElementById('out').textContent = other + '|' + both(frames[1]);");

        Assert.Equal("SecurityError|page|page", page);
    }

    [Fact]
    public void TheGlobalsAreasAreReadOnlyAccessors()
    {
        // As in Chromium: a getter and no setter, enumerable and configurable, and an assignment in sloppy
        // code replaces nothing.
        using var server = new LoopbackCookieServer();
        using var profile = NewProfile();

        var page = Run(server, profile, string.Empty,
            "function shape(name) { var d = Object.getOwnPropertyDescriptor(window, name);" +
            " return [typeof d.get, typeof d.set, 'value' in d, d.enumerable, d.configurable].join(','); }" +
            "var kept = (function () { localStorage = 5; sessionStorage = 6; return typeof localStorage.getItem + ',' + typeof sessionStorage.getItem; })();" +
            "document.getElementById('out').textContent = shape('localStorage') + '|' + shape('sessionStorage') + '|' + kept;");

        Assert.Equal("function,undefined,false,true,true|function,undefined,false,true,true|function,function", page);
    }
}
