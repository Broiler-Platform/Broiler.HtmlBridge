using System.Drawing;
using System.Globalization;

using Broiler.Dom;
using Broiler.HtmlBridge;
using Broiler.Layout;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Answers that changed when the bridge stopped re-deriving something a pinned dependency already
/// owns. Each case fails against the deleted copy — that is what makes it worth pinning: the copy
/// and the canonical algorithm disagree, and the canonical one is right.
/// <para>
/// <b>Slot assignment (DOM §4.2.2.3).</b> The scroll-geometry walk used to ask each
/// <c>&lt;slot&gt;</c> whether it accepted a host child by <em>name</em>
/// (<c>DomSlotting.SlotAcceptsNode</c>), so two same-named slots in one shadow tree both claimed
/// the same child and both measured it. The canonical algorithm assigns a slottable to the
/// <em>first</em> accepting slot only (<c>DomSlotting.GetAssignedNodes</c>, which the walk now
/// calls), so the child contributes scrollable overflow to that slot's scroll container and to no
/// other.
/// </para>
/// </summary>
public class DependencyAlignmentTests
{
    private const string PageUrl = "https://example.test/dependency-alignment";

    /// <summary>The attribute <see cref="BoxAttributeLayoutView"/> reads a laid-out box from.</summary>
    private const string BoxAttribute = "data-box";

    /// <summary>
    /// Three shadow hosts, each with one 400px-tall light child carrying <c>slot="a"</c>. The shadow
    /// trees are built from script so each scroller keeps a JavaScript reference; the layout view
    /// below gives every element with a <see cref="BoxAttribute"/> exactly the box it names, so a
    /// scroller's <c>scrollHeight</c> is its own 100px unless a child it renders reaches past it.
    /// </summary>
    private const string PageHtml =
        "<!DOCTYPE html><html><head><title>t</title></head><body>" +
        "<div id=\"h1\"><div id=\"l1\" slot=\"a\" data-box=\"0,0,50,400\"></div></div>" +
        "<div id=\"h2\"><div id=\"l2\" slot=\"a\" data-box=\"0,0,50,400\"></div></div>" +
        "<div id=\"h3\"><div id=\"l3\" slot=\"a\" data-box=\"0,0,50,400\"></div></div>" +
        "<div id=\"out\"></div>" +
        "</body></html>";

    /// <summary>
    /// <c>#sc1</c> holds the only slot of its tree. <c>#sc2</c>'s slot is preceded by a same-named
    /// slot outside it, so the child is assigned outside <c>#sc2</c>; <c>#sc3</c>'s slot comes
    /// first and the same-named one after it sits outside, so the child is assigned inside
    /// <c>#sc3</c>. The two differ only in the order of two identical slots.
    /// </summary>
    private const string Helpers = """
        function namedSlot() {
          var element = document.createElement('slot');
          element.setAttribute('name', 'a');
          return element;
        }
        function scroller(id) {
          var element = document.createElement('div');
          element.setAttribute('id', id);
          element.setAttribute('data-box', '0,0,100,100');
          element.setAttribute('style', 'overflow: auto');
          return element;
        }
        var oneSlot = document.getElementById('h1').attachShadow({ mode: 'open' });
        var sc1 = scroller('sc1');
        sc1.appendChild(namedSlot());
        oneSlot.appendChild(sc1);

        var slotOutsideFirst = document.getElementById('h2').attachShadow({ mode: 'open' });
        slotOutsideFirst.appendChild(namedSlot());
        var sc2 = scroller('sc2');
        sc2.appendChild(namedSlot());
        slotOutsideFirst.appendChild(sc2);

        var slotInsideFirst = document.getElementById('h3').attachShadow({ mode: 'open' });
        var sc3 = scroller('sc3');
        sc3.appendChild(namedSlot());
        slotInsideFirst.appendChild(sc3);
        slotInsideFirst.appendChild(namedSlot());
        """;

    [Fact]
    public void ASlottableRendersInTheFirstAcceptingSlotAndNoOther()
    {
        // What changed: the walk asks DomSlotting.GetAssignedNodes which children this slot renders,
        // rather than asking whether this slot's name accepts them. The two markups differ only in
        // which of the two identical slots comes first in the shadow tree: #sc2's slot is the second
        // one, so its child is assigned outside it and #sc2 scrolls nothing (100, its own box);
        // #sc3's slot is the first, so the 400px child is its scrollable overflow. The name test
        // answered 400 for both, measuring the same child inside every scroller whose slot happened
        // to share a name.
        Assert.Equal(
            "outerSlotFirst=100 innerSlotFirst=400",
            Run("'outerSlotFirst=' + sc2.scrollHeight + ' innerSlotFirst=' + sc3.scrollHeight"));
    }

