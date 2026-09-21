using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// An SVG geometry attribute this component cannot represent must not reach geometry or hit
/// testing.
/// </summary>
/// <remarks>
/// <para>
/// SVG geometry does not go through the CSS box tree, so it does not go through the length guard
/// an earlier round put on <c>TryEvaluateCssLengthWithViewport</c> either. An SVG attribute is a
/// number <em>or</em> a CSS length, and the number spelling is read with a bare
/// <c>NumberStyles.Float</c> parse — which accepts .NET's symbolic <c>NaN</c> and
/// <c>Infinity</c>, and which overflows a double on an exponent with no symbol in it at all. So
/// <c>&lt;rect width="1e400"&gt;</c> parsed successfully, and the shape's resolved rect carried
/// <c>+∞</c> into <c>getBoundingClientRect</c> and into <c>document.elementFromPoint</c>.
/// </para>
/// <para>
/// The hit-test case is the one that is different in kind. Every other case here is a wrong
/// number a page reads; that one is an element answering for a point it does not cover, because
/// <c>IsElementHitTestCandidate</c> asks <c>x &lt; rect.Left + rect.Width</c> and
/// <c>0 + ∞</c> is true of every point on the row.
/// </para>
/// <para>
/// <b>What refusing an attribute means.</b> The SVG default for it — <c>0</c>, for a position and
/// for an extent alike. A zero extent is not a shrunken shape: SVG 1.1 §9.2 disables rendering of
/// an element whose <c>width</c>, <c>height</c> or <c>r</c> is zero, and
/// <c>TryGetSvgUserSpaceBounds</c> already implements that by refusing to produce bounds at all,
/// so the shape reports the same <c>0,0,0,0</c> that a <c>&lt;path&gt;</c> — whose bounds this
/// component does not model — has always reported, and hit testing skips it.
/// </para>
/// </remarks>
public class NonFiniteSvgGeometryTests
{
    private const string PageUrl = "https://example.test/non-finite-svg";

    /// <summary>
    /// The <c>&lt;svg&gt;</c>'s own box. Its children have none — that is the point — so this is
    /// the only box the layout view declares, and every number below is resolved from attributes
    /// against it.
    /// </summary>
    private static readonly Dictionary<string, System.Drawing.RectangleF> ViewportBox = new()
    {
        ["v"] = new System.Drawing.RectangleF(0, 0, 200, 100),
    };

