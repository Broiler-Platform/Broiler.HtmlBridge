using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// Wrapper identity — that one DOM node has exactly one script object for the life of a document —
/// asserted from page script, ahead of the commits that re-type the registry holding it.
/// <para>
/// <c>Runtime/JsObjectRegistry.cs</c> is two dictionaries keyed on a node's reference identity plus a
/// reverse <c>ConditionalWeakTable</c> keyed on the wrapper, and the remaining interop commits re-type
/// all three — the forward maps become handle-valued and the reverse table is the member the registry's
/// own remarks say cannot follow. Nothing else in this suite notices when they stop agreeing:
/// <c>el === el</c> going false throws nothing, changes no serialization and fails no existing test. It
/// changes what a page's <c>Map</c> finds, whether a listener is reached again, and whether a node
/// handed back from a lookup is the object the page was already holding.
/// </para>
/// <para>
/// <b>The last two tests are the pair that matters.</b> A wrapper the page still holds must keep naming
/// its node after the node leaves the tree, which is the only reason the reverse table deliberately
/// outlives <c>JsObjectRegistry.Remove</c>. The other half of that invariant is that putting the node
/// back hands the page the object it kept — broken today by one of the two removal routes and not the
/// other, which is what the skipped test and the one before it say between them.
/// </para>
/// </summary>
public class WrapperIdentityTests
{
    private const string PageUrl = "https://example.test/identity";

    /// <summary>
    /// No whitespace between the elements, so <c>childNodes[0]</c> is the span rather than a text node.
    /// <c>#kid</c> carries an id and a class so a detached wrapper has something to answer with, and
    /// <c>#para</c> holds the one text node a non-element wrapper's identity is read from.
    /// </summary>
    private const string PageHtml =
        "<html><body>" +
        "<div id=\"host\"><span id=\"kid\" class=\"k\">text</span></div>" +
        "<p id=\"para\">hello</p>" +
        "<div id=\"out\"></div>" +
        "</body></html>";

    /// <summary>
    /// Runs <paramref name="script"/> against the fixture document and returns what it wrote to
    /// <c>#out</c>. Reading the result out of the serialized DOM keeps the test to the engine's public
    /// surface — the registry under test is internal, and reaching for it directly would pin its shape
    /// rather than its behaviour, which is the one thing these commits are about to change.
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
    public void TheSameNodeFetchedTwiceIsOneObject()
    {
        // The floor of the whole file: two lookups of one node must not mint two wrappers. The text node
        // is here because it takes a different arm of the wrapper factory than an element does; classList
        // and attributes because they are the sibling per-element caches that make the same promise about
        // objects that are not nodes; and the document because its wrapper is a bridge field as well as a
        // node-map entry, so the tree walk that arrives at it and the global that names it have to agree
        // or `node.ownerDocument === document` answers false and every guard written that way inverts.
        Assert.Equal(
            "element=true body=true root=true text=true window=true " +
            "classList=true attributes=true bodyRoute=true bodyParent=true rootParent=true owner=true",
            Run("""
                (function () {
                  var para = document.getElementById('para');
                  var kid = document.getElementById('kid');
                  return 'element=' + (document.getElementById('kid') === document.getElementById('kid')) +
                         ' body=' + (document.body === document.body) +
                         ' root=' + (document.documentElement === document.documentElement) +
                         ' text=' + (para.firstChild === para.firstChild) +
                         ' window=' + (document === window.document) +
                         ' classList=' + (kid.classList === kid.classList) +
                         ' attributes=' + (kid.attributes === kid.attributes) +
                         ' bodyRoute=' + (document.body === document.getElementById('out').parentNode) +
                         ' bodyParent=' + (document.body.parentNode === document.documentElement) +
                         ' rootParent=' + (document.documentElement.parentNode === document) +
                         ' owner=' + (document.getElementById('kid').ownerDocument === document);
                })()
                """));
    }

    [Fact]
    public void OneElementReachedEightWaysIsOneObject()
    {
        // Eight different bindings hand back this span, and every one of them goes through the same
        // wrapper hub. A route that stopped consulting the registry — or a registry that stopped
        // answering for it — would give that one route a private wrapper and break nothing visible.
        Assert.Equal(
            "query=true children=true childNodes=true firstElement=true parentWalk=true byTag=true all=true closest=true",
            Run("""
                (function () {
                  var byId = document.getElementById('kid');
                  var host = document.getElementById('host');
                  return 'query=' + (document.querySelector('#kid') === byId) +
                         ' children=' + (host.children[0] === byId) +
                         ' childNodes=' + (host.childNodes[0] === byId) +
                         ' firstElement=' + (host.firstElementChild === byId) +
                         ' parentWalk=' + (byId.parentNode.firstElementChild === byId) +
                         ' byTag=' + (document.getElementsByTagName('span')[0] === byId) +
                         ' all=' + (document.querySelectorAll('span')[0] === byId) +
                         ' closest=' + (byId.closest('span') === byId);
                })()
                """));
    }