    [Fact]
    public void TheOnlySlotOfAShadowTreeStillMeasuresItsAssignedChild()
    {
        // The unambiguous case, unchanged by the cutover and the guard on it: with one slot in the
        // tree, the first accepting slot *is* that slot, so its host's 400px light child is still
        // scrollable overflow of the scroller that renders it. A 100 here would mean the new call
        // assigns nothing at all rather than assigning once.
        Assert.Equal("single=400", Run("'single=' + sc1.scrollHeight"));
    }

    /// <summary>
    /// Runs <paramref name="expression"/> against the fixture with a layout view bound, since the
    /// geometry entry points answer exclusively from the shared snapshot and the default (null)
    /// view reports no boxes at all.
    /// </summary>
    private static string Run(string expression)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = static () => new BoxAttributeLayoutView(),
        }));

        var html = engine.Execute([Helpers, PageProbe.GuardedProbe(expression)], PageHtml, PageUrl);

        Assert.NotNull(html);

        return PageProbe.OutOf(html!);
    }

    /// <summary>
    /// A layout view that lays nothing out: an element carrying <c>data-box="left,top,width,height"</c>
    /// gets exactly that rectangle at all three box-model levels, and every other element gets no box.
    /// That is enough to make scrollable-overflow geometry observable from page script, which is the
    /// only page-visible surface the rendered-children walk has.
    /// </summary>
    private sealed class BoxAttributeLayoutView : ILayoutView
    {
        public IReadOnlyDictionary<DomElement, BoxGeometry> GetGeometry(
            DomDocument document, SizeF viewport, string baseUrl,
            Func<DomElement, DomDocument?>? contentDocumentResolver = null)
        {
            var boxes = new Dictionary<DomElement, BoxGeometry>(ReferenceEqualityComparer.Instance);
            Collect(document, boxes);
            return boxes;
        }

        public void Dispose()
        {
        }

        private static void Collect(DomNode node, Dictionary<DomElement, BoxGeometry> boxes)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child is DomElement element)
                {
                    if (TryReadBox(element.GetAttribute(BoxAttribute), out var rect))
                        boxes[element] = new BoxGeometry(rect, rect, rect);

                    // A shadow tree containing a slot is left attached in the render projection
                    // rather than unwrapped onto its host, so its elements are reachable only
                    // through the shadow root.
                    if (element.InternalShadowRoot is { } shadowRoot)
                        Collect(shadowRoot, boxes);
                }

                Collect(child, boxes);
            }
        }

        private static bool TryReadBox(string? specification, out RectangleF rect)
        {
            rect = default;
            if (string.IsNullOrWhiteSpace(specification))
                return false;

            var parts = specification.Split(',');
            if (parts.Length != 4)
                return false;

            var values = new float[4];
            for (var index = 0; index < parts.Length; index++)
            {
                if (!float.TryParse(parts[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]))
                    return false;
            }

            rect = new RectangleF(values[0], values[1], values[2], values[3]);
            return true;
        }
    }
}

/// <summary>
/// The hit-testing half of <see cref="DependencyAlignmentTests"/>, in its own class because these
/// cases drive a bridge session with a layout view injected into it.
/// <para>
/// CHANGED: a <c>&lt;td&gt;</c>/<c>&lt;th&gt;</c> now hit-tests against the box the layout engine
/// gave it, like every other element. The bridge used to answer for a cell by taking the
/// <em>table's</em> rect and cutting it into a uniform grid — table width ÷ the widest row's cell
/// count, table height ÷ row count, less a border-spacing that fell back to a hardcoded 2px. That
/// arithmetic cannot see <c>colspan</c>, <c>rowspan</c>, per-column widths, per-cell borders and
/// padding, or a caption, and because it was tried <em>first</em> it masked the real box for every
/// table whose columns are not all the same width. <c>Broiler.Layout</c> lays the table out
/// properly, so the cell branch is gone and a cell falls through to the same shared geometry
/// snapshot the surrounding code reads for every other element.
/// </para>
/// <para>
/// The fixture declares the layout it means through <see cref="DeclaredBoxLayoutView"/> rather than
/// through a real renderer, which this suite does not reference: what is under test is which box
/// the bridge asks for, not how an engine arrives at one.
/// </para>
/// </summary>
public class DependencyAlignmentHitTestTests
{
    private const string PageUrl = "https://example.test/pages/table.html";

