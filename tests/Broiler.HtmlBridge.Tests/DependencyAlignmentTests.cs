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
    /// complete function before it standing, and — the point of these cases — does not throw. The
    /// values arrive as a presentation attribute because an unclosed function does not survive the
    /// CSS declaration parser.
    /// <para>
    /// These three answers are what the guard on <c>FindMatching</c> has to keep producing, and the
    /// guard has changed shape under them: it used to compare the returned index against the
    /// opening one and then check the landing character, because no match was reported as
    /// <c>text.Length - 1</c>. It is now the sign test the name suggests — see
    /// <see cref="AnUnmatchedParenthesisIsReportedAsMinusOne"/> for the dependency contract that
    /// makes the two the same test.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("translateY(", "0")]
    [InlineData("translateY(20px", "0")]
    [InlineData("translateY(20px) translateX(", "20")]
    public void AnUnclosedFunctionIsDroppedWithoutDisturbingTheRest(string transformAttribute, string expectedTop) =>
        Assert.Equal(expectedTop, RectTopOf($"<div id=\"t\" transform=\"{transformAttribute}\"></div>"));

    /// <summary>
    /// CHANGED, in the dependency: <c>CssSyntax.FindMatching</c> answers <c>-1</c> when nothing
    /// closes the opening character (Broiler.CSS #54). It used to answer <c>text.Length - 1</c>,
    /// which is a valid index into the string and for a value ending in <c>'('</c> is the opening
    /// index itself — so a caller could not read the sign and had to test where it landed instead.
    /// <para>
    /// This is pinned as a case of its own because the split above no longer demonstrates it: the
    /// old guard and the new one agree on every input, which is what makes replacing one with the
    /// other safe and also means no behaviour test can tell them apart. What the new guard rests on
    /// is this contract, so this is the case that fails if a later bump takes the sentinel back.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("translateY(")]
    [InlineData("translateY(20px")]
    [InlineData("translateY(20px) translateX(")]
    public void AnUnmatchedParenthesisIsReportedAsMinusOne(string value) =>
        Assert.Equal(-1, Broiler.CSS.CssSyntax.FindMatching(value, value.LastIndexOf('('), '(', ')'));

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
/// takes the caller's not-a-length path.
/// </para>
/// <para>
/// A local <c>TryParseExponentNumber</c> stood behind both helpers for one release, because
/// <c>TryParseNumeric</c> did not scan an exponent and <c>"1e2px"</c> is a valid <c>&lt;length&gt;</c>
/// per css-syntax-3 §4.3.12. Broiler.CSS #53 closed that, so the fallback is gone — and its
/// finiteness test moved into the helpers rather than going with it: an exponent a double cannot
/// hold now parses, to ±∞, which is the dependency's deliberate answer (it matches
/// <c>CssLengthParser</c>, and CSS Values 4 §11.1 clamps rather than invalidates) and not one these
/// call sites can hold.
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
    /// PRESERVED: a length in scientific notation, legal per css-syntax-3 §4.3.12. The answer used
    /// to come from this component's own fallback, because <c>CssValueParser</c>'s number scan
    /// stopped before the exponent; since Broiler.CSS #53 it comes from <c>CssValueParser</c>
    /// itself, and it is the same answer.
    /// </summary>
    [Theory]
    [InlineData("1e2px")]
    [InlineData("1E2px")]
    [InlineData("10e1")]
    public void AnExponentLengthStillParses(string fontSize) =>
        Assert.Equal("200", SerializedRectWidthFor(fontSize));

    /// <summary>
    /// An exponent that overflows a double is not a length <em>here</em>, in either sign and with
    /// or without a unit. <c>CssValueParser.TryParseNumeric</c> answers <c>(±∞, Px)</c> for these
    /// and is not wrong to: CSS Values 4 §11.1 clamps an out-of-range number rather than making the
    /// declaration invalid, and <c>CssLengthParser</c> in the same package has always said so. But
    /// the ~94 call sites behind <c>TryParsePx</c> multiply what they are handed into geometry with
    /// no clamp of their own, which is the whole reason <c>"Infinitypx"</c> was a defect. So the
    /// helpers keep the finiteness test, and the value takes the not-a-length path: 2 × the 16px
    /// root default.
    /// </summary>
    [Theory]
    [InlineData("1e400px")]
    [InlineData("-1e400px")]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    public void AnOverflowingExponentIsNotALength(string fontSize) =>
        Assert.Equal("32", SerializedRectWidthFor(fontSize));

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

