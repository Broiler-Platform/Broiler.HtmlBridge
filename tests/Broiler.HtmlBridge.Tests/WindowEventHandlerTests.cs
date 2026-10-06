using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The page window's event-handler attributes -- <c>onmessage</c>, <c>onhashchange</c> and the rest --
/// which are accessors of the global object now, kept for the page apart from any frame's.
/// </summary>
/// <remarks>
/// The page's window dispatch ran only its listeners, so <c>onmessage = …</c> and
/// <c>onhashchange = …</c> on the page itself never ran, the commonest way a page listens to messages
/// from its frames among them. Measured in Chromium: a handler is <c>null</c> until set, holds only an
/// object, and runs as a listener does, its <c>return false</c> cancelling the event.
/// </remarks>
public class WindowEventHandlerTests
{
    private static string Settle(string script)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions()));
        using var session = engine.ExecuteInteractive(
            ["var out = document.getElementById('out'); var seen = [];" +
             "function log(entry) { seen.push(entry); out.textContent = seen.join('|'); }" + script],
            [], "<!DOCTYPE html><html><body><div id=\"out\">none</div></body></html>", "https://example.com/page");
        return PageProbe.OutOf(session!.SettleLoadWindow(), decode: true);
    }

    /// <summary>A page's own <c>onmessage</c> and <c>onhashchange</c> run.</summary>
    [Fact]
    public void ThePagesOnMessageAndOnHashChangeRun()
    {
        Assert.Equal(
            "onhashchange #x|onmessage hi",
            Settle(
                "onmessage = function (e) { log('onmessage ' + e.data); };" +
                "window.onhashchange = function () { log('onhashchange ' + location.hash); };" +
                "location.hash = '#x'; postMessage('hi', '*');"));
    }

    /// <summary>A handler runs after the window's listeners, and its <c>return false</c> cancels the event.</summary>
    [Fact]
    public void AHandlerRunsAfterTheListenersAndCanCancel()
    {
        Assert.Equal(
            "listener|handler true|dispatched false",
            Settle(
                "onresize = function (e) { log('handler ' + (this === window)); return false; };" +
                "addEventListener('resize', function () { log('listener'); });" +
                "log('dispatched ' + window.dispatchEvent(new Event('resize', { cancelable: true })));"));
    }

    /// <summary>A handler is <c>null</c> until it is set, holds only an object, and is the same however it is spelled.</summary>
    [Fact]
    public void AHandlerIsNullUntilSetAndHoldsOnlyAnObject()
    {
        Assert.Equal(
            "null|null|function|true|true|null",
            Settle(
                "log(String(onmessage));" +
                "onmessage = 'not a function'; log(String(onmessage));" +
                "window.onmessage = function () {}; log(typeof onmessage);" +
                "log(String(window.onmessage === onmessage));" +
                "log(String('onmessage' in window));" +
                "onmessage = null; log(String(window.onmessage));"));
    }

    /// <summary>The page's <c>onload</c> runs once, in the load sequence, however it was set.</summary>
    [Fact]
    public void ThePagesOnloadRunsOnce()
    {
        Assert.Equal(
            "onload 1",
            Settle("var loads = 0; onload = function () { loads++; log('onload ' + loads); };"));
    }
}
