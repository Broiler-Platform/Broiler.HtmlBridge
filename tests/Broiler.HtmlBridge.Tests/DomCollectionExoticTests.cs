using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// The DOM collections that complete their own property lookup — <c>NodeList</c> and
/// <c>HTMLCollection</c> — asserted from page script, across the JSEAL exotic-object conversion.
/// <para>
/// The collection used to derive from the engine's object type and override its lookup protocol,
/// including the materialisation of its indices into the engine's own property storage. It is now an
/// <c>IJsExotic</c> handler the realm mints an object around, and the materialisation is the
/// provider's — it happens from <c>IJsExotic.IndexedLength</c>, because <em>that</em> an engine needs
/// its indices to be real is a fact about that engine's property storage rather than about the DOM.
/// Nothing about either move is visible to a page, and this file is what says so.
/// </para>
/// <para>
/// <b>The ordering rule is why the first test exists.</b> Consulting the named getter before the
/// object's ordinary properties and its prototype compiles, passes every test that reads a member by
/// name, and is wrong in exactly one case: a collection that happens to contain an element named
/// <c>item</c> would start shadowing its own <c>item()</c> method. There is no test that fails at the
/// moment the order is inverted unless one is written for that case, so one is.
/// </para>
/// </summary>
public class DomCollectionExoticTests
{
    private const string PageUrl = "https://example.test/collections";

    /// <summary>
    /// Three children with no whitespace between them, so <c>childNodes</c> is the three elements and
    /// not seven nodes; a form named <c>item</c> to collide with the interface's own member, and one
    /// named <c>login</c> to be found by the named getter.
    /// </summary>
    private const string PageHtml =
        "<html><body>" +
        "<div id=\"host\"><span id=\"a\"></span><span id=\"b\"></span><span id=\"c\"></span></div>" +
        "<form id=\"item\"></form><form name=\"login\" id=\"first\"></form>" +
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
    public void ACollectionsOwnMembersWinOverItsNamedGetter()
    {
        // A <form id="item"> is in document.forms and is reachable by name — but not as `.item`,
        // because the prototype answers first. Inverting the lookup order breaks precisely this line
        // and nothing else in the suite.
        Assert.Equal(
            "item=function named=FORM namedId=first length=2",
            Run("""
                (function () {
                  var forms = document.forms;
                  return 'item=' + (typeof forms.item) +
                         ' named=' + forms.login.tagName +
                         ' namedId=' + forms.login.id +
                         ' length=' + forms.length;
                })()
                """));
    }

    [Fact]
    public void ACollectionIsLiveInBothDirections()
    {
        // Growing and shrinking are separate paths: the second is what a materialised index gets
        // wrong if it is only ever added, leaving a removed element still reachable at its old index.
        Assert.Equal(
            "before=3 grown=4 shrunk=2 gone=undefined present=false",
            Run("""
                (function () {
                  var host = document.getElementById('host');
                  var kids = host.childNodes;
                  var before = kids.length;
                  host.appendChild(document.createElement('span'));
                  var grown = kids.length;
                  host.removeChild(host.lastChild);
                  host.removeChild(host.lastChild);
                  return 'before=' + before +
                         ' grown=' + grown +
                         ' shrunk=' + kids.length +
                         ' gone=' + kids[2] +
                         ' present=' + (2 in kids);
                })()
                """));
    }

    [Fact]
    public void ACollectionsIndicesAreRealPropertiesAndItsLengthIsNot()
    {
        // The array generics are the reason the indices are materialised rather than intercepted:
        // map asks whether index i is *present* before reading it. length is the opposite case — it
        // is answered, never installed, so it stays out of every enumeration the way a browser's
        // prototype accessor does.
        Assert.Equal(
            "map=a,b,c keys=0,1,2 own=0,1,2 spread=0,1,2 length=3",
            Run("""
                (function () {
                  var kids = document.getElementById('host').childNodes;
                  return 'map=' + Array.prototype.map.call(kids, function (n) { return n.id; }).join(',') +
                         ' keys=' + Object.keys(kids).join(',') +
                         ' own=' + Object.getOwnPropertyNames(kids).join(',') +
                         ' spread=' + Object.keys(Object.assign({}, kids)).join(',') +
                         ' length=' + kids.length;
                })()
                """));
    }

    [Fact]
    public void ACollectionInheritsItsInterfacesMembersAndEnumeratesThem()
    {
        // The prototype link is what `instanceof` answers through, and the interface's members are
        // enumerable as Web IDL says — so for…in yields them beside the indices, and never `length`.
        Assert.Equal(
            "nodeList=true htmlCollection=true ctor=NodeList forin=true noLength=true iterated=3",
            Run("""
                (function () {
                  var kids = document.getElementById('host').childNodes;
                  var seen = [];
                  for (var k in kids) { seen.push(k); }
                  var count = 0;
                  for (var i = 0; i < kids.length; i++) { count++; }
                  return 'nodeList=' + (kids instanceof NodeList) +
                         ' htmlCollection=' + (document.forms instanceof HTMLCollection) +
                         ' ctor=' + kids.constructor.name +
                         ' forin=' + (seen.indexOf('item') >= 0) +
                         ' noLength=' + (seen.indexOf('length') < 0) +
                         ' iterated=' + count;
                })()
                """));
    }

    [Fact]
    public void AStaticCollectionStaysStaticAndALiveOneDoesNot()
    {
        // querySelectorAll is the one collection DOM §4.2.6 defines as a snapshot. Both flavours are
        // the same object over different contents functions, so the difference has to be visible here
        // or the distinction has been lost.
        Assert.Equal(
            "staticBefore=3 staticAfter=3 liveAfter=4",
            Run("""
                (function () {
                  var host = document.getElementById('host');
                  var snapshot = document.querySelectorAll('span');
                  var live = host.childNodes;
                  var before = snapshot.length;
                  host.appendChild(document.createElement('span'));
                  return 'staticBefore=' + before +
                         ' staticAfter=' + snapshot.length +
                         ' liveAfter=' + live.length;
                })()
                """));
    }

    [Fact]
    public void ACollectionIsIterableAndItsMethodsReadItLive()
    {
        // item(), the spread and forEach are all written against `this.length` and `this[i]` in the
        // interface's JavaScript, so they are the shortest proof that the handler answers both for a
        // receiver the method never saw built.
        Assert.Equal(
            "item=b outOfRange=null spread=a,b,c forEach=a,b,c",
            Run("""
                (function () {
                  var kids = document.getElementById('host').childNodes;
                  var spread = [];
                  for (var i = 0; i < kids.length; i++) { spread.push(kids[i].id); }
                  var visited = [];
                  kids.forEach(function (n) { visited.push(n.id); });
                  return 'item=' + kids.item(1).id +
                         ' outOfRange=' + kids.item(9) +
                         ' spread=' + spread.join(',') +
                         ' forEach=' + visited.join(',');
                })()
                """));
    }
}
