using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// An <c>&lt;iframe&gt;</c>'s <c>contentDocument</c> as a DOM of its own — what it finds, what the
/// containing page must not find, and the wrapper identity inside it — asserted from the top-level
/// script.
/// <para>
/// This suite had never loaded a frame at all: a search for <c>iframe</c> across it returned nothing
/// before this file. That gap is what the pending sweep commits are blocked on — each moves frame code
/// while claiming to change no frame behaviour, and "no behaviour changed" is a claim only a test can
/// make. It also covers <c>BuildDocument</c>, the sub-document builder an earlier sweep moved onto the
/// realm with no test standing over it: every <c>contentDocument</c> read below is its output.
/// </para>
/// <para>
/// <b>The identity assertions are the ones the sweep actually needs.</b> A frame's elements come from
/// the same node→wrapper registry the page's do, keyed on the node's reference identity, and the
/// frame's document object is registered by hand as its root's wrapper. Nothing fails loudly when that
/// stops holding: a lookup that mints a second object throws nothing and serializes identically — it
/// just makes <c>===</c> answer false and drops the expando the page wrote. The
/// same-<c>id</c>-in-both-trees test is the other half, and a registry keyed on anything looser than
/// the node passes every other line here.
/// </para>
/// <para>
/// Every assertion is what a browser answers, not what this bridge happens to do; the one place they
/// differ is the skipped test, which spells the browser's answer and names the defect.
/// </para>
/// </summary>
public class FrameDocumentTests
{
    private const string PageUrl = "https://example.test/frames";

    /// <summary>
    /// A <c>srcdoc</c> frame, so the fixture needs no network, and its inner attributes are
    /// single-quoted so the double-quoted <c>srcdoc</c> value survives. No whitespace between the
    /// frame's body children, so a child index inside it is the element count rather than a count of
    /// interleaved text nodes. <c>#shared</c> exists twice on purpose — a <c>&lt;div&gt;</c> in the
    /// page, a <c>&lt;span&gt;</c> in the frame — so the two are told apart by tag name rather than by
    /// the identity comparison under test. The frame's <c>&lt;script&gt;</c> is what routes its
    /// construction through the sub-window and the window-context swap; a script-less frame skips
    /// both, and the write is idempotent so running it twice would not change the answer.
    /// </summary>
    private const string PageHtml =
        "<html><body>" +
        "<div id=\"shared\">page</div>" +
        "<iframe id=\"f\" srcdoc=\"<html><body><p id='inner'>hi</p>" +
        "<span id='shared'>frame</span>" +
        "<script>document.body.setAttribute('data-ran','yes');</script>" +
        "</body></html>\"></iframe>" +
        "<div id=\"out\"></div>" +
        "</body></html>";

    /// <summary>
    /// Runs <paramref name="script"/> against the fixture document and returns what it wrote to
    /// <c>#out</c>. Reading the result out of the serialized DOM keeps the test to the engine's public
    /// surface — the bindings under test are internal, and reaching for them directly would pin their
    /// shape rather than their behaviour, which is what these commits are about to change.
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
    public void TheFramesDocumentFindsItsOwnElementsAndThePageDoesNot()
    {
        // Two documents, two searches. A frame's content is a separate tree rather than a subtree of
        // the page's, so the page's lookups stop at the frame's boundary and the frame's reach the
        // tree its own resource was parsed into. Answering the page's document for both reads as a
        // working feature until the first frame whose markup collides with its embedder's.
        Assert.Equal(
            "frameFinds=P text=hi pageFinds=null pageQuery=null framePs=1 pagePs=0 owner=true root=true",
            Run("""
                (function () {
                  var d = document.getElementById('f').contentDocument;
                  var inner = d.getElementById('inner');
                  return 'frameFinds=' + inner.tagName +
                         ' text=' + inner.textContent +
                         ' pageFinds=' + document.getElementById('inner') +
                         ' pageQuery=' + document.querySelector('#inner') +
                         ' framePs=' + d.getElementsByTagName('p').length +
                         ' pagePs=' + document.getElementsByTagName('p').length +
                         ' owner=' + (inner.ownerDocument === d) +
                         ' root=' + (inner.getRootNode() === d);
                })()
                """));
    }

    [Fact]
    public void OneElementInTheFrameIsOneObjectHoweverItIsReached()
    {
        // Five bindings hand this paragraph back, all of them through the one registry. A route that
        // mints its own wrapper instead loses the expando and breaks the `===` a page writes its
        // guards with, while changing nothing a serialization or an existing test can see.
        Assert.Equal(
            "identity=true expando=kept query=true byTag=true all=true parent=true documentStable=true",
            Run("""
                (function () {
                  var d = document.getElementById('f').contentDocument;
                  var held = d.getElementById('inner');
                  held.__probe = 'kept';
                  var again = d.getElementById('inner');
                  return 'identity=' + (held === again) +
                         ' expando=' + again.__probe +
                         ' query=' + (d.querySelector('#inner') === held) +
                         ' byTag=' + (d.getElementsByTagName('p')[0] === held) +
                         ' all=' + (d.querySelectorAll('p')[0] === held) +
                         ' parent=' + (held.parentNode === d.body) +
                         ' documentStable=' + (document.getElementById('f').contentDocument === d);
                })()
                """));
    }