/// <summary>
/// Answers that changed when the animation pass stopped matching selectors with a stub of its own
/// and asked <c>Broiler.CSS.Dom.CssSelectorMatcher</c>.
/// <para>
/// CHANGED: <c>DomBridgeUtils.SimpleMatchesElement</c> understood a bare tag name, <c>#id</c>,
/// <c>.class</c> and <c>:root</c> — and answered <see langword="false"/> for everything else, so an
/// <c>animation</c> declared on a compound, a combinator, <c>*</c>, an attribute selector or a
/// functional pseudo-class was silently dropped. The canonical matcher answers all of them.
/// </para>
/// <para>
/// PRESERVED, and the reason the substitution was reverted once before: the canonical matcher's
/// lenient <c>Matches</c> answers <see langword="true"/> for a pseudo-class the specs define but it
/// does not model, and for any vendor-prefixed name. Over-applying a rule is the right trade for
/// the cascade and the wrong one here, where the declaration being applied is an animation.
/// <c>TryMatch</c> reports that the answer is a guess and the pass reads a guess as no match, so
/// <c>:read-only</c> still attaches its animation to nothing.
/// </para>
/// <para>
/// The bake is read out of the serialized page because that is where it lands: the resolved
/// property goes into the element's baked inline style and reaches the renderer through the
/// <c>style=</c> attribute. The interpolation is deliberately trivial — <c>linear</c> easing,
/// half-way through a <c>0px → 100px</c> width — so a matched element is exactly the ones carrying
/// <c>width: 50px</c>.
/// </para>
/// </summary>
public class DependencyAlignmentAnimationSelectorTests
{
    private const string PageUrl = "https://example.test/pages/animation-selector.html";

    /// <summary>The bake a matched element carries, at the halfway point of the fixture's animation.</summary>
    private const string Baked = "width: 50px";

    /// <summary>Every element of the fixture that has an <c>id</c>, in document order.</summary>
    private static readonly string[] Ids = ["root", "page", "outer", "first", "second", "ro"];

    [Fact]
    public void ACombinatorSelectorNowCarriesItsAnimation() =>
        Assert.Equal("first,second (2)", BakedBy("div > p"));

    [Fact]
    public void ACompoundClassSelectorNowCarriesItsAnimation() =>
        Assert.Equal("first (1)", BakedBy(".a.b"));

    [Fact]
    public void AnAttributeSelectorNowCarriesItsAnimation() =>
        Assert.Equal("first (1)", BakedBy("[data-x]"));

    [Fact]
    public void AFunctionalPseudoClassNowCarriesItsAnimation() =>
        Assert.Equal("page,second,ro (4)", BakedBy(":nth-child(2)"));

    [Fact]
    public void TheUniversalSelectorNowCarriesItsAnimation() =>
        Assert.Equal("root,page,outer,first,second,ro (9)", BakedBy("*"));

    /// <summary>
    /// The case that reverted this substitution the first time. <c>:read-only</c> is a pseudo-class
    /// the matcher recognises and does not model, so its lenient answer is <see langword="true"/>
    /// for every element in the document — which would play one keyframe animation on all of them.
    /// </summary>
    [Fact]
    public void ARecognisedButUnmodelledPseudoClassCarriesItsAnimationNowhere() =>
        Assert.Equal(" (0)", BakedBy(":read-only"));

    /// <summary>The other lenient arm: any vendor-prefixed name matches everything.</summary>
    [Fact]
    public void AVendorPrefixedPseudoClassCarriesItsAnimationNowhere() =>
        Assert.Equal(" (0)", BakedBy(":-webkit-any-link"));

    /// <summary>
    /// PRESERVED: the four shapes the stub did understand still select the same elements. A bare
    /// tag name, an <c>#id</c>, a <c>.class</c> and <c>:root</c> are what the WPT fixtures this
    /// pass was written for declare their animations on.
    /// </summary>
    [Theory]
    [InlineData("p", "first,second (2)")]
    [InlineData("#first", "first (1)")]
    [InlineData(".a", "first (1)")]
    [InlineData(":root", "root (1)")]
    [InlineData("body", "page (1)")]
    public void TheSelectorsTheStubUnderstoodSelectTheSameElements(string selector, string expected) =>
        Assert.Equal(expected, BakedBy(selector));

