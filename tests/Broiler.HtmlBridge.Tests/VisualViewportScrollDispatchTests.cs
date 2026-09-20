using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>window.visualViewport</c>'s <c>scroll</c> dispatch, asserted from page script.
/// <para>
/// <b>Nothing had ever run it.</b> The visual viewport carries <c>addEventListener</c>, a listener store,
/// an event object and a dispatcher, and before this file no test named any of them —
/// <c>grep -rl visualViewport src --include=*Tests*.cs</c> found nothing. The dispatcher reads two things a
/// page never sees the type of: the wrapper root it takes the event's target from, and the listener list it
/// walks. Changing how either is held can break an identity a page relies on without anything failing
/// loudly: a target that is no longer the viewport, a listener that no longer comes off.
/// </para>
/// <para>
/// <b>Every marker begins by proving a scroll happened.</b>
/// <c>DomBridge.DispatchVisualViewportScrollEvent</c> returns before building an event when the viewport
/// wrapper is absent or nothing is listening, and it is only reached when the visual viewport's page offset
/// actually changes. A test that never moved the viewport would pass every clause it wrote, so each marker
/// states how many times the listener ran and whether <c>visualViewport.pageTop</c> moved, and the whole
/// string is asserted.
/// </para>
/// <para>
/// <b>Why the fixture scrolls a fixed, absurdly tall element rather than the page.</b> This harness never
/// lays the document out: no composition root registers a layout view, so the bridge's geometry snapshot is
/// empty and every scroll extent is zero. <c>window.scrollTo</c>, <c>window.scrollBy</c>,
/// <c>documentElement.scrollTop</c> and an ordinary <c>scrollIntoView</c> all clamp to that empty range,
/// stay at zero and dispatch nothing. The branch of <c>scrollIntoView</c> that brings a
/// <c>position: fixed</c> target into the visual viewport needs no layout. Once
/// <c>visualViewport.scale</c> is above 1 the visual viewport is smaller than the layout viewport and can
/// move by the difference, and a target with no box takes its height from its declared style.
/// <c>block: 'end'</c> asks for that height less the visual viewport's, so the declared height has to be
/// the larger or the request resolves below zero, clamps back, and the test goes quiet instead of red.
/// 100000px is the larger at any viewport a test here is given.
/// </para>
/// <para>
/// <b>Every clause is recorded by value, never thrown.</b> The dispatcher catches and logs whatever a
/// listener throws, so an assertion made inside the listener would vanish. A clause pushed into the marker
/// cannot, and a listener that died before pushing it shows up as a count of zero.
/// </para>
/// </summary>
public class VisualViewportScrollDispatchTests
{
    private const string PageUrl = "https://example.test/visual-viewport";

    /// <summary>
    /// <c>#pinned</c> is the fixed, over-tall element whose <c>scrollIntoView</c> moves the visual
    /// viewport; <c>#out</c> is where each script writes its marker.
    /// </summary>
    private const string PageHtml =
        "<html><body>" +
        "<div id=\"pinned\" style=\"position:fixed;height:100000px\"></div>" +
        "<div id=\"out\"></div>" +
        "</body></html>";

    /// <summary>
    /// The probe read, which keeps each test to what a page can see: the listener store, the dispatcher
    /// and the event object are all internal.
    /// </summary>
    private static string Run(string script) => PageProbe.RunAgainst(PageHtml, PageUrl, script);

    [Fact]
    public void AScrollListenerSeesTheVisualViewportAsTargetAndCurrentTarget()
    {
        // fired= and moved= are the control: without them every clause after them describes an event that
        // was never built. The scale is assigned before the listener is added so that the count is the
        // scroll's alone — assigning it re-clamps the offsets and dispatches nothing today, and a change
        // that made it dispatch should not have to come through this assertion. distinct= is the premise
        // target= rests on: that window.visualViewport is an object of its own, neither the window nor the
        // document, so being identical to it means being the viewport.
        Assert.Equal(
            "fired=1 moved=true distinct=true type=scroll target=true current=true",
            Run("""
                (function () {
                  var vv = window.visualViewport;
                  var seen = [];
                  var h = function (e) {
                    seen.push('type=' + e.type +
                              ' target=' + (e.target === window.visualViewport) +
                              ' current=' + (e.currentTarget === e.target));
                  };
                  vv.scale = 2;
                  vv.addEventListener('scroll', h);
                  var before = vv.pageTop;
                  document.getElementById('pinned').scrollIntoView({ block: 'end' });
                  return 'fired=' + seen.length +
                         ' moved=' + (vv.pageTop > before) +
                         ' distinct=' + (typeof vv === 'object' && vv !== null && vv !== window && vv !== document) +
                         ' ' + seen.join('|');
                })()
                """));
    }

    [Fact]
    public void RemovingAScrollListenerTakesTheReferenceThatWasAdded()
    {
        // The two reference questions EventListenerRegistrationTests asks of the node and window stores,
        // asked of a listener store it does not reach. The same listener added twice runs once. The twin
        // has the same source and is a different object, so removing it must take nothing off — the
        // scroll back to the start proves that by dispatching. movedWithNoListener= is the other half of
        // the control: once the last listener is gone the viewport must still move, which is what tells a
        // dispatch the early return suppressed apart from a scroll that never happened.
        Assert.Equal(
            "deduped=1 afterTwin=1 afterSelf=0 movedWithNoListener=true",
            Run("""
                (function () {
                  var vv = window.visualViewport;
                  var pinned = document.getElementById('pinned');
                  var n = 0;
                  var make = function () { return function () { n++; }; };
                  var a = make();
                  var twin = make();
                  vv.scale = 2;
                  vv.addEventListener('scroll', a);
                  vv.addEventListener('scroll', a);
                  pinned.scrollIntoView({ block: 'end' });
                  var deduped = n;
                  vv.removeEventListener('scroll', twin);
                  pinned.scrollIntoView({ block: 'start' });
                  var afterTwin = n - deduped;
                  vv.removeEventListener('scroll', a);
                  var before = vv.pageTop;
                  pinned.scrollIntoView({ block: 'end' });
                  return 'deduped=' + deduped +
                         ' afterTwin=' + afterTwin +
                         ' afterSelf=' + (n - deduped - afterTwin) +
                         ' movedWithNoListener=' + (vv.pageTop > before);
                })()
                """));
    }

    [Fact(Skip = "A visual-viewport scroll listener is called with itself as this. " +
                 "DomBridge.DispatchVisualViewportScrollEvent, in " +
                 "src/Broiler.HtmlBridge.Dom/DomBridge/LayoutMetrics.Scrolling.cs, passes each listener as its " +
                 "own receiver, and has since the dispatch was first written, where the inner invoke of " +
                 "DOM §2.9 calls the callback with the event's currentTarget, which is window.visualViewport.")]
    public void AScrollListenerIsCalledWithTheVisualViewportAsThis()
    {
        Assert.Equal(
            "viewport",
            Run("""
                (function () {
                  var vv = window.visualViewport;
                  var seen = 'never';
                  var h = function () {
                    seen = (this === vv) ? 'viewport' : ((this === h) ? 'listener' : 'other');
                  };
                  vv.scale = 2;
                  vv.addEventListener('scroll', h);
                  document.getElementById('pinned').scrollIntoView({ block: 'end' });
                  return seen;
                })()
                """));
    }
}