    [Fact]
    public void TheFramesBodyAndRootAreItsOwnAndItsDocumentIsNotThePages()
    {
        // The document object is built rather than minted as a node wrapper, so its registry entry is
        // written by hand — and `documentElement.parentNode` is the line that reads it back. Lose it
        // and the frame's root reports a parent that is not the frame's document, which is how a
        // page's "which document am I in?" walk arrives at the wrong one.
        Assert.Equal(
            "notPage=true nodeType=9 nodeName=#document root=HTML body=BODY bodyStable=true " +
            "bodyParent=true rootParent=true notPageBody=true notPageRoot=true",
            Run("""
                (function () {
                  var d = document.getElementById('f').contentDocument;
                  return 'notPage=' + (d !== document) +
                         ' nodeType=' + d.nodeType +
                         ' nodeName=' + d.nodeName +
                         ' root=' + d.documentElement.tagName +
                         ' body=' + d.body.tagName +
                         ' bodyStable=' + (d.body === d.body) +
                         ' bodyParent=' + (d.body.parentNode === d.documentElement) +
                         ' rootParent=' + (d.documentElement.parentNode === d) +
                         ' notPageBody=' + (d.body !== document.body) +
                         ' notPageRoot=' + (d.documentElement !== document.documentElement);
                })()
                """));
    }

    [Fact]
    public void TwoElementsSharingAnIdInTwoDocumentsAreTwoObjects()
    {
        // The assertion that catches a registry keyed too loosely. An id is unique within a document
        // and says nothing across two, so a cache that keys on one hands the page and the frame a
        // single object — and every other test here still passes when it does.
        Assert.Equal(
            "page=DIV frame=SPAN distinct=true noLeak=undefined pageOwner=true frameOwner=true " +
            "pageHolds=true pageDoesNotHold=false",
            Run("""
                (function () {
                  var d = document.getElementById('f').contentDocument;
                  var pageOne = document.getElementById('shared');
                  var frameOne = d.getElementById('shared');
                  pageOne.__probe = 'page';
                  return 'page=' + pageOne.tagName +
                         ' frame=' + frameOne.tagName +
                         ' distinct=' + (pageOne !== frameOne) +
                         ' noLeak=' + frameOne.__probe +
                         ' pageOwner=' + (pageOne.ownerDocument === document) +
                         ' frameOwner=' + (frameOne.ownerDocument === d) +
                         ' pageHolds=' + document.contains(pageOne) +
                         ' pageDoesNotHold=' + document.contains(frameOne);
                })()
                """));
    }

    [Fact]
    public void AScriptInTheFrameWritesToTheFramesDocumentAndNotThePages()
    {
        // The frame's scripts run in the one shared context, so the only thing making their bare
        // `document` the frame's own is the window-context swap the sub-document script runner wraps
        // them in — and nothing has ever asserted it. With that swap inert, `document.body` is the
        // embedding page's: no throw, no diagnostic, just a frame quietly writing into its parent.
        // It is also the one assertion here that needs the frame's *window* built at all.
        Assert.Equal(
            "frameHas=true frameValue=yes pageHas=false distinctBodies=true",
            Run("""
                (function () {
                  var d = document.getElementById('f').contentDocument;
                  return 'frameHas=' + d.body.hasAttribute('data-ran') +
                         ' frameValue=' + d.body.getAttribute('data-ran') +
                         ' pageHas=' + document.body.hasAttribute('data-ran') +
                         ' distinctBodies=' + (d.body !== document.body);
                })()
                """));
    }

    [Fact(Skip = "isConnected compares a node's tree root against the MAIN document, so every node in " +
                 "a frame's document answers false — its own body and documentElement included. " +
                 "DOM §4.4 makes a node connected when its shadow-including root is a document, and a " +
                 "frame's document is one, so a framed script's standard \"am I in the page yet?\" " +
                 "guard never opens. " +
                 "src/Broiler.HtmlBridge.Dom/Features/NodeAccessorsBinding.cs:29")]
    public void EveryNodeInTheFramesDocumentIsConnected()
    {
        Assert.Equal(
            "inner=true body=true root=true",
            Run("""
                (function () {
                  var d = document.getElementById('f').contentDocument;
                  return 'inner=' + d.getElementById('inner').isConnected +
                         ' body=' + d.body.isConnected +
                         ' root=' + d.documentElement.isConnected;
                })()
                """));
    }
}