    /// <summary>
    /// The elements <paramref name="selector"/> ends up animating: the ones whose serialized tag
    /// carries the bake, and — in parentheses, so an animation applied document-wide is visible
    /// even on the elements with no <c>id</c> to name — how many elements carry it in all.
    /// </summary>
    private static string BakedBy(string selector)
    {
        var html = SerializeAfterAnimationSnapshot(PageHtml(selector));
        var matched = Ids.Where(id => TagOf(html, id).Contains(Baked, StringComparison.Ordinal));
        return $"{string.Join(",", matched)} ({Occurrences(html, Baked)})";
    }

    /// <summary>
    /// A document whose one style rule declares an animation on <paramref name="selector"/>, half
    /// of whose ten seconds have already elapsed when the snapshot is taken (the negative delay).
    /// </summary>
    private static string PageHtml(string selector) =>
        "<!DOCTYPE html><html id=\"root\"><head><title>t</title><style>" +
        "@keyframes grow { from { width: 0px } to { width: 100px } } " +
        selector + " { animation: grow 10s linear -5s }" +
        "</style></head><body id=\"page\">" +
        "<div id=\"outer\"><p id=\"first\" class=\"a b\" data-x=\"1\">one</p><p id=\"second\">two</p></div>" +
        "<input id=\"ro\" readonly>" +
        "</body></html>";

    /// <summary>
    /// The page as it serializes once the animation snapshot has been resolved. Nothing in this
    /// repository calls <see cref="DomBridge.ResolveAnimationSnapshots"/> — the hosts that render a
    /// page do — so the bridge is captured out of the factory the engine builds it with and the
    /// pass is driven directly, between the page's scripts and the serialization.
    /// </summary>
    private static string SerializeAfterAnimationSnapshot(string pageHtml)
    {
        var factory = new CapturingBridgeFactory();

        using var session = new ScriptEngine(factory).ExecuteInteractive(["void 0;"], [], pageHtml, PageUrl);

        Assert.NotNull(session);
        Assert.NotNull(factory.Bridge);

        factory.Bridge!.ResolveAnimationSnapshots();

        return session!.CurrentHtml();
    }

    /// <summary>The serialized open tag of the element with <paramref name="id"/>, or empty when it has none.</summary>
    private static string TagOf(string html, string id)
    {
        var attribute = $"id=\"{id}\"";
        var at = html.IndexOf(attribute, StringComparison.Ordinal);
        if (at < 0)
            return string.Empty;

        var open = html.LastIndexOf('<', at);
        var close = html.IndexOf('>', at);
        return open < 0 || close < 0 ? string.Empty : html[open..close];
    }

