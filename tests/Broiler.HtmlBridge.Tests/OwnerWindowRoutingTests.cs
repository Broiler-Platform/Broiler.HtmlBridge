using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// Owner-window routing: a <c>MessagePort</c> delivers its <c>message</c> event with the globals
/// switched to the window that owns the port, not to whichever window happens to be current when
/// the delivery runs.
/// <para>
/// <b>Nothing has ever asserted this, and the map that implements it can be deleted without a
/// single existing test noticing.</b> <c>EventTargetRegistry</c> files a port's owner at creation
/// (<c>Features/MessagingBinding.cs:685</c>) and <c>WindowContextManager.ResolveOwnerWindow</c>
/// reads it back — but on a miss that method falls through to <c>ResolveCurrentWindow()</c>, so on
/// a one-window page the fallback and the map answer the same object. Every routing test that can
/// be written against a single window therefore passes with the map gone.
/// </para>
/// <para>
/// <b>Which is also why this is not a <c>frameWindow.postMessage</c> test.</b> That path's caller
/// already wraps its own delivery in the window switch
/// (<c>Features/MessagingBinding.cs:384-399</c>), so by the time the owner map is consulted the
/// current window IS the target window and the fallback is again indistinguishable. The port path
/// is the one that queues its delivery as a bare frame action
/// (<c>Features/MessagingBinding.cs:757-766</c>) and reaches
/// <c>RunInOwnerWindow</c> with nothing having switched: the channel is built inside the frame's
/// script, where the current window is the sub-window, and the message is delivered from the
/// post-load drain, where it is the page's. Owner and current differ, and the map is the only
/// thing that can tell them apart.
/// </para>
/// <para>
/// The listener writes into the PAGE's <c>#out</c> through an element captured before the swap, so
/// the output location is fixed whichever window wins; what the text says is which document the
/// listener's own <c>document</c> named. The placeholder distinguishes "routed to the wrong
/// window" from "never ran at all" — a frame action that throws is caught and logged
/// (<c>Runtime/BrowserEventLoop.cs:380-385</c>), so a broken fixture would otherwise be silent.
/// </para>
/// </summary>
public class OwnerWindowRoutingTests
{
    private const string PageUrl = "https://example.test/owner-window";

    /// <summary>
    /// A <c>srcdoc</c> frame, so the fixture needs no network and its document URL is
    /// <c>about:srcdoc</c> — which is also the second observable below, since <c>location</c> is
    /// swapped alongside <c>document</c>. The frame's script is an IIFE on purpose: a top-level
    /// <c>var</c> in a sub-document's script is diffed out of the global object and republished on
    /// the frame's window (<c>DomBridge.SubDocumentGlobals.cs</c>), and this fixture has no reason
    /// to exercise that. Its inner attributes are single-quoted so the double-quoted <c>srcdoc</c>
    /// value survives, and the script contains no double quote for the same reason.
    /// </summary>
    private const string PageHtml =
        """
        <html><body>
        <iframe id="f" srcdoc="<html><body><p id='inner'>hi</p><script>
        (function () {
          var frameDocument = document;
          var channel = new MessageChannel();
          channel.port2.onmessage = function () {
            pageOut.textContent =
              'ran=true' +
              ' sameAsFrame=' + (document === frameDocument) +
              ' sameAsPage=' + (document === pageDocument) +
              ' twoDocuments=' + (frameDocument !== pageDocument) +
              ' href=' + location.href;
          };
          channel.port1.postMessage('ping');
        })();
        </script></body></html>"></iframe>
        <div id="out"></div>
        </body></html>
        """;

    /// <summary>
    /// Runs the fixture and returns what the port's listener wrote to the page's <c>#out</c>.
    /// <para>
    /// The top-level script does three things and each is load-bearing. It publishes the page's
    /// document and its <c>#out</c> element as globals, which the frame's listener reads by name —
    /// bare globals rather than <c>parent.document</c>, because <c>parent</c> and <c>document</c>
    /// are two of the seven names the window-context swap rebinds and reading either from inside a
    /// swapped frame would be reading the thing under test. It writes a placeholder, so a listener
    /// that never runs is told apart from one that ran in the wrong window. And it touches
    /// <c>contentDocument</c>, which is what builds the sub-document and runs the frame's scripts
    /// (<c>DomBridge/SubDocuments.cs:238-243</c>) — entering there rather than letting the load
    /// event's <c>window.frames</c> enumeration do it, because that entry order mints a throwaway
    /// window the outer call then replaces (<c>DomBridge.SubDocumentGlobals.cs</c>) and this test
    /// should not depend on which of the two the port got filed against.
    /// </para>
    /// <para>
    /// The delivery itself happens in <c>ScriptEngine</c>'s post-load drain: the frame action lands
    /// in <c>BrowserEventLoop</c>, <c>HasWorkDueBy</c> reports frame actions as due at any horizon,
    /// and <c>RunPageScripts</c> calls <c>DrainAsyncWork</c> after <c>FireWindowLoadEvent</c> and
    /// before serialization.
    /// </para>
    /// </summary>
    private static string Run()
    {
        var html = new ScriptEngine().Execute(
            [
                """
                var pageDocument = document;
                var pageOut = document.getElementById('out');
                pageOut.textContent = 'listener-never-ran';
                document.getElementById('f').contentDocument;
                """
            ],
            PageHtml,
            PageUrl);

        Assert.NotNull(html);

        const string open = "<div id=\"out\">";
        var start = html!.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no #out div in serialized output: {html}");
        start += open.Length;
        var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"unterminated #out div in serialized output: {html}");
        return html[start..end];
    }

    /// <summary>
    /// The whole assertion in one string, because the fields are only worth anything together.
    /// <c>sameAsFrame</c> and <c>sameAsPage</c> are the two halves that flip when the owner map
    /// stops being consulted; <c>twoDocuments</c> is the guard that stops the pair passing
    /// vacuously if the fixture ever stopped building a second document at all; <c>href</c> is a
    /// second, independent read of the same swap (<c>location</c> is rebound alongside
    /// <c>document</c>), and <c>about:srcdoc</c> is what a srcdoc frame's Location reports —
    /// <c>FrameLocationTests</c> pins it.
    /// </summary>
    [Fact]
    public void APortCreatedInAFrameDeliversInTheFramesWindowAndNotThePages()
    {
        Assert.Equal(
            "ran=true sameAsFrame=true sameAsPage=false twoDocuments=true href=about:srcdoc",
            Run());
    }
}
