using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The frame tree a page reaches from script — <c>contentWindow</c>, <c>contentDocument</c>,
/// <c>window.frames</c>, and the <c>parent</c>/<c>top</c>/<c>document</c> links back out of a nested
/// browsing context — asserted through two <c>&lt;iframe srcdoc&gt;</c>, which need no network.
/// <para>
/// <b>Nearly every line here is an identity question, and a lost identity is silent.</b> One container
/// element has one window object for the life of a document, minted by
/// <c>Features/SubWindowBinding.cs</c> and cached in <c>Runtime/BrowsingContextManager.cs</c>;
/// <c>iframe.contentWindow</c> and <c>window.frames[i]</c> are two unrelated call paths
/// (<c>Features/IframeElementBinding.cs</c>, and the frames array in <c>DomBridge/Lifecycle.cs</c>)
/// into that one cache. A cache that stops holding does not throw — it hands back a second window that
/// answers every question the same way and is <c>!==</c> the first, so the two paths go quietly out of
/// step and every listener, Map key and message target the page holds addresses a window nothing else
/// knows about. The frame's window is also built twice and only the second survives — building the
/// sub-document is what runs the frame's scripts, and those reach back for a window before the outer
/// build has cached one — which is why asking twice is asserted at all. <c>#f1</c> carries a script and
/// <c>#f2</c> does not, so one set of assertions covers both orderings.
/// </para>
/// <para>
/// <b>One test pins this bridge's shape rather than a browser's, deliberately.</b> A browser answers
/// <c>Object.keys(location)</c> with nothing, its components being prototype accessors, where a frame's
/// Location here carries thirteen own enumerable properties. That list and its order are asserted
/// because <c>Features/LocationBinding.cs</c> held two builders for the object, and moving the frame's
/// call site from one to the other, which has since happened, had no other page-visible surface.
/// </para>
/// </summary>
public class FrameStructureTests
{
    private const string PageUrl = "https://example.test/frames";

    /// <summary>
    /// Two same-origin frames, told apart by what is inside them. <c>#f1</c>'s script writes into an
    /// element whose <c>id</c> the containing page also uses, so a context switch that leaked shows up
    /// as the wrong <c>#one</c> having moved.
    /// </summary>
    private const string PageHtml =
        "<html><body>" +
        "<p id=\"one\">top</p>" +
        "<iframe id=\"f1\" srcdoc=\"<html><body><p id='one'>first</p>" +
        "<script>document.getElementById('one').textContent = 'ran-in-frame';</script>" +
        "</body></html>\"></iframe>" +
        "<iframe id=\"f2\" srcdoc=\"<html><body><p id='two'>second</p></body></html>\"></iframe>" +
        "<div id=\"out\"></div>" +
        "</body></html>";

    private static string Run(string script) => PageProbe.RunAgainst(PageHtml, PageUrl, script);

    [Fact]
    public void OneWindowPerFrameHoweverThePageAsksForIt()
    {
        // This is the assertion the SubWindowBinding retype has to survive. `byIndex` and `bare` reach
        // the frames array, `again` reaches the element accessor, and both end at the same cache — so a
        // GetOrCreate that stopped answering with the cached instance breaks this line and only this
        // line, everywhere else still reading like a working frame.
        Assert.Equal(
            "contentWindow=object contentDocument=object frames=2 " +
            "again=true byIndex=true secondByIndex=true bare=true distinct=true acrossReads=true",
            Run("""
                (function () {
                  var f1 = document.getElementById('f1'), f2 = document.getElementById('f2');
                  var w1 = f1.contentWindow, w2 = f2.contentWindow;
                  return 'contentWindow=' + (typeof w1) +
                         ' contentDocument=' + (typeof f1.contentDocument) +
                         ' frames=' + window.frames.length +
                         ' again=' + (f1.contentWindow === w1) +
                         ' byIndex=' + (window.frames[0] === w1) +
                         ' secondByIndex=' + (window.frames[1] === w2) +
                         ' bare=' + (frames[0] === w1) +
                         ' distinct=' + (w1 !== w2) +
                         ' acrossReads=' + (window.frames[0] === window.frames[0]);
                })()
                """));
    }