    /// <summary>
    /// Two columns of very unequal width, and a second row whose single cell spans both — the
    /// markup class the uniform-grid arithmetic could not represent at all.
    /// </summary>
    private const string TablePage =
        "<html><body><table id=\"grid\">" +
        "<tr><td id=\"wide\">wwwwwwwwwwwwwwwwwwwwwwww</td><td id=\"narrow\">n</td></tr>" +
        "<tr><td id=\"spanned\" colspan=\"2\">s</td></tr>" +
        "</table><div id=\"out\"></div></body></html>";

    /// <summary>
    /// The boxes the layout engine is taken to have produced for <see cref="TablePage"/>: the first
    /// column is wide because its content is, the second is narrow, the spanning cell is the full
    /// content width, and the table's own box runs 10px past the last row — border and spacing that
    /// belong to no cell.
    /// </summary>
    private static readonly Dictionary<string, System.Drawing.RectangleF> TableBoxes = new()
    {
        ["grid"] = new System.Drawing.RectangleF(10, 10, 300, 50),
        ["wide"] = new System.Drawing.RectangleF(10, 10, 240, 20),
        ["narrow"] = new System.Drawing.RectangleF(250, 10, 60, 20),
        ["spanned"] = new System.Drawing.RectangleF(10, 30, 300, 20),
    };

    /// <summary>
    /// The <c>id</c> of the topmost element at (<paramref name="x"/>, <paramref name="y"/>), or
    /// <c>none</c> when the point hits nothing at all.
    /// </summary>
    private static string ElementIdAt(int x, int y)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredBoxLayoutView(TableBoxes),
        }));

        var script = PageProbe.Probe($"(document.elementFromPoint({x}, {y}) || {{ id: 'none' }}).id");
        var html = engine.Execute([script], TablePage, PageUrl);

        Assert.NotNull(html);

        return PageProbe.OutOf(html!);
    }

    /// <summary>
    /// CHANGED: the point sits in the wide first column, two thirds of the way across the table.
    /// The uniform grid put the column boundary at the table's midpoint and answered
    /// <c>narrow</c> — a cell the point is nowhere near.
    /// </summary>
    [Fact]
    public void ACellHitTestsAgainstItsOwnLaidOutBox() =>
        Assert.Equal("wide", ElementIdAt(200, 20));

    /// <summary>
    /// CHANGED: a cell spanning both columns is hit across the whole span. The grid arithmetic gave
    /// every cell exactly one column's width whatever its <c>colspan</c>, so the right-hand half of
    /// this cell belonged to no cell at all and the hit fell through to the table.
    /// </summary>
    [Fact]
    public void ACellSpanningTwoColumnsIsHitAcrossBothOfThem() =>
        Assert.Equal("spanned", ElementIdAt(280, 40));

    /// <summary>
    /// CHANGED: the column boundary is the one layout computed (x=250), not table width ÷ column
    /// count. The outer two coordinates are where the old arithmetic and the real boxes disagree.
    /// </summary>
    [Theory]
    [InlineData(20, "wide")]
    [InlineData(249, "wide")]
    [InlineData(250, "narrow")]
    [InlineData(309, "narrow")]
    public void TheColumnBoundaryIsWhereLayoutPutIt(int x, string expectedId) =>
        Assert.Equal(expectedId, ElementIdAt(x, 20));

    /// <summary>
    /// CHANGED: a point inside the table but below its last row belongs to the table, not to a
    /// cell. The grid arithmetic divided the table's whole height among the rows, so the table's
    /// own trailing border and spacing hit-tested as cell content.
    /// </summary>
    [Fact]
    public void ThePartOfTheTableBelowTheLastRowIsTheTable() =>
        Assert.Equal("grid", ElementIdAt(100, 55));

    /// <summary>
    /// PRESERVED: a point outside the table's box hits no part of the table. Nothing about the
    /// change widens what a table or a cell claims.
    /// </summary>
    [Fact]
    public void APointOutsideTheTableHitsNoPartOfIt() =>
        Assert.DoesNotContain(ElementIdAt(400, 200), new[] { "grid", "wide", "narrow", "spanned" });

    /// <summary>
    /// A layout view that reports the border box declared for an element's <c>id</c> and no box at
    /// all for anything else — the narrow slice of <c>Broiler.Layout.ILayoutView</c> the bridge's
    /// geometry snapshot consumes. Keyed by <c>id</c> rather than by element because the document
    /// it is asked about is the bridge's render projection, a clone of the live tree.
    /// </summary>
    private sealed class DeclaredBoxLayoutView : Broiler.Layout.ILayoutView
    {
        private readonly IReadOnlyDictionary<string, System.Drawing.RectangleF> _borderBoxesById;

        internal DeclaredBoxLayoutView(IReadOnlyDictionary<string, System.Drawing.RectangleF> borderBoxesById) =>
            _borderBoxesById = borderBoxesById;

        public IReadOnlyDictionary<Broiler.Dom.DomElement, Broiler.Layout.BoxGeometry> GetGeometry(
            Broiler.Dom.DomDocument document,
            System.Drawing.SizeF viewport,
            string baseUrl,
            Func<Broiler.Dom.DomElement, Broiler.Dom.DomDocument?>? contentDocumentResolver = null)
        {
            var geometry = new Dictionary<Broiler.Dom.DomElement, Broiler.Layout.BoxGeometry>(
                ReferenceEqualityComparer.Instance);
            Collect(document, geometry);
            return geometry;
        }

        public void Dispose()
        {
        }

        private void Collect(
            Broiler.Dom.DomNode node,
            Dictionary<Broiler.Dom.DomElement, Broiler.Layout.BoxGeometry> geometry)
        {
            foreach (var element in node.ChildElements)
            {
                if (element.GetAttributeByQualifiedName("id") is { } id &&
                    _borderBoxesById.TryGetValue(id, out var borderBox))
                {
                    // One rectangle for all three box-model levels: hit testing reads the border
                    // box, and a fixture that declared three would be pinning the box model rather
                    // than which box the bridge looks up.
                    geometry[element] = new Broiler.Layout.BoxGeometry(borderBox, borderBox, borderBox);
                }

                Collect(element, geometry);
            }
        }
    }
}

