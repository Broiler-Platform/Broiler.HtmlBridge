using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// The node-tree relationships every wrapper answers — <c>getRootNode</c>, <c>contains</c>,
/// <c>compareDocumentPosition</c>, <c>parentNode</c>/<c>parentElement</c> — and the wrapper identity all
/// four are read through, asserted from page script.
/// <para>
/// One DOM node has exactly one JavaScript object for the life of a document, and the tables that
/// guarantee it are the engine-typed ones: a node→wrapper dictionary and a weakly-keyed wrapper→node
/// table. Every operation here reaches one or both — <c>contains</c> and <c>compareDocumentPosition</c>
/// resolve their argument through the reverse map, <c>getRootNode</c> and <c>parentNode</c> mint their
/// answer through the forward one. Re-typing that storage is what these tests are written ahead of, and
/// the failure mode is silent: a wrapper that stops holding does not throw, it hands back a second object
/// that is <c>!==</c> the first, so every identity comparison a page makes goes quietly false.
/// </para>
/// <para>
/// <b>The named position constants are why the compare test spells them out rather than the numbers.</b>
/// A correct bitmask is worth nothing to a page that cannot decode it: with the constants absent,
/// <c>result &amp; Node.DOCUMENT_POSITION_CONTAINED_BY</c> is <c>result &amp; undefined</c> — which is
/// <c>0</c>, not an error, so a containment test answers "no" for every pair of nodes and still reads as
/// a working feature. Asserting through the constants is the only spelling that fails when they go.
/// </para>
/// </summary>
public class NodeTreeIdentityTests
{
    private const string PageUrl = "https://example.test/node-tree";

    /// <summary>
    /// No whitespace between the tags, so a <c>childNodes</c> count below is the element count rather
    /// than a count of interleaved text nodes.
    /// </summary>
    private const string PageHtml =
        "<html><body>" +
        "<div id=\"host\"><span id=\"a\"></span><span id=\"b\"></span></div>" +
        "<div id=\"from\"><div id=\"mover\"></div></div><div id=\"to\"></div>" +
        "<div id=\"openHost\"></div><div id=\"closedHost\"></div>" +
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
    public void TheDocumentIsTheRootAndTheParentOfTheElementBelowIt()
    {
        // The document object is built rather than minted as a node wrapper, so it is the one wrapper
        // whose registry entry is written by hand. Lose that entry and every line here changes at once.
        Assert.Equal(
            "bodyRoot=true rootElementRoot=true documentRoot=true documentParent=null " +
            "rootParentNode=true rootParentElement=null bodyParent=true",
            Run("""
                (function () {
                  var html = document.documentElement;
                  return 'bodyRoot=' + (document.body.getRootNode() === document) +
                         ' rootElementRoot=' + (html.getRootNode() === document) +
                         ' documentRoot=' + (document.getRootNode() === document) +
                         ' documentParent=' + document.parentNode +
                         ' rootParentNode=' + (html.parentNode === document) +
                         ' rootParentElement=' + html.parentElement +
                         ' bodyParent=' + (document.body.parentElement === html);
                })()
                """));
    }

    [Fact]
    public void ADetachedSubtreeRootsToItsOwnTopUntilItIsInserted()
    {
        // DOM §4.4 walks to the topmost node of the node's *own* tree, which for anything outside the
        // document is that subtree's root — not the document that created it. Answering the document is
        // the plausible wrong answer, and the one a page's "am I in the page yet?" guard cannot survive.
        Assert.Equal(
            "detachedSelf=true kidRoot=true kidNotDocument=true fragRoot=true fragHolds=true " +
            "connectedBefore=false rootAfter=true connectedAfter=true",
            Run("""
                (function () {
                  var wrap = document.createElement('div'), kid = document.createElement('span');
                  wrap.appendChild(kid);
                  var frag = document.createDocumentFragment(), pooled = document.createElement('i');
                  frag.appendChild(pooled);
                  var before = 'detachedSelf=' + (wrap.getRootNode() === wrap) +
                               ' kidRoot=' + (kid.getRootNode() === wrap) +
                               ' kidNotDocument=' + (kid.getRootNode() !== document) +
                               ' fragRoot=' + (pooled.getRootNode() === frag) +
                               ' fragHolds=' + frag.contains(pooled) +
                               ' connectedBefore=' + kid.isConnected;
                  document.getElementById('to').appendChild(wrap);
                  return before + ' rootAfter=' + (kid.getRootNode() === document) +
                         ' connectedAfter=' + kid.isConnected;
                })()
                """));
    }