    [Fact]
    public void AFrameDocumentIsOneObjectReachedFromBothSides()
    {
        // `defaultView` decides the order the other two are built in: the sub-document is minted first
        // and points at the containing window until the frame's own window exists and claims it. A page
        // walking `node.ownerDocument.defaultView` to find "my window" otherwise gets the parent's and
        // operates on the wrong document with no error.
        Assert.Equal(
            "windowDocument=true again=true defaultView=true notThePageDocument=true " +
            "siblingsDiffer=true location=true",
            Run("""
                (function () {
                  var f1 = document.getElementById('f1');
                  var w = f1.contentWindow, d = f1.contentDocument;
                  return 'windowDocument=' + (w.document === d) +
                         ' again=' + (f1.contentDocument === d) +
                         ' defaultView=' + (d.defaultView === w) +
                         ' notThePageDocument=' + (d !== document) +
                         ' siblingsDiffer=' + (document.getElementById('f2').contentDocument !== d) +
                         ' location=' + (d.location === w.location);
                })()
                """));
    }

    [Fact]
    public void AFrameNamesTheContainingWindowAsParentAndTop()
    {
        // `parent` and `top` come from one resolution, so both being the page's window is one fact
        // asserted twice; `self` and `window` closing back on the frame is what stops a script handed
        // `frames[0]` from walking into the parent's globals believing they are its own.
        Assert.Equal(
            "parent=true top=true self=true window=true sibling=true notThePageWindow=true",
            Run("""
                (function () {
                  var w = document.getElementById('f1').contentWindow;
                  var sibling = document.getElementById('f2').contentWindow;
                  return 'parent=' + (w.parent === window) +
                         ' top=' + (w.top === window) +
                         ' self=' + (w.self === w) +
                         ' window=' + (w.window === w) +
                         ' sibling=' + (sibling.parent === w.parent) +
                         ' notThePageWindow=' + (w !== window);
                })()
                """));
    }

    [Fact]
    public void AFramesScriptRunsAgainstItsOwnDocumentAndTheTreesStaySevered()
    {
        // The window-context switch around the sub-document script run (DomBridge/SubDocuments.cs) is
        // what makes a frame's bare `document` mean the frame's. Both halves are asserted because the
        // failure is a swap, not an absence: with the switch gone, `#one` in the page carries the
        // frame's write and the frame's own still reads "first" — two wrong documents and no exception.
        Assert.Equal(
            "inFrame=ran-in-frame inPage=top pageCannotSeeIn=true frameSeesItsOwn=two",
            Run("""
                (function () {
                  var d1 = document.getElementById('f1').contentWindow.document;
                  var d2 = document.getElementById('f2').contentDocument;
                  return 'inFrame=' + d1.getElementById('one').textContent +
                         ' inPage=' + document.getElementById('one').textContent +
                         ' pageCannotSeeIn=' + (document.getElementById('two') === null) +
                         ' frameSeesItsOwn=' + d2.getElementById('two').id;
                })()
                """));
    }

