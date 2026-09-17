using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The node-relationship members roadmap D3 moves onto <c>Broiler.Dom</c> — <c>compareDocumentPosition</c>,
/// <c>textContent</c> read and write, and the element-traversal views (<c>children</c>,
/// <c>firstElementChild</c>, <c>lastElementChild</c>, <c>childElementCount</c>,
/// <c>previousElementSibling</c>, <c>nextElementSibling</c>) — asserted from page script, written before
/// the bridge's own copies of those algorithms were swapped for the canonical <c>DomNode</c> members.
/// <para>
/// <b>Two kinds of test live here, and the name says which.</b> Most assert what the DOM Standard and
/// Chromium answer: a node in a fragment is disconnected from the document with a direction, a fragment
/// has the text of its descendants, an element in a fragment has element siblings. Several of those
/// failed against the bridge's own copies, which is why they were written first — the bridge computed
/// each of them its own way (a bare <c>DISCONNECTED</c>, a fragment treated as textless, siblings found
/// only under an element parent), and the canonical member answers the standard. The rest are named
/// <c>Characterization_</c> and pin what the bridge delivers today where that is known to differ from
/// Chromium, so a change to it is a decision someone makes rather than a side effect; the ones the cutover
/// did change say what changed. Each says what Chromium does instead.
/// </para>
/// <para>
/// <b>Every bit is read through the <c>Node.DOCUMENT_POSITION_*</c> constants,</b> for the reason
/// <see cref="NodeTreeIdentityTests"/> gives: with the constants absent, <c>result &amp; undefined</c> is
/// <c>0</c> rather than an error, and a test spelled with numbers keeps passing against a page that can no
/// longer decode the answer.
/// </para>
/// <para>
/// The <c>textContent</c> half of the surface, the mutation records a write delivers and the bindings that
/// read or write an element's text on script's behalf are in <c>NodeRelationshipCanonicalTests.TextContent.cs</c>.
/// </para>
/// </summary>
public partial class NodeRelationshipCanonicalTests
{
    private const string PageUrl = "https://example.test/node-relationships";

    /// <summary>
    /// No whitespace between tags, so every child index below is the markup's own. <c>#tree</c> gives
    /// siblings and cousins; <c>#mixed</c> interleaves text, comments and elements, with a text node nested
    /// two elements down so descendant text is distinguishable from child text; <c>#three</c> and
    /// <c>#swap</c> are the targets of the mutation-record tests and are observed nowhere else.
    /// </summary>
    private const string PageHtml =
        "<!DOCTYPE html><html><head><title>t</title></head><body>" +
        "<div id=\"tree\"><div id=\"p1\"><span id=\"c1\"></span><span id=\"c2\"></span></div>" +
        "<div id=\"p2\"><span id=\"c3\"></span></div></div>" +
        "<div id=\"mixed\">lead<!--note--><b id=\"m1\">bold</b>mid<i id=\"m2\">it<u>al</u></i><!--tail-->end</div>" +
        "<div id=\"three\"><span>1</span><span>2</span><span>3</span></div>" +
        "<div id=\"swap\"><b>a</b><b>b</b><b>c</b></div>" +
        "<div id=\"empty\"></div>" +
        "<div id=\"host\"></div>" +
        "<select id=\"sel\"><option id=\"o1\">First</option><option id=\"o2\" value=\"v2\">Second</option></select>" +
        "<textarea id=\"ta\">orig</textarea>" +
        "<svg><text id=\"svgText\">hello<tspan>!!</tspan></text></svg>" +
        "<div id=\"out\"></div>" +
        "</body></html>";

    /// <summary>
    /// <see cref="PageHtml"/> with a <c>srcdoc</c> frame in front of <c>#out</c>, for the tests that need a
    /// second document. Kept out of the main fixture so every other test does not build a frame it never
    /// reads. The frame's attributes are single-quoted so the double-quoted <c>srcdoc</c> survives.
    /// </summary>
    private static readonly string FramePageHtml = PageHtml.Replace(
        "<div id=\"out\">",
        "<iframe id=\"f\" srcdoc=\"<html><head><title>inner</title></head><body><p id='fp'>x</p></body></html>\">" +
        "</iframe><div id=\"out\">",
        StringComparison.Ordinal);

