using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Where an element's event travels, and how its listeners are called: through the element's own
/// document -- a frame's, inside a frame -- and on to that document's window, with the object a
/// listener is on as its <c>this</c>, and an <c>on…</c> handler's <c>return false</c> cancelling it.
/// </summary>
/// <remarks>
/// Each of these held for no event, made by a script or by a user. An element's event stopped at the
/// top document whatever document the element was in, so no window heard one; a listener's
/// <c>this</c> was the listener function; and an inline handler's return value was dropped.
/// </remarks>
public class EventPathTests
{
    private const string PageUrl = "https://example.test/event-path";

    private static string Run(string body, string script) =>
        PageProbe.OutOf(PageProbe.Render([script], $"<html><body>{body}<div id=\"out\"></div></body></html>", PageUrl), decode: true);

    /// <summary>
    /// A bubbling event dispatched at an element reaches the window's capture listener first and its
    /// bubbling listener and <c>onclick</c> last; a <c>load</c> event stops at the document.
    /// </summary>
    [Fact]
    public void AnElementsEventReachesTheWindowExceptLoad()
    {
        Assert.Equal(
            "window-capture>element>document>window>window.onclick|load:element>document",
            Run("<p id=\"p\">p</p>",
                "var p = document.getElementById('p'); var path = [];" +
                "function note(name) { return function () { path.push(name); }; }" +
                "window.addEventListener('click', note('window-capture'), true);" +
                "p.addEventListener('click', note('element'));" +
                "document.addEventListener('click', note('document'));" +
                "window.addEventListener('click', note('window'));" +
                "window.onclick = note('window.onclick');" +
                "p.dispatchEvent(new Event('click', { bubbles: true }));" +
                "var clicked = path.join('>'); path = [];" +
                "p.addEventListener('load', note('element'));" +
                "document.addEventListener('load', note('document'));" +
                "window.addEventListener('load', note('window'));" +
                "p.dispatchEvent(new Event('load', { bubbles: true }));" +
                "document.getElementById('out').textContent = clicked + '|load:' + path.join('>');"));
    }

    /// <summary>
    /// The document's <c>on…</c> handlers run, as Chromium's do: <c>onclick</c> after the document's
    /// listeners, with the document as <c>this</c> and its <c>return false</c> cancelling, and
    /// <c>onreadystatechange</c> as the document's readyState moves on. Neither was defined, so assigning
    /// one set a plain property nothing read.
    /// </summary>
    [Fact]
    public void TheDocumentsHandlersRun()
    {
        Assert.Equal(
            "initial null true|non-function null|listener|onclick 3 p true|dispatched false|" +
            "readystatechange interactive true|readystatechange complete true",
            Run("<p id=\"p\">p</p>",
                "var p = document.getElementById('p'); var log = [];" +
                "function show() { document.getElementById('out').textContent = log.join('|'); }" +
                "log.push('initial ' + document.onclick + ' ' + ('onscroll' in document));" +
                "document.addEventListener('click', function () { log.push('listener'); });" +
                "document.onclick = function (e) {" +
                " log.push('onclick ' + e.eventPhase + ' ' + e.target.id + ' ' + (this === document)); return false; };" +
                "document.onkeydown = 'x'; log.push('non-function ' + document.onkeydown);" +
                "log.push('dispatched ' + p.dispatchEvent(new Event('click', { bubbles: true, cancelable: true })));" +
                "document.onreadystatechange = function () {" +
                " log.push('readystatechange ' + document.readyState + ' ' + (this === document)); show(); };" +
                "show();"));
    }

    /// <summary>A frame's document has its own <c>on…</c> handlers, which its own events run.</summary>
    [Fact]
    public void AFramesDocumentsHandlersRun()
    {
        Assert.Equal(
            "frame-document.onclick true|page null",
            Run("<iframe id=\"f\" srcdoc=\"<p id=p>p</p>\"></iframe>",
                "var log = []; var d = document.getElementById('f').contentWindow.document;" +
                "d.onclick = function () { log.push('frame-document.onclick ' + (this === d)); };" +
                "document.onclick = function () { log.push('page-document.onclick'); };" +
                "d.getElementById('p').dispatchEvent(new Event('click', { bubbles: true }));" +
                "document.onclick = null; log.push('page ' + document.onclick);" +
                "document.getElementById('out').textContent = log.join('|');"));
    }

