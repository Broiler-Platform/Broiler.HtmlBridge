using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// A nested browsing context, asserted from the containing page's script: the frame's window,
/// document and Location, and the frame's own inline script — that it runs at all, and which
/// window, document and Location it runs against.
/// <para>
/// <b>Nothing in this suite mentioned frames, which is why two ordinary changes could not land.</b>
/// Retyping <c>SubWindowBinding.GetOrCreate</c> rebinds <c>DomBridge/SubDocuments.cs:396</c>'s
/// <c>RunWithWindowContext(subWindow, …)</c> from one overload to the other with no textual change
/// to that line, and pointing the frame's Location at the realm-framed builder moves a live path
/// onto a builder that has never executed. Neither failure is one a compiler sees: the first reads
/// as a frame running against the wrong globals, or a page never getting its own back; the second
/// as a Location missing a member, or carrying the same members in another order.
/// </para>
/// <para>
/// <b>The Location test spells the object out, and it pins the CURRENT SHAPE rather than a
/// browser's.</b> A browser keeps every Location member on <c>Location.prototype</c> and
/// non-enumerable, so <c>Object.keys(location)</c> answers empty there and thirteen names here.
/// The browser's answer would fail today and say nothing about the swap; these thirteen names in
/// this order, <c>href</c> and <c>hash</c> as accessors and the seven components as data, is the
/// statement "the surviving builder is interchangeable with the one it replaces". Every other test
/// asserts what a browser does — including the three skipped ones, which fail on one fact: every
/// document shares one realm, so a frame's scripts are evaluated in the page's own global object
/// with <c>window</c>/<c>document</c>/<c>location</c>/<c>parent</c> swapped on it for the duration.
/// </para>
/// </summary>
public class FrameScriptExecutionTests
{
    private const string PageUrl = "https://example.test/frames";

    /// <summary>
    /// The frame's document, carried in <c>srcdoc</c> so no network is involved, with one probe
    /// element per fact its script records. It reports by writing into its own DOM because that is
    /// the one channel back to the page with no publishing step in it: what a frame declares is
    /// recovered by a diff of the global's names and re-published on the frame's window, while what
    /// it writes is simply there to be read. <c>#outer</c> is the page's, and the frame looks for it
    /// twice — to prove its <c>document</c> is not the page's, and to reach the page's via
    /// <c>parent</c>.
    /// </summary>
    private const string FrameHtml =
        "<html><head><title>frame</title></head><body>" +
        "<p id='ran'>no</p><p id='own'>?</p><p id='ctx'>?</p><p id='up'>?</p><p id='echo'>?</p>" +
        "<script>" +
        "var frameGlobal = 'declared-in-frame';" +
        "function frameFn() { return document.getElementById('ran').textContent; }" +
        "window.marked = 'on-window';" +
        "document.getElementById('ran').textContent = 'yes';" +
        "document.getElementById('own').textContent =" +
        " String(document.getElementById('ran') !== null) + '/' +" +
        " String(document.getElementById('outer') === null);" +
        "document.getElementById('ctx').textContent =" +
        " String(window === self) + '/' + String(window !== parent) + '/' + location.href;" +
        "document.getElementById('up').textContent =" +
        " (parent.document.getElementById('outer') ? 'parent' : 'own');" +
        "document.getElementById('echo').textContent = String(window.marked === 'on-window');" +
        "</script></body></html>";

    private const string PageHtml =
        "<html><body>" +
        "<p id=\"outer\">page</p>" +
        "<iframe id=\"f\" srcdoc=\"" + FrameHtml + "\"></iframe>" +
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
    public void AFrameIsAWindowAndADocumentWhoseTopAndParentAreThePage()
    {
        // The identity is the half a page depends on without saying so: `frames[0]` and
        // `contentWindow` have to name one object, or a page that stashes a reference from one
        // spelling and compares it against the other is holding two frames that are one frame.
        Assert.Equal(
            "win=object doc=object frames=1 identity=true parent=true top=true self=true",
            Run("""
                (function () {
                  var f = document.getElementById('f');
                  var w = f.contentWindow;
                  return 'win=' + (typeof w) +
                         ' doc=' + (typeof f.contentDocument) +
                         ' frames=' + window.frames.length +
                         ' identity=' + (window.frames[0] === w) +
                         ' parent=' + (w.parent === window) +
                         ' top=' + (w.top === window) +
                         ' self=' + (w.self === w);
                })()
                """));
    }

    [Fact]
    public void AFramesLocationCarriesEveryComponentAndTheWholeNavigationSurface()
    {
        // Members and order are what a page can tell apart, so both are named. `hash` is an
        // accessor because a fragment navigation moves it and `href` together, and the components
        // either side of it are data — the distinction a rebuild loses first. assign/replace/reload
        // absent is not a missing property but a TypeError that aborts the framed page's script.
        Assert.Equal(
            "keys=protocol|host|hostname|port|pathname|search|origin|href|hash|assign|replace|reload|toString " +
            "href=about:srcdoc protocol=about: string=about:srcdoc " +
            "hrefAccessor=function protocolData=undefined " +
            "assign=function replace=function reload=function",
            Run("""
                (function () {
                  var loc = document.getElementById('f').contentWindow.location;
                  var own = Object.getOwnPropertyDescriptor;
                  return 'keys=' + Object.keys(loc).join('|') +
                         ' href=' + loc.href + ' protocol=' + loc.protocol +
                         ' string=' + loc.toString() +
                         ' hrefAccessor=' + (typeof own(loc, 'href').set) +
                         ' protocolData=' + (typeof own(loc, 'protocol').set) +
                         ' assign=' + (typeof loc.assign) +
                         ' replace=' + (typeof loc.replace) +
                         ' reload=' + (typeof loc.reload);
                })()
                """));
    }

