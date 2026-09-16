using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The bridge's three wrapper roots — the <c>document</c>, <c>window</c> and <c>window.visualViewport</c>
/// objects it holds as its own — read back from page script through members that read them, so the
/// commit that re-types the roots lands onto an assertion rather than beside one.
/// <para>
/// A root is read in a dozen places and almost none of them hands it to a page as itself. They use it
/// as an event's target, as the end of a composed path, as a synthetic event's <c>view</c>, or as the
/// receiver an <c>EventTarget.prototype</c> method is routed by. Those are the reads a retype can break
/// without a word: a guard that stops testing anything still compiles, and a root that answers Missing
/// where it used to answer the object is not an error anywhere. Each test below goes through one such
/// reader that nothing else in this suite reaches.
/// </para>
/// <para>
/// <b>Each identity is asserted beside a count and a negative.</b> A listener that did not run leaves
/// nothing to compare, and a root answered from the wrong object is still an object; so each test also
/// records how often its listener ran, and asks that the root is not the other root or that a listener
/// on a different target did not run. The visualViewport root's one reader, its scroll dispatch, is
/// covered by the visualViewport scroll test rather than here.
/// </para>
/// </summary>
public class WrapperRootReaderTests
{
    private const string PageUrl = "https://example.test/wrapper-roots";

    private const string PageHtml =
        "<html><body>" +
        "<div id=\"host\"><span id=\"kid\">text</span></div>" +
        "<div id=\"out\"></div>" +
        "</body></html>";

    /// <summary>
    /// Runs <paramref name="script"/> against the fixture document and returns what it wrote to
    /// <c>#out</c>, which keeps each test to the page's own view of the roots.
    /// </summary>
    private static string Run(string script)
    {
        var html = new ScriptEngine().Execute(
            [$"document.getElementById('out').textContent = String({script});"],
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

    [Fact]
    public void AWindowDispatchTargetsTheWindowRoot()
    {
        // DispatchWindowEvent reads the window root for the event's target, its currentTarget and its
        // one-entry composed path, and returns before firing anything when the root is absent -- so an
        // absent root reads here as fired=0, and a root answered from another object as false.
        Assert.Equal(
            "fired=1 target=true current=true notDocument=true path=true",
            Run("""
                (function () {
                  var seen = [];
                  window.addEventListener('rootprobe', function (e) {
                    var p = e.composedPath();
                    seen.push('target=' + (e.target === window) +
                              ' current=' + (e.currentTarget === window) +
                              ' notDocument=' + (e.target !== document) +
                              ' path=' + (p.length === 1 && p[0] === window));
                  });
                  window.dispatchEvent(new Event('rootprobe'));
                  return 'fired=' + seen.length + ' ' + seen.join('|');
                })()
                """));
    }

    [Fact]
    public void TheSharedEventTargetMethodsTellTheWindowByItsRoot()
    {
        // The window installs its own addEventListener, so window.addEventListener never reaches the
        // routed EventTarget.prototype method; calling that method on the window does, and the only
        // way it tells the window from a node is to compare the receiver with the window root. A
        // comparison that never matched would hand the call to the engine's own method, which files
        // the listener where window.dispatchEvent never looks. One that always matched would file the
        // element's listener on the window, and the element half is the control for that.
        Assert.Equal(
            "threw=none window=1,1,1 host=0,1",
            Run("""
                (function () {
                  var host = document.getElementById('host');
                  var onWindow = 0, onHost = 0, threw = 'none';
                  var w = function () { onWindow++; };
                  var h = function () { onHost++; };
                  try {
                    EventTarget.prototype.addEventListener.call(window, 'routed', w);
                    EventTarget.prototype.addEventListener.call(host, 'routed', h);
                  } catch (e) { threw = String(e); }
                  window.dispatchEvent(new Event('routed'));
                  var windowAfterWindow = onWindow, hostAfterWindow = onHost;
                  host.dispatchEvent(new Event('routed'));
                  var windowAfterHost = onWindow, hostAfterHost = onHost;
                  try {
                    EventTarget.prototype.removeEventListener.call(window, 'routed', w);
                  } catch (e) { threw = String(e); }
                  window.dispatchEvent(new Event('routed'));
                  return 'threw=' + threw +
                         ' window=' + windowAfterWindow + ',' + windowAfterHost + ',' + onWindow +
                         ' host=' + hostAfterWindow + ',' + hostAfterHost;
                })()
                """));
    }

    [Fact]
    public void AnElementsComposedPathEndsAtTheDocumentRootAndThenTheWindowRoot()
    {
        // The dispatch module reads both roots through its host: the document root as the current
        // target while the event passes the document node, and both as the last two entries of
        // composedPath(). The capture listener goes through EventTarget.prototype so that this test
        // does not also depend on the document object's own prototype chain.
        Assert.Equal(
            "first=true beforeLast=true last=true documentOnce=true doc=true:true",
            Run("""
                (function () {
                  var kid = document.getElementById('kid');
                  var path = null, doc = 'none';
                  EventTarget.prototype.addEventListener.call(document, 'pathprobe', function (e) {
                    doc = (e.currentTarget === document) + ':' + (e.currentTarget !== window);
                  }, true);
                  kid.addEventListener('pathprobe', function (e) { path = e.composedPath(); });
                  kid.dispatchEvent(new Event('pathprobe'));
                  if (!path) return 'path=none doc=' + doc;
                  var n = path.length;
                  return 'first=' + (path[0] === kid) +
                         ' beforeLast=' + (path[n - 2] === document) +
                         ' last=' + (path[n - 1] === window) +
                         ' documentOnce=' + (path.indexOf(document) === n - 2) +
                         ' doc=' + doc;
                })()
                """));
    }

    [Fact]
    public void ASyntheticFocusEventsViewIsTheWindowRoot()
    {
        // focus() and blur() build their event with the window root as `view`, read through the
        // event-target host, and put null there when the root is not an object.
        Assert.Equal(
            "fired=1 view=true notDocument=true",
            Run("""
                (function () {
                  var kid = document.getElementById('kid');
                  var seen = [];
                  kid.addEventListener('focus', function (e) {
                    seen.push('view=' + (e.view === window) + ' notDocument=' + (e.view !== document));
                  });
                  kid.focus();
                  return 'fired=' + seen.length + ' ' + seen.join('|');
                })()
                """));
    }
}