    /// <summary>
    /// <c>DOMContentLoaded</c> fires once at the document and reaches the window once, through the
    /// document's path, with the document as its target. A second dispatch at the window ran the
    /// window's listeners twice once the path reached the window.
    /// </summary>
    [Fact]
    public void DomContentLoadedReachesTheWindowOnceThroughTheDocument()
    {
        Assert.Equal(
            "document,window:true",
            Run("",
                "var log = [];" +
                "document.addEventListener('DOMContentLoaded', function () { log.push('document'); });" +
                "window.addEventListener('DOMContentLoaded', function (e) {" +
                " log.push('window:' + (e.target === document)); document.getElementById('out').textContent = log.join(','); });"));
    }

    /// <summary>
    /// A viewport scroll reaches each of the window's scroll listeners once, a capture listener
    /// included: it is fired at the document, whose path starts and ends at the window. The scroll is
    /// heard in the next frame, not in the call that scrolled.
    /// </summary>
    [Fact]
    public void AViewportScrollReachesEachWindowListenerOnce()
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredBoxLayoutView(new Dictionary<string, System.Drawing.RectangleF>
            {
                ["root"] = new(0, 0, 1024, 5000),
                ["tall"] = new(0, 0, 1024, 5000),
            }),
        }));

        var html = engine.Execute(
            ["var hits = [], out = document.getElementById('out');" +
             "function report(hit) { hits.push(hit); out.textContent = hits.join(',') + '|' + scrollY; }" +
             "window.addEventListener('scroll', function () { report('capture'); }, true);" +
             "window.addEventListener('scroll', function () { report('bubble'); });" +
             "window.scrollTo(0, 100); out.textContent = 'scrolled ' + hits.length;"],
            "<html id=\"root\"><body><div id=\"tall\" style=\"height:5000px\"></div><div id=\"out\"></div></body></html>",
            PageUrl);

        Assert.Equal("capture,bubble|100", PageProbe.OutOf(html!));
    }

    /// <summary>
    /// An event inside a frame travels through the frame's document and window, not the page's.
    /// </summary>
    [Fact]
    public void AnEventInAFrameTravelsThroughTheFramesDocumentAndWindow()
    {
        Assert.Equal(
            "frame-element>frame-document>frame-window",
            Run("<iframe id=\"f\" srcdoc=\"<p id=p>p</p>\"></iframe>",
                "var path = [];" +
                "function note(name) { return function () { path.push(name); }; }" +
                "document.addEventListener('ping', note('page-document'));" +
                "window.addEventListener('ping', note('page-window'));" +
                "var w = document.getElementById('f').contentWindow; var d = w.document;" +
                "d.getElementById('p').addEventListener('ping', note('frame-element'));" +
                "d.addEventListener('ping', note('frame-document'));" +
                "w.addEventListener('ping', note('frame-window'));" +
                "d.getElementById('p').dispatchEvent(new Event('ping', { bubbles: true }));" +
                "document.getElementById('out').textContent = path.join('>');"));
    }

    /// <summary>
    /// A listener's <c>this</c> is the object it is on -- the element, the document, the window -- and
    /// an inline handler's is its element. Both were the function itself.
    /// </summary>
    [Fact]
    public void AListenersThisIsTheObjectItIsOn()
    {
        Assert.Equal(
            "p|document|window|b",
            Run("<p id=\"p\">p</p><button id=\"b\" onclick=\"seen.push(this.id)\">b</button>",
                "var seen = [];" +
                "document.getElementById('p').addEventListener('ping', function () { seen.push(this.id); });" +
                "document.addEventListener('ping', function () { seen.push(this === document ? 'document' : '?'); });" +
                "window.addEventListener('ping', function () { seen.push(this === window ? 'window' : '?'); });" +
                "document.getElementById('p').dispatchEvent(new Event('ping', { bubbles: true }));" +
                "document.getElementById('b').dispatchEvent(new Event('click'));" +
                "document.getElementById('out').textContent = seen.join('|');"));
    }

    /// <summary>
    /// An inline handler that returns <see langword="false"/> cancels a cancelable event, so
    /// <c>dispatchEvent</c> answers false; returning anything else does not.
    /// </summary>
    [Fact]
    public void AnInlineHandlerReturningFalseCancelsTheEvent()
    {
        Assert.Equal(
            "false|true|true",
            Run("<a id=\"no\" href=\"#\" onclick=\"return false\">no</a><a id=\"yes\" href=\"#\" onclick=\"return true\">yes</a>",
                "function fire(id, cancelable) { return document.getElementById(id).dispatchEvent(new Event('click', { cancelable: cancelable })); }" +
                "document.getElementById('out').textContent = [fire('no', true), fire('yes', true), fire('no', false)].join('|');"));
    }
}