    /// <summary>
    /// Page-global helpers the scripts below share, run as their own script ahead of the probe.
    /// <c>describeNode</c> names a node without markup characters (the answer is read back out of the
    /// serialized DOM, where <c>&lt;</c> would arrive escaped); <c>describeRecord</c> spells one
    /// <c>MutationRecord</c> as its added and removed nodes and its two siblings; <c>describeDisconnected</c>
    /// is the DOM §4.4 contract for two nodes in different trees, both ways round and asked twice.
    /// </summary>
    private const string Helpers = """
        function describeNode(node) {
          if (node === null) return 'null';
          if (node === undefined) return 'undefined';
          if (node.nodeType === 3) return '#text:' + node.data;
          if (node.nodeType === 8) return '#comment:' + node.data;
          return node.id ? node.nodeName + '#' + node.id : node.nodeName + ':' + node.textContent;
        }
        function describeList(list) {
          var names = [];
          for (var i = 0; i < list.length; i++) names.push(describeNode(list[i]));
          return names.join('|');
        }
        function describeRecord(record) {
          return '{' + record.type + ' added=' + describeList(record.addedNodes) +
                 ' removed=' + describeList(record.removedNodes) +
                 ' prev=' + describeNode(record.previousSibling) +
                 ' next=' + describeNode(record.nextSibling) + '}';
        }
        function describeDisconnected(x, y) {
          var forward = x.compareDocumentPosition(y), backward = y.compareDocumentPosition(x);
          var marks = Node.DOCUMENT_POSITION_DISCONNECTED | Node.DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC;
          var both = Node.DOCUMENT_POSITION_PRECEDING | Node.DOCUMENT_POSITION_FOLLOWING;
          function oneWay(bits) {
            return ((bits & Node.DOCUMENT_POSITION_PRECEDING) !== 0) !==
                   ((bits & Node.DOCUMENT_POSITION_FOLLOWING) !== 0);
          }
          return 'marked=' + ((forward & marks) === marks && (backward & marks) === marks) +
                 ' noContainment=' + (((forward | backward) & ~(marks | both)) === 0) +
                 ' oneDirection=' + (oneWay(forward) && oneWay(backward)) +
                 ' reversed=' + (((forward ^ backward) & both) === both) +
                 ' stable=' + (x.compareDocumentPosition(y) === forward && y.compareDocumentPosition(x) === backward);
        }
        """;

