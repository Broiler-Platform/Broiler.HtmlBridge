using System.Globalization;

using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Every route by which a value this component cannot represent used to reach a page, read back the
/// way a page reads it, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Four independent audits found sixteen of these, in six subsystems that share no code: the
/// transform pipeline, SVG geometry attributes, the <c>line-height</c> multiplier, a frame's
/// dimension attribute, used zoom, and the image dimension getters. A fifth pass over those fixes
/// found five more, in two subsystems the audits had not named: the SVG DOM's own readers of a
/// geometry attribute, and the lengths serialization computes rather than reads back. Each was
/// closed where the value came into existence, and each has a test file of its own that says what
/// refusing means there and pins what the refusal must not disturb. Those files are the argument;
/// this one is the net.
/// </para>
/// <para>
/// It exists because the defect recurred four times, and every time for the same reason: a guard
/// written against a spelling rather than against representability, in a parser nobody thought of
/// as related to the last one. Sixteen is enough evidence to assume a seventeenth. So the suite
/// carries one class that asks every known route the same question — does a page get back a number
/// this component can represent — and a roster that has to be extended when a route is added, so
/// the next one lands here rather than in a report.
/// </para>
/// <para>
/// Deliberately not a test of the fixes. Each route is read through the API a page uses —
/// <c>getBoundingClientRect</c>, <c>scrollHeight</c>, <c>clientWidth</c>, <c>img.width</c>,
/// <c>elementFromPoint</c>, the serialized document — and never through the helper that was
/// changed, so a future refactor that moves the refusal is free to move it.
/// </para>
/// </remarks>
public class NonFiniteValueSurfaceTests
{
    private const string PageUrl = "https://example.test/non-finite-surface";

    /// <summary>
    /// The boxes the layout view declares. Nothing else gets one — in particular no SVG shape,
    /// whose geometry has to come from its attributes for those routes to mean anything.
    /// </summary>
    private static readonly Dictionary<string, System.Drawing.RectangleF> Boxes = new()
    {
        ["t"] = new System.Drawing.RectangleF(0, 0, 100, 50),
        ["v"] = new System.Drawing.RectangleF(0, 0, 200, 100),
        ["z"] = new System.Drawing.RectangleF(10, 20, 100, 50),
    };