/// <summary>
/// Answers that changed when the speculative preload scanner stopped deciding for itself which
/// <c>&lt;base href&gt;</c> sets a document's base and asked
/// <c>Broiler.Dom.Html.HtmlDocumentQueries.GetEffectiveBaseHref</c> — the same reading of HTML
/// §4.2.3 the DOM walk and the WPT stylesheet inliner already share through <c>HtmlBaseHref</c>.
/// <para>
/// The scanner's copy took the first <c>href</c> of any non-zero length and did not trim it. A
/// blank one therefore won, resolved to nothing, and left the whole document falling back to the
/// page URL — so every speculated request on such a page was keyed to a URL no consume site would
/// ever ask for, and the scan doubled the page's requests instead of overlapping them. That is the
/// one failure the scanner exists to prevent, which is why the divergence is worth pinning.
/// </para>
/// </summary>
public class PreloadScanBaseHrefAlignmentTests
{
    private const string PageUrl = "https://example.test/pages/doc.html";

    /// <summary>
    /// A document carrying <paramref name="head"/> plus one stylesheet and one image, so both the
    /// base itself and a URL resolved against it are observable.
    /// </summary>
    private static PreloadScanResult ScanWith(string head) =>
        PreloadScanner.Scan(
            "<!DOCTYPE html><html><head>" + head + "</head><body>" +
            "<link rel=\"stylesheet\" href=\"s.css\"><img src=\"i.png\"></body></html>",
            PageUrl);

    /// <summary>
    /// CHANGED: a <c>&lt;base&gt;</c> whose <c>href</c> is only whitespace does not count, and the
    /// scan goes on to the next one. The copy latched the blank value and the document silently
    /// fell back to the page URL, taking every candidate's resolved URL with it — which is what the
    /// two resolved-URL assertions here are for.
    /// </summary>
    [Fact]
    public void BlankBaseHrefIsSkippedForTheNextUsableOne()
    {
        var scan = ScanWith("<base href=\"   \"><base href=\"/assets/\">");

        Assert.Equal("https://example.test/assets/", scan.DocumentBaseUrl);
        Assert.Equal(
            new[] { "https://example.test/assets/s.css" },
            scan.ResolvedUrls(PreloadKind.StyleSheet));
        Assert.Equal(
            new[] { "https://example.test/assets/i.png" },
            scan.ResolvedUrls(PreloadKind.Image));
    }

