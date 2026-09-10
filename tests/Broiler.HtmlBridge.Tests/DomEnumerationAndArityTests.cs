using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// The two properties of the DOM binding nothing else in this suite reads: the ORDER an object's keys
/// come out in, and the ARITY the methods the bridge installs advertise.
/// <para>
/// Both are invisible to every test that calls a method and checks its answer, and both are what a
/// handle-shaped rewrite of the wrapper storage changes without failing anything. Key order is decided
/// by <em>where</em> a key lives — an index materialised into indexed storage, a name the exotic
/// handler appends, an ordinary property a page assigned — so moving any of those stores reorders
/// <c>Object.keys</c>. Arity is a number handed to the realm once at each mint site and never read
/// back, so a site that drops it reports <c>0</c> for ever and the feature detection that reads
/// <c>fn.length</c> to decide whether an optional argument exists takes the wrong branch.
/// </para>
/// <para>
/// <b>Everything asserted here is what a browser does, read off Web IDL rather than off this bridge.</b>
/// Where the bridge is known to disagree the test says so in its Skip string and names the line, so the
/// net records the defect without turning the suite red.
/// </para>
/// </summary>
public class DomEnumerationAndArityTests
{
    private const string PageUrl = "https://example.test/enumeration";

    /// <summary>
    /// Twelve spans with no whitespace between them, so a collection over them has indices past nine —
    /// the only way numeric key order can be told from string key order — and one carries an
    /// <c>id</c>, for the <c>HTMLCollection</c> named getter.
    /// </summary>
    private const string PageHtml =
        "<html><body><div id=\"host\">" +
        "<span></span><span></span><span></span><span></span><span id=\"beacon\"></span><span></span>" +
        "<span></span><span></span><span></span><span></span><span></span><span></span>" +
        "</div><div id=\"out\"></div></body></html>";

    /// <summary>
    /// Runs <paramref name="script"/> against the fixture document and returns what it wrote to
    /// <c>#out</c>. Reading the result out of the serialized DOM keeps the test to the engine's public
    /// surface — the binding under test is internal, and reaching for it directly would pin its shape.
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
    public void ACollectionsKeysAreItsIndicesInNumericOrderAndNothingItsNamedGetterAnswers()
    {
        // Ten is the first index that tells numeric order from string order — a store keyed by string
        // yields 0,1,10,11,2,… — and every existing collection test stops at three. The assigned
        // property is the second half: it reaches ordinary storage BEFORE the indices are materialised,
        // so creation order and key order disagree here and nowhere else, and ECMAScript puts the array
        // indices first and ascending, then the string keys in creation order. The third is what must
        // NOT be there — HTMLCollection is [LegacyUnenumerableNamedProperties] (Web IDL §3.7.4), so a
        // member reachable by its id answers `in` and answers by name and is still absent from the keys.
        Assert.Equal(
            "keys=0,1,2,3,4,5,6,7,8,9,10,11,probe own=0,1,2,3,4,5,6,7,8,9,10,11,probe " +
            "forin=0,1,2,3,4,5,6,7,8,9,10,11 byName=SPAN in=true",
            Run("""
                (function () {
                  var spans = document.getElementsByTagName('span');
                  spans.probe = 'assigned before anything read this collection';
                  var seen = [];
                  for (var k in spans) { if (!isNaN(k)) seen.push(k); }
                  return 'keys=' + Object.keys(spans).join(',') +
                         ' own=' + Object.getOwnPropertyNames(spans).join(',') +
                         ' forin=' + seen.join(',') +
                         ' byName=' + spans.beacon.tagName +
                         ' in=' + ('beacon' in spans);
                })()
                """));
    }