    private static string Render(string page, IReadOnlyList<string> scripts)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredBoxLayoutView(Boxes),
        }));

        var html = engine.Execute(scripts, page, PageUrl);

        Assert.NotNull(html);

        return html!;
    }

    private static string Answer((string Page, string Expression) probe) =>
        PageProbe.OutOf(Render(probe.Page, [PageProbe.GuardedProbe(probe.Expression)]));

    // -- the pages ----------------------------------------------------------------------------

    private static string Body(string markup) =>
        "<!DOCTYPE html><html><head><title>t</title></head><body>" +
        markup + "<div id=\"out\"></div></body></html>";

    /// <summary>A transformed 100x50 box at the origin.</summary>
    private static string TransformPage(string style) =>
        Body($"<div id=\"t\" style=\"{style}\"></div>");

    /// <summary>An <c>&lt;svg&gt;</c> with a declared box and children that have none.</summary>
    private static string SvgPage(string children, string viewportAttributes = "") =>
        Body($"<svg id=\"v\"{viewportAttributes}>{children}</svg>");

    /// <summary>A six-option list box at a pinned font size.</summary>
    private static string SelectPage(string lineHeight) =>
        "<!DOCTYPE html><html><head><style>#s { font-size: 16px; line-height: " + lineHeight + "; }" +
        "</style></head><body><select id=\"s\" size=\"4\">" +
        "<option>a</option><option>b</option><option>c</option>" +
        "<option>d</option><option>e</option><option>f</option>" +
        "</select><div id=\"out\"></div></body></html>";

    private static string FramePage(string frameAttributes, string frameHead = "") =>
        Body("<iframe id=\"f\" " + frameAttributes + " srcdoc=\"<!DOCTYPE html><html><head>" +
             frameHead + "</head><body><div id='w'></div></body></html>\"></iframe>");

    /// <summary><c>#t</c>'s or another element's client rect as <c>left,top,width,height</c>.</summary>
    private static string RectProbe(string id) =>
        $"(function () {{ var b = document.getElementById('{id}').getBoundingClientRect();" +
        " return [b.left, b.top, b.width, b.height].join(','); })()";

    // -- the roster ---------------------------------------------------------------------------

    /// <summary>
    /// Every route whose answer is a number or a list of them, keyed by the identifier the audits
    /// and the commit messages use. The value is the page and the expression a script evaluates on
    /// it; the assertion is the same for all of them, which is the point.
    /// </summary>
    private static readonly Dictionary<string, (string Page, string Expression)> NumericProbes = new()
    {
        // -- the transform pipeline, closed in DomBridgeUtils/Animations.cs -----------------
        ["transform-translate-px-infinity"] =
            (TransformPage("transform: translateX(1e400px)"), RectProbe("t")),
        ["transform-matrix-component"] =
            (TransformPage("transform: matrix(1e400, 0, 0, 1, 0, 0)"), RectProbe("t")),
        ["transform-rotate-angle-nan"] =
            (TransformPage("transform: rotate(1e400deg)"), RectProbe("t")),
        ["transform-scale-ratio"] =
            (TransformPage("transform: scale(1e400)"), RectProbe("t")),
        ["transform-translate-percent"] =
            (TransformPage("transform: translate(1e400%, 0)"), RectProbe("t")),
        ["transform-chain-non-finite-matrix"] =
            (TransformPage("transform: rotate(90deg); transform-origin: 1e400px 0"), RectProbe("t")),
        ["transform-composition-overflow"] =
            (TransformPage("transform: scale(1e200) scale(1e200)"), RectProbe("t")),

        // -- SVG geometry, closed in DomBridge/LayoutMetrics.Svg.cs -------------------------
        ["svg-geometry-attr-extent-infinity"] =
            (SvgPage("<rect id=\"r\" x=\"10\" y=\"10\" width=\"1e400\" height=\"20\"></rect>"),
             RectProbe("r")),
        ["svg-geometry-attr-extent-nan"] =
            (SvgPage("<rect id=\"r\" x=\"10\" y=\"10\" width=\"NaN\" height=\"20\"></rect>"),
             RectProbe("r")),
        ["svg-geometry-attr-origin-infinity"] =
            (SvgPage("<rect id=\"r\" x=\"1e400\" y=\"10\" width=\"20\" height=\"20\"></rect>"),
             RectProbe("r")),
        ["svg-text-coordinate-scalar-fallback"] =
            (SvgPage("<g id=\"g\"><text x=\"1e400\" y=\"50\">hello</text></g>"), RectProbe("g")),
        ["svg-textpath-moveto-digit-overflow"] =
            (SvgPage($"<defs><path id=\"p\" d=\"M {new string('1', 401)} 5\"></path></defs>" +
                     "<g id=\"g\"><text><textPath href=\"#p\">hi</textPath></text></g>"),
             RectProbe("g")),
        ["svg-resolved-rect-overflows-on-the-mapping"] =
            (SvgPage("<rect id=\"r\" x=\"0\" y=\"0\" width=\"1e308\" height=\"1\"></rect>",
                     " viewBox=\"0 0 20 10\""),
             RectProbe("r")),

        // -- the line-height multiplier, closed in DomBridge/LayoutMetrics.Geometry.cs ------
        ["line-height-multiplier-select-scrollheight"] =
            (SelectPage("1e400"), "document.getElementById('s').scrollHeight"),
        ["select-row-product-overflow"] =
            (SelectPage("1e307"), "document.getElementById('s').scrollHeight"),

        // -- the frame dimension attribute, same file ---------------------------------------
        ["frame-dimension-attribute-viewport-length"] =
            (FramePage("width='1e400' height='1e400'"),
             "(function () { var d = document.getElementById('f').contentDocument;" +
             " return [d.documentElement.clientWidth, d.documentElement.clientHeight].join(','); })()"),

        // -- used zoom, closed in DomBridge/LayoutMetrics.Svg.cs ----------------------------
        ["used-zoom-roundtrip-nan-rect"] =
            (Body("<div id=\"z\" style=\"zoom: 1e400\">z</div>"),
             RectProbe("z") + " + ',' + document.getElementById('z').offsetWidth"),

        // -- the image dimension getters, closed in Features/ComputedStyleBinding.cs --------
        ["img-used-dimension-css-length"] =
            (Body("<img id=\"i\" style=\"width: 1e400px\">"), "document.getElementById('i').width"),
        ["img-used-dimension-content-attribute"] =
            (Body("<img id=\"i\" width=\"1e400\">"), "document.getElementById('i').width"),

        // -- the SVG DOM's own readers of the same attributes, closed in
        //    Features/SvgElementBinding.cs. The geometry side of these was closed a round
        //    earlier; these are the IDL stubs, which is why one attribute answered 0,0,0,0 to
        //    getBoundingClientRect() and Infinity to width.baseVal.value on the same page.
        ["svg-dom-animated-length-idl"] =
            (SvgPage("<rect id=\"r\" width=\"1e400\" height=\"10\"></rect>"),
             "document.getElementById('r').width.baseVal.value"),
        ["svg-dom-viewbox-rect-idl"] =
            (SvgPage("<rect id=\"r\" width=\"10\" height=\"10\"></rect>", " viewBox=\"0 0 1e400 10\""),
             "(function () { var b = document.getElementById('v').viewBox.baseVal;" +
             " return [b.x, b.y, b.width, b.height].join(','); })()"),
        ["svg-dom-text-metric-font-size"] =
            (SvgPage("<text id=\"tx\" font-size=\"1e400\">hello</text>"),
             "(function () { var t = document.getElementById('tx');" +
             " var p = t.getStartPositionOfChar(0);" +
             " return [t.getComputedTextLength(), p.x, p.y, t.getSubStringLength(0, 2)]" +
             ".join(','); })()"),
    };

    /// <summary>
    /// The routes whose answer is not a number. Each names what the page must get instead, and each
    /// is a value measured from the same fixture with the declaration spelled in a way this
    /// component has always refused — so "unrepresentable" and "unreadable" are demonstrably one
    /// answer rather than two that happen to agree.
    /// </summary>
    private static readonly Dictionary<string, (string Page, string Expression, string Expected)> ExactProbes = new()
    {
        // A shape with an infinite extent used to answer elementFromPoint for every point on its
        // row, 580px clear of its right edge and outside the <svg> entirely. HTML is the document
        // element, which covers the viewport and so is the honest answer for a point no shape does.
        ["svg-infinite-rect-steals-hit-test"] =
            (SvgPage("<rect id=\"r\" x=\"0\" y=\"0\" width=\"1e400\" height=\"10\"></rect>"),
             "(function () { var e = document.elementFromPoint(600, 5);" +
             " return e ? (e.id || e.tagName) : 'none'; })()",
             "HTML"),

        // The frame's media queries are sized by three parses of their own, and (int) of an
        // infinity is int.MaxValue rather than a failure — so the frame's document used to be laid
        // out in a viewport two billion pixels wide.
        ["frame-media-viewport-int-saturation"] =
            (FramePage("width='1e400'",
                       "<style media='(min-width: 2000000000px)'>#w{display:inline-block}</style>"),
             "String(getComputedStyle(document.getElementById('f').contentDocument" +
             ".getElementById('w')).display === 'inline-block')",
             "false"),
    };

    /// <summary>
    /// The routes whose surface is the document this component hands back rather than a value a
    /// script reads. Each gives the setup script and the opening tag the serialized page must carry.
    /// </summary>
    private static readonly Dictionary<string, (string Page, string[] Scripts, string Tag, string Expected)> MarkupProbes = new()
    {
        // An animate() endpoint of 401 digits overflows a double with no exponent and no symbol, and
        // the interpolated infinity was formatted back into the document. A refused pair does not
        // interpolate, so the transform steps discretely and the page keeps what it wrote.
        ["transform-keyframe-interpolation"] =
            ("<!DOCTYPE html><html><head><title>t</title></head><body><div id=\"t\"></div></body></html>",
             [
                 "document.getElementById('t').animate(" +
                 "[{ transform: 'translateX(0px)' }, { transform: 'translateX(1" +
                 new string('0', 400) + "px)' }], { duration: 100, delay: -50, fill: 'both' });",
             ],
             "<div id=\"t\"",
             "<div id=\"t\" style=\"transform: translateX(0px)\">"),

        // Serialization bakes every zoom-scaled length into an inline style, so an infinite used
        // zoom wrote `width: Infinitypx` into the markup. A refused zoom bakes nothing, which is
        // what an unreadable one has always done.
        ["used-zoom-serialization-bake"] =
            ("<!DOCTYPE html><html><head><style>" +
             "#z { zoom: 1e400; width: 100px; height: 50px; margin-left: 7px; }" +
             "</style></head><body><div id=\"z\">z</div></body></html>",
             ["1;"],
             "<div id=\"z\"",
             "<div id=\"z\">"),

        // The same bake by the other arithmetic: here the zoom is representable and so is the
        // attribute, and their product is not, so no guard at either parse could have caught it.
        // An attribute that cannot be rescaled keeps the value the page wrote.
        ["svg-attribute-zoom-scale-bake"] =
            ("<!DOCTYPE html><html><head><style>#v { zoom: 2; }</style></head><body>" +
             "<svg id=\"v\"><rect id=\"r\" x=\"0\" y=\"0\" width=\"1e308\" height=\"1\"></rect></svg>" +
             "</body></html>",
             ["1;"],
             "<rect id=\"r\"",
             "<rect id=\"r\" x=\"0\" y=\"0\" width=\"1e308\" height=\"2\">"),

        // A <progress> is serialized with a track placeholder sized from its own width, and a
        // width this component cannot represent wrote `width: Infinitypx` into that placeholder.
        // The default track length is what an unreadable width has always given.
        ["progress-track-placeholder-bake"] =
            ("<!DOCTYPE html><html><head><title>t</title></head><body>" +
             "<progress id=\"p\" value=\"0.5\" style=\"width: 1e400px\"></progress></body></html>",
             ["1;"],
             "<div style=\"position: absolute",
             "<div style=\"position: absolute; background-color: #0a84ff; top: 0; bottom: 0;" +
             " left: 0; width: 60px\">"),
    };

    public static TheoryData<string> NumericRoutes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var route in NumericProbes.Keys)
                data.Add(route);
            return data;
        }
    }

    public static TheoryData<string> ExactRoutes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var route in ExactProbes.Keys)
                data.Add(route);
            return data;
        }
    }

    public static TheoryData<string> MarkupRoutes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var route in MarkupProbes.Keys)
                data.Add(route);
            return data;
        }
    }

    // -- the assertions -----------------------------------------------------------------------

    /// <summary>
    /// The one question this class exists to ask: does the page get back a number this component
    /// can represent? Not which number — each route's own file says that, and says why. Here the
    /// only wrong answers are <c>Infinity</c>, <c>-Infinity</c> and <c>NaN</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(NumericRoutes))]
    public void NoRouteAnswersANumberThisComponentCannotRepresent(string route)
    {
        var answer = Answer(NumericProbes[route]);

        Assert.False(
            string.IsNullOrWhiteSpace(answer),
            $"{route}: the probe wrote nothing to #out");

        foreach (var token in answer.Split(','))
        {
            Assert.True(
                double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                double.IsFinite(value),
                $"{route}: answered \"{answer}\" — \"{token}\" is not a number this component can represent");
        }
    }

    /// <summary>
    /// The routes that answer something other than a number, each against the answer the same page
    /// gives for a declaration this component has always refused.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExactRoutes))]
    public void EveryNonNumericRouteAnswersWhatIsReallyThere(string route)
    {
        var (page, expression, expected) = ExactProbes[route];

        Assert.Equal(expected, Answer((page, expression)));
    }

    /// <summary>
    /// The routes whose surface is the serialized document — the output a consumer of this
    /// component actually receives, and the only place a serialize-time store can be read.
    /// </summary>
    [Theory]
    [MemberData(nameof(MarkupRoutes))]
    public void NoRouteWritesAnUnrepresentableValueIntoTheDocument(string route)
    {
        var (page, scripts, tag, expected) = MarkupProbes[route];
        var html = Render(page, scripts);

        var start = html.IndexOf(tag, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{route}: no {tag}> in serialized output: {html}");
        var end = html.IndexOf('>', start);
        Assert.True(end >= 0, $"{route}: unterminated {tag}> in serialized output: {html}");

        Assert.Equal(expected, html[start..(end + 1)]);
    }

    /// <summary>
    /// The roster is the point of the class, so it is asserted rather than left to drift. It
    /// carried twenty-three routes — sixteen assigned across four audits and the rest found while
    /// closing them — and the adversarial review of those fixes found five more, which is exactly
    /// the recurrence this class was written to expect. A twenty-ninth belongs here, which is what
    /// this fails to say.
    /// </summary>
    [Fact]
    public void TheRosterCoversEveryRouteThatWasFound() =>
        Assert.Equal(
            "numeric=22 exact=2 markup=4 total=28",
            $"numeric={NumericProbes.Count}" +
            $" exact={ExactProbes.Count}" +
            $" markup={MarkupProbes.Count}" +
            $" total={NumericProbes.Count + ExactProbes.Count + MarkupProbes.Count}");
}