    [Fact]
    public void AFramesLocationIsAboutSrcdocAndCarriesItsMembersInOneFixedOrder()
    {
        // PINNED SHAPE, NOT BROWSER BEHAVIOUR — see the class remarks. LocationBinding keeps two
        // builders that install exactly this list in exactly this order; pointing the frame's call site
        // at the other one is meant to be unobservable, and this is the only place a page could observe
        // it — down to which two of the thirteen are accessors, since an `href` that quietly stopped
        // having a setter would take assignment with it.
        Assert.Equal(
            "href=about:srcdoc protocol=about: " +
            "keys=protocol|host|hostname|port|pathname|search|origin|href|hash|assign|replace|reload|toString " +
            "hrefIsAccessor=function protocolIsData=undefined " +
            "assign=function replace=function reload=function",
            Run("""
                (function () {
                  var loc = document.getElementById('f1').contentWindow.location;
                  var describe = Object.getOwnPropertyDescriptor;
                  return 'href=' + loc.href +
                         ' protocol=' + loc.protocol +
                         ' keys=' + Object.keys(loc).join('|') +
                         ' hrefIsAccessor=' + (typeof describe(loc, 'href').set) +
                         ' protocolIsData=' + (typeof describe(loc, 'protocol').set) +
                         ' assign=' + (typeof loc.assign) +
                         ' replace=' + (typeof loc.replace) +
                         ' reload=' + (typeof loc.reload);
                })()
                """));
    }

    [Fact(Skip = "window.frames is WindowFrames (an exotic object forwarding to window and child frames) rather than window itself, so frames['named'] finds child frames even when shadowed by global vars. See FrameNameTests.")]
    public void TheFrameListIsTheWindowItself()
    {
        Assert.Equal(
            "isWindow=true stable=true",
            Run("""
                (function () {
                  return 'isWindow=' + (window.frames === window) +
                         ' stable=' + (window.frames === window.frames);
                })()
                """));
    }

    [Fact]
    public void WindowNumericIndexResolvesChildFramesDirectly()
    {
        Assert.Equal(
            "win0=true win1=true win2=undefined self0=true self1=true top0=true in0=true in1=true in2=false",
            Run("""
                (function () {
                  var f1 = document.getElementById('f1'), f2 = document.getElementById('f2');
                  var w1 = f1.contentWindow, w2 = f2.contentWindow;
                  return 'win0=' + (window[0] === w1) +
                         ' win1=' + (window[1] === w2) +
                         ' win2=' + String(window[2]) +
                         ' self0=' + (self[0] === w1) +
                         ' self1=' + (self[1] === w2) +
                         ' top0=' + (top[0] === w1) +
                         ' in0=' + (0 in window) +
                         ' in1=' + (1 in window) +
                         ' in2=' + (2 in window);
                })()
                """));
    }

    [Fact]
    public void WindowNumericIndexUpdatesOnDynamicFrameInsertionAndRemoval()
    {
        Assert.Equal(
            "startLen=2 startWin0=true startWin1=true " +
            "afterAddLen=3 afterAddWin2=true afterAddIn2=true " +
            "afterRemoveLen=2 afterRemoveWin0=true afterRemoveWin2=undefined afterRemoveIn2=false",
            Run("""
                (function () {
                  var f1 = document.getElementById('f1'), f2 = document.getElementById('f2');
                  var w1 = f1.contentWindow, w2 = f2.contentWindow;
                  var startLen = window.length;
                  var startWin0 = window[0] === w1;
                  var startWin1 = window[1] === w2;

                  var f3 = document.createElement('iframe');
                  document.body.appendChild(f3);
                  var w3 = f3.contentWindow;
                  var afterAddLen = window.length;
                  var afterAddWin2 = window[2] === w3;
                  var afterAddIn2 = 2 in window;

                  f1.remove();
                  var afterRemoveLen = window.length;
                  var afterRemoveWin0 = window[0] === w2;
                  var afterRemoveWin2 = String(window[2]);
                  var afterRemoveIn2 = 2 in window;

                  return 'startLen=' + startLen +
                         ' startWin0=' + startWin0 +
                         ' startWin1=' + startWin1 +
                         ' afterAddLen=' + afterAddLen +
                         ' afterAddWin2=' + afterAddWin2 +
                         ' afterAddIn2=' + afterAddIn2 +
                         ' afterRemoveLen=' + afterRemoveLen +
                         ' afterRemoveWin0=' + afterRemoveWin0 +
                         ' afterRemoveWin2=' + afterRemoveWin2 +
                         ' afterRemoveIn2=' + afterRemoveIn2;
                })()
                """));
    }