    /// <summary>
    /// Runs <paramref name="script"/> against <paramref name="pageHtml"/> (the main fixture by default) and
    /// returns what it wrote to <c>#out</c>, as <see cref="NodeTreeIdentityTests"/> does. A throw is
    /// written there too: a member that is missing or throws would otherwise leave <c>#out</c> empty,
    /// which reports "the script died" with no word of where.
    /// </summary>
    private static string Run(string script, string? pageHtml = null)
    {
        var html = new ScriptEngine().Execute(
            [
                Helpers,
                "var probeResult;" +
                $"try {{ probeResult = String({script}); }} " +
                "catch (e) { probeResult = 'threw ' + (e && e.name) + ': ' + (e && e.message); }" +
                "document.getElementById('out').textContent = probeResult;",
            ],
            pageHtml ?? PageHtml,
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

    // ── compareDocumentPosition ────────────────────────────────────────────────────────────────

    [Fact]
    public void NodesInOneTreeCompareByContainmentAndThenTreeOrder()
    {
        // The in-tree half of DOM §4.4, which the bridge used to answer from its own ancestor walks plus a
        // boundary-point comparison and the canonical member answers in one pass. Cousins are the case
        // that reaches the tree-order comparison at all — siblings share a parent, and every other pair
        // here is decided by containment first.
        Assert.Equal(
            "self=true ancestor=true descendant=true following=true preceding=true cousinFollowing=true " +
            "cousinPreceding=true documentDescendant=true documentAncestor=true textBeforeElement=true " +
            "elementAfterText=true",
            Run("""
                (function () {
                  function $(id) { return document.getElementById(id); }
                  var N = Node, tree = $('tree'), c1 = $('c1'), c2 = $('c2'), c3 = $('c3');
                  var lead = $('mixed').firstChild, bold = $('m1');
                  return 'self=' + (c1.compareDocumentPosition(c1) === 0) +
                         ' ancestor=' + (c1.compareDocumentPosition(tree) ===
                             (N.DOCUMENT_POSITION_CONTAINS | N.DOCUMENT_POSITION_PRECEDING)) +
                         ' descendant=' + (tree.compareDocumentPosition(c1) ===
                             (N.DOCUMENT_POSITION_CONTAINED_BY | N.DOCUMENT_POSITION_FOLLOWING)) +
                         ' following=' + (c1.compareDocumentPosition(c2) === N.DOCUMENT_POSITION_FOLLOWING) +
                         ' preceding=' + (c2.compareDocumentPosition(c1) === N.DOCUMENT_POSITION_PRECEDING) +
                         ' cousinFollowing=' + (c1.compareDocumentPosition(c3) === N.DOCUMENT_POSITION_FOLLOWING) +
                         ' cousinPreceding=' + (c3.compareDocumentPosition(c1) === N.DOCUMENT_POSITION_PRECEDING) +
                         ' documentDescendant=' + (document.compareDocumentPosition(c1) ===
                             (N.DOCUMENT_POSITION_CONTAINED_BY | N.DOCUMENT_POSITION_FOLLOWING)) +
                         ' documentAncestor=' + (c1.compareDocumentPosition(document) ===
                             (N.DOCUMENT_POSITION_CONTAINS | N.DOCUMENT_POSITION_PRECEDING)) +
                         ' textBeforeElement=' + (lead.compareDocumentPosition(bold) === N.DOCUMENT_POSITION_FOLLOWING) +
                         ' elementAfterText=' + (bold.compareDocumentPosition(lead) === N.DOCUMENT_POSITION_PRECEDING);
                })()
                """));
    }

    [Fact]
    public void ANodeInAFragmentIsDisconnectedFromTheDocumentWithAStableDirection()
    {
        // DOM §4.4: nodes that do not share a root answer DISCONNECTED | IMPLEMENTATION_SPECIFIC and
        // exactly one of PRECEDING/FOLLOWING, and that one must reverse when the arguments do and hold
        // while both trees live — it is what lets a page sort nodes from two trees at all. The bridge
        // answered a bare DISCONNECTED both ways until compareDocumentPosition became the canonical
        // member. Inside the fragment the tree is an ordinary one, which the last field holds, so the fix
        // cannot be "call everything outside the document disconnected".
        Assert.Equal(
            "marked=true noContainment=true oneDirection=true reversed=true stable=true fragmentHoldsChild=true",
            Run("""
                (function () {
                  var fragment = document.createDocumentFragment(), pooled = document.createElement('i');
                  fragment.appendChild(pooled);
                  return describeDisconnected(pooled, document.getElementById('c1')) +
                         ' fragmentHoldsChild=' + (fragment.compareDocumentPosition(pooled) ===
                             (Node.DOCUMENT_POSITION_CONTAINED_BY | Node.DOCUMENT_POSITION_FOLLOWING));
                })()
                """));
    }

    [Fact]
    public void ACreatedButUnattachedElementIsDisconnectedFromTheDocumentWithAStableDirection()
    {
        // The same contract for the commonest disconnected node a page holds: one it has created and not
        // yet inserted, whose root is itself.
        Assert.Equal(
            "marked=true noContainment=true oneDirection=true reversed=true stable=true",
            Run("""
                (function () {
                  return describeDisconnected(document.createElement('div'), document.getElementById('c1'));
                })()
                """));
    }

    [Fact]
    public void Characterization_AShadowTreeNodeComparesAsItsHostsDescendant()
    {
        // Chromium treats a shadow tree as a tree of its own here: a node inside it and its host answer
        // DISCONNECTED | IMPLEMENTATION_SPECIFIC plus a direction, as any two nodes with different roots
        // do. The bridge parents a shadow root into its host (NodeTreeIdentityTests'
        // AShadowTreeIsNotPartOfItsHostsNodeTree is the skipped test for that), so the node is the host's
        // descendant to every walk up the parent chain — the bridge's and the canonical member's alike.
        // This pins today's containment answer so the cutover leaves it as it found it; it is not
        // Chromium's.
        Assert.Equal(
            "innerToHost=contains|preceding hostToInner=containedBy|following rootToHost=contains|preceding",
            Run("""
                (function () {
                  function bits(value) {
                    var names = [];
                    if (value & Node.DOCUMENT_POSITION_DISCONNECTED) names.push('disconnected');
                    if (value & Node.DOCUMENT_POSITION_CONTAINS) names.push('contains');
                    if (value & Node.DOCUMENT_POSITION_CONTAINED_BY) names.push('containedBy');
                    if (value & Node.DOCUMENT_POSITION_PRECEDING) names.push('preceding');
                    if (value & Node.DOCUMENT_POSITION_FOLLOWING) names.push('following');
                    if (value & Node.DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC) names.push('implementationSpecific');
                    return names.join('|') || 'none';
                  }
                  var host = document.getElementById('host');
                  var root = host.attachShadow({ mode: 'open' });
                  var inner = document.createElement('span');
                  root.appendChild(inner);
                  return 'innerToHost=' + bits(inner.compareDocumentPosition(host)) +
                         ' hostToInner=' + bits(host.compareDocumentPosition(inner)) +
                         ' rootToHost=' + bits(root.compareDocumentPosition(host));
                })()
                """));
    }

    // ── element traversal ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ElementTraversalSkipsTextAndCommentChildren()
    {
        // #mixed opens and closes on text and holds two comments, so every one of these reads past a
        // non-element on at least one side. A view that skipped text but not comments would count four
        // children and find a comment first; one that skipped nothing would count seven.
        Assert.Equal(
            "children=m1,m2 first=B#m1 last=I#m2 count=2 boldNext=I#m2 italicPrevious=B#m1 " +
            "boldPrevious=null italicNext=null",
            Run("""
                (function () {
                  var mixed = document.getElementById('mixed');
                  var bold = document.getElementById('m1'), italic = document.getElementById('m2');
                  return 'children=' + mixed.children[0].id + ',' + mixed.children[1].id +
                         (mixed.children.length === 2 ? '' : ' length=' + mixed.children.length) +
                         ' first=' + describeNode(mixed.firstElementChild) +
                         ' last=' + describeNode(mixed.lastElementChild) +
                         ' count=' + mixed.childElementCount +
                         ' boldNext=' + describeNode(bold.nextElementSibling) +
                         ' italicPrevious=' + describeNode(italic.previousElementSibling) +
                         ' boldPrevious=' + describeNode(bold.previousElementSibling) +
                         ' italicNext=' + describeNode(italic.nextElementSibling);
                })()
                """));
    }

    [Fact]
    public void TheRootElementHasNoElementSiblingsBesideTheDoctypeAndComments()
    {
        // The root element's parent is the document, not an element — the case a sibling walk written as
        // "parent element's children" reaches as "no parent" and answers null for. That is the right
        // answer here only because a document holds one element at most; the doctype and the comments
        // either side are siblings, and none of them is an element.
        Assert.Equal(
            "previousSibling=#comment:lead nextSibling=#comment:tail previousElement=null nextElement=null " +
            "doctypeFirst=true",
            Run("""
                (function () {
                  var root = document.documentElement;
                  document.insertBefore(document.createComment('lead'), root);
                  document.appendChild(document.createComment('tail'));
                  return 'previousSibling=' + describeNode(root.previousSibling) +
                         ' nextSibling=' + describeNode(root.nextSibling) +
                         ' previousElement=' + describeNode(root.previousElementSibling) +
                         ' nextElement=' + describeNode(root.nextElementSibling) +
                         ' doctypeFirst=' + (document.firstChild === document.doctype);
                })()
                """));
    }

    [Fact]
    public void AnElementInAFragmentFindsItsElementSiblings()
    {
        // A fragment is a parent like any other, so its element children are each other's element
        // siblings. The bridge used to look siblings up through the parent *element*, found none under a
        // fragment, and answered null both ways — which is what a template-cloning loop walking
        // `firstElementChild` then `nextElementSibling` reads as a one-element template. The canonical
        // members walk the parent node.
        Assert.Equal(
            "firstNext=B#fb lastPrevious=A#fa firstPrevious=null lastNext=null",
            Run("""
                (function () {
                  var fragment = document.createDocumentFragment();
                  var a = document.createElement('a'), b = document.createElement('b');
                  a.id = 'fa'; b.id = 'fb';
                  fragment.appendChild(document.createTextNode('t'));
                  fragment.appendChild(a);
                  fragment.appendChild(document.createComment('c'));
                  fragment.appendChild(b);
                  fragment.appendChild(document.createTextNode('u'));
                  return 'firstNext=' + describeNode(a.nextElementSibling) +
                         ' lastPrevious=' + describeNode(b.previousElementSibling) +
                         ' firstPrevious=' + describeNode(a.previousElementSibling) +
                         ' lastNext=' + describeNode(b.nextElementSibling);
                })()
                """));
    }

    [Fact]
    public void AFragmentsElementViewsSkipTextAndCommentChildren()
    {
        // The ParentNode half on a fragment, which its wrapper installs for itself rather than inheriting
        // from Element: the same text, comment, element, comment, element, text shape as #mixed.
        Assert.Equal(
            "children=fa,fb count=2 first=A#fa last=B#fb",
            Run("""
                (function () {
                  var fragment = document.createDocumentFragment();
                  var a = document.createElement('a'), b = document.createElement('b');
                  a.id = 'fa'; b.id = 'fb';
                  fragment.appendChild(document.createTextNode('t'));
                  fragment.appendChild(a);
                  fragment.appendChild(document.createComment('c'));
                  fragment.appendChild(b);
                  fragment.appendChild(document.createTextNode('u'));
                  return 'children=' + fragment.children[0].id + ',' + fragment.children[1].id +
                         (fragment.children.length === 2 ? '' : ' length=' + fragment.children.length) +
                         ' count=' + fragment.childElementCount +
                         ' first=' + describeNode(fragment.firstElementChild) +
                         ' last=' + describeNode(fragment.lastElementChild);
                })()
                """));
    }
}