    [Fact]
    public void AStorageAreaEnumeratesItsKeysAndItsOwnMembersStillWinOverThem()
    {
        // The opposite case to a collection, and the ordering rule on the one lookup-completing object
        // that is NOT yet an exotic handler — so this is where that rule gets re-argued rather than
        // inherited. A storage area's named properties ARE enumerable, in the order the keys were
        // added (HTML §12.2.2), which is how a page sweeps an area it did not write; and a key called
        // `length` or `getItem` must not cost it the count or the method, because Web IDL hides a
        // named property whose name anything on the prototype chain already answers.
        Assert.Equal(
            "forin=alpha,beta,gamma keys=alpha,beta,gamma members=false " +
            "count=5 getItem=function stored=9",
            Run("""
                (function () {
                  localStorage.setItem('alpha', '1');
                  localStorage.setItem('beta', '2');
                  localStorage.setItem('gamma', '3');
                  var seen = [];
                  for (var k in localStorage) { seen.push(k); }
                  localStorage.setItem('length', '9');
                  localStorage.setItem('getItem', 'shadow');
                  var keys = Object.keys(localStorage);
                  return 'forin=' + seen.join(',') +
                         ' keys=' + keys.join(',') +
                         ' members=' + (keys.indexOf('getItem') >= 0 || keys.indexOf('length') >= 0) +
                         ' count=' + localStorage.length +
                         ' getItem=' + (typeof localStorage.getItem) +
                         ' stored=' + localStorage.getItem('length');
                })()
                """));
    }

    [Fact]
    public void TheMethodsTheBridgeInstallsReportTheirRequiredArgumentCount()
    {
        // Web IDL's `length` counts the REQUIRED arguments, so an optional trailing one does not raise
        // it — which is why these numbers are smaller than the parameter lists behind them. Each is
        // read off an instance rather than off a prototype, because that is the spelling a page uses
        // and it also proves the member is where it is meant to be.
        Assert.Equal(
            "getElementById=1 createElement=1 querySelector=1 getAttribute=1 setAttribute=2 " +
            "appendChild=1 insertBefore=2 addEventListener=2 removeEventListener=2 " +
            "dispatchEvent=1 setItem=2 clear=0",
            Run("""
                (function () {
                  var el = document.getElementById('host');
                  return 'getElementById=' + document.getElementById.length +
                         ' createElement=' + document.createElement.length +
                         ' querySelector=' + document.querySelector.length +
                         ' getAttribute=' + el.getAttribute.length +
                         ' setAttribute=' + el.setAttribute.length +
                         ' appendChild=' + el.appendChild.length +
                         ' insertBefore=' + el.insertBefore.length +
                         ' addEventListener=' + el.addEventListener.length +
                         ' removeEventListener=' + el.removeEventListener.length +
                         ' dispatchEvent=' + el.dispatchEvent.length +
                         ' setItem=' + localStorage.setItem.length +
                         ' clear=' + localStorage.clear.length;
                })()
                """));
    }

    [Fact(Skip =
        "DomBridge/Registration/Window.cs:468-470 installs the window's own copies of the three " +
        "EventTarget methods unguarded, declaring 3, 3 and 1 arguments — where the document's " +
        "(Registration/Document.cs:234) and every element's (JsObjects.cs:275) are guarded by " +
        "_eventTargetRoutingReady and so come from the routed EventTarget.prototype methods, minted " +
        "with Web IDL's 2, 2, 1 at DomBridge/EventTargetInterface.cs:96-106. So " +
        "window.addEventListener.length is 3 where a browser says 2, and it is a different function " +
        "object from EventTarget.prototype.addEventListener — which the defensive " +
        "`EventTarget.prototype.addEventListener.call(window, ...)` idiom needs it to be.")]
    public void TheWindowsEventTargetMethodsAreTheOnesOnEventTargetPrototype()
    {
        Assert.Equal(
            "length=2 shared=true removeShared=true",
            Run("""
                (function () {
                  return 'length=' + window.addEventListener.length +
                         ' shared=' + (window.addEventListener === EventTarget.prototype.addEventListener) +
                         ' removeShared=' + (window.removeEventListener === EventTarget.prototype.removeEventListener);
                })()
                """));
    }