    [Fact]
    public void ContainsIsInclusiveAndOnlyEverLooksDownward()
    {
        // contains resolves its argument back to a node through the wrapper→node map, and its answer for
        // "no such node" is `false` — so a broken reverse map reads exactly like a node genuinely
        // elsewhere in the page, the one wrong answer no caller can tell from a right one.
        Assert.Equal(
            "self=true child=true upward=false documentBody=true documentSelf=true " +
            "documentDetached=false nullArgument=false",
            Run("""
                (function () {
                  var host = document.getElementById('host'), a = document.getElementById('a');
                  return 'self=' + a.contains(a) + ' child=' + host.contains(a) +
                         ' upward=' + a.contains(host) +
                         ' documentBody=' + document.contains(document.body) +
                         ' documentSelf=' + document.contains(document) +
                         ' documentDetached=' + document.contains(document.createElement('div')) +
                         ' nullArgument=' + a.contains(null);
                })()
                """));
    }

    [Fact]
    public void ComparingTwoNodesSetsTheBitsTheNodeConstantsName()
    {
        // Every bit is read through its constant, on the instance as well as on the Node global: a page
        // writes `result & node.DOCUMENT_POSITION_CONTAINED_BY`, and that spelling depends on the
        // constants reaching an element through its prototype chain, not only on the number returned.
        Assert.Equal(
            "containedBy=true downFollows=true downNotContains=true contains=true upPrecedes=true " +
            "sibFollowing=true sibPreceding=true self=0 instanceConstant=true",
            Run("""
                (function () {
                  var host = document.getElementById('host');
                  var a = document.getElementById('a'), b = document.getElementById('b');
                  var down = host.compareDocumentPosition(a), up = a.compareDocumentPosition(host);
                  return 'containedBy=' + ((down & Node.DOCUMENT_POSITION_CONTAINED_BY) !== 0) +
                         ' downFollows=' + ((down & Node.DOCUMENT_POSITION_FOLLOWING) !== 0) +
                         ' downNotContains=' + ((down & Node.DOCUMENT_POSITION_CONTAINS) === 0) +
                         ' contains=' + ((up & Node.DOCUMENT_POSITION_CONTAINS) !== 0) +
                         ' upPrecedes=' + ((up & Node.DOCUMENT_POSITION_PRECEDING) !== 0) +
                         ' sibFollowing=' + (a.compareDocumentPosition(b) === Node.DOCUMENT_POSITION_FOLLOWING) +
                         ' sibPreceding=' + (b.compareDocumentPosition(a) === Node.DOCUMENT_POSITION_PRECEDING) +
                         ' self=' + a.compareDocumentPosition(a) +
                         ' instanceConstant=' + (a.DOCUMENT_POSITION_CONTAINED_BY === Node.DOCUMENT_POSITION_CONTAINED_BY);
                })()
                """));
    }

    [Fact(Skip = "compareDocumentPosition answers a bare DOCUMENT_POSITION_DISCONNECTED for nodes in " +
                 "different trees; DOM §4.4 requires DISCONNECTED | IMPLEMENTATION_SPECIFIC plus a " +
                 "PRECEDING/FOLLOWING that reverses between the two orders, so a page sorting " +
                 "disconnected nodes is handed no ordering at all. " +
                 "src/Broiler.HtmlBridge.Dom/Features/NodeRelationshipsBinding.cs:53")]
    public void TwoDisconnectedNodesCompareWithAnImplementationBitAndAStableDirection()
    {
        Assert.Equal(
            "disconnected=true implementationSpecific=true oneDirection=true reversed=true",
            Run("""
                (function () {
                  var x = document.createElement('div'), y = document.createElement('div');
                  var forward = x.compareDocumentPosition(y), backward = y.compareDocumentPosition(x);
                  var both = Node.DOCUMENT_POSITION_PRECEDING | Node.DOCUMENT_POSITION_FOLLOWING;
                  function oneWay(bits) {
                    return ((bits & Node.DOCUMENT_POSITION_PRECEDING) !== 0) !==
                           ((bits & Node.DOCUMENT_POSITION_FOLLOWING) !== 0);
                  }
                  return 'disconnected=' + ((forward & Node.DOCUMENT_POSITION_DISCONNECTED) !== 0) +
                         ' implementationSpecific=' +
                             ((forward & Node.DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC) !== 0) +
                         ' oneDirection=' + (oneWay(forward) && oneWay(backward)) +
                         ' reversed=' + (((forward ^ backward) & both) === both);
                })()
                """));
    }