    [Fact]
    public void ANodeIsFoundAgainAsAMapKeyAndASetMember()
    {
        // A Map keyed on elements is how a component library associates state with the DOM, and it
        // hashes on object identity. A second wrapper for one node is a lookup miss plus a duplicate
        // entry that never expires — nothing throws where the mistake is made, and the symptom is state
        // that quietly stops being found.
        Assert.Equal(
            "get=kid has=true set=true mapSize=2 setSize=1 other=host",
            Run("""
                (function () {
                  var kid = document.getElementById('kid');
                  var host = document.getElementById('host');
                  var m = new Map();
                  var s = new Set();
                  m.set(kid, 'kid');
                  m.set(host, 'host');
                  s.add(kid);
                  var again = document.querySelector('#kid');
                  s.add(again);
                  return 'get=' + m.get(again) +
                         ' has=' + m.has(again) +
                         ' set=' + s.has(again) +
                         ' mapSize=' + m.size +
                         ' setSize=' + s.size +
                         ' other=' + m.get(again.parentNode);
                })()
                """));
    }

    [Fact]
    public void AWrapperKeepsNamingItsNodeAfterTheNodeLeavesTheTree()
    {
        // Every member read here lives on Element.prototype or Node.prototype and finds its node by
        // asking the reverse table about the receiver, so this is the test that fails the moment that
        // table is dropped alongside the forward one. It is not a corner case: holding a node across an
        // innerHTML rewrite is ordinary, and in a browser the removed node goes on answering.
        Assert.Equal(
            "tag=SPAN id=kid class=k nodeType=1 parent=null connected=false emptied=true",
            Run("""
                (function () {
                  var host = document.getElementById('host');
                  var gone = host.firstElementChild;
                  host.innerHTML = '';
                  return 'tag=' + gone.tagName +
                         ' id=' + gone.getAttribute('id') +
                         ' class=' + gone.className +
                         ' nodeType=' + gone.nodeType +
                         ' parent=' + gone.parentNode +
                         ' connected=' + gone.isConnected +
                         ' emptied=' + (host.firstElementChild === null);
                })()
                """));
    }

    [Fact]
    public void ANodeTakenOutWithRemoveChildIsTheSameObjectWhenItGoesBack()
    {
        // Detaching a node and re-inserting it is one node throughout, so it is one wrapper throughout;
        // a clone is a different node and must therefore be a different object. This route leaves the
        // registry entry alone, which is the whole difference between it and the skipped test below.
        Assert.Equal(
            "detached=true back=true byId=true clone=false",
            Run("""
                (function () {
                  var host = document.getElementById('host');
                  var kid = host.firstElementChild;
                  host.removeChild(kid);
                  var detached = document.getElementById('kid') === null;
                  host.appendChild(kid);
                  return 'detached=' + detached +
                         ' back=' + (host.firstElementChild === kid) +
                         ' byId=' + (document.getElementById('kid') === kid) +
                         ' clone=' + (kid.cloneNode(true) === kid);
                })()
                """));
    }

    [Fact(Skip = "Known defect: an innerHTML rewrite drops the forward registry entry for every node it " +
                 "removes (DomBridge/HtmlFragmentMutation.cs:243 -> RemoveElementsRecursive at :22 -> " +
                 "JsObjectRegistry.Remove at Runtime/JsObjectRegistry.cs:106), so re-inserting a node " +
                 "the page still holds mints a SECOND wrapper: back/byId answer false while isSameNode " +
                 "answers true — one node, two wrappers, both named by the reverse table. removeChild " +
                 "leaves the entry alone, which is why the test above passes and this one does not.")]
    public void ANodeTakenOutByAnInnerHtmlRewriteIsTheSameObjectWhenItGoesBack()
    {
        // The other half of the invariant the reverse table exists for. Emptying a container and putting
        // a saved child back is the ordinary list-reorder idiom, and in a browser the node that comes
        // back is the object the page saved.
        Assert.Equal(
            "back=true byId=true sameNode=true",
            Run("""
                (function () {
                  var host = document.getElementById('host');
                  var gone = host.firstElementChild;
                  host.innerHTML = '';
                  host.appendChild(gone);
                  return 'back=' + (host.firstElementChild === gone) +
                         ' byId=' + (document.getElementById('kid') === gone) +
                         ' sameNode=' + gone.isSameNode(host.firstElementChild);
                })()
                """));
    }
}
