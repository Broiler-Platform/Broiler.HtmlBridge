using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A frame's own window and document: the listeners its script adds without naming a window, and the
/// events that tell it its document has loaded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every document shares one global object</b>, which is the page's window. A frame's script that
/// called <c>addEventListener</c> or set <c>onload</c> without <c>window.</c> reached that global, so
/// it listened to the page: its <c>load</c> listener ran when the page loaded, or never, for a frame
/// added after that.
/// </para>
/// <para>
/// <b>A frame's document never loaded.</b> It had no <c>readyState</c>, and nothing fired
/// <c>DOMContentLoaded</c>, <c>load</c> or <c>pageshow</c> for it, so a frame's script that waited for
/// any of them waited forever. The order these tests expect is Chromium's, measured: the frame's
/// document goes <c>interactive</c> and gets <c>DOMContentLoaded</c>, goes <c>complete</c>, its window
/// gets <c>load</c> (whose target is the document) and its <c>onload</c>, the page's frame element gets
/// <c>load</c>, and the frame's window gets <c>pageshow</c> last.
/// </para>
/// </remarks>
public class FrameLifecycleTests
{
    private static BrowserNetworkSession NewProfile() =>
        new(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });

    private static ScriptEngine EngineFor(BrowserNetworkSession profile) =>
        new(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));

    private static string Page(string body) =>
        $"<!DOCTYPE html><html><head></head><body><div id=\"out\">waiting</div>{body}</body></html>";

    private static string Html(string script, string body = "") =>
        $"<html><body>{body}<script>{script}</script></body></html>";

    /// <summary>The page's log, which a frame appends to as <c>parent.log(…)</c>.</summary>
    private const string Prelude =
        "var pageOut = document.getElementById('out'); var entries = [];" +
        "function log(entry) { entries.push(entry); pageOut.textContent = entries.join('|'); }";

    private static string Settle(LoopbackCookieServer server, string body, string pageScript = "")
    {
        using var profile = NewProfile();
        using var session = EngineFor(profile).ExecuteInteractive(
            [Prelude + pageScript], [], Page(body), server.LocalhostUrl("/page"));
        return PageProbe.OutOf(session!.SettleLoadWindow(), decode: true);
    }

    /// <summary>
    /// The frame's load events arrive in Chromium's order, each with the frame's document in the state
    /// Chromium shows it in, and the frame element's <c>load</c> between the frame's <c>load</c> and its
    /// <c>pageshow</c>.
    /// </summary>
    [Fact]
    public void AFramesDocumentLoadsInChromiumsOrder()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/frame", new Reply(Body: Html(
            "parent.log('script ' + document.readyState);" +
            "document.addEventListener('readystatechange', function () { parent.log('readystatechange ' + document.readyState); });" +
            "document.addEventListener('DOMContentLoaded', function (e) { parent.log('document DOMContentLoaded ' + document.readyState + ' ' + e.bubbles); });" +
            "addEventListener('DOMContentLoaded', function (e) { parent.log('window DOMContentLoaded ' + e.eventPhase + ' ' + (e.target === document)); });" +
            "addEventListener('load', function (e) { parent.log('window load ' + document.readyState + ' ' + (e.target === document) + ' ' + e.bubbles + ' ' + e.cancelable); });" +
            "addEventListener('pageshow', function (e) { parent.log('window pageshow ' + e.persisted + ' ' + (e.target === document)); });" +
            "onload = function () { parent.log('onload ' + (this === window)); };" +
            "document.addEventListener('load', function () { parent.log('document load'); });")));

        Assert.Equal(
            "script loading" +
            "|readystatechange interactive" +
            "|document DOMContentLoaded interactive true" +
            "|window DOMContentLoaded 3 true" +
            "|readystatechange complete" +
            "|window load complete true false false" +
            "|onload true" +
            "|element load" +
            "|window pageshow false true",
            Settle(server, "<iframe id=\"f\" src=\"/frame\"></iframe>",
                "document.getElementById('f').addEventListener('load', function () { log('element load'); });"));
    }

    /// <summary>
    /// A frame's script that waits for its window's <c>load</c> before it starts is started, once, and
    /// as the frame -- in a frame the page adds after its own <c>load</c> too, which nothing loaded at
    /// all before.
    /// </summary>
    [Fact]
    public void AFrameAddedAfterThePageLoadedStartsItsLoadListener()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/frame", new Reply(Body: Html(
            "var starts = 0;" +
            "addEventListener('load', function () { starts++; parent.log('started ' + starts + ' ' + location.pathname + ' ' + (window !== parent)); });")));

        Assert.Equal(
            "page loaded|started 1 /frame true",
            Settle(server, string.Empty,
                "addEventListener('load', function () {" +
                "  log('page loaded');" +
                "  var frame = document.createElement('iframe'); frame.src = '/frame'; document.body.appendChild(frame);" +
                "});"));
    }

    /// <summary>
    /// A frame's bare <c>addEventListener</c> listens to the frame's window, not the page's: an event
    /// at the page's window does not reach it, and one at the frame's does.
    /// </summary>
    [Fact]
    public void AFramesBareAddEventListenerListensToTheFramesWindow()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/frame", new Reply(Body: Html(
            "addEventListener('ping', function (e) { parent.log('frame heard ' + e.detail + ' ' + (this === window)); });" +
            "this.addEventListener('ping', function (e) { parent.log('frame this heard ' + e.detail); });")));

        Assert.Equal(
            "page heard page|frame heard frame true|frame this heard frame",
            Settle(server, "<iframe id=\"f\" src=\"/frame\"></iframe>",
                "var frameWindow = document.getElementById('f').contentWindow;" +
                "addEventListener('ping', function (e) { log('page heard ' + e.detail); });" +
                "addEventListener('load', function () {" +
                "  window.dispatchEvent(new CustomEvent('ping', { detail: 'page' }));" +
                "  frameWindow.dispatchEvent(new CustomEvent('ping', { detail: 'frame' }));" +
                "});"));
    }

    /// <summary>
    /// A frame's <c>parent.addEventListener</c> and <c>top.dispatchEvent</c> are the page's: the frame
    /// reaches the page through them, not itself.
    /// </summary>
    [Fact]
    public void AFrameListensToThePageThroughParent()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/frame", new Reply(Body: Html(
            "parent.addEventListener('ping', function (e) { parent.log('frame heard the page ' + e.detail); });" +
            "addEventListener('pong', function () { parent.log('frame heard itself'); });" +
            "top.dispatchEvent(new CustomEvent('pong'));")));

        Assert.Equal(
            "page heard pong|frame heard the page ping",
            Settle(server, "<iframe id=\"f\" src=\"/frame\"></iframe>",
                "addEventListener('pong', function () { log('page heard pong'); });" +
                "document.getElementById('f').contentWindow;" +
                "window.dispatchEvent(new CustomEvent('ping', { detail: 'ping' }));"));
    }

    /// <summary>
    /// A frame's bare <c>onmessage</c> is its window's handler: a message to the frame runs it, one to
    /// the page does not, and the page's own <c>onmessage</c> is untouched.
    /// </summary>
    [Fact]
    public void AFramesBareOnHandlerIsTheFramesWindows()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/frame", new Reply(Body: Html(
            "onmessage = function (e) { parent.log('frame got ' + e.data + ' ' + (window.onmessage === onmessage)); };")));

        Assert.Equal(
            "page handler null|frame got to-frame true",
            Settle(server, "<iframe id=\"f\" src=\"/frame\"></iframe>",
                "var frameWindow = document.getElementById('f').contentWindow;" +
                "log('page handler ' + onmessage);" +
                "addEventListener('message', function (e) { if (e.data === 'to-page') log('page got ' + e.data); });" +
                "frameWindow.postMessage('to-frame', '*');"));
    }

    /// <summary>
    /// In a strict-mode script a bare <c>addEventListener</c> is called with no receiver at all, which
    /// a browser takes as the window: the listener hears the window's events.
    /// </summary>
    [Fact]
    public void AStrictBareAddEventListenerListensToTheWindow()
    {
        using var server = new LoopbackCookieServer();

        Assert.Equal(
            "strict heard ping",
            Settle(server, string.Empty,
                "(function () { 'use strict';" +
                "  addEventListener('ping', function () { log('strict heard ping'); });" +
                "  dispatchEvent(new Event('ping'));" +
                "})();"));
    }

    /// <summary>
    /// A frame that navigates loads again: the next document's script sees its own <c>loading</c>
    /// document and gets its own <c>load</c>, with what the first document listened to gone.
    /// </summary>
    [Fact]
    public void ANavigatedFrameLoadsItsNextDocument()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/first", new Reply(Body: Html(
            "addEventListener('load', function () { parent.log('first loaded'); location.href = '/second'; });")));
        server.Map("/second", new Reply(Body: Html(
            "parent.log('second ' + document.readyState);" +
            "addEventListener('load', function () { parent.log('second loaded ' + document.readyState); });")));

        Assert.Equal(
            "first loaded|second loading|second loaded complete",
            Settle(server, "<iframe id=\"f\" src=\"/first\"></iframe>", "document.getElementById('f').contentWindow;"));
    }
}