    [Fact]
    public void NestedFrameNumericIndexAccessResolvesSubFrames()
    {
        Assert.Equal(
            "parentHas0=true childHas0=false childLen=0 " +
            "afterNestedChildLen=1 childHas0Now=true nestedMatch=true parentHas2=true",
            Run("""
                (function () {
                  var f1 = document.getElementById('f1');
                  var w1 = f1.contentWindow;
                  var parentHas0 = window[0] === w1;
                  var childHas0 = 0 in w1;
                  var childLen = w1.length;

                  var nested = f1.contentDocument.createElement('iframe');
                  f1.contentDocument.body.appendChild(nested);
                  var nestedWin = nested.contentWindow;

                  var afterNestedChildLen = w1.length;
                  var childHas0Now = 0 in w1;
                  var nestedMatch = w1[0] === nestedWin;
                  var parentHas2 = window.length === 2 && window[0] === w1;

                  return 'parentHas0=' + parentHas0 +
                         ' childHas0=' + childHas0 +
                         ' childLen=' + childLen +
                         ' afterNestedChildLen=' + afterNestedChildLen +
                         ' childHas0Now=' + childHas0Now +
                         ' nestedMatch=' + nestedMatch +
                         ' parentHas2=' + parentHas2;
                })()
                """));
    }

    [Fact]
    public void CreepJsFrameIsolationProbePattern()
    {
        Assert.Equal(
            "start=2 length=3 indexed=true indexedType=object framesIndexed=true " +
            "ownDocument=true ownWindow=true sharedFunction=true sentinelPreserved=true afterRemoveLen=2 afterRemoveIndexed=false",
            Run("""
                (function () {
                  var start = window.length;
                  var frame = document.createElement('iframe');
                  document.body.appendChild(frame);
                  var child = frame.contentWindow;
                  var indexed = window[start] === child;
                  var indexedType = typeof window[start];
                  var framesIndexed = window.frames[start] === child;
                  var ownDocument = child.document !== document;
                  var ownWindow = child.window === child;
                  var sharedFunction = child.Function === Function;
                  child.document.body.innerHTML = '<p>child only</p>';
                  var sentinelPreserved = document.getElementById('one') !== null;
                  var length = window.length;
                  frame.remove();
                  var afterRemoveLen = window.length;
                  var afterRemoveIndexed = window[start] === child;

                  return 'start=' + start +
                         ' length=' + length +
                         ' indexed=' + indexed +
                         ' indexedType=' + indexedType +
                         ' framesIndexed=' + framesIndexed +
                         ' ownDocument=' + ownDocument +
                         ' ownWindow=' + ownWindow +
                         ' sharedFunction=' + sharedFunction +
                         ' sentinelPreserved=' + sentinelPreserved +
                         ' afterRemoveLen=' + afterRemoveLen +
                         ' afterRemoveIndexed=' + afterRemoveIndexed;
                })()
                """));
    }

    [Fact]
    public void SubWindowExposesScreenMatchMediaAndCrypto()
    {
        Assert.Equal(
            "hasScreen=object screenWidth=number hasMatchMedia=function hasCrypto=object hasCryptoCtor=function hasInnerWidth=number",
            Run("""
                (function () {
                  var f1 = document.getElementById('f1');
                  var w1 = f1.contentWindow;
                  return 'hasScreen=' + (typeof w1.screen) +
                         ' screenWidth=' + (typeof w1.screen.width) +
                         ' hasMatchMedia=' + (typeof w1.matchMedia) +
                         ' hasCrypto=' + (typeof w1.crypto) +
                         ' hasCryptoCtor=' + (typeof w1.Crypto) +
                         ' hasInnerWidth=' + (typeof w1.innerWidth);
                })()
                """));
    }
}