    /// <summary>
    /// CHANGED: the <c>href</c> is trimmed before it is resolved.
    /// <para>
    /// The copy passed the padded value to the URL resolver, whose path-absolute check tests the
    /// first character and so does not recognise <c>"  /assets/  "</c> as root-relative. The value
    /// then fell through to absolute-URI parsing, where a leading slash is an absolute <em>file</em>
    /// path on Unix and nothing at all on Windows — the very trap that resolver's own remarks
    /// document for the unpadded form. Trimming upstream means there is one answer on both.
    /// </para>
    /// </summary>
    [Fact]
    public void BaseHrefIsTrimmedBeforeResolution()
    {
        var scan = ScanWith("<base href=\"  /assets/  \">");

        Assert.Equal("https://example.test/assets/", scan.DocumentBaseUrl);
        Assert.Equal(
            new[] { "https://example.test/assets/s.css" },
            scan.ResolvedUrls(PreloadKind.StyleSheet));
    }

    /// <summary>
    /// PRESERVED: the first <c>&lt;base&gt;</c> carrying a usable <c>href</c> wins, and one with no
    /// <c>href</c> attribute or an empty one is not it.
    /// </summary>
    [Theory]
    [InlineData("<base href=\"/first/\"><base href=\"/second/\">")]
    [InlineData("<base><base href=\"/first/\">")]
    [InlineData("<base href=\"\"><base href=\"/first/\">")]
    public void FirstUsableBaseWins(string head) =>
        Assert.Equal("https://example.test/first/", ScanWith(head).DocumentBaseUrl);

    /// <summary>
    /// PRESERVED: a <c>&lt;base&gt;</c> inside a <c>&lt;template&gt;</c> is not an element of the
    /// document, so it does not set the base — nesting included. A self-closing
    /// <c>&lt;template/&gt;</c> opens no contents, so the one after it does count.
    /// </summary>
    [Theory]
    [InlineData("<template><base href=\"/inert/\"></template><base href=\"/real/\">")]
    [InlineData("<template><template><base href=\"/inert/\"></template></template><base href=\"/real/\">")]
    [InlineData("<template/><base href=\"/real/\">")]
    public void BaseInsideAnInertContainerDoesNotCount(string head) =>
        Assert.Equal("https://example.test/real/", ScanWith(head).DocumentBaseUrl);

    /// <summary>
    /// PRESERVED: a document naming no base keeps the page URL — including one whose only
    /// <c>&lt;base</c> is a different tag that starts the same way, or a mention inside a comment.
    /// The shared query's substring fast path admits both; the answer must still be "no base".
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("<basefont color=\"red\">")]
    [InlineData("<!-- <base href=\"/commented/\"> -->")]
    public void ADocumentWithNoBaseKeepsThePageUrl(string head) =>
        Assert.Equal(PageUrl, ScanWith(head).DocumentBaseUrl);

    /// <summary>
    /// PRESERVED: taking the base rule out of the token pass left that pass's own inert-depth
    /// counter in place, so a resource inside a <c>&lt;template&gt;</c> is still never speculated
    /// on. The counter served both jobs and only one of them moved; this is the half that stayed.
    /// </summary>
    [Fact]
    public void ResourcesInsideAnInertContainerAreStillSkipped()
    {
        var scan = PreloadScanner.Scan(
            "<!DOCTYPE html><html><head><base href=\"/assets/\"></head><body>" +
            "<template><img src=\"inert.png\"></template>" +
            "<noscript><img src=\"noscript.png\"></noscript>" +
            "<img src=\"real.png\"></body></html>",
            PageUrl);

        Assert.Equal(new[] { "real.png" }, scan.RawUrls(PreloadKind.Image));
        Assert.Equal(
            new[] { "https://example.test/assets/real.png" },
            scan.ResolvedUrls(PreloadKind.Image));
    }
}