    private static int Occurrences(string html, string value)
    {
        var count = 0;
        for (var at = html.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = html.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>Hands the engine an ordinary bridge and keeps hold of it.</summary>
    private sealed class CapturingBridgeFactory : Dom.IDomBridgeRuntimeFactory
    {
        internal DomBridge? Bridge { get; private set; }

        public Dom.IDomBridgeRuntime Create() => Bridge = new DomBridge();
    }
}

/// <summary>
/// Confining a shadow root's style rules to its own tree, now that the selector text is read
/// through the dependency's structural model instead of re-scanned here.
/// <para>
/// The pass appends a scope marker to the <em>subject</em> compound of every complex selector in a
/// shadow tree's <c>&lt;style&gt;</c>, so <c>p</c> written inside a shadow root matches only that
/// root's paragraphs once the rule is serialized into the render document as a global one. Finding
/// the subject compound, and finding the point inside it just past the type selector, used to be
/// two hand-rolled character scans over the selector text; <c>CssSelector.Subject</c> and
/// <c>CssCompoundSelector.TypeSelectorEnd</c> are those two answers, and
/// <c>CssSyntax.SplitTopLevel</c> is the comma split above them.
/// </para>
/// <para>
/// <b>Three answers change, and each is a rewrite the old scans corrupted.</b> A selector carrying
/// a comment, a comment carrying a comma, and an escaped comma were all split or marked in the
/// middle of a token, so the rule reached the renderer as something that parses differently from
/// what the author wrote — or does not parse at all. The rest of the cases here are the shapes that
/// already worked and must keep working.
/// </para>
/// </summary>
public class DependencyAlignmentShadowScopeTests
{
    private const string PageUrl = "https://example.test/pages/shadow-scope.html";

    /// <summary>The marker the pass appends, for the fixture's single shadow root.</summary>
    private const string Marker = "[data-broiler-shadow-scope=\"0\"]";

    /// <summary>
    /// FIXED: a trailing comment. The old scan took the last whitespace run inside the comment as a
    /// combinator, so the subject compound was <c>*&#47;</c> and the marker landed between the
    /// comment's own two closing characters.
    /// </summary>
    [Fact]
    public void ACommentAfterTheSubjectCompoundIsNotTakenForACombinator() =>
        Assert.Equal($"p{Marker} /* c */{{color:red}}", Scoped("p /* c */"));

    /// <summary>
    /// FIXED: a comma inside a comment. The old comma split saw only strings and brackets, so it cut
    /// the selector list in the middle of the comment and marked each half.
    /// </summary>
    [Fact]
    public void ACommaInsideACommentDoesNotSplitTheSelectorList() =>
        Assert.Equal($"a{Marker} /*,*/ ,  b{Marker}{{color:red}}", Scoped("a /*,*/ , b"));

    /// <summary>
    /// FIXED: an escaped comma. <c>a\,b</c> is one type selector naming an element whose name
    /// contains a comma; the old split cut it in two and left a dangling backslash.
    /// </summary>
    [Fact]
    public void AnEscapedCommaDoesNotSplitTheSelectorList() =>
        Assert.Equal($"a\\,b{Marker}{{color:red}}", Scoped("a\\,b"));

    /// <summary>
    /// PRESERVED: the marker goes just past the type selector, so it precedes a pseudo-element and
    /// follows a namespace prefix, and a compound with no type selector takes it at the front.
    /// </summary>
    [Theory]
    [InlineData("p", "p{0}")]
    [InlineData("*", "*{0}")]
    [InlineData(".a.b", "{0}.a.b")]
    [InlineData("p::before", "p{0}::before")]
    [InlineData("svg|circle", "svg|circle{0}")]
    public void TheMarkerGoesJustPastTheTypeSelector(string selector, string expected) =>
        Assert.Equal(string.Format(expected, Marker) + "{color:red}", Scoped(selector));

    /// <summary>
    /// PRESERVED: only the subject compound is marked, and a combinator character inside brackets or
    /// parentheses is not one.
    /// </summary>
    [Theory]
    [InlineData("div > p", "div > p{0}")]
    [InlineData("#id .cls", "#id {0}.cls")]
    [InlineData("li:nth-child(2n + 1) > span", "li:nth-child(2n + 1) > span{0}")]
    [InlineData("a[href=\",\"]", "a{0}[href=\",\"]")]
    [InlineData("p:is(a, b)", "p{0}:is(a, b)")]
    public void OnlyTheSubjectCompoundIsMarked(string selector, string expected) =>
        Assert.Equal(string.Format(expected, Marker) + "{color:red}", Scoped(selector));

    /// <summary>
    /// PRESERVED: the three compounds that must not be narrowed to tree membership. <c>:host</c>
    /// addresses the host, which is in the light tree; <c>::slotted</c> and <c>::part</c> address
    /// light-DOM nodes. None of them gains the scope marker — what each does gain is the rewrite its
    /// own pass performs, which is the answer being preserved here.
    /// </summary>
    [Theory]
    [InlineData(":host")]
    [InlineData(":host(.x)")]
    [InlineData("::slotted(p)")]
    [InlineData("::part(x)")]
    [InlineData("div :host")]
    [InlineData("a ::part(b)")]
    public void TheCompoundsThatAddressLightDomAreNotNarrowed(string selector) =>
        Assert.DoesNotContain(Marker, Scoped(selector));

    /// <summary>
    /// The shadow tree's <c>&lt;style&gt;</c> text as it reaches the renderer, for a tree whose one
    /// rule is declared on <paramref name="selector"/>.
    /// </summary>
    private static string Scoped(string selector) =>
        StyleTextIn(PageProbe.Render(
            [PageProbe.Probe("'ok'")],
            "<html><body><div id=\"h\"><template shadowrootmode=\"open\"><style>" +
            selector + "{color:red}</style><p>s</p></template></div>" +
            "<div id=\"out\"></div></body></html>",
            PageUrl));

    /// <summary>
    /// The text of the serialized page's one <c>&lt;style&gt;</c>. Matched on the tag name alone,
    /// because whether the pass stamps the element itself is part of what varies between cases.
    /// </summary>
    private static string StyleTextIn(string html)
    {
        var open = html.IndexOf("<style", StringComparison.Ordinal);
        Assert.True(open >= 0, $"no <style> in serialized output: {html}");

        var start = html.IndexOf('>', open);
        var end = html.IndexOf("</style>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"unterminated <style> in serialized output: {html}");

        return html[(start + 1)..end];
    }
}

/// <summary>
/// The document mode a serialized page carries out, now that the doctype is read as the whole
/// name/public-id/system-id triple the HTML Standard's condition is written over.
/// <para>
/// Nothing downstream is handed this component's document; it is handed a STRING, and re-derives
/// the mode from it with <c>DocumentModeContext.IsQuirksHtml</c> — the same predicate the parse
/// applied to the markup on the way in. So the property that has to hold is that the two agree:
/// a page that parsed in quirks mode must serialise to a string that parses in quirks mode.
/// </para>
/// <para>
/// It did not hold for a legacy doctype. <c>SelectsStandardsMode</c> tested that the doctype's NAME
/// was <c>html</c>, which <c>&lt;!DOCTYPE HTML PUBLIC "-//W3C//DTD HTML 4.0 Transitional//EN"&gt;</c>
/// is — so the bare <c>&lt;!DOCTYPE html&gt;</c> went out and the document came back standards. The
/// predicate over a parsed triple is <c>DocumentModeContext.IsQuirksDoctype</c>, new in
/// <c>Broiler.Layout 0.1.0-preview.5</c>; the identifiers are still dropped from the emitted text,
/// which they always were, because it is the mode and not the spelling that has to survive.
/// </para>
/// </summary>
public class DependencyAlignmentDocumentModeTests
{
    private const string PageUrl = "https://example.test/pages/document-mode.html";

    /// <summary>The two legacy doctypes, one reached through each identifier.</summary>
    private const string Html4Transitional =
        "<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.0 Transitional//EN\">";

    private const string SystemIdentifierOnly =
        "<!DOCTYPE html SYSTEM \"http://www.ibm.com/data/dtd/v11/ibmxhtml1-transitional.dtd\">";

    /// <summary>
    /// FIXED: a doctype named <c>html</c> that selects quirks anyway, through its public identifier
    /// and through its system identifier. Neither may serialise as standards.
    /// </summary>
    [Theory]
    [InlineData(Html4Transitional)]
    [InlineData(SystemIdentifierOnly)]
    public void ALegacyDoctypeDoesNotSerializeAsStandardsMode(string doctype)
    {
        var html = Serialize(doctype);

        Assert.DoesNotContain("<!DOCTYPE", html, StringComparison.OrdinalIgnoreCase);
        Assert.True(DocumentModeContext.IsQuirksHtml(html));
    }

    /// <summary>
    /// PRESERVED: the doctypes that do select standards, including the limited-quirks XHTML 1.0
    /// Transitional the CSS2.1 <c>.xht</c> tests carry — limited quirks is not quirks, and reading
    /// the identifiers must not start treating it as such.
    /// </summary>
    [Theory]
    [InlineData("<!DOCTYPE html>")]
    [InlineData("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01//EN\" \"http://www.w3.org/TR/html4/strict.dtd\">")]
    [InlineData("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" " +
                "\"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">")]
    public void ADoctypeThatSelectsStandardsModeStillSerializesAsOne(string doctype)
    {
        var html = Serialize(doctype);

        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.False(DocumentModeContext.IsQuirksHtml(html));
    }

    /// <summary>
    /// PRESERVED: no doctype at all, and a doctype by another name. Both selected quirks under the
    /// name test and still do.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("<!DOCTYPE foo>")]
    public void ADocumentWithNoHtmlDoctypeStillSerializesWithoutOne(string doctype)
    {
        var html = Serialize(doctype);

        Assert.DoesNotContain("<!DOCTYPE", html, StringComparison.OrdinalIgnoreCase);
        Assert.True(DocumentModeContext.IsQuirksHtml(html));
    }

    /// <summary>
    /// The property the two cases above are halves of, stated over every doctype in this class: the
    /// mode the page parsed in is the mode the serialized string parses in.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("<!DOCTYPE html>")]
    [InlineData("<!DOCTYPE foo>")]
    [InlineData(Html4Transitional)]
    [InlineData(SystemIdentifierOnly)]
    [InlineData("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01//EN\" \"http://www.w3.org/TR/html4/strict.dtd\">")]
    [InlineData("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" " +
                "\"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">")]
    public void TheDocumentModeSurvivesSerialization(string doctype) =>
        Assert.Equal(
            DocumentModeContext.IsQuirksHtml(Page(doctype)),
            DocumentModeContext.IsQuirksHtml(Serialize(doctype)));

    private static string Page(string doctype) =>
        doctype + "<html><body><p>t</p><div id=\"out\"></div></body></html>";

    private static string Serialize(string doctype) =>
        PageProbe.Render([PageProbe.Probe("'ok'")], Page(doctype), PageUrl);
}
