using Broiler.HtmlBridge;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The window a page is handed for a frame of another origin: one it can post to, navigate and walk
/// the frame tree through, and nothing more -- every other member throws a <c>SecurityError</c>, as a
/// browser's cross-origin WindowProxy does (HTML §7.2.3).
/// </summary>
/// <remarks>
/// <para>
/// <b><c>contentWindow</c> was <c>null</c> for such a frame</b>, and <c>window.frames</c> left it out,
/// so a page could not start a conversation with a frame of another origin; it could only answer one,
/// through the <c>source</c> of a message the frame sent first. reCAPTCHA's frames are of another
/// origin on every site but Google's own, and the page that embeds them posts to them.
/// </para>
/// <para>
/// The page is on <c>localhost</c> and the frames on <c>127.0.0.1</c>, another site, served by a
/// loopback server. What a frame sees is reported back by a request it makes to <c>/report</c>.
/// </para>
/// </remarks>
public class CrossOriginWindowTests
{
    private static BrowserNetworkSession NewProfile() =>
        new(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });

    private static ScriptEngine EngineFor(BrowserNetworkSession profile) =>
        new(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));

    private static string Page(string body) =>
        $"<!DOCTYPE html><html><head></head><body><div id=\"out\"></div>{body}</body></html>";

    private static string[] Reports(LoopbackCookieServer server) =>
        [.. server.RequestsFor("/report").Select(r => Uri.UnescapeDataString(r.Path))];

    /// <summary>Runs <paramref name="script"/> on a page at <c>localhost</c>, settles it, and answers what it wrote.</summary>
    private static string Settle(LoopbackCookieServer server, BrowserNetworkSession profile, string body, string script)
    {
        using var session = EngineFor(profile).ExecuteInteractive([script], [], Page(body), server.LocalhostUrl("/page"));
        return PageProbe.OutOf(session!.SettleLoadWindow(), decode: true);
    }

    /// <summary>
    /// A page posts to a frame of another origin through its <c>contentWindow</c>, and the frame's
    /// listener receives the message with the page's origin. <c>contentWindow</c> was <c>null</c>, so
    /// the page's script threw before it posted.
    /// </summary>
    [Fact]
    public void APagePostsToAFrameOfAnotherOriginThroughItsContentWindow()
    {
        using var server = new LoopbackCookieServer();
        var report = server.Url("/report");
        server
            .Map("/frame", new Reply(Body:
                "<html><body><script>window.addEventListener('message', function (e) {" +
                $" fetch('{report}?got=' + e.data + '&origin=' + e.origin, {{ mode: 'no-cors' }}); }});</script></body></html>"))
            .Map("/report", new Reply(ContentType: "text/plain", Body: "x"));
        using var profile = NewProfile();

        var seen = Settle(server, profile, $"<iframe id=\"f\" src=\"{server.Url("/frame")}\"></iframe>",
            "document.getElementById('f').contentWindow.postMessage('hello', '*');" +
            "document.getElementById('out').textContent = 'posted';");

        Assert.Equal("posted", seen);
        Assert.Contains($"/report?got=hello&origin=http://localhost:{server.Port}", Reports(server));
    }

    /// <summary>
    /// The members a cross-origin window answers: itself as <c>window</c>, <c>self</c> and <c>frames</c>;
    /// the page as <c>parent</c> and <c>top</c>; no <c>opener</c>; <c>closed</c>; its frame count; and
    /// the same <c>postMessage</c>, <c>focus</c> and <c>blur</c> each time. <c>then</c> reads as
    /// undefined, so a promise resolves with it, and its prototype is null.
    /// </summary>
    [Fact]
    public void ACrossOriginWindowAnswersItsCrossOriginMembers()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/frame", new Reply(Body: "<html><body><iframe src='about:blank'></iframe><p>frame</p></body></html>"));
        using var profile = NewProfile();

        var seen = Settle(server, profile, $"<iframe id=\"f\" src=\"{server.Url("/frame")}\"></iframe>",
            "var w = document.getElementById('f').contentWindow;" +
            "document.getElementById('out').textContent = [w === w.window && w === w.self && w === w.frames," +
            " w.parent === window, w.top === window, w.opener, w.closed, w.length," +
            " w.postMessage === w.postMessage, typeof w.focus, typeof w.blur, typeof w.close," +
            " w.then === undefined, Object.getPrototypeOf(w), w === document.getElementById('f').contentWindow].join('|');");

        Assert.Equal("true|true|true||false|1|true|function|function|function|true||true", seen);
    }

    /// <summary>
    /// Every other access to a cross-origin window throws a <c>SecurityError</c>: reading its
    /// <c>document</c> or an expando, writing one, asking <c>in</c>, defining or deleting a property,
    /// converting it to a string, and reading anything of its <c>location</c>.
    /// </summary>
    [Theory]
    [InlineData("w.document")]
    [InlineData("w.secret")]
    [InlineData("w.secret = 1")]
    [InlineData("'secret' in w")]
    [InlineData("Object.defineProperty(w, 'secret', { value: 1 })")]
    [InlineData("delete w.postMessage")]
    [InlineData("String(w)")]
    [InlineData("w.location.href")]
    [InlineData("w.location.pathname")]
    public void EveryOtherAccessToACrossOriginWindowThrowsSecurityError(string access)
    {
        using var server = new LoopbackCookieServer();
        server.Map("/frame", new Reply(Body: "<html><body><script>var secret = 'SECRET';</script></body></html>"));
        using var profile = NewProfile();

        var seen = Settle(server, profile, $"<iframe id=\"f\" src=\"{server.Url("/frame")}\"></iframe>",
            "var w = document.getElementById('f').contentWindow;" +
            $"try {{ document.getElementById('out').textContent = 'read ' + ({access}); }}" +
            " catch (e) { document.getElementById('out').textContent = e.name; }");

        Assert.Equal("SecurityError", seen);
    }

    /// <summary>
    /// A message from a frame of another origin names the frame's <c>contentWindow</c> as its
    /// <c>source</c>, so a page can tell which of its frames is talking. Its <c>source</c> was a
    /// stand-in equal to nothing the page held, and <c>contentWindow</c> was <c>null</c>.
    /// </summary>
    [Fact]
    public void AMessagesSourceIsTheSendingFramesContentWindow()
    {
        using var server = new LoopbackCookieServer();
        server
            .Map("/talks", new Reply(Body: "<html><body><script>parent.postMessage('hi', '*');</script></body></html>"))
            .Map("/silent", new Reply(Body: "<html><body>silent</body></html>"));
        using var profile = NewProfile();

        var seen = Settle(server, profile,
            $"<iframe id=\"f\" src=\"{server.Url("/talks")}\"></iframe><iframe id=\"g\" src=\"{server.Url("/silent")}\"></iframe>",
            "var out = document.getElementById('out'); out.textContent = 'no-message';" +
            "window.addEventListener('message', function (e) {" +
            " out.textContent = [e.source === document.getElementById('f').contentWindow," +
            " e.source === document.getElementById('g').contentWindow].join('|'); });" +
            "document.getElementById('f').contentWindow;");

        Assert.Equal("true|false", seen);
    }

    /// <summary>
    /// A cross-origin window's child frames are reachable by index and by name, each as the page may
    /// have it: a child of the frame's origin is cross-origin to the page too, and a child of the
    /// page's origin is the page's to read.
    /// </summary>
    [Fact]
    public void ACrossOriginWindowsChildrenAreReachableAsThePageMayHaveThem()
    {
        using var server = new LoopbackCookieServer();
        server
            .Map("/frame", new Reply(Body:
                $"<html><body><iframe name='theirs' src='{server.Url("/child")}'></iframe>" +
                $"<iframe name='ours' src='{server.LocalhostUrl("/child")}'></iframe></body></html>"))
            .Map("/child", new Reply(Body: "<html><body><p id='in'>CHILD</p></body></html>"));
        using var profile = NewProfile();

        var seen = Settle(server, profile, $"<iframe id=\"f\" src=\"{server.Url("/frame")}\"></iframe>",
            "var w = document.getElementById('f').contentWindow;" +
            "function read(child) { try { return child.document.getElementById('in').textContent; } catch (e) { return e.name; } }" +
            "document.getElementById('out').textContent = [w.length, w[0] === w.theirs, w[1] === w.ours," +
            " read(w.theirs), read(w.ours), w[0].parent === w].join('|');");

        Assert.Equal("2|true|true|SecurityError|CHILD|true", seen);
    }

    /// <summary>
    /// reCAPTCHA's frames on a page of another origin: one frame finds its sibling by name through
    /// <c>parent.frames</c>, and since the two share an origin, it can read the sibling's document --
    /// even though the page between them is of another origin. The sibling was not in
    /// <c>frames</c> by name at all.
    /// </summary>
    [Fact]
    public void AFrameFindsASameOriginSiblingByNameOnAPageOfAnotherOrigin()
    {
        using var server = new LoopbackCookieServer();
        var report = server.Url("/report");
        server
            .Map("/anchor", new Reply(Body: "<html><body><p id='in'>ANCHOR</p></body></html>"))
            .Map("/challenge", new Reply(Body:
                "<html><body><script>" +
                "var anchor = parent.frames[window.name.replace('c-', 'a-')];" +
                $"fetch('{report}?sibling=' + anchor.document.getElementById('in').textContent, {{ mode: 'no-cors' }});" +
                "</script></body></html>"))
            .Map("/report", new Reply(ContentType: "text/plain", Body: "x"));
        using var profile = NewProfile();

        Settle(server, profile,
            $"<iframe name=\"a-1\" src=\"{server.Url("/anchor")}\"></iframe><iframe name=\"c-1\" src=\"{server.Url("/challenge")}\"></iframe>",
            "document.getElementById('out').textContent = 'page';");

        Assert.Contains("/report?sibling=ANCHOR", Reports(server));
    }

    /// <summary>
    /// A page navigates a cross-origin frame through its window's <c>location</c> without reading it:
    /// assigning <c>location</c> or <c>location.href</c>, or calling <c>location.replace</c>, does not
    /// throw, though nothing of the location can be read.
    /// </summary>
    [Fact]
    public void ACrossOriginWindowsLocationCanBeAssignedButNotRead()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/frame", new Reply(Body: "<html><body>frame</body></html>"));
        using var profile = NewProfile();

        var seen = Settle(server, profile, $"<iframe id=\"f\" src=\"{server.Url("/frame")}\"></iframe>",
            "var w = document.getElementById('f').contentWindow; var steps = [];" +
            "function attempt(label, f) { try { f(); steps.push(label); } catch (e) { steps.push(label + ':' + e.name); } }" +
            $"attempt('assign', function () {{ w.location = '{server.Url("/frame")}#a'; }});" +
            $"attempt('href', function () {{ w.location.href = '{server.Url("/frame")}#b'; }});" +
            $"attempt('replace', function () {{ w.location.replace('{server.Url("/frame")}#c'); }});" +
            "attempt('read', function () { return w.location.hash; });" +
            "document.getElementById('out').textContent = steps.join('|');");

        Assert.Equal("assign|href|replace|read:SecurityError", seen);
    }
}