/// <summary>
/// Answers that changed when <c>DomBridgeUtils.SplitTransformFunctions</c> stopped scanning for the
/// next <c>')'</c> and asked <c>Broiler.CSS.CssSyntax.FindMatching</c> for the one that actually
/// closes the function.
/// <para>
/// CHANGED: a transform function whose argument is itself a function — <c>calc()</c>, <c>min()</c>,
/// <c>clamp()</c>, <c>var()</c> — used to end at the inner bracket. The first argument came out
/// unterminated, the scan resumed in the middle of the value, and everything after the nested call
/// was either mis-named or lost, so the whole declaration collapsed to identity through
/// <c>ParseTransformFunction</c>'s default arm. <c>FindMatching</c> counts nesting (and skips
/// strings and comments), so the arguments after a nested one are read and applied.
/// </para>
/// <para>
/// The boxes come from a declared layout view rather than a real renderer: what is under test is
/// which transform functions the bridge reads out of the value, and a translation is independent of
/// <c>transform-origin</c>, so the box only has to exist.
/// </para>
/// </summary>
public class DependencyAlignmentTransformSplitTests
{
    private const string PageUrl = "https://example.test/pages/transform.html";

    /// <summary>A box at the origin, so <c>getBoundingClientRect().top</c> IS the translation.</summary>
    private static readonly Dictionary<string, RectangleF> ProbeBoxes = new()
    {
        ["t"] = new RectangleF(0, 0, 100, 50),
    };

    /// <summary>
    /// <c>#t</c>'s <c>getBoundingClientRect().top</c> after <paramref name="probeMarkup"/> is parsed
    /// as the page's only content besides the probe.
    /// </summary>
    private static string RectTopOf(string probeMarkup)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = static () => new IdBoxLayoutView(ProbeBoxes),
        }));

        var html = engine.Execute(
            [PageProbe.Probe("document.getElementById('t').getBoundingClientRect().top")],
            "<!DOCTYPE html><html><head><title>t</title></head><body>" +
            probeMarkup + "<div id=\"out\"></div></body></html>",
            PageUrl);

        Assert.NotNull(html);

        return PageProbe.OutOf(html!);
    }

    /// <summary>
    /// CHANGED: <c>translate()</c>'s second argument survives a first argument that contains
    /// parentheses. The old split ended the function at calc's <c>)</c>, so <c>translate</c> was
    /// handed the single unterminated argument <c>calc(100% - 10px</c>, the y translation was
    /// absent, and the matrix came out as identity — top stayed at the box's own 0.
    /// </summary>
    [Fact]
    public void ANestedFunctionArgumentDoesNotEndTheFunction() =>
        Assert.Equal(
            "20",
            RectTopOf("<div id=\"t\" style=\"transform: translate(calc(100% - 10px), 20px)\"></div>"));

    /// <summary>
    /// CHANGED: the function <em>after</em> one with a nested argument is still read. The old scan
    /// resumed just past calc's <c>)</c>, so the next name it cut out was <c>") translatey"</c> —
    /// no switch arm, identity, and the 30px translation was silently dropped.
    /// </summary>
    [Fact]
    public void AFunctionFollowingANestedArgumentIsStillApplied() =>
        Assert.Equal(
            "30",
            RectTopOf("<div id=\"t\" style=\"transform: translateX(calc(1px + 1px)) translateY(30px)\"></div>"));

    /// <summary>
    /// PRESERVED: the ordinary values, which have no nesting for the two scans to disagree about.
    /// </summary>
    [Theory]
    [InlineData("<div id=\"t\" style=\"transform: translateY(20px)\"></div>", "20")]
    [InlineData("<div id=\"t\" style=\"transform: translateX(10px) translateY(40px)\"></div>", "40")]
    [InlineData("<div id=\"t\" style=\"transform: none\"></div>", "0")]
    [InlineData("<div id=\"t\"></div>", "0")]
    public void AnUnnestedTransformIsReadAsBefore(string probeMarkup, string expectedTop) =>
        Assert.Equal(expectedTop, RectTopOf(probeMarkup));

    /// <summary>
    /// PRESERVED: a function whose parenthesis never closes contributes nothing, leaves any
    /// complete function before it standing, and — the point of these cases — does not throw.
    /// <c>FindMatching</c> reports <c>text.Length - 1</c> rather than <c>-1</c> when it finds no
    /// match, which for a value ending in <c>'('</c> is the opening index itself; the guard compares
    /// the two indices, so the argument range below it stays in bounds. The values arrive as a
    /// presentation attribute because an unclosed function does not survive the CSS declaration
    /// parser.
    /// </summary>
    [Theory]
    [InlineData("translateY(", "0")]
    [InlineData("translateY(20px", "0")]
    [InlineData("translateY(20px) translateX(", "20")]
    public void AnUnclosedFunctionIsDroppedWithoutDisturbingTheRest(string transformAttribute, string expectedTop) =>
        Assert.Equal(expectedTop, RectTopOf($"<div id=\"t\" transform=\"{transformAttribute}\"></div>"));

    /// <summary>
    /// A layout view that gives an element with a declared <c>id</c> exactly the border box named
    /// for it and every other element none — the slice of <c>ILayoutView</c> the bridge's geometry
    /// snapshot reads. Keyed by <c>id</c> because the document it is handed is the render
    /// projection, a clone of the live tree.
    /// </summary>
    private sealed class IdBoxLayoutView : ILayoutView
    {
        private readonly IReadOnlyDictionary<string, RectangleF> _borderBoxesById;

        internal IdBoxLayoutView(IReadOnlyDictionary<string, RectangleF> borderBoxesById) =>
            _borderBoxesById = borderBoxesById;

        public IReadOnlyDictionary<DomElement, BoxGeometry> GetGeometry(
            DomDocument document, SizeF viewport, string baseUrl,
            Func<DomElement, DomDocument?>? contentDocumentResolver = null)
        {
            // DomElement overrides neither Equals nor GetHashCode, so the default comparer is
            // already reference equality, which is what a per-node geometry map wants.
            var geometry = new Dictionary<DomElement, BoxGeometry>();
            Collect(document, geometry);
            return geometry;
        }

        public void Dispose()
        {
        }

        private void Collect(DomNode node, Dictionary<DomElement, BoxGeometry> geometry)
        {
            foreach (var element in node.ChildElements)
            {
                if (element.GetAttributeByQualifiedName("id") is { } id &&
                    _borderBoxesById.TryGetValue(id, out var borderBox))
                {
                    geometry[element] = new BoxGeometry(borderBox, borderBox, borderBox);
                }

                Collect(element, geometry);
            }
        }
    }
}