    private static string Render(string viewportAttributes, string svgChildren, string probe)
    {
        var page =
            "<html><body>" +
            $"<svg id=\"v\"{viewportAttributes}>{svgChildren}</svg>" +
            "<div id=\"out\"></div></body></html>";

        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredBoxLayoutView(ViewportBox),
        }));

        var html = engine.Execute([PageProbe.Probe(probe)], page, PageUrl);

        Assert.NotNull(html);

        return PageProbe.OutOf(html!);
    }

    /// <summary>
    /// The client box of <paramref name="id"/> as <c>left,top,width,height</c> — read the way a
    /// page reads it, through <c>getBoundingClientRect</c>, rather than through the resolver under
    /// test.
    /// </summary>
    private static string RectOf(string svgChildren, string id = "r", string viewportAttributes = "") =>
        Render(viewportAttributes, svgChildren,
            $"(function () {{ var b = document.getElementById('{id}').getBoundingClientRect();" +
            " return [b.left, b.top, b.width, b.height].join(','); })()");

    /// <summary>
    /// What <c>document.elementFromPoint</c> answers at a point, by <c>id</c> or — for an element
    /// that has none — by tag name. <c>HTML</c> is the document element, which covers the whole
    /// viewport and so is the honest answer for a point inside it that no shape covers.
    /// </summary>
    private static string HitAt(string svgChildren, int x, int y) =>
        Render(string.Empty, svgChildren,
            $"(function () {{ var e = document.elementFromPoint({x}, {y});" +
            " return e ? (e.id || e.tagName) : 'none'; })()");

    /// <summary>
    /// An extent that cannot be represented is not an extent, so the shape has no bounds and
    /// reports the empty rect — which is what SVG 1.1 says a zero extent means and what this
    /// component already answered for a shape it cannot resolve.
    /// </summary>
    [Theory]
    // an exponent that overflows a double, with no symbol in the value at all
    [InlineData("<rect id=\"r\" x=\"10\" y=\"10\" width=\"1e400\" height=\"20\"></rect>")]
    [InlineData("<rect id=\"r\" x=\"10\" y=\"10\" width=\"20\" height=\"1e400\"></rect>")]
    [InlineData("<circle id=\"r\" cx=\"50\" cy=\"50\" r=\"1e400\"></circle>")]
    [InlineData("<ellipse id=\"r\" cx=\"50\" cy=\"50\" rx=\"1e400\" ry=\"10\"></ellipse>")]
    [InlineData("<image id=\"r\" x=\"0\" y=\"0\" width=\"1e400\" height=\"10\"></image>")]
    // the symbolic spellings NumberStyles.Float admits
    [InlineData("<rect id=\"r\" x=\"10\" y=\"10\" width=\"Infinity\" height=\"20\"></rect>")]
    [InlineData("<rect id=\"r\" x=\"10\" y=\"10\" width=\"NaN\" height=\"20\"></rect>")]
    [InlineData("<rect id=\"r\" x=\"10\" y=\"10\" width=\"20\" height=\"NaN\"></rect>")]
    [InlineData("<circle id=\"r\" cx=\"50\" cy=\"50\" r=\"NaN\"></circle>")]
    [InlineData("<ellipse id=\"r\" cx=\"50\" cy=\"50\" rx=\"10\" ry=\"Infinity\"></ellipse>")]
    public void AnExtentThatCannotBeRepresentedIsNotAnExtent(string svgChildren) =>
        Assert.Equal("0,0,0,0", RectOf(svgChildren));

    /// <summary>
    /// A position that cannot be represented is not a position, so the attribute contributes its
    /// SVG default of <c>0</c> and the shape keeps the extent the page did write. Refusing the
    /// whole shape here would throw away geometry that is perfectly readable.
    /// </summary>
    [Theory]
    [InlineData("<rect id=\"r\" x=\"1e400\" y=\"10\" width=\"20\" height=\"20\"></rect>", "0,10,20,20")]
    [InlineData("<rect id=\"r\" x=\"10\" y=\"1e400\" width=\"20\" height=\"20\"></rect>", "10,0,20,20")]
    [InlineData("<rect id=\"r\" x=\"NaN\" y=\"10\" width=\"20\" height=\"20\"></rect>", "0,10,20,20")]
    [InlineData("<rect id=\"r\" x=\"-Infinity\" y=\"10\" width=\"20\" height=\"20\"></rect>", "0,10,20,20")]
    [InlineData("<image id=\"r\" x=\"Infinity\" y=\"0\" width=\"10\" height=\"10\"></image>", "0,0,10,10")]
    // a circle's centre is a position too, and its bounds are centre ± r
    [InlineData("<circle id=\"r\" cx=\"1e400\" cy=\"50\" r=\"10\"></circle>", "-10,40,20,20")]
    [InlineData("<circle id=\"r\" cx=\"50\" cy=\"Infinity\" r=\"10\"></circle>", "40,-10,20,20")]
    // a line's endpoint is a position whose refusal changes the extent, because the extent is the
    // distance between the two
    [InlineData("<line id=\"r\" x1=\"1e400\" y1=\"0\" x2=\"10\" y2=\"10\"></line>", "0,0,10,10")]
    public void AnOriginThatCannotBeRepresentedIsTheSvgDefault(string svgChildren, string expected) =>
        Assert.Equal(expected, RectOf(svgChildren));

    /// <summary>
    /// PRESERVED. The guard refuses what it cannot represent and nothing that merely looks
    /// unusual: an exponent a double can hold is a number, a percentage still resolves against the
    /// viewport, and the two shapes that already refused — a negative extent, by the sign test,
    /// and a <c>points</c> list, by its own finiteness test — still answer the same way.
    /// </summary>
    [Theory]
    [InlineData("<rect id=\"r\" x=\"10\" y=\"10\" width=\"20\" height=\"20\"></rect>", "10,10,20,20")]
    [InlineData("<rect id=\"r\" x=\"1e1\" y=\"1e1\" width=\"2e1\" height=\"2e1\"></rect>", "10,10,20,20")]
    [InlineData("<rect id=\"r\" x=\"10\" y=\"10\" width=\"50%\" height=\"50%\"></rect>", "10,10,100,50")]
    [InlineData("<circle id=\"r\" cx=\"50\" cy=\"50\" r=\"10\"></circle>", "40,40,20,20")]
    [InlineData("<rect id=\"r\" x=\"10\" y=\"10\" width=\"-Infinity\" height=\"20\"></rect>", "0,0,0,0")]
    [InlineData("<polyline id=\"r\" points=\"0,0 1e400,10\"></polyline>", "0,0,0,0")]
    public void ARepresentableGeometryAttributeStillResolves(string svgChildren, string expected) =>
        Assert.Equal(expected, RectOf(svgChildren));

    /// <summary>
    /// The consequence that is not a wrong number. An infinite extent made the shape answer
    /// <c>document.elementFromPoint</c> for every point on its row or column, because the
    /// candidate test is <c>x &lt; rect.Left + rect.Width</c> and that is true of every <c>x</c>
    /// when the width is <c>+∞</c> — an element capturing input over ground it does not cover.
    /// With the extent refused the shape is not rendered at all, so the point falls through to
    /// whatever really is underneath it: the document element far out, and the
    /// <c>&lt;svg&gt;</c> itself over the viewport.
    /// </summary>
    [Theory]
    [InlineData("<rect id=\"r\" x=\"0\" y=\"0\" width=\"1e400\" height=\"10\"></rect>", 600, 5, "HTML")]
    [InlineData("<rect id=\"r\" x=\"0\" y=\"0\" width=\"1e400\" height=\"10\"></rect>", 10, 5, "v")]
    [InlineData("<rect id=\"r\" x=\"0\" y=\"0\" width=\"20\" height=\"1e400\"></rect>", 10, 600, "HTML")]
    [InlineData("<rect id=\"r\" x=\"0\" y=\"0\" width=\"Infinity\" height=\"10\"></rect>", 600, 5, "HTML")]
    public void AnInfiniteExtentDoesNotStealTheHitTest(string svgChildren, int x, int y, string expected) =>
        Assert.Equal(expected, HitAt(svgChildren, x, y));

    /// <summary>
    /// PRESERVED: the same reads against a shape whose extent is representable. The point over the
    /// rect still answers the rect, the point beside it inside the viewport still answers the
    /// <c>&lt;svg&gt;</c>, and the point far out still answers the document element.
    /// </summary>
    [Theory]
    [InlineData(10, 5, "r")]
    [InlineData(50, 5, "v")]
    [InlineData(600, 5, "HTML")]
    public void AFiniteExtentStillTakesTheHitTest(int x, int y, string expected) =>
        Assert.Equal(expected, HitAt("<rect id=\"r\" x=\"0\" y=\"0\" width=\"20\" height=\"10\"></rect>", x, y));

    /// <summary>
    /// <c>x</c>/<c>y</c> on <c>&lt;text&gt;</c>, <c>&lt;tspan&gt;</c> and <c>&lt;textPath&gt;</c>
    /// are read by a second scalar parse, with the same hole. These read through the enclosing
    /// <c>&lt;g&gt;</c>, whose rect is the union of its children's and so is the surface a text
    /// coordinate reaches a page through.
    /// <para>
    /// A coordinate that cannot be represented takes the path one that cannot be parsed already
    /// took: it is not an answer, so the walk carries on to the ancestor that has one, then to the
    /// path start, then to <c>0</c>. Each expectation below is the answer the same fixture gives
    /// with the value spelled <c>garbage</c>, which is what makes "unrepresentable" and
    /// "unparseable" the same thing rather than two things that happen to agree.
    /// </para>
    /// </summary>
    [Theory]
    // no ancestor has one, so the SVG default
    [InlineData("<g id=\"g\"><text x=\"1e400\" y=\"50\">hello</text></g>", "0,34,48,16")]
    [InlineData("<g id=\"g\"><text x=\"Infinity\" y=\"50\">hello</text></g>", "0,34,48,16")]
    [InlineData("<g id=\"g\"><text x=\"NaN\" y=\"50\">hello</text></g>", "0,34,48,16")]
    [InlineData("<g id=\"g\"><text x=\"10\" y=\"1e400\">hello</text></g>", "10,-16,48,16")]
    // a tspan's refused x falls through to the one its <text> ancestor does have
    [InlineData("<g id=\"g\"><text x=\"10\" y=\"50\"><tspan x=\"1e400\">hi</tspan></text></g>", "10,34,19.2,16")]
    [InlineData("<g id=\"g\"><text><tspan x=\"1e400\" y=\"50\">hi</tspan></text></g>", "0,34,19.2,16")]
    public void ATextCoordinateThatCannotBeRepresentedIsNotACoordinate(string svgChildren, string expected) =>
        Assert.Equal(expected, RectOf(svgChildren, "g"));

    /// <summary>
    /// PRESERVED, and the reference the cases above are measured against: the same fixtures with
    /// the coordinate spelled <c>garbage</c>, which the parse has always rejected. A refused
    /// coordinate has to answer exactly this.
    /// </summary>
    [Theory]
    [InlineData("<g id=\"g\"><text x=\"garbage\" y=\"50\">hello</text></g>", "0,34,48,16")]
    [InlineData("<g id=\"g\"><text x=\"10\" y=\"garbage\">hello</text></g>", "10,-16,48,16")]
    [InlineData("<g id=\"g\"><text x=\"10\" y=\"50\"><tspan x=\"garbage\">hi</tspan></text></g>", "10,34,19.2,16")]
    [InlineData("<g id=\"g\"><text x=\"10\" y=\"50\">hello</text></g>", "10,34,48,16")]
    [InlineData("<g id=\"g\"><text x=\"10\" y=\"50\"><tspan>hi</tspan></text></g>", "10,34,19.2,16")]
    public void AnUnreadableTextCoordinateAnswersTheSameWay(string svgChildren, string expected) =>
        Assert.Equal(expected, RectOf(svgChildren, "g"));

    /// <summary>The <c>&lt;path&gt;</c> a <c>&lt;textPath&gt;</c> below is laid along.</summary>
    private const string Path = "<defs><path id=\"p\" d=\"M 5 5 L 50 50\"></path></defs>";

    /// <summary>
    /// A <c>&lt;textPath&gt;</c>'s own refused <c>x</c> falls through to the path's start point,
    /// which is where a <c>&lt;textPath&gt;</c> begins when it carries no <c>x</c> at all — again
    /// the answer the unparseable spelling already gave.
    /// </summary>
    [Theory]
    [InlineData("1e400")]
    [InlineData("Infinity")]
    [InlineData("NaN")]
    [InlineData("garbage")]
    public void ARefusedTextPathCoordinateFallsBackToThePathStart(string x) =>
        Assert.Equal("5,-11,19.2,16", RectOf(
            $"{Path}<g id=\"g\"><text><textPath href=\"#p\" x=\"{x}\">hi</textPath></text></g>", "g"));

    /// <summary>
    /// The same rule one level further in: the path's own <c>moveto</c> is read with the same
    /// scalar parse. Its regex admits neither <c>Infinity</c> nor an exponent, so the only way to
    /// overflow it is a digit string longer than a double can hold — 401 of them here, an
    /// ordinary-looking coordinate with no symbol and no exponent anywhere in it. A start point
    /// that cannot be represented is not a start point, so the <c>&lt;textPath&gt;</c> begins
    /// where one with no resolvable path begins.
    /// </summary>
    [Fact]
    public void APathStartThatCannotBeRepresentedIsNotAPathStart() =>
        Assert.Equal("0,-16,19.2,16", RectOf(
            $"<defs><path id=\"p\" d=\"M {new string('1', 401)} 5\"></path></defs>" +
            "<g id=\"g\"><text><textPath href=\"#p\">hi</textPath></text></g>", "g"));

    /// <summary>
    /// PRESERVED: the same fixture with a <c>moveto</c> a double can hold, and with a path that
    /// cannot be resolved at all. The refusal above has to land on the second of these.
    /// </summary>
    [Theory]
    [InlineData("<defs><path id=\"p\" d=\"M 5 5 L 50 50\"></path></defs>", "5,-11,19.2,16")]
    [InlineData("<defs><path id=\"p\" d=\"garbage\"></path></defs>", "0,-16,19.2,16")]
    public void AReadablePathStartStillPlacesTheText(string defs, string expected) =>
        Assert.Equal(expected, RectOf(
            $"{defs}<g id=\"g\"><text><textPath href=\"#p\">hi</textPath></text></g>", "g"));
}