    [Fact]
    public void AFramesInlineScriptRunsInTheFramesContextAndGivesThePageItsOwnBack()
    {
        // `ran` says the script ran at all; `own` says its `document` was the frame's; `ctx` says
        // `window`/`self`/`location` were too and `parent` was not. `back` is the other half of the
        // same switch and the one nothing else would notice: the page's globals are saved and
        // restored around a frame's scripts, so a page that touches a frame mid-script still finds
        // its own document and its own URL afterwards.
        Assert.Equal(
            "ran=yes own=true/true ctx=true/true/about:srcdoc back=true/" + PageUrl,
            Run("""
                (function () {
                  var d = document.getElementById('f').contentDocument;
                  return 'ran=' + d.getElementById('ran').textContent +
                         ' own=' + d.getElementById('own').textContent +
                         ' ctx=' + d.getElementById('ctx').textContent +
                         ' back=' + (document.getElementById('outer') !== null) + '/' + location.href;
                })()
                """));
    }

    [Fact]
    public void AGlobalTheFramesScriptDeclaredIsReachableOnTheFramesWindow()
    {
        // `frames[0].foo()` is how a page drives a frame it controls, and a declaration that never
        // reaches the frame's window reads `undefined` there though the function exists and ran.
        // Calling it is the second half: a promoted function has to re-enter its own frame's
        // context, or one that means "my document" silently operates on the page's.
        Assert.Equal(
            "value=declared-in-frame fn=function called=yes",
            Run("""
                (function () {
                  var w = window.frames[0];
                  return 'value=' + w.frameGlobal + ' fn=' + (typeof w.frameFn) +
                         ' called=' + w.frameFn();
                })()
                """));
    }

    [Fact(Skip = "A frame's scripts are evaluated in the containing page's global object, and under " +
                 "this engine that object IS the page's window, so a frame's top-level var/function " +
                 "becomes a property of the page's own window as well as of the frame's: " +
                 "frames[0].frameGlobal and the page's unqualified frameGlobal are one binding, and " +
                 "two frames declaring the same name overwrite each other. HTML gives every nested " +
                 "browsing context a global object of its own. " +
                 "src/Broiler.HtmlBridge.Dom/DomBridge/SubDocuments.cs:372")]
    public void AFramesDeclarationsDoNotLandOnTheContainingPagesWindow()
    {
        Assert.Equal(
            "frame=declared-in-frame onPage=false bare=undefined",
            Run("""
                (function () {
                  var w = document.getElementById('f').contentWindow;
                  return 'frame=' + w.frameGlobal + ' onPage=' + ('frameGlobal' in window) +
                         ' bare=' + (typeof frameGlobal);
                })()
                """));
    }

    [Fact(Skip = "The sub-document script runner asks for the frame's window before the builder " +
                 "asking for the sub-document has cached one, so the scripts run against a " +
                 "throwaway window the outer build then replaces in the cache. Only names landing " +
                 "on the global object survive, recovered by the diff in " +
                 "DomBridge.SubDocumentGlobals.cs, so `window.x = …` inside a frame is written to " +
                 "an object nothing keeps: frames[0].marked is undefined while the frame's own echo " +
                 "proves the assignment happened. Which of the two windows survives also depends on " +
                 "whether the page reads contentDocument or contentWindow first. " +
                 "src/Broiler.HtmlBridge.Dom/DomBridge/SubDocuments.cs:358")]
    public void APropertyTheFramesScriptPutsOnItsWindowIsOnTheWindowThePageSees()
    {
        Assert.Equal(
            "marked=on-window echo=true",
            Run("""
                (function () {
                  var w = document.getElementById('f').contentWindow;
                  return 'marked=' + w.marked +
                         ' echo=' + w.document.getElementById('echo').textContent;
                })()
                """));
    }

    [Fact(Skip = "The window-context switch writes the frame's window/document/location/parent onto " +
                 "the realm's global object, and that object IS the containing page's window — " +
                 "which is what `parent` names from inside the frame. So for as long as a frame's " +
                 "script runs, parent.document is the frame's own document and parent.window its " +
                 "own window: a same-origin frame reading or writing its embedder's DOM silently " +
                 "operates on itself, and finds nothing rather than throwing. " +
                 "src/Broiler.HtmlBridge.Dom/Runtime/WindowContextManager.cs:166")]
    public void AFramesScriptReachesTheContainingPagesDomThroughParent()
    {
        Assert.Equal(
            "parent",
            Run("document.getElementById('f').contentDocument.getElementById('up').textContent"));
    }
}