/// <summary>
/// Answers that changed when <c>DomBridgeUtils.TryParsePx</c> and <c>TryParsePercent</c> — the
/// bridge's two workhorse CSS numeric parsers, behind ~94 call sites — stopped stripping the unit
/// themselves and handing the rest to <c>double.TryParse(NumberStyles.Float, …)</c>, and became a
/// unit test over <c>Broiler.CSS.CssValueParser.TryParseNumeric</c>.
/// <para>
/// CHANGED: <c>NumberStyles.Float</c> accepts .NET's <c>NaN</c> and <c>Infinity</c> symbols, so
/// <c>"NaNpx"</c> and <c>"Infinitypx"</c> were <em>successful</em> parses that put a NaN or an
/// infinity into geometry. The shared parser requires a digit and rejects both, so such a value now
/// takes the caller's not-a-length path. It also does not scan an exponent, which css-syntax-3
/// §4.3.12 allows, so <c>TryParseExponentNumber</c> covers that one shape rather than letting a
/// valid length regress; that gap is <c>CssValueParser</c>'s to close, and when it does the
/// fallback becomes dead rather than wrong.
/// </para>
/// <para>
/// The surface these cases read through is SVG font-relative length resolution during render
/// serialization: <c>2em</c> on an SVG geometry attribute is baked to pixels against the nearest
/// <em>specified</em> <c>font-size</c>, which <c>TryParsePx</c> reads. It is the one place the
/// helper's answer reaches the serialized page, and the specified map keeps an inline declaration
/// verbatim, which is what lets a case name the exact string being parsed.
/// </para>
/// </summary>
public class DependencyAlignmentCssNumericParseTests
{
    private const string PageUrl = "https://example.test/pages/lengths.html";