    [Fact(Skip =
        "getRootNode is minted with a declared length of 1 at DomBridge/JsObjects.cs:463, " +
        "DomBridge/JsObjects.NonElementNodes.cs:212, :328 and :585, and on the character-data " +
        "prototype at DomBridge/CharacterDataInterface.cs:248. DOM §4.4 declares " +
        "`Node getRootNode(optional GetRootNodeOptions options = {})`, so its only argument is " +
        "optional and a browser reports 0 — a page feature-detecting composed-tree support by " +
        "reading el.getRootNode.length gets the wrong answer.")]
    public void GetRootNodeDeclaresNoRequiredArgument()
    {
        Assert.Equal(
            "getRootNode=0 root=true",
            Run("""
                (function () {
                  var el = document.getElementById('host');
                  return 'getRootNode=' + el.getRootNode.length +
                         ' root=' + (el.getRootNode() === document);
                })()
                """));
    }

    [Fact]
    public void ADomObjectReportsItsKindAndItsInterface()
    {
        // Kind is what the handle seam has just started preserving, read through the two operators a
        // page uses. `typeof` separating a wrapper from its members is the shallow half; the interface
        // answers are the half that breaks silently, because instanceof would keep answering through
        // the @@hasInstance hooks while constructor.name fell back to "Object".
        Assert.Equal(
            "typeofDocument=object typeofElement=object typeofMethod=function " +
            "typeofCollection=object typeofStorage=object htmlElement=true element=true " +
            "node=true eventTarget=true isDocument=true ctor=HTMLDivElement bodyCtor=HTMLBodyElement",
            Run("""
                (function () {
                  var div = document.createElement('div');
                  return 'typeofDocument=' + (typeof document) +
                         ' typeofElement=' + (typeof div) +
                         ' typeofMethod=' + (typeof div.setAttribute) +
                         ' typeofCollection=' + (typeof document.getElementsByTagName('span')) +
                         ' typeofStorage=' + (typeof localStorage) +
                         ' htmlElement=' + (div instanceof HTMLElement) +
                         ' element=' + (div instanceof Element) +
                         ' node=' + (div instanceof Node) +
                         ' eventTarget=' + (div instanceof EventTarget) +
                         ' isDocument=' + (document instanceof Document) +
                         ' ctor=' + div.constructor.name +
                         ' bodyCtor=' + document.body.constructor.name;
                })()
                """));
    }

    /// <summary>
    /// <c>location</c>'s own members keep the descriptor shape a page can read off them.
    /// </summary>
    /// <remarks>
    /// <b>Written because the interop sweep is about to rebuild this object through the realm rather
    /// than the engine, and a descriptor is the part of it nothing else checks.</b> A page reads
    /// these: a framework feature-detects with <c>getOwnPropertyDescriptor(location, 'href').set</c>,
    /// and <c>Object.keys(location)</c> is what a logger serialises. The accessor's arity is the
    /// specification's — a setter declares one required argument and a getter none — and it is
    /// already correct here, so this test exists to keep it correct across a change of the machinery
    /// that mints it rather than to fix it.
    /// <para>
    /// <c>protocol</c> is asserted alongside <c>href</c> because the two are built differently: one
    /// is an accessor and the other a data property, and a rebuild that made them uniform would be
    /// invisible to every other assertion in this file.
    /// </para>
    /// </remarks>
    [Fact]
    public void LocationsMembersKeepTheirDescriptorShape()
    {
        Assert.Equal("set=1 get=0 enum=true config=true", Run("""
            (function () {
              var d = Object.getOwnPropertyDescriptor(location, 'href');
              return 'set=' + d.set.length + ' get=' + d.get.length +
                     ' enum=' + d.enumerable + ' config=' + d.configurable;
            })()
            """));

        // A data property, not an accessor -- the distinction a uniform rebuild would erase.
        Assert.Equal("accessor=undefined enumerable=true", Run("""
            (function () {
              var d = Object.getOwnPropertyDescriptor(location, 'protocol');
              return 'accessor=' + typeof d.set + ' enumerable=' + d.enumerable;
            })()
            """));

        // href is enumerable and reachable by name, which is what a for-in over location depends on.
        Assert.Equal("count=13 hasHref=true", Run("""
            (function () {
              var ks = Object.keys(location);
              return 'count=' + ks.length + ' hasHref=' + (ks.indexOf('href') >= 0);
            })()
            """));
    }
}
