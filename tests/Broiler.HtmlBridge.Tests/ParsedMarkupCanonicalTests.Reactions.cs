using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The half of <see cref="ParsedMarkupCanonicalTests"/> about parsed fragments once they are inserted:
/// when a defined custom element among them is upgraded, which document the parsed nodes end up in, and
/// the fragment-parse behaviours the reader swap must not disturb (template contents, inert scripts).
/// <para>
/// <b>Why the reaction order moved.</b> Every fragment the bridge parsed used to be moved, node by node,
/// into a staging fragment owned by the page's document before the caller inserted it. That move published
/// a child-list record on a document the custom-element registry listens to, so a defined element was
/// upgraded <em>there</em> — disconnected, with a fragment for a parent — and taking it back out published
/// a removal, which ran <c>disconnectedCallback</c> on an element that had never been connected. The
/// parser's own fragment belongs to a document nobody listens to, so nothing is heard until the caller
/// inserts the nodes, and the upgrade happens then, connected — which is what DOM §4.2.3 "insert" does
/// ("try upgrade" each connected inclusive descendant) and what Chromium logs.
/// </para>
/// </summary>
public partial class ParsedMarkupCanonicalTests
{
    /// <summary>
    /// Wraps <paramref name="action"/> in a script that defines <c>x-parsed</c> first and returns the
    /// reactions it heard, in order: <c>ctor:</c> with the element's <c>isConnected</c> at construction,
    /// then <c>connected</c>, <c>disconnected</c> and <c>adopted</c>.
    /// </summary>
    private static string Reactions(string action) => $$"""
        (function () {
          var log = [];
          customElements.define('x-parsed', class extends HTMLElement {
            constructor() { super(); log.push('ctor:' + this.isConnected); }
            connectedCallback() { log.push('connected'); }
            disconnectedCallback() { log.push('disconnected'); }
            adoptedCallback() { log.push('adopted'); }
          });
          {{action}}
          return log.join(',');
        })()
        """;

    [Fact]
    public void InnerHtmlUpgradesADefinedElementWhenItIsInserted()
    {
        // CHANGED from "ctor:false,disconnected,connected": the element was upgraded in the page-owned
        // staging fragment and then reported a disconnection as it left it.
        Assert.Equal(
            "ctor:true,connected",
            Run(PlainFramePage, Reactions("document.getElementById('host').innerHTML = '<x-parsed></x-parsed>';")));
    }

    [Fact]
    public void OuterHtmlUpgradesADefinedElementWhenItIsInserted()
    {
        // CHANGED, as for innerHTML: outerHTML parsed through the same staging fragment.
        Assert.Equal(
            "ctor:true,connected",
            Run(PlainFramePage, Reactions("document.getElementById('old').outerHTML = '<x-parsed></x-parsed>';")));
    }

    [Fact]
    public void InsertAdjacentHtmlUpgradesADefinedElementWhenItIsInserted()
    {
        // CHANGED, as for innerHTML: the parsed nodes were taken back out of the staging fragment one by
        // one before insertion, and each removal was heard.
        Assert.Equal(
            "ctor:true,connected",
            Run(PlainFramePage, Reactions("document.getElementById('host').insertAdjacentHTML('beforeend', '<x-parsed></x-parsed>');")));
    }

    [Fact]
    public void Characterization_DocumentWriteUpgradesAtInsertion()
    {
        // CHANGED, as for innerHTML. Characterization only because of where the markup lands: after load
        // Chromium's document.write implicitly calls document.open() and replaces the document, where this
        // bridge appends to <body>. The reaction order is the part pinned here.
        Assert.Equal(
            "ctor:true,connected",
            Run(PlainFramePage, Reactions("document.write('<x-parsed></x-parsed>');")));
    }

    [Fact]
    public void Characterization_CreateContextualFragmentUpgradesInTheDetachedFragment()
    {
        // CHANGED from "ctor:false,disconnected": the spurious disconnection is gone, and the upgrade still
        // happens when the parsed node is appended to the (disconnected) result fragment, because the
        // registry upgrades on any added record, connected or not. Chromium logs nothing: the fragment
        // parser's document has no registry, and inserting into a disconnected fragment tries no upgrade.
        Assert.Equal(
            "ctor:false",
            Run(PlainFramePage, Reactions(
                "var r = document.createRange(); r.selectNodeContents(document.getElementById('host')); " +
                "window.__fragment = r.createContextualFragment('<x-parsed></x-parsed>');")));
    }

    [Fact]
    public void Characterization_InnerHtmlIntoADetachedElementUpgradesThere()
    {
        // CHANGED from "ctor:false,disconnected": the staging fragment's removal is gone. What is left is
        // the registry upgrading on insertion into a disconnected element, which is unchanged. Chromium logs
        // nothing until the element is connected (DOM §4.2.3 only tries to upgrade connected descendants).
        Assert.Equal(
            "ctor:false",
            Run(PlainFramePage, Reactions("document.createElement('div').innerHTML = '<x-parsed></x-parsed>';")));
    }

    [Fact]
    public void Characterization_InnerHtmlIntoAFrameUpgradesAgainstThePagesRegistry()
    {
        // CHANGED from "ctor:false,disconnected,adopted,connected", for two reasons. The staging fragment
        // belonged to the page, so a frame target also heard an adoption out of it; the upgrade now happens
        // on insertion into the frame, connected. And isConnected answered whether the node's root was the
        // PAGE's document, so the constructor read false in a frame; it now answers whether the node's
        // shadow-including root is a document (DOM §4.2.2), and the frame's document is one.
        // Characterization still, because this bridge has one registry across every document it owns; in
        // Chromium the frame's window has its own customElements, x-parsed is not defined there, and
        // nothing is logged.
        Assert.Equal(
            "ctor:true,connected",
            Run(PlainFramePage, Reactions(
                "document.getElementById('f').contentDocument.getElementById('fp').innerHTML = '<x-parsed></x-parsed>';")));
    }

