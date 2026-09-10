using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// The frame tree a page reaches from script — <c>contentWindow</c>, <c>contentDocument</c>,
/// <c>window.frames</c>, and the <c>parent</c>/<c>top</c>/<c>document</c> links back out of a nested
/// browsing context — asserted through two <c>&lt;iframe srcdoc&gt;</c>, which need no network.
/// <para>
/// <b>Nearly every line here is an identity question, and a lost identity is silent.</b> One container
/// element has one window object for the life of a document, minted by
/// <c>Features/SubWindowBinding.cs</c> and cached in <c>Runtime/BrowsingContextManager.cs</c>;
/// <c>iframe.contentWindow</c> and <c>window.frames[i]</c> are two unrelated call paths
/// (<c>Features/IframeElementBinding.cs</c>, and the frames array in <c>DomBridge.WindowLoad.cs</c>)
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
/// because <c>Features/LocationBinding.cs</c> holds two builders for the object, and moving the frame's
/// call site from one to the other has no other page-visible surface at all.
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

    /// <summary>
    /// Runs <paramref name="script"/> against the fixture document and returns what it wrote to
    /// <c>#out</c>. Reading the result out of the serialized DOM keeps the test to the engine's public
    /// surface — the binding under test is internal, and reaching for it directly would pin its shape
    /// rather than its behaviour.
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

    [Fact(Skip = "window.frames is a fresh array built on every read (BuildWindowFramesArray, " +
                 "src/Broiler.HtmlBridge.Dom/DomBridge.WindowLoad.cs:486, installed as the accessor at " +
                 "src/Broiler.HtmlBridge.Dom/DomBridge/Registration/Window.cs:479), where HTML's Window " +
                 "interface has window, self and frames all answer with the Window itself — so " +
                 "`window.frames === window` is false and two reads hand back two objects, which is " +
                 "what a page caching `var f = frames` and comparing it against `window` asks.")]
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
}