    [Fact]
    public void AMovedNodeKeepsTheWrapperThePageIsAlreadyHolding()
    {
        // A move is a remove followed by an insert, and the remove is where a wrapper gets dropped. If it
        // is, the page's reference and the next query's are two objects: the expando is gone, `===` is
        // false, and every listener and Map key the page holds addresses an object the DOM has forgotten.
        Assert.Equal(
            "identity=true expando=kept parentNode=true parentElement=true oldContains=false " +
            "newContains=true oldChildren=0 newChildren=1",
            Run("""
                (function () {
                  var from = document.getElementById('from'), to = document.getElementById('to');
                  var held = document.getElementById('mover');
                  held.__probe = 'kept';
                  to.appendChild(held);
                  var found = document.getElementById('mover');
                  return 'identity=' + (found === held) + ' expando=' + found.__probe +
                         ' parentNode=' + (found.parentNode === to) +
                         ' parentElement=' + (found.parentElement === to) +
                         ' oldContains=' + from.contains(found) + ' newContains=' + to.contains(found) +
                         ' oldChildren=' + from.childNodes.length +
                         ' newChildren=' + to.childNodes.length;
                })()
                """));
    }

    [Fact]
    public void AShadowTreeRootsToItsShadowRootUntilCompositionIsAskedFor()
    {
        // getRootNode is the one relationship here with a second answer, and `composed` selects it. A
        // closed root is still a root — the mode hides it from `host.shadowRoot`, not from the node
        // standing in it — which is the arm that fails first if the two paths are ever merged.
        Assert.Equal(
            "rootIdentity=true innerRoot=true rootOfRoot=true composed=true hostRoot=true " +
            "closedHidden=true closedInnerRoot=true",
            Run("""
                (function () {
                  var host = document.getElementById('openHost');
                  var root = host.attachShadow({ mode: 'open' });
                  var inner = document.createElement('span');
                  root.appendChild(inner);
                  var closedHost = document.getElementById('closedHost');
                  var closedRoot = closedHost.attachShadow({ mode: 'closed' });
                  var closedInner = document.createElement('span');
                  closedRoot.appendChild(closedInner);
                  return 'rootIdentity=' + (root === host.shadowRoot) +
                         ' innerRoot=' + (inner.getRootNode() === root) +
                         ' rootOfRoot=' + (root.getRootNode() === root) +
                         ' composed=' + (inner.getRootNode({ composed: true }) === document) +
                         ' hostRoot=' + (host.getRootNode() === document) +
                         ' closedHidden=' + (closedHost.shadowRoot === null) +
                         ' closedInnerRoot=' + (closedInner.getRootNode() === closedRoot);
                })()
                """));
    }

    [Fact(Skip = "A shadow root is a #shadow-root element parented into its host, so the shadow tree " +
                 "is part of the host's node tree: document.contains and host.childNodes both reach " +
                 "into it and the root reports a parent, where DOM §4.2.2 gives a shadow root a tree " +
                 "of its own with no parent — which is what makes the composed flag mean anything. " +
                 "src/Broiler.HtmlBridge.Dom/DomBridge/DomBridge.ShadowDomHost.cs:40")]
    public void AShadowTreeIsNotPartOfItsHostsNodeTree()
    {
        Assert.Equal(
            "documentContains=false hostContains=false hostChildren=0 rootParent=null",
            Run("""
                (function () {
                  var host = document.getElementById('openHost');
                  var root = host.attachShadow({ mode: 'open' });
                  root.appendChild(document.createElement('span'));
                  return 'documentContains=' + document.contains(root) +
                         ' hostContains=' + host.contains(root) +
                         ' hostChildren=' + host.childNodes.length +
                         ' rootParent=' + root.parentNode;
                })()
                """));
    }
}