    [Fact]
    public void Characterization_InnerHtmlIntoACreateHtmlDocumentUpgradesAgainstThePagesRegistry()
    {
        // CHANGED, as for a frame: the constructor read false while isConnected answered whether the node's
        // root was the PAGE's document; a document a script built is a document too, so it now reads true
        // (DOM §4.2.2). Characterization, because nothing should be upgraded here at all: this bridge's one
        // registry upgrades in every document it owns, while HTML's definition lookup answers null for a
        // document with no browsing context, so Chromium logs nothing. The XMLHttpRequest polyfill builds
        // responseXML this way, so fetched markup naming a defined element reaches this path too.
        Assert.Equal(
            "ctor:true,connected",
            Run(PlainFramePage, Reactions(
                "document.implementation.createHTMLDocument('').body.innerHTML = '<x-parsed></x-parsed>';")));
    }

    [Fact]
    public void InnerHtmlScriptsStillNeverRun()
    {
        // HTML §8.4: a script inserted by innerHTML is marked already started and never runs. The staging
        // fragment going away must not hand it to the script-insertion runner.
        Assert.Equal(
            "undefined",
            Run(PlainFramePage, """
                (function () {
                  document.getElementById('host').innerHTML = '<script>window.__ran = 1<\/script>';
                  return typeof window.__ran;
                })()
                """));
    }

    [Fact]
    public void ParsedNodesBelongToTheDocumentTheyAreInsertedInto()
    {
        // The parser's nodes start out in a document of its own; insertion adopts them (DOM §4.2.3), so
        // every route ends with the ownerDocument of the tree they joined — the page for innerHTML,
        // insertAdjacentHTML and a contextual fragment, the frame for an element inside the frame. Text and
        // comment nodes travel with the elements.
        Assert.Equal(
            "4 true I true 2 B true true",
            Run(PlainFramePage, """
                (function () {
                  var h = document.getElementById('host');
                  h.innerHTML = '<b>x</b>text<!--c-->';
                  h.insertAdjacentHTML('beforeend', '<i>z</i>');
                  var r = document.createRange();
                  r.selectNodeContents(h);
                  var f = r.createContextualFragment('<b>y</b>t');
                  var d = document.getElementById('f').contentDocument;
                  var p = d.getElementById('fp');
                  p.innerHTML = '<u>q</u>';
                  return h.childNodes.length + ' ' + (h.firstChild.ownerDocument === document) + ' ' +
                         h.lastChild.nodeName + ' ' + (h.lastChild.ownerDocument === document) + ' ' +
                         f.childNodes.length + ' ' + f.firstChild.nodeName + ' ' +
                         (f.firstChild.ownerDocument === document) + ' ' + (p.firstChild.ownerDocument === d);
                })()
                """));
    }

    [Fact]
    public void TemplateInnerHtmlStillFillsContentAndDivertsNestedTemplates()
    {
        // HTML §4.12.3: innerHTML on a template writes its contents, and a template inside that markup has
        // its own children diverted into its own contents.
        Assert.Equal(
            "0 2 I",
            Run(PlainFramePage, """
                (function () {
                  var t = document.createElement('template');
                  t.innerHTML = '<p>a</p><template><i>b</i></template>';
                  return t.childNodes.length + ' ' + t.content.childNodes.length + ' ' +
                         t.content.lastChild.content.firstChild.nodeName;
                })()
                """));
    }

    /// <summary>
    /// A page whose body holds a parsed <c>&lt;template&gt;</c> with a nested one inside it, and a
    /// second template a script appends to rather than writes through <c>innerHTML</c>.
    /// </summary>
    private const string TemplatePage =
        "<html><body>" +
        "<template id=\"t\"><p class=\"row\">a</p><template><i>b</i></template></template>" +
        "<div id=\"out\"></div></body></html>";

    [Fact]
    public void AParsedTemplatesChildrenAreItsContentsAndAreNotInTheDocument()
    {
        // HTML §4.12.3: the tree builder puts a template's children in its contents fragment, so the
        // element is childless and a document walk never reaches them — `t.querySelector('.row')`
        // answers null in a browser because the markup is not in the tree at all. The fragment is the
        // element's own and is created with it, so asking twice gives the same node.
        Assert.Equal(
            "own=0 content=2 nested=I query=null identity=true",
            Run(TemplatePage, """
                (function () {
                  var t = document.getElementById('t');
                  return 'own=' + t.childNodes.length +
                         ' content=' + t.content.childNodes.length +
                         ' nested=' + t.content.lastChild.content.firstChild.nodeName +
                         ' query=' + (document.querySelector('.row') === null ? 'null' : 'found') +
                         ' identity=' + (t.content === t.content);
                })()
                """));
    }

    [Fact]
    public void ATemplateSerializesItsContentsAndNotItsOwnChildren()
    {
        // The serialization walk reaches through to the contents fragment (HTML §13.3 serializes a
        // template's contents in place of its child list), which is also where a script's appendChild
        // does NOT land: appending to the element leaves the contents alone, so the appended node is
        // not serialized. Both halves are the dependency's own serializer's answer.
        Assert.Equal(
            "<template id=\"t\"><p class=\"row\">a</p><template><i>b</i></template></template>",
            PageProbe.OutOf(
                RunForHtml(TemplatePage, """
                    (function () {
                      var t = document.getElementById('t');
                      t.appendChild(document.createElement('u'));
                      return t.outerHTML;
                    })()
                    """),
                decode: true));
    }
}