    /// <summary>
    /// The <c>width</c> the <c>&lt;rect&gt;</c> is serialized with when the nearest specified
    /// <c>font-size</c> is <paramref name="fontSize"/>. <c>2em</c> resolves to twice whatever that
    /// font size parses to — or, when it parses to nothing at all, to twice the 16px document-root
    /// default.
    /// </summary>
    private static string SerializedRectWidthFor(string fontSize)
    {
        var html = PageProbe.Render(
            [PageProbe.Probe("'rendered'")],
            "<!DOCTYPE html><html><head><title>t</title></head><body>" +
            $"<div style=\"font-size:{fontSize}\"><svg><rect id=\"r\" width=\"2em\"></rect></svg></div>" +
            "<div id=\"out\"></div></body></html>",
            PageUrl);

        return RectWidthOf(html);
    }

    /// <summary>
    /// CHANGED: <c>Infinity</c> and <c>NaN</c> are no longer numbers. <c>"Infinitypx"</c> parsed as
    /// +∞ and multiplied straight into the baked length; <c>"NaNpx"</c> parsed as NaN, failed the
    /// <c>pixels &gt; 0</c> test downstream, and left the <c>2em</c> standing unresolved. Both now
    /// fall through to the document-root default, so the rect is 2 × 16.
    /// </summary>
    [Theory]
    [InlineData("Infinitypx")]
    [InlineData("-Infinitypx")]
    [InlineData("NaNpx")]
    [InlineData("Infinity")]
    [InlineData("NaN")]
    public void ANonFiniteSymbolIsNotAPixelLength(string fontSize) =>
        Assert.Equal("32", SerializedRectWidthFor(fontSize));

    /// <summary>
    /// PRESERVED: a length in scientific notation, legal per css-syntax-3 §4.3.12.
    /// <c>CssValueParser</c>'s number scan stops before the exponent, so adopting it alone would
    /// have regressed this from 100 to not-a-length. <c>TryParseExponentNumber</c> covers exactly
    /// that shape, so the answer is the one the old local scan gave.
    /// </summary>
    [Theory]
    [InlineData("1e2px")]
    [InlineData("1E2px")]
    [InlineData("10e1")]
    public void AnExponentLengthStillParses(string fontSize) =>
        Assert.Equal("200", SerializedRectWidthFor(fontSize));

    /// <summary>
    /// An exponent that overflows is not a length: the fallback requires a finite result, so
    /// <c>"1e400px"</c> does not reach geometry as an infinity by the back door.
    /// </summary>
    [Fact]
    public void AnOverflowingExponentIsNotALength() =>
        Assert.Equal("32", SerializedRectWidthFor("1e400px"));

    /// <summary>
    /// PRESERVED: the contract the call sites depend on. A plain <c>px</c> length and a bare number
    /// both parse (the unit check admits <c>CssUnit.Px</c> and <c>CssUnit.None</c>); a percentage
    /// does not, which the old body enforced with a hand-written <c>'%'</c> guard and the unit check
    /// now enforces by construction; nor does any other unit; nor does a keyword.
    /// </summary>
    [Theory]
    [InlineData("10px", "20")]
    [InlineData("10PX", "20")]
    [InlineData("10", "20")]
    [InlineData(".5px", "1")]
    [InlineData("+10px", "20")]
    [InlineData("50%", "32")]
    [InlineData("10pt", "32")]
    [InlineData("2em", "32")]
    [InlineData("inherit", "32")]
    public void APixelOrUnitlessValueParsesAndNothingElseDoes(string fontSize, string expectedWidth) =>
        Assert.Equal(expectedWidth, SerializedRectWidthFor(fontSize));

    /// <summary>The <c>width</c> attribute of the first <c>&lt;rect&gt;</c> in <paramref name="html"/>.</summary>
    private static string RectWidthOf(string html)
    {
        var start = html.IndexOf("<rect", StringComparison.OrdinalIgnoreCase);
        Assert.True(start >= 0, $"no <rect> in serialized output: {html}");

        var end = html.IndexOf('>', start);
        Assert.True(end >= 0, $"unterminated <rect> in serialized output: {html}");

        var tag = html[start..end];
        const string attribute = "width=\"";
        var value = tag.IndexOf(attribute, StringComparison.Ordinal);
        Assert.True(value >= 0, $"no width attribute on the serialized <rect>: {tag}");
        value += attribute.Length;

        var close = tag.IndexOf('"', value);
        Assert.True(close >= 0, $"unterminated width attribute on the serialized <rect>: {tag}");

        return tag[value..close];
    }
}
