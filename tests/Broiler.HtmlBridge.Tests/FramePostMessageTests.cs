using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>postMessage</c> between a page and its frame, in both directions: a message the page posts to
/// <c>frame.contentWindow</c> reaches the frame's <c>message</c> listener, a <c>MessagePort</c> it
/// transfers arrives in <c>event.ports</c> and carries the frame's answer back, and a message the
/// frame posts to <c>parent</c> names the frame's <c>contentWindow</c> as its <c>source</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>None of it worked.</b> However a page reaches its frame -- <c>contentWindow</c>,
/// <c>window.frames[0]</c>, even <c>contentDocument</c>, which judges the frame's origin by loading it
/// through its window -- the frame's window is built first. Building it builds the frame's document,
/// which runs the frame's scripts, which ask for the frame's window: that re-entrant call built one,
/// the scripts registered their listeners on it and posted from it, and the outer call then replaced
/// it in the cache with a second. The page posted to a window the frame had never seen, and a message
/// from the frame carried a <c>source</c> the page could not match to any frame. reCAPTCHA's checkbox
/// frame waits five seconds for the page to hand it a <c>MessagePort</c> this way, then gives up, and
/// the page reports "reCAPTCHA Timeout".
/// </para>
/// <para>
/// The frames are <c>srcdoc</c> documents, so no network is involved. Each listener writes into the
/// page's <c>#out</c> through <c>pageOut</c>, a global the page's script sets before it touches the
/// frame: every document shares one realm, so the frame's script reads it by name. The placeholder the
/// page writes first tells "never ran" apart from a wrong answer. The frame's script contains no
/// double quote, so the double-quoted <c>srcdoc</c> attribute carries it unescaped.
/// </para>
/// </remarks>
public class FramePostMessageTests
{
    private const string PageUrl = "https://example.test/frame-messaging";

    private static string PageWithFrame(string frameScript) =>
        "<html><body>" +
        "<iframe id=\"f\" srcdoc=\"<html><body><script>" + frameScript + "</script></body></html>\"></iframe>" +
        "<div id=\"out\"></div>" +
        "</body></html>";

    private static string Run(string frameScript, string pageScript) =>
        PageProbe.OutOf(PageProbe.Render([pageScript], PageWithFrame(frameScript), PageUrl));

    /// <summary>
    /// A message the page posts to its frame reaches the frame's listener, whichever way the page
    /// reached the frame first, with the page as its <c>source</c> and the page's origin. The
    /// listener never ran, whichever way it was.
    /// </summary>
    [Theory]
    [InlineData("document.getElementById('f').contentWindow")]
    [InlineData("window.frames[0]")]
    [InlineData("document.getElementById('f').contentDocument")]
    public void AMessageThePagePostsReachesTheFramesListener(string firstReach)
    {
        Assert.Equal(
            "got=hello sourceIsParent=true origin=https://example.test",
            Run(
                "window.addEventListener('message', function (e) {" +
                "  pageOut.textContent = 'got=' + e.data + ' sourceIsParent=' + (e.source === parent) +" +
                "    ' origin=' + e.origin;" +
                "});",
                "var pageOut = document.getElementById('out');" +
                "pageOut.textContent = 'listener-never-ran';" +
                firstReach + ";" +
                "document.getElementById('f').contentWindow.postMessage('hello', '*');"));
    }

    /// <summary>
    /// A port the page transfers with its message arrives in the frame's <c>event.ports</c>, and the
    /// frame's answer through it reaches the page's end of the channel. This is the handshake
    /// reCAPTCHA's frame waits for. The page heard nothing back.
    /// </summary>
    [Fact]
    public void APortThePageTransfersReachesTheFrameAndCarriesItsAnswerBack()
    {
        Assert.Equal(
            "page got pong, ports=1",
            Run(
                "window.addEventListener('message', function (e) {" +
                "  e.ports[0].postMessage('pong, ports=' + e.ports.length);" +
                "});",
                "var pageOut = document.getElementById('out');" +
                "pageOut.textContent = 'never-answered';" +
                "var channel = new MessageChannel();" +
                "channel.port1.onmessage = function (e) { pageOut.textContent = 'page got ' + e.data; };" +
                "document.getElementById('f').contentWindow.postMessage('take this port', '*', [channel.port2]);"));
    }

    /// <summary>
    /// A message the frame posts to its parent names the frame's <c>contentWindow</c> as its
    /// <c>source</c>, which is how a page tells which of its frames is talking. It named the window
    /// the frame's scripts ran against, which was no longer the frame's <c>contentWindow</c>.
    /// </summary>
    [Fact]
    public void AMessageTheFramePostsNamesTheFramesContentWindowAsItsSource()
    {
        Assert.Equal(
            "data=from the frame sourceIsFrame=true",
            Run(
                "parent.postMessage('from the frame', '*');",
                "var pageOut = document.getElementById('out');" +
                "pageOut.textContent = 'never-received';" +
                "var frame = document.getElementById('f');" +
                "window.addEventListener('message', function (e) {" +
                "  pageOut.textContent = 'data=' + e.data + ' sourceIsFrame=' + (e.source === frame.contentWindow);" +
                "});" +
                "frame.contentWindow;"));
    }

    /// <summary>
    /// The window the frame's script knows as <c>window</c> is its <c>document.defaultView</c> after
    /// the scripts have run too, as it is in a browser. The outer build defined <c>defaultView</c>
    /// again, over the second window, so a listener that ran later found the two apart.
    /// </summary>
    [Fact]
    public void AFramesWindowIsItsDocumentsDefaultViewWhenAMessageArrives()
    {
        Assert.Equal(
            "same=true",
            Run(
                "window.addEventListener('message', function () {" +
                "  pageOut.textContent = 'same=' + (window === document.defaultView);" +
                "});",
                "var pageOut = document.getElementById('out');" +
                "pageOut.textContent = 'listener-never-ran';" +
                "document.getElementById('f').contentWindow.postMessage('check', '*');"));
    }

    /// <summary>
    /// Messages a frame posts arrive in the order it posted them, round after round. The page's queue
    /// ran its tasks in hash-bucket order, and a task's id hashes to itself: a round whose ids
    /// straddled a multiple of the bucket count -- as ids do once a session has run a while -- arrived
    /// with its later messages first.
    /// </summary>
    [Fact]
    public void MessagesArriveInTheOrderTheyWerePosted()
    {
        Assert.Equal(
            "200 in order",
            Run(
                "var n = 0;" +
                "function round() { for (var i = 0; i < 20; i++) parent.postMessage(n++, '*'); if (n < 200) setTimeout(round, 10); }" +
                "round();",
                "var pageOut = document.getElementById('out'); var got = [];" +
                "pageOut.textContent = 'listener-never-ran';" +
                "window.addEventListener('message', function (e) {" +
                " got.push(e.data);" +
                " if (got.length === 200) {" +
                "  var wrong = got.findIndex(function (v, i) { return v !== i; });" +
                "  pageOut.textContent = wrong < 0 ? '200 in order' : 'message ' + wrong + ' was ' + got[wrong]; } });" +
                "document.getElementById('f').contentWindow;"));
    }
}
